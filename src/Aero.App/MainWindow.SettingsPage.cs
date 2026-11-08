using Aero.App.Data;
using Aero.App.Settings;
using Aero.Search;
using Aero.Extensions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;

namespace Aero.App;

public sealed partial class MainWindow
{
    private const string SettingsAddress = "aero://settings";
    private const string HomeAddress = "aero://home";
    private string? _profileToDeleteOnClose;

    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly HashSet<string> EditableSettingNames =
    [
        nameof(BrowserSettings.AppLanguage),
        nameof(BrowserSettings.SearchEngine),
        nameof(BrowserSettings.CustomSearchUrl),
        nameof(BrowserSettings.HomePageUrl),
        nameof(BrowserSettings.NewTabMode),
        nameof(BrowserSettings.CustomNewTabUrl),
        nameof(BrowserSettings.StartupBehavior),
        nameof(BrowserSettings.RestorePreviousSession),
        nameof(BrowserSettings.OpenLinksBehavior),
        nameof(BrowserSettings.ThemeMode),
        nameof(BrowserSettings.AccentColor),
        nameof(BrowserSettings.InterfaceDensity),
        nameof(BrowserSettings.ReduceMotion),
        nameof(BrowserSettings.TabSize),
        nameof(BrowserSettings.TabBarHeight),
        nameof(BrowserSettings.TabBarBackgroundStyle),
        nameof(BrowserSettings.TabBarBackgroundColor),
        nameof(BrowserSettings.TabBarBackgroundOpacity),
        nameof(BrowserSettings.TabBarBlur),
        nameof(BrowserSettings.TabShape),
        nameof(BrowserSettings.TabCornerRadius),
        nameof(BrowserSettings.TabHorizontalPadding),
        nameof(BrowserSettings.TabSpacing),
        nameof(BrowserSettings.TabStripPadding),
        nameof(BrowserSettings.PinnedTabWidth),
        nameof(BrowserSettings.TabSurfaceStyle),
        nameof(BrowserSettings.TabActiveColor),
        nameof(BrowserSettings.TabHoverColor),
        nameof(BrowserSettings.TabInactiveColor),
        nameof(BrowserSettings.TabActiveOpacity),
        nameof(BrowserSettings.TabHoverOpacity),
        nameof(BrowserSettings.TabInactiveOpacity),
        nameof(BrowserSettings.TabBorderThickness),
        nameof(BrowserSettings.TabBorderOpacity),
        nameof(BrowserSettings.TabAnimation),
        nameof(BrowserSettings.TabAnimationDuration),
        nameof(BrowserSettings.TabHoverLift),
        nameof(BrowserSettings.TabInactiveScalePercent),
        nameof(BrowserSettings.TabIconSize),
        nameof(BrowserSettings.TabFontSize),
        nameof(BrowserSettings.TabBoldActiveText),
        nameof(BrowserSettings.TabUseAccentText),
        nameof(BrowserSettings.TabCloseButtonMode),
        nameof(BrowserSettings.ShowTabSeparators),
        nameof(BrowserSettings.TabActiveIndicator),
        nameof(BrowserSettings.TabIndicatorThickness),
        nameof(BrowserSettings.TabTextAlignment),
        nameof(BrowserSettings.HomeBackgroundStyle),
        nameof(BrowserSettings.HomeBackgroundColor),
        nameof(BrowserSettings.HomeBackgroundSecondaryColor),
        nameof(BrowserSettings.HomeBackgroundOpacity),
        nameof(BrowserSettings.HomeBackgroundBlur),
        nameof(BrowserSettings.HomeBackgroundImageFit),
        nameof(BrowserSettings.HomeBackgroundAdaptiveOverlay),
        nameof(BrowserSettings.ShowHomeButton),
        nameof(BrowserSettings.ShowQuickLinks),
        nameof(BrowserSettings.ShowBookmarksBar),
        nameof(BrowserSettings.DefaultDownloadFolder),
        nameof(BrowserSettings.AskDownloadPathEveryTime),
        nameof(BrowserSettings.OpenDownloadsPanelAutomatically),
        nameof(BrowserSettings.WarnDangerousFiles),
        nameof(BrowserSettings.AutomaticBackupsEnabled),
        nameof(BrowserSettings.AutomaticBackupIntervalHours),
        nameof(BrowserSettings.BackupRetentionCount),
        nameof(BrowserSettings.HistoryEnabled),
        nameof(BrowserSettings.CookiesEnabled),
        nameof(BrowserSettings.BlockThirdPartyCookies),
        nameof(BrowserSettings.CacheEnabled),
        nameof(BrowserSettings.ClearDataOnClose),
        nameof(BrowserSettings.ClearHistoryOnClose),
        nameof(BrowserSettings.ClearCookiesOnClose),
        nameof(BrowserSettings.ClearCacheOnClose),
        nameof(BrowserSettings.ClearSiteDataOnClose),
        nameof(BrowserSettings.PrivacySiteExceptions),
        nameof(BrowserSettings.ThirdPartyCookieExceptions),
        nameof(BrowserSettings.TemporaryCookieSites),
        nameof(BrowserSettings.DoNotTrack),
        nameof(BrowserSettings.HttpsFirst),
        nameof(BrowserSettings.BlockAdsAndTrackers),
        nameof(BrowserSettings.ShieldMode),
        nameof(BrowserSettings.ShieldEngineMode),
        nameof(BrowserSettings.ShieldCosmeticFiltering),
        nameof(BrowserSettings.ShieldAutoUpdate),
        nameof(BrowserSettings.ShieldYouTubeCleanMode),
        nameof(BrowserSettings.CleanTrackingLinks),
        nameof(BrowserSettings.ShieldAllowlist),
        nameof(BrowserSettings.ShieldCustomRules),
        nameof(BrowserSettings.MemorySaverMode),
        nameof(BrowserSettings.SleepBackgroundTabs),
        nameof(BrowserSettings.BackgroundTabSleepSeconds),
        nameof(BrowserSettings.BackgroundTabDiscardMinutes),
        nameof(BrowserSettings.MemoryLimitMb),
        nameof(BrowserSettings.KeepAudioTabsAwake),
        nameof(BrowserSettings.KeepDownloadingTabsAwake),
        nameof(BrowserSettings.ProtectFilledForms),
        nameof(BrowserSettings.MemorySaverSiteExceptions),
        nameof(BrowserSettings.ResourceBudgetEnabled),
        nameof(BrowserSettings.BackgroundCpuBudgetPercent),
        nameof(BrowserSettings.BackgroundMemoryBudgetMb),
        nameof(BrowserSettings.LockedSites),
        nameof(BrowserSettings.WipeLockedSiteSessions),
        nameof(BrowserSettings.ProtectCookiesWithHello),
        nameof(BrowserSettings.CheckForUpdates),
        nameof(BrowserSettings.CameraPermissionDefault),
        nameof(BrowserSettings.MicrophonePermissionDefault),
        nameof(BrowserSettings.GeolocationPermissionDefault),
        nameof(BrowserSettings.NotificationsPermissionDefault),
        nameof(BrowserSettings.PopupsPermissionDefault),
        nameof(BrowserSettings.RedirectsPermissionDefault),
        nameof(BrowserSettings.ClipboardPermissionDefault),
        nameof(BrowserSettings.MidiPermissionDefault),
        nameof(BrowserSettings.AutomaticDownloadsPermissionDefault)
    ];

    private async void ShowSettingsPage(BrowserTabState state)
    {
        if (state.IsDiscarded)
        {
            state.PendingUri = new Uri(SettingsAddress);
            state.PendingStartPage = false;
            state.InternalPageAddress = SettingsAddress;
            state.IsStartPage = false;
            state.CurrentUri = new Uri(SettingsAddress);
            await RestoreDiscardedTabAsync(state);
            return;
        }

        state.PendingUri = null;
        state.PendingStartPage = false;
        state.InternalPageAddress = SettingsAddress;
        state.IsStartPage = false;
        state.CurrentUri = new Uri(SettingsAddress);
        state.Title = "Settings";
        state.TitleText.Text = DisplayTabTitle("Settings");
        state.FaviconImage.Source = null;
        state.FaviconFallbackAttempted = false;
        state.AudioButton.Visibility = Visibility.Collapsed;
        UpdateTabIconPresentation(state);
        state.Icon.Glyph = "\uE713";
        ToolTipService.SetToolTip(state.Header, DisplayTabTitle("Aero Settings"));

        await RefreshInstalledExtensionsAsync();
        if (!_tabs.Contains(state) ||
            !string.Equals(state.InternalPageAddress, SettingsAddress, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        state.Browser.NavigateToString(BuildSettingsPageHtml());
        UpdateActiveChrome();
        SaveSessionSnapshot();
    }

    private bool TryNavigateInternalPage(BrowserTabState state, string? address)
    {
        string normalized = address?.Trim().TrimEnd('/') ?? string.Empty;
        if (normalized.Equals(SettingsAddress, StringComparison.OrdinalIgnoreCase))
        {
            ShowSettingsPage(state);
            return true;
        }

        if (normalized.Equals(HomeAddress, StringComparison.OrdinalIgnoreCase))
        {
            ShowAeroHomePage(state);
            return true;
        }

        return false;
    }

    private void NavigateToConfiguredAddress(BrowserTabState state, string? address)
    {
        string target = string.IsNullOrWhiteSpace(address) ? HomeAddress : address.Trim();
        if (TryNavigateInternalPage(state, target))
        {
            return;
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) && IsBrowsableUri(uri))
        {
            Navigate(state, uri);
        }
        else
        {
            ShowAeroHomePage(state);
        }
    }

    private Uri BuildSearchUri(string query) =>
        SearchUriBuilder.Build(
            query,
            _settings.SearchEngine,
            _settings.CustomSearchUrl);

    private Uri UpgradeToHttpsIfNeeded(Uri uri)
    {
        if (!_settings.HttpsFirst ||
            !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            uri.IsLoopback ||
            IsLoopbackHost(uri.Host) ||
            IsPrivacyException(uri.Host))
        {
            return uri;
        }

        try
        {
            return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        }
        catch
        {
            return uri;
        }
    }

    private bool IsPrivacyException(string? host) =>
        IsLoopbackHost(host) ||
        HostMatchesAny(host, _settings.PrivacySiteExceptions);

    private static bool HostMatchesAny(string? host, IEnumerable<string> entries)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return entries.Any(entry =>
            host.Equals(entry, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith('.' + entry, StringComparison.OrdinalIgnoreCase));
    }

    private string BuildSettingsPageHtml()
    {
        IReadOnlyList<SitePermissionSnapshot> permissions = _sitePermissionStore.Snapshot();
        IReadOnlyList<BrowserProfile> profiles = BrowserProfileManager.ListProfiles();
        object model = new
        {
            settings = _settings,
            profile = _browserProfile,
            profiles,
            profileIcons = BrowserProfileManager.AvailableIcons,
            permissions,
            extensions = _installedExtensions
                .Where(extension => !IsBuiltInShieldExtension(extension))
                .Select(extension =>
                {
                    ExtensionPackage? package = GetManagedExtensionPackage(extension);
                    return new
                    {
                        extension.Id,
                        extension.Name,
                        extension.IsEnabled,
                        IsPinned = _extensionRegistry.IsPinned(extension.Id),
                        Permissions = package?.Permissions ?? [],
                        HasHostAccess = HasExtensionHostAccess(package),
                        AllSitesAccess = _extensionRegistry.IsAllSitesAccessEnabled(extension.Id),
                        HasUserScriptsPermission = HasUserScriptsPermission(package),
                        UserScriptsAllowed = _extensionRegistry.IsUserScriptsEnabled(extension.Id)
                    };
                }),
            shield = new
            {
                blockedSession = Interlocked.Read(ref _shieldBlockedSessionTotal),
                engine = IsUsingUBlockOriginEngine() ? "uBlock Origin + native" : "Native",
                networkRules = _requestBlocker.NetworkRuleCount,
                cosmeticRules = _requestBlocker.CosmeticRuleCount,
                updatedAt = _requestBlocker.LastUpdatedUtc,
                lastError = _requestBlocker.LastError
            },
            memorySaver = BuildMemorySaverOverview(),
            backups = ProfileBackupService.ListAutomaticBackups(_browserProfile.StorageRoot)
                .Select(backup => new
                {
                    backup.FileName,
                    backup.CreatedAt,
                    backup.Size
                }),
            isPrivate = _isPrivateWindow
        };

        string json = JsonSerializer.Serialize(model, SettingsJsonOptions);
        string html = SettingsPageTemplate
            .Replace("__INITIAL_STATE__", json, StringComparison.Ordinal)
            .Replace("__LANG__", Localization.AeroLocalizer.Language, StringComparison.Ordinal)
            .Replace("__APP_VERSION__", typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "Aero", StringComparison.Ordinal);
        if (Localization.AeroLocalizer.Language == "en")
        {
            html = Localization.AeroLocalizer.TranslateDocument(html);
        }

        // Inserted after translation so text replacement never scans the font payload.
        return html.Replace(AeroFontFaceToken, GetAeroFontFaceCss(), StringComparison.Ordinal);
    }

    private bool TryHandleSettingsMessage(BrowserTabState state, JsonElement root, string messageType)
    {
        bool isSettingsPage =
            string.Equals(state.InternalPageAddress, SettingsAddress, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state.CurrentUri?.AbsoluteUri, SettingsAddress, StringComparison.OrdinalIgnoreCase);
        if (!isSettingsPage ||
            !messageType.StartsWith("aero-settings-", StringComparison.Ordinal))
        {
            return false;
        }

        JsonElement message = root.Clone();
        RootGrid.DispatcherQueue.TryEnqueue(async () => await HandleSettingsMessageAsync(state, message));
        return true;
    }

    private async Task HandleSettingsMessageAsync(BrowserTabState state, JsonElement message)
    {
        string type = ReadString(message, "type");
        try
        {
            switch (type)
            {
                case "aero-settings-set":
                    string changedSetting = ApplySettingMessage(message);
                    await PostSettingsStatusAsync(
                        state,
                        string.IsNullOrWhiteSpace(changedSetting)
                            ? "Setting was not changed."
                            : $"{changedSetting} saved.",
                        isError: string.IsNullOrWhiteSpace(changedSetting));
                    if (changedSetting == nameof(BrowserSettings.AppLanguage))
                    {
                        ShowSettingsPage(state);
                    }
                    else if (changedSetting == nameof(BrowserSettings.ProtectCookiesWithHello))
                    {
                        await OnCookieVaultSettingChangedAsync();
                    }
                    break;
                case "aero-settings-action":
                    await HandleSettingsActionAsync(state, ReadString(message, "action"), message);
                    break;
                case "aero-settings-extension":
                    await HandleExtensionSettingsActionAsync(
                        state,
                        ReadString(message, "action"),
                        ReadString(message, "id"));
                    break;
                case "aero-settings-profile":
                    await HandleProfileSettingsActionAsync(
                        ReadString(message, "action"),
                        ReadString(message, "id"),
                        ReadString(message, "name"),
                        ReadString(message, "icon"));
                    break;
            }
        }
        catch (Exception exception)
        {
            await PostSettingsStatusAsync(state, exception.Message, isError: true);
        }
    }

    private string ApplySettingMessage(JsonElement message)
    {
        string name = ReadString(message, "name");
        if (!EditableSettingNames.Contains(name) ||
            !message.TryGetProperty("value", out JsonElement value))
        {
            return string.Empty;
        }

        PropertyInfo? property = typeof(BrowserSettings).GetProperty(name);
        if (property?.CanWrite != true)
        {
            return string.Empty;
        }

        object? converted = JsonSerializer.Deserialize(
            value.GetRawText(),
            property.PropertyType,
            SettingsJsonOptions);
        property.SetValue(_settings, converted);
        _settings.Save();
        ApplyAllRuntimeSettings(name);
        return name;
    }

    private void ApplyAllRuntimeSettings(string changedSetting)
    {
        if (changedSetting == nameof(BrowserSettings.AppLanguage))
        {
            Localization.AeroLocalizer.Configure(_settings.AppLanguage);
            ApplyLocalization();
        }

        ApplyTheme();
        ApplyProfileSettingsUi();
        ApplyPasswordAndAutofillSettingsToAllTabs();
        if (changedSetting.StartsWith("Memory", StringComparison.Ordinal) ||
            changedSetting is nameof(BrowserSettings.SleepBackgroundTabs) or
                nameof(BrowserSettings.BackgroundTabSleepSeconds) or
                nameof(BrowserSettings.BackgroundTabDiscardMinutes) or
                nameof(BrowserSettings.KeepAudioTabsAwake) or
                nameof(BrowserSettings.KeepDownloadingTabsAwake) or
                nameof(BrowserSettings.ProtectFilledForms))
        {
            ApplyMemorySaverSettings();
        }
        else
        {
            UpdateMemorySaverStatusText();
        }

        if (changedSetting.StartsWith("Shield", StringComparison.Ordinal) ||
            changedSetting is nameof(BrowserSettings.BlockAdsAndTrackers) or
                nameof(BrowserSettings.CleanTrackingLinks))
        {
            _requestBlocker.ApplyCustomRules(_settings.ShieldCustomRules);
            _ = RefreshShieldAfterSettingsChangeAsync();
        }

        if (changedSetting is nameof(BrowserSettings.NewTabMode) or
            nameof(BrowserSettings.ShowQuickLinks) or
            nameof(BrowserSettings.ThemeMode) or
            nameof(BrowserSettings.ReduceMotion) or
            nameof(BrowserSettings.SearchEngine) or
            nameof(BrowserSettings.CustomSearchUrl) or
            nameof(BrowserSettings.HomeBackgroundStyle) or
            nameof(BrowserSettings.HomeBackgroundColor) or
            nameof(BrowserSettings.HomeBackgroundSecondaryColor) or
            nameof(BrowserSettings.HomeBackgroundOpacity) or
            nameof(BrowserSettings.HomeBackgroundBlur) or
            nameof(BrowserSettings.HomeBackgroundImageFit) or
            nameof(BrowserSettings.HomeBackgroundAdaptiveOverlay))
        {
            RefreshStartPages();
        }

        UpdateActiveChrome();
        SaveSessionSnapshot();
    }

    private async Task RefreshShieldAfterSettingsChangeAsync()
    {
        await ApplyShieldEngineModeAsync();

        foreach (BrowserTabState tab in _tabs.Where(tab => !tab.IsStartPage))
        {
            await ApplyCosmeticFilteringAsync(tab);
        }

        UpdateSiteIndicator(ActiveTab?.CurrentUri ?? ActiveTab?.PendingUri);
        UpdateShieldUi();
    }

    private async Task HandleSettingsActionAsync(
        BrowserTabState state,
        string action,
        JsonElement message)
    {
        switch (action)
        {
            case "choose-download-folder":
                await ChooseDownloadFolderFromSettingsAsync(state);
                break;
            case "open-downloads":
                OpenDownloadsFolder();
                break;
            case "choose-home-background":
                await ChooseHomeBackgroundImageAsync(state);
                break;
            case "reset-home-background":
                await ResetHomeBackgroundAsync(state);
                break;
            case "tab-style-preset":
                await ApplyTabStylePresetAsync(state, ReadString(message, "preset"));
                break;
            case "reset-permissions":
                _sitePermissionStore.ResetAll();
                await RefreshSettingsPageAsync(state, "Site permissions reset.");
                break;
            case "view-passwords":
                await ShowSavedSignInsAsync();
                break;
            case "clear-data":
                await ClearSelectedBrowsingDataAsync(state, message);
                break;
            case "memory-freeze":
                await RunMemorySaverCycleAsync(forceImmediate: true);
                await RefreshSettingsPageAsync(
                    state,
                    MemorySaverStatusText.Text,
                    "memory");
                break;
            case "shield-refresh":
                await _requestBlocker.RefreshAsync(
                    _settings.ShieldCustomRules,
                    _shieldCancellation.Token);
                await RefreshSettingsPageAsync(state, "Shield filter lists refreshed.");
                break;
            case "install-extension":
                OpenExtensionsPane();
                await PostSettingsStatusAsync(
                    state,
                    "Use “Load unpacked” or import from Chrome in the extensions panel.");
                break;
            default:
                await HandleDataSettingsActionAsync(state, action, message);
                break;
        }
    }


    private async Task ChooseDownloadFolderFromSettingsAsync(BrowserTabState state)
    {
        FolderPicker picker = new() { CommitButtonText = "Use this folder" };
        picker.FileTypeFilter.Add("*");
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        _settings.DefaultDownloadFolder = folder.Path;
        _settings.Save();
        await RefreshSettingsPageAsync(state, $"Downloads will be saved to {folder.Path}.");
    }

    private async Task ChooseHomeBackgroundImageAsync(BrowserTabState state)
    {
        FileOpenPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail
        };
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".webp");
        picker.FileTypeFilter.Add(".bmp");

        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
        StorageFile? selected = await picker.PickSingleFileAsync();
        if (selected is null)
        {
            return;
        }

