using System.Security.Cryptography;

namespace Aero.App.Security;

/// <summary>
/// Owns Aero's device-bound node key. The preferred provider is the
/// Microsoft Platform Crypto Provider, which stores the private key in the TPM.
/// Only the public key is exportable; the private key never leaves the provider.
/// </summary>
internal static class DeviceNodeLockKey
{
    private const string ProviderName = "Microsoft Platform Crypto Provider";
    private const int RsaKeyLengthBits = 2048;
    private static readonly CngProvider Provider = new(ProviderName);

    public static bool Exists(string keyName)
    {
        try
        {
            return CngKey.Exists(keyName, Provider, CngKeyOpenOptions.None);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    public static byte[] GetOrCreateNodeId(string keyName)
    {
        using CngKey key = OpenOrCreate(keyName);
        return ComputeNodeId(key);
    }

    public static byte[] GetExistingNodeId(string keyName)
    {
        using CngKey key = OpenExisting(keyName);
        return ComputeNodeId(key);
    }

    public static byte[] EncryptKeyMaterial(
        string keyName,
        ReadOnlySpan<byte> plaintext)
    {
        using CngKey key = OpenOrCreate(keyName);
        VerifyKey(key);
        using RSACng rsa = new(key);
        byte[] copy = plaintext.ToArray();
        try
        {
            return rsa.Encrypt(copy, RSAEncryptionPadding.OaepSHA256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    public static byte[] DecryptKeyMaterial(
        string keyName,
        ReadOnlySpan<byte> ciphertext)
    {
        using CngKey key = OpenExisting(keyName);
        VerifyKey(key);
        using RSACng rsa = new(key);
        byte[] copy = ciphertext.ToArray();
        try
        {
            return rsa.Decrypt(copy, RSAEncryptionPadding.OaepSHA256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    public static void Delete(string keyName)
    {
        try
        {
            if (!CngKey.Exists(keyName, Provider, CngKeyOpenOptions.None))
            {
                return;
            }

            using CngKey key = CngKey.Open(keyName, Provider, CngKeyOpenOptions.None);
            key.Delete();
        }
        catch (CryptographicException)
        {
            // Clearing the vault is best effort for a provider key that may
            // already have been removed by Windows or a TPM reset.
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private static CngKey OpenOrCreate(string keyName)
    {
        if (CngKey.Exists(keyName, Provider, CngKeyOpenOptions.None))
        {
            return OpenExisting(keyName);
        }

        CngKeyCreationParameters parameters = new()
        {
            Provider = Provider,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Decryption,
            KeyCreationOptions = CngKeyCreationOptions.None
        };
        parameters.Parameters.Add(new CngProperty(
            "Length",
            BitConverter.GetBytes(RsaKeyLengthBits),
            CngPropertyOptions.None));

        CngKey key;
        try
        {
            key = CngKey.Create(CngAlgorithm.Rsa, keyName, parameters);
        }
        catch (CryptographicException) when (Exists(keyName))
        {
            // Two Aero instances can perform the first migration at the same
            // time. Open the winner's key rather than weakening the vault or
            // failing the protected operation because of that benign race.
            return OpenExisting(keyName);
        }

        try
        {
            VerifyKey(key);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static CngKey OpenExisting(string keyName)
    {
        if (!CngKey.Exists(keyName, Provider, CngKeyOpenOptions.None))
        {
            throw new CryptographicException(
                "The TPM-backed Aero Node Lock key is unavailable on this device.");
        }

        CngKey key = CngKey.Open(keyName, Provider, CngKeyOpenOptions.None);
        try
        {
            VerifyKey(key);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static void VerifyKey(CngKey key)
    {
        if (!IsExpectedHardwareKey(key))
        {
            throw new CryptographicException(
                "Aero refused a Node Lock key that is not an expected non-exportable TPM RSA key.");
        }
    }

    private static bool IsExpectedHardwareKey(CngKey key)
    {
        bool exportable = key.ExportPolicy != CngExportPolicies.None;
        CngProvider? provider = key.Provider;
        return !exportable &&
            provider is not null &&
            provider.Provider.Equals(ProviderName, StringComparison.Ordinal) &&
            CngAlgorithmGroup.Rsa.Equals(key.AlgorithmGroup) &&
            (key.KeyUsage & CngKeyUsages.Decryption) != 0;
    }

    private static byte[] ComputeNodeId(CngKey key)
    {
        byte[] publicBlob = key.Export(CngKeyBlobFormat.GenericPublicBlob);
        try
        {
            return SHA256.HashData(publicBlob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicBlob);
        }
    }
}
