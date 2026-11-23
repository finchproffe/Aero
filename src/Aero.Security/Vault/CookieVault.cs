using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Aero.App.Security;

public sealed record VaultCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    double ExpiresUnixSeconds,
    bool IsSecure,
    bool IsHttpOnly,
    int SameSite);

public readonly record struct ObservedCookie(VaultCookie Cookie, bool IsSession);

public sealed record CookieVaultReconcileResult(
    IReadOnlyList<VaultCookie> ToMakeSession,
    IReadOnlyList<VaultCookie> ToDelete,
    bool Changed);

/// <summary>
/// The authoritative copy of the profile's persistent cookies. WebView2 only ever holds them as
/// session cookies, which Chromium keeps in memory and never writes to its on-disk Cookies DB,
/// so the file info-stealers read stays (almost) empty.
/// </summary>
public sealed class CookieVaultLedger
{
    private readonly Dictionary<string, VaultCookie> _entries = new(StringComparer.Ordinal);

    public CookieVaultLedger(IEnumerable<VaultCookie> entries)
    {
        foreach (VaultCookie cookie in entries)
        {
            _entries[Key(cookie)] = cookie;
        }
    }

    public IReadOnlyCollection<VaultCookie> Entries => _entries.Values;

    /// <param name="observed">Every cookie currently in the WebView2 profile.</param>
    public CookieVaultReconcileResult Reconcile(IReadOnlyList<ObservedCookie> observed, DateTimeOffset now)
    {
        double nowUnix = now.ToUnixTimeSeconds();
        bool changed = false;
        List<VaultCookie> toMakeSession = [];
        List<VaultCookie> toDelete = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (ObservedCookie observation in observed)
        {
            VaultCookie cookie = observation.Cookie;
            string key = Key(cookie);
            seen.Add(key);

            if (!observation.IsSession)
            {
                // A site set (or refreshed) a persistent cookie: adopt it, then hide it from disk.
                if (cookie.ExpiresUnixSeconds > nowUnix)
                {
                    if (!_entries.TryGetValue(key, out VaultCookie? held) || held != cookie)
                    {
                        _entries[key] = cookie;
                        changed = true;
                    }

                    toMakeSession.Add(cookie);
                }
            }
            else if (_entries.TryGetValue(key, out VaultCookie? held))
            {
                if (held.Value != cookie.Value)
                {
                    // The site replaced our cookie with a genuine session cookie.
                    _entries.Remove(key);
                    changed = true;
                }
                else if (held.ExpiresUnixSeconds <= nowUnix)
                {
                    // Session copies never expire while the browser runs; enforce the real expiry.
                    _entries.Remove(key);
                    toDelete.Add(cookie);
                    changed = true;
                }
            }
        }

        foreach (string key in _entries.Keys.Where(key => !seen.Contains(key)).ToArray())
        {
            _entries.Remove(key); // Deleted by the site (sign-out) or by the user.
            changed = true;
        }

        return new CookieVaultReconcileResult(toMakeSession, toDelete, changed);
    }

    public IReadOnlyList<VaultCookie> Live(DateTimeOffset now)
    {
        double nowUnix = now.ToUnixTimeSeconds();
        return _entries.Values.Where(cookie => cookie.ExpiresUnixSeconds > nowUnix).ToList();
    }

    private static string Key(VaultCookie cookie) =>
        cookie.Domain.ToLowerInvariant() + "\n" + cookie.Path + "\n" + cookie.Name;
}

/// <summary>
/// AES-256-GCM container: version (1) | nonce (12) | tag (16) | ciphertext. Sealing runs in the
/// Rust core, so the key is read only from protected memory and opened plaintext lands there too.
/// </summary>
public static unsafe class CookieVaultFile
{
    private const int HeaderSize = 1 + 12 + 16;

