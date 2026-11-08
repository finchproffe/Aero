using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aero.App.Security;

/// <summary>One synced record. Payload is opaque bytes so passwords never become .NET strings.</summary>
public sealed record SyncItem(string Key, long ModifiedAt, bool Deleted, byte[] Payload);

/// <summary>
/// Free sync through a folder the user's own cloud client syncs (OneDrive, Yandex Disk, Google
/// Drive, Dropbox, Syncthing). There is no server: every device writes only its own encrypted
/// files and reads the others', so the cloud never sees two writers on one file.
/// </summary>
public static class FolderSync
{
    public const string HeaderFileName = "aero-sync.json";
    public const string LibraryExtension = ".library";
    public const string PasswordsExtension = ".passwords";
    public static readonly TimeSpan TombstoneLifetime = TimeSpan.FromDays(180);

    // OWASP 2023 guidance for PBKDF2-HMAC-SHA256. A stolen sync file is attacked offline,
    // so the passphrase cost must be paid here, once per setup or password sync.
    private const int Iterations = 600_000;
    private const int MinimumPassphraseLength = 10;
    private static readonly byte[] CheckPlaintext = "aero-sync-check-v1"u8.ToArray();

    private sealed record Header(int Version, string Salt, int Iterations, string Check);

    /// <summary>
    /// The passphrase yields two independent keys. The library key is stored on the device so
    /// bookmarks sync silently; the password key is never stored, so the synced passwords stay
    /// behind the passphrase even if the device's stored key leaks.
    /// </summary>
    public sealed class Keys : IDisposable
    {
        internal Keys(byte[] library, byte[] passwords)
        {
            Library = library;
            Passwords = passwords;
        }

        public byte[] Library { get; }

