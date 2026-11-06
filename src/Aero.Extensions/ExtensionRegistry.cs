using System.Text;
using System.Text.Json;

namespace Aero.Extensions;

public sealed class ExtensionRegistry
{
    private readonly object _sync = new();
    private string? _registryFile;

    public Dictionary<string, string> ManagedPaths { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> PinnedExtensionIds { get; set; } = [];

    public Dictionary<string, bool> UserScriptsEnabled { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, bool> AllSitesAccessEnabled { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public static ExtensionRegistry Load(string? storageRoot = null)
    {
        string root = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Aero");
        string registryFile = Path.Combine(root, "extensions.json");

        ExtensionRegistry registry = TryRead(registryFile) ??
            TryRead(registryFile + ".bak") ??
            new ExtensionRegistry();

        registry._registryFile = registryFile;
        registry.Normalize();
        return registry;
    }

    public void SetPath(string extensionId, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        lock (_sync)
        {
            ManagedPaths[extensionId] = path;
            SaveLocked();
        }
    }

    public string? GetPath(string extensionId)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            return null;
        }

        lock (_sync)
        {
            return ManagedPaths.TryGetValue(extensionId, out string? path) ? path : null;
        }
    }

    public bool IsPinned(string extensionId)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            return false;
        }

        lock (_sync)
        {
            return PinnedExtensionIds.Any(id =>
                id.Equals(extensionId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void SetPinned(string extensionId, bool pinned)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);

        lock (_sync)
        {
            PinnedExtensionIds.RemoveAll(id =>
                id.Equals(extensionId, StringComparison.OrdinalIgnoreCase));

            if (pinned)
            {
                PinnedExtensionIds.Add(extensionId);
            }

            SaveLocked();
        }
    }


    public bool IsUserScriptsEnabled(string extensionId)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            return false;
        }

        lock (_sync)
        {
            return UserScriptsEnabled.TryGetValue(extensionId, out bool enabled) && enabled;
        }
    }

    public void SetUserScriptsEnabled(string extensionId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        lock (_sync)
        {
            UserScriptsEnabled[extensionId] = enabled;
            SaveLocked();
        }
    }

    public bool IsAllSitesAccessEnabled(string extensionId)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            return true;
        }

        lock (_sync)
        {
            return !AllSitesAccessEnabled.TryGetValue(extensionId, out bool enabled) || enabled;
        }
    }

    public void SetAllSitesAccessEnabled(string extensionId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        lock (_sync)
        {
            AllSitesAccessEnabled[extensionId] = enabled;
            SaveLocked();
        }
    }

    public string? Remove(string extensionId)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            return null;
        }

        lock (_sync)
        {
            string? path = ManagedPaths.TryGetValue(extensionId, out string? value)
                ? value
                : null;
            ManagedPaths.Remove(extensionId);
            PinnedExtensionIds.RemoveAll(id =>
                id.Equals(extensionId, StringComparison.OrdinalIgnoreCase));
            UserScriptsEnabled.Remove(extensionId);
            AllSitesAccessEnabled.Remove(extensionId);
            SaveLocked();
            return path;
        }
    }

    public IReadOnlyDictionary<string, string> SnapshotManagedPaths()
    {
        lock (_sync)
        {
            return new Dictionary<string, string>(ManagedPaths, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static ExtensionRegistry? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<ExtensionRegistry>(json);
        }
        catch
        {
            return null;
        }
    }

    private void Normalize()
    {
        ManagedPaths = new Dictionary<string, string>(
            ManagedPaths ?? [],
            StringComparer.OrdinalIgnoreCase);
        PinnedExtensionIds ??= [];
        PinnedExtensionIds = PinnedExtensionIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        UserScriptsEnabled = new Dictionary<string, bool>(
            UserScriptsEnabled ?? [],
            StringComparer.OrdinalIgnoreCase);
        AllSitesAccessEnabled = new Dictionary<string, bool>(
            AllSitesAccessEnabled ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    private void SaveLocked()
    {
        if (string.IsNullOrWhiteSpace(_registryFile))
        {
            return;
        }

        string? directory = Path.GetDirectoryName(_registryFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        ExtensionRegistrySnapshot snapshot = new(
            new Dictionary<string, string>(ManagedPaths, StringComparer.OrdinalIgnoreCase),
            [.. PinnedExtensionIds],
            new Dictionary<string, bool>(UserScriptsEnabled, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, bool>(AllSitesAccessEnabled, StringComparer.OrdinalIgnoreCase));
        string json = JsonSerializer.Serialize(
            snapshot,
            new JsonSerializerOptions { WriteIndented = true });
        string temporaryFile = _registryFile + ".tmp";
        string backupFile = _registryFile + ".bak";

        File.WriteAllText(temporaryFile, json, new UTF8Encoding(false));
        if (File.Exists(_registryFile))
        {
            try
            {
                File.Replace(temporaryFile, _registryFile, backupFile, ignoreMetadataErrors: true);
                return;
            }
            catch (PlatformNotSupportedException)
            {
                // Fall through to the portable replace path.
            }
            catch (IOException)
            {
                // A filesystem without Replace support uses the portable path.
            }
        }

        if (File.Exists(_registryFile))
        {
            File.Copy(_registryFile, backupFile, overwrite: true);
        }
        File.Move(temporaryFile, _registryFile, overwrite: true);
    }

    private sealed record ExtensionRegistrySnapshot(
        Dictionary<string, string> ManagedPaths,
        List<string> PinnedExtensionIds,
        Dictionary<string, bool> UserScriptsEnabled,
        Dictionary<string, bool> AllSitesAccessEnabled);
}
