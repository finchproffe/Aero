using System.Text.Json;

namespace Aero.App.Data;

internal enum SitePermissionKind
{
    Camera,
    Microphone,
    Geolocation,
    Notifications,
    Popups,
    Redirects,
    Clipboard,
    Midi,
    AutomaticDownloads
}

internal enum SitePermissionDecision
{
    Ask,
    Allow,
    Block
}

internal sealed class SitePermissionStore
{
    private const string FileName = "site-permissions.json";

    private readonly object _sync = new();
    private readonly string? _filePath;
    private Dictionary<string, SitePermissionEntry> _entries;

    private SitePermissionStore(string? filePath, Dictionary<string, SitePermissionEntry> entries)
    {
        _filePath = filePath;
        _entries = entries;
    }

    public static SitePermissionStore Load(
        bool persistToDisk,
        string? storageRoot = null)
    {
        if (!persistToDisk)
        {
            return new SitePermissionStore(null, new(StringComparer.OrdinalIgnoreCase));
        }

        string root = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Aero");
        string filePath = Path.Combine(root, FileName);

        try
        {
            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath);
                Dictionary<string, SitePermissionEntry>? entries = JsonSerializer.Deserialize<Dictionary<string, SitePermissionEntry>>(json);
                if (entries is not null)
                {
                    return new SitePermissionStore(
                        filePath,
                        new Dictionary<string, SitePermissionEntry>(entries, StringComparer.OrdinalIgnoreCase));
                }
            }
        }
        catch
        {
            // A corrupt permission file must not prevent Aero from starting.
        }

        return new SitePermissionStore(filePath, new(StringComparer.OrdinalIgnoreCase));
    }

    public SitePermissionDecision GetDecision(string origin, SitePermissionKind kind)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(origin, out SitePermissionEntry? entry)
                ? entry.Get(kind)
                : SitePermissionDecision.Ask;
        }
    }

    public void SetDecision(string origin, SitePermissionKind kind, SitePermissionDecision decision)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return;
        }

        lock (_sync)
        {
            if (!_entries.TryGetValue(origin, out SitePermissionEntry? entry))
            {
                entry = new SitePermissionEntry();
                _entries[origin] = entry;
            }

            entry.Set(kind, decision);

            if (entry.IsDefault)
            {
                _entries.Remove(origin);
            }

            SaveLocked();
        }
    }

    public void Reset(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return;
        }

        lock (_sync)
        {
            if (_entries.Remove(origin))
            {
                SaveLocked();
            }
        }
    }

    public void ResetAll()
    {
        lock (_sync)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            _entries.Clear();
            SaveLocked();
        }
    }

    public IReadOnlyList<SitePermissionSnapshot> Snapshot()
    {
        lock (_sync)
        {
            return _entries
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new SitePermissionSnapshot(
                    pair.Key,
                    pair.Value.Camera,
                    pair.Value.Microphone,
                    pair.Value.Geolocation,
                    pair.Value.Notifications,
                    pair.Value.Popups,
                    pair.Value.Redirects,
                    pair.Value.Clipboard,
                    pair.Value.Midi,
                    pair.Value.AutomaticDownloads))
                .ToList();
        }
    }

    public void Replace(IEnumerable<SitePermissionSnapshot> snapshots)
    {
        lock (_sync)
        {
            Dictionary<string, SitePermissionEntry> replacement =
                new(StringComparer.OrdinalIgnoreCase);

            foreach (SitePermissionSnapshot snapshot in snapshots)
            {
                string? origin = NormalizeOrigin(snapshot.Origin);
                if (origin is null)
                {
                    continue;
                }

                replacement[origin] = new SitePermissionEntry
                {
                    Camera = snapshot.Camera,
                    Microphone = snapshot.Microphone,
                    Geolocation = snapshot.Geolocation,
                    Notifications = snapshot.Notifications,
                    Popups = snapshot.Popups,
                    Redirects = snapshot.Redirects,
                    Clipboard = snapshot.Clipboard,
                    Midi = snapshot.Midi,
                    AutomaticDownloads = snapshot.AutomaticDownloads
                };
            }

            _entries = replacement;
            SaveLocked();
        }
    }

    public static string? NormalizeOrigin(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private void SaveLocked()
    {
        if (_filePath is null)
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = _filePath + ".tmp";
            string json = JsonSerializer.Serialize(
                _entries,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch
        {
            // Permission choices still remain valid for the current session.
        }
    }

    private sealed class SitePermissionEntry
    {
        public SitePermissionDecision Camera { get; set; }

        public SitePermissionDecision Microphone { get; set; }

        public SitePermissionDecision Geolocation { get; set; }

        public SitePermissionDecision Notifications { get; set; }

        public SitePermissionDecision Popups { get; set; }

        public SitePermissionDecision Redirects { get; set; }

        public SitePermissionDecision Clipboard { get; set; }

        public SitePermissionDecision Midi { get; set; }

        public SitePermissionDecision AutomaticDownloads { get; set; }

        public bool IsDefault =>
            Camera == SitePermissionDecision.Ask &&
            Microphone == SitePermissionDecision.Ask &&
            Geolocation == SitePermissionDecision.Ask &&
            Notifications == SitePermissionDecision.Ask &&
            Popups == SitePermissionDecision.Ask &&
            Redirects == SitePermissionDecision.Ask &&
            Clipboard == SitePermissionDecision.Ask &&
            Midi == SitePermissionDecision.Ask &&
            AutomaticDownloads == SitePermissionDecision.Ask;

        public SitePermissionDecision Get(SitePermissionKind kind) => kind switch
        {
            SitePermissionKind.Camera => Camera,
            SitePermissionKind.Microphone => Microphone,
            SitePermissionKind.Geolocation => Geolocation,
            SitePermissionKind.Notifications => Notifications,
            SitePermissionKind.Popups => Popups,
            SitePermissionKind.Redirects => Redirects,
            SitePermissionKind.Clipboard => Clipboard,
            SitePermissionKind.Midi => Midi,
            SitePermissionKind.AutomaticDownloads => AutomaticDownloads,
            _ => SitePermissionDecision.Ask
        };

        public void Set(SitePermissionKind kind, SitePermissionDecision decision)
        {
            switch (kind)
            {
                case SitePermissionKind.Camera:
                    Camera = decision;
                    break;
                case SitePermissionKind.Microphone:
                    Microphone = decision;
                    break;
                case SitePermissionKind.Geolocation:
                    Geolocation = decision;
                    break;
                case SitePermissionKind.Notifications:
                    Notifications = decision;
                    break;
                case SitePermissionKind.Popups:
                    Popups = decision;
                    break;
                case SitePermissionKind.Redirects:
                    Redirects = decision;
                    break;
                case SitePermissionKind.Clipboard:
                    Clipboard = decision;
                    break;
                case SitePermissionKind.Midi:
                    Midi = decision;
                    break;
                case SitePermissionKind.AutomaticDownloads:
                    AutomaticDownloads = decision;
                    break;
            }
        }
    }
}

internal sealed record SitePermissionSnapshot(
    string Origin,
    SitePermissionDecision Camera,
    SitePermissionDecision Microphone,
    SitePermissionDecision Geolocation,
    SitePermissionDecision Notifications,
    SitePermissionDecision Popups,
    SitePermissionDecision Redirects,
    SitePermissionDecision Clipboard,
    SitePermissionDecision Midi,
    SitePermissionDecision AutomaticDownloads);
