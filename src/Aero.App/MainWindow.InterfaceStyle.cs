using Aero.App.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Aero.App;

// An interface style is a full bundle: window material, palette, UI font, icon set, geometry of
// the toolbar, address bar, buttons, panes, menus and overlays, the look of Aero's own pages, and
// a tab-strip preset (plus layout for Sidebar) written into the settings when the style is picked.
public sealed partial class MainWindow
{
    // ToolbarHeight is the whole toolbar row, margins of a floating toolbar included.
    private sealed record StyleGeometry(
        double ButtonSize,
        double ButtonRadius,
        double AddressHeight,
        double AddressRadius,
        bool AddressOutline,
        bool AddressUnderline,
        double ToolbarHeight,
        Thickness ToolbarMargin,
        double ToolbarRadius,
        bool ToolbarFloating,
        bool GlassEdge,
        bool ToolbarDivider,
        double PaneRadius,
        double PaneGap,
        double OverlayRadius,
        double FlyoutRadius,
        double ContentInset);

    private static StyleGeometry GetStyleGeometry(InterfaceStyle style) => style switch
    {
        InterfaceStyle.LiquidGlass => new(34, 999, 38, 19, true, false, 54, new Thickness(0), 0, false, true, false, 24, 10, 26, 18, 0),
        InterfaceStyle.Material3 => new(40, 999, 48, 24, false, false, 64, new Thickness(0), 0, false, false, false, 28, 12, 28, 16, 0),
        InterfaceStyle.Fluent => new(32, 4, 32, 4, true, true, 44, new Thickness(0), 0, false, false, true, 8, 0, 8, 8, 0),
        InterfaceStyle.Oled => new(30, 6, 30, 6, false, false, 40, new Thickness(0), 0, false, false, true, 0, 0, 10, 8, 0),
        InterfaceStyle.Sidebar => new(32, 10, 34, 10, false, false, 56, new Thickness(8, 6, 8, 6), 12, true, false, false, 14, 8, 16, 12, 8),
        _ => new(34, 8, 36, 18, false, false, 48, new Thickness(0), 0, false, false, true, 0, 0, 15, 12, 0)
    };

    private const string ManropeFont = "ms-appx:///Assets/Fonts/Manrope.ttf#Manrope";
    private const string SegoeIconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    // Google Sans Flex has no Cyrillic, so Roboto Flex (Material's own typeface) covers it.
    private static (string Ui, string Icons) GetStyleFonts(InterfaceStyle style) => style switch
    {
        InterfaceStyle.Material3 => (
            "ms-appx:///Assets/Fonts/GoogleSansFlex.ttf#Google Sans Flex, ms-appx:///Assets/Fonts/RobotoFlex.ttf#Roboto Flex",
            "ms-appx:///Assets/Fonts/AeroExpressiveIcons.ttf#Aero Expressive Icons, " + SegoeIconFont),
        InterfaceStyle.LiquidGlass => (
            "ms-appx:///Assets/Fonts/Inter.ttf#Inter",
            "ms-appx:///Assets/Fonts/AeroGlassIcons.ttf#Aero Glass Icons, " + SegoeIconFont),
        InterfaceStyle.Fluent => ("Segoe UI Variable Text, Segoe UI", SegoeIconFont),
        _ => (ManropeFont, SegoeIconFont)
    };

    private InterfaceStyle EffectiveInterfaceStyle => _chromeAccessibilitySettings.HighContrast
        ? InterfaceStyle.Aero
        : _settings.InterfaceStyle;

    private StyleGeometry CurrentStyleGeometry => GetStyleGeometry(EffectiveInterfaceStyle);

    private static readonly string[] StyledChromeKeys =
    {
        "AeroWindowBrush", "AeroTitleBarBrush", "AeroToolbarBrush", "AeroActiveTabBrush",
        "AeroTabHoverBrush", "AeroTabOutlineBrush", "AeroAddressBrush", "AeroAddressFocusedBrush",
        "AeroSurfaceBrush", "AeroRaisedSurfaceBrush", "AeroHoverBrush", "AeroPressedBrush",
        "AeroSoftBorderBrush", "AeroBorderBrush", "AeroTabIndicatorBrush", "AeroTextBrush",
        "AeroSecondaryTextBrush", "AeroMutedTextBrush", "AeroAccentBrush", "AeroOnAccentBrush",
        "ButtonBackgroundPointerOver", "ButtonBackgroundPressed"
    };

