using Aero.Search;
using System.Text.Json;

namespace Aero.App.Settings;

public enum AeroThemeMode
{
    Dark,
    Light,
    System
}

public enum NewTabMode
{
    AeroHome,
    Google,
    Custom
}

public enum StartupBehavior
{
    NewTab,
    RestoreSession,
    HomePage
}

public enum OpenLinksBehavior
{
    NewTab,
    CurrentTab,
    NewWindow
}

public enum InterfaceDensity
{
    Comfortable,
    Compact
}

public enum TabSizeMode
{
    Compact,
    Medium,
    Large
}

public enum TabBarBackgroundStyle
{
    Default,
    Solid,
    Gradient,
    Mountains
}

public enum TabShapeStyle
{
    Connected,
    Rounded,
    Pill,
    Square,
    Custom
}

public enum TabSurfaceStyle
{
    Theme,
    Solid,
    Accent,
    Gradient,
    Acrylic
}

public enum TabAnimationStyle
{
    None,
    Subtle,
    Scale,
    Slide,
    Bounce
}

public enum TabCloseButtonMode
{
    HoverOrActive,
    ActiveOnly,
    Always,
    Never
}

public enum TabActiveIndicatorStyle
{
    Top,
    Bottom,
    Outline,
    None
}

public enum TabTextAlignmentMode
{
    Left,
    Center
}

public enum TabBarBlurStyle
{
    Off,
    Soft,
    Strong
}

public enum HomeBackgroundStyle
{
    Default,
    Solid,
    Gradient,
    Mountains,
    Image
}

public enum HomeBackgroundImageFit
{
    Cover,
    Contain,
    Center,
    Tile
}

public enum PermissionDefault
{
    Ask,
    Allow,
    Block
}

public enum SpeedMode
{
    Maximum,
    Balanced,
    Compatibility
}

public enum ShieldMode
{
    Standard,
    Strict
}

public enum ShieldEngineMode
{
    Hybrid,
    Advanced,
    Native
}

public enum MemorySaverMode
{
    Maximum,
    Balanced,
    Off
}

public sealed class BrowserSettings
{
    private string? _settingsFile;
    private bool _persistToDisk;

    public BrowserSettings()
    {
    }

    private BrowserSettings(string? settingsFile, bool persistToDisk)
    {
        _settingsFile = settingsFile;
        _persistToDisk = persistToDisk;
    }

    public int OnboardingVersion { get; set; }

    public string AppLanguage { get; set; } =
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals(
            "ru",
            StringComparison.OrdinalIgnoreCase)
                ? "ru"
                : "en";

    public AeroThemeMode ThemeMode { get; set; } = AeroThemeMode.Dark;

    public NewTabMode NewTabMode { get; set; } = NewTabMode.AeroHome;

    public SearchEngineKind SearchEngine { get; set; } = SearchEngineKind.Google;

    public string CustomSearchUrl { get; set; } = "https://www.google.com/search?q={query}";

    public string HomePageUrl { get; set; } = "aero://home";

    public string CustomNewTabUrl { get; set; } = "aero://home";

    public StartupBehavior StartupBehavior { get; set; } = StartupBehavior.RestoreSession;

    public OpenLinksBehavior OpenLinksBehavior { get; set; } = OpenLinksBehavior.NewTab;

    public SpeedMode SpeedMode { get; set; } = SpeedMode.Maximum;

    public ShieldMode ShieldMode { get; set; } = ShieldMode.Standard;

    public ShieldEngineMode ShieldEngineMode { get; set; } = ShieldEngineMode.Hybrid;

    public MemorySaverMode MemorySaverMode { get; set; } = MemorySaverMode.Balanced;

    public InterfaceDensity InterfaceDensity { get; set; } = InterfaceDensity.Comfortable;

    public TabSizeMode TabSize { get; set; } = TabSizeMode.Medium;

    public int TabBarHeight { get; set; } = 40;

    public TabBarBackgroundStyle TabBarBackgroundStyle { get; set; } =
        TabBarBackgroundStyle.Default;

    public string TabBarBackgroundColor { get; set; } = "#17212B";

    public int TabBarBackgroundOpacity { get; set; } = 92;

    public TabBarBlurStyle TabBarBlur { get; set; } = TabBarBlurStyle.Off;

    public TabShapeStyle TabShape { get; set; } = TabShapeStyle.Connected;

    public int TabCornerRadius { get; set; } = 8;

    public int TabHorizontalPadding { get; set; } = 9;

    public int TabSpacing { get; set; } = 2;

    public int TabStripPadding { get; set; }

    public int PinnedTabWidth { get; set; } = 46;

