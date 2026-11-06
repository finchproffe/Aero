using Aero.App.Settings;
using Aero.Search;
using Aero.Extensions;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aero.App.Data;

internal enum ChromiumBrowserKind
{
    Chrome,
    Edge
}

internal sealed record ChromiumImportOptions(
    bool Bookmarks,
    bool History,
    bool OpenTabs,
    bool SearchEngine,
    bool HomePage,
    bool Settings,
    bool Extensions);

internal sealed record ChromiumImportResult(
    int BookmarkCount,
    int HistoryCount,
    IReadOnlyList<string> OpenTabs,
    IReadOnlyList<ExtensionPackage> Extensions,
    bool SearchEngineImported,
    bool HomePageImported,
    bool SettingsImported)
{
    public string BuildSummary(int importedExtensionCount)
    {
        List<string> parts = [];
        if (BookmarkCount > 0) parts.Add($"{BookmarkCount} bookmarks");
        if (HistoryCount > 0) parts.Add($"{HistoryCount} history entries");
        if (OpenTabs.Count > 0) parts.Add($"{OpenTabs.Count} tabs");
        if (SearchEngineImported) parts.Add("search engine");
        if (HomePageImported) parts.Add("home page");
        if (SettingsImported) parts.Add("settings");
        if (importedExtensionCount > 0) parts.Add($"{importedExtensionCount} extensions");
        return parts.Count == 0
            ? "No compatible data was found in the selected browser profiles."
            : "Imported " + string.Join(", ", parts) + ".";
    }
}