    private static readonly string[] StyledThemes = { "Dark", "Light", "Default" };

    // Brushes, fonts and styles live in Application resources, so originals are kept once per process.
    private static Dictionary<(string Theme, string Key), Color>? _baseChromeColors;
    private static Style? _baseChromeButtonStyle;
    private Style? _baseFlyoutPresenterStyle;
    private object? _flyoutPresenterStyleKey;
    private (double Size, double Radius)? _appliedChromeButtonMetrics;
    private string _appliedUiFont = ManropeFont;
    private string? _appliedIconFont;
    private MaterialScheme? _materialScheme;
    private readonly Dictionary<FontIcon, double> _baseToolbarIconSizes = new();

    // Writes the style's tab preset (and layout) into the settings; called when the user picks a style.
    private void ApplyInterfaceStyleBundle(InterfaceStyle previous)
    {
        InterfaceStyle style = _settings.InterfaceStyle;
        ApplyTabPresetValues(style switch
        {
            InterfaceStyle.LiquidGlass => "glass",
            InterfaceStyle.Material3 => "pill",
            InterfaceStyle.Oled or InterfaceStyle.Sidebar => "minimal",
            _ => "classic"
        });

        // The window material and palette replace the tab bar's own blur and backgrounds.
        _settings.TabBarBlur = TabBarBlurStyle.Off;
        _settings.TabBarBackgroundStyle = TabBarBackgroundStyle.Default;
        switch (style)
        {
            case InterfaceStyle.LiquidGlass:
                _settings.TabShape = TabShapeStyle.Pill;
                _settings.TabSurfaceStyle = TabSurfaceStyle.Theme;
                _settings.TabBarHeight = 42;
                _settings.TabSpacing = 6;
                _settings.TabStripPadding = 6;
                _settings.TabHorizontalPadding = 14;
                _settings.TabBorderThickness = 1;
                _settings.TabBorderOpacity = 100;
                _settings.TabActiveIndicator = TabActiveIndicatorStyle.None;
                _settings.TabBoldActiveText = true;
                _settings.TabInactiveScalePercent = 100;
                _settings.TabHoverLift = 0;
                _settings.TabAnimation = TabAnimationStyle.Bounce;
                _settings.TabAnimationDuration = 320;
                _settings.TabFontSize = 12;
                break;
            case InterfaceStyle.Material3:
                _settings.TabShape = TabShapeStyle.Pill;
                _settings.TabSurfaceStyle = TabSurfaceStyle.Theme;
                _settings.TabBarHeight = 48;
                _settings.TabSpacing = 4;
                _settings.TabStripPadding = 8;
                _settings.TabHorizontalPadding = 16;
                _settings.TabBorderThickness = 0;
                _settings.TabInactiveOpacity = 0;
                _settings.TabHoverLift = 0;
                _settings.TabInactiveScalePercent = 100;
                _settings.TabActiveIndicator = TabActiveIndicatorStyle.None;
                _settings.TabAnimation = TabAnimationStyle.Bounce;
                _settings.TabAnimationDuration = 350;
                _settings.TabBoldActiveText = true;
                _settings.TabFontSize = 13;
                _settings.TabIconSize = 18;
                break;
            case InterfaceStyle.Fluent:
                _settings.TabCornerRadius = 8;
                _settings.TabAnimationDuration = 167;
                break;
            case InterfaceStyle.Oled:
                _settings.TabBarHeight = 36;
                break;
            case InterfaceStyle.Sidebar:
                _settings.TabShape = TabShapeStyle.Rounded;
                _settings.TabCornerRadius = 10;
                _settings.TabHorizontalPadding = 10;
                _settings.TabActiveIndicator = TabActiveIndicatorStyle.None;
                _settings.TabCloseButtonMode = TabCloseButtonMode.HoverOrActive;
                _settings.TabBarHeight = 38;
                _settings.TabPosition = TabStripPosition.Left;
                _settings.VerticalTabWidth = 240;
                break;
        }

        if (style != InterfaceStyle.Sidebar && previous == InterfaceStyle.Sidebar)
        {
            _settings.TabPosition = TabStripPosition.Top;
        }

        _settings.Save();
    }

