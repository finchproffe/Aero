using Microsoft.Web.WebView2.Core;

namespace Aero.WebView;

public static class WebViewEnvironmentFactory
{
    public static async Task<CoreWebView2Environment> CreateAsync(
        string userDataFolder,
        bool enableBrowserExtensions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataFolder);
        cancellationToken.ThrowIfCancellationRequested();

        // Fail closed before the runtime starts: an injected debugging port or runtime folder
        // would hand the whole profile (cookies, sessions) to another process.
        WebViewLaunchGuard.ScrubLoaderEnvironment();
        string executableName = Path.GetFileName(Environment.ProcessPath) ?? "Aero.exe";
        if (WebViewLaunchGuard.FindHijackingPolicy(executableName) is string policy)
        {
            throw new WebViewLaunchBlockedException(
                "Aero refused to start the web engine because a Windows policy tries to change how it " +
                "starts. This is a known technique used by password and cookie stealers. Remove this " +
                "registry value and scan the computer for malware:\n" + policy);
        }

        Directory.CreateDirectory(userDataFolder);
        CoreWebView2EnvironmentOptions options = new()
        {
            // No --autoplay-policy override: Chromium's default already blocks sound on sites the
            // user never engaged with, while the stricter document-activation policy left YouTube's
            // player stuck on its thumbnail when a video was opened from the address bar or a new tab.
            AreBrowserExtensionsEnabled = enableBrowserExtensions
        };

        CoreWebView2Environment environment = await
            CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: options)
            .AsTask();

        cancellationToken.ThrowIfCancellationRequested();
        return environment;
    }
}
