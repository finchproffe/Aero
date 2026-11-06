using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aero.WebView;

public static class WebViewControlFactory
{
    public static WebView2 CreateBrowserControl() =>
        Create(allowDrop: true, initiallyVisible: false);

    public static WebView2 Create(bool allowDrop, bool initiallyVisible) => new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        Visibility = initiallyVisible ? Visibility.Visible : Visibility.Collapsed,
        // Main tab controls keep WinUI routed file drag-and-drop enabled. Aero.App
        // owns the single page-delivery path so native WebView2 and CDP delivery
        // cannot upload the same external file twice.
        AllowDrop = allowDrop
    };
}