    public TabSurfaceStyle TabSurfaceStyle { get; set; } = TabSurfaceStyle.Theme;

    public string TabActiveColor { get; set; } = "#2A3036";

    public string TabHoverColor { get; set; } = "#22282D";

    public string TabInactiveColor { get; set; } = "#17212B";

    public int TabActiveOpacity { get; set; } = 100;

    public int TabHoverOpacity { get; set; } = 100;

    public int TabInactiveOpacity { get; set; }

    public int TabBorderThickness { get; set; }

    public int TabBorderOpacity { get; set; } = 100;

    public TabAnimationStyle TabAnimation { get; set; } = TabAnimationStyle.Subtle;

    public int TabAnimationDuration { get; set; } = 180;

    public int TabHoverLift { get; set; }

    public int TabInactiveScalePercent { get; set; } = 100;

    public int TabIconSize { get; set; } = 16;

    public int TabFontSize { get; set; } = 12;

    public bool TabBoldActiveText { get; set; }

    public bool TabUseAccentText { get; set; }

    public TabCloseButtonMode TabCloseButtonMode { get; set; } =
        TabCloseButtonMode.HoverOrActive;

    public bool ShowTabSeparators { get; set; } = true;

    public TabActiveIndicatorStyle TabActiveIndicator { get; set; } =
        TabActiveIndicatorStyle.None;

    public int TabIndicatorThickness { get; set; } = 2;

    public TabTextAlignmentMode TabTextAlignment { get; set; } =
        TabTextAlignmentMode.Left;

    public HomeBackgroundStyle HomeBackgroundStyle { get; set; } =
        HomeBackgroundStyle.Default;

    public string HomeBackgroundColor { get; set; } = "#17212B";

    public string HomeBackgroundSecondaryColor { get; set; } = "#35506A";

    public int HomeBackgroundOpacity { get; set; } = 100;

    public int HomeBackgroundBlur { get; set; }

    public HomeBackgroundImageFit HomeBackgroundImageFit { get; set; } =
        HomeBackgroundImageFit.Cover;

    public string HomeBackgroundImagePath { get; set; } = string.Empty;

    public bool HomeBackgroundAdaptiveOverlay { get; set; } = true;

    public string AccentColor { get; set; } = "#7CB7FF";

    public bool ShowHomeButton { get; set; } = true;

    public bool ShowBookmarksBar { get; set; }

    public bool ShowQuickLinks { get; set; } = true;

    public bool RestorePreviousSession { get; set; } = true;

    public bool BlockAdsAndTrackers { get; set; } = true;

    public bool ShieldCosmeticFiltering { get; set; } = true;

    public bool ShieldAutoUpdate { get; set; } = true;

    public bool ShieldYouTubeCleanMode { get; set; } = true;

    public bool CleanTrackingLinks { get; set; } = true;

    public bool SleepBackgroundTabs { get; set; } = true;

    public bool KeepAudioTabsAwake { get; set; } = true;

    public bool KeepDownloadingTabsAwake { get; set; } = true;

    public bool ProtectFilledForms { get; set; } = true;

    public bool ReduceMotion { get; set; }

    public bool SavePasswords { get; set; } = true;

    public bool AutofillForms { get; set; } = true;

    public string DefaultDownloadFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads");

    public bool AskDownloadPathEveryTime { get; set; } = true;

    public bool OpenDownloadsPanelAutomatically { get; set; } = true;

    public bool WarnDangerousFiles { get; set; } = true;

    public bool AutomaticBackupsEnabled { get; set; } = true;

    public int AutomaticBackupIntervalHours { get; set; } = 24;

    public int BackupRetentionCount { get; set; } = 5;

    public DateTimeOffset? LastAutomaticBackupUtc { get; set; }

    public bool HistoryEnabled { get; set; } = true;

    public bool CookiesEnabled { get; set; } = true;

    public bool BlockThirdPartyCookies { get; set; } = true;

    public bool CacheEnabled { get; set; } = true;

    public bool ClearDataOnClose { get; set; }

    public bool ClearHistoryOnClose { get; set; }

    public bool ClearCookiesOnClose { get; set; }

    public bool ClearCacheOnClose { get; set; }

    public bool ClearSiteDataOnClose { get; set; }

    public bool DoNotTrack { get; set; } = true;

    public bool HttpsFirst { get; set; } = true;

    public int MemoryLimitMb { get; set; }

    public PermissionDefault CameraPermissionDefault { get; set; } = PermissionDefault.Ask;

    public PermissionDefault MicrophonePermissionDefault { get; set; } = PermissionDefault.Ask;

    public string PreferredMicrophoneName { get; set; } = string.Empty;

