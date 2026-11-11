using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aero.App.Data;

internal enum ProfileBackupEncryption
{
    None = 0,
    Password = 1,
    CurrentWindowsUser = 2
}

internal sealed record ProfileBackupFile(
    string Path,
    long Length,
    string Sha256);

internal sealed record ProfileBackupManifest(
    int SchemaVersion,
    string AeroVersion,
    DateTimeOffset CreatedAt,
    string ProfileId,
    string ProfileName,
    int SkippedFileCount,
    IReadOnlyList<ProfileBackupFile> Files);

internal sealed record ProfileBackupInfo(
    string FileName,
    string FullPath,
    DateTimeOffset CreatedAt,
    long Size);

internal static class ProfileBackupService
{
    public const string CurrentAeroVersion = "0.26.0.0";
    public const string BackupExtension = ".aerobackup";

    private const int SchemaVersion = 1;
    private const int PasswordIterations = 210_000;
    private const long MaximumSingleFileBytes = 128L * 1024 * 1024;
    private const long MaximumBackupInputBytes = 768L * 1024 * 1024;
    private const string PendingFolderName = "RestorePending";
    private const string PendingMarkerName = "restore-pending.json";

    private static readonly byte[] EnvelopeMagic = "AEROBK18"u8.ToArray();
    private static readonly byte[] BackupEntropy =
        SHA256.HashData("Aero 0.18 automatic profile backup"u8.ToArray());
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] ManagedFiles =
    [
        "settings.json",
        "library.json",
        "session.json",
        "site-permissions.json",
        "extensions.json"
    ];

    private static readonly string[] ManagedFolders =
    [
        "Extensions"
    ];

    public static string AutomaticBackupFolder(string storageRoot) =>
        Path.Combine(storageRoot, "Backups");

    public static IReadOnlyList<ProfileBackupInfo> ListAutomaticBackups(string storageRoot)
    {
        string folder = AutomaticBackupFolder(storageRoot);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder, "*" + BackupExtension)
            .Select(path =>
            {
                FileInfo file = new(path);
                return new ProfileBackupInfo(
                    file.Name,
                    file.FullName,
                    new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                    file.Length);
            })
            .OrderByDescending(item => item.CreatedAt)
            .ToList();
    }

    public static bool IsAutomaticBackupDue(
        DateTimeOffset? lastBackupUtc,
        int intervalHours) =>
        lastBackupUtc is null ||
        DateTimeOffset.UtcNow - lastBackupUtc.Value >=
            TimeSpan.FromHours(Math.Clamp(intervalHours, 1, 168));

    public static async Task<string> CreateAutomaticBackupAsync(
        BrowserProfile profile,
        int retentionCount,
        CancellationToken cancellationToken = default)
    {
        string folder = AutomaticBackupFolder(profile.StorageRoot);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(
            folder,
            $"Aero-auto-{DateTime.UtcNow:yyyyMMdd-HHmmss}{BackupExtension}");

        await CreateBackupAsync(
            profile,
            path,
            ProfileBackupEncryption.CurrentWindowsUser,
            password: null,
            cancellationToken);

        BackupPayload verified = ReadAndValidate(path, password: null);
        CryptographicOperations.ZeroMemory(verified.ZipBytes);
        TrimAutomaticBackups(profile.StorageRoot, retentionCount);
        return path;
    }

    public static Task<ProfileBackupManifest> CreateBackupAsync(
        BrowserProfile profile,
        string destinationPath,
        ProfileBackupEncryption encryption,
        string? password,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (encryption == ProfileBackupEncryption.Password &&
                string.IsNullOrWhiteSpace(password))
            {
                throw new ArgumentException(
                    "Enter a password to encrypt this backup.",
                    nameof(password));
            }

            byte[] zip = BuildZip(profile, encryption, cancellationToken, out ProfileBackupManifest manifest);
            try
            {
                byte[] envelope = Protect(zip, encryption, password);
                try
                {
                    WriteAtomically(destinationPath, envelope);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(envelope);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(zip);
            }

            return manifest;
        }, cancellationToken);

    public static Task<ProfileBackupManifest> ValidateBackupAsync(
        string path,
        string? password,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            BackupPayload payload = ReadAndValidate(path, password);
            try
            {
                return payload.Manifest;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload.ZipBytes);
            }
        }, cancellationToken);

    public static Task<ProfileBackupManifest> StageRestoreAsync(
        BrowserProfile profile,
        string backupPath,
        string? password,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            BackupPayload payload = ReadAndValidate(backupPath, password);
            try
            {
                EnsureCompatible(payload.Manifest);

                string pendingRoot = Path.Combine(profile.StorageRoot, PendingFolderName);
                ResetDirectory(pendingRoot);
                ExtractPayload(payload.ZipBytes, pendingRoot, payload.Manifest, cancellationToken);
                File.WriteAllText(
                    Path.Combine(pendingRoot, PendingMarkerName),
                    JsonSerializer.Serialize(payload.Manifest, JsonOptions));
                return payload.Manifest;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload.ZipBytes);
            }
        }, cancellationToken);

    public static Task<ProfileBackupManifest> StageLatestRecoveryAsync(
        BrowserProfile profile,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            List<Exception> failures = [];
            foreach (ProfileBackupInfo backup in ListAutomaticBackups(profile.StorageRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    BackupPayload payload = ReadAndValidate(backup.FullPath, password: null);
                    try
                    {
                        EnsureCompatible(payload.Manifest);
                        string pendingRoot = Path.Combine(profile.StorageRoot, PendingFolderName);
                        ResetDirectory(pendingRoot);
                        ExtractPayload(payload.ZipBytes, pendingRoot, payload.Manifest, cancellationToken);
                        File.WriteAllText(
                            Path.Combine(pendingRoot, PendingMarkerName),
                            JsonSerializer.Serialize(payload.Manifest, JsonOptions));
                        return payload.Manifest;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(payload.ZipBytes);
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            throw new InvalidDataException(
                failures.Count == 0
                    ? "No automatic backup is available for this profile."
                    : "No intact automatic backup could be opened.",
                failures.FirstOrDefault());
        }, cancellationToken);

    public static void ApplyPendingRestore(string storageRoot)
    {
        string pendingRoot = Path.Combine(storageRoot, PendingFolderName);
        string markerPath = Path.Combine(pendingRoot, PendingMarkerName);
        if (!File.Exists(markerPath))
        {
            return;
        }

        ProfileBackupManifest manifest = JsonSerializer.Deserialize<ProfileBackupManifest>(
            File.ReadAllText(markerPath),
            JsonOptions) ?? throw new InvalidDataException("The pending restore marker is invalid.");
        EnsureCompatible(manifest);

        foreach (ProfileBackupFile backupFile in manifest.Files)
        {
            ThrowIfForbiddenBackupPath(backupFile.Path);
            string source = ResolveInside(pendingRoot, backupFile.Path);
            string destination = ResolveInside(storageRoot, backupFile.Path);
            if (!File.Exists(source))
            {
                throw new InvalidDataException(
                    $"The pending restore is missing {backupFile.Path}.");
            }

            FileInfo sourceInfo = new(source);
            if (sourceInfo.Length != backupFile.Length ||
                !ComputeSha256(source).Equals(
                    backupFile.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The staged integrity check failed for {backupFile.Path}.");
            }

            string? directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = destination + ".restoretmp";
            File.Copy(source, temporary, overwrite: true);
            File.Move(temporary, destination, overwrite: true);
        }

        Directory.Delete(pendingRoot, recursive: true);
    }

    public static bool TryRecoverCorruptedProfile(BrowserProfile profile)
    {
        if (!ContainsCorruptManagedJson(profile.StorageRoot))
        {
            return false;
        }

        foreach (ProfileBackupInfo backup in ListAutomaticBackups(profile.StorageRoot))
        {
            try
            {
                BackupPayload payload = ReadAndValidate(backup.FullPath, password: null);
                EnsureCompatible(payload.Manifest);
                string pendingRoot = Path.Combine(profile.StorageRoot, PendingFolderName);
                ResetDirectory(pendingRoot);
                ExtractPayload(payload.ZipBytes, pendingRoot, payload.Manifest, CancellationToken.None);
                File.WriteAllText(
                    Path.Combine(pendingRoot, PendingMarkerName),
                    JsonSerializer.Serialize(payload.Manifest, JsonOptions));
                CryptographicOperations.ZeroMemory(payload.ZipBytes);
                ApplyPendingRestore(profile.StorageRoot);
                return true;
            }
            catch
            {
                // Try the next older intact automatic backup.
            }
        }

        return false;
    }

    private static byte[] BuildZip(
        BrowserProfile profile,
        ProfileBackupEncryption encryption,
        CancellationToken cancellationToken,
        out ProfileBackupManifest manifest)
    {
        List<SourceFile> sources = CollectSourceFiles(
            profile,
            cancellationToken,
            out int skipped);
        List<ProfileBackupFile> files = [];
        using MemoryStream memory = new();
        using (ZipArchive archive = new(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (SourceFile source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] bytes;
                try
                {
                    bytes = ReadSharedBytes(source.FullPath);
                }
                catch
                {
                    skipped++;
                    continue;
                }

                try
                {
                    ZipArchiveEntry entry = archive.CreateEntry(
                        "profile/" + NormalizeArchivePath(source.RelativePath),
                        CompressionLevel.Optimal);
                    using Stream output = entry.Open();
                    output.Write(bytes);
                    files.Add(new ProfileBackupFile(
                        NormalizeArchivePath(source.RelativePath),
                        bytes.LongLength,
                        Convert.ToHexString(SHA256.HashData(bytes))));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            manifest = new ProfileBackupManifest(
                SchemaVersion,
                CurrentAeroVersion,
                DateTimeOffset.UtcNow,
                profile.Id,
                profile.Name,
                skipped,
                files);
            ZipArchiveEntry manifestEntry = archive.CreateEntry(
                "manifest.json",
                CompressionLevel.Optimal);
            using StreamWriter writer = new(
                manifestEntry.Open(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(JsonSerializer.Serialize(manifest, JsonOptions));
        }

        return memory.ToArray();
    }

    private static List<SourceFile> CollectSourceFiles(
        BrowserProfile profile,
        CancellationToken cancellationToken,
        out int skipped)
    {
        int skippedCount = 0;
        long totalBytes = 0;
        Dictionary<string, SourceFile> sources = new(StringComparer.OrdinalIgnoreCase);

        void AddFile(string fullPath, string relativePath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileInfo file = new(fullPath);
                if (!file.Exists ||
                    file.Length > MaximumSingleFileBytes ||
                    totalBytes + file.Length > MaximumBackupInputBytes)
                {
                    if (file.Exists) skippedCount++;
                    return;
                }

                string normalized = NormalizeArchivePath(relativePath);
                sources[normalized] = new SourceFile(file.FullName, normalized);
                totalBytes += file.Length;
            }
            catch
            {
                skippedCount++;
            }
        }

        foreach (string file in ManagedFiles)
        {
            AddFile(Path.Combine(profile.StorageRoot, file), file);
        }

        foreach (string folder in ManagedFolders)
        {
            string fullFolder = Path.Combine(profile.StorageRoot, folder);
            if (!Directory.Exists(fullFolder))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(fullFolder, "*", SearchOption.AllDirectories))
            {
                AddFile(file, Path.GetRelativePath(profile.StorageRoot, file));
            }
        }


        skipped = skippedCount;
        return sources.Values
            .OrderBy(source => source.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static byte[] Protect(
        byte[] plaintext,
        ProfileBackupEncryption encryption,
        string? password)
    {
        using MemoryStream memory = new();
        using BinaryWriter writer = new(memory, Encoding.UTF8, leaveOpen: true);
        writer.Write(EnvelopeMagic);
        writer.Write(SchemaVersion);
        writer.Write((byte)encryption);

        if (encryption == ProfileBackupEncryption.None)
        {
            writer.Write(plaintext.Length);
            writer.Write(plaintext);
        }
        else if (encryption == ProfileBackupEncryption.CurrentWindowsUser)
        {
            byte[] protectedBytes = ProtectedData.Protect(
                plaintext,
                BackupEntropy,
                DataProtectionScope.CurrentUser);
            writer.Write(protectedBytes.Length);
            writer.Write(protectedBytes);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
        else
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] tag = new byte[16];
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(
                password!,
                salt,
                PasswordIterations,
                HashAlgorithmName.SHA256,
                32);

            try
            {
                using AesGcm aes = new(key, tagSizeInBytes: 16);
                aes.Encrypt(nonce, plaintext, ciphertext, tag, EnvelopeMagic);
                writer.Write(PasswordIterations);
                writer.Write(salt.Length);
                writer.Write(salt);
                writer.Write(nonce.Length);
                writer.Write(nonce);
                writer.Write(tag.Length);
                writer.Write(tag);
                writer.Write(ciphertext.Length);
                writer.Write(ciphertext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(tag);
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(nonce);
            }
        }

        writer.Flush();
        return memory.ToArray();
    }

    private static BackupPayload ReadAndValidate(string path, string? password)
    {
        byte[] envelope = ReadSharedBytes(path);
        byte[] zip;
        try
        {
            zip = Unprotect(envelope, password);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }

        try
        {
            ProfileBackupManifest manifest = ValidateZip(zip);
            return new BackupPayload(manifest, zip);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(zip);
            throw;
        }
    }

    private static byte[] Unprotect(byte[] envelope, string? password)
    {
        using MemoryStream memory = new(envelope, writable: false);
        using BinaryReader reader = new(memory, Encoding.UTF8, leaveOpen: false);
        byte[] magic = reader.ReadBytes(EnvelopeMagic.Length);
        if (!magic.SequenceEqual(EnvelopeMagic))
        {
            throw new InvalidDataException("This is not an Aero 0.18 backup.");
        }

        int schema = reader.ReadInt32();
        if (schema > SchemaVersion)
        {
            throw new InvalidDataException(
                "This backup was created by a newer incompatible Aero version.");
        }

        ProfileBackupEncryption encryption = (ProfileBackupEncryption)reader.ReadByte();
        if (encryption == ProfileBackupEncryption.None)
        {
            return reader.ReadBytes(ReadSafeLength(reader));
        }

        if (encryption == ProfileBackupEncryption.CurrentWindowsUser)
        {
            byte[] protectedBytes = reader.ReadBytes(ReadSafeLength(reader));
            try
            {
                return ProtectedData.Unprotect(
                    protectedBytes,
                    BackupEntropy,
                    DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException exception)
            {
                throw new CryptographicException(
                    "This automatic backup belongs to another Windows user.",
                    exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }

        if (encryption != ProfileBackupEncryption.Password)
        {
            throw new InvalidDataException("The backup uses an unknown encryption mode.");
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new CryptographicException("Enter the password for this encrypted backup.");
        }

        int iterations = reader.ReadInt32();
        if (iterations < 100_000 || iterations > 2_000_000)
        {
            throw new InvalidDataException("The backup key parameters are invalid.");
        }

        byte[] salt = reader.ReadBytes(ReadSafeLength(reader, 64));
        byte[] nonce = reader.ReadBytes(ReadSafeLength(reader, 32));
        byte[] tag = reader.ReadBytes(ReadSafeLength(reader, 32));
        byte[] ciphertext = reader.ReadBytes(ReadSafeLength(reader));
        byte[] plaintext = new byte[ciphertext.Length];
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            32);

        try
        {
            using AesGcm aes = new(key, tagSizeInBytes: tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, EnvelopeMagic);
            return plaintext;
        }
        catch (CryptographicException exception)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new CryptographicException(
                "The backup password is incorrect or the file was modified.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private static ProfileBackupManifest ValidateZip(byte[] zip)
    {
        using MemoryStream memory = new(zip, writable: false);
        using ZipArchive archive = new(memory, ZipArchiveMode.Read);
        ZipArchiveEntry manifestEntry = archive.GetEntry("manifest.json") ??
            throw new InvalidDataException("The backup manifest is missing.");
        using Stream manifestStream = manifestEntry.Open();
        ProfileBackupManifest manifest =
            JsonSerializer.Deserialize<ProfileBackupManifest>(manifestStream, JsonOptions) ??
            throw new InvalidDataException("The backup manifest is invalid.");

        if (manifest.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException("The backup schema is incompatible.");
        }

        foreach (ProfileBackupFile file in manifest.Files)
        {
            ThrowIfForbiddenBackupPath(file.Path);
            ZipArchiveEntry entry = archive.GetEntry(
                "profile/" + NormalizeArchivePath(file.Path)) ??
                throw new InvalidDataException($"The backup is missing {file.Path}.");
            if (entry.Length != file.Length)
            {
                throw new InvalidDataException($"The size check failed for {file.Path}.");
            }

            using Stream input = entry.Open();
            using SHA256 sha = SHA256.Create();
            string actual = Convert.ToHexString(sha.ComputeHash(input));
            if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The integrity check failed for {file.Path}.");
            }
        }

        return manifest;
    }

    private static void ExtractPayload(
        byte[] zip,
        string pendingRoot,
        ProfileBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        using MemoryStream memory = new(zip, writable: false);
        using ZipArchive archive = new(memory, ZipArchiveMode.Read);
        foreach (ProfileBackupFile file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfForbiddenBackupPath(file.Path);
            ZipArchiveEntry entry = archive.GetEntry(
                "profile/" + NormalizeArchivePath(file.Path)) ??
                throw new InvalidDataException($"The backup is missing {file.Path}.");
            string destination = ResolveInside(pendingRoot, file.Path);
            string? directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using Stream input = entry.Open();
            using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write);
            input.CopyTo(output);
        }
    }

    private static void EnsureCompatible(ProfileBackupManifest manifest)
    {
        if (!Version.TryParse(manifest.AeroVersion, out Version? backupVersion) ||
            !Version.TryParse(CurrentAeroVersion, out Version? currentVersion))
        {
            throw new InvalidDataException("The backup version cannot be read.");
        }

        if (backupVersion.Major > currentVersion.Major ||
            backupVersion.Minor > currentVersion.Minor)
        {
            throw new InvalidDataException(
                $"Backup version {manifest.AeroVersion} is newer than Aero {CurrentAeroVersion}.");
        }
    }

    private static bool ContainsCorruptManagedJson(string storageRoot)
    {
        foreach (string file in ManagedFiles.Where(file => file.EndsWith(".json", StringComparison.Ordinal)))
        {
            string path = Path.Combine(storageRoot, file);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using JsonDocument _ = JsonDocument.Parse(ReadSharedBytes(path));
            }
            catch
            {
                return true;
            }
        }

        return false;
    }

    private static int ReadSafeLength(BinaryReader reader, int maximum = int.MaxValue)
    {
        int length = reader.ReadInt32();
        long remaining = reader.BaseStream.Length - reader.BaseStream.Position;
        if (length < 0 || length > maximum || length > remaining)
        {
            throw new InvalidDataException("The backup envelope contains an invalid length.");
        }

        return length;
    }

    private static void TrimAutomaticBackups(string storageRoot, int retentionCount)
    {
        foreach (ProfileBackupInfo backup in ListAutomaticBackups(storageRoot)
                     .Skip(Math.Clamp(retentionCount, 2, 20)))
        {
            try
            {
                File.Delete(backup.FullPath);
            }
            catch
            {
                // A locked old backup can be removed during the next rotation.
            }
        }
    }

    private static byte[] ReadSharedBytes(string path)
    {
        using FileStream input = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using MemoryStream memory = new();
        input.CopyTo(memory);
        return memory.ToArray();
    }

    private static string ComputeSha256(string path)
    {
        using FileStream input = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using SHA256 sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(input));
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }

    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    private static void ThrowIfForbiddenBackupPath(string relativePath)
    {
        string normalized = NormalizeArchivePath(relativePath);
        string padded = "/" + normalized.Trim('/') + "/";
        bool forbidden =
            normalized.Equals("vault.bin", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/vault.bin", StringComparison.OrdinalIgnoreCase) ||
            padded.Contains("/Security/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/Network/Cookies", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/Secure Preferences", StringComparison.OrdinalIgnoreCase) ||
            padded.Contains("/Local Storage/", StringComparison.OrdinalIgnoreCase) ||
            padded.Contains("/IndexedDB/", StringComparison.OrdinalIgnoreCase) ||
            padded.Contains("/Service Worker/", StringComparison.OrdinalIgnoreCase) ||
            padded.Contains("/CacheStorage/", StringComparison.OrdinalIgnoreCase) ||
            padded.Contains("/Session Storage/", StringComparison.OrdinalIgnoreCase) ||
            padded.Contains("/Sessions/", StringComparison.OrdinalIgnoreCase) ||
            padded.Contains("/MediaCookies/", StringComparison.OrdinalIgnoreCase);
        if (forbidden)
        {
            throw new InvalidDataException(
                $"The backup contains forbidden credential or session data: {normalized}.");
        }
    }

    private static string ResolveInside(string root, string relativePath)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string destination = Path.GetFullPath(Path.Combine(rootFull, relativePath));
        if (!destination.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The backup contains an unsafe file path.");
        }

        return destination;
    }

    private static string NormalizeArchivePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .TrimStart('/');

    private sealed record SourceFile(string FullPath, string RelativePath);

    private sealed record BackupPayload(
        ProfileBackupManifest Manifest,
        byte[] ZipBytes);
}
