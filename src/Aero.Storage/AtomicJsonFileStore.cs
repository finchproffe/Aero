using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Aero.App.Data;

/// <summary>
/// Small JSON persistence primitive used by Aero.Storage. Writes occur to a
/// sibling temporary file and are committed atomically. The previous valid
/// destination is retained as a recovery backup.
/// </summary>
internal sealed class AtomicJsonFileStore
{
    private readonly string? _dataFolder;
    private readonly JsonSerializerOptions _jsonOptions;

    public AtomicJsonFileStore(string? dataFolder)
    {
        _dataFolder = dataFolder;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };
    }

    public T Read<T>(string path, T fallback)
    {
        string temporaryPath = GetTemporaryPath(path);
        if (TryRead(path, out T? value))
        {
            // A valid primary file always wins. A leftover .tmp can only be from
            // an interrupted older commit and should not be reconsidered later.
            TryDelete(temporaryPath);
            return value;
        }

        string backupPath = GetBackupPath(path);
        string[] recoveryCandidates = [temporaryPath, backupPath];
        Array.Sort(recoveryCandidates, static (left, right) =>
            GetLastWriteTimeUtc(right).CompareTo(GetLastWriteTimeUtc(left)));

        foreach (string recoveryPath in recoveryCandidates)
        {
            if (!TryRead(recoveryPath, out value))
            {
                continue;
            }

            TryRestoreRecoveryFile(recoveryPath, path);
            if (recoveryPath.Equals(temporaryPath, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(temporaryPath);
            }

            return value;
        }

        return fallback;
    }

    public async Task WriteAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        EnsureDataFolder();
        string temporaryPath = GetTemporaryPath(path);

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    _jsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Commit(temporaryPath, path);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public void Write<T>(string path, T value)
    {
        EnsureDataFolder();
        string temporaryPath = GetTemporaryPath(path);

        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan))
            {
                JsonSerializer.Serialize(stream, value, _jsonOptions);
                stream.Flush(flushToDisk: true);
            }

            Commit(temporaryPath, path);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private bool TryRead<T>(string path, [NotNullWhen(true)] out T? value)
    {
        value = default;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            value = JsonSerializer.Deserialize<T>(stream, _jsonOptions);
            return value is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void Commit(string temporaryPath, string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            File.Move(temporaryPath, destinationPath, overwrite: true);
            return;
        }

        string backupPath = GetBackupPath(destinationPath);
        try
        {
            File.Replace(
                temporaryPath,
                destinationPath,
                backupPath,
                ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            FallbackCommit(temporaryPath, destinationPath, backupPath);
        }
        catch (IOException)
        {
            FallbackCommit(temporaryPath, destinationPath, backupPath);
        }
    }

    private static void FallbackCommit(
        string temporaryPath,
        string destinationPath,
        string backupPath)
    {
        File.Copy(destinationPath, backupPath, overwrite: true);
        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private void EnsureDataFolder()
    {
        if (!string.IsNullOrWhiteSpace(_dataFolder))
        {
            Directory.CreateDirectory(_dataFolder);
        }
    }

    private static string GetTemporaryPath(string path) => path + ".tmp";

    private static string GetBackupPath(string path) => path + ".bak";

    private static DateTime GetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static void TryRestoreRecoveryFile(string sourcePath, string destinationPath)
    {
        try
        {
            string directory = Path.GetDirectoryName(destinationPath) ?? string.Empty;
            if (directory.Length > 0)
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
        catch
        {
            // Recovery is best effort. The valid in-memory value is still returned.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A stale temporary file is harmless and can be considered on next load.
        }
    }
}