    private void ApplyInterfaceStyle()
    {
        InterfaceStyle style = EffectiveInterfaceStyle;
        var themes = Application.Current.Resources.ThemeDictionaries;

        _baseChromeColors ??= StyledThemes
            .SelectMany(theme => StyledChromeKeys.Select(key =>
                (theme, key, ((SolidColorBrush)((ResourceDictionary)themes[theme])[key]).Color)))
            .ToDictionary(item => (item.theme, item.key), item => item.Color);

        Color accent = GetConfiguredAccentColor();
        _materialScheme = style == InterfaceStyle.Material3 ? BuildMaterialScheme(accent, IsDarkTheme()) : null;
        foreach (string theme in StyledThemes)
        {
            ResourceDictionary dictionary = (ResourceDictionary)themes[theme];
            bool dark = theme != "Light";
            MaterialScheme? scheme = style == InterfaceStyle.Material3 ? BuildMaterialScheme(accent, dark) : null;
            Color BaseOf(string key) => _baseChromeColors[(theme, key)];
            foreach (string key in StyledChromeKeys)
            {
                ((SolidColorBrush)dictionary[key]).Color =
                    StyleChromeColor(style, key, BaseOf(key), dark, scheme, BaseOf);
            }
        }

        SystemBackdrop = style switch
        {
            InterfaceStyle.LiquidGlass => SystemBackdrop as DesktopAcrylicBackdrop ?? new DesktopAcrylicBackdrop(),
            InterfaceStyle.Fluent => SystemBackdrop as MicaBackdrop ?? new MicaBackdrop(),
            _ => null
        };

        ApplyStyleFonts(style);
    }

    // Geometry and style-specific chrome: toolbar, address bar, buttons, panes, menus, overlays.
    private void ApplyChromeGeometry()
    {
        InterfaceStyle style = EffectiveInterfaceStyle;
        StyleGeometry g = GetStyleGeometry(style);
        bool compact = _settings.InterfaceDensity == InterfaceDensity.Compact;
        bool dark = IsDarkTheme();
        bool glass = style == InterfaceStyle.LiquidGlass;
        bool material = style == InterfaceStyle.Material3 && _materialScheme is not null;
        // Compact trims the bar and the address field like the original density setting did.
        double toolbar = g.ToolbarHeight - (compact ? 6 : 0);
        double address = Math.Max(28, g.AddressHeight - (compact ? 2 : 0));
        double button = g.ButtonSize;
        double sidePadding = g.ToolbarFloating ? 10 : glass || material ? 12 : 8;
        double borders = g.ToolbarFloating ? 2 : g.ToolbarDivider ? 1 : 0;
        double verticalPadding = Math.Max(2, Math.Floor((toolbar - g.ToolbarMargin.Top - g.ToolbarMargin.Bottom - borders - Math.Max(address, button)) / 2));

        NavigationToolbarSurface.Margin = g.ToolbarMargin;
        NavigationToolbarSurface.CornerRadius = new CornerRadius(g.ToolbarRadius);
        NavigationToolbarSurface.Padding = new Thickness(sidePadding, verticalPadding, sidePadding, verticalPadding);
        NavigationToolbarSurface.BorderThickness = g.ToolbarFloating
            ? new Thickness(1)
            : g.ToolbarDivider ? new Thickness(0, 0, 0, 1) : new Thickness(0);
        NavigationToolbarSurface.BorderBrush = g.GlassEdge && g.ToolbarFloating
            ? BuildGlassEdgeBrush(dark)
            : GetChromeBrush("AeroSoftBorderBrush");
        NavigationToolbarGrid.ColumnSpacing = glass ? 2 : material ? 4 : 3;

        AddressSurface.Height = address;
        AddressBox.Height = Math.Min(32, address - 4); // taller boxes push the text to the top
        AddressSurface.CornerRadius = new CornerRadius(Math.Min(g.AddressRadius, address / 2));
        // Safari-style: the glass address field is a centred capsule, not a full-width bar.
        AddressSurface.MaxWidth = glass ? 760 : double.PositiveInfinity;
        AddressSurface.HorizontalAlignment = glass ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        AddressSurface.MinWidth = glass ? 320 : 0;

        ApplyChromeButtonStyle(button, g.ButtonRadius);
        ApplyToolbarIconSizes(style);
        ApplyToolbarGroups(style, dark, button);
        ApplyNewTabButtonStyle(style);
        ApplyFlyoutStyle(style, g.FlyoutRadius, dark);

        foreach (Border pane in new[] { LibraryPane, DownloadsPane, WorkspacesPane, PasswordsPane, ExtensionsPane, SettingsPane })
        {
            bool floating = g.PaneGap > 0;
            pane.Margin = new Thickness(g.PaneGap);
            pane.CornerRadius = floating
                ? new CornerRadius(g.PaneRadius)
                : new CornerRadius(g.PaneRadius, 0, 0, g.PaneRadius);
            pane.BorderThickness = material ? new Thickness(0) : floating ? new Thickness(1) : new Thickness(1, 0, 0, 0);
            pane.BorderBrush = glass ? BuildGlassEdgeBrush(dark) : GetChromeBrush("AeroBorderBrush");
            pane.Background = glass ? BuildGlassSheetBrush(dark) : GetChromeBrush("AeroSurfaceBrush");
        }

        foreach (Border overlay in new[] { CommandPalette, DownloadToast, FindBar })
        {
            overlay.Background = glass ? BuildGlassSheetBrush(dark) : GetChromeBrush("AeroRaisedSurfaceBrush");
            overlay.BorderBrush = glass ? BuildGlassEdgeBrush(dark) : GetChromeBrush("AeroBorderBrush");
            overlay.BorderThickness = new Thickness(material ? 0 : 1);
        }

        CommandPalette.CornerRadius = new CornerRadius(g.OverlayRadius);
        DownloadToast.CornerRadius = new CornerRadius(Math.Min(g.OverlayRadius, glass ? 14 : 16));
        FindBar.CornerRadius = new CornerRadius(Math.Min(g.OverlayRadius, glass ? 14 : material ? 16 : 18));
        UpdateAddressSurface();
    }

