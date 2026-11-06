namespace Aero.Downloads;

public static class DownloadPathPolicy
{
    private const int MaximumFileNameLength = 180;

    private static readonly HashSet<string> DangerousExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".bat", ".cmd", ".com", ".scr", ".pif",
        ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta",
        ".jar", ".reg", ".lnk", ".cpl", ".msc", ".application", ".appref-ms"
    };

    private static readonly HashSet<string> ReservedDeviceNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static bool IsPotentiallyDangerous(string? suggestedPath, string? sourceUri)
    {
        string safeFileName = ResolveSafeFileName(suggestedPath, sourceUri);
        string extension = Path.GetExtension(safeFileName);
        return !string.IsNullOrWhiteSpace(extension) && DangerousExtensions.Contains(extension);
    }

    public static string CreateAutomaticPath(
        string destinationFolder,
        string? suggestedPath,
        string? sourceUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
        Directory.CreateDirectory(destinationFolder);

        string fileName = ResolveSafeFileName(suggestedPath, sourceUri);
        string candidate = Path.Combine(destinationFolder, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int suffix = 1; suffix <= 10_000; suffix++)
        {
            candidate = Path.Combine(destinationFolder, $"{stem} ({suffix}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(
            destinationFolder,
            $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    public static string ResolveSafeFileName(string? suggestedPath, string? sourceUri)
    {
        string fileName = Path.GetFileName(suggestedPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fileName) &&
            Uri.TryCreate(sourceUri, UriKind.Absolute, out Uri? uri))
        {
            fileName = Path.GetFileName(uri.LocalPath);
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "download";
        }

        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        char[] buffer = new char[fileName.Length];
        int written = 0;
        foreach (char character in fileName)
        {
            buffer[written++] = invalidCharacters.Contains(character) || character < ' '
                ? '_'
                : character;
        }

        fileName = new string(buffer, 0, written).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..")
        {
            fileName = "download";
        }

        string extension = Path.GetExtension(fileName);
        string stem;
        if (extension.Length > 32)
        {
            extension = string.Empty;
            stem = fileName;
        }
        else
        {
            stem = Path.GetFileNameWithoutExtension(fileName);
        }

        if (ReservedDeviceNames.Contains(stem))
        {
            stem = "_" + stem;
        }

        int maximumStemLength = Math.Max(1, MaximumFileNameLength - extension.Length);
        if (stem.Length > maximumStemLength)
        {
            stem = stem[..maximumStemLength];
        }

        return stem + extension;
    }
}