    public PermissionDefault GeolocationPermissionDefault { get; set; } = PermissionDefault.Ask;

    public PermissionDefault NotificationsPermissionDefault { get; set; } = PermissionDefault.Ask;

    public PermissionDefault PopupsPermissionDefault { get; set; } = PermissionDefault.Ask;

    public PermissionDefault RedirectsPermissionDefault { get; set; } = PermissionDefault.Allow;

    public PermissionDefault ClipboardPermissionDefault { get; set; } = PermissionDefault.Ask;

    public PermissionDefault MidiPermissionDefault { get; set; } = PermissionDefault.Ask;

    public PermissionDefault AutomaticDownloadsPermissionDefault { get; set; } = PermissionDefault.Ask;

    public int BackgroundTabSleepSeconds { get; set; } = 20;

    public int BackgroundTabDiscardMinutes { get; set; } = 2;

    public List<string> ShieldAllowlist { get; set; } = [];

    public List<string> ShieldCustomRules { get; set; } = [];

    public List<string> PrivacySiteExceptions { get; set; } = [];

    public List<string> ThirdPartyCookieExceptions { get; set; } = [];

    public List<string> TemporaryCookieSites { get; set; } = [];

    public List<string> MemorySaverSiteExceptions { get; set; } = [];

    public bool ResourceBudgetEnabled { get; set; } = true;

    // Share of all CPU cores; 10 % is about one busy core on an 8-core machine.
    public int BackgroundCpuBudgetPercent { get; set; } = 10;

    public int BackgroundMemoryBudgetMb { get; set; } = 1024;

    public List<string> LockedSites { get; set; } = [];

    public bool WipeLockedSiteSessions { get; set; } = true;

    // Opt-in: it asks for Windows Hello on every launch.
    public bool ProtectCookiesWithHello { get; set; }

    public static BrowserSettings Load(
        string? storageRoot = null,
        bool persistToDisk = true)
    {
        string root = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Aero");
        string settingsFile = Path.Combine(root, "settings.json");

        try
        {
            if (!persistToDisk || !File.Exists(settingsFile))
            {
                return new BrowserSettings(settingsFile, persistToDisk);
            }

            string json = File.ReadAllText(settingsFile);
            BrowserSettings settings = JsonSerializer.Deserialize<BrowserSettings>(json) ?? new BrowserSettings();
            settings._settingsFile = settingsFile;
            settings._persistToDisk = persistToDisk;
            settings.Normalize();
            return settings;
        }
        catch
        {
            return new BrowserSettings(settingsFile, persistToDisk);
        }
    }

    public void Save()
    {
        Normalize();
        if (!_persistToDisk || string.IsNullOrWhiteSpace(_settingsFile))
        {
            return;
        }

        string? directory = Path.GetDirectoryName(_settingsFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(
            this,
            new JsonSerializerOptions { WriteIndented = true });

        string temporaryPath = _settingsFile + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _settingsFile, overwrite: true);
    }