        public byte[] Passwords { get; }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Library);
            CryptographicOperations.ZeroMemory(Passwords);
        }
    }

    public static bool IsPassphraseStrongEnough(string passphrase) =>
        passphrase.Length >= MinimumPassphraseLength;

    /// <summary>Creates the folder header on first use, otherwise checks the passphrase against it.</summary>
    /// <exception cref="CryptographicException">The passphrase does not match this sync folder.</exception>
    public static Keys OpenOrCreate(string folder, string passphrase)
    {
        string headerPath = Path.Combine(folder, HeaderFileName);
        if (!File.Exists(headerPath))
        {
            byte[] newSalt = RandomNumberGenerator.GetBytes(32);
            Keys created = Derive(passphrase, newSalt, Iterations);
            Header header = new(
                1,
                Convert.ToBase64String(newSalt),
                Iterations,
                Convert.ToBase64String(CookieVaultFile.SealBytes(created.Library, CheckPlaintext, "aero-sync-check")));
            File.WriteAllText(headerPath, JsonSerializer.Serialize(header));
            return created;
        }

        Header existing = JsonSerializer.Deserialize<Header>(File.ReadAllText(headerPath))
            ?? throw new CryptographicException("The sync folder header is damaged.");
        Keys keys = Derive(passphrase, Convert.FromBase64String(existing.Salt), existing.Iterations);
        try
        {
            byte[] check = CookieVaultFile.OpenBytes(keys.Library, Convert.FromBase64String(existing.Check), "aero-sync-check");
            if (!check.AsSpan().SequenceEqual(CheckPlaintext))
            {
                throw new CryptographicException("The sync folder header is damaged.");
            }

            return keys;
        }
        catch
        {
            keys.Dispose();
            throw;
        }
    }

    private static Keys Derive(string passphrase, byte[] salt, int iterations)
    {
        byte[] master = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            return new Keys(
                HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "aero-sync-library"u8.ToArray()),
                HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "aero-sync-passwords"u8.ToArray()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    // The device id is bound into the ciphertext, so one device's file cannot be swapped in for another's.
    public static string Context(string extension, string deviceId) => "aero-sync" + extension + ":" + deviceId;

    public static string FilePath(string folder, string deviceId, string extension) =>
        Path.Combine(folder, deviceId + extension);

    /// <summary>Reads every other device's file of one kind; unreadable files are skipped, not fatal.</summary>
    public static List<List<SyncItem>> ReadOthers(string folder, string ownDeviceId, string extension, byte[] key)
    {
        List<List<SyncItem>> sets = [];
        foreach (string path in Directory.EnumerateFiles(folder, "*" + extension))
        {
            string deviceId = Path.GetFileNameWithoutExtension(path);
            if (deviceId.Equals(ownDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                sets.Add(ReadFile(path, Context(extension, deviceId), key));
            }
            catch (Exception exception) when (exception is CryptographicException or IOException or InvalidDataException)
            {
                // A half-synced cloud file or a device from another passphrase: next round retries.
            }
        }

        return sets;
    }

    public static List<SyncItem> ReadFile(string path, string context, byte[] key)
    {
        byte[] plaintext = CookieVaultFile.OpenBytes(key, File.ReadAllBytes(path), context);
        try
        {
            return Deserialize(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static void WriteFile(string path, string context, byte[] key, IReadOnlyCollection<SyncItem> items)
    {
        byte[] plaintext = Serialize(items);
        try
        {
            // Write-then-rename so the cloud client never uploads a half-written file.
            string temporary = path + ".tmp";
            File.WriteAllBytes(temporary, CookieVaultFile.SealBytes(key, plaintext, context));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Timestamps this device's current state against what it published last time: unchanged
    /// items keep their time, new or edited ones get <paramref name="now"/>, vanished ones become
    /// tombstones. An empty local state never deletes anything: that is a lost or reset store
    /// (new Windows, cleared vault), and it must be refilled from the other devices, not wipe them.
    /// </summary>
    public static List<SyncItem> Stamp(
        IReadOnlyDictionary<string, byte[]> current,
        IReadOnlyCollection<SyncItem> previouslyPublished,
        long now)
    {
        Dictionary<string, SyncItem> previous = previouslyPublished.ToDictionary(item => item.Key, StringComparer.Ordinal);
        List<SyncItem> stamped = [];
        foreach ((string key, byte[] payload) in current)
        {
            stamped.Add(previous.TryGetValue(key, out SyncItem? old) && !old.Deleted && old.Payload.AsSpan().SequenceEqual(payload)
                ? old
                : new SyncItem(key, now, false, payload));
        }

        long expiry = now - (long)TombstoneLifetime.TotalSeconds;
        foreach (SyncItem old in previous.Values)
        {
            if (current.ContainsKey(old.Key))
            {
                continue;
            }

            if (old.Deleted)
            {
                if (old.ModifiedAt > expiry)
                {
                    stamped.Add(old);
                }
            }
            else if (current.Count > 0)
            {
                stamped.Add(new SyncItem(old.Key, now, true, []));
            }
        }

        return stamped;
    }

    /// <summary>Newest change per key wins; on an exact tie a deletion wins so it cannot resurrect.</summary>
    public static Dictionary<string, SyncItem> Merge(IEnumerable<IReadOnlyCollection<SyncItem>> sets)
    {
        Dictionary<string, SyncItem> winners = new(StringComparer.Ordinal);
        foreach (IReadOnlyCollection<SyncItem> set in sets)
        {
            foreach (SyncItem item in set)
            {
                if (!winners.TryGetValue(item.Key, out SyncItem? best) ||
                    item.ModifiedAt > best.ModifiedAt ||
                    item.ModifiedAt == best.ModifiedAt && item.Deleted && !best.Deleted)
                {
                    winners[item.Key] = item;
                }
            }
        }

        return winners;
    }

    // Record: key length (u16) | key UTF-8 | modified (i64) | deleted (u8) | payload length (i32) | payload.
    public static byte[] Serialize(IReadOnlyCollection<SyncItem> items)
    {
        using MemoryStream stream = new();
        Span<byte> number = stackalloc byte[8];
        foreach (SyncItem item in items)
        {
            byte[] key = Encoding.UTF8.GetBytes(item.Key);
            BinaryPrimitives.WriteUInt16LittleEndian(number, checked((ushort)key.Length));
            stream.Write(number[..2]);
            stream.Write(key);
            BinaryPrimitives.WriteInt64LittleEndian(number, item.ModifiedAt);
            stream.Write(number);
            stream.WriteByte(item.Deleted ? (byte)1 : (byte)0);
            BinaryPrimitives.WriteInt32LittleEndian(number, item.Payload.Length);
            stream.Write(number[..4]);
            stream.Write(item.Payload);
        }

        // GetBuffer + exact copy so no oversized buffer with secrets is left behind unzeroed.
        byte[] result = stream.ToArray();
        CryptographicOperations.ZeroMemory(stream.GetBuffer());
        return result;
    }

    /// <exception cref="InvalidDataException">The data is truncated or malformed.</exception>
    public static List<SyncItem> Deserialize(ReadOnlySpan<byte> data)
    {
        List<SyncItem> items = [];
        while (!data.IsEmpty)
        {
            if (data.Length < 2)
            {
                throw new InvalidDataException("Truncated sync record.");
            }

            int keyLength = BinaryPrimitives.ReadUInt16LittleEndian(data);
            if (data.Length < 2 + keyLength + 13)
            {
                throw new InvalidDataException("Truncated sync record.");
            }

            string key = Encoding.UTF8.GetString(data.Slice(2, keyLength));
            data = data[(2 + keyLength)..];
            long modified = BinaryPrimitives.ReadInt64LittleEndian(data);
            bool deleted = data[8] == 1;
            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(data[9..]);
            data = data[13..];
            if (payloadLength < 0 || payloadLength > data.Length)
            {
                throw new InvalidDataException("Truncated sync record.");
            }

            items.Add(new SyncItem(key, modified, deleted, data[..payloadLength].ToArray()));
            data = data[payloadLength..];
        }

        return items;
    }
}
