using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Aero.App.Security;

internal enum BrokerMessageType : ushort
{
    ClientHello = 1,
    ServerHello = 2,
    CredentialRequest = 3,
    CredentialResponse = 4,
    NotFound = 5,
    Failure = 6,
    RevealCompleted = 7
}

internal sealed class BrokerFrame : IDisposable
{
    private byte[]? _payload;

    public BrokerFrame(
        BrokerMessageType type,
        ulong sequence,
        Guid requestId,
        byte[] payload)
    {
        Type = type;
        Sequence = sequence;
        RequestId = requestId;
        _payload = payload;
    }

    public BrokerMessageType Type { get; }

    public ulong Sequence { get; }

    public Guid RequestId { get; }

    public ReadOnlySpan<byte> Payload => _payload
        ?? throw new ObjectDisposedException(nameof(BrokerFrame));

    public void Dispose()
    {
        byte[]? payload = Interlocked.Exchange(ref _payload, null);
        if (payload is not null)
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }
}

internal static class BrokerProtocol
{
    public const ushort Version = 3;
    public const int AuthenticationKeyLength = 32;
    public const int ChallengeLength = 32;
    public const int DocumentIdentityLength = 16;
    public const int MaximumOriginLength = 2048;
    public const int MaximumUsernameLength = 16 * 1024;
    public const int MaximumPasswordLength = 32 * 1024;
    public const int MaximumPayloadLength = 64 * 1024;
    public const int BootstrapHeaderLength = 110;
    public const int MaximumBootstrapLength = 8 * 1024;

    private const int HeaderWithoutMacLength = 80;
    private const int MacLength = 32;
    private const int HeaderLength = HeaderWithoutMacLength + MacLength;
    private static readonly byte[] FrameMagic = "AEROBRK1"u8.ToArray();
    private static readonly byte[] BootstrapMagic = "AEROBST1"u8.ToArray();
    private static readonly TimeSpan MaximumFrameAge = TimeSpan.FromMinutes(2);

    public static byte[] CreateBootstrap(
        int clientProcessId,
        int clientSessionId,
        DateTimeOffset expiresUtc,
        Guid clientInstanceId,
        ReadOnlySpan<byte> authenticationKey,
        string pipeName,
        string securityDirectory)
    {
        if (authenticationKey.Length != AuthenticationKeyLength)
        {
            throw new ArgumentException("Invalid broker authentication key length.", nameof(authenticationKey));
        }

        byte[]? pipeNameBytes = null;
        byte[]? pipeHash = null;
        byte[]? securityDirectoryBytes = null;
        try
        {
            pipeNameBytes = Encoding.UTF8.GetBytes(pipeName);
            pipeHash = SHA256.HashData(pipeNameBytes);
            securityDirectoryBytes = Encoding.UTF8.GetBytes(Path.GetFullPath(securityDirectory));
            if (securityDirectoryBytes.Length == 0 ||
                securityDirectoryBytes.Length > MaximumBootstrapLength - BootstrapHeaderLength)
            {
                throw new ArgumentException("Invalid Broker security directory.", nameof(securityDirectory));
            }

            byte[] bootstrap = new byte[checked(BootstrapHeaderLength + securityDirectoryBytes.Length)];
            BootstrapMagic.CopyTo(bootstrap, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(bootstrap.AsSpan(8, 2), Version);
            BinaryPrimitives.WriteInt32LittleEndian(bootstrap.AsSpan(10, 4), clientProcessId);
            BinaryPrimitives.WriteInt32LittleEndian(bootstrap.AsSpan(14, 4), clientSessionId);
            BinaryPrimitives.WriteInt64LittleEndian(bootstrap.AsSpan(18, 8), expiresUtc.UtcDateTime.Ticks);
            clientInstanceId.TryWriteBytes(bootstrap.AsSpan(26, 16));
            authenticationKey.CopyTo(bootstrap.AsSpan(42, AuthenticationKeyLength));
            pipeHash.CopyTo(bootstrap, 74);
            BinaryPrimitives.WriteInt32LittleEndian(
                bootstrap.AsSpan(106, 4),
                securityDirectoryBytes.Length);
            securityDirectoryBytes.CopyTo(bootstrap, BootstrapHeaderLength);
            return bootstrap;
        }
        finally
        {
            Zero(pipeNameBytes);
            Zero(pipeHash);
            Zero(securityDirectoryBytes);
        }
    }

    public static async Task<byte[]> ReadBootstrapAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[BootstrapHeaderLength];
        byte[]? result = null;
        try
        {
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            if (!header.AsSpan(0, 8).SequenceEqual(BootstrapMagic) ||
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2)) != Version)
            {
                throw new InvalidDataException("Invalid Aero Security Broker bootstrap header.");
            }