internal static partial class ChromiumImportService
{
    private static readonly Regex SessionUrlRegex = SessionUrlPattern();
    private static readonly DateTimeOffset ChromiumEpoch =
        new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static string GetBrowserRoot(ChromiumBrowserKind browser) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            browser == ChromiumBrowserKind.Edge ? "Microsoft" : "Google",
            browser == ChromiumBrowserKind.Edge ? "Edge" : "Chrome",
            "User Data");

    public static Task<ChromiumImportResult> ImportAsync(
        ChromiumBrowserKind browser,
        ChromiumImportOptions options,
        BrowserDataStore dataStore,
        BrowserSettings settings,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Import(browser, options, dataStore, settings, cancellationToken), cancellationToken);

    private static ChromiumImportResult Import(
        ChromiumBrowserKind browser,
        ChromiumImportOptions options,
        BrowserDataStore dataStore,
        BrowserSettings settings,
        CancellationToken cancellationToken)
    {
        string browserRoot = GetBrowserRoot(browser);
        if (!Directory.Exists(browserRoot))
        {
            throw new DirectoryNotFoundException(
                $"{browser} profile data was not found at {browserRoot}.");
        }

        List<string> profileFolders = EnumerateProfiles(browserRoot).ToList();
        if (profileFolders.Count == 0)
        {
            throw new InvalidDataException($"{browser} does not contain a Default or Profile folder.");
        }

        List<BookmarkEntry> bookmarks = [];
        List<HistoryEntry> history = [];
        HashSet<string> tabs = new(StringComparer.OrdinalIgnoreCase);
        bool searchImported = false;
        bool homeImported = false;
        bool settingsImported = false;

        foreach (string profileFolder in profileFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (options.Bookmarks)
            {
                ReadBookmarks(Path.Combine(profileFolder, "Bookmarks"), bookmarks);
            }

            if (options.History)
            {
                ReadHistory(Path.Combine(profileFolder, "History"), history, cancellationToken);
            }

            if (options.OpenTabs)
            {
                ReadOpenTabs(profileFolder, tabs);
            }

            if ((!searchImported && options.SearchEngine) ||
                (!homeImported && options.HomePage) ||
                (!settingsImported && options.Settings))
            {
                ReadPreferences(
                    Path.Combine(profileFolder, "Preferences"),
                    settings,
                    options,
                    ref searchImported,
                    ref homeImported,
                    ref settingsImported);
            }
        }

        int bookmarkCount = options.Bookmarks ? dataStore.ImportBookmarks(bookmarks) : 0;
        int historyCount = options.History ? dataStore.ImportHistory(history) : 0;
        if (searchImported || homeImported || settingsImported)
        {
            settings.Save();
        }

        IReadOnlyList<ExtensionPackage> extensions = options.Extensions
            ? ExtensionCatalog.ScanChromiumProfiles(browserRoot)
            : [];

        return new ChromiumImportResult(
            bookmarkCount,
            historyCount,
            tabs.Take(100).ToList(),
            extensions,
            searchImported,
            homeImported,
            settingsImported);
    }

    private static IEnumerable<string> EnumerateProfiles(string browserRoot)
    {
        string defaultProfile = Path.Combine(browserRoot, "Default");
        if (Directory.Exists(defaultProfile))
        {
            yield return defaultProfile;
        }

        foreach (string profile in Directory.EnumerateDirectories(browserRoot, "Profile *")
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            yield return profile;
        }
    }

    private static void ReadBookmarks(string path, List<BookmarkEntry> destination)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(ReadSharedText(path));
            if (!document.RootElement.TryGetProperty("roots", out JsonElement roots))
            {
                return;
            }

            foreach (JsonProperty root in roots.EnumerateObject())
            {
                ReadBookmarkNode(root.Value, destination);
            }
        }
        catch
        {
            // One damaged Chromium profile should not prevent importing the others.
        }
    }

    private static void ReadBookmarkNode(JsonElement node, List<BookmarkEntry> destination)
    {
        if (node.TryGetProperty("type", out JsonElement type) &&
            type.GetString()?.Equals("url", StringComparison.OrdinalIgnoreCase) == true &&
            node.TryGetProperty("url", out JsonElement urlValue) &&
            Uri.TryCreate(urlValue.GetString(), UriKind.Absolute, out Uri? uri) &&
            uri.Scheme is "http" or "https")
        {
            string title = node.TryGetProperty("name", out JsonElement name)
                ? name.GetString() ?? uri.Host
                : uri.Host;
            DateTimeOffset addedAt = DateTimeOffset.UtcNow;
            if (node.TryGetProperty("date_added", out JsonElement dateAdded) &&
                long.TryParse(dateAdded.GetString(), out long chromiumTime))
            {
                addedAt = FromChromiumTime(chromiumTime);
            }

            destination.Add(new BookmarkEntry
            {
                Url = uri.AbsoluteUri,
                Title = title,
                AddedAt = addedAt
            });
        }

        if (node.TryGetProperty("children", out JsonElement children) &&
            children.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                ReadBookmarkNode(child, destination);
            }
        }
    }

    private static void ReadHistory(
        string path,
        List<HistoryEntry> destination,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        string temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"Aero-Chromium-History-{Guid.NewGuid():N}.db");

        try
        {
            CopySharedFile(path, temporaryPath);
            using SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT url, title, last_visit_time FROM urls " +
                "WHERE hidden = 0 AND (url LIKE 'http://%' OR url LIKE 'https://%') " +
                "ORDER BY last_visit_time DESC LIMIT 2000";

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string url = reader.GetString(0);
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                {
                    continue;
                }

                destination.Add(new HistoryEntry
                {
                    Url = uri.AbsoluteUri,
                    Title = reader.IsDBNull(1) || string.IsNullOrWhiteSpace(reader.GetString(1))
                        ? uri.Host
                        : reader.GetString(1),
                    VisitedAt = FromChromiumTime(reader.GetInt64(2))
                });
            }
        }
        catch
        {
            // History can be unavailable when Chromium is compacting or upgrading its database.
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void ReadOpenTabs(string profileFolder, HashSet<string> destination)
    {
        string sessionsFolder = Path.Combine(profileFolder, "Sessions");
        if (!Directory.Exists(sessionsFolder))
        {
            return;
        }

        IEnumerable<string> sessionFiles = Directory.EnumerateFiles(sessionsFolder)
            .Where(path =>
            {
                string name = Path.GetFileName(path);
                return name.StartsWith("Tabs_", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Session_", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(8);

        foreach (string sessionFile in sessionFiles)
        {
            try
            {
                byte[] bytes = ReadSharedBytes(sessionFile);
                string text = Encoding.UTF8.GetString(bytes);
                foreach (Match match in SessionUrlRegex.Matches(text))
                {
                    string candidate = match.Value.TrimEnd(
                        '.', ',', ';', ':', ')', ']', '}', '"', '\'', '\\');
                    if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) &&
                        uri.Scheme is "http" or "https")
                    {
                        destination.Add(uri.AbsoluteUri);
                    }
                }
            }
            catch
            {
                // Session extraction is best-effort because Chromium's format is private.
            }
        }
    }

    private static void ReadPreferences(
        string path,
        BrowserSettings settings,
        ChromiumImportOptions options,
        ref bool searchImported,
        ref bool homeImported,
        ref bool settingsImported)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(ReadSharedText(path));
            JsonElement root = document.RootElement;

            if (options.HomePage &&
                root.TryGetProperty("homepage", out JsonElement homepage) &&
                homepage.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(homepage.GetString(), UriKind.Absolute, out Uri? homeUri) &&
                homeUri.Scheme is "http" or "https")
            {
                settings.HomePageUrl = homeUri.AbsoluteUri;
                homeImported = true;
            }

            if (options.SearchEngine &&
                TryReadSearchTemplate(root, out string searchTemplate))
            {
                ApplySearchTemplate(settings, searchTemplate);
                searchImported = true;
            }

            if (options.Settings)
            {
                if (root.TryGetProperty("bookmark_bar", out JsonElement bookmarkBar) &&
                    bookmarkBar.TryGetProperty("show_on_all_tabs", out JsonElement showBar) &&
                    showBar.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    settings.ShowBookmarksBar = showBar.GetBoolean();
                }

                if (root.TryGetProperty("download", out JsonElement download))
                {
                    if (download.TryGetProperty("prompt_for_download", out JsonElement prompt) &&
                        prompt.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        settings.AskDownloadPathEveryTime = prompt.GetBoolean();
                    }

                    if (download.TryGetProperty("default_directory", out JsonElement directory) &&
                        directory.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(directory.GetString()))
                    {
                        settings.DefaultDownloadFolder = directory.GetString()!;
                    }
                }

                if (root.TryGetProperty("browser", out JsonElement browser) &&
                    browser.TryGetProperty("show_home_button", out JsonElement showHome) &&
                    showHome.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    settings.ShowHomeButton = showHome.GetBoolean();
                }

                if (root.TryGetProperty("session", out JsonElement session) &&
                    session.TryGetProperty("restore_on_startup", out JsonElement restore) &&
                    restore.TryGetInt32(out int restoreMode))
                {
                    settings.StartupBehavior = restoreMode == 1
                        ? StartupBehavior.RestoreSession
                        : StartupBehavior.NewTab;
                    settings.RestorePreviousSession = restoreMode == 1;
                }

                settingsImported = true;
            }
        }
        catch
        {
            // Preferences from another profile may still be usable.
        }
    }

    private static bool TryReadSearchTemplate(JsonElement root, out string template)
    {
        template = string.Empty;
        if (!root.TryGetProperty("default_search_provider_data", out JsonElement provider))
        {
            return false;
        }

        JsonElement templateData = provider;
        if (provider.TryGetProperty("template_url_data", out JsonElement nested))
        {
            templateData = nested;
        }

        if (!templateData.TryGetProperty("url", out JsonElement url) ||
            url.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? candidate = url.GetString()
            ?.Replace("{searchTerms}", "{query}", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        template = candidate;
        return true;
    }

    private static void ApplySearchTemplate(BrowserSettings settings, string template)
    {
        if (template.Contains("google.", StringComparison.OrdinalIgnoreCase))
        {
            settings.SearchEngine = SearchEngineKind.Google;
        }
        else if (template.Contains("bing.com", StringComparison.OrdinalIgnoreCase))
        {
            settings.SearchEngine = SearchEngineKind.Bing;
        }
        else if (template.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            settings.SearchEngine = SearchEngineKind.DuckDuckGo;
        }
        else if (template.Contains("search.brave.com", StringComparison.OrdinalIgnoreCase))
        {
            settings.SearchEngine = SearchEngineKind.Brave;
        }
        else
        {
            settings.SearchEngine = SearchEngineKind.Custom;
            settings.CustomSearchUrl = template;
        }
    }

    private static DateTimeOffset FromChromiumTime(long microseconds)
    {
        try
        {
            return microseconds > 0
                ? ChromiumEpoch.AddTicks(checked(microseconds * 10))
                : DateTimeOffset.UtcNow;
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static string ReadSharedText(string path) =>
        Encoding.UTF8.GetString(ReadSharedBytes(path));

    private static byte[] ReadSharedBytes(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using MemoryStream memory = new();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void CopySharedFile(string source, string destination)
    {
        using FileStream input = new(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Temporary files are harmless if Windows still has a short-lived handle.
        }
    }

    [GeneratedRegex(@"https?://[^\x00-\x20<>""']{4,2048}", RegexOptions.IgnoreCase)]
    private static partial Regex SessionUrlPattern();
}
