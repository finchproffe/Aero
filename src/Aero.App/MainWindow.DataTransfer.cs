using Aero.App.Data;
using Aero.App.Security;
using Aero.Extensions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Aero.App;

public sealed partial class MainWindow
{
    private async Task<bool> HandleDataSettingsActionAsync(
        BrowserTabState state,
        string action,
        JsonElement message)
    {
        switch (action)
        {
            case "import-chrome":
                await ImportChromiumDataAsync(state, ChromiumBrowserKind.Chrome, message);
                return true;
            case "import-edge":
                await ImportChromiumDataAsync(state, ChromiumBrowserKind.Edge, message);
                return true;
            case "import-passwords-csv":
                await ImportPasswordsFromCsvAsync(state);
                return true;
            case "import-cookies-file":
                await ImportCookiesFromFileAsync(state);
                return true;
            case "export-passwords-csv":
                await ExportPasswordsToCsvAsync(state);
                return true;
            case "export-data":
                await ExportSelectedDataAsync(state, message);
                return true;
            case "backup-create":
                await CreateManualBackupAsync(state);
                return true;
            case "backup-validate":
                await ValidateBackupFromPickerAsync(state);
                return true;
            case "backup-restore":
                await StageBackupRestoreFromPickerAsync(state);
                return true;
            case "backup-recover-latest":
                await StageLatestRecoveryAsync(state);
                return true;
            default:
                return false;
        }
    }