    public bool IsShieldAllowedForHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) &&
        ShieldAllowlist.Any(item => HostMatches(item, host));

    public void SetShieldAllowedForHost(string host, bool allowed)
    {
        string normalizedHost = host.Trim().TrimEnd('.').ToLowerInvariant();
        ShieldAllowlist.RemoveAll(item =>
            item.Equals(normalizedHost, StringComparison.OrdinalIgnoreCase));

        if (allowed)
        {
            ShieldAllowlist.Add(normalizedHost);
        }
    }

    private static bool HostMatches(string allowedHost, string currentHost)
    {
        string allowed = allowedHost.Trim().TrimEnd('.');
        string current = currentHost.Trim().TrimEnd('.');

        return current.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
            current.EndsWith('.' + allowed, StringComparison.OrdinalIgnoreCase);
    }

    private void Normalize()
    {
        AppLanguage = AppLanguage.Equals("ru", StringComparison.OrdinalIgnoreCase)
            ? "ru"
            : "en";
        BackgroundTabSleepSeconds = Math.Clamp(BackgroundTabSleepSeconds, 15, 3600);
        BackgroundTabDiscardMinutes = Math.Clamp(BackgroundTabDiscardMinutes, 1, 240);
        MemoryLimitMb = Math.Clamp(MemoryLimitMb, 0, 32768);
        BackgroundCpuBudgetPercent = Math.Clamp(BackgroundCpuBudgetPercent, 2, 100);
        BackgroundMemoryBudgetMb = Math.Clamp(BackgroundMemoryBudgetMb, 128, 16384);
        AutomaticBackupIntervalHours = Math.Clamp(AutomaticBackupIntervalHours, 1, 168);
        BackupRetentionCount = Math.Clamp(BackupRetentionCount, 2, 20);
        TabBarHeight = Math.Clamp(TabBarHeight, 36, 60);
        TabBarBackgroundOpacity = Math.Clamp(TabBarBackgroundOpacity, 20, 100);
        TabBarBackgroundColor = NormalizeColor(TabBarBackgroundColor, "#17212B");
        TabCornerRadius = Math.Clamp(TabCornerRadius, 0, 30);
        TabHorizontalPadding = Math.Clamp(TabHorizontalPadding, 2, 28);
        TabSpacing = Math.Clamp(TabSpacing, 0, 14);
        TabStripPadding = Math.Clamp(TabStripPadding, 0, 40);
        PinnedTabWidth = Math.Clamp(PinnedTabWidth, 36, 80);
        TabActiveColor = NormalizeColor(TabActiveColor, "#2A3036");
        TabHoverColor = NormalizeColor(TabHoverColor, "#22282D");
        TabInactiveColor = NormalizeColor(TabInactiveColor, "#17212B");
        TabActiveOpacity = Math.Clamp(TabActiveOpacity, 0, 100);
        TabHoverOpacity = Math.Clamp(TabHoverOpacity, 0, 100);
        TabInactiveOpacity = Math.Clamp(TabInactiveOpacity, 0, 100);
        TabBorderThickness = Math.Clamp(TabBorderThickness, 0, 4);
        TabBorderOpacity = Math.Clamp(TabBorderOpacity, 0, 100);
        TabAnimationDuration = Math.Clamp(TabAnimationDuration, 0, 600);
        TabHoverLift = Math.Clamp(TabHoverLift, 0, 8);
        TabInactiveScalePercent = Math.Clamp(TabInactiveScalePercent, 85, 100);
        TabIconSize = Math.Clamp(TabIconSize, 10, 24);
        TabFontSize = Math.Clamp(TabFontSize, 9, 18);
        TabIndicatorThickness = Math.Clamp(TabIndicatorThickness, 1, 6);
        HomeBackgroundOpacity = Math.Clamp(HomeBackgroundOpacity, 20, 100);
        HomeBackgroundBlur = Math.Clamp(HomeBackgroundBlur, 0, 24);
        HomeBackgroundColor = NormalizeColor(HomeBackgroundColor, "#17212B");
        HomeBackgroundSecondaryColor = NormalizeColor(
            HomeBackgroundSecondaryColor,
            "#35506A");
        HomeBackgroundImagePath = (HomeBackgroundImagePath ?? string.Empty).Trim();
        AccentColor = NormalizeColor(AccentColor, "#7CB7FF");
        HomePageUrl = NormalizeInternalOrWebUrl(HomePageUrl, "aero://home");
        CustomNewTabUrl = NormalizeInternalOrWebUrl(CustomNewTabUrl, "aero://home");
        CustomSearchUrl = string.IsNullOrWhiteSpace(CustomSearchUrl)
            ? "https://www.google.com/search?q={query}"
            : CustomSearchUrl.Trim();
        DefaultDownloadFolder = string.IsNullOrWhiteSpace(DefaultDownloadFolder)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads")
            : DefaultDownloadFolder.Trim();
        PreferredMicrophoneName = (PreferredMicrophoneName ?? string.Empty).Trim();
        ShieldAllowlist = NormalizeHosts(ShieldAllowlist);
        ShieldCustomRules ??= [];
        PrivacySiteExceptions = NormalizeHosts(PrivacySiteExceptions);
        ThirdPartyCookieExceptions = NormalizeHosts(ThirdPartyCookieExceptions);
        TemporaryCookieSites = NormalizeHosts(TemporaryCookieSites);
        MemorySaverSiteExceptions = NormalizeHosts(MemorySaverSiteExceptions);
    }

    private static List<string> NormalizeHosts(List<string>? values) =>
        (values ?? [])
            .Select(value => value.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();

    private static string NormalizeColor(string? value, string fallback)
    {
        string color = value?.Trim() ?? string.Empty;
        if (color.Length == 7 &&
            color[0] == '#' &&
            color[1..].All(Uri.IsHexDigit))
        {
            return color.ToUpperInvariant();
        }

        return fallback;
    }

    private static string NormalizeInternalOrWebUrl(string? value, string fallback)
    {
        string candidate = value?.Trim() ?? string.Empty;
        if (candidate.Equals("aero://home", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("aero://settings", StringComparison.OrdinalIgnoreCase) ||
            Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) &&
            uri.Scheme is "http" or "https")
        {
            return candidate;
        }

        return fallback;
    }
}