    // Liquid Glass: the toolbar disappears and its button groups float as glass capsules.
    // Material 3 Expressive: back/forward become a connected tonal button group.
    private void ApplyToolbarGroups(InterfaceStyle style, bool dark, double button)
    {
        bool glass = style == InterfaceStyle.LiquidGlass;
        bool material = style == InterfaceStyle.Material3;
        foreach (Border group in new[] { NavGroupSurface, ActionGroupSurface })
        {
            group.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
            if (glass)
            {
                group.Background = GetChromeBrush("AeroAddressBrush");
                group.BorderBrush = BuildGlassEdgeBrush(dark);
                group.BorderThickness = new Thickness(1);
                // An exact half-height radius: WinUI turns oversized radii into an ellipse.
                group.CornerRadius = new CornerRadius((button + 6) / 2);
                group.Margin = new Thickness(-3, -3, -3, -3);
            }
        }

        if (material)
        {
            double outer = button / 2;
            BackButton.CornerRadius = new CornerRadius(outer, 6, 6, outer);
            ForwardButton.CornerRadius = new CornerRadius(6, outer, outer, 6);
            BackButton.Width = button + 8;
            ForwardButton.Width = button + 8;
            BackButton.Background = GetChromeBrush("AeroRaisedSurfaceBrush");
            ForwardButton.Background = GetChromeBrush("AeroRaisedSurfaceBrush");
        }
        else
        {
            foreach (Button navigation in new[] { BackButton, ForwardButton })
            {
                navigation.ClearValue(Control.CornerRadiusProperty);
                navigation.ClearValue(FrameworkElement.WidthProperty);
                navigation.ClearValue(Control.BackgroundProperty);
            }
        }
    }

    // Material icons are drawn for 20-24 px; Segoe's 13 px looks lost in a 40 px button.
    private void ApplyToolbarIconSizes(InterfaceStyle style)
    {
        double scale = style switch
        {
            InterfaceStyle.Material3 => 1.5,
            InterfaceStyle.LiquidGlass => 1.2,
            _ => 1
        };
        IEnumerable<UIElement> toolbarItems = NavigationToolbarGrid.Children
            .Concat(new UIElement[] { TabListButton, AddTabButton });
        foreach (UIElement item in toolbarItems)
        {
            if (item is Button { Content: FontIcon icon })
            {
                if (!_baseToolbarIconSizes.TryGetValue(icon, out double baseSize))
                {
                    baseSize = icon.FontSize;
                    _baseToolbarIconSizes[icon] = baseSize;
                }

                icon.FontSize = Math.Round(baseSize * scale);
            }
        }
    }

