// Aero Vault. Vault is Powered by Aero.
// Copyright (c) 2026 finchproffe. Licensed under the Mozilla Public License 2.0.
// Under MPL-2.0 section 3.4 this notice must stay in every copy and fork of this file.
using System.Security.Cryptography;

namespace Aero.App.Security;

/// <summary>
/// A small secret owned by the Rust core: outside the GC heap, fenced by no-access guard pages,
/// locked in RAM when Windows permits it, excluded from crash dumps and wiped on release.
/// <see cref="Conceal"/> keeps a long-lived key encrypted in place between uses.
/// </summary>
public sealed unsafe class SecureBuffer : IDisposable
{
    private readonly SecretHandle _handle;

    private SecureBuffer(SecretHandle handle)
    {
        _handle = handle;
    }

    public int Length => (int)AeroSecurityNative.SecretLength(Handle);

    public bool IsPageLocked => AeroSecurityNative.SecretIsLocked(Handle) != 0;

    public bool IsConcealed => AeroSecurityNative.SecretData(Handle) is null;

    /// <exception cref="InvalidOperationException">The buffer is concealed.</exception>
    public ReadOnlySpan<byte> ReadOnlySpan => Span;

    internal Span<byte> Span
    {
        get
        {
            byte* data = AeroSecurityNative.SecretData(Handle);
            return data is null
                ? throw new InvalidOperationException("A concealed secret is only usable inside the Aero native core.")
                : new Span<byte>(data, Length);
        }
    }

    internal SecretHandle Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return _handle;
        }
    }

    public static SecureBuffer From(ReadOnlySpan<byte> source)
    {
        SecureBuffer buffer = Allocate(source.Length, random: false);
        source.CopyTo(buffer.Span);
        return buffer;
    }

    /// <summary>A fresh key from the OS CSPRNG, generated straight into protected memory.</summary>
    public static SecureBuffer Random(int length) => Allocate(length, random: true);

    internal static SecureBuffer Adopt(SecretHandle handle) => new(handle);

    private static SecureBuffer Allocate(int length, bool random)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        int status = AeroSecurityNative.SecretNew((nuint)length, random ? 1 : 0, out SecretHandle handle);
        if (status != AeroSecurityNative.Ok)
        {
            handle.Dispose();
            throw new OutOfMemoryException("Aero could not allocate protected memory for a secret.");
        }

        return new SecureBuffer(handle);
    }

    /// <summary>
    /// Encrypts the secret in place with a key only this process can use, for the rest of its
    /// life: memory scrapers and dumps see ciphertext. Call before sharing the buffer.
    /// </summary>
    public void Conceal()
    {
        if (AeroSecurityNative.SecretConceal(Handle) != AeroSecurityNative.Ok)
        {
            throw new CryptographicException("Aero could not conceal a secret in memory.");
        }
    }

    /// <summary>A managed copy (decrypted if concealed); the caller zeroes it.</summary>
    public byte[] CopyToArray()
    {
        byte[] copy = GC.AllocateUninitializedArray<byte>(Length);
        fixed (byte* destination = copy)
        {
            if (AeroSecurityNative.SecretReveal(Handle, destination, (nuint)copy.Length) != AeroSecurityNative.Ok)
            {
                CryptographicOperations.ZeroMemory(copy);
                throw new CryptographicException("Aero could not read a protected secret.");
            }
        }

        return copy;
    }

    public void Dispose() => _handle.Dispose();
}