    public static byte[] Seal(SecureBuffer key, IReadOnlyCollection<VaultCookie> cookies, string context)
    {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(cookies);
        try
        {
            return SealBytes(key, plaintext, context);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <exception cref="CryptographicException">Wrong key, wrong profile or tampered file.</exception>
    public static List<VaultCookie> Open(SecureBuffer key, byte[] sealedData, string context)
    {
        using SecureBuffer plaintext = OpenSecure(key, sealedData, context);
        return JsonSerializer.Deserialize<List<VaultCookie>>(plaintext.ReadOnlySpan) ?? [];
    }

    /// <summary>The same container for any payload (the sync files use it too).</summary>
    public static byte[] SealBytes(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, string context)
    {
        using SecureBuffer protectedKey = SecureBuffer.From(key);
        return SealBytes(protectedKey, plaintext, context);
    }

    public static byte[] SealBytes(SecureBuffer key, ReadOnlySpan<byte> plaintext, string context)
    {
        byte[] output = new byte[HeaderSize + plaintext.Length];
        byte[] aad = Encoding.UTF8.GetBytes(context);
        fixed (byte* plain = plaintext)
        fixed (byte* contextBytes = aad)
        fixed (byte* sealedData = output)
        {
            Check(AeroSecurityNative.VaultSeal(
                key.Handle, plain, (nuint)plaintext.Length, contextBytes, (nuint)aad.Length, sealedData, (nuint)output.Length));
        }

        return output;
    }

    /// <summary>Returns the plaintext; the caller zeroes it.</summary>
    /// <exception cref="CryptographicException">Wrong key, wrong context or tampered data.</exception>
    public static byte[] OpenBytes(ReadOnlySpan<byte> key, byte[] sealedData, string context)
    {
        using SecureBuffer protectedKey = SecureBuffer.From(key);
        using SecureBuffer plaintext = OpenSecure(protectedKey, sealedData, context);
        return plaintext.CopyToArray();
    }

    /// <exception cref="CryptographicException">Wrong key, wrong context or tampered data.</exception>
    public static SecureBuffer OpenSecure(SecureBuffer key, ReadOnlySpan<byte> sealedData, string context)
    {
        byte[] aad = Encoding.UTF8.GetBytes(context);
        SecretHandle plain;
        fixed (byte* input = sealedData)
        fixed (byte* contextBytes = aad)
        {
            Check(AeroSecurityNative.VaultOpen(
                key.Handle, input, (nuint)sealedData.Length, contextBytes, (nuint)aad.Length, out plain));
        }

        return SecureBuffer.Adopt(plain);
    }

    private static void Check(int status)
    {
        switch (status)
        {
            case AeroSecurityNative.Ok:
                return;
            case AeroSecurityNative.Rejected:
                throw new CryptographicException("The Aero sealed data is damaged, tampered or belongs to another key or profile.");
            case AeroSecurityNative.InvalidArgument:
                throw new CryptographicException("The Aero vault key or buffer has the wrong size.");
            default:
                throw new CryptographicException("Aero could not protect the vault data in memory.");
        }
    }
}

/// <summary>
/// The vault's AES key file: one mode byte, then the wrapped key. 'A' (default): wrapped by the
/// AeroAppBound service, opens silently but only inside Aero. 'H' (opt-in): wrapped by a
/// non-exportable Windows Hello key, needs the user's face, fingerprint or PIN on every launch.
/// </summary>
public static class CookieVaultKey
{
    private const byte AppBoundMode = (byte)'A';
    private const byte HelloMode = (byte)'H';

    public static string KeyNameFor(string storageRoot)
    {
        string userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new PlatformNotSupportedException("The cookie vault requires a Windows user SID.");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(storageRoot).ToUpperInvariant()));
        return $"{userSid}//Aero//Cookies-{Convert.ToHexString(hash.AsSpan(0, 12))}";
    }

    /// <summary>A new data key, generated into protected memory and concealed there.</summary>
    public static SecureBuffer CreateDataKey() => Concealed(SecureBuffer.Random(32));

    /// <summary>With Hello this creates the Hello key if needed (one prompt).</summary>
    public static byte[] Wrap(SecureBuffer dataKey, bool withHello, string keyName, IntPtr ownerWindow)
    {
        // The wrapping APIs take managed arrays: the plain copy lives only for this call.
        byte[] plain = dataKey.CopyToArray();
        try
        {
            return withHello
                ? [HelloMode, .. WindowsHelloCngKey.EncryptPkcs1(keyName, plain, ownerWindow)]
                : [AppBoundMode, .. AppBoundKey.Wrap(plain)];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>The unwrapped key goes straight into concealed protected memory.</summary>
    public static SecureBuffer Unwrap(string keyName, byte[] wrapped, IntPtr ownerWindow)
    {
        byte[] plain = (wrapped.Length > 1 ? wrapped[0] : 0) switch
        {
            AppBoundMode => AppBoundKey.Unwrap(wrapped[1..]),
            HelloMode => WindowsHelloCngKey.Decrypt(
                keyName, wrapped[1..], ownerWindow, "Unlock your Aero sign-ins", oaepSha256: false),
            _ => throw new CryptographicException("Unknown Aero cookie vault key format.")
        };
        try
        {
            return Concealed(SecureBuffer.From(plain));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static SecureBuffer Concealed(SecureBuffer key)
    {
        try
        {
            key.Conceal();
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public static bool IsHelloWrapped(byte[] wrapped) => wrapped.Length > 0 && wrapped[0] == HelloMode;
}
