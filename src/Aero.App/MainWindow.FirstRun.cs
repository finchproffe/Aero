using Aero.App.Data;
using Aero.App.Localization;
using Aero.App.Settings;
using Aero.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Aero.App;

public sealed partial class MainWindow
{
    private const int CurrentOnboardingVersion = 1;
    private ChromiumBrowserKind? _pendingFirstRunImport;
    private bool _openDefaultAppsAfterOnboarding;

    private async Task ShowFirstRunExperienceIfNeededAsync()
    {
        if (_isPrivateWindow ||
            _browserProfile.IsGuest ||
            _settings.OnboardingVersion >= CurrentOnboardingVersion)
        {
            return;
        }

        ComboBox language = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        language.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        language.Items.Add(new ComboBoxItem { Content = "Русский", Tag = "ru" });
        language.SelectedIndex = _settings.AppLanguage == "ru" ? 1 : 0;

        ComboBox search = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (SearchEngineKind engine in new[]
        {
            SearchEngineKind.Google,
            SearchEngineKind.Bing,
            SearchEngineKind.DuckDuckGo,
            SearchEngineKind.Brave
        })
        {
            search.Items.Add(new ComboBoxItem { Content = engine.ToString(), Tag = engine });
        }
        search.SelectedIndex = Math.Max(0, Array.IndexOf(
            new[]
            {
                SearchEngineKind.Google,
                SearchEngineKind.Bing,
                SearchEngineKind.DuckDuckGo,
                SearchEngineKind.Brave
            },
            _settings.SearchEngine));

        ComboBox theme = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        ComboBoxItem systemTheme = new() { Tag = AeroThemeMode.System };
        ComboBoxItem lightTheme = new() { Tag = AeroThemeMode.Light };
        ComboBoxItem darkTheme = new() { Tag = AeroThemeMode.Dark };
        theme.Items.Add(systemTheme);
        theme.Items.Add(lightTheme);
        theme.Items.Add(darkTheme);
        theme.SelectedIndex = _settings.ThemeMode switch
        {
            AeroThemeMode.Light => 1,
            AeroThemeMode.Dark => 2,
            _ => 0
        };

        ToggleSwitch memorySaver = new()
        {
            IsOn = _settings.MemorySaverMode != MemorySaverMode.Off
        };
        TextBlock memoryDescription = BuildFirstRunDescription();
        ToggleSwitch shield = new() { IsOn = _settings.BlockAdsAndTrackers };
        TextBlock shieldDescription = BuildFirstRunDescription();

        ComboBox import = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        ComboBoxItem importNone = new() { Tag = string.Empty };
        ComboBoxItem importChrome = new() { Tag = ChromiumBrowserKind.Chrome };
        ComboBoxItem importEdge = new() { Tag = ChromiumBrowserKind.Edge };
        import.Items.Add(importNone);
        import.Items.Add(importChrome);
        import.Items.Add(importEdge);
        foreach ((ChromiumBrowserKind kind, string name) in new[]
                 {
                     (ChromiumBrowserKind.Yandex, "Yandex Browser"),
                     (ChromiumBrowserKind.Brave, "Brave"),
                     (ChromiumBrowserKind.Vivaldi, "Vivaldi"),
                     (ChromiumBrowserKind.Opera, "Opera"),
                     (ChromiumBrowserKind.Firefox, "Firefox")
                 })
        {
            // Offer only browsers that are actually installed for this user.
            if (Directory.Exists(ChromiumImportService.GetBrowserRoot(kind)))
            {
                import.Items.Add(new ComboBoxItem { Tag = kind, Content = name });
            }
        }
        import.SelectedIndex = 0;

        CheckBox defaultBrowser = new();
        TextBlock vaultTitle = new()
        {
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        TextBlock vaultDescription = BuildFirstRunDescription();

        Border vaultCard = new()
        {
            Padding = new Thickness(13),
            Background = GetTransparentBrush(),
            BorderBrush = GetSoftBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Child = new StackPanel
            {
                Spacing = 4,
                Children = { vaultTitle, vaultDescription }
            }
        };

        TextBlock subtitle = BuildFirstRunDescription();
        StackPanel panel = new() { Width = 500, Spacing = 13 };
        panel.Children.Add(subtitle);
        panel.Children.Add(language);
        panel.Children.Add(search);
        panel.Children.Add(theme);
        panel.Children.Add(memorySaver);
        panel.Children.Add(memoryDescription);
        panel.Children.Add(shield);
        panel.Children.Add(shieldDescription);
        panel.Children.Add(import);
        panel.Children.Add(defaultBrowser);
        panel.Children.Add(vaultCard);

        ScrollViewer scroll = new()
        {
            MaxHeight = 620,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel
        };
        ContentDialog dialog = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Content = scroll,
            DefaultButton = ContentDialogButton.Primary
        };

        void RefreshLocalizedText()
        {
            dialog.Title = AeroLocalizer.Get("Onboarding.Title");
            dialog.PrimaryButtonText = AeroLocalizer.Get("Onboarding.Start");
            dialog.CloseButtonText = AeroLocalizer.Get("Common.Cancel");
            subtitle.Text = AeroLocalizer.Get("Onboarding.Subtitle");
            language.Header = AeroLocalizer.Get("Onboarding.Language");
            search.Header = AeroLocalizer.Get("Onboarding.Search");
            theme.Header = AeroLocalizer.Get("Onboarding.Theme");
            systemTheme.Content = AeroLocalizer.Get("Onboarding.ThemeSystem");
            lightTheme.Content = AeroLocalizer.Get("Onboarding.ThemeLight");
            darkTheme.Content = AeroLocalizer.Get("Onboarding.ThemeDark");
            memorySaver.Header = AeroLocalizer.Get("Onboarding.Memory");
            memoryDescription.Text = AeroLocalizer.Get("Onboarding.MemoryDescription");
            shield.Header = AeroLocalizer.Get("Onboarding.Shield");
            shieldDescription.Text = AeroLocalizer.Get("Onboarding.ShieldDescription");
            import.Header = AeroLocalizer.Get("Onboarding.Import");
            importNone.Content = AeroLocalizer.Get("Onboarding.ImportNone");
            importChrome.Content = AeroLocalizer.Get("Onboarding.ImportChrome");
            importEdge.Content = AeroLocalizer.Get("Onboarding.ImportEdge");
            defaultBrowser.Content = AeroLocalizer.Get("Onboarding.DefaultBrowser");
            vaultTitle.Text = AeroLocalizer.Get("Onboarding.VaultTitle");
            vaultDescription.Text = AeroLocalizer.Get("Onboarding.VaultDescription");
        }

        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedItem is ComboBoxItem item && item.Tag is string languageCode)
            {
                AeroLocalizer.Configure(languageCode);
                RefreshLocalizedText();
            }
        };
        RefreshLocalizedText();
        AutomationProperties.SetName(panel, AeroLocalizer.Get("Onboarding.Title"));

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _settings.AppLanguage = language.SelectedItem is ComboBoxItem languageItem &&
                languageItem.Tag is string languageCode
                    ? languageCode
                    : "en";
            _settings.SearchEngine = search.SelectedItem is ComboBoxItem searchItem &&
                searchItem.Tag is SearchEngineKind searchEngine
                    ? searchEngine
                    : SearchEngineKind.Google;
            _settings.ThemeMode = theme.SelectedItem is ComboBoxItem themeItem &&
                themeItem.Tag is AeroThemeMode themeMode
                    ? themeMode
                    : AeroThemeMode.System;
            _settings.MemorySaverMode = memorySaver.IsOn
                ? MemorySaverMode.Balanced
                : MemorySaverMode.Off;
            _settings.SleepBackgroundTabs = memorySaver.IsOn;
            _settings.BlockAdsAndTrackers = shield.IsOn;
            _pendingFirstRunImport = import.SelectedItem is ComboBoxItem importItem &&
                importItem.Tag is ChromiumBrowserKind browser
                    ? browser
                    : null;
            _openDefaultAppsAfterOnboarding = defaultBrowser.IsChecked == true;
        }

        _settings.OnboardingVersion = CurrentOnboardingVersion;
        if (_settings.InterfaceStyle == InterfaceStyle.Material3)
        {
            // The default style also brings its tab preset, as if the user had picked it.
            ApplyInterfaceStyleBundle(InterfaceStyle.Aero);
        }

        _settings.Save();
        AeroLocalizer.Configure(_settings.AppLanguage);
        ApplyAllRuntimeSettings(nameof(BrowserSettings.InterfaceStyle));
        ApplyLocalization();
    }

    private static TextBlock BuildFirstRunDescription() => new()
    {
        FontSize = 11,
        Foreground = (Brush)Application.Current.Resources["AeroMutedTextBrush"],
        TextWrapping = TextWrapping.Wrap
    };

    private async Task RunPendingFirstRunActionsAsync()
    {
        if (_pendingFirstRunImport is ChromiumBrowserKind browser)
        {
            _pendingFirstRunImport = null;
            try
            {
                ChromiumImportResult result = await ChromiumImportService.ImportAsync(
                    browser,
                    new ChromiumImportOptions(
                        Bookmarks: true,
                        History: true,
                        OpenTabs: true,
                        SearchEngine: true,
                        HomePage: true,
                        Settings: false,
                        Extensions: true),
                    _dataStore,
                    _settings);
                await ImportCompatibleExtensionsAsync(result.Extensions);
                foreach (string url in result.OpenTabs.Take(30))
                {
                    if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && IsBrowsableUri(uri))
                    {
                        await CreateTabAsync(
                            uri,
                            selectTab: false,
                            deferWebViewInitialization: true);
                    }
                }
                RenderBookmarksBar();
                ApplyAllRuntimeSettings(nameof(BrowserSettings.SearchEngine));
            }
            catch (Exception exception)
            {
                await ShowErrorDialogAsync(
                    AeroLocalizer.Get("App.Name"),
                    AeroLocalizer.Format("Onboarding.ImportFailed", exception.Message));
            }
        }

        if (_openDefaultAppsAfterOnboarding)
        {
            _openDefaultAppsAfterOnboarding = false;
            await Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
        }
    }
}
