namespace Aero.Downloads;

public static class DownloadFingerprint
{
    public static string NormalizeUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return value?.Trim().ToLowerInvariant() ?? string.Empty;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return uri.AbsoluteUri.Trim().ToLowerInvariant();
        }

        UriBuilder normalized = new(uri) { Fragment = string.Empty };
        return normalized.Uri.AbsoluteUri.TrimEnd('/').ToLowerInvariant();
    }

    public static string NormalizeFileName(string? path)
    {
        string fileName = Path.GetFileName(path ?? string.Empty).Trim().ToLowerInvariant();
        string extension = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        int suffixStart = stem.LastIndexOf(" (", StringComparison.Ordinal);
        if (suffixStart >= 0 && stem.EndsWith(')'))
        {
            string suffix = stem[(suffixStart + 2)..^1];
            if (int.TryParse(suffix, out _))
            {
                stem = stem[..suffixStart];
            }
        }

        return stem + extension;
    }
}
