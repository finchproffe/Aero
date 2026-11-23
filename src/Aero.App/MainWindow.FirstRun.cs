using Aero.App.Data;
using Aero.App.Localization;
using Aero.App.Settings;
using Aero.Search;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace Aero.App;

public sealed partial class MainWindow
{
    private const int CurrentOnboardingVersion = 1;
    private ChromiumBrowserKind? _pendingFirstRunImport;
    private bool _openDefaultAppsAfterOnboarding;
    private Grid? _firstRunOverlay;

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
        subtitle.FontSize = 15;
        TextBlock basicsTitle = BuildFirstRunStepTitle();
        TextBlock protectionTitle = BuildFirstRunStepTitle();
        TextBlock importTitle = BuildFirstRunStepTitle();
        StackPanel[] steps =
        [
            BuildFirstRunStep(basicsTitle, language, search, theme),
            BuildFirstRunStep(protectionTitle, memorySaver, memoryDescription, shield, shieldDescription, vaultCard),
            BuildFirstRunStep(importTitle, import, defaultBrowser)
        ];
        // Tall enough for the longest step, so the card does not jump between steps.
        Grid stepHost = new() { MinHeight = 380, Margin = new Thickness(0, 34, 0, 0) };
        foreach (StackPanel step in steps)
        {
            step.Visibility = step == steps[0] ? Visibility.Visible : Visibility.Collapsed;
            step.RenderTransform = new CompositeTransform();
            stepHost.Children.Add(step);
        }

        Border[] dots = steps.Select((_, index) => new Border
        {
            Width = index == 0 ? 22 : 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = index == 0 ? GetChromeBrush("AeroAccentBrush") : GetSoftBorderBrush()
        }).ToArray();
        StackPanel dotsPanel = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (Border dot in dots)
        {
            dotsPanel.Children.Add(dot);
        }

        Button skip = new()
        {
            Height = 34,
            Padding = new Thickness(12, 0, 12, 0),
            Background = GetTransparentBrush(),
            BorderThickness = new Thickness(0),
            Foreground = (Brush)Application.Current.Resources["AeroMutedTextBrush"]
        };
        Button back = new() { Height = 34, Padding = new Thickness(16, 0, 16, 0), Visibility = Visibility.Collapsed };
        Button next = new() { Style = (Style)Application.Current.Resources["PrimaryButtonStyle"], MinWidth = 120 };
        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        actions.Children.Add(skip);
        actions.Children.Add(back);
        actions.Children.Add(next);
        Grid footer = new() { Margin = new Thickness(0, 24, 0, 0) };
        footer.Children.Add(dotsPanel);
        footer.Children.Add(actions);

        TextBlock wordmark = new()
        {
            Text = "Aero",
            FontSize = 76,
            FontWeight = Microsoft.UI.Text.FontWeights.Light,
            CharacterSpacing = -50,
            Foreground = GetChromeBrush("AeroTextBrush")
        };
        StackPanel card = new() { Width = 500, Margin = new Thickness(32) };
        UIElement[] cardParts = [wordmark, subtitle, stepHost, footer];
        foreach (UIElement part in cardParts)
        {
            part.RenderTransform = new CompositeTransform();
            part.RenderTransformOrigin = new Windows.Foundation.Point(0, 0.5);
            card.Children.Add(part);
        }