    private async Task ImportChromiumDataAsync(
        BrowserTabState state,
        ChromiumBrowserKind browser,
        JsonElement message)
    {
        if (_isPrivateWindow || _browserProfile.IsGuest)
        {
            await PostSettingsStatusAsync(
                state,
                "Import is disabled in private and guest profiles.",
                isError: true);
            return;
        }

        HashSet<string> selected = ReadSelectedItems(message);
        ChromiumImportOptions options = new(
            Bookmarks: selected.Contains("bookmarks"),
            History: selected.Contains("history"),
            OpenTabs: selected.Contains("tabs"),
            SearchEngine: selected.Contains("search"),
            HomePage: selected.Contains("home"),
            Settings: selected.Contains("settings"),
            Extensions: selected.Contains("extensions"));

        await PostSettingsStatusAsync(state, $"Reading {browser} profiles…");
        ChromiumImportResult result = await ChromiumImportService.ImportAsync(
            browser,
            options,
            _dataStore,
            _settings);

        int extensionCount = options.Extensions
            ? await ImportCompatibleExtensionsAsync(result.Extensions)
            : 0;

        foreach (string url in result.OpenTabs)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && IsBrowsableUri(uri))
            {
                await CreateTabAsync(
                    uri,
                    selectTab: false,
                    deferWebViewInitialization: true);
            }
        }

        ApplyAllRuntimeSettings(nameof(Aero.App.Settings.BrowserSettings.SearchEngine));
        RenderBookmarksBar();
        if (_libraryPaneOpen)
        {
            RefreshLibraryPane();
        }

        await RefreshSettingsPageAsync(state, result.BuildSummary(extensionCount));
    }

    private async Task<int> ImportCompatibleExtensionsAsync(
        IReadOnlyList<ExtensionPackage> packages)
    {
        CoreWebView2Profile? profile = GetActiveProfile();
        if (profile is null || packages.Count == 0)
        {
            return 0;
        }

        await RefreshInstalledExtensionsAsync();
        HashSet<string> installed = _installedExtensions
            .Select(extension => extension.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int imported = 0;

        foreach (ExtensionPackage package in packages
                     .Where(package => !installed.Contains(package.Id))
                     .Take(80))
        {
            string managedPath = CreateManagedExtensionPath(package.Name);
            try
            {
                CopyDirectory(package.FolderPath, managedPath);
                CoreWebView2BrowserExtension extension =
                    await profile.AddBrowserExtensionAsync(managedPath);
                _extensionRegistry.SetPath(extension.Id, managedPath);
                installed.Add(extension.Id);
                imported++;
            }
            catch
            {
                TryDeleteManagedExtensionFolder(managedPath);
            }
        }

        await RefreshExtensionToolbarAsync();
        return imported;
    }

    private async Task ImportPasswordsFromCsvAsync(BrowserTabState state)
    {
        if (_isPrivateWindow || _browserProfile.IsGuest)
        {
            await PostSettingsStatusAsync(
                state,
                "Password import is disabled in private and guest profiles.",
                isError: true);
            return;
        }

        ContentDialog warning = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Safe password import",
            Content =
                "Choose a password CSV exported by Chrome, Edge, Firefox, Brave, Opera, " +
                "Vivaldi, Safari, 1Password, Bitwarden, or another browser. Aero recognizes " +
                "their common column names. The file is streamed row by row; password fields " +
                "use disposable character buffers instead of immutable .NET strings. Delete " +
                "the CSV securely after import.",
            PrimaryButtonText = "Choose exported CSV",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await warning.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        FileOpenPicker picker = new();
        picker.FileTypeFilter.Add(".csv");
        picker.FileTypeFilter.Add(".txt");
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        if (new FileInfo(file.Path).Length > 32L * 1024 * 1024)
        {
            throw new InvalidDataException("The selected CSV is unexpectedly large.");
        }

        char delimiter = await StreamingCsvReader.DetectDelimiterAsync(file.Path);
        Dictionary<string, int>? columns = null;
        int urlColumn = -1;
        int usernameColumn = -1;
        int passwordColumn = -1;
        int imported = 0;
        int skipped = 0;
        int dataRows = 0;

        await using (PasswordVaultService.PasswordVaultWriteSession session =
            await _passwordVaultService.BeginWriteSessionAsync(
                handle,
                "Import protected sign-ins from the selected CSV file",
                forceReauthentication: true))
        {
            await foreach (StreamingCsvReader.SecureCsvRow row in StreamingCsvReader.ReadRowsAsync(
                file.Path,
                delimiter,
                maximumRows: 5001))
            {
                using (row)
                {
                    if (columns is null)
                    {
                        columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        for (int index = 0; index < row.Count; index++)
                        {
                            string name = NormalizeCsvColumnName(new string(row.GetField(index)));
                            if (name.Length > 0 && !columns.ContainsKey(name))
                            {
                                columns.Add(name, index);
                            }
                        }

                        urlColumn = FindColumn(
                            columns,
                            "url", "origin", "website", "origin_url", "signon_realm",
                            "login_uri", "formActionOrigin", "hostname");
                        usernameColumn = FindColumn(
                            columns,
                            "username", "user", "login", "login_username", "user_name");
                        passwordColumn = FindColumn(
                            columns,
                            "password", "pass", "login_password");
                        continue;
                    }

                    dataRows++;
                    string? url = CreateSafeCsvUrl(row.GetField(urlColumn));
                    if (row.GetField(passwordColumn).IsEmpty ||
                        url is null ||
                        !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                        !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                        uri.UserInfo.Length > 0 ||
                        PasswordVaultService.IsSuspiciousHost(uri.IdnHost, out _))
                    {
                        skipped++;
                        continue;
                    }

                    byte[] usernameBytes = EncodeUtf8(row.GetField(usernameColumn));
                    byte[] passwordBytes = EncodeUtf8(row.GetField(passwordColumn));
                    try
                    {
                        await session.SavePasswordAsync(
                            uri.GetLeftPart(UriPartial.Authority),
                            usernameBytes,
                            passwordBytes);
                        imported++;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(usernameBytes);
                        CryptographicOperations.ZeroMemory(passwordBytes);
                    }
                }
            }

            if (columns is null || dataRows == 0)
            {
                throw new InvalidDataException("The CSV contains no password records.");
            }

            if (imported > 0)
            {
                await session.CommitAsync();
            }
        }

        columns?.Clear();
        await RefreshPasswordVaultCountAsync();
        await PostSettingsStatusAsync(
            state,
            $"Imported {imported} HTTPS passwords into Aero Vault" +
            (skipped > 0 ? $"; skipped {skipped} unsupported rows." : "."));
    }

    private async Task ImportCookiesFromFileAsync(BrowserTabState state)
    {
        if (_isPrivateWindow || _browserProfile.IsGuest)
        {
            await PostSettingsStatusAsync(
                state,
                "Cookie import is disabled in private and guest profiles.",
                isError: true);
            return;
        }

        CoreWebView2Profile? profile = GetActiveProfile();
        if (profile is null)
        {
            await PostSettingsStatusAsync(
                state,
                "The WebView2 profile is not ready yet. Open a normal tab and try again.",
                isError: true);
            return;
        }

        ContentDialog warning = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Import cookies into this profile?",
            Content =
                "Cookie files can contain active sign-in sessions. Import only a file you " +
                "created yourself and trust. Aero supports Netscape cookies.txt, JSON from " +
                "popular cookie-export extensions, and CSV. The source file is not copied " +
                "into Aero or written to logs.",
            PrimaryButtonText = "Choose cookie file",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await warning.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        FileOpenPicker picker = new();
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".csv");
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        FileInfo fileInfo = new(file.Path);
        if (fileInfo.Length <= 0 || fileInfo.Length > 32L * 1024 * 1024)
        {
            throw new InvalidDataException(
                "The selected cookie file is empty or larger than the 32 MB safety limit.");
        }

        await PostSettingsStatusAsync(state, $"Reading {file.Name}…");
        string content = await File.ReadAllTextAsync(file.Path);
        CookieImportBatch batch;
        try
        {
            batch = ParseCookieImportFile(content, file.FileType);
        }
        finally
        {
            content = string.Empty;
        }

        if (batch.Cookies.Count == 0)
        {
            throw new InvalidDataException(
                "No valid cookies were found. Use Netscape cookies.txt, JSON, or CSV with name, value, and domain fields.");
        }

        string domainPreview = string.Join(
            ", ",
            batch.Cookies
                .Select(cookie => cookie.Domain.TrimStart('.'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)
                .Take(10));
        int distinctDomainCount = batch.Cookies
            .Select(cookie => cookie.Domain.TrimStart('.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (distinctDomainCount > 10)
        {
            domainPreview += $" and {distinctDomainCount - 10} more";
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"Import {batch.Cookies.Count} cookies?",
            Content =
                $"Format: {batch.Format}. Domains: {domainPreview}. " +
                (batch.Skipped > 0
                    ? $"Aero already rejected {batch.Skipped} malformed or expired records. "
                    : string.Empty) +
                "Existing cookies with the same name, domain, and path will be updated. " +
                "Windows Hello is required to continue.",
            PrimaryButtonText = "Import cookies",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            batch.Cookies.Clear();
            return;
        }

        int imported = 0;
        int skipped = batch.Skipped;
        try
        {
            await PasswordVaultService.VerifyCookieImportConsentAsync(handle);
            CoreWebView2CookieManager cookieManager = profile.CookieManager;
            foreach (CookieImportRecord record in batch.Cookies.Take(MaximumImportedCookieCount))
            {
                try
                {
                    CoreWebView2Cookie cookie = cookieManager.CreateCookie(
                        record.Name,
                        record.Value,
                        record.Domain,
                        record.Path);
                    cookie.IsSecure = record.IsSecure;
                    cookie.IsHttpOnly = record.IsHttpOnly;
                    cookie.SameSite = record.SameSite;
                    if (record.ExpiresUtc is DateTimeOffset expiresUtc)
                    {
                        cookie.Expires = expiresUtc.ToUnixTimeMilliseconds() / 1000d;
                    }
                    cookieManager.AddOrUpdateCookie(cookie);
                    imported++;
                }
                catch
                {
                    skipped++;
                }
            }

            if (batch.Cookies.Count > MaximumImportedCookieCount)
            {
                skipped += batch.Cookies.Count - MaximumImportedCookieCount;
            }
        }
        finally
        {
            batch.Cookies.Clear();
        }

        await PostSettingsStatusAsync(
            state,
            $"Imported {imported} cookies into profile “{_browserProfile.Name}”" +
            (skipped > 0 ? $"; skipped {skipped} unsupported records." : "."),
            isError: imported == 0);
    }

    private static CookieImportBatch ParseCookieImportFile(
        string content,
        string? fileExtension)
    {
        string normalized = content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (normalized.Length == 0)
        {
            return new CookieImportBatch([], 0, "empty file");
        }

        if (normalized[0] is '{' or '[' ||
            string.Equals(fileExtension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            return ParseJsonCookies(normalized);
        }

        if (LooksLikeNetscapeCookieFile(normalized))
        {
            return ParseNetscapeCookies(normalized);
        }

        return ParseCookieCsv(normalized);
    }

    private static bool LooksLikeNetscapeCookieFile(string content)
    {
        if (content.Contains(
            "Netscape HTTP Cookie File",
            StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 ||
                (line.StartsWith('#') &&
                 !line.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string candidate = line.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase)
                ? line["#HttpOnly_".Length..]
                : line;
            string[] fields = candidate.Split('\t');
            return fields.Length >= 7 &&
                IsBooleanToken(fields[1]) &&
                fields[2].StartsWith("/", StringComparison.Ordinal);
        }

        return false;
    }

    private static CookieImportBatch ParseNetscapeCookies(string content)
    {
        List<CookieImportRecord> cookies = [];
        int skipped = 0;

        foreach (string rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            bool isHttpOnly = line.StartsWith(
                "#HttpOnly_",
                StringComparison.OrdinalIgnoreCase);
            if (line.StartsWith('#') && !isHttpOnly)
            {
                continue;
            }

            if (isHttpOnly)
            {
                line = line["#HttpOnly_".Length..];
            }

            string[] fields = line.Split('\t');
            if (fields.Length < 7)
            {
                skipped++;
                continue;
            }

            string domain = fields[0];
            bool includeSubdomains = ParseBooleanToken(fields[1]);
            if (includeSubdomains &&
                !domain.StartsWith('.') &&
                !IsIpOrLocalHost(domain))
            {
                domain = "." + domain;
            }

            DateTimeOffset? expires = ParseCookieExpiration(fields[4]);
            string value = fields.Length == 7
                ? fields[6]
                : string.Join("\t", fields.Skip(6));
            if (TryBuildCookieImportRecord(
                fields[5],
                value,
                domain,
                fields[2],
                ParseBooleanToken(fields[3]),
                isHttpOnly,
                expires,
                CoreWebView2CookieSameSiteKind.Lax,
                out CookieImportRecord record))
            {
                cookies.Add(record);
            }
            else
            {
                skipped++;
            }
        }

        return new CookieImportBatch(cookies, skipped, "Netscape cookies.txt");
    }

    private static CookieImportBatch ParseJsonCookies(string content)
    {
        List<CookieImportRecord> cookies = [];
        int skipped = 0;
        using JsonDocument document = JsonDocument.Parse(
            content,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64
            });

        IEnumerable<JsonElement> elements = EnumerateJsonCookieObjects(document.RootElement);
        foreach (JsonElement element in elements.Take(MaximumImportedCookieCount + 1))
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                skipped++;
                continue;
            }

            string name = ReadJsonCookieString(element, "name", "key") ?? string.Empty;
            string value = ReadJsonCookieString(element, "value", "content") ?? string.Empty;
            string domain = ReadJsonCookieString(element, "domain", "host", "hostname") ?? string.Empty;
            string? url = ReadJsonCookieString(element, "url", "origin");
            if (domain.Length == 0 &&
                Uri.TryCreate(url, UriKind.Absolute, out Uri? sourceUri))
            {
                domain = sourceUri.Host;
            }

            bool? hostOnly = ReadJsonCookieBoolean(element, "hostOnly", "host_only");
            if (hostOnly == false &&
                domain.Length > 0 &&
                !domain.StartsWith('.') &&
                !IsIpOrLocalHost(domain))
            {
                domain = "." + domain;
            }

            bool isSecure = ReadJsonCookieBoolean(element, "secure", "isSecure") ??
                (Uri.TryCreate(url, UriKind.Absolute, out Uri? secureUri) &&
                 secureUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
            bool isHttpOnly = ReadJsonCookieBoolean(
                element,
                "httpOnly",
                "httponly",
                "isHttpOnly") ?? false;
            bool isSession = ReadJsonCookieBoolean(element, "session", "isSession") ?? false;
            DateTimeOffset? expires = isSession
                ? null
                : ReadJsonCookieExpiration(
                    element,
                    "expirationDate",
                    "expires",
                    "expiration",
                    "expiry");
            CoreWebView2CookieSameSiteKind sameSite = ParseSameSite(
                ReadJsonCookieString(element, "sameSite", "samesite", "same_site"));

            if (TryBuildCookieImportRecord(
                name,
                value,
                domain,
                ReadJsonCookieString(element, "path") ?? "/",
                isSecure,
                isHttpOnly,
                expires,
                sameSite,
                out CookieImportRecord record))
            {
                cookies.Add(record);
            }
            else
            {
                skipped++;
            }
        }

        return new CookieImportBatch(cookies, skipped, "JSON");
    }

    private static IEnumerable<JsonElement> EnumerateJsonCookieObjects(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in root.EnumerateArray())
            {
                yield return item;
            }
            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (string propertyName in new[] { "cookies", "data", "items" })
        {
            if (TryGetJsonProperty(root, out JsonElement collection, propertyName) &&
                collection.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in collection.EnumerateArray())
                {
                    yield return item;
                }
                yield break;
            }
        }

        if (TryGetJsonProperty(root, out _, "name") &&
            TryGetJsonProperty(root, out _, "value"))
        {
            yield return root;
        }
    }

    private static CookieImportBatch ParseCookieCsv(string content)
    {
        char delimiter = DetectCsvDelimiter(content);
        List<IReadOnlyList<string>> rows = ParseCsv(content, delimiter);
        if (rows.Count < 2)
        {
            throw new InvalidDataException("The CSV contains no cookie records.");
        }

        Dictionary<string, int> columns = rows[0]
            .Select((name, index) => new { Name = NormalizeCsvColumnName(name), Index = index })
            .Where(item => item.Name.Length > 0)
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Index,
                StringComparer.OrdinalIgnoreCase);
        int nameColumn = FindColumn(columns, "name", "key", "cookiename");
        int valueColumn = FindColumn(columns, "value", "content", "cookievalue");
        int domainColumn = FindOptionalColumn(columns, "domain", "host", "hostname");
        int urlColumn = FindOptionalColumn(columns, "url", "origin");
        if (domainColumn < 0 && urlColumn < 0)
        {
            throw new InvalidDataException("The CSV is missing the domain or URL column.");
        }

        int pathColumn = FindOptionalColumn(columns, "path");
        int secureColumn = FindOptionalColumn(columns, "secure", "issecure");
        int httpOnlyColumn = FindOptionalColumn(columns, "httponly", "ishttponly");
        int expiresColumn = FindOptionalColumn(
            columns,
            "expirationdate",
            "expires",
            "expiration",
            "expiry");
        int sessionColumn = FindOptionalColumn(columns, "session", "issession");
        int sameSiteColumn = FindOptionalColumn(columns, "samesite", "samesitekind");
        int hostOnlyColumn = FindOptionalColumn(columns, "hostonly", "host_only");

        List<CookieImportRecord> cookies = [];
        int skipped = 0;
        foreach (IReadOnlyList<string> row in rows.Skip(1).Take(MaximumImportedCookieCount + 1))
        {
            _ = TryGetColumn(row, nameColumn, out string? name);
            _ = TryGetColumn(row, valueColumn, out string? value);
            _ = TryGetColumn(row, domainColumn, out string? domain);
            _ = TryGetColumn(row, urlColumn, out string? url);
            if (string.IsNullOrWhiteSpace(domain) &&
                Uri.TryCreate(url, UriKind.Absolute, out Uri? sourceUri))
            {
                domain = sourceUri.Host;
            }

            bool? hostOnly = TryReadCsvBoolean(row, hostOnlyColumn);
            if (hostOnly == false &&
                !string.IsNullOrWhiteSpace(domain) &&
                !domain.StartsWith('.') &&
                !IsIpOrLocalHost(domain))
            {
                domain = "." + domain;
            }

            bool isSecure = TryReadCsvBoolean(row, secureColumn) ??
                (Uri.TryCreate(url, UriKind.Absolute, out Uri? secureUri) &&
                 secureUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
            bool isSession = TryReadCsvBoolean(row, sessionColumn) ?? false;
            _ = TryGetColumn(row, expiresColumn, out string? expiresText);
            _ = TryGetColumn(row, pathColumn, out string? path);
            _ = TryGetColumn(row, sameSiteColumn, out string? sameSiteText);

            if (TryBuildCookieImportRecord(
                name ?? string.Empty,
                value ?? string.Empty,
                domain ?? string.Empty,
                path ?? "/",
                isSecure,
                TryReadCsvBoolean(row, httpOnlyColumn) ?? false,
                isSession ? null : ParseCookieExpiration(expiresText),
                ParseSameSite(sameSiteText),
                out CookieImportRecord record))
            {
                cookies.Add(record);
            }
            else
            {
                skipped++;
            }
        }

        return new CookieImportBatch(cookies, skipped, "CSV");
    }

    private static bool TryBuildCookieImportRecord(
        string name,
        string value,
        string domain,
        string path,
        bool isSecure,
        bool isHttpOnly,
        DateTimeOffset? expiresUtc,
        CoreWebView2CookieSameSiteKind sameSite,
        out CookieImportRecord record)
    {
        record = null!;
        name = name.Trim();
        domain = domain.Trim();
        path = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();

        if (domain.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase))
        {
            domain = domain["#HttpOnly_".Length..];
            isHttpOnly = true;
        }
        if (Uri.TryCreate(domain, UriKind.Absolute, out Uri? domainUri))
        {
            domain = domainUri.Host;
        }
        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            path = "/" + path;
        }

        string host = domain.TrimStart('.').Trim('[', ']');
        if (name.Length == 0 ||
            name.Length > 1024 ||
            value.Length > 16 * 1024 ||
            domain.Length == 0 ||
            domain.Length > 255 ||
            path.Length > 2048 ||
            ContainsCookieControlCharacters(name) ||
            ContainsCookieControlCharacters(value) ||
            ContainsCookieControlCharacters(domain) ||
            ContainsCookieControlCharacters(path) ||
            (Uri.CheckHostName(host) == UriHostNameType.Unknown &&
             !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) ||
            (expiresUtc.HasValue && expiresUtc.Value <= DateTimeOffset.UtcNow) ||
            (name.StartsWith("__Host-", StringComparison.Ordinal) &&
             (domain.StartsWith('.') || path != "/")) )
        {
            return false;
        }

        if (name.StartsWith("__Host-", StringComparison.Ordinal) ||
            name.StartsWith("__Secure-", StringComparison.Ordinal) ||
            sameSite == CoreWebView2CookieSameSiteKind.None)
        {
            isSecure = true;
        }

        record = new CookieImportRecord(
            name,
            value,
            domain,
            path,
            isSecure,
            isHttpOnly,
            expiresUtc?.ToUniversalTime(),
            sameSite);
        return true;
    }

    private static bool ContainsCookieControlCharacters(string value) =>
        value.Any(character => character == '\0' || character == '\r' || character == '\n' || character == '\t');

    private static bool IsIpOrLocalHost(string domain)
    {
        string host = domain.Trim().TrimStart('.').Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            Uri.CheckHostName(host) is UriHostNameType.IPv4 or UriHostNameType.IPv6;
    }

    private static bool IsBooleanToken(string? value) =>
        value?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Trim() is "1" or "0";

    private static bool ParseBooleanToken(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            normalized is "1";
    }

    private static bool? TryReadCsvBoolean(IReadOnlyList<string> row, int column)
    {
        if (!TryGetColumn(row, column, out string? value) ||
            string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ParseBooleanToken(value);
    }

    private static DateTimeOffset? ParseCookieExpiration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim();
        if (double.TryParse(
            normalized,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double numeric))
        {
            if (numeric <= 0)
            {
                return null;
            }
            if (numeric > 100_000_000_000d)
            {
                numeric /= 1000d;
            }
            if (numeric > 253_402_300_799d)
            {
                return null;
            }

            try
            {
                return DateTimeOffset.UnixEpoch.AddSeconds(numeric);
            }
            catch
            {
                return null;
            }
        }

        return DateTimeOffset.TryParse(
            normalized,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
                ? parsed
                : null;
    }

    private static DateTimeOffset? ReadJsonCookieExpiration(
        JsonElement element,
        params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetJsonProperty(element, out JsonElement value, name))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double numeric))
            {
                return ParseCookieExpiration(
                    numeric.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (value.ValueKind == JsonValueKind.String)
            {
                return ParseCookieExpiration(value.GetString());
            }
        }

        return null;
    }

    private static string? ReadJsonCookieString(
        JsonElement element,
        params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetJsonProperty(element, out JsonElement value, name))
            {
                continue;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
                _ => null
            };
        }

        return null;
    }

    private static bool? ReadJsonCookieBoolean(
        JsonElement element,
        params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetJsonProperty(element, out JsonElement value, name))
            {
                continue;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when value.TryGetInt32(out int numeric) => numeric != 0,
                JsonValueKind.String => ParseBooleanToken(value.GetString()),
                _ => null
            };
        }

        return null;
    }

    private static bool TryGetJsonProperty(
        JsonElement element,
        out JsonElement value,
        params string[] names)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static CoreWebView2CookieSameSiteKind ParseSameSite(string? value)
    {
        string normalized = NormalizeCsvColumnName(value);
        return normalized switch
        {
            "none" or "norestriction" or "no_restriction" =>
                CoreWebView2CookieSameSiteKind.None,
            "strict" => CoreWebView2CookieSameSiteKind.Strict,
            _ => CoreWebView2CookieSameSiteKind.Lax
        };
    }

    private async Task ExportPasswordsToCsvAsync(BrowserTabState state)
    {
        if (_isPrivateWindow || _browserProfile.IsGuest)
        {
            await PostSettingsStatusAsync(
                state,
                "Password export is disabled in private and guest profiles.",
                isError: true);
            return;
        }

        int passwordCount = await _passwordVaultService.CountAsync();
        if (passwordCount == 0)
        {
            await PostSettingsStatusAsync(state, "Aero Vault has no passwords to export.", isError: true);
            return;
        }

        ContentDialog warning = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Export passwords as plain text?",
            Content =
                "The exported CSV can be imported by Chrome, Edge, Firefox, Brave, Opera, " +
                "Vivaldi, Safari, and most password managers. Anyone who obtains this file " +
                "can read every password. Aero will require Windows Hello before decrypting it.",
            PrimaryButtonText = "Choose export location",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await warning.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        FileSavePicker picker = new()
        {
            SuggestedFileName = $"Aero-passwords-{DateTime.Now:yyyy-MM-dd}"
        };
        picker.FileTypeChoices.Add("Password CSV", [".csv"]);
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        bool exportCompleted = false;
        try
        {
            using PasswordExportLease exportLease =
                await _passwordVaultService.GetAllPasswordsJitAsync(handle);
            // The user explicitly selected a plaintext destination. Write only to that
            // destination instead of creating a second plaintext .tmp copy that could
            // survive a crash. An incomplete destination is deleted on failure.
            await using (FileStream stream = new(
                file.Path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                32 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                byte[] header = "name,url,username,password\r\n"u8.ToArray();
                await stream.WriteAsync(header);

                foreach (PasswordExportEntryLease entry in exportLease.Entries)
                {
                    // The origin is used as both name and URL. This keeps the file
                    // compatible without creating extra plaintext password strings.
                    WritePasswordCsvField(stream, entry.OriginUtf8.Span);
                    stream.WriteByte((byte)',');
                    WritePasswordCsvField(stream, entry.OriginUtf8.Span);
                    stream.WriteByte((byte)',');
                    WritePasswordCsvField(stream, entry.UsernameUtf8.Span);
                    stream.WriteByte((byte)',');
                    WritePasswordCsvField(stream, entry.PasswordUtf8);
                    stream.WriteByte((byte)'\r');
                    stream.WriteByte((byte)'\n');
                }

                await stream.FlushAsync();
            }

            exportCompleted = true;
            await PostSettingsStatusAsync(
                state,
                $"Exported {passwordCount} passwords to {file.Name}. Keep the CSV private and delete it after import.");
        }
        finally
        {
            if (!exportCompleted)
            {
                try
                {
                    if (File.Exists(file.Path))
                    {
                        File.Delete(file.Path);
                    }
                }
                catch
                {
                    // Best-effort cleanup of an incomplete, user-authorized plaintext export.
                }
            }
        }
    }

    private static void WritePasswordCsvField(
        Stream stream,
        ReadOnlySpan<byte> utf8Value)
    {
        stream.WriteByte((byte)'"');
        int segmentStart = 0;

        for (int index = 0; index < utf8Value.Length; index++)
        {
            if (utf8Value[index] != (byte)'"')
            {
                continue;
            }

            if (index > segmentStart)
            {
                stream.Write(utf8Value[segmentStart..index]);
            }

            stream.WriteByte((byte)'"');
            stream.WriteByte((byte)'"');
            segmentStart = index + 1;
        }

        if (segmentStart < utf8Value.Length)
        {
            stream.Write(utf8Value[segmentStart..]);
        }

        stream.WriteByte((byte)'"');
    }

    private async Task ExportSelectedDataAsync(
        BrowserTabState state,
        JsonElement message)
    {
        HashSet<string> selected = ReadSelectedItems(message);
        if (selected.Count == 0)
        {
            await PostSettingsStatusAsync(state, "Select at least one data category.", isError: true);
            return;
        }

        Dictionary<string, object?> export = new()
        {
            ["format"] = "AeroDataExport",
            ["schemaVersion"] = 1,
            ["aeroVersion"] = ProfileBackupService.CurrentAeroVersion,
            ["exportedAt"] = DateTimeOffset.UtcNow,
            ["profile"] = new { _browserProfile.Id, _browserProfile.Name }
        };

        if (selected.Contains("bookmarks"))
        {
            export["bookmarks"] = _dataStore.Library.Bookmarks;
        }
        if (selected.Contains("history"))
        {
            export["history"] = _dataStore.Library.History;
        }
        if (selected.Contains("settings"))
        {
            export["settings"] = _settings;
        }
        if (selected.Contains("tabs"))
        {
            export["tabs"] = _tabs.Select(tab => new
            {
                Url = (tab.CurrentUri ?? tab.PendingUri)?.AbsoluteUri,
                tab.IsPinned,
                tab.TabGroupId
            }).ToList();
        }
        if (selected.Contains("workspaces"))
        {
            export["workspaces"] = _dataStore.Library.Workspaces;
        }
        if (selected.Contains("permissions"))
        {
            export["permissions"] = _sitePermissionStore.Snapshot();
        }
        if (selected.Contains("shield"))
        {
            export["shield"] = new
            {
                Allowlist = _settings.ShieldAllowlist,
                CustomRules = _settings.ShieldCustomRules
            };
        }

        FileSavePicker picker = new()
        {
            SuggestedFileName = $"Aero-data-{DateTime.Now:yyyy-MM-dd}"
        };
        picker.FileTypeChoices.Add("Aero data (JSON)", [".aerodata"]);
        picker.FileTypeChoices.Add("JSON", [".json"]);
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        JsonSerializerOptions exportOptions = new(SettingsJsonOptions)
        {
            WriteIndented = true
        };
        string json = JsonSerializer.Serialize(export, exportOptions);
        await File.WriteAllTextAsync(file.Path, json, new UTF8Encoding(false));
        await PostSettingsStatusAsync(state, $"Data exported to {file.Name}.");
    }

    private async Task CreateManualBackupAsync(BrowserTabState state)
    {
        if (_isPrivateWindow || _browserProfile.IsGuest)
        {
            await PostSettingsStatusAsync(
                state,
                "Profile backups are unavailable in private and guest profiles.",
                isError: true);
            return;
        }

        BackupPasswordChoice? choice = await AskBackupPasswordAsync(
            "Create profile backup",
            "Create backup",
            allowUnencrypted: true);
        if (choice is null)
        {
            return;
        }

        FileSavePicker picker = new()
        {
            SuggestedFileName = $"Aero-{SanitizeFileName(_browserProfile.Name)}-{DateTime.Now:yyyy-MM-dd}"
        };
        picker.FileTypeChoices.Add("Aero profile backup", [ProfileBackupService.BackupExtension]);
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        _settings.Save();
        SaveSessionSnapshot();
        ProfileBackupEncryption encryption = choice.Encrypt
            ? ProfileBackupEncryption.Password
            : ProfileBackupEncryption.None;
        ProfileBackupManifest manifest = await ProfileBackupService.CreateBackupAsync(
            _browserProfile,
            file.Path,
            encryption,
            choice.Password);
        choice = null;
        string securityNote = encryption == ProfileBackupEncryption.None
            ? " Passwords, cookies, secure preferences, local storage, and IndexedDB were excluded because the backup is not encrypted."
            : " Sensitive profile data is included inside the encrypted envelope.";
        await PostSettingsStatusAsync(
            state,
            $"Backup created and verified: {manifest.Files.Count} files" +
            (manifest.SkippedFileCount > 0
                ? $", {manifest.SkippedFileCount} live or oversized files skipped."
                : ".") +
            securityNote);
    }

    private async Task ValidateBackupFromPickerAsync(BrowserTabState state)
    {
        StorageFile? file = await PickBackupFileAsync();
        if (file is null)
        {
            return;
        }

        BackupPasswordChoice? choice = await AskBackupPasswordAsync(
            "Check backup integrity",
            "Verify",
            allowUnencrypted: true,
            encryptionLabel: "This backup is password-encrypted");
        if (choice is null)
        {
            return;
        }

        ProfileBackupManifest manifest = await ProfileBackupService.ValidateBackupAsync(
            file.Path,
            choice.Encrypt ? choice.Password : null);
        choice = null;
        await PostSettingsStatusAsync(
            state,
            $"Backup is intact: Aero {manifest.AeroVersion}, " +
            $"{manifest.Files.Count} files, {manifest.CreatedAt.LocalDateTime:g}.");
    }

    private async Task StageBackupRestoreFromPickerAsync(BrowserTabState state)
    {
        if (_isPrivateWindow || _browserProfile.IsGuest)
        {
            await PostSettingsStatusAsync(
                state,
                "Profile restore is unavailable in private and guest profiles.",
                isError: true);
            return;
        }

        StorageFile? file = await PickBackupFileAsync();
        if (file is null)
        {
            return;
        }

        BackupPasswordChoice? choice = await AskBackupPasswordAsync(
            "Restore profile",
            "Continue",
            allowUnencrypted: true,
            encryptionLabel: "This backup is password-encrypted");
        if (choice is null)
        {
            return;
        }

        ProfileBackupManifest inspected = await ProfileBackupService.ValidateBackupAsync(
            file.Path,
            choice.Encrypt ? choice.Password : null);

        ContentDialog confirmation = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Restore this profile?",
            Content =
                $"Backup: {inspected.ProfileName}, Aero {inspected.AeroVersion}, " +
                $"{inspected.CreatedAt.LocalDateTime:g}.\n\n" +
                "Current profile data will be replaced on the next Aero launch. " +
                "An automatic copy of the current profile will be kept first.",
            PrimaryButtonText = "Schedule restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _settings.Save();
        SaveSessionSnapshot();
        await ProfileBackupService.CreateAutomaticBackupAsync(
            _browserProfile,
            _settings.BackupRetentionCount);
        ProfileBackupManifest manifest = await ProfileBackupService.StageRestoreAsync(
            _browserProfile,
            file.Path,
            choice.Encrypt ? choice.Password : null);
        choice = null;
        await ShowRestartRequiredDialogAsync(
            $"Profile restore is ready ({manifest.Files.Count} verified files).");
        await PostSettingsStatusAsync(state, "Restore scheduled for the next Aero launch.");
    }

    private async Task StageLatestRecoveryAsync(BrowserTabState state)
    {
        if (_isPrivateWindow || _browserProfile.IsGuest)
        {
            await PostSettingsStatusAsync(
                state,
                "Profile recovery is unavailable in private and guest profiles.",
                isError: true);
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Recover from the latest intact copy?",
            Content =
                "Aero will check automatic copies from newest to oldest and stage the first " +
                "one that passes every integrity check.",
            PrimaryButtonText = "Recover",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ProfileBackupManifest manifest =
            await ProfileBackupService.StageLatestRecoveryAsync(_browserProfile);
        await ShowRestartRequiredDialogAsync(
            $"Recovery from {manifest.CreatedAt.LocalDateTime:g} is ready.");
        await PostSettingsStatusAsync(state, "Recovery scheduled for the next Aero launch.");
    }

    private async Task RunAutomaticBackupIfDueAsync()
    {
        if (_isPrivateWindow ||
            _browserProfile.IsGuest ||
            !_settings.AutomaticBackupsEnabled ||
            !ProfileBackupService.IsAutomaticBackupDue(
                _settings.LastAutomaticBackupUtc,
                _settings.AutomaticBackupIntervalHours))
        {
            return;
        }

        try
        {
            _settings.Save();
            SaveSessionSnapshot();
            await ProfileBackupService.CreateAutomaticBackupAsync(
                _browserProfile,
                _settings.BackupRetentionCount);
            _settings.LastAutomaticBackupUtc = DateTimeOffset.UtcNow;
            _settings.Save();
        }
        catch
        {
            // Backup errors are surfaced in Settings without blocking browser startup.
        }
    }

    private async Task<StorageFile?> PickBackupFileAsync()
    {
        FileOpenPicker picker = new();
        picker.FileTypeFilter.Add(ProfileBackupService.BackupExtension);
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
        return await picker.PickSingleFileAsync();
    }

    private async Task<BackupPasswordChoice?> AskBackupPasswordAsync(
        string title,
        string primaryButton,
        bool allowUnencrypted,
        string encryptionLabel = "Encrypt with a password")
    {
        CheckBox encrypted = new()
        {
            Content = encryptionLabel,
            IsChecked = !allowUnencrypted || encryptionLabel.StartsWith("Encrypt", StringComparison.Ordinal)
        };
        PasswordBox password = new()
        {
            PlaceholderText = "Backup password",
            IsEnabled = encrypted.IsChecked == true
        };
        TextBlock validation = new()
        {
            Text = "Use at least 8 characters.",
            Visibility = Visibility.Collapsed
        };
        encrypted.Checked += (_, _) => password.IsEnabled = true;
        encrypted.Unchecked += (_, _) =>
        {
            password.Password = string.Empty;
            password.IsEnabled = false;
        };

        StackPanel content = new()
        {
            Spacing = 10,
            Children =
            {
                encrypted,
                password,
                validation,
                new TextBlock
                {
                    Text = allowUnencrypted
                        ? "Without encryption, Aero excludes Vault files, cookies, secure preferences, local storage, and IndexedDB. Enable encryption to include them."
                        : "A password is required.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.72
                }
            }
        };

        ContentDialog dialog = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButton,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        while (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            bool usePassword = encrypted.IsChecked == true;
            if (!usePassword || password.Password.Length >= 8)
            {
                string value = password.Password;
                password.Password = string.Empty;
                return new BackupPasswordChoice(usePassword, value);
            }

            validation.Visibility = Visibility.Visible;
        }

        password.Password = string.Empty;
        return null;
    }

    private async Task ShowRestartRequiredDialogAsync(string message)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Restart Aero to finish",
            Content = message + " Close every Aero window, then start Aero again.",
            CloseButtonText = "OK"
        };
        await dialog.ShowAsync();
    }

    private static HashSet<string> ReadSelectedItems(JsonElement message) =>
        message.TryGetProperty("items", out JsonElement items) &&
        items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

    private static int FindOptionalColumn(
        IReadOnlyDictionary<string, int> columns,
        params string[] names)
    {
        foreach (string name in names)
        {
            if (columns.TryGetValue(NormalizeCsvColumnName(name), out int index))
            {
                return index;
            }
        }

        return -1;
    }

    private static string? CreateSafeCsvUrl(ReadOnlySpan<char> value)
    {
        // Avoid creating an immutable managed string if a CSV URL embeds userinfo.
        // Such values can contain credentials and are unsupported by Aero Vault.
        return value.IsEmpty || value.IndexOf('@') >= 0
            ? null
            : new string(value);
    }

    private static byte[] EncodeUtf8(ReadOnlySpan<char> value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        byte[] bytes = GC.AllocateUninitializedArray<byte>(byteCount);
        _ = Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    private static int FindColumn(
        IReadOnlyDictionary<string, int> columns,
        params string[] names)
    {
        foreach (string name in names)
        {
            if (columns.TryGetValue(NormalizeCsvColumnName(name), out int index))
            {
                return index;
            }
        }

        throw new InvalidDataException(
            $"The CSV is missing the {names[0]} column.");
    }

    private static bool TryGetColumn(
        IReadOnlyList<string> row,
        int index,
        out string? value)
    {
        value = index >= 0 && index < row.Count ? row[index] : null;
        return value is not null;
    }

    private static string NormalizeCsvColumnName(string? value)
    {
        string name = (value ?? string.Empty).Trim().TrimStart('\uFEFF');
        StringBuilder normalized = new(name.Length);
        foreach (char character in name)
        {
            if (char.IsLetterOrDigit(character))
            {
                normalized.Append(char.ToLowerInvariant(character));
            }
        }
        return normalized.ToString();
    }

    private static char DetectCsvDelimiter(string csv)
    {
        int commas = 0;
        int semicolons = 0;
        int tabs = 0;
        bool quoted = false;

        foreach (char character in csv)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (!quoted && character is '\r' or '\n')
            {
                break;
            }
            if (quoted)
            {
                continue;
            }

            if (character == ',') commas++;
            else if (character == ';') semicolons++;
            else if (character == '\t') tabs++;
        }

        if (tabs > commas && tabs > semicolons)
        {
            return '\t';
        }
        return semicolons > commas ? ';' : ',';
    }

    private static List<IReadOnlyList<string>> ParseCsv(string csv, char delimiter)
    {
        List<IReadOnlyList<string>> rows = [];
        List<string> row = [];
        StringBuilder value = new();
        bool quoted = false;

        for (int index = 0; index < csv.Length; index++)
        {
            char current = csv[index];
            if (quoted)
            {
                if (current == '"' && index + 1 < csv.Length && csv[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else if (current == '"')
                {
                    quoted = false;
                }
                else
                {
                    value.Append(current);
                }
            }
            else if (current == '"')
            {
                quoted = true;
            }
            else if (current == delimiter)
            {
                row.Add(value.ToString());
                value.Clear();
            }
            else if (current is '\r' or '\n')
            {
                if (current == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n')
                {
                    index++;
                }

                row.Add(value.ToString());
                value.Clear();
                if (row.Any(cell => cell.Length > 0))
                {
                    rows.Add(row);
                }
                row = [];
            }
            else
            {
                value.Append(current);
            }
        }

        if (value.Length > 0 || row.Count > 0)
        {
            row.Add(value.ToString());
            if (row.Any(cell => cell.Length > 0))
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private static string SanitizeFileName(string value)
    {
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string cleaned = new(value
            .Where(character => !invalid.Contains(character))
            .Take(50)
            .ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "Profile" : cleaned.Trim();
    }

    private const int MaximumImportedCookieCount = 20_000;

    private sealed record CookieImportRecord(
        string Name,
        string Value,
        string Domain,
        string Path,
        bool IsSecure,
        bool IsHttpOnly,
        DateTimeOffset? ExpiresUtc,
        CoreWebView2CookieSameSiteKind SameSite);

    private sealed record CookieImportBatch(
        List<CookieImportRecord> Cookies,
        int Skipped,
        string Format);

    private sealed record BackupPasswordChoice(bool Encrypt, string Password);
}
