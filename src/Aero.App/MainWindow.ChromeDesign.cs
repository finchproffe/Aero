using Aero.App.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Aero.App;

public sealed partial class MainWindow
{
    private readonly AccessibilitySettings _chromeAccessibilitySettings = new();

    // Shared semantic brushes keep dynamically-created controls in sync with XAML.
    private Brush GetChromeBrush(string key)
    {
        string theme = _chromeAccessibilitySettings.HighContrast
            ? "HighContrast"
            : IsDarkTheme() ? "Dark" : "Light";
        ResourceDictionary dictionary = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[theme];
        return (Brush)dictionary[key];
    }

    private Color GetChromeColor(string key) => ((SolidColorBrush)GetChromeBrush(key)).Color;

    private static string BuildHomeLabelsJson()
    {
        string[] labels =
        {
            "Search",
            "Search the web or enter an address",
            "Search the web or enter a question",
            "Quick links",
            "Add shortcut",
            "Add quick link",
            "Edit quick link",
            "Edit",
            "Name",
            "URL",
            "Icon",
            "How it appears",
            "Emoji, image URL, or leave empty for site icon",
            "Delete",
            "Cancel",
            "Save",
            "A lighter way to browse.",
            "Private browsing. Local session data is removed when this window closes.",
            "Guest browsing. This profile is removed when its last window closes.",
        };
        return System.Text.Json.JsonSerializer.Serialize(labels.ToDictionary(
            text => text,
            text => Localization.AeroLocalizer.TranslateUi(text)));
    }

    // JSON embedded in the pages escapes '<', so user data can never contain this token.
    // If the font is missing it is left in place as a harmless CSS comment.
    private const string AeroFontFaceToken = "/*<aero-font-face>*/";

    private static string? _aeroFontFaceCss;

    // Internal pages load via NavigateToString, so the bundled font is inlined rather than
    // exposed through a virtual host that any site in the WebView could probe.
    private static string GetAeroFontFaceCss()
    {
        if (_aeroFontFaceCss is not null)
        {
            return _aeroFontFaceCss;
        }

        try
        {
            byte[] font = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Manrope.ttf"));
            _aeroFontFaceCss = "@font-face{font-family:Manrope;src:url(data:font/ttf;base64," +
                Convert.ToBase64String(font) +
                ") format(\"truetype\");font-weight:200 800;font-display:block}";
        }
        catch (IOException)
        {
            _aeroFontFaceCss = string.Empty; // CSS falls back to Segoe UI Variable.
        }
        catch (UnauthorizedAccessException)
        {
            _aeroFontFaceCss = string.Empty;
        }

        return _aeroFontFaceCss;
    }

    private string GetChromeCssColor(string key)
    {
        Color color = GetChromeColor(key);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private int ConfiguredToolbarHeight =>
        _settings.InterfaceDensity == InterfaceDensity.Compact ? 42 : 48;

    private void ApplyChromeDensity()
    {
        bool compact = _settings.InterfaceDensity == InterfaceDensity.Compact;
        NavigationToolbarSurface.Padding = new Thickness(8, compact ? 3 : 5, 8, compact ? 3 : 5);
        AddressSurface.Height = compact ? 34 : 36;
        AddressBox.Height = compact ? 30 : 32;
    }

    private void UpdateAddressSurface(bool isPointerOver = false)
    {
        if (AddressSurface is null || AddressBox is null) return;
        bool focused = AddressBox.FocusState != FocusState.Unfocused;
        AddressSurface.Background = GetChromeBrush(focused
            ? "AeroAddressFocusedBrush"
            : isPointerOver ? "AeroSurfaceBrush" : "AeroAddressBrush");
        // High Contrast fills are identical, so the field needs its system-coloured edge there.
        AddressSurface.BorderBrush = focused
            ? GetChromeBrush("AeroTabIndicatorBrush")
            : _chromeAccessibilitySettings.HighContrast
                ? GetChromeBrush("AeroBorderBrush")
                : isPointerOver ? GetChromeBrush("AeroSoftBorderBrush") : AddressSurface.Background;
        AddressSurfaceTransform.ScaleX = 1;
        AddressSurfaceTransform.ScaleY = 1;
    }

    private void RootGrid_ChromeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = Math.Max(0, e.NewSize.Width - 24);
        foreach (FrameworkElement pane in new FrameworkElement[]
        {
            LibraryPane, DownloadsPane, WorkspacesPane, PasswordsPane,
            ExtensionsPane, SettingsPane, CommandPalette, DownloadToast, FindBar
        })
        {
            pane.MaxWidth = width;
        }
        CommandPalette.MaxHeight = Math.Max(0, e.NewSize.Height - 80);
        if (AddressSuggestionsPopup.IsOpen) PositionAddressSuggestionsPopup();
    }
}