            int pathLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(106, 4));
            int totalLength = checked(BootstrapHeaderLength + pathLength);
            if (pathLength <= 0 || totalLength > MaximumBootstrapLength)
            {
                throw new InvalidDataException("Aero Security Broker bootstrap length is invalid.");
            }

            result = new byte[totalLength];
            header.CopyTo(result, 0);
            await stream.ReadExactlyAsync(
                result.AsMemory(BootstrapHeaderLength, pathLength),
                cancellationToken).ConfigureAwait(false);
            byte[] completed = result;
            result = null;
            return completed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(header);
            Zero(result);
        }
    }

    public static void ParseBootstrap(
        ReadOnlySpan<byte> bootstrap,
        string expectedPipeName,
        out int clientProcessId,
        out int clientSessionId,
        out DateTimeOffset expiresUtc,
        out Guid clientInstanceId,
        out byte[] authenticationKey,
        out string securityDirectory)
    {
        clientProcessId = 0;
        clientSessionId = 0;
        expiresUtc = default;
        clientInstanceId = Guid.Empty;
        authenticationKey = Array.Empty<byte>();
        securityDirectory = string.Empty;

        byte[]? parsedAuthenticationKey = null;
        byte[]? pipeNameBytes = null;
        byte[]? pipeHash = null;
        try
        {
            if (bootstrap.Length < BootstrapHeaderLength ||
                bootstrap.Length > MaximumBootstrapLength ||
                !bootstrap[..8].SequenceEqual(BootstrapMagic) ||
                BinaryPrimitives.ReadUInt16LittleEndian(bootstrap.Slice(8, 2)) != Version)
            {
                throw new InvalidDataException("Invalid Aero Security Broker bootstrap.");
            }

            clientProcessId = BinaryPrimitives.ReadInt32LittleEndian(bootstrap.Slice(10, 4));
            clientSessionId = BinaryPrimitives.ReadInt32LittleEndian(bootstrap.Slice(14, 4));
            expiresUtc = new DateTimeOffset(
                BinaryPrimitives.ReadInt64LittleEndian(bootstrap.Slice(18, 8)),
                TimeSpan.Zero);
            clientInstanceId = new Guid(bootstrap.Slice(26, 16));
            parsedAuthenticationKey = bootstrap.Slice(42, AuthenticationKeyLength).ToArray();

            int securityDirectoryLength = BinaryPrimitives.ReadInt32LittleEndian(
                bootstrap.Slice(106, 4));
            if (securityDirectoryLength <= 0 ||
                securityDirectoryLength != bootstrap.Length - BootstrapHeaderLength)
            {
                throw new InvalidDataException("Broker security directory length is invalid.");
            }

            securityDirectory = Path.GetFullPath(Encoding.UTF8.GetString(
                bootstrap.Slice(BootstrapHeaderLength, securityDirectoryLength)));

            pipeNameBytes = Encoding.UTF8.GetBytes(expectedPipeName);
            pipeHash = SHA256.HashData(pipeNameBytes);
            if (!CryptographicOperations.FixedTimeEquals(pipeHash, bootstrap.Slice(74, 32)))
            {
                throw new InvalidDataException("Broker pipe binding validation failed.");
            }

            authenticationKey = parsedAuthenticationKey;
            parsedAuthenticationKey = null;
        }
        finally
        {
            Zero(parsedAuthenticationKey);
            Zero(pipeNameBytes);
            Zero(pipeHash);
        }
    }

    public static async Task WriteFrameAsync(
        Stream stream,
        BrokerMessageType type,
        ulong sequence,
        Guid requestId,
        ReadOnlyMemory<byte> payload,
        ReadOnlyMemory<byte> authenticationKey,
        CancellationToken cancellationToken)
    {
        if (payload.Length > MaximumPayloadLength)
        {
            throw new InvalidDataException("Broker message exceeds the allowed size.");
        }

        if (authenticationKey.Length != AuthenticationKeyLength)
        {
            throw new InvalidDataException("Broker authentication key is invalid.");
        }

        byte[] frame = new byte[checked(HeaderLength + payload.Length)];
        byte[]? macInput = null;
        byte[]? mac = null;
        try
        {
            FrameMagic.CopyTo(frame, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8, 2), Version);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(10, 2), (ushort)type);
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(12, 8), sequence);
            BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(20, 8), DateTimeOffset.UtcNow.UtcDateTime.Ticks);
            requestId.TryWriteBytes(frame.AsSpan(28, 16));
            RandomNumberGenerator.Fill(frame.AsSpan(44, ChallengeLength));
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(76, 4), payload.Length);
            payload.Span.CopyTo(frame.AsSpan(HeaderLength));

            macInput = new byte[checked(HeaderWithoutMacLength + payload.Length)];
            frame.AsSpan(0, HeaderWithoutMacLength).CopyTo(macInput);
            payload.Span.CopyTo(macInput.AsSpan(HeaderWithoutMacLength));
            mac = HMACSHA256.HashData(authenticationKey.Span, macInput);
            mac.CopyTo(frame, HeaderWithoutMacLength);

            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(frame);
            Zero(macInput);
            Zero(mac);
        }
    }

    public static async Task<BrokerFrame> ReadFrameAsync(
        Stream stream,
        ulong expectedSequence,
        ReadOnlyMemory<byte> authenticationKey,
        CancellationToken cancellationToken)
    {
        if (authenticationKey.Length != AuthenticationKeyLength)
        {
            throw new InvalidDataException("Broker authentication key is invalid.");
        }

        byte[] header = new byte[HeaderLength];
        byte[]? payload = null;
        byte[]? macInput = null;
        byte[]? computedMac = null;
        try
        {
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            if (!header.AsSpan(0, 8).SequenceEqual(FrameMagic) ||
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2)) != Version)
            {
                throw new InvalidDataException("Invalid Broker message header.");
            }

            BrokerMessageType type = (BrokerMessageType)BinaryPrimitives.ReadUInt16LittleEndian(
                header.AsSpan(10, 2));
            ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(12, 8));
            if (sequence != expectedSequence)
            {
                throw new InvalidDataException("Broker message sequence is invalid.");
            }

            long timestampTicks = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(20, 8));
            DateTimeOffset timestamp = new(timestampTicks, TimeSpan.Zero);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (timestamp > now.AddSeconds(30) || now - timestamp > MaximumFrameAge)
            {
                throw new InvalidDataException("Broker message has expired.");
            }

            Guid requestId = new(header.AsSpan(28, 16));
            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(76, 4));
            if (payloadLength < 0 || payloadLength > MaximumPayloadLength)
            {
                throw new InvalidDataException("Broker payload length is invalid.");
            }

            payload = new byte[payloadLength];
            if (payloadLength > 0)
            {
                await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            macInput = new byte[checked(HeaderWithoutMacLength + payloadLength)];
            header.AsSpan(0, HeaderWithoutMacLength).CopyTo(macInput);
            payload.CopyTo(macInput, HeaderWithoutMacLength);
            computedMac = HMACSHA256.HashData(authenticationKey.Span, macInput);
            if (!CryptographicOperations.FixedTimeEquals(
                    computedMac,
                    header.AsSpan(HeaderWithoutMacLength, MacLength)))
            {
                throw new InvalidDataException("Broker message authentication failed.");
            }

            BrokerFrame result = new(type, sequence, requestId, payload);
            payload = null;
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(header);
            Zero(payload);
            Zero(macInput);
            Zero(computedMac);
        }
    }

    public static byte[] BuildClientHelloPayload(
        int clientProcessId,
        int clientSessionId,
        Guid clientInstanceId,
        ReadOnlySpan<byte> clientChallenge,
        DateTimeOffset expiresUtc)
    {
        if (clientChallenge.Length != ChallengeLength)
        {
            throw new ArgumentException("Invalid client challenge.", nameof(clientChallenge));
        }

        byte[] payload = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), clientProcessId);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), clientSessionId);
        clientInstanceId.TryWriteBytes(payload.AsSpan(8, 16));
        clientChallenge.CopyTo(payload.AsSpan(24, ChallengeLength));
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(56, 8), expiresUtc.UtcDateTime.Ticks);
        return payload;
    }

    public static byte[] BuildServerHelloPayload(
        int brokerProcessId,
        int brokerSessionId,
        Guid clientInstanceId,
        ReadOnlySpan<byte> clientChallenge,
        ReadOnlySpan<byte> serverChallenge)
    {
        if (clientChallenge.Length != ChallengeLength || serverChallenge.Length != ChallengeLength)
        {
            throw new ArgumentException("Invalid Broker challenge length.");
        }

        byte[] payload = new byte[88];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), brokerProcessId);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), brokerSessionId);
        clientInstanceId.TryWriteBytes(payload.AsSpan(8, 16));
        clientChallenge.CopyTo(payload.AsSpan(24, ChallengeLength));
        serverChallenge.CopyTo(payload.AsSpan(56, ChallengeLength));
        return payload;
    }

    public static byte[] BuildCredentialRequestPayload(
        PasswordBrokerOperation operation,
        Guid clientInstanceId,
        ReadOnlySpan<byte> clientChallenge,
        ReadOnlySpan<byte> serverChallenge,
        ulong navigationId,
        Guid documentIdentity,
        IntPtr ownerWindow,
        ReadOnlySpan<byte> originUtf8,
        ReadOnlySpan<byte> usernameUtf8)
    {
        if (!Enum.IsDefined(operation) ||
            clientChallenge.Length != ChallengeLength ||
            serverChallenge.Length != ChallengeLength ||
            originUtf8.IsEmpty ||
            originUtf8.Length > MaximumOriginLength ||
            usernameUtf8.Length > MaximumUsernameLength ||
            (operation != PasswordBrokerOperation.GenerateAndSave && !usernameUtf8.IsEmpty))
        {
            throw new ArgumentException("Invalid Broker credential request.");
        }

        byte[] payload = new byte[checked(121 + originUtf8.Length + usernameUtf8.Length)];
        payload[0] = (byte)operation;
        clientInstanceId.TryWriteBytes(payload.AsSpan(1, 16));
        clientChallenge.CopyTo(payload.AsSpan(17, ChallengeLength));
        serverChallenge.CopyTo(payload.AsSpan(49, ChallengeLength));
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(81, 8), navigationId);
        documentIdentity.TryWriteBytes(payload.AsSpan(89, DocumentIdentityLength));
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(105, 8), ownerWindow.ToInt64());
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(113, 4), originUtf8.Length);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(117, 4), usernameUtf8.Length);
        originUtf8.CopyTo(payload.AsSpan(121, originUtf8.Length));
        usernameUtf8.CopyTo(payload.AsSpan(121 + originUtf8.Length));
        return payload;
    }

    public static byte[] BuildCredentialResponsePayload(
        ulong navigationId,
        Guid documentIdentity,
        ReadOnlySpan<byte> normalizedOriginUtf8,
        ReadOnlySpan<byte> usernameUtf8,
        ReadOnlySpan<byte> passwordUtf8)
    {
        if (normalizedOriginUtf8.IsEmpty ||
            normalizedOriginUtf8.Length > MaximumOriginLength ||
            usernameUtf8.Length > MaximumUsernameLength ||
            passwordUtf8.IsEmpty ||
            passwordUtf8.Length > MaximumPasswordLength)
        {
            throw new InvalidDataException("Broker credential response exceeds allowed limits.");
        }

        byte[]? originHash = null;
        try
        {
            originHash = SHA256.HashData(normalizedOriginUtf8);
            byte[] payload = new byte[checked(64 + usernameUtf8.Length + passwordUtf8.Length)];
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(0, 8), navigationId);
            documentIdentity.TryWriteBytes(payload.AsSpan(8, DocumentIdentityLength));
            originHash.CopyTo(payload, 24);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(56, 4), usernameUtf8.Length);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(60, 4), passwordUtf8.Length);
            usernameUtf8.CopyTo(payload.AsSpan(64, usernameUtf8.Length));
            passwordUtf8.CopyTo(payload.AsSpan(64 + usernameUtf8.Length, passwordUtf8.Length));
            return payload;
        }
        finally
        {
            Zero(originHash);
        }
    }


    public static byte[] BuildRevealCompletedPayload(
        ulong navigationId,
        Guid documentIdentity,
        ReadOnlySpan<byte> normalizedOriginUtf8)
    {
        if (normalizedOriginUtf8.IsEmpty ||
            normalizedOriginUtf8.Length > MaximumOriginLength)
        {
            throw new InvalidDataException("Broker reveal completion exceeds allowed limits.");
        }

        byte[]? originHash = null;
        try
        {
            originHash = SHA256.HashData(normalizedOriginUtf8);
            byte[] payload = new byte[56];
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(0, 8), navigationId);
            documentIdentity.TryWriteBytes(payload.AsSpan(8, DocumentIdentityLength));
            originHash.CopyTo(payload, 24);
            return payload;
        }
        finally
        {
            Zero(originHash);
        }
    }

    public static byte[] BuildFailurePayload(PasswordBrokerFailure failure)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, (int)failure);
        return payload;
    }

    public static PasswordBrokerFailure ParseFailure(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 4)
        {
            return PasswordBrokerFailure.ProtocolError;
        }

        int value = BinaryPrimitives.ReadInt32LittleEndian(payload);
        return Enum.IsDefined(typeof(PasswordBrokerFailure), value)
            ? (PasswordBrokerFailure)value
            : PasswordBrokerFailure.ProtocolError;
    }

    public static byte[] HashOrigin(ReadOnlySpan<byte> originUtf8) =>
        SHA256.HashData(originUtf8);

    public static bool IsAllZero(ReadOnlySpan<byte> value)
    {
        byte aggregate = 0;
        foreach (byte item in value)
        {
            aggregate |= item;
        }

        return aggregate == 0;
    }

    public static void Zero(byte[]? buffer)
    {
        if (buffer is not null)
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
