using System.Buffers.Binary;

namespace Aero.Security.TestLab.Scanning;

internal sealed record NodeLockInspection(
    bool Exists,
    bool StructurallyValid,
    bool LegacyHelloOnlyEnvelopePresent,
    long FileLength,
    string Message);

internal static class NodeLockInspector
{
    private const int MaximumEnvelopeLength = 64 * 1024;
    private const int NodeIdLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private static readonly byte[] Magic = "AERONDL1"u8.ToArray();

    public static NodeLockInspection Inspect(string securityDirectory)
    {
        string fullDirectory = Path.GetFullPath(securityDirectory);
        string nodeLockPath = Path.Combine(fullDirectory, ".vault-node-lock.bin");
        string legacyEnvelopePath = Path.Combine(fullDirectory, ".vault-key-v2.bin");
        bool legacyPresent = File.Exists(legacyEnvelopePath);

        if (!File.Exists(nodeLockPath))
        {
            return new NodeLockInspection(
                Exists: false,
                StructurallyValid: false,
                LegacyHelloOnlyEnvelopePresent: legacyPresent,
                FileLength: 0,
                Message: legacyPresent
                    ? "Node Lock is not active; a legacy Windows Hello-only envelope is present."
                    : "No Node Lock envelope was found.");
        }

        FileInfo file = new(nodeLockPath);
        if (file.Length <= Magic.Length || file.Length > MaximumEnvelopeLength)
        {
            return Invalid(file.Length, legacyPresent, "Node Lock file length is invalid.");
        }

        byte[] data = File.ReadAllBytes(nodeLockPath);
        try
        {
            const int lengthFieldCount = 6;
            int minimumHeader = Magic.Length + lengthFieldCount * sizeof(int);
            if (data.Length < minimumHeader ||
                !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                return Invalid(file.Length, legacyPresent, "Node Lock magic/version is invalid.");
            }

            int offset = Magic.Length;
            int padding = ReadInt32(data, ref offset);
            int nodeIdLength = ReadInt32(data, ref offset);
            int wrappedNodeKeyLength = ReadInt32(data, ref offset);
            int nonceLength = ReadInt32(data, ref offset);
            int tagLength = ReadInt32(data, ref offset);
            int ciphertextLength = ReadInt32(data, ref offset);

            if (padding is < 0 or > 1 ||
                nodeIdLength != NodeIdLength ||
                wrappedNodeKeyLength <= 0 || wrappedNodeKeyLength > 4096 ||
                nonceLength != NonceLength ||
                tagLength != TagLength ||
                ciphertextLength <= 0 || ciphertextLength > 16 * 1024)
            {
                return Invalid(file.Length, legacyPresent, "Node Lock field lengths are invalid.");
            }

            int expectedLength = checked(
                offset + nodeIdLength + wrappedNodeKeyLength + nonceLength +
                tagLength + ciphertextLength);
            if (expectedLength != data.Length)
            {
                return Invalid(file.Length, legacyPresent, "Node Lock envelope is truncated.");
            }

            return new NodeLockInspection(
                Exists: true,
                StructurallyValid: true,
                LegacyHelloOnlyEnvelopePresent: legacyPresent,
                FileLength: file.Length,
                Message: legacyPresent
                    ? "Node Lock is valid, but the legacy Hello-only envelope is still present and must be removed after a successful unlock."
                    : "Node Lock envelope is structurally valid and no legacy bypass envelope is present.");
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or OverflowException)
        {
            return Invalid(file.Length, legacyPresent, "Node Lock inspection failed safely.");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(data);
        }
    }

    private static int ReadInt32(byte[] data, ref int offset)
    {
        if (offset < 0 || data.Length - offset < sizeof(int))
        {
            throw new InvalidDataException("Truncated Node Lock envelope.");
        }

        int value = BinaryPrimitives.ReadInt32LittleEndian(
            data.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        return value;
    }

    private static NodeLockInspection Invalid(
        long length,
        bool legacyPresent,
        string message) =>
        new(
            Exists: true,
            StructurallyValid: false,
            LegacyHelloOnlyEnvelopePresent: legacyPresent,
            FileLength: length,
            Message: message);
}
