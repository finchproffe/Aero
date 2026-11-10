// Aero Vault. Vault is Powered by Aero.
// Copyright (c) 2026 finchproffe. Licensed under the Mozilla Public License 2.0.
// Under MPL-2.0 section 3.4 this notice must stay in every copy and fork of this file.
using Aero.App.Security;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Aero.Security.BrokerHost;

internal sealed class BrokerServer : IDisposable
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(80);
    private static readonly TimeSpan RevealLifetime = TimeSpan.FromSeconds(10);

    private readonly string _pipeName;
    private readonly int _clientProcessId;
    private readonly int _clientSessionId;
    private readonly Guid _clientInstanceId;
    private readonly DateTimeOffset _expiresUtc;
    private readonly byte[] _authenticationKey;
    private readonly PasswordVaultService _vault;
    private readonly BrokerAudit _audit;
    private readonly string _expectedClientPath;
    private BrokerProcessHardening? _hardening;
    private readonly object _secretProtectionGate = new();
    private AeroSecurityKernelProtection? _kernelProtection;
    private bool _secretProtectionActive;
    private int _disposed;

    public BrokerServer(
        string pipeName,
        int clientProcessId,
        int clientSessionId,
        Guid clientInstanceId,
        DateTimeOffset expiresUtc,
        byte[] authenticationKey,
        PasswordVaultService vault,
        string securityDirectory,
        string expectedClientPath)
    {
        _pipeName = pipeName;
        _clientProcessId = clientProcessId;
        _clientSessionId = clientSessionId;
        _clientInstanceId = clientInstanceId;
        _expiresUtc = expiresUtc;
        _authenticationKey = authenticationKey;
        _vault = vault;
        _audit = new BrokerAudit(securityDirectory);
        _expectedClientPath = Path.GetFullPath(expectedClientPath);
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using NamedPipeServerStream pipe = new(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 4096,
            outBufferSize: 4096);

        using CancellationTokenSource connectTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(ConnectionTimeout);
        await pipe.WaitForConnectionAsync(connectTimeout.Token);

        if (BrokerNativeMethods.GetPipeClientProcessId(pipe.SafePipeHandle) != _clientProcessId ||
            !TryValidateClientProcess())
        {
            _audit.Write("connect", "client-rejected", _clientProcessId, Guid.Empty);
            return 10;
        }

        using CancellationTokenSource requestTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(RequestTimeout);
        CancellationToken operationToken = requestTimeout.Token;

        byte[] clientChallenge = new byte[BrokerProtocol.ChallengeLength];
        byte[] serverChallenge = RandomNumberGenerator.GetBytes(BrokerProtocol.ChallengeLength);
        byte[]? serverHelloPayload = null;
        try
        {
            using BrokerFrame clientHello = await BrokerProtocol.ReadFrameAsync(
                pipe,
                expectedSequence: 1,
                _authenticationKey,
                operationToken);
            if (!ValidateClientHello(clientHello, clientChallenge))
            {
                _audit.Write("handshake", "client-rejected", _clientProcessId, clientHello.RequestId);
                return 11;
            }

            serverHelloPayload = BrokerProtocol.BuildServerHelloPayload(
                Environment.ProcessId,
                BrokerNativeMethods.GetCurrentSessionId(),
                _clientInstanceId,
                clientChallenge,
                serverChallenge);
            await BrokerProtocol.WriteFrameAsync(
                pipe,
                BrokerMessageType.ServerHello,
                sequence: 1,
                clientHello.RequestId,
                serverHelloPayload,
                _authenticationKey,
                operationToken);

            using BrokerFrame request = await BrokerProtocol.ReadFrameAsync(
                pipe,
                expectedSequence: 2,
                _authenticationKey,
                operationToken);

            // This method is the sole reader for the duplex pipe. A previous
            // disconnect monitor issued a second ReadAsync after the request and
            // raced the response lifecycle: it consumed EOF/extra data while the
            // authenticated response was being written and cancelled the reveal.
            // Client death is already bounded by the parent-process watcher and
            // request timeout; an early pipe close is detected by the response write.
            return await HandleCredentialRequestAsync(
                pipe,
                request,
                clientChallenge,
                serverChallenge,
                operationToken);
        }
        catch (OperationCanceledException)
        {
            _audit.Write("operation", "timeout", _clientProcessId, Guid.Empty);
            return 12;
        }
        catch (InvalidDataException)
        {
            _audit.Write("protocol", "invalid", _clientProcessId, Guid.Empty);
            return 13;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clientChallenge);
            CryptographicOperations.ZeroMemory(serverChallenge);
            BrokerProtocol.Zero(serverHelloPayload);
            try
            {
                pipe.Disconnect();
            }
            catch
            {
            }
        }
    }

    private bool ValidateClientHello(BrokerFrame frame, Span<byte> clientChallenge)
    {
        ReadOnlySpan<byte> payload = frame.Payload;
        if (frame.Type != BrokerMessageType.ClientHello ||
            payload.Length != 64 ||
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(0, 4)) != _clientProcessId ||
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4)) != _clientSessionId ||
            new Guid(payload.Slice(8, 16)) != _clientInstanceId)
        {
            return false;
        }

        DateTimeOffset expiresUtc = new(
            BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(56, 8)),
            TimeSpan.Zero);
        if (expiresUtc != _expiresUtc || DateTimeOffset.UtcNow >= expiresUtc)
        {
            return false;
        }

        payload.Slice(24, BrokerProtocol.ChallengeLength).CopyTo(clientChallenge);
        return !BrokerProtocol.IsAllZero(clientChallenge);
    }

    private async Task<int> HandleCredentialRequestAsync(
        NamedPipeServerStream pipe,
        BrokerFrame request,
        byte[] clientChallenge,
        byte[] serverChallenge,
        CancellationToken cancellationToken)
    {
        byte[]? failurePayload = null;
        byte[]? responsePayload = null;
        byte[]? originUtf8 = null;
        byte[]? requestedUsernameUtf8 = null;
        try
        {
            if (request.Type != BrokerMessageType.CredentialRequest ||
                !TryParseCredentialRequest(
                    request.Payload,
                    clientChallenge,
                    serverChallenge,
                    out PasswordBrokerOperation operation,
                    out ulong navigationId,
                    out Guid documentIdentity,
                    out IntPtr ownerWindow,
                    out originUtf8,
                    out requestedUsernameUtf8))
            {
                failurePayload = BrokerProtocol.BuildFailurePayload(
                    PasswordBrokerFailure.ProtocolError);
                await WritePipeFrameAsync(
                    pipe,
                    BrokerMessageType.Failure,
                    sequence: 2,
                    request.RequestId,
                    failurePayload,
                    cancellationToken);
                _audit.Write("credential", "protocol-rejected", _clientProcessId, request.RequestId);
                return 20;
            }

            if (!BrokerNativeMethods.WindowBelongsToProcess(ownerWindow, _clientProcessId))
            {
                failurePayload = BrokerProtocol.BuildFailurePayload(
                    PasswordBrokerFailure.WindowRejected);
                await WritePipeFrameAsync(
                    pipe,
                    BrokerMessageType.Failure,
                    sequence: 2,
                    request.RequestId,
                    failurePayload,
                    cancellationToken);
                _audit.Write("credential", "window-rejected", _clientProcessId, request.RequestId, originUtf8);
                return 21;
            }

            string origin;
            try
            {
                origin = PasswordVaultService.NormalizeHttpsOrigin(
                    Encoding.UTF8.GetString(originUtf8));
            }
            catch
            {
                failurePayload = BrokerProtocol.BuildFailurePayload(
                    PasswordBrokerFailure.OriginRejected);
                await WritePipeFrameAsync(
                    pipe,
                    BrokerMessageType.Failure,
                    sequence: 2,
                    request.RequestId,
                    failurePayload,
                    cancellationToken);
                _audit.Write("credential", "origin-rejected", _clientProcessId, request.RequestId, originUtf8);
                return 22;
            }

            BrokerNativeMethods.TryActivateOwnerWindow(ownerWindow);

            try
            {
                if (operation == PasswordBrokerOperation.Reveal)
                {
                    using SecureRevealWindow revealWindow =
                        SecureRevealWindow.Create(ownerWindow, origin);

                    PasswordLease? revealLease =
                        await _vault.GetPasswordForRevealJitAsync(
                            origin,
                            revealWindow.WindowHandle,
                            EnsureSecretProtectionActive,
                            cancellationToken);
                    if (revealLease is null)
                    {
                        await WritePipeFrameAsync(
                            pipe,
                            BrokerMessageType.NotFound,
                            sequence: 2,
                            request.RequestId,
                            ReadOnlyMemory<byte>.Empty,
                            cancellationToken);
                        _audit.Write("credential", "not-found", _clientProcessId, request.RequestId, originUtf8);
                        return 0;
                    }

                    using (revealLease)
                    {
                        revealWindow.SetPassword(revealLease.PasswordUtf8);
                    }

                    await revealWindow.ShowForAsync(
                        RevealLifetime,
                        cancellationToken);

                    responsePayload = BrokerProtocol.BuildRevealCompletedPayload(
                        navigationId,
                        documentIdentity,
                        originUtf8);
                    await WritePipeFrameAsync(
                        pipe,
                        BrokerMessageType.RevealCompleted,
                        sequence: 2,
                        request.RequestId,
                        responsePayload,
                        cancellationToken);
                    _audit.Write("credential", "revealed-in-broker", _clientProcessId, request.RequestId, originUtf8);
                    return 0;
                }

                EnsureSecretProtectionActive();

                using PasswordLease? lease = operation == PasswordBrokerOperation.GenerateAndSave
                    ? await GenerateAndSaveCredentialAsync(
                        origin,
                        requestedUsernameUtf8,
                        ownerWindow,
                        cancellationToken)
                    : await _vault.GetPasswordJitAsync(
                        origin,
                        ownerWindow,
                        cancellationToken);

                if (lease is null)
                {
                    await WritePipeFrameAsync(
                        pipe,
                        BrokerMessageType.NotFound,
                        sequence: 2,
                        request.RequestId,
                        ReadOnlyMemory<byte>.Empty,
                        cancellationToken);
                    _audit.Write("credential", "not-found", _clientProcessId, request.RequestId, originUtf8);
                    return 0;
                }

                responsePayload = BrokerProtocol.BuildCredentialResponsePayload(
                    navigationId,
                    documentIdentity,
                    originUtf8,
                    lease.UsernameUtf8,
                    lease.PasswordUtf8);
                await WritePipeFrameAsync(
                    pipe,
                    BrokerMessageType.CredentialResponse,
                    sequence: 2,
                    request.RequestId,
                    responsePayload,
                    cancellationToken);
                _audit.Write(
                    "credential",
                    operation == PasswordBrokerOperation.GenerateAndSave ? "generated" : "filled",
                    _clientProcessId,
                    request.RequestId,
                    originUtf8);
                return 0;
            }
            catch (BrokerProtectionException exception)
            {
                BrokerProtocol.Zero(failurePayload);
                failurePayload = BrokerProtocol.BuildFailurePayload(
                    PasswordBrokerFailure.UserModeProtectionUnavailable);
                await WritePipeFrameAsync(
                    pipe,
                    BrokerMessageType.Failure,
                    sequence: 2,
                    request.RequestId,
                    failurePayload,
                    cancellationToken);
                _audit.Write(
                    "credential",
                    $"user-mode-hardening-failed-{exception.Stage}-{exception.ErrorCode:X8}",
                    _clientProcessId,
                    request.RequestId,
                    originUtf8 ?? Array.Empty<byte>());
                return 25;
            }
            catch (PasswordVaultVerificationException)
            {
                BrokerProtocol.Zero(failurePayload);
                failurePayload = BrokerProtocol.BuildFailurePayload(
                    PasswordBrokerFailure.VerificationCancelled);
                await WritePipeFrameAsync(
                    pipe,
                    BrokerMessageType.Failure,
                    sequence: 2,
                    request.RequestId,
                    failurePayload,
                    cancellationToken);
                _audit.Write("credential", "hello-not-completed", _clientProcessId, request.RequestId, originUtf8 ?? Array.Empty<byte>());
                return 23;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                BrokerProtocol.Zero(failurePayload);
                failurePayload = BrokerProtocol.BuildFailurePayload(
                    PasswordBrokerFailure.VerificationCancelled);
                await WritePipeFrameAsync(
                    pipe,
                    BrokerMessageType.Failure,
                    sequence: 2,
                    request.RequestId,
                    failurePayload,
                    cancellationToken);
                _audit.Write(
                    "credential",
                    "reveal-window-closed",
                    _clientProcessId,
                    request.RequestId,
                    originUtf8 ?? Array.Empty<byte>());
                return 23;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                BrokerProtocol.Zero(failurePayload);
                failurePayload = BrokerProtocol.BuildFailurePayload(
                    PasswordBrokerFailure.VaultError);
                await WritePipeFrameAsync(
                    pipe,
                    BrokerMessageType.Failure,
                    sequence: 2,
                    request.RequestId,
                    failurePayload,
                    cancellationToken);
                _audit.Write("credential", "vault-failed", _clientProcessId, request.RequestId, originUtf8 ?? Array.Empty<byte>());
                return 24;
            }
        }
        finally
        {
            BrokerProtocol.Zero(failurePayload);
            BrokerProtocol.Zero(responsePayload);
            BrokerProtocol.Zero(originUtf8);
            BrokerProtocol.Zero(requestedUsernameUtf8);
        }
    }

    private void EnsureSecretProtectionActive()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            nameof(BrokerServer));

        lock (_secretProtectionGate)
        {
            if (_secretProtectionActive)
            {
                return;
            }

            string protectionStage = "process-hardening-create";
            try
            {
                // Create process hardening only after Windows Hello has completed.
                // Applying loader/mitigation policy at Broker startup can break WinRT
                // authentication initialization and surface as an OS "Unknown Hard Error".
                // The callback reaches this point before the protected vault payload is
                // decrypted, so the secret boundary remains fail-closed.
                BrokerProcessHardening activeHardening;
                if (_hardening is null)
                {
                    if (!BrokerProcessHardening.TryCreate(
                            out BrokerProcessHardening? createdHardening,
                            out string hardeningFailure) ||
                        createdHardening is null)
                    {
                        _audit.Write("hardening", hardeningFailure, _clientProcessId, Guid.Empty);
                        throw new InvalidOperationException(
                            "Aero Security Broker could not activate process hardening.");
                    }

                    _hardening = createdHardening;
                }

                activeHardening = _hardening;
                protectionStage = "process-integrity-audit";
                activeHardening.EnsureTrustedForSecretOperation();

                protectionStage = "process-dacl";
                if (!BrokerNativeMethods.TryRestrictCurrentProcessDacl())
                {
                    throw new InvalidOperationException(
                        "Aero Security Broker could not restrict its process DACL.");
                }

                protectionStage = "kernel-protection";
                if (AeroSecurityKernelProtection.TryActivate(
                        out AeroSecurityKernelProtection? protection,
                        out KernelProtectionStatus kernelStatus))
                {
                    _kernelProtection = protection;
                }
                else
                {
                    protection?.Dispose();
                    _kernelProtection?.Dispose();
                    _kernelProtection = null;
                }

                _audit.Write(
                    "kernel",
                    kernelStatus.IsActive
                        ? "enhanced-protection-active"
                        : $"standard-protection-{kernelStatus.State}-{kernelStatus.ErrorCode:X8}",
                    _clientProcessId,
                    Guid.Empty);

                _secretProtectionActive = true;
            }
            catch (Exception exception)
            {
                throw new BrokerProtectionException(protectionStage, exception);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_secretProtectionGate)
        {
            _kernelProtection?.Dispose();
            _kernelProtection = null;
            _hardening?.Dispose();
            _hardening = null;
            _secretProtectionActive = false;
        }
    }

    private async Task WritePipeFrameAsync(
        NamedPipeServerStream pipe,
        BrokerMessageType type,
        ulong sequence,
        Guid requestId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        await BrokerProtocol.WriteFrameAsync(
            pipe,
            type,
            sequence,
            requestId,
            payload,
            _authenticationKey,
            cancellationToken);

        // WriteFrameAsync flushes the authenticated frame. Drain synchronously so
        // the one-shot Broker cannot disconnect until the client has consumed it.
        // An early client close fails here and is handled by the operation boundary.
        pipe.WaitForPipeDrain();
    }

    private async Task<PasswordLease> GenerateAndSaveCredentialAsync(
        string origin,
        ReadOnlyMemory<byte> usernameUtf8,
        IntPtr ownerWindow,
        CancellationToken cancellationToken)
    {
        byte[] generatedPassword = BrokerPasswordGenerator.GenerateUtf8();
        byte[]? responseUsername = null;
        try
        {
            await _vault.SavePasswordAsync(
                origin,
                usernameUtf8,
                generatedPassword,
                ownerWindow,
                cancellationToken);

            responseUsername = usernameUtf8.ToArray();

            // PasswordLease copies both values directly into locked unmanaged
            // buffers and zeroes these arrays in its constructor. Do not create a
            // second managed plaintext password copy just to construct the lease.
            PasswordLease lease = new(responseUsername, generatedPassword);
            responseUsername = null;
            return lease;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(generatedPassword);
            BrokerProtocol.Zero(responseUsername);
        }
    }

    private bool TryParseCredentialRequest(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> expectedClientChallenge,
        ReadOnlySpan<byte> expectedServerChallenge,
        out PasswordBrokerOperation operation,
        out ulong navigationId,
        out Guid documentIdentity,
        out IntPtr ownerWindow,
        out byte[] originUtf8,
        out byte[] usernameUtf8)
    {
        operation = default;
        navigationId = 0;
        documentIdentity = Guid.Empty;
        ownerWindow = IntPtr.Zero;
        originUtf8 = [];
        usernameUtf8 = [];

        if (payload.Length < 122 ||
            !Enum.IsDefined((PasswordBrokerOperation)payload[0]) ||
            new Guid(payload.Slice(1, 16)) != _clientInstanceId ||
            !CryptographicOperations.FixedTimeEquals(payload.Slice(17, 32), expectedClientChallenge) ||
            !CryptographicOperations.FixedTimeEquals(payload.Slice(49, 32), expectedServerChallenge))
        {
            return false;
        }

        int originLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(113, 4));
        int usernameLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(117, 4));
        PasswordBrokerOperation parsedOperation = (PasswordBrokerOperation)payload[0];
        if (originLength <= 0 ||
            originLength > BrokerProtocol.MaximumOriginLength ||
            usernameLength < 0 ||
            usernameLength > BrokerProtocol.MaximumUsernameLength ||
            (parsedOperation != PasswordBrokerOperation.GenerateAndSave && usernameLength != 0) ||
            payload.Length != 121 + originLength + usernameLength)
        {
            return false;
        }

        operation = parsedOperation;
        navigationId = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(81, 8));
        documentIdentity = new Guid(payload.Slice(89, 16));
        ownerWindow = new IntPtr(BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(105, 8)));
        originUtf8 = payload.Slice(121, originLength).ToArray();
        usernameUtf8 = payload.Slice(121 + originLength, usernameLength).ToArray();
        return true;
    }

    private bool TryValidateClientProcess() =>
        BrokerNativeMethods.IsExpectedProcessImage(
            _clientProcessId,
            _expectedClientPath,
            _clientSessionId);
}

internal sealed class BrokerProtectionException : Exception
{
    public BrokerProtectionException(string stage, Exception innerException)
        : base("Aero Security Broker could not activate its secret-protection boundary.", innerException)
    {
        Stage = stage;
        ErrorCode = innerException.HResult;
    }

    public string Stage { get; }

    public int ErrorCode { get; }
}
