using System.Security.Cryptography;

namespace Aero.App.Security;

/// <summary>
/// Test-only stand-in for the Windows Hello vault key: an in-memory RSA key plus a scripted user
/// decision, so the real unlock and reveal path can run without a face, finger or PIN. Reachable
/// only through the internal <see cref="PasswordVaultService"/> constructor (InternalsVisibleTo
/// the smoke tests); nothing in Aero creates one, and it never touches the TPM.
/// </summary>
internal sealed class SimulatedWindowsHello : IDisposable
{
    private readonly RSA _key = RSA.Create(2048);

    public bool UserApproves { get; set; } = true;

    public int Prompts { get; private set; }

    public byte[] Encrypt(byte[] data) => _key.Encrypt(data, RSAEncryptionPadding.Pkcs1);

    public byte[] Decrypt(byte[] data)
    {
        Prompts++;
        return UserApproves
            ? _key.Decrypt(data, RSAEncryptionPadding.Pkcs1)
            : throw new CryptographicException("The simulated Windows Hello prompt was cancelled.");
    }

    public void Dispose() => _key.Dispose();
}
