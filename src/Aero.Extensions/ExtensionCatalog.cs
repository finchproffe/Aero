using System.Text.Json;

namespace Aero.Extensions;

public static class ExtensionCatalog
{
    public static ExtensionPackage? ReadPackage(string folderPath, string? idOverride = null)
    {
        try
        {
            string manifestPath = System.IO.Path.Combine(folderPath, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement root = document.RootElement;

            string id = string.IsNullOrWhiteSpace(idOverride)
                ? new DirectoryInfo(folderPath).Parent?.Name ?? new DirectoryInfo(folderPath).Name
                : idOverride;
            string name = GetString(root, "name") ?? id;
            string description = GetString(root, "description") ?? string.Empty;
            string version = GetString(root, "version") ?? "Unknown";

            name = ResolveLocalizedValue(folderPath, name);
            description = ResolveLocalizedValue(folderPath, description);

            string? actionPage = null;
            if (root.TryGetProperty("action", out JsonElement action))
            {
                actionPage = GetString(action, "default_popup");
            }
            else if (root.TryGetProperty("browser_action", out JsonElement browserAction))
            {
                actionPage = GetString(browserAction, "default_popup");
            }

            string? optionsPage = GetString(root, "options_page");
            if (root.TryGetProperty("options_ui", out JsonElement optionsUi))
            {
                optionsPage ??= GetString(optionsUi, "page");
            }

            List<string> permissions = [];
            ReadStringArray(root, "permissions", permissions);
            ReadStringArray(root, "host_permissions", permissions);
            string? iconPath = FindIconPath(root, folderPath);

            return new ExtensionPackage(
                id,
                string.IsNullOrWhiteSpace(name) ? id : name,
                description,
                version,
                folderPath,
                actionPage,
                optionsPage,
                permissions
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                iconPath);
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<ExtensionPackage> ScanChromeProfiles() =>
        ScanChromiumProfiles(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google",
            "Chrome",
            "User Data"));

    public static IReadOnlyList<ExtensionPackage> ScanEdgeProfiles() =>
        ScanChromiumProfiles(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "Edge",
            "User Data"));

    public static IReadOnlyList<ExtensionPackage> ScanChromiumProfiles(string browserRoot)
    {
        if (!Directory.Exists(browserRoot))
        {
            return [];
        }

        Dictionary<string, ExtensionPackage> packages = new(StringComparer.OrdinalIgnoreCase);

        foreach (string profilePath in EnumerateDirectoriesSafely(browserRoot))
        {
            string profileName = System.IO.Path.GetFileName(profilePath);
            if (!profileName.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                !profileName.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string extensionsPath = System.IO.Path.Combine(profilePath, "Extensions");
            if (!Directory.Exists(extensionsPath))
            {
                continue;
            }

            foreach (string extensionIdPath in EnumerateDirectoriesSafely(extensionsPath))
            {
                string extensionId = System.IO.Path.GetFileName(extensionIdPath);
                string? latestVersionPath = EnumerateDirectoriesSafely(extensionIdPath)
                    .Where(path => File.Exists(System.IO.Path.Combine(path, "manifest.json")))
                    .OrderByDescending(GetLastWriteTimeUtcSafely)
                    .FirstOrDefault();

                if (latestVersionPath is null)
                {
                    continue;
                }

                ExtensionPackage? package = ReadPackage(latestVersionPath, extensionId);
                if (package is not null)
                {
                    packages[extensionId] = package;
                }
            }
        }

        return packages.Values
            .OrderBy(package => package.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> EnumerateDirectoriesSafely(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch
        {
            return [];
        }
    }

    private static DateTime GetLastWriteTimeUtcSafely(string path)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string? FindIconPath(JsonElement root, string extensionFolder)
    {
        List<(int Size, string Path)> candidates = [];
        AddIconCandidates(root, "icons", candidates);

        if (root.TryGetProperty("action", out JsonElement action))
        {
            AddIconCandidates(action, "default_icon", candidates);
        }

        if (root.TryGetProperty("browser_action", out JsonElement browserAction))
        {
            AddIconCandidates(browserAction, "default_icon", candidates);
        }

        if (root.TryGetProperty("page_action", out JsonElement pageAction))
        {
            AddIconCandidates(pageAction, "default_icon", candidates);
        }

        string rootPath = System.IO.Path.GetFullPath(extensionFolder).TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar);
        string rootPrefix = rootPath + System.IO.Path.DirectorySeparatorChar;

        foreach ((int _, string relativePath) in candidates
                     .OrderByDescending(candidate => candidate.Size))
        {
            try
            {
                string normalized = relativePath
                    .Trim()
                    .TrimStart('/', '\\')
                    .Replace('/', System.IO.Path.DirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                string fullPath = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(rootPath, normalized));
                if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(fullPath) ||
                    !IsSupportedIconFile(fullPath))
                {
                    continue;
                }

                return fullPath;
            }
            catch
            {
                // Ignore malformed or escaping manifest paths.
            }
        }

        return null;
    }

    private static void AddIconCandidates(
        JsonElement parent,
        string propertyName,
        List<(int Size, string Path)> destination)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement icons))
        {
            return;
        }

        if (icons.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(icons.GetString()))
        {
            destination.Add((0, icons.GetString()!));
            return;
        }

        if (icons.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty icon in icons.EnumerateObject())
        {
            if (icon.Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(icon.Value.GetString()))
            {
                continue;
            }

            int size = int.TryParse(icon.Name, out int parsedSize)
                ? parsedSize
                : 0;
            destination.Add((size, icon.Value.GetString()!));
        }
    }

    private static bool IsSupportedIconFile(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".ico" or ".bmp" or ".webp";

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void ReadStringArray(
        JsonElement root,
        string propertyName,
        List<string> destination)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                destination.Add(value.GetString()!.Trim());
            }
        }
    }

    private static string ResolveLocalizedValue(string extensionFolder, string value)
    {
        if (!value.StartsWith("__MSG_", StringComparison.OrdinalIgnoreCase) ||
            !value.EndsWith("__", StringComparison.Ordinal))
        {
            return value;
        }

        string key = value[6..^2];
        string localesPath = System.IO.Path.Combine(extensionFolder, "_locales");
        if (!Directory.Exists(localesPath))
        {
            return value;
        }

        IEnumerable<string> localeDirectories = EnumerateDirectoriesSafely(localesPath)
            .OrderByDescending(path =>
            {
                string locale = System.IO.Path.GetFileName(path);
                return locale.Equals("en", StringComparison.OrdinalIgnoreCase) ||
                       locale.Equals("en_US", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0;
            });

        foreach (string localeDirectory in localeDirectories)
        {
            string messagesPath = System.IO.Path.Combine(localeDirectory, "messages.json");
            if (!File.Exists(messagesPath))
            {
                continue;
            }

            try
            {
                using JsonDocument messages = JsonDocument.Parse(File.ReadAllText(messagesPath));
                if (messages.RootElement.TryGetProperty(key, out JsonElement entry) &&
                    entry.TryGetProperty("message", out JsonElement message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? value;
                }
            }
            catch
            {
                // Try another locale.
            }
        }

        return value;
    }
}