        Windows.Storage.FileProperties.BasicProperties properties =
            await selected.GetBasicPropertiesAsync();
        if (properties.Size is 0 or > 32UL * 1024 * 1024)
        {
            await PostSettingsStatusAsync(
                state,
                "Choose an image smaller than 32 MB.",
                isError: true);
            return;
        }

        string extension = selected.FileType.ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp"))
        {
            await PostSettingsStatusAsync(
                state,
                "Aero supports PNG, JPG, WEBP and BMP backgrounds.",
                isError: true);
            return;
        }

        string folder = Path.Combine(_browserProfile.StorageRoot, "Customization");
        Directory.CreateDirectory(folder);
        string destinationPath = Path.Combine(folder, "home-background" + extension);
        string temporaryPath = Path.Combine(folder, ".home-background-import.tmp");
        TryDeleteFile(temporaryPath);

        await using (Stream source = await selected.OpenStreamForReadAsync())
        await using (FileStream destination = new(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true))
        {
            await source.CopyToAsync(destination);
        }

        foreach (string oldFile in Directory.EnumerateFiles(folder, "home-background.*"))
        {
            TryDeleteFile(oldFile);
        }
        File.Move(temporaryPath, destinationPath, overwrite: true);

        _settings.HomeBackgroundImagePath = destinationPath;
        _settings.HomeBackgroundStyle = HomeBackgroundStyle.Image;
        _settings.Save();
        ApplyAllRuntimeSettings(nameof(BrowserSettings.HomeBackgroundStyle));
        await RefreshSettingsPageAsync(
            state,
            "Aero Home background updated.",
            "appearance");
    }

    private async Task ResetHomeBackgroundAsync(BrowserTabState state)
    {
        string folder = Path.Combine(_browserProfile.StorageRoot, "Customization");
        if (Directory.Exists(folder))
        {
            foreach (string oldFile in Directory.EnumerateFiles(folder, "home-background.*"))
            {
                TryDeleteFile(oldFile);
            }
        }

        _settings.HomeBackgroundStyle = HomeBackgroundStyle.Default;
        _settings.HomeBackgroundColor = "#17212B";
        _settings.HomeBackgroundSecondaryColor = "#35506A";
        _settings.HomeBackgroundOpacity = 100;
        _settings.HomeBackgroundBlur = 0;
        _settings.HomeBackgroundImageFit = HomeBackgroundImageFit.Cover;
        _settings.HomeBackgroundImagePath = string.Empty;
        _settings.HomeBackgroundAdaptiveOverlay = true;
        _settings.Save();
        ApplyAllRuntimeSettings(nameof(BrowserSettings.HomeBackgroundStyle));
        await RefreshSettingsPageAsync(
            state,
            "Aero Home background reset.",
            "appearance");
    }

    private async Task ApplyTabStylePresetAsync(BrowserTabState state, string preset)
    {
        // Presets only touch tab-strip appearance. The user can keep editing every
        // value afterwards, so they are starting points rather than locked themes.
        switch (preset.ToLowerInvariant())
        {
            case "glass":
                _settings.TabShape = TabShapeStyle.Rounded;
                _settings.TabCornerRadius = 15;
                _settings.TabSurfaceStyle = TabSurfaceStyle.Acrylic;
                _settings.TabBarBlur = TabBarBlurStyle.Strong;
                _settings.TabBarBackgroundStyle = TabBarBackgroundStyle.Gradient;
                _settings.TabSpacing = 5;
                _settings.TabStripPadding = 6;
                _settings.TabActiveOpacity = 78;
                _settings.TabHoverOpacity = 62;
                _settings.TabInactiveOpacity = 20;
                _settings.TabBorderOpacity = 48;
                _settings.TabHoverLift = 2;
                _settings.TabInactiveScalePercent = 98;
                _settings.TabAnimation = TabAnimationStyle.Bounce;
                _settings.TabAnimationDuration = 210;
                _settings.TabActiveIndicator = TabActiveIndicatorStyle.Bottom;
                _settings.ShowTabSeparators = false;
                break;
            case "pill":
                _settings.TabShape = TabShapeStyle.Pill;
                _settings.TabSurfaceStyle = TabSurfaceStyle.Accent;
                _settings.TabBarBlur = TabBarBlurStyle.Soft;
                _settings.TabSpacing = 7;
                _settings.TabStripPadding = 8;
                _settings.TabHorizontalPadding = 13;
                _settings.TabActiveOpacity = 94;
                _settings.TabHoverOpacity = 72;
                _settings.TabInactiveOpacity = 8;
                _settings.TabBorderThickness = 1;
                _settings.TabBorderOpacity = 34;
                _settings.TabHoverLift = 2;
                _settings.TabInactiveScalePercent = 97;
                _settings.TabAnimation = TabAnimationStyle.Scale;
                _settings.TabAnimationDuration = 170;
                _settings.TabActiveIndicator = TabActiveIndicatorStyle.None;
                _settings.ShowTabSeparators = false;
                break;
            case "minimal":
                _settings.TabShape = TabShapeStyle.Custom;
                _settings.TabCornerRadius = 7;
                _settings.TabSurfaceStyle = TabSurfaceStyle.Theme;
                _settings.TabBarBlur = TabBarBlurStyle.Off;
                _settings.TabSpacing = 1;
                _settings.TabStripPadding = 0;
                _settings.TabHorizontalPadding = 8;
                _settings.TabActiveOpacity = 100;
                _settings.TabHoverOpacity = 100;
                _settings.TabInactiveOpacity = 0;
                _settings.TabBorderThickness = 0;
                _settings.TabHoverLift = 0;
                _settings.TabInactiveScalePercent = 100;
                _settings.TabAnimation = TabAnimationStyle.Subtle;
                _settings.TabAnimationDuration = 120;
                _settings.TabActiveIndicator = TabActiveIndicatorStyle.Bottom;
                _settings.TabIndicatorThickness = 2;
                _settings.TabCloseButtonMode = TabCloseButtonMode.ActiveOnly;
                _settings.ShowTabSeparators = false;
                break;
            case "neon":
                _settings.TabShape = TabShapeStyle.Rounded;
                _settings.TabCornerRadius = 13;
                _settings.TabSurfaceStyle = TabSurfaceStyle.Gradient;
                _settings.TabBarBlur = TabBarBlurStyle.Strong;
                _settings.TabSpacing = 5;
                _settings.TabStripPadding = 7;
                _settings.TabActiveOpacity = 92;
                _settings.TabHoverOpacity = 70;
                _settings.TabInactiveOpacity = 12;
                _settings.TabBorderThickness = 2;
                _settings.TabBorderOpacity = 88;
                _settings.TabUseAccentText = true;
                _settings.TabBoldActiveText = true;
                _settings.TabHoverLift = 3;
                _settings.TabInactiveScalePercent = 96;
                _settings.TabAnimation = TabAnimationStyle.Bounce;
                _settings.TabAnimationDuration = 230;
                _settings.TabActiveIndicator = TabActiveIndicatorStyle.Outline;
                _settings.ShowTabSeparators = false;
                break;
            default:
                _settings.TabShape = TabShapeStyle.Connected;
                _settings.TabBarHeight = 40;
                _settings.TabCornerRadius = 8;
                _settings.TabHorizontalPadding = 9;
                _settings.TabSpacing = 2;
                _settings.TabStripPadding = 0;
                _settings.PinnedTabWidth = 46;
                _settings.TabSurfaceStyle = TabSurfaceStyle.Theme;
                _settings.TabActiveColor = "#2A3036";
                _settings.TabHoverColor = "#22282D";
                _settings.TabInactiveColor = "#17212B";
                _settings.TabActiveOpacity = 100;
                _settings.TabHoverOpacity = 100;
                _settings.TabInactiveOpacity = 0;
                _settings.TabBorderThickness = 0;
                _settings.TabBorderOpacity = 100;
                _settings.TabBarBlur = TabBarBlurStyle.Off;
                _settings.TabAnimation = TabAnimationStyle.Subtle;
                _settings.TabAnimationDuration = 180;
                _settings.TabHoverLift = 0;
                _settings.TabInactiveScalePercent = 100;
                _settings.TabIconSize = 16;
                _settings.TabFontSize = 12;
                _settings.TabBoldActiveText = false;
                _settings.TabUseAccentText = false;
                _settings.TabCloseButtonMode = TabCloseButtonMode.HoverOrActive;
                _settings.ShowTabSeparators = true;
                _settings.TabActiveIndicator = TabActiveIndicatorStyle.None;
                _settings.TabIndicatorThickness = 2;
                _settings.TabTextAlignment = TabTextAlignmentMode.Left;
                break;
        }

        _settings.Save();
        ApplyAllRuntimeSettings(nameof(BrowserSettings.TabShape));
        await RefreshSettingsPageAsync(
            state,
            "Tab style applied. Every value remains editable.",
            "appearance");
    }

    private async Task ClearSelectedBrowsingDataAsync(BrowserTabState state, JsonElement message)
    {
        HashSet<string> selected = message.TryGetProperty("items", out JsonElement items) &&
            items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];

        DateTime start = ReadString(message, "range") switch
        {
            "hour" => DateTime.UtcNow.AddHours(-1),
            "day" => DateTime.UtcNow.AddDays(-1),
            "week" => DateTime.UtcNow.AddDays(-7),
            "month" => DateTime.UtcNow.AddDays(-30),
            _ => DateTime.UnixEpoch
        };

        CoreWebView2BrowsingDataKinds kinds = (CoreWebView2BrowsingDataKinds)0;
        if (selected.Contains("history"))
        {
            kinds |= CoreWebView2BrowsingDataKinds.BrowsingHistory;
            _dataStore.ClearHistorySince(new DateTimeOffset(start, TimeSpan.Zero));
        }
        if (selected.Contains("cookies")) kinds |= CoreWebView2BrowsingDataKinds.Cookies;
        if (selected.Contains("cache")) kinds |= CoreWebView2BrowsingDataKinds.DiskCache;
        if (selected.Contains("localStorage")) kinds |= CoreWebView2BrowsingDataKinds.LocalStorage;
        if (selected.Contains("indexedDb")) kinds |= CoreWebView2BrowsingDataKinds.IndexedDb;
        if (selected.Contains("permissions"))
        {
            kinds |= CoreWebView2BrowsingDataKinds.Settings;
            _sitePermissionStore.ResetAll();
        }
        if (selected.Contains("passwords"))
        {
            kinds |= CoreWebView2BrowsingDataKinds.PasswordAutosave;
            IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            await _passwordVaultService.ClearAsync(windowHandle);
        }
        if (selected.Contains("downloads"))
        {
            kinds |= CoreWebView2BrowsingDataKinds.DownloadHistory;
            RemoveInactiveDownloads();
        }

        CoreWebView2Profile? profile = GetActiveProfile();
        if (profile is not null && kinds != 0)
        {
            await profile.ClearBrowsingDataAsync(kinds, start, DateTime.UtcNow);
        }

        await RefreshSettingsPageAsync(state, "Selected browsing data cleared.");
    }

    private async Task HandleExtensionSettingsActionAsync(
        BrowserTabState state,
        string action,
        string extensionId)
    {
        await RefreshInstalledExtensionsAsync();
        CoreWebView2BrowserExtension? extension = _installedExtensions.FirstOrDefault(item =>
            item.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase));
        if (extension is null)
        {
            await RefreshSettingsPageAsync(state, "Extension is no longer installed.");
            return;
        }

        switch (action)
        {
            case "toggle":
                await extension.EnableAsync(!extension.IsEnabled);
                break;
            case "pin":
                _extensionRegistry.SetPinned(extension.Id, !_extensionRegistry.IsPinned(extension.Id));
                break;
            case "all-sites-on":
                _extensionRegistry.SetAllSitesAccessEnabled(extension.Id, true);
                await RefreshSettingsPageAsync(
                    state,
                    "Site access updated. Restart Aero to apply the extension permission change.",
                    "extensions");
                return;
            case "all-sites-off":
                _extensionRegistry.SetAllSitesAccessEnabled(extension.Id, false);
                await RefreshSettingsPageAsync(
                    state,
                    "Site access updated. Restart Aero to apply the extension permission change.",
                    "extensions");
                return;
            case "user-scripts-on":
                _extensionRegistry.SetUserScriptsEnabled(extension.Id, true);
                await RefreshSettingsPageAsync(
                    state,
                    "User scripts enabled. Restart Aero to apply the extension permission change.",
                    "extensions");
                return;
            case "user-scripts-off":
                _extensionRegistry.SetUserScriptsEnabled(extension.Id, false);
                await RefreshSettingsPageAsync(
                    state,
                    "User scripts disabled. Restart Aero to apply the extension permission change.",
                    "extensions");
                return;
            case "open":
                await OpenExtensionPageAsync(extension);
                return;
            case "remove":
                await extension.RemoveAsync();
                TryDeleteManagedExtensionFolder(_extensionRegistry.Remove(extension.Id));
                break;
        }

        await RefreshExtensionToolbarAsync();
        await RefreshSettingsPageAsync(state, "Extension settings updated.");
    }

    private async Task HandleProfileSettingsActionAsync(
        string action,
        string profileId,
        string name,
        string icon)
    {
        switch (action)
        {
            case "create":
            {
                BrowserProfile created = BrowserProfileManager.Create(name, icon);
                await SwitchToProfileAsync(created);
                break;
            }
            case "update":
            {
                BrowserProfile? updated = BrowserProfileManager.Update(
                    string.IsNullOrWhiteSpace(profileId) ? _browserProfile.Id : profileId,
                    name,
                    icon);
                if (updated is not null && updated.Id.Equals(_browserProfile.Id, StringComparison.OrdinalIgnoreCase))
                {
                    await SwitchToProfileAsync(updated);
                }
                break;
            }
            case "switch":
            {
                BrowserProfile? target = BrowserProfileManager.Find(profileId);
                if (target is not null)
                {
                    await SwitchToProfileAsync(target);
                }
                break;
            }
            case "delete":
                if (profileId.Equals(_browserProfile.Id, StringComparison.OrdinalIgnoreCase))
                {
                    if (OpenWindows.Any(window =>
                        !ReferenceEquals(window, this) &&
                        window._browserProfile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (ActiveTab is BrowserTabState busyCurrentSettingsTab)
                        {
                            await PostSettingsStatusAsync(
                                busyCurrentSettingsTab,
                                "Close the other windows of this profile before deleting it.",
                                isError: true);
                        }
                        return;
                    }

                    BrowserProfile fallback = BrowserProfileManager.Find(BrowserProfileManager.DefaultProfileId)!;
                    BrowserProfileManager.SetActive(fallback.Id);
                    _profileToDeleteOnClose = profileId;
                    await SwitchToProfileAsync(fallback);
                }
                else
                {
                    if (OpenWindows.Any(window =>
                        window._browserProfile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (ActiveTab is BrowserTabState busySettingsTab)
                        {
                            await PostSettingsStatusAsync(
                                busySettingsTab,
                                "Close every window of this profile before deleting it.",
                                isError: true);
                        }
                        return;
                    }

                    BrowserProfileManager.Delete(profileId);
                    if (ActiveTab is BrowserTabState settingsTab)
                    {
                        await RefreshSettingsPageAsync(settingsTab, "Profile deleted.");
                    }
                }
                break;
            case "guest":
                await SwitchToProfileAsync(BrowserProfileManager.CreateGuest());
                break;
        }
    }

    private async Task SwitchToProfileAsync(BrowserProfile target)
    {
        if (!target.IsGuest)
        {
            BrowserProfileManager.SetActive(target.Id);
        }

        MainWindow window = new(
            isPrivate: false,
            browserProfile: target,
            startupTransfer: null,
            sharedPrivateUserDataFolder: null);
        window.Activate();
        await Task.Yield();
        Close();
    }

    private async Task RefreshSettingsPageAsync(
        BrowserTabState state,
        string status,
        string? section = null)
    {
        await RefreshInstalledExtensionsAsync();
        if (_tabs.Contains(state) &&
            string.Equals(state.InternalPageAddress, SettingsAddress, StringComparison.OrdinalIgnoreCase))
        {
            state.Browser.NavigateToString(BuildSettingsPageHtml());
            if (!string.IsNullOrWhiteSpace(section))
            {
                await Task.Delay(120);
                string sectionJson = JsonSerializer.Serialize(section, SettingsJsonOptions);
                try
                {
                    await state.Browser.CoreWebView2.ExecuteScriptAsync(
                        $"show({sectionJson});");
                }
                catch
                {
                    // The settings tab may close while the refreshed document loads.
                }
            }
            await PostSettingsStatusAsync(state, status);
        }
    }

    private static async Task PostSettingsStatusAsync(
        BrowserTabState state,
        string status,
        bool isError = false)
    {
        try
        {
            if (state.Browser.CoreWebView2 is null)
            {
                return;
            }

            await Task.Delay(120);
            string json = JsonSerializer.Serialize(new { status, isError }, SettingsJsonOptions);
            await state.Browser.CoreWebView2.ExecuteScriptAsync(
                $"window.aeroSettingsStatus?.({json});");
        }
        catch
        {
            // A tab can navigate or close before a transient status is displayed.
        }
    }

    private SitePermissionDecision GetDefaultPermissionDecision(SitePermissionKind kind)
    {
        PermissionDefault value = kind switch
        {
            SitePermissionKind.Camera => _settings.CameraPermissionDefault,
            SitePermissionKind.Microphone => _settings.MicrophonePermissionDefault,
            SitePermissionKind.Geolocation => _settings.GeolocationPermissionDefault,
            SitePermissionKind.Notifications => _settings.NotificationsPermissionDefault,
            SitePermissionKind.Popups => _settings.PopupsPermissionDefault,
            SitePermissionKind.Redirects => _settings.RedirectsPermissionDefault,
            SitePermissionKind.Clipboard => _settings.ClipboardPermissionDefault,
            SitePermissionKind.Midi => _settings.MidiPermissionDefault,
            SitePermissionKind.AutomaticDownloads => _settings.AutomaticDownloadsPermissionDefault,
            _ => PermissionDefault.Ask
        };

        return value switch
        {
            PermissionDefault.Allow => SitePermissionDecision.Allow,
            PermissionDefault.Block => SitePermissionDecision.Block,
            _ => SitePermissionDecision.Ask
        };
    }

    private void ApplyProfileSettingsUi()
    {
        if (HomeButton is null || BookmarksBar is null)
        {
            return;
        }

        RefreshProfileSwitcher();

        HomeButton.Visibility = _settings.ShowHomeButton && !_compactNavigation
            ? Visibility.Visible
            : Visibility.Collapsed;
        ToolTipService.SetToolTip(HomeButton, $"Home · {_settings.HomePageUrl}");
        AddressBox.PlaceholderText = SearchUriBuilder.GetAddressBarPlaceholder(
            _settings.SearchEngine);
        SolidColorBrush accentBrush = new(GetConfiguredAccentColor());
        PageProgress.Foreground = accentBrush;
        SplitLeftActiveOutline.BorderBrush = accentBrush;
        SplitRightActiveOutline.BorderBrush = accentBrush;

        bool showBookmarks = _settings.ShowBookmarksBar && !_isPrivateWindow && !_isFullscreen;
        BookmarksBar.Visibility = showBookmarks ? Visibility.Visible : Visibility.Collapsed;
        BookmarksBarRow.Height = new GridLength(showBookmarks ? 32 : 0);

        foreach (BrowserTabState tab in _tabs)
        {
            tab.Browser.Margin = new Thickness(0);
            tab.SnapshotView.Margin = new Thickness(0);
        }

        SplitLeftActiveOutline.Margin = new Thickness(2);
        SplitRightActiveOutline.Margin = new Thickness(2);
        SplitDivider.Margin = new Thickness(0);
        SplitViewToolbar.Margin = new Thickness(0, 8, 0, 0);
        FindBar.Margin = new Thickness(0, 8, 12, 0);
        ToolbarRow.Height = new GridLength(_isFullscreen ? 0 : ConfiguredToolbarHeight);
        ApplyChromeDensity();
        ApplyTabBarAppearance();
        RenderBookmarksBar();
        UpdateTabWidths();
    }

    private void ApplyTabBarAppearance()
    {
        int height = Math.Clamp(_settings.TabBarHeight, 36, 60);
        if (!_isFullscreen)
        {
            TitleBarRow.Height = new GridLength(height);
        }

        double buttonSize = Math.Clamp(height - 10, 26, 40);
        double verticalMargin = Math.Max(2, (height - buttonSize) / 2);
        TitleBarSurface.ColumnDefinitions[1].Width = new GridLength(buttonSize + 6);
        TabListButton.Width = buttonSize;
        TabListButton.Height = buttonSize;
        TabListButton.Margin = new Thickness(3, verticalMargin, 3, verticalMargin);
        AddTabButton.Width = buttonSize;
        AddTabButton.Height = buttonSize;
        AddTabButton.Margin = new Thickness(2, verticalMargin, 4, verticalMargin);
        PrivateModeBadge.Margin = new Thickness(12, verticalMargin, 8, verticalMargin);
        TabStripPanel.Spacing = Math.Clamp(_settings.TabSpacing, 0, 14);
        TabStripHost.Spacing = Math.Clamp(_settings.TabSpacing, 0, 14);
        TabStripHost.Margin = new Thickness(
            Math.Clamp(_settings.TabStripPadding, 0, 40),
            0,
            0,
            0);
        TitleBarSurface.Background = _settings.TabBarBlur == TabBarBlurStyle.Off
            ? new SolidColorBrush(IsDarkTheme()
                ? Color.FromArgb(255, 23, 28, 32)
                : Color.FromArgb(255, 221, 226, 229))
            : GetTransparentBrush();

        foreach (BrowserTabState tab in _tabs)
        {
            tab.Header.Height = Math.Max(33, height - 3);
            if (tab.Header.Child is Grid visualRoot &&
                visualRoot.Children.Count > 0 &&
                visualRoot.Children[0] is Grid content)
            {
                content.Height = Math.Max(29, height - 7);
            }
        }

        foreach (TabGroupState group in _tabGroups)
        {
            group.Header.Height = Math.Max(31, height - 7);
            double groupMargin = Math.Max(2, (height - group.Header.Height) / 2);
            group.Header.Margin = new Thickness(3, groupMargin, 1, 0);
            if (group.Header.Child is Grid content)
            {
                content.Height = Math.Max(28, height - 10);
            }
        }

        bool customBackground =
            _settings.TabBarBackgroundStyle != TabBarBackgroundStyle.Default ||
            _settings.TabBarBlur != TabBarBlurStyle.Off;
        TitleBarCustomBackground.Background = BuildTabBarBackgroundBrush();
        TitleBarCustomBackground.Opacity = customBackground
            ? Math.Clamp(_settings.TabBarBackgroundOpacity, 20, 100) / 100d
            : 0;

        bool mountains =
            _settings.TabBarBackgroundStyle == TabBarBackgroundStyle.Mountains;
        TitleBarMountainLayer.Visibility = mountains
            ? Visibility.Visible
            : Visibility.Collapsed;
        TitleBarMountainLayer.Opacity = mountains
            ? Math.Clamp(_settings.TabBarBackgroundOpacity, 20, 100) / 100d
            : 0;

        Color baseColor = GetConfiguredTabBarBackgroundColor();
        Color accentColor = GetConfiguredAccentColor();
        MountainBackPath.Fill = new SolidColorBrush(
            BlendColors(baseColor, accentColor, 0.34));
        MountainMistPath.Fill = new SolidColorBrush(
            BlendColors(baseColor, accentColor, 0.55));
        MountainFrontPath.Fill = new SolidColorBrush(
            BlendColors(baseColor, accentColor, 0.72));

        UpdateAllTabVisuals();
        UpdateTitleBarColors();
    }

    private Brush BuildTabBarBackgroundBrush()
    {
        Color baseColor = GetConfiguredTabBarBackgroundColor();
        if (_settings.TabBarBlur != TabBarBlurStyle.Off)
        {
            return BuildTabBarAcrylicBrush(baseColor);
        }

        if (_settings.TabBarBackgroundStyle == TabBarBackgroundStyle.Default)
        {
            return new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        }

        if (_settings.TabBarBackgroundStyle == TabBarBackgroundStyle.Solid)
        {
            return new SolidColorBrush(baseColor);
        }

        Color accentColor = GetConfiguredAccentColor();
        LinearGradientBrush gradient = new()
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0)
        };
        gradient.GradientStops.Add(new GradientStop
        {
            Color = BlendColors(baseColor, Color.FromArgb(255, 8, 12, 18), 0.18),
            Offset = 0
        });
        gradient.GradientStops.Add(new GradientStop
        {
            Color = BlendColors(baseColor, accentColor, 0.38),
            Offset = 0.52
        });
        gradient.GradientStops.Add(new GradientStop
        {
            Color = BlendColors(baseColor, accentColor, 0.16),
            Offset = 1
        });
        return gradient;
    }

    private static Color BlendColors(Color first, Color second, double amount)
    {
        double mix = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            255,
            (byte)Math.Round(first.R + (second.R - first.R) * mix),
            (byte)Math.Round(first.G + (second.G - first.G) * mix),
            (byte)Math.Round(first.B + (second.B - first.B) * mix));
    }

    private void RenderBookmarksBar()
    {
        if (BookmarksBarItemsPanel is null)
        {
            return;
        }

        BookmarksBarItemsPanel.Children.Clear();
        foreach (BookmarkEntry bookmark in _dataStore.Library.Bookmarks.Take(80))
        {
            Button button = BuildBookmarkBarButton(bookmark);
            BookmarksBarItemsPanel.Children.Add(button);
        }
    }

    private Button BuildBookmarkBarButton(BookmarkEntry bookmark)
    {
        StackPanel content = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(BuildBookmarkIcon(bookmark));
        content.Children.Add(new TextBlock
        {
            Text = bookmark.Title,
            MaxWidth = 150,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });

        Button button = new()
        {
            Content = content,
            Tag = bookmark,
            Height = 26,
            MinWidth = 0,
            MaxWidth = 195,
            Padding = new Thickness(9, 0, 9, 0),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            CanDrag = true,
            AllowDrop = true
        };
        ToolTipService.SetToolTip(button, $"{bookmark.Url}\nDrag to reorder");
        button.Click += (_, _) =>
        {
            if (ActiveTab is BrowserTabState tab &&
                Uri.TryCreate(bookmark.Url, UriKind.Absolute, out Uri? uri))
            {
                Navigate(tab, uri);
            }
        };
        button.DragStarting += BookmarkBarButton_DragStarting;
        button.DragOver += BookmarkBarButton_DragOver;
        button.Drop += BookmarkBarButton_Drop;
        button.ContextFlyout = BuildBookmarkContextFlyout(bookmark);
        return button;
    }

    private FrameworkElement BuildBookmarkIcon(BookmarkEntry bookmark)
    {
        Grid host = new()
        {
            Width = 16,
            Height = 16
        };
        TextBlock fallback = new()
        {
            Text = string.IsNullOrWhiteSpace(bookmark.Icon) ||
                Uri.TryCreate(bookmark.Icon, UriKind.Absolute, out _)
                    ? "•"
                    : bookmark.Icon,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        host.Children.Add(fallback);

        Uri? iconUri = TryBuildBookmarkIconUri(bookmark);
        if (iconUri is not null)
        {
            Image image = new()
            {
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                Source = new BitmapImage(iconUri)
            };
            bool fallbackTried = false;
            image.ImageOpened += (_, _) => fallback.Visibility = Visibility.Collapsed;
            image.ImageFailed += (_, _) =>
            {
                if (!fallbackTried && TryBuildBookmarkFallbackIconUri(bookmark) is Uri fallbackUri)
                {
                    fallbackTried = true;
                    image.Source = new BitmapImage(fallbackUri);
                    return;
                }

                image.Visibility = Visibility.Collapsed;
                fallback.Visibility = Visibility.Visible;
            };
            host.Children.Add(image);
        }

        return host;
    }

    private static Uri? TryBuildBookmarkIconUri(BookmarkEntry bookmark)
    {
        if (Uri.TryCreate(bookmark.Icon, UriKind.Absolute, out Uri? customIcon) &&
            customIcon.Scheme is "http" or "https")
        {
            return customIcon;
        }

        if (Uri.TryCreate(bookmark.Url, UriKind.Absolute, out Uri? pageUri))
        {
            try
            {
                return new Uri(pageUri.GetLeftPart(UriPartial.Authority) + "/favicon.ico");
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static Uri? TryBuildBookmarkFallbackIconUri(BookmarkEntry bookmark)
    {
        if (!Uri.TryCreate(bookmark.Url, UriKind.Absolute, out Uri? pageUri) ||
            string.IsNullOrWhiteSpace(pageUri.Host))
        {
            return null;
        }

        try
        {
            return new Uri(
                $"https://icons.duckduckgo.com/ip3/{Uri.EscapeDataString(pageUri.Host)}.ico");
        }
        catch
        {
            return null;
        }
    }

    private MenuFlyout BuildBookmarkContextFlyout(BookmarkEntry bookmark)
    {
        MenuFlyout flyout = new();
        MenuFlyoutItem edit = new() { Text = "Edit bookmark" };
        edit.Click += async (_, _) => await ShowBookmarkEditorAsync(bookmark);
        flyout.Items.Add(edit);

        MenuFlyoutItem moveLeft = new() { Text = "Move left" };
        moveLeft.Click += (_, _) => MoveBookmarkByOffset(bookmark.Id, -1);
        flyout.Items.Add(moveLeft);

        MenuFlyoutItem moveRight = new() { Text = "Move right" };
        moveRight.Click += (_, _) => MoveBookmarkByOffset(bookmark.Id, 1);
        flyout.Items.Add(moveRight);

        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem remove = new() { Text = "Remove bookmark" };
        remove.Click += (_, _) =>
        {
            _dataStore.RemoveBookmarkById(bookmark.Id);
            RenderBookmarksBar();
            UpdateBookmarkButton();
        };
        flyout.Items.Add(remove);

        flyout.Opening += (_, _) =>
        {
            int index = _dataStore.Library.Bookmarks.FindIndex(entry =>
                entry.Id.Equals(bookmark.Id, StringComparison.OrdinalIgnoreCase));
            moveLeft.IsEnabled = index > 0;
            moveRight.IsEnabled = index >= 0 &&
                index < _dataStore.Library.Bookmarks.Count - 1;
        };
        return flyout;
    }

    private void MoveBookmarkByOffset(string bookmarkId, int offset)
    {
        List<BookmarkEntry> bookmarks = _dataStore.Library.Bookmarks;
        int index = bookmarks.FindIndex(entry =>
            entry.Id.Equals(bookmarkId, StringComparison.OrdinalIgnoreCase));
        int targetIndex = index + Math.Sign(offset);
        if (index < 0 || targetIndex < 0 || targetIndex >= bookmarks.Count)
        {
            return;
        }

        string? beforeId = offset < 0
            ? bookmarks[targetIndex].Id
            : targetIndex + 1 < bookmarks.Count
                ? bookmarks[targetIndex + 1].Id
                : null;
        if (_dataStore.MoveBookmark(bookmarkId, beforeId))
        {
            RenderBookmarksBar();
        }
    }

    private async void BookmarksBarAddButton_Click(object sender, RoutedEventArgs e) =>
        await ShowBookmarkEditorAsync(null);

    private async Task ShowBookmarkEditorAsync(BookmarkEntry? bookmark)
    {
        TextBox titleBox = new()
        {
            Header = "Name",
            Text = bookmark?.Title ?? ActiveTab?.Title ?? string.Empty,
            PlaceholderText = "How it appears on the bar"
        };
        TextBox urlBox = new()
        {
            Header = "URL",
            Text = bookmark?.Url ?? ActiveTab?.CurrentUri?.AbsoluteUri ?? string.Empty,
            PlaceholderText = "https://example.com"
        };
        TextBox iconBox = new()
        {
            Header = "Icon",
            Text = bookmark?.Icon ?? string.Empty,
            PlaceholderText = "Emoji, image URL, or empty for favicon"
        };
        StackPanel content = new() { Spacing = 11 };
        content.Children.Add(titleBox);
        content.Children.Add(urlBox);
        content.Children.Add(iconBox);

        ContentDialog dialog = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = bookmark is null ? "Add bookmark" : "Edit bookmark",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            if (bookmark is null)
            {
                _dataStore.AddBookmark(urlBox.Text, titleBox.Text, iconBox.Text);
            }
            else
            {
                _dataStore.UpdateBookmark(bookmark.Id, urlBox.Text, titleBox.Text, iconBox.Text);
            }
            RenderBookmarksBar();
            UpdateBookmarkButton();
        }
        catch (Exception exception)
        {
            await ShowErrorDialogAsync("Bookmark was not saved", exception.Message);
        }
    }

    private void BookmarkBarButton_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is Button { Tag: BookmarkEntry bookmark })
        {
            args.Data.SetText(bookmark.Id);
            args.Data.RequestedOperation = DataPackageOperation.Move;
        }
    }

    private void BookmarkBarButton_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Move;
    }

    private async void BookmarkBarButton_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Button { Tag: BookmarkEntry target } targetButton ||
            !e.DataView.Contains(StandardDataFormats.Text))
        {
            return;
        }

        string sourceId = await e.DataView.GetTextAsync();
        if (sourceId.Equals(target.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool placeAfter = e.GetPosition(targetButton).X > targetButton.ActualWidth / 2;
        string? beforeId = target.Id;
        if (placeAfter)
        {
            int targetIndex = _dataStore.Library.Bookmarks.FindIndex(entry =>
                entry.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
            beforeId = targetIndex >= 0 &&
                targetIndex + 1 < _dataStore.Library.Bookmarks.Count
                    ? _dataStore.Library.Bookmarks[targetIndex + 1].Id
                    : null;
        }

        if (_dataStore.MoveBookmark(sourceId, beforeId))
        {
            RenderBookmarksBar();
        }
    }

    private void BookmarksBarItemsPanel_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Move;
    }

    private async void BookmarksBarItemsPanel_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text))
        {
            return;
        }

        string sourceId = await e.DataView.GetTextAsync();
        if (_dataStore.MoveBookmark(sourceId, beforeId: null))
        {
            RenderBookmarksBar();
        }
    }

    private Color GetConfiguredAccentColor() =>
        ParseConfiguredColor(_settings.AccentColor, Color.FromArgb(255, 124, 183, 255));

    private Color GetConfiguredTabBarBackgroundColor() =>
        ParseConfiguredColor(_settings.TabBarBackgroundColor, Color.FromArgb(255, 23, 33, 43));

    private static Color ParseConfiguredColor(string? configured, Color fallback)
    {
        string value = (configured ?? string.Empty).TrimStart('#');
        return value.Length == 6 &&
            byte.TryParse(value[..2], System.Globalization.NumberStyles.HexNumber, null, out byte red) &&
            byte.TryParse(value.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte green) &&
            byte.TryParse(value.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte blue)
                ? Color.FromArgb(255, red, green, blue)
                : fallback;
    }

    private void CleanupGuestProfileAfterClose()
    {
        if (!_browserProfile.IsGuest ||
            OpenWindows.Any(window =>
                window._browserProfile.Id.Equals(_browserProfile.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            BrowserProfileManager.CleanupGuest(_browserProfile);
        });
    }

    private void CleanupDeletedProfileAfterClose()
    {
        string? profileId = _profileToDeleteOnClose;
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            BrowserProfileManager.Delete(profileId);
        });
    }

    private void ClearConfiguredDataOnClose()
    {
        if (_isPrivateWindow ||
            _browserProfile.IsGuest ||
            !_settings.ClearDataOnClose && !HasTemporaryCookieSites)
        {
            return;
        }

        if (OpenWindows.Any(window =>
            !ReferenceEquals(window, this) &&
            !window._isPrivateWindow &&
            window._browserProfile.Id.Equals(_browserProfile.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        CoreWebView2BrowsingDataKinds kinds = (CoreWebView2BrowsingDataKinds)0;
        if (_settings.ClearDataOnClose && _settings.ClearHistoryOnClose)
        {
            _dataStore.ClearHistory();
            kinds |= CoreWebView2BrowsingDataKinds.BrowsingHistory;
        }
        if (_settings.ClearDataOnClose && _settings.ClearCacheOnClose)
        {
            kinds |= CoreWebView2BrowsingDataKinds.DiskCache;
        }
        if (_settings.ClearDataOnClose && _settings.ClearSiteDataOnClose)
        {
            kinds |= CoreWebView2BrowsingDataKinds.LocalStorage |
                CoreWebView2BrowsingDataKinds.IndexedDb |
                CoreWebView2BrowsingDataKinds.FileSystems |
                CoreWebView2BrowsingDataKinds.ServiceWorkers;
            _sitePermissionStore.ResetAll();
        }

        CoreWebView2Profile? profile = GetActiveProfile();
        if (profile is not null)
        {
            if (HasTemporaryCookieSites ||
                _settings.ClearDataOnClose && _settings.ClearCookiesOnClose)
            {
                _ = ClearConfiguredCookiesOnCloseAsync(
                    profile.CookieManager,
                    clearNonExceptionCookies: _settings.ClearDataOnClose &&
                        _settings.ClearCookiesOnClose);
            }

            if (kinds != 0)
            {
                _ = profile.ClearBrowsingDataAsync(kinds);
            }
        }
    }

    private const string SettingsPageTemplate = """
<!doctype html>
<html lang="__LANG__">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Настройки Aero</title>
<style>
/*<aero-font-face>*/
:root{color-scheme:dark;--bg:#17191d;--side:#17191d;--card:#202329;--raised:#292d34;--line:#2c3138;--control-line:#434b57;--text:#f1f3f7;--muted:#a5afbe;--accent:#a8c7fa;--on-accent:#142540;--danger:#ff9b9b;font:14px/1.5 Manrope,"Segoe UI Variable Text","Segoe UI",Arial,sans-serif}
*{box-sizing:border-box}html,body{margin:0;min-height:100%;background:var(--bg);color:var(--text)}body{display:grid;grid-template-columns:232px minmax(0,1fr)}button,input,select,textarea{font:inherit}button,input,select,textarea,a{-webkit-tap-highlight-color:transparent}button{cursor:pointer}button:disabled{cursor:default;opacity:.48}button:focus-visible,a:focus-visible,input:focus-visible,select:focus-visible,textarea:focus-visible{outline:2px solid var(--accent);outline-offset:3px}button:disabled:hover{filter:none}button:hover:not(:disabled){filter:brightness(1.08)}button:active:not(:disabled){filter:brightness(.94)}
aside{position:sticky;top:0;height:100vh;height:100dvh;padding:20px 12px;display:flex;flex-direction:column;background:var(--side);border-right:1px solid var(--line);overflow:auto}.brand{padding:0 8px 14px;font-size:18px;font-weight:600}.version{margin-top:auto;padding:16px 10px 0;color:var(--muted);font-size:12px}.settings-search{display:block;margin:0 0 12px}.settings-search span{position:absolute;width:1px;height:1px;overflow:hidden;clip-path:inset(50%);white-space:nowrap}.settings-search input{width:100%}
nav{display:grid;gap:2px}nav button{position:relative;display:flex;align-items:center;gap:12px;width:100%;min-height:36px;padding:7px 10px;border:0;border-radius:4px;background:transparent;color:var(--text);text-align:left}nav button .nav-icon{color:var(--muted)}.nav-icon{width:18px;display:inline-flex;flex-shrink:0;text-align:center;font-family:"Segoe Fluent Icons","Segoe MDL2 Assets";font-size:16px;line-height:1}nav button:hover{background:var(--raised);color:var(--text)}nav button.active{background:var(--raised);color:var(--text)}nav button.active .nav-icon{color:var(--text)}nav button.active:before{content:"";position:absolute;left:0;top:10px;bottom:10px;width:3px;border-radius:2px;background:var(--accent)}
main{width:min(860px,100%);padding:28px clamp(20px,4vw,48px) 80px;min-width:0}section{display:none}section.active{display:block}h1{font-size:20px;line-height:1.3;font-weight:600;margin:0 0 4px}h2{font-size:14px;line-height:1.4;margin:0;font-weight:600}.lead{margin:0 0 16px;color:var(--muted);font-size:13px;max-width:65ch}.card{margin:0 0 28px;padding:0;border:0;border-bottom:1px solid var(--line);background:none;min-width:0}.card:last-child{border-bottom:0}.card-head{margin:12px 0}.hint,.meta{color:var(--muted);font-size:12px;overflow-wrap:anywhere}.hint{margin:12px 0}.row{display:grid;grid-template-columns:minmax(0,1fr) minmax(180px,320px);gap:24px;align-items:center;min-height:56px;padding:10px 0;border-top:1px solid var(--line)}.row:first-child{border-top:0}.row>*{min-width:0}.label b{display:block;font-weight:400}.label span{display:block;margin-top:2px;color:var(--muted);font-size:12px;line-height:1.5;overflow-wrap:anywhere}
input,select,textarea,button.action{max-width:100%;min-width:0;width:100%;min-height:32px;border:1px solid var(--control-line);border-radius:4px;background:var(--raised);color:var(--text);padding:5px 10px}input:focus,select:focus,textarea:focus{border-bottom-color:var(--accent);box-shadow:inset 0 -1px 0 var(--accent)}input:focus-visible,select:focus-visible,textarea:focus-visible{outline:0}input::placeholder{color:var(--muted)}textarea{min-height:88px;resize:vertical}input[type=color]{height:38px;padding:4px}input[type=checkbox]{width:18px;height:18px;min-height:18px;justify-self:end;accent-color:var(--accent);flex-shrink:0}.row>input[type=checkbox]{appearance:none;position:relative;width:40px;height:20px;min-height:20px;padding:0;border:1px solid var(--muted);border-radius:10px;background:transparent;cursor:pointer;transition:background-color .12s,border-color .12s}.row>input[type=checkbox]:before{content:"";position:absolute;top:3px;left:3px;width:12px;height:12px;border-radius:50%;background:var(--muted);transition:transform .12s}.row>input[type=checkbox]:checked{border-color:var(--accent);background:var(--accent)}.row>input[type=checkbox]:checked:before{background:var(--on-accent);transform:translateX(20px)}.row>input[type=checkbox]:focus{box-shadow:none}.row>input[type=checkbox]:focus-visible{outline:2px solid var(--accent);outline-offset:3px}button.action{width:auto;font-weight:500;transition:background-color .12s,color .12s}button.primary{border-color:transparent;background:var(--accent);color:var(--on-accent)}.actions{display:flex;gap:8px;justify-content:flex-end;flex-wrap:wrap;padding:4px 0}.stack{display:grid;gap:8px}.chips{display:flex;gap:8px;flex-wrap:wrap;margin:12px 0}.chip{padding:2px 8px;border-radius:4px;background:var(--raised);color:var(--muted);font-size:12px}.list-item{display:grid;grid-template-columns:32px minmax(0,1fr) auto;gap:12px;align-items:center;padding:12px 0;border-top:1px solid var(--line)}.list-item:first-child{border-top:0}.list-item b{overflow-wrap:anywhere;font-weight:600}.list-item .actions{max-width:300px}.avatar{display:grid;place-items:center;width:32px;height:32px;border-radius:4px;background:var(--raised);font-size:18px}.danger,button.danger{color:var(--danger)}.check-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px;padding:12px 0}.check-grid label{display:flex;gap:10px;align-items:center;min-height:28px}.check-grid input{justify-self:auto}.toast{position:fixed;right:24px;bottom:24px;max-width:min(460px,calc(100vw - 32px));padding:10px 14px;border:1px solid var(--line);border-radius:6px;background:var(--raised);box-shadow:0 8px 24px #0003;opacity:0;transform:translateY(8px);transition:opacity .16s,transform .16s;pointer-events:none;z-index:20;overflow-wrap:anywhere}.toast.show{opacity:1;transform:none}.toast.error{border-color:var(--danger)}[hidden]{display:none!important}.search-empty{color:var(--muted);padding:20px 0}html.reduce-motion *{scroll-behavior:auto!important;transition:none!important;animation:none!important}
html[data-theme=Light]{color-scheme:light;--bg:#f7f8fa;--side:#f7f8fa;--card:#fff;--raised:#fff;--line:#e3e7ed;--control-line:#c4cbd6;--text:#20242c;--muted:#566274;--accent:#1b57b1;--on-accent:#fff;--danger:#b42330}
@media(prefers-color-scheme:light){html[data-theme=System]{color-scheme:light;--bg:#f7f8fa;--side:#f7f8fa;--card:#fff;--raised:#fff;--line:#e3e7ed;--control-line:#c4cbd6;--text:#20242c;--muted:#566274;--accent:#1b57b1;--on-accent:#fff;--danger:#b42330}}
@media(max-width:1050px){.row{grid-template-columns:minmax(0,1fr) minmax(160px,280px);gap:16px}.list-item{grid-template-columns:38px minmax(0,1fr)}.list-item .actions{grid-column:2;justify-content:flex-start;max-width:none}}
@media(max-width:760px){body{grid-template-columns:190px minmax(0,1fr)}aside{padding:20px 10px}main{padding:24px 20px 60px}.row{grid-template-columns:1fr;gap:10px}.row>input[type=checkbox]{justify-self:start}.check-grid{grid-template-columns:1fr}}
@media(max-width:540px){body{display:block}aside{position:relative;height:auto;border-right:0;border-bottom:1px solid var(--line);padding:16px}.brand{padding-bottom:14px}.settings-search{margin-bottom:12px}nav{display:flex;overflow-x:auto;padding:4px 2px 8px}nav button{width:auto;flex-shrink:0}main{padding:24px 16px 60px}.toast{right:16px;bottom:16px}.row .actions{justify-content:flex-start}}
@media(prefers-reduced-motion:reduce){*,*:before,*:after{scroll-behavior:auto!important;transition:none!important;animation:none!important}}
@media(forced-colors:active){:root{--bg:Canvas;--side:Canvas;--card:Canvas;--raised:Canvas;--text:CanvasText;--muted:CanvasText;--line:CanvasText;--control-line:ButtonText;--accent:Highlight;--on-accent:HighlightText;--danger:LinkText}nav button.active{outline:1px solid Highlight}button.primary{forced-color-adjust:auto}}
</style>
</head>
<body>
<aside><div class="brand">Настройки</div><label class="settings-search"><span>Поиск настроек</span><input id="settingsSearch" type="search" autocomplete="off" placeholder="Найти настройку"></label><nav id="nav" aria-label="Разделы настроек"></nav><div class="version">Aero __APP_VERSION__</div></aside>
<main>
<p id="settingsSearchEmpty" class="search-empty" role="status" hidden>Ничего не найдено. Попробуйте другой запрос.</p>
<section id="general"><h1>Основные настройки</h1><p class="lead">Поиск, домашняя страница и запуск браузера.</p><div class="card">
<div class="row"><div class="label"><b>Язык интерфейса</b><span>Русский или English. Интерфейс обновится сразу.</span></div><select data-setting="AppLanguage"><option value="ru">Русский</option><option value="en">English</option></select></div>
<div class="row"><div class="label"><b>Поисковая система</b><span>Используется в адресной строке и Aero Home.</span></div><select data-setting="SearchEngine"><option>Google</option><option>Bing</option><option>DuckDuckGo</option><option>Brave</option><option>Custom</option></select></div>
<div class="row"><div class="label"><b>Свой поисковик</b><span>Используйте {query} вместо запроса.</span></div><input data-setting="CustomSearchUrl"></div>
<div class="row"><div class="label"><b>Домашняя страница</b><span>URL или aero://home.</span></div><input data-setting="HomePageUrl"></div>
<div class="row"><div class="label"><b>Новая вкладка</b></div><select data-setting="NewTabMode"><option value="AeroHome">Aero Home</option><option>Google</option><option>Custom</option></select></div>
<div class="row"><div class="label"><b>Своя новая вкладка</b></div><input data-setting="CustomNewTabUrl"></div>
<div class="row"><div class="label"><b>При запуске</b></div><select data-setting="StartupBehavior"><option value="NewTab">Новая вкладка</option><option value="RestoreSession">Восстановить сессию</option><option value="HomePage">Домашняя страница</option></select></div>
<div class="row"><div class="label"><b>Восстановление сессии</b></div><input type="checkbox" data-setting="RestorePreviousSession"></div>
<div class="row"><div class="label"><b>Проверять обновления</b><span>При запуске Aero спрашивает у GitHub номер последней версии. О вас ничего не отправляется.</span></div><input type="checkbox" data-setting="CheckForUpdates"></div>
<div class="row"><div class="label"><b>Открытие ссылок</b></div><select data-setting="OpenLinksBehavior"><option value="NewTab">В новой вкладке</option><option value="CurrentTab">В текущей вкладке</option><option value="NewWindow">В новом окне</option></select></div>
</div></section>
<section id="appearance"><h1>Внешний вид</h1><p class="lead">Тема, акцент и плотность интерфейса.</p><div class="card">
<div class="row"><div class="label"><b>Тема</b></div><select data-setting="ThemeMode"><option value="Light">Светлая</option><option value="Dark">Тёмная</option><option value="System">Системная</option></select></div>
<div class="row"><div class="label"><b>Акцентный цвет</b></div><input type="color" data-setting="AccentColor"></div>
<div class="row"><div class="label"><b>Плотность интерфейса</b></div><select data-setting="InterfaceDensity"><option value="Comfortable">Обычная</option><option value="Compact">Компактная</option></select></div>
<div class="row"><div class="label"><b>Уменьшить анимации</b><span>Отключает масштабирование и перемещение элементов.</span></div><input type="checkbox" data-setting="ReduceMotion"></div>
<div class="row"><div class="label"><b>Ширина вкладок</b></div><select data-setting="TabSize"><option value="Compact">Компактная</option><option value="Medium">Средняя</option><option value="Large">Широкая</option></select></div>
<div class="row"><div class="label"><b>Высота верхней панели</b><span>От 36 до 60 px. Увеличивает вкладки и кнопки сверху.</span></div><input type="number" min="36" max="60" step="2" data-setting="TabBarHeight"></div>
<div class="row"><div class="label"><b>Фон панели вкладок</b><span>Горы — встроенный векторный фон, интернет не нужен.</span></div><select data-setting="TabBarBackgroundStyle"><option value="Default">Стандартный</option><option value="Solid">Однотонный</option><option value="Gradient">Градиент</option><option value="Mountains">Горы</option></select></div>
<div class="row"><div class="label"><b>Цвет верхней панели</b></div><input type="color" data-setting="TabBarBackgroundColor"></div>
<div class="row"><div class="label"><b>Насыщенность фона, %</b><span>20–100. Для гор лучше 70–95.</span></div><input type="number" min="20" max="100" step="5" data-setting="TabBarBackgroundOpacity"></div>
<div class="row"><div class="label"><b>Стекло и размытие панели</b><span>Soft — лёгкое стекло, Strong — более прозрачное и глубокое.</span></div><select data-setting="TabBarBlur"><option value="Off">Выключено</option><option value="Soft">Soft blur</option><option value="Strong">Strong blur</option></select></div>
</div>
<div class="card"><div class="card-head"><h2>Конструктор панели вкладок</h2><p class="hint">Пресеты меняют только вкладки. После применения каждый параметр можно настроить вручную.</p></div>
<div class="actions" style="justify-content:flex-start"><button class="action" data-tab-preset="classic">Classic</button><button class="action" data-tab-preset="glass">Glass</button><button class="action" data-tab-preset="pill">Pill</button><button class="action" data-tab-preset="minimal">Minimal</button><button class="action" data-tab-preset="neon">Neon</button></div>
<div class="row"><div class="label"><b>Форма вкладок</b><span>Connected сохраняет форму как в текущем Aero, Custom использует радиус ниже.</span></div><select data-setting="TabShape"><option value="Connected">Connected</option><option value="Rounded">Rounded</option><option value="Pill">Pill</option><option value="Square">Square</option><option value="Custom">Custom</option></select></div>
<div class="row"><div class="label"><b>Радиус углов</b><span>0–30 px для Rounded и Custom, до 12 px для Connected.</span></div><input type="number" min="0" max="30" step="1" data-setting="TabCornerRadius"></div>
<div class="row"><div class="label"><b>Стиль поверхности</b><span>Theme, собственные цвета, акцент, градиент или Acrylic.</span></div><select data-setting="TabSurfaceStyle"><option value="Theme">Theme</option><option value="Solid">Solid</option><option value="Accent">Accent</option><option value="Gradient">Gradient</option><option value="Acrylic">Acrylic</option></select></div>
<div class="row"><div class="label"><b>Цвет активной вкладки</b></div><input type="color" data-setting="TabActiveColor"></div>
<div class="row"><div class="label"><b>Цвет при наведении</b></div><input type="color" data-setting="TabHoverColor"></div>
<div class="row"><div class="label"><b>Цвет неактивной вкладки</b></div><input type="color" data-setting="TabInactiveColor"></div>
<div class="row"><div class="label"><b>Прозрачность активной, %</b></div><input type="number" min="0" max="100" step="5" data-setting="TabActiveOpacity"></div>
<div class="row"><div class="label"><b>Прозрачность hover, %</b></div><input type="number" min="0" max="100" step="5" data-setting="TabHoverOpacity"></div>
<div class="row"><div class="label"><b>Прозрачность неактивной, %</b></div><input type="number" min="0" max="100" step="5" data-setting="TabInactiveOpacity"></div>
</div>
<div class="card"><div class="card-head"><h2>Геометрия и содержимое</h2></div>
<div class="row"><div class="label"><b>Горизонтальный padding</b><span>2–28 px.</span></div><input type="number" min="2" max="28" step="1" data-setting="TabHorizontalPadding"></div>
<div class="row"><div class="label"><b>Расстояние между вкладками</b><span>0–14 px.</span></div><input type="number" min="0" max="14" step="1" data-setting="TabSpacing"></div>
<div class="row"><div class="label"><b>Отступ всей ленты слева</b><span>0–40 px.</span></div><input type="number" min="0" max="40" step="1" data-setting="TabStripPadding"></div>
<div class="row"><div class="label"><b>Ширина закреплённой вкладки</b><span>36–80 px.</span></div><input type="number" min="36" max="80" step="1" data-setting="PinnedTabWidth"></div>
<div class="row"><div class="label"><b>Размер иконки</b><span>10–24 px.</span></div><input type="number" min="10" max="24" step="1" data-setting="TabIconSize"></div>
<div class="row"><div class="label"><b>Размер текста</b><span>9–18 px.</span></div><input type="number" min="9" max="18" step="1" data-setting="TabFontSize"></div>
<div class="row"><div class="label"><b>Выравнивание текста</b></div><select data-setting="TabTextAlignment"><option value="Left">Слева</option><option value="Center">По центру</option></select></div>
<div class="row"><div class="label"><b>Жирный текст активной вкладки</b></div><input type="checkbox" data-setting="TabBoldActiveText"></div>
<div class="row"><div class="label"><b>Акцентный текст активной вкладки</b></div><input type="checkbox" data-setting="TabUseAccentText"></div>
<div class="row"><div class="label"><b>Кнопка закрытия</b></div><select data-setting="TabCloseButtonMode"><option value="HoverOrActive">Активная или hover</option><option value="ActiveOnly">Только активная</option><option value="Always">Всегда</option><option value="Never">Никогда</option></select></div>
<div class="row"><div class="label"><b>Разделители вкладок</b></div><input type="checkbox" data-setting="ShowTabSeparators"></div>
</div>
<div class="card"><div class="card-head"><h2>Контур, индикатор и движение</h2></div>
<div class="row"><div class="label"><b>Толщина контура</b><span>0–4 px.</span></div><input type="number" min="0" max="4" step="1" data-setting="TabBorderThickness"></div>
<div class="row"><div class="label"><b>Прозрачность контура, %</b></div><input type="number" min="0" max="100" step="5" data-setting="TabBorderOpacity"></div>
<div class="row"><div class="label"><b>Индикатор активной вкладки</b></div><select data-setting="TabActiveIndicator"><option value="Top">Сверху</option><option value="Bottom">Снизу</option><option value="Outline">Контур</option><option value="None">Нет</option></select></div>
<div class="row"><div class="label"><b>Толщина индикатора</b><span>1–6 px.</span></div><input type="number" min="1" max="6" step="1" data-setting="TabIndicatorThickness"></div>
<div class="row"><div class="label"><b>Анимация вкладок</b></div><select data-setting="TabAnimation"><option value="None">Нет</option><option value="Subtle">Subtle</option><option value="Scale">Scale</option><option value="Slide">Slide</option><option value="Bounce">Bounce</option></select></div>
<div class="row"><div class="label"><b>Скорость анимации</b><span>0–600 мс.</span></div><input type="number" min="0" max="600" step="10" data-setting="TabAnimationDuration"></div>
<div class="row"><div class="label"><b>Подъём при наведении</b><span>0–8 px.</span></div><input type="number" min="0" max="8" step="1" data-setting="TabHoverLift"></div>
<div class="row"><div class="label"><b>Масштаб неактивных вкладок</b><span>85–100%.</span></div><input type="number" min="85" max="100" step="1" data-setting="TabInactiveScalePercent"></div>
</div><div class="card"><div class="card-head"><h2>Фон Aero Home</h2><p class="hint">Цвет, градиент, встроенные горы или своя картинка из профиля Aero.</p></div>
<div class="row"><div class="label"><b>Тип фона главной</b></div><select data-setting="HomeBackgroundStyle"><option value="Default">Стандартный</option><option value="Solid">Однотонный</option><option value="Gradient">Градиент</option><option value="Mountains">Горы</option><option value="Image">Своя картинка</option></select></div>
<div class="row"><div class="label"><b>Основной цвет</b></div><input type="color" data-setting="HomeBackgroundColor"></div>
<div class="row"><div class="label"><b>Второй цвет</b><span>Используется для градиента и неба в фоне с горами.</span></div><input type="color" data-setting="HomeBackgroundSecondaryColor"></div>
<div class="row"><div class="label"><b>Интенсивность, %</b><span>20–100.</span></div><input type="number" min="20" max="100" step="5" data-setting="HomeBackgroundOpacity"></div>
<div class="row"><div class="label"><b>Размытие, px</b><span>0–24. Для фотографий обычно достаточно 4–10.</span></div><input type="number" min="0" max="24" step="1" data-setting="HomeBackgroundBlur"></div>
<div class="row"><div class="label"><b>Расположение картинки</b></div><select data-setting="HomeBackgroundImageFit"><option value="Cover">Заполнить экран</option><option value="Contain">Показать целиком</option><option value="Center">По центру</option><option value="Tile">Плиткой</option></select></div>
<div class="row"><div class="label"><b>Адаптировать под тему</b><span>Добавляет светлый или тёмный слой, чтобы поиск оставался читаемым.</span></div><input type="checkbox" data-setting="HomeBackgroundAdaptiveOverlay"></div>
<div class="row"><div class="label"><b>Своя картинка</b><span id="homeBackgroundPath" class="meta"></span></div><div class="actions"><button class="action primary" data-action="choose-home-background">Выбрать картинку</button><button class="action" data-action="reset-home-background">Сбросить фон</button></div></div>
</div><div class="card">
<div class="row"><div class="label"><b>Показывать кнопку Home</b></div><input type="checkbox" data-setting="ShowHomeButton"></div>
<div class="row"><div class="label"><b>Быстрые ссылки на новой вкладке</b></div><input type="checkbox" data-setting="ShowQuickLinks"></div>
<div class="row"><div class="label"><b>Показывать панель закладок</b></div><input type="checkbox" data-setting="ShowBookmarksBar"></div>
</div></section>
<section id="downloads"><h1>Загрузки</h1><p class="lead">Папка, подтверждения и безопасность файлов.</p><div class="card">
<div class="row"><div class="label"><b>Папка по умолчанию</b></div><div class="stack"><input data-setting="DefaultDownloadFolder"><div class="actions"><button class="action" data-action="choose-download-folder">Выбрать</button><button class="action" data-action="open-downloads">Открыть</button></div></div></div>
<div class="row"><div class="label"><b>Спрашивать путь каждый раз</b></div><input type="checkbox" data-setting="AskDownloadPathEveryTime"></div>
<div class="row"><div class="label"><b>Автоматически открывать панель</b></div><input type="checkbox" data-setting="OpenDownloadsPanelAutomatically"></div>
<div class="row"><div class="label"><b>Предупреждать об опасных файлах</b></div><input type="checkbox" data-setting="WarnDangerousFiles"></div>
</div></section>
<section id="privacy"><h1>Приватность</h1><p class="lead">История, cookies, кэш и защищённые подключения.</p><div class="card">
<div class="row"><div class="label"><b>Сохранять историю</b></div><input type="checkbox" data-setting="HistoryEnabled"></div><div class="row"><div class="label"><b>Разрешать cookies</b></div><input type="checkbox" data-setting="CookiesEnabled"></div><div class="row"><div class="label"><b>Блокировать сторонние cookies</b><span>Сторонние запросы не получают Cookie-заголовок.</span></div><input type="checkbox" data-setting="BlockThirdPartyCookies"></div><div class="row"><div class="label"><b>Использовать кэш</b></div><input type="checkbox" data-setting="CacheEnabled"></div>
<div class="row"><div class="label"><b>Do Not Track</b></div><input type="checkbox" data-setting="DoNotTrack"></div><div class="row"><div class="label"><b>HTTPS-first</b><span>HTTP автоматически повышается до HTTPS, кроме исключений.</span></div><input type="checkbox" data-setting="HttpsFirst"></div>
<div class="row"><div class="label"><b>Очищать при закрытии</b></div><input type="checkbox" data-setting="ClearDataOnClose"></div>
<div class="row"><div class="label"><b>Что очищать</b></div><div class="stack"><label><input type="checkbox" data-setting="ClearHistoryOnClose"> История</label><label><input type="checkbox" data-setting="ClearCookiesOnClose"> Cookies</label><label><input type="checkbox" data-setting="ClearCacheOnClose"> Кэш</label><label><input type="checkbox" data-setting="ClearSiteDataOnClose"> Данные сайтов</label></div></div>
<div class="row"><div class="label"><b>Исключения сайтов</b><span>Один домен на строку.</span></div><textarea data-setting="PrivacySiteExceptions" data-list="true"></textarea></div>
<div class="row"><div class="label"><b>Исключения сторонних cookies</b><span>Домены документов или ресурсов, которым разрешены сторонние cookies.</span></div><textarea data-setting="ThirdPartyCookieExceptions" data-list="true"></textarea></div>
<div class="row"><div class="label"><b>Защитить входы на сайты Windows Hello</b><span>Cookies и так хранятся вне файла браузера, который ищут стиллеры, под ключом, который открывается только внутри Aero. С Windows Hello этот ключ ещё и требует подтверждения при каждом запуске Aero.</span></div><input type="checkbox" data-setting="ProtectCookiesWithHello"></div>
<div class="row"><div class="label"><b>Сайты под замком</b><span>Один домен на строку. Открываются только после Windows Hello; доступ действует 30 минут после последнего визита.</span></div><textarea data-setting="LockedSites" data-list="true"></textarea></div>
<div class="row"><div class="label"><b>Выходить из сайтов под замком</b><span>Их cookies станут сессионными и удалятся при закрытии Aero.</span></div><input type="checkbox" data-setting="WipeLockedSiteSessions"></div>
<div class="row"><div class="label"><b>Временные cookies</b><span>Для этих сайтов постоянные cookies преобразуются в сессионные.</span></div><textarea data-setting="TemporaryCookieSites" data-list="true"></textarea></div>
</div></section>
<section id="shield"><h1>Shield</h1><p class="lead">Блокировка рекламы, трекеров и косметическая фильтрация.</p><div class="card">
<div class="row"><div class="label"><b>Включить Shield</b></div><input type="checkbox" data-setting="BlockAdsAndTrackers"></div><div class="row"><div class="label"><b>Уровень блокировки</b></div><select data-setting="ShieldMode"><option value="Standard">Стандартный</option><option value="Strict">Строгий</option></select></div><div class="row"><div class="label"><b>Движок</b></div><select data-setting="ShieldEngineMode"><option value="Hybrid">Гибридный</option><option value="Advanced">Расширенный</option><option value="Native">Нативный</option></select></div>
<div class="row"><div class="label"><b>Косметическая фильтрация</b></div><input type="checkbox" data-setting="ShieldCosmeticFiltering"></div><div class="row"><div class="label"><b>Автообновление списков</b></div><input type="checkbox" data-setting="ShieldAutoUpdate"></div><div class="row"><div class="label"><b>YouTube Clean Mode</b></div><input type="checkbox" data-setting="ShieldYouTubeCleanMode"></div><div class="row"><div class="label"><b>Очищать tracking-параметры</b></div><input type="checkbox" data-setting="CleanTrackingLinks"></div>
<div class="row"><div class="label"><b>Исключения</b></div><textarea data-setting="ShieldAllowlist" data-list="true"></textarea></div><div class="row"><div class="label"><b>Пользовательские фильтры</b></div><textarea data-setting="ShieldCustomRules" data-list="true"></textarea></div>
<div class="row"><div class="label"><b>Списки фильтров</b><span>Встроенный быстрый список, EasyList, EasyPrivacy и пользовательские правила.</span></div><div class="chips"><span class="chip">Aero Fast</span><span class="chip">EasyList</span><span class="chip">EasyPrivacy</span><span class="chip">Custom</span></div></div>
<div class="row"><div class="label"><b>Статистика и диагностика</b><span id="shieldStats"></span></div><button class="action" data-action="shield-refresh">Обновить списки</button></div>
</div></section>
<section id="memory"><h1>Memory Saver</h1><p class="lead">Замораживает неактивные вкладки без закрытия и перезагрузки страницы.</p><div class="card">
<div class="row"><div class="label"><b>Состояние памяти</b><span id="memoryStats">Подсчёт…</span></div><div class="chips"><span class="chip" id="memoryCurrent">—</span><span class="chip" id="memoryForecast">—</span></div></div>
<div class="row"><div class="label"><b>Включение</b></div><select data-setting="MemorySaverMode"><option value="Maximum">Максимальный — замораживать раньше</option><option value="Balanced">Сбалансированный — через 3 минуты</option><option value="Off">Выкл.</option></select></div>
<div class="row"><div class="label"><b>Усыплять фоновые вкладки</b><span>Активная вкладка всегда получает приоритет.</span></div><input type="checkbox" data-setting="SleepBackgroundTabs"></div>
<div class="row"><div class="label"><b>Задержка сна, секунд</b></div><input type="number" min="15" max="3600" data-setting="BackgroundTabSleepSeconds"></div>
<div class="row"><div class="label"><b>Общий лимит памяти, МБ</b><span>Учитываются процессы Aero и WebView2; 0 — без лимита.</span></div><input type="number" min="0" max="32768" data-setting="MemoryLimitMb"></div>
<div class="row"><div class="label"><b>Защищать вкладки с аудио</b><span>Воспроизведение не будет прервано.</span></div><input type="checkbox" data-setting="KeepAudioTabsAwake"></div>
<div class="row"><div class="label"><b>Защищать активные загрузки</b><span>Вкладка не уснёт до завершения загрузки.</span></div><input type="checkbox" data-setting="KeepDownloadingTabsAwake"></div>
<div class="row"><div class="label"><b>Защищать заполненные формы</b><span>Используется только при явной ручной выгрузке вкладки.</span></div><input type="checkbox" data-setting="ProtectFilledForms"></div>
<div class="row"><div class="label"><b>Сохранение состояния</b><span>При автоматической заморозке WebView остаётся открытым: адрес, видео, формы и прокрутка не сбрасываются.</span></div><span class="chip">Включено</span></div>
<div class="row"><div class="label"><b>Исключения сайтов</b><span>Один домен на строку.</span></div><textarea data-setting="MemorySaverSiteExceptions" data-list="true"></textarea></div>
<div class="row"><div class="label"><b>Бюджет ресурсов</b><span>Фоновая вкладка, которая дольше 30 секунд превышает лимит, замораживается. Исключения выше тоже действуют.</span></div><input type="checkbox" data-setting="ResourceBudgetEnabled"></div>
<div class="row"><div class="label"><b>Лимит CPU в фоне, %</b><span>Доля всех ядер процессора.</span></div><input type="number" min="2" max="100" data-setting="BackgroundCpuBudgetPercent"></div>
<div class="row"><div class="label"><b>Лимит памяти в фоне, МБ</b><span>На одну вкладку.</span></div><input type="number" min="128" max="16384" data-setting="BackgroundMemoryBudgetMb"></div>
<div class="actions"><button class="action primary" data-action="memory-freeze">Заморозить фоновые вкладки сейчас</button></div>
</div></section>
<section id="extensions"><h1>Расширения</h1><p class="lead">Отдельный набор расширений для каждого профиля.</p><div class="card"><div class="actions"><button class="action primary" data-action="install-extension">Установить или импортировать</button></div><div id="extensionList"></div></div></section>
<section id="permissions"><h1>Разрешения сайтов</h1><p class="lead">Значения по умолчанию и сохранённые исключения.</p><div class="card" id="permissionDefaults"></div><div class="card"><div class="card-head"><h2>Сохранённые решения</h2></div><div id="permissionList"></div><div class="actions"><button class="action danger" data-action="reset-permissions">Сбросить все разрешения</button></div></div></section>
<section id="clear"><h1>Очистка данных</h1><p class="lead">Удалите данные текущего профиля за выбранный период.</p><div class="card"><div class="check-grid" id="clearItems"></div><div class="row"><div class="label"><b>Диапазон времени</b></div><select id="clearRange"><option value="hour">Последний час</option><option value="day">Последние 24 часа</option><option value="week">Последние 7 дней</option><option value="month">Последние 30 дней</option><option value="all">Всё время</option></select></div><div class="actions"><button class="action primary" id="clearButton">Очистить данные</button></div></div></section>
<section id="data"><h1>Импорт, экспорт и копии</h1><p class="lead">Переносите данные из других браузеров и защищайте текущий профиль Aero.</p>
<div class="card"><div class="card-head"><h2>Импорт из браузеров</h2><p class="hint">Aero читает профили Chrome, Edge, Яндекс Браузера, Brave, Vivaldi и Opera; из Firefox — закладки и историю. Для паролей поддерживаются CSV-файлы Chrome, Edge, Firefox, Brave, Opera, Vivaldi, Safari, 1Password, Bitwarden и других популярных браузеров.</p></div><div class="check-grid" id="importItems"></div><div class="actions"><button class="action" id="importChrome">Импорт из Chrome</button><button class="action" id="importEdge">Импорт из Edge</button><button class="action" id="importYandex">Яндекс Браузер</button><button class="action" id="importBrave">Brave</button><button class="action" id="importVivaldi">Vivaldi</button><button class="action" id="importOpera">Opera</button><button class="action" id="importFirefox">Firefox</button></div><div class="row"><div class="label"><b>Пароли</b><span>Сайты, логины и скрытые поля паролей открываются только после Windows Hello.</span></div><div class="actions"><button class="action primary" id="viewPasswords">Открыть пароли</button><button class="action" id="importPasswords">Импортировать CSV</button><button class="action" id="exportPasswords">Экспортировать пароли</button></div></div><div class="row"><div class="label"><b>Cookies</b><span>Импорт Netscape cookies.txt, JSON или CSV в текущий профиль. Файл может содержать активные сеансы входа; Aero потребует Windows Hello.</span></div><div class="actions"><button class="action" id="importCookies">Импортировать cookies</button></div></div></div>
<div class="card"><div class="card-head"><h2>Синхронизация</h2><p class="hint">Бесплатно через вашу облачную папку: OneDrive, Яндекс Диск, Google Drive, Dropbox или Syncthing. Всё в ней зашифровано фразой, которую знаете только вы. Закладки синхронизируются сами, пароли — кнопкой с подтверждением Windows Hello. Та же папка и фраза вернут пароли на новом компьютере.</p></div><p class="hint" id="syncStatus"></p><div class="actions"><button class="action primary" id="syncSetup">Выбрать папку</button><button class="action" id="syncPasswords">Синхронизировать пароли</button><button class="action danger" id="syncDisable">Отключить</button></div></div>
<div class="card"><div class="card-head"><h2>Экспорт данных</h2><p class="hint">Создаёт читаемый файл .aerodata без cookies и паролей.</p></div><div class="check-grid" id="exportItems"></div><div class="actions"><button class="action primary" id="exportData">Экспортировать выбранное</button></div></div>
<div class="card"><div class="card-head"><h2>Резервные копии профиля</h2><p class="hint">В копию входят настройки, история, закладки, вкладки, workspaces, разрешения, Shield и расширения. Aero Vault, cookies, Local Storage, IndexedDB, Service Worker и данные сеансов намеренно не копируются. Каждый файл проверяется SHA-256.</p></div>
<div class="row"><div class="label"><b>Автоматическая локальная копия</b><span>Шифруется для текущего пользователя Windows.</span></div><input type="checkbox" data-setting="AutomaticBackupsEnabled"></div>
<div class="row"><div class="label"><b>Интервал, часов</b></div><input type="number" min="1" max="168" data-setting="AutomaticBackupIntervalHours"></div>
<div class="row"><div class="label"><b>Хранить последних копий</b></div><input type="number" min="2" max="20" data-setting="BackupRetentionCount"></div>
<div class="actions"><button class="action primary" id="backupCreate">Создать копию</button><button class="action" id="backupValidate">Проверить файл</button><button class="action" id="backupRestore">Восстановить из файла</button><button class="action danger" id="backupRecover">Восстановить последнюю исправную</button></div><div id="backupList"></div></div></section>
<section id="profiles"><h1>Профили</h1><p class="lead">История, cookies, расширения, WebView2 и Aero Vault изолированы.</p><div class="card"><div class="card-head"><h2>Текущий профиль</h2><p class="hint" id="profilePath"></p></div><div class="row"><div class="label"><b>Аватар</b><span>Выберите готовую иконку — ввод короткого кода больше не нужен.</span></div><select id="profileIcon"></select></div><div class="row"><div class="label"><b>Имя профиля</b><span>До 80 символов.</span></div><input id="profileName" maxlength="80"></div><div class="actions"><button class="action" id="profileSave">Сохранить</button><button class="action" data-profile-action="guest">Гостевой профиль</button></div></div><div class="card"><div class="card-head"><h2>Все профили</h2></div><div id="profileList"></div></div><div class="card"><div class="card-head"><h2>Новый профиль</h2></div><div class="row"><select id="newProfileIcon" aria-label="Значок нового профиля"></select><input id="newProfileName" aria-label="Имя нового профиля" placeholder="Имя профиля" maxlength="80"></div><div class="actions"><button class="action primary" id="profileCreate">Создать и открыть</button></div></div></section>
</main><div id="toast" class="toast" role="status" aria-live="polite" aria-atomic="true"></div>
<script>
const initial=__INITIAL_STATE__;
document.documentElement.dataset.theme=initial.settings.themeMode;applyAccent(initial.settings.accentColor);document.documentElement.classList.toggle('reduce-motion',!!initial.settings.reduceMotion);
const sections=[['general','Основные','\uE713'],['appearance','Внешний вид','\uE790'],['downloads','Загрузки','\uE896'],['privacy','Приватность','\uE72E'],['shield','Shield','\uE83D'],['memory','Memory Saver','\uE950'],['extensions','Расширения','\uE74C'],['permissions','Разрешения сайтов','\uE8D7'],['clear','Очистка данных','\uE74D'],['data','Импорт и копии','\uE8B5'],['profiles','Профили','\uE77B']];
const post=m=>chrome.webview.postMessage(m);const lower=s=>s[0].toLowerCase()+s.slice(1);const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
let currentSection='general';
function show(id,focus=false){
 if(!sections.some(s=>s[0]===id))id='general';
 currentSection=id;
 document.querySelectorAll('section,nav button').forEach(x=>x.classList.remove('active'));
 document.querySelectorAll('nav button').forEach(x=>x.removeAttribute('aria-current'));
 const page=document.getElementById(id),button=document.querySelector(`[data-page="${id}"]`);
 page.classList.add('active');button.classList.add('active');button.setAttribute('aria-current','page');
 // No history.replaceState: rewriting a data: document's URL during load swallows WebView2's NavigationCompleted.
 if(focus){const title=page.querySelector('h1');title.tabIndex=-1;title.focus({preventScroll:true});window.scrollTo(0,0)}
}
function applyAccent(value){
 if(!/^#[0-9a-f]{6}$/i.test(value||''))return;
 const rgb=value.slice(1).match(/../g).map(x=>parseInt(x,16)/255);
 const linear=rgb.map(x=>x<=.04045?x/12.92:((x+.055)/1.055)**2.4);
 const l=linear[0]*.2126+linear[1]*.7152+linear[2]*.0722;
 document.documentElement.style.setProperty('--accent',value);
 document.documentElement.style.setProperty('--on-accent',l>.179?'#000000':'#ffffff');
}

const navPaths={
 general:'<path d="M4 7h16M4 17h16"/><circle cx="9" cy="7" r="3"/><circle cx="15" cy="17" r="3"/>',
 appearance:'<path d="M12 3a9 9 0 1 0 0 18h1a2 2 0 0 0 1-4c-1-1 0-2 1-2h2a4 4 0 0 0 4-4c0-5-4-8-9-8Z"/><path d="M7 10h.01M10 6h.01M15 7h.01"/>',
 downloads:'<path d="M12 3v12m-5-5 5 5 5-5M4 16v5h16v-5"/>',
 privacy:'<rect x="5" y="10" width="14" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3m-4 5v2"/>',
 shield:'<path d="m12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6l8-3Z"/><path d="m8 12 3 3 5-6"/>',
 memory:'<rect x="6" y="6" width="12" height="12" rx="2"/><path d="M9 1v5m6-5v5M9 18v5m6-5v5M1 9h5m-5 6h5m12-6h5m-5 6h5M10 10h4v4h-4z"/>',
 extensions:'<path d="M8 3h4a3 3 0 1 1 6 0h3v6a3 3 0 1 0 0 6v6h-6a3 3 0 1 0-6 0H3v-6a3 3 0 1 0 0-6V3h5Z"/>',
 permissions:'<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c5 5 5 13 0 18-5-5-5-13 0-18Z"/>',
 clear:'<path d="M3 6h18M9 6V3h6v3M5 6l1 15h12l1-15M10 10v7m4-7v7"/>',
 data:'<path d="M3 7h7l2-3h9v16H3V7Z"/><path d="M12 10v7m-3-3 3 3 3-3"/>',
 profiles:'<circle cx="12" cy="8" r="4"/><path d="M4 21v-2a8 8 0 0 1 16 0v2"/>'
};
const navIcon=id=>`<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${navPaths[id]}</svg>`;
document.getElementById('nav').innerHTML=sections.map(([id,n,icon])=>`<button data-page="${id}"><span class="nav-icon" aria-hidden="true">${navIcon(id)}</span><span>${n}</span></button>`).join('');document.querySelectorAll('[data-page]').forEach(b=>b.onclick=()=>{document.getElementById('settingsSearch').value='';filterSettings('');show(b.dataset.page,true)});show(location.hash.slice(1)&&document.getElementById(location.hash.slice(1))?location.hash.slice(1):'general');
document.querySelectorAll('[data-setting]').forEach(el=>{const key=lower(el.dataset.setting),v=initial.settings[key];if(el.dataset.list)el.value=(v||[]).join('\n');else if(el.type==='checkbox')el.checked=!!v;else el.value=v??'';el.addEventListener('change',()=>{if(!el.checkValidity()){el.reportValidity();return}let value=el.dataset.list?el.value.split(/\r?\n|,/).map(x=>x.trim()).filter(Boolean):el.type==='checkbox'?el.checked:el.type==='number'?Number(el.value):el.value;if(el.dataset.setting==='ThemeMode')document.documentElement.dataset.theme=value;if(el.dataset.setting==='AccentColor')applyAccent(value);if(el.dataset.setting==='ReduceMotion')document.documentElement.classList.toggle('reduce-motion',!!value);post({type:'aero-settings-set',name:el.dataset.setting,value});toast('Сохранение…')})});
const memorySize=n=>n<1048576?`${Math.max(0,Math.round(n/1024))} КБ`:`${(n/1048576).toFixed(n<104857600?1:0)} МБ`;const memory=initial.memorySaver;document.getElementById('memoryCurrent').textContent=`Сейчас ${memorySize(memory.currentBytes)}`;document.getElementById('memoryForecast').textContent=`Можно освободить ≈ ${memorySize(memory.estimatedSavingsBytes)}`;const protectedParts=[memory.protectedDownloads?`${memory.protectedDownloads} с загрузкой`:'',memory.protectedAudio?`${memory.protectedAudio} с аудио`:'',memory.protectedForms?`${memory.protectedForms} с формой`:''].filter(Boolean);document.getElementById('memoryStats').textContent=`${memory.eligibleTabs} вкладок доступно для оптимизации · ${memory.sleepingTabs} спят · ${memory.unloadedTabs} выгружено${protectedParts.length?' · Защищено: '+protectedParts.join(', '):''}`;
const homeBackgroundPath=initial.settings.homeBackgroundImagePath||'';document.getElementById('homeBackgroundPath').textContent=homeBackgroundPath?homeBackgroundPath:'Картинка ещё не выбрана.';
document.querySelectorAll('[data-action]').forEach(b=>b.onclick=()=>post({type:'aero-settings-action',action:b.dataset.action}));document.querySelectorAll('[data-tab-preset]').forEach(b=>b.onclick=()=>post({type:'aero-settings-action',action:'tab-style-preset',preset:b.dataset.tabPreset}));
document.getElementById('shieldStats').textContent=`${initial.shield.blockedSession} заблокировано за сеанс · ${initial.shield.networkRules} сетевых и ${initial.shield.cosmeticRules} косметических правил · ${initial.shield.engine}${initial.shield.lastError?' · Ошибка: '+initial.shield.lastError:''}`;
const ext=document.getElementById('extensionList');ext.innerHTML=initial.extensions.length?initial.extensions.map(x=>`<div class="list-item"><div class="avatar">${esc(x.name.slice(0,1).toUpperCase())}</div><div><b>${esc(x.name)}</b><div class="meta">${x.isEnabled?'Включено':'Отключено'}${x.permissions.length?' · '+esc(x.permissions.slice(0,4).join(', ')):''}</div></div><div class="actions"><button class="action" data-ext="toggle" data-id="${esc(x.id)}">${x.isEnabled?'Отключить':'Включить'}</button><button class="action" data-ext="pin" data-id="${esc(x.id)}">${x.isPinned?'Открепить':'Закрепить'}</button>${x.hasHostAccess?`<button class="action" data-ext="${x.allSitesAccess?'all-sites-off':'all-sites-on'}" data-id="${esc(x.id)}">${x.allSitesAccess?'Ограничить сайты':'Разрешить сайты'}</button>`:''}${x.hasUserScriptsPermission?`<button class="action" data-ext="${x.userScriptsAllowed?'user-scripts-off':'user-scripts-on'}" data-id="${esc(x.id)}">${x.userScriptsAllowed?'Запретить user scripts':'Разрешить user scripts'}</button>`:''}<button class="action" data-ext="open" data-id="${esc(x.id)}">Настройки</button><button class="action danger" data-ext="remove" data-id="${esc(x.id)}">Удалить</button></div></div>`).join(''):'<p class="hint">Расширения пока не установлены.</p>';document.querySelectorAll('[data-ext]').forEach(b=>b.onclick=()=>{if(b.dataset.ext==='remove'&&!confirm('Удалить расширение?'))return;post({type:'aero-settings-extension',action:b.dataset.ext,id:b.dataset.id})});
const perms=[['CameraPermissionDefault','Камера'],['MicrophonePermissionDefault','Микрофон'],['GeolocationPermissionDefault','Геолокация'],['NotificationsPermissionDefault','Уведомления'],['PopupsPermissionDefault','Всплывающие окна'],['RedirectsPermissionDefault','Перенаправления'],['ClipboardPermissionDefault','Clipboard'],['MidiPermissionDefault','MIDI'],['AutomaticDownloadsPermissionDefault','Автоматические загрузки']];document.getElementById('permissionDefaults').innerHTML=perms.map(([k,n])=>`<div class="row"><div class="label"><b>${n}</b></div><select data-perm="${k}"><option value="Ask">Спрашивать</option><option value="Allow">Разрешить</option><option value="Block">Блокировать</option></select></div>`).join('');document.querySelectorAll('[data-perm]').forEach(el=>{el.value=initial.settings[lower(el.dataset.perm)];el.onchange=()=>post({type:'aero-settings-set',name:el.dataset.perm,value:el.value})});
document.getElementById('permissionList').innerHTML=initial.permissions.length?initial.permissions.map(p=>`<div class="list-item"><div class="avatar nav-icon" aria-hidden="true">&#xE72E;</div><div><b>${esc(p.origin)}</b><div class="meta">${perms.map(([k,n])=>p[lower(k.replace('PermissionDefault',''))]!=='Ask'?n+': '+p[lower(k.replace('PermissionDefault',''))]:'').filter(Boolean).join(' · ')||'Спрашивать'}</div></div></div>`).join(''):'<p class="hint">Нет сохранённых исключений.</p>';
const clearNames=[['history','История'],['cookies','Cookies'],['cache','Кэш'],['localStorage','localStorage'],['indexedDb','IndexedDB'],['permissions','Разрешения'],['passwords','Пароли'],['downloads','Загрузки']];document.getElementById('clearItems').innerHTML=clearNames.map(([id,n])=>`<label><input type="checkbox" value="${id}" ${id==='history'||id==='cache'?'checked':''}> ${n}</label>`).join('');document.getElementById('clearButton').onclick=()=>{if(!document.querySelector('#clearItems input:checked')){toast('Выберите данные для удаления.',true);return}if(!confirm('Удалить выбранные данные без возможности восстановления?'))return;post({type:'aero-settings-action',action:'clear-data',range:document.getElementById('clearRange').value,items:[...document.querySelectorAll('#clearItems input:checked')].map(x=>x.value)})};
const importNames=[['bookmarks','Закладки'],['history','История'],['tabs','Открытые вкладки'],['search','Поисковая система'],['home','Домашняя страница'],['settings','Настройки'],['extensions','Совместимые расширения']];const exportNames=[['bookmarks','Закладки'],['history','История'],['settings','Настройки'],['tabs','Список вкладок'],['workspaces','Workspaces'],['permissions','Разрешения'],['shield','Shield-исключения']];const fillChecks=(id,items)=>document.getElementById(id).innerHTML=items.map(([value,name])=>`<label><input type="checkbox" value="${value}" checked> ${name}</label>`).join('');const checked=id=>[...document.querySelectorAll(`#${id} input:checked`)].map(x=>x.value);fillChecks('importItems',importNames);fillChecks('exportItems',exportNames);const importBrowsers=['Chrome','Edge','Yandex','Brave','Vivaldi','Opera','Firefox'];importBrowsers.forEach(n=>document.getElementById('import'+n).onclick=()=>post({type:'aero-settings-action',action:'import-'+n.toLowerCase(),items:checked('importItems')}));document.getElementById('viewPasswords').onclick=()=>post({type:'aero-settings-action',action:'view-passwords'});document.getElementById('importPasswords').onclick=()=>post({type:'aero-settings-action',action:'import-passwords-csv'});document.getElementById('exportPasswords').onclick=()=>post({type:'aero-settings-action',action:'export-passwords-csv'});document.getElementById('importCookies').onclick=()=>post({type:'aero-settings-action',action:'import-cookies-file'});document.getElementById('exportData').onclick=()=>post({type:'aero-settings-action',action:'export-data',items:checked('exportItems')});document.getElementById('backupCreate').onclick=()=>post({type:'aero-settings-action',action:'backup-create'});document.getElementById('backupValidate').onclick=()=>post({type:'aero-settings-action',action:'backup-validate'});document.getElementById('backupRestore').onclick=()=>post({type:'aero-settings-action',action:'backup-restore'});document.getElementById('backupRecover').onclick=()=>post({type:'aero-settings-action',action:'backup-recover-latest'});const formatSize=n=>n<1048576?`${Math.max(1,Math.round(n/1024))} КБ`:`${(n/1048576).toFixed(1)} МБ`;document.getElementById('backupList').innerHTML=initial.backups.length?'<div class="card-head"><h2>Последние автоматические копии</h2></div>'+initial.backups.map(x=>`<div class="list-item"><div class="avatar">↻</div><div><b>${esc(x.fileName)}</b><div class="meta">${new Date(x.createdAt).toLocaleString()} · ${formatSize(x.size)}</div></div></div>`).join(''):'<p class="hint">Автоматических копий пока нет.</p>';const syncFolder=initial.settings.syncFolder||'';document.getElementById('syncStatus').textContent=syncFolder?'Папка: '+syncFolder:'Синхронизация выключена.';document.getElementById('syncPasswords').disabled=!syncFolder;document.getElementById('syncDisable').disabled=!syncFolder;document.getElementById('syncSetup').onclick=()=>post({type:'aero-settings-action',action:'sync-setup'});document.getElementById('syncPasswords').onclick=()=>post({type:'aero-settings-action',action:'sync-passwords'});document.getElementById('syncDisable').onclick=()=>{if(confirm('Отключить синхронизацию на этом компьютере?'))post({type:'aero-settings-action',action:'sync-disable'})};if(initial.isPrivate){['syncSetup','syncPasswords','syncDisable','importChrome','importEdge','importYandex','importBrave','importVivaldi','importOpera','importFirefox','importPasswords','exportPasswords','importCookies','backupCreate','backupRestore','backupRecover'].forEach(id=>document.getElementById(id).disabled=true)}
document.getElementById('profilePath').textContent=initial.profile.isGuest?'Временный гостевой каталог':initial.profile.webViewDataFolder;const avatarOptions=initial.profileIcons.map(icon=>`<option value="${esc(icon)}">${esc(icon)}</option>`).join('');document.getElementById('profileIcon').innerHTML=avatarOptions;document.getElementById('newProfileIcon').innerHTML=avatarOptions;document.getElementById('profileIcon').value=initial.profile.icon;document.getElementById('newProfileIcon').value=initial.profileIcons[1]||initial.profileIcons[0];document.getElementById('profileName').value=initial.profile.name;document.getElementById('profileSave').onclick=()=>post({type:'aero-settings-profile',action:'update',id:initial.profile.id,name:document.getElementById('profileName').value,icon:document.getElementById('profileIcon').value});document.querySelector('[data-profile-action="guest"]').onclick=()=>post({type:'aero-settings-profile',action:'guest'});if(initial.profile.isGuest){['syncSetup','syncPasswords','syncDisable','importChrome','importEdge','importYandex','importBrave','importVivaldi','importOpera','importFirefox','importPasswords','exportPasswords','importCookies','backupCreate','backupRestore','backupRecover'].forEach(id=>document.getElementById(id).disabled=true)}
document.getElementById('profileList').innerHTML=initial.profiles.map(p=>`<div class="list-item"><div class="avatar">${esc(p.icon)}</div><div><b>${esc(p.name)}</b><div class="meta">${p.id===initial.profile.id?'Текущий · ':''}${p.isDefault?'Основной профиль':'Отдельный профиль'}</div></div><div class="actions">${p.id!==initial.profile.id?`<button class="action" data-profile="switch" data-id="${p.id}">Открыть</button>`:''}${!p.isDefault?`<button class="action danger" data-profile="delete" data-id="${p.id}">Удалить</button>`:''}</div></div>`).join('');document.querySelectorAll('[data-profile]').forEach(b=>b.onclick=()=>{if(b.dataset.profile==='delete'&&!confirm('Удалить профиль и все его данные?'))return;post({type:'aero-settings-profile',action:b.dataset.profile,id:b.dataset.id})});
document.getElementById('profileCreate').onclick=()=>post({type:'aero-settings-profile',action:'create',name:document.getElementById('newProfileName').value,icon:document.getElementById('newProfileIcon').value});
let toastTimer;function toast(message,error=false){const t=document.getElementById('toast');t.setAttribute('role',error?'alert':'status');t.textContent=message;t.className='toast show'+(error?' error':'');clearTimeout(toastTimer);toastTimer=setTimeout(()=>t.className='toast',3200)}window.aeroSettingsStatus=x=>toast(x.status,x.isError);

// Associate generated settings fields with their visible label and helper text.
document.querySelectorAll('.row').forEach((row,index)=>{
 const label=row.querySelector('.label b'),hint=row.querySelector('.label span');
 if(label)label.id=label.id||`setting-label-${index}`;
 if(hint)hint.id=hint.id||`setting-hint-${index}`;
 row.querySelectorAll('input,select,textarea').forEach(el=>{
  if(label)el.setAttribute('aria-labelledby',label.id);
  if(hint)el.setAttribute('aria-describedby',hint.id);
 });
});
function filterSettings(query){
 const q=query.trim().toLocaleLowerCase();let total=0;
 document.querySelectorAll('main section').forEach(section=>{
  const matches=!q||section.textContent.toLocaleLowerCase().includes(q);
  section.classList.toggle('active',!!q&&matches);
  document.querySelector(`[data-page="${section.id}"]`).hidden=!matches;
  section.querySelectorAll('.card').forEach(card=>{
   card.hidden=!!q&&!card.textContent.toLocaleLowerCase().includes(q)&&!section.querySelector('h1').textContent.toLocaleLowerCase().includes(q);
  });
  if(matches)total++;
 });
 document.getElementById('settingsSearchEmpty').hidden=!q||total>0;
 if(!q)show(currentSection);
}
document.getElementById('settingsSearch').addEventListener('input',e=>filterSettings(e.target.value));
window.addEventListener('hashchange',()=>{if(!document.getElementById('settingsSearch').value)show(location.hash.slice(1))});
</script></body></html>
""";
}