    // Material 3 Expressive gives "new tab" a primary-container FAB shape.
    private void ApplyNewTabButtonStyle(InterfaceStyle style)
    {
        string[] stateKeys = { "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonForegroundPointerOver", "ButtonForegroundPressed" };
        if (style == InterfaceStyle.Material3 && _materialScheme is MaterialScheme scheme)
        {
            AddTabButton.Background = new SolidColorBrush(scheme.PrimaryContainer);
            AddTabButton.Foreground = new SolidColorBrush(scheme.OnPrimaryContainer);
            AddTabButton.CornerRadius = new CornerRadius(12);
            AddTabButton.Resources[stateKeys[0]] = new SolidColorBrush(BlendColors(scheme.PrimaryContainer, scheme.OnPrimaryContainer, 0.08));
            AddTabButton.Resources[stateKeys[1]] = new SolidColorBrush(BlendColors(scheme.PrimaryContainer, scheme.OnPrimaryContainer, 0.12));
            AddTabButton.Resources[stateKeys[2]] = new SolidColorBrush(scheme.OnPrimaryContainer);
            AddTabButton.Resources[stateKeys[3]] = new SolidColorBrush(scheme.OnPrimaryContainer);
            return;
        }

        AddTabButton.ClearValue(Control.BackgroundProperty);
        AddTabButton.ClearValue(Control.ForegroundProperty);
        AddTabButton.ClearValue(Control.CornerRadiusProperty);
        foreach (string key in stateKeys)
        {
            AddTabButton.Resources.Remove(key);
        }
    }