        // A wash of the accent colour from the top edge, like light at the start of a day.
        Color accent = GetConfiguredAccentColor();
        Rectangle glow = new()
        {
            Height = 460,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0.5, 0),
                EndPoint = new Windows.Foundation.Point(0.5, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(IsDarkTheme() ? (byte)0x50 : (byte)0x40, accent.R, accent.G, accent.B), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0, accent.R, accent.G, accent.B), Offset = 1 }
                }
            }
        };

        Grid overlay = new() { Background = GetChromeBrush("AeroWindowBrush") };
        Grid.SetRowSpan(overlay, Math.Max(1, RootGrid.RowDefinitions.Count));
        Grid.SetColumnSpan(overlay, Math.Max(1, RootGrid.ColumnDefinitions.Count));
        Canvas.SetZIndex(overlay, 1000);
        overlay.Children.Add(glow);
        overlay.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { card }
            }
        });
        // Browser shortcuts (Ctrl+T, zoom) wait until there is a browser behind this screen.
        overlay.ProcessKeyboardAccelerators += (_, args) => args.Handled = true;
        overlay.KeyDown += (_, args) =>
        {
            if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0)
            {
                args.Handled = true;
            }
        };
        _firstRunOverlay = overlay;
        RootGrid.Children.Add(overlay);

        int current = 0;
        TaskCompletionSource<bool> finished = new();

        void RefreshLocalizedText()
        {
            subtitle.Text = AeroLocalizer.Get("Onboarding.Subtitle");
            basicsTitle.Text = AeroLocalizer.Get("Onboarding.StepBasics");
            protectionTitle.Text = AeroLocalizer.Get("Onboarding.StepProtection");
            importTitle.Text = AeroLocalizer.Get("Onboarding.StepImport");
            skip.Content = AeroLocalizer.Get("Onboarding.Skip");
            back.Content = AeroLocalizer.Get("Onboarding.Back");
            next.Content = AeroLocalizer.Get(current == steps.Length - 1 ? "Onboarding.Start" : "Onboarding.Next");
            AutomationProperties.SetName(dotsPanel, AeroLocalizer.Format("Onboarding.StepOf", current + 1, steps.Length));
            AutomationProperties.SetName(overlay, AeroLocalizer.Get("Onboarding.Title"));
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
        // The old step slides out against the direction of travel while the new one follows it in.
        void GoToStep(int index)
        {
            int direction = index > current ? 1 : -1;
            StackPanel leaving = steps[current];
            StackPanel entering = steps[index];
            current = index;
            var leavingTransform = (CompositeTransform)leaving.RenderTransform;
            var enteringTransform = (CompositeTransform)entering.RenderTransform;
            AnimateFirstRun(leaving, "Opacity", leaving.Opacity, 0, 120, completed: () =>
            {
                if (steps[current] != leaving)
                {
                    leaving.Visibility = Visibility.Collapsed;
                }
            });
            AnimateFirstRun(leavingTransform, "TranslateX", leavingTransform.TranslateX, -28 * direction, 140);
            entering.Visibility = Visibility.Visible;
            entering.Opacity = 0;
            AnimateFirstRun(entering, "Opacity", 0, 1, 260, delay: 70);
            AnimateFirstRun(enteringTransform, "TranslateX", 44 * direction, 0, 420, delay: 70, ease: SpringEase());
            for (int dot = 0; dot < dots.Length; dot++)
            {
                AnimateFirstRun(dots[dot], "Width", dots[dot].Width, dot == index ? 22 : 6, 320, ease: SpringEase(), dependent: true);
                dots[dot].Background = dot == index ? GetChromeBrush("AeroAccentBrush") : GetSoftBorderBrush();
            }

            bool backHadFocus = back.FocusState != FocusState.Unfocused;
            back.Visibility = index > 0 ? Visibility.Visible : Visibility.Collapsed;
            skip.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (backHadFocus && index == 0)
            {
                next.Focus(FocusState.Keyboard);
            }
            RefreshLocalizedText();
        }

        next.Click += (_, _) =>
        {
            if (current < steps.Length - 1)
            {
                GoToStep(current + 1);
            }
            else
            {
                finished.TrySetResult(true);
            }
        };
        back.Click += (_, _) => GoToStep(current - 1);
        skip.Click += (_, _) => finished.TrySetResult(false);
        RefreshLocalizedText();

        // Opening: the name rises first, then the sentence, then the questions.
        foreach (UIElement part in cardParts)
        {
            part.Opacity = 0;
        }
        glow.Opacity = 0;
        AnimateFirstRun(glow, "Opacity", 0, 1, 1400, delay: 100);
        AnimateFirstRun(wordmark, "Opacity", 0, 1, 700);
        AnimateFirstRun(wordmark.RenderTransform, "TranslateY", 26, 0, 900, ease: SpringEase());
        AnimateFirstRun(wordmark.RenderTransform, "ScaleX", 0.92, 1, 900, ease: SpringEase());
        AnimateFirstRun(wordmark.RenderTransform, "ScaleY", 0.92, 1, 900, ease: SpringEase());
        int delay = 260;
        foreach (UIElement part in cardParts.Skip(1))
        {
            AnimateFirstRun(part, "Opacity", 0, 1, 500, delay);
            AnimateFirstRun(part.RenderTransform, "TranslateY", 18, 0, 700, delay, SpringEase());
            delay += 110;
        }
        language.Focus(FocusState.Programmatic);

        if (await finished.Task)
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

        // The questions leave; the name stays on screen until the first page is ready
        // (DismissFirstRunOverlay), so setup flows straight into the browser.
        overlay.IsHitTestVisible = false;
        foreach (UIElement part in cardParts.Skip(1))
        {
            AnimateFirstRun(part, "Opacity", part.Opacity, 0, 220);
            AnimateFirstRun(part.RenderTransform, "TranslateY", 0, 10, 220);
        }
        AnimateFirstRun(wordmark.RenderTransform, "TranslateY", 0, 120, 700, delay: 80);
    }

    private void DismissFirstRunOverlay()
    {
        if (_firstRunOverlay is not Grid overlay)
        {
            return;
        }

        _firstRunOverlay = null;
        overlay.IsHitTestVisible = false;
        AnimateFirstRun(overlay, "Opacity", overlay.Opacity, 0, 520, completed: () => RootGrid.Children.Remove(overlay));
        if (overlay.Children.OfType<ScrollViewer>().FirstOrDefault() is ScrollViewer content)
        {
            content.RenderTransform = new CompositeTransform();
            content.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            AnimateFirstRun(content.RenderTransform, "ScaleX", 1, 1.06, 520);
            AnimateFirstRun(content.RenderTransform, "ScaleY", 1, 1.06, 520);
        }
    }

    private static TextBlock BuildFirstRunStepTitle() => new()
    {
        FontSize = 20,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 4)
    };

    private static StackPanel BuildFirstRunStep(params UIElement[] children)
    {
        StackPanel step = new() { Spacing = 13, VerticalAlignment = VerticalAlignment.Top };
        foreach (UIElement child in children)
        {
            step.Children.Add(child);
        }
        return step;
    }

    private static BackEase SpringEase() => new() { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

    private void AnimateFirstRun(
        DependencyObject target,
        string property,
        double from,
        double to,
        int durationMilliseconds,
        int delay = 0,
        EasingFunctionBase? ease = null,
        Action? completed = null,
        bool dependent = false)
    {
        DoubleAnimation animation = new()
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(GetFrameAlignedAnimationDuration(durationMilliseconds))),
            BeginTime = TimeSpan.FromMilliseconds(_settings.ReduceMotion ? 0 : delay),
            EasingFunction = ease ?? DecelerateEase(),
            EnableDependentAnimation = dependent
        };
        Storyboard storyboard = new();
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
        if (completed is not null)
        {
            storyboard.Completed += (_, _) => completed();
        }
        storyboard.Begin();
    }

    private static TextBlock BuildFirstRunDescription() => new()
    {
        FontSize = 12,
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
