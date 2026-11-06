using Microsoft.UI.Xaml.Controls;

namespace Aero.WebView;

public static class WebViewControllerLifecycle
{
    public static void TryStop(WebView2 browser)
    {
        ArgumentNullException.ThrowIfNull(browser);

        try
        {
            browser.CoreWebView2?.Stop();
        }
        catch
        {
            // The renderer or browser process may already be gone.
        }
    }

    public static void TryClose(WebView2 browser)
    {
        ArgumentNullException.ThrowIfNull(browser);

        try
        {
            browser.Close();
        }
        catch
        {
            // Close remains best effort during process failure and app shutdown.
        }
    }
}
