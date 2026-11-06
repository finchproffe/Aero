using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Aero.WebView;

public readonly record struct WebViewProfileOptions(
    string ProfileName,
    bool IsInPrivateModeEnabled);

public static class WebViewControllerInitializer
{
    public static async Task EnsureInitializedAsync(
        WebView2 browser,
        CoreWebView2Environment environment,
        WebViewProfileOptions? profile = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(environment);
        cancellationToken.ThrowIfCancellationRequested();

        if (profile is not WebViewProfileOptions profileOptions)
        {
            await browser.EnsureCoreWebView2Async(environment);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(profileOptions.ProfileName);
        CoreWebView2ControllerOptions controllerOptions =
            environment.CreateCoreWebView2ControllerOptions();
        controllerOptions.ProfileName = profileOptions.ProfileName;
        controllerOptions.IsInPrivateModeEnabled = profileOptions.IsInPrivateModeEnabled;

        await browser.EnsureCoreWebView2Async(environment, controllerOptions);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