    private static Brush BuildGlassEdgeBrush(bool dark)
    {
        // A bright upper rim and a fainter lower one read as light caught by the glass.
        LinearGradientBrush edge = new()
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1)
        };
        edge.GradientStops.Add(new GradientStop { Offset = 0, Color = dark ? Color.FromArgb(0x80, 255, 255, 255) : Color.FromArgb(0xFF, 255, 255, 255) });
        edge.GradientStops.Add(new GradientStop { Offset = 0.45, Color = dark ? Color.FromArgb(0x1C, 255, 255, 255) : Color.FromArgb(0x90, 255, 255, 255) });
        edge.GradientStops.Add(new GradientStop { Offset = 1, Color = dark ? Color.FromArgb(0x30, 255, 255, 255) : Color.FromArgb(0x24, 0, 0, 0) });
        return edge;
    }

    // In-app acrylic blurs whatever is behind the sheet.
    private static Brush BuildGlassSheetBrush(bool dark) => new AcrylicBrush
    {
        TintColor = dark ? Color.FromArgb(255, 30, 32, 38) : Color.FromArgb(255, 246, 247, 250),
        TintOpacity = dark ? 0.42 : 0.5,
        TintLuminosityOpacity = dark ? 0.62 : 0.72,
        FallbackColor = dark ? Color.FromArgb(240, 30, 32, 38) : Color.FromArgb(240, 246, 247, 250)
    };

    // Code creates chrome buttons from the "ChromeIconButtonStyle" resource, so replacing the
    // resource covers future buttons; buttons already on screen are switched over here.
    private void ApplyChromeButtonStyle(double size, double radius)
    {
        if (_appliedChromeButtonMetrics == (size, radius))
        {
            return;
        }

        if (!RootGrid.IsLoaded)
        {
            // The buttons from MainWindow.xaml are not in the visual tree yet; restyle them on load.
            RootGrid.Loaded -= RestyleChromeOnLoad;
            RootGrid.Loaded += RestyleChromeOnLoad;
        }

        ResourceDictionary resources = Application.Current.Resources;
        _baseChromeButtonStyle ??= (Style)resources["ChromeIconButtonStyle"];
        Style current = (Style)resources["ChromeIconButtonStyle"];
        Style next = new(typeof(Button)) { BasedOn = _baseChromeButtonStyle };
        next.Setters.Add(new Setter(FrameworkElement.WidthProperty, size));
        next.Setters.Add(new Setter(FrameworkElement.HeightProperty, size));
        next.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(radius)));
        resources["ChromeIconButtonStyle"] = next;

        foreach (Button chromeButton in FindDescendants<Button>(RootGrid))
        {
            if (chromeButton.Style is Style style && (style == current || style == _baseChromeButtonStyle))
            {
                chromeButton.Style = next;
            }
        }

        if (RootGrid.IsLoaded)
        {
            _appliedChromeButtonMetrics = (size, radius);
        }
    }

    private void RestyleChromeOnLoad(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RestyleChromeOnLoad;
        (string ui, _) = GetStyleFonts(EffectiveInterfaceStyle);
        RestyleFontsIn(RootGrid, ManropeFont, new FontFamily(ui), new FontFamily(_appliedIconFont ?? SegoeIconFont));
        ApplyChromeGeometry();
    }

    private void ApplyFlyoutStyle(InterfaceStyle style, double radius, bool dark)
    {
        if (_flyoutPresenterStyleKey is null)
        {
            foreach (KeyValuePair<object, object> entry in RootGrid.Resources)
            {
                if (entry.Value is Style existing && existing.TargetType == typeof(FlyoutPresenter))
                {
                    _flyoutPresenterStyleKey = entry.Key;
                    _baseFlyoutPresenterStyle = existing;
                    break;
                }
            }
        }

        if (_flyoutPresenterStyleKey is null || _baseFlyoutPresenterStyle is null)
        {
            return;
        }

        Style next = new(typeof(FlyoutPresenter)) { BasedOn = _baseFlyoutPresenterStyle };
        next.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(radius)));
        if (style == InterfaceStyle.LiquidGlass)
        {
            next.Setters.Add(new Setter(Control.BackgroundProperty, BuildGlassSheetBrush(dark)));
            next.Setters.Add(new Setter(Control.BorderBrushProperty, BuildGlassEdgeBrush(dark)));
        }
        else if (style == InterfaceStyle.Material3 && _materialScheme is MaterialScheme scheme)
        {
            next.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(scheme.Container)));
            next.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        }

        RootGrid.Resources[_flyoutPresenterStyleKey] = next;
    }

    // Swaps the UI font and icon set. The icon fonts keep Segoe Fluent code points, so every
    // glyph Aero sets anywhere keeps its meaning; unmapped ones fall back to Segoe.
    private void ApplyStyleFonts(InterfaceStyle style)
    {
        (string ui, string icons) = GetStyleFonts(style);
        if (ui == _appliedUiFont && icons == _appliedIconFont)
        {
            return;
        }

        FontFamily uiFont = new(ui);
        FontFamily iconFont = new(icons);
        ResourceDictionary resources = Application.Current.Resources;
        resources["AeroFontFamily"] = uiFont;
        resources["ContentControlThemeFontFamily"] = uiFont;
        resources["SymbolThemeFontFamily"] = iconFont;
        foreach (KeyValuePair<object, object> entry in resources.ToArray())
        {
            if (entry.Value is Style existing && existing.TargetType == typeof(TextBlock))
            {
                Style textStyle = new(typeof(TextBlock));
                textStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, uiFont));
                resources[entry.Key] = textStyle;
            }
        }

        RestyleFontsIn(RootGrid, _appliedUiFont, uiFont, iconFont);
        _appliedUiFont = ui;
        _appliedIconFont = icons;
    }

    // For elements created after the style was applied (tab headers, menus built in code).
    private void RestyleFontsIn(DependencyObject root) =>
        RestyleFontsIn(root, ManropeFont, new FontFamily(_appliedUiFont), new FontFamily(_appliedIconFont ?? SegoeIconFont));

    private static void RestyleFontsIn(DependencyObject root, string previousUi, FontFamily uiFont, FontFamily iconFont)
    {
        foreach (DependencyObject node in EnumerateStyleTargets(root))
        {
            switch (node)
            {
                case FontIcon icon when IsIconFont(icon.FontFamily):
                    icon.FontFamily = iconFont;
                    break;
                case TextBlock text when IsIconFont(text.FontFamily):
                    text.FontFamily = iconFont;
                    break;
                case TextBlock text when IsUiFont(text.FontFamily, previousUi):
                    text.FontFamily = uiFont;
                    break;
                case Control control when IsUiFont(control.FontFamily, previousUi):
                    control.FontFamily = uiFont;
                    break;
            }
        }
    }

    private static bool IsUiFont(FontFamily? family, string previousUi) =>
        family?.Source is string source &&
        (source == previousUi ||
         source.Contains("Manrope", StringComparison.Ordinal) ||
         source.Contains("Google Sans Flex", StringComparison.Ordinal) ||
         source.Contains("#Inter", StringComparison.Ordinal) ||
         source.StartsWith("Segoe UI Variable Text", StringComparison.Ordinal));

    private static bool IsIconFont(FontFamily? family) =>
        family?.Source is string source &&
        (source.Contains("Segoe Fluent Icons", StringComparison.Ordinal) ||
         source.Contains("Segoe MDL2 Assets", StringComparison.Ordinal));

    // Visual tree plus content that is not in it yet: flyouts, menu items and button content.
    private static IEnumerable<DependencyObject> EnumerateStyleTargets(DependencyObject root)
    {
        HashSet<DependencyObject> seen = new();
        Stack<DependencyObject> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject node = pending.Pop();
            if (!seen.Add(node))
            {
                continue;
            }

            yield return node;
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int index = 0; index < count; index++)
            {
                pending.Push(VisualTreeHelper.GetChild(node, index));
            }

            if (count == 0)
            {
                switch (node)
                {
                    case ContentControl { Content: DependencyObject content }:
                        pending.Push(content);
                        break;
                    case Border { Child: DependencyObject child }:
                        pending.Push(child);
                        break;
                    case Panel panel:
                        foreach (UIElement element in panel.Children)
                        {
                            pending.Push(element);
                        }

                        break;
                }
            }

            if (node is MenuFlyoutItem { Icon: IconElement itemIcon })
            {
                pending.Push(itemIcon);
            }

            if (node is MenuFlyoutSubItem subItem)
            {
                foreach (MenuFlyoutItemBase item in subItem.Items)
                {
                    pending.Push(item);
                }
            }

            FlyoutBase? buttonFlyout = node is Button { Flyout: FlyoutBase attachedFlyout } ? attachedFlyout : null;
            foreach (FlyoutBase? attached in new[] { buttonFlyout, (node as UIElement)?.ContextFlyout })
            {
                switch (attached)
                {
                    case Flyout { Content: DependencyObject flyoutContent }:
                        pending.Push(flyoutContent);
                        break;
                    case MenuFlyout menu:
                        foreach (MenuFlyoutItemBase item in menu.Items)
                        {
                            pending.Push(item);
                        }

                        break;
                }
            }
        }
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        Stack<DependencyObject> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject node = pending.Pop();
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int index = 0; index < count; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, index);
                if (child is T match)
                {
                    yield return match;
                }

                pending.Push(child);
            }
        }
    }

    private static Color StyleChromeColor(
        InterfaceStyle style,
        string key,
        Color color,
        bool dark,
        MaterialScheme? m,
        Func<string, Color> baseOf)
    {
        static Color White(byte alpha) => Color.FromArgb(alpha, 255, 255, 255);
        static Color Black(byte alpha) => Color.FromArgb(alpha, 0, 0, 0);
        return style switch
        {
            InterfaceStyle.LiquidGlass => key switch
            {
                "AeroWindowBrush" => dark ? Color.FromArgb(0x50, 14, 16, 20) : Color.FromArgb(0x60, 244, 246, 250),
                "AeroTitleBarBrush" or "AeroToolbarBrush" => Color.FromArgb(0, color.R, color.G, color.B),
                "AeroActiveTabBrush" => dark ? White(0x2A) : White(0xC0),
                "AeroTabHoverBrush" => dark ? White(0x14) : White(0x70),
                "AeroTabOutlineBrush" => dark ? White(0x38) : White(0xF0),
                "AeroAddressBrush" => dark ? White(0x1C) : White(0x8C),
                "AeroAddressFocusedBrush" => dark ? White(0x28) : White(0xD8),
                "AeroSurfaceBrush" => dark ? Color.FromArgb(0xD0, 28, 30, 36) : Color.FromArgb(0xD8, 248, 249, 251),
                "AeroRaisedSurfaceBrush" => dark ? White(0x1A) : White(0xA0),
                "AeroHoverBrush" or "ButtonBackgroundPointerOver" => dark ? White(0x1E) : Black(0x0E),
                "AeroPressedBrush" or "ButtonBackgroundPressed" => dark ? White(0x2C) : Black(0x18),
                "AeroSoftBorderBrush" => dark ? White(0x26) : White(0xC8),
                "AeroBorderBrush" => dark ? White(0x40) : Black(0x22),
                "AeroSecondaryTextBrush" => dark ? White(0xC0) : Black(0xA8),
                "AeroMutedTextBrush" => dark ? White(0x90) : Black(0x80),
                _ => color
            },
            InterfaceStyle.Material3 when m is not null => key switch
            {
                "AeroWindowBrush" => m.Surface,
                "AeroTitleBarBrush" or "AeroToolbarBrush" => m.Container,
                "AeroActiveTabBrush" => m.SecondaryContainer,
                "AeroTabHoverBrush" => m.ContainerHigh,
                "AeroTabOutlineBrush" or "AeroSoftBorderBrush" => m.OutlineVariant,
                "AeroAddressBrush" or "AeroAddressFocusedBrush" => m.ContainerHighest,
                "AeroSurfaceBrush" => m.ContainerLow,
                "AeroRaisedSurfaceBrush" => m.ContainerHigh,
                "AeroHoverBrush" => m.ContainerHighest,
                "AeroPressedBrush" => BlendColors(m.ContainerHighest, m.OnSurface, 0.12),
                "AeroBorderBrush" => m.Outline,
                "AeroTabIndicatorBrush" or "AeroAccentBrush" => m.Primary,
                "AeroOnAccentBrush" => m.OnPrimary,
                "AeroTextBrush" => m.OnSurface,
                "AeroSecondaryTextBrush" => m.OnSurfaceVariant,
                "AeroMutedTextBrush" => m.Outline,
                "ButtonBackgroundPointerOver" => BlendColors(m.Container, m.OnSurface, 0.08),
                "ButtonBackgroundPressed" => BlendColors(m.Container, m.OnSurface, 0.12),
                _ => color
            },
            InterfaceStyle.Fluent => key switch
            {
                "AeroWindowBrush" or "AeroTitleBarBrush" => Color.FromArgb(0, color.R, color.G, color.B),
                "AeroToolbarBrush" => Color.FromArgb((byte)(dark ? 0x80 : 0xA0), color.R, color.G, color.B),
                "AeroActiveTabBrush" => Color.FromArgb((byte)(dark ? 0xB0 : 0xD0), color.R, color.G, color.B),
                "AeroTabHoverBrush" => Color.FromArgb(0x70, color.R, color.G, color.B),
                _ => color
            },
            InterfaceStyle.Oled when dark => key switch
            {
                "AeroWindowBrush" or "AeroTitleBarBrush" or "AeroToolbarBrush" => Color.FromArgb(255, 0, 0, 0),
                "AeroSurfaceBrush" => Color.FromArgb(255, 10, 10, 10),
                "AeroAddressBrush" or "AeroRaisedSurfaceBrush" => Color.FromArgb(255, 18, 18, 18),
                "AeroTabHoverBrush" => Color.FromArgb(255, 20, 20, 20),
                "AeroActiveTabBrush" or "AeroAddressFocusedBrush" => Color.FromArgb(255, 28, 28, 30),
                "AeroHoverBrush" or "ButtonBackgroundPointerOver" => Color.FromArgb(255, 34, 34, 34),
                "AeroPressedBrush" or "ButtonBackgroundPressed" => Color.FromArgb(255, 44, 44, 44),
                "AeroSoftBorderBrush" or "AeroTabOutlineBrush" => Color.FromArgb(255, 38, 38, 38),
                "AeroBorderBrush" => Color.FromArgb(255, 58, 58, 58),
                _ => color
            },
            // One continuous frame (title bar, sidebar, window) with the toolbar as a raised card.
            InterfaceStyle.Sidebar => key switch
            {
                "AeroTitleBarBrush" => baseOf("AeroWindowBrush"),
                "AeroToolbarBrush" => baseOf("AeroRaisedSurfaceBrush"),
                "AeroActiveTabBrush" => baseOf("AeroRaisedSurfaceBrush"),
                "AeroAddressBrush" => baseOf("AeroSurfaceBrush"),
                _ => color
            },
            _ => color
        };
    }
}
