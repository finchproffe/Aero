using Aero.Security.TestLab.Models;

namespace Aero.Security.TestLab.Scanning;

internal sealed class FileExposureScanner
{
    private static readonly HashSet<string> SkippedDirectoryNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Cache",
        "Code Cache",
        "GPUCache",
        "DawnCache",
        "GrShaderCache",
        "ShaderCache",
        "Crashpad",
        "Temp",
        "BrowserMetrics"
    };

    private static readonly HashSet<string> HighRiskNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Login Data",
        "Login Data For Account",
        "Web Data",
        "Cookies",
        "Local State",
        "Secure Preferences",
        "Preferences",
        "vault.bin",
        "vault.key",
        "audit.log"
    };

    private static readonly HashSet<string> InterestingExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".json",
        ".db",
        ".sqlite",
        ".sqlite3",
        ".log",
        ".txt",
        ".csv",
        ".bak",
        ".aerobackup"
    };

    public FileExposureScanner(
        MarkerSet markerSet,
        long maximumFileBytes,
        bool deepScan)
    {
        _markerSet = markerSet;
        _maximumFileBytes = maximumFileBytes;
        _deepScan = deepScan;
    }

    private readonly MarkerSet _markerSet;
    private readonly long _maximumFileBytes;
    private readonly bool _deepScan;

    public int FilesVisited { get; private set; }

    public long BytesRead { get; private set; }

    public int ErrorCount { get; private set; }

    public IReadOnlyList<ScanFinding> ScanRoot(
        string root,
        CancellationToken cancellationToken = default)
    {
        List<ScanFinding> findings = [];
        if (!Directory.Exists(root))
        {
            findings.Add(new ScanFinding(
                FindingSeverity.Warning,
                "files",
                "profile-root-missing",
                root,
                "The requested profile root does not exist."));
            return findings;
        }

        foreach (string path in EnumerateFilesSafely(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_deepScan && !IsInteresting(path, root))
            {
                continue;
            }

            try
            {
                FileInfo info = new(path);
                if (info.Length == 0)
                {
                    continue;
                }

                FilesVisited++;
                using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 1024 * 1024,
                    FileOptions.SequentialScan);

                BinaryScanResult result = BinaryPatternScanner.Scan(
                    stream,
                    _markerSet,
                    Math.Min(_maximumFileBytes, info.Length),
                    cancellationToken);
                BytesRead += result.BytesRead;

                foreach (PatternHit hit in result.Hits)
                {
                    findings.Add(new ScanFinding(
                        DetermineSeverity(path),
                        "files",
                        "plaintext-marker",
                        path,
                        "A test secret marker was found in a profile file.",
                        hit.Encoding,
                        $"0x{hit.Offset:X}"));
                }

                if (result.Truncated)
                {
                    findings.Add(new ScanFinding(
                        FindingSeverity.Info,
                        "files",
                        "file-scan-truncated",
                        path,
                        $"The file exceeded the {_maximumFileBytes / (1024 * 1024)} MB per-file scan limit."));
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                ErrorCount++;
                findings.Add(new ScanFinding(
                    FindingSeverity.Warning,
                    "files",
                    "file-unreadable",
                    path,
                    $"The file could not be scanned: {exception.GetType().Name}."));
            }
        }

        return findings;
    }

    private IEnumerable<string> EnumerateFilesSafely(string root)
    {
        Stack<string> pending = new();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                ErrorCount++;
                continue;
            }

            foreach (string file in files)
            {
                yield return file;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                ErrorCount++;
                continue;
            }

            foreach (string child in children)
            {
                if (!_deepScan && SkippedDirectoryNames.Contains(Path.GetFileName(child)))
                {
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    ErrorCount++;
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    private static bool IsInteresting(string path, string root)
    {
        string fileName = Path.GetFileName(path);
        if (HighRiskNames.Contains(fileName) ||
            InterestingExtensions.Contains(Path.GetExtension(path)))
        {
            return true;
        }

        string relative = Path.GetRelativePath(root, path);
        string[] parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part =>
            part.Equals("Security", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("Network", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("Local Storage", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("IndexedDB", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("Backups", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("RestorePending", StringComparison.OrdinalIgnoreCase));
    }

    private static FindingSeverity DetermineSeverity(string path)
    {
        string fileName = Path.GetFileName(path);
        if (fileName.Equals("Login Data", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("Cookies", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("vault.bin", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".aerobackup", StringComparison.OrdinalIgnoreCase))
        {
            return FindingSeverity.Critical;
        }

        return FindingSeverity.High;
    }
}
