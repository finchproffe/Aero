using System.Globalization;
using System.Text.Json;

namespace Aero.App.Data;

public sealed record BrowserProfile(
    string Id,
    string Name,
    string Icon,
    string StorageRoot,
    string WebViewDataFolder,
    bool IsDefault = false,
    bool IsGuest = false);

public static class BrowserProfileManager
{
    public const string DefaultProfileId = "default";
    public const string AvatarFileName = "avatar.png";
    public static IReadOnlyList<string> AvailableIcons { get; } =
    [
        "●", "🙂", "😎", "🧭", "🌊", "🌙", "☀️", "⭐",
        "🎨", "💻", "🎮", "📚", "🚀", "🦊", "🐼", "🐳"
    ];

    private static readonly object Sync = new();
    private static readonly string AeroRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Aero");
    private static readonly string ProfilesRoot = Path.Combine(AeroRoot, "Profiles");
    private static readonly string RegistryPath = Path.Combine(AeroRoot, "profiles.json");

    public static BrowserProfile LoadActiveProfile()
    {
        lock (Sync)
        {
            ProfileRegistry registry = LoadRegistryLocked();
            BrowserProfile profile = BuildProfiles(registry)
                .FirstOrDefault(item =>
                    item.Id.Equals(registry.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
                ?? BuildDefaultProfile();
            EnsureProfileFolders(profile);
            return profile;
        }
    }

    public static IReadOnlyList<BrowserProfile> ListProfiles()
    {
        lock (Sync)
        {
            ProfileRegistry registry = LoadRegistryLocked();
            return BuildProfiles(registry);
        }
    }

    public static BrowserProfile? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        lock (Sync)
        {
            return BuildProfiles(LoadRegistryLocked()).FirstOrDefault(profile =>
                profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static BrowserProfile Create(string? name, string? icon)
    {
        lock (Sync)
        {
            ProfileRegistry registry = LoadRegistryLocked();
            string id = Guid.NewGuid().ToString("N");
            ProfileMetadata metadata = new()
            {
                Id = id,
                Name = NormalizeName(name, registry.Profiles.Count + 2),
                Icon = NormalizeIcon(icon)
            };
            registry.Profiles.Add(metadata);
            SaveRegistryLocked(registry);

            BrowserProfile profile = BuildProfile(metadata);
            EnsureProfileFolders(profile);
            return profile;
        }
    }

    /// <summary>The custom photo that replaces the emoji icon, if the profile has one.</summary>
    public static string? GetAvatarPath(BrowserProfile profile)
    {
        string path = Path.Combine(profile.StorageRoot, AvatarFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Stores an already re-encoded PNG as the profile photo; null removes it.</summary>
    public static void SetAvatar(BrowserProfile profile, byte[]? png)
    {
        string path = Path.Combine(profile.StorageRoot, AvatarFileName);
        if (png is null)
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(profile.StorageRoot);
        string temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, png);
        File.Move(temporaryPath, path, overwrite: true);
    }

    public static BrowserProfile? Update(string id, string? name, string? icon)
    {
        lock (Sync)
        {
            ProfileRegistry registry = LoadRegistryLocked();
            if (id.Equals(DefaultProfileId, StringComparison.OrdinalIgnoreCase))
            {
                registry.DefaultName = NormalizeName(name, 1);
                registry.DefaultIcon = NormalizeIcon(icon);
                SaveRegistryLocked(registry);
                return BuildDefaultProfile(registry.DefaultName, registry.DefaultIcon);
            }

            ProfileMetadata? metadata = registry.Profiles.FirstOrDefault(profile =>
                profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (metadata is null)
            {
                return null;
            }

            metadata.Name = NormalizeName(name, 2);
            metadata.Icon = NormalizeIcon(icon);
            SaveRegistryLocked(registry);
            return BuildProfile(metadata);
        }
    }

    public static bool SetActive(string id)
    {
        lock (Sync)
        {
            ProfileRegistry registry = LoadRegistryLocked();
            bool exists = id.Equals(DefaultProfileId, StringComparison.OrdinalIgnoreCase) ||
                registry.Profiles.Any(profile =>
                    profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                return false;
            }

            registry.ActiveProfileId = id;
            SaveRegistryLocked(registry);
            return true;
        }
    }

    public static bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            id.Equals(DefaultProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (Sync)
        {
            ProfileRegistry registry = LoadRegistryLocked();
            ProfileMetadata? metadata = registry.Profiles.FirstOrDefault(profile =>
                profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (metadata is null)
            {
                return false;
            }

            registry.Profiles.Remove(metadata);
            if (registry.ActiveProfileId.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                registry.ActiveProfileId = DefaultProfileId;
            }
            SaveRegistryLocked(registry);

            string profileRoot = Path.Combine(ProfilesRoot, metadata.Id);
            try
            {
                if (Directory.Exists(profileRoot))
                {
                    Directory.Delete(profileRoot, recursive: true);
                }
            }
            catch
            {
                _ = DeleteFolderWithRetriesAsync(profileRoot);
            }

            return true;
        }
    }

    public static BrowserProfile CreateGuest()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "Aero",
            "Guest",
            Guid.NewGuid().ToString("N"));
        BrowserProfile profile = new(
            $"guest-{Guid.NewGuid():N}",
            "Guest",
            "👤",
            root,
            Path.Combine(root, "WebView2"),
            IsGuest: true);
        EnsureProfileFolders(profile);
        return profile;
    }

    public static void CleanupGuest(BrowserProfile profile)
    {
        if (!profile.IsGuest)
        {
            return;
        }

        try
        {
            if (Directory.Exists(profile.StorageRoot))
            {
                Directory.Delete(profile.StorageRoot, recursive: true);
            }
        }
        catch
        {
            _ = DeleteFolderWithRetriesAsync(profile.StorageRoot);
        }
    }

    private static async Task DeleteFolderWithRetriesAsync(string path)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            await Task.Delay(500 + attempt * 350);
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                return;
            }
            catch
            {
                // WebView2 releases its final profile files asynchronously.
            }
        }
    }

    private static List<BrowserProfile> BuildProfiles(ProfileRegistry registry)
    {
        List<BrowserProfile> profiles =
        [
            BuildDefaultProfile(registry.DefaultName, registry.DefaultIcon)
        ];
        profiles.AddRange(registry.Profiles.Select(BuildProfile));
        return profiles;
    }

    private static BrowserProfile BuildDefaultProfile(
        string? name = null,
        string? icon = null) => new(
            DefaultProfileId,
            string.IsNullOrWhiteSpace(name) ? "Default" : name,
            NormalizeIcon(icon),
            AeroRoot,
            Path.Combine(AeroRoot, "UserData"),
            IsDefault: true);

    private static BrowserProfile BuildProfile(ProfileMetadata metadata)
    {
        string root = Path.Combine(ProfilesRoot, metadata.Id);
        return new BrowserProfile(
            metadata.Id,
            metadata.Name,
            NormalizeIcon(metadata.Icon),
            root,
            Path.Combine(root, "WebView2"));
    }

    private static void EnsureProfileFolders(BrowserProfile profile)
    {
        Directory.CreateDirectory(profile.StorageRoot);
        Directory.CreateDirectory(profile.WebViewDataFolder);
    }

    private static ProfileRegistry LoadRegistryLocked()
    {
        try
        {
            if (File.Exists(RegistryPath))
            {
                ProfileRegistry? registry =
                    JsonSerializer.Deserialize<ProfileRegistry>(File.ReadAllText(RegistryPath));
                if (registry is not null)
                {
                    registry.ActiveProfileId =
                        string.IsNullOrWhiteSpace(registry.ActiveProfileId)
                            ? DefaultProfileId
                            : registry.ActiveProfileId;
                    registry.DefaultName =
                        string.IsNullOrWhiteSpace(registry.DefaultName)
                            ? "Default"
                            : registry.DefaultName.Trim();
                    registry.DefaultIcon = NormalizeIcon(registry.DefaultIcon);
                    registry.Profiles ??= [];
                    registry.Profiles = registry.Profiles
                        .Where(profile =>
                            !string.IsNullOrWhiteSpace(profile.Id) &&
                            profile.Id.All(character =>
                                char.IsLetterOrDigit(character) || character is '-' or '_'))
                        .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First())
                        .ToList();
                    return registry;
                }
            }
        }
        catch
        {
            // A broken registry falls back to the legacy Default profile.
        }

        return new ProfileRegistry();
    }

    private static void SaveRegistryLocked(ProfileRegistry registry)
    {
        Directory.CreateDirectory(AeroRoot);
        string temporaryPath = RegistryPath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(registry, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, RegistryPath, overwrite: true);
    }

    private static string NormalizeName(string? name, int fallbackNumber)
    {
        string resolved = string.IsNullOrWhiteSpace(name)
            ? $"Profile {fallbackNumber}"
            : name.Trim();
        return resolved.Length <= 80 ? resolved : resolved[..80];
    }

    private static string NormalizeIcon(string? icon)
    {
        string resolved = string.IsNullOrWhiteSpace(icon) ? "●" : icon.Trim();
        return StringInfo.GetNextTextElement(resolved);
    }

    private sealed class ProfileRegistry
    {
        public string ActiveProfileId { get; set; } = DefaultProfileId;

        public string DefaultName { get; set; } = "Default";

        public string DefaultIcon { get; set; } = "●";

        public List<ProfileMetadata> Profiles { get; set; } = [];
    }

    private sealed class ProfileMetadata
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "Profile";

        public string Icon { get; set; } = "●";
    }
}
