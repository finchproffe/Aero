using Aero.App.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;

namespace Aero.App;

public sealed partial class MainWindow
{
    private async void ShieldEngineComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_updatingShieldUi || ShieldEngineComboBox.SelectedIndex < 0)
        {
            return;
        }

        _settings.ShieldEngineMode =
            (ShieldEngineMode)ShieldEngineComboBox.SelectedIndex;
        _settings.Save();

        await ApplyShieldEngineModeAsync();
        await RefreshShieldForOpenTabsAsync();
        UpdateSiteIndicator(ActiveTab?.CurrentUri ?? ActiveTab?.PendingUri);
        UpdateShieldUi();
    }

    private async void ShieldYoutubeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingShieldUi)
        {
            return;
        }

        _settings.ShieldYouTubeCleanMode = ShieldYoutubeToggle.IsOn;
        _settings.Save();
        await RefreshShieldForOpenTabsAsync();
    }

    private void ShieldAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ShieldFlyout.ShowAt(ShieldButton);
    }

    private Task ApplyShieldEngineModeAsync() =>
        SyncAdvancedShieldForUriAsync(ActiveTab?.CurrentUri ?? ActiveTab?.PendingUri);

    private async Task RefreshShieldForOpenTabsAsync()
    {
        foreach (BrowserTabState tab in _tabs.ToArray())
        {
            await ApplyCosmeticFilteringAsync(tab);
        }
    }

    private static bool IsYouTubeHost(string host)
    {
        string normalized = host.Trim().TrimEnd('.');
        return normalized.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".youtu.be", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ApplyYouTubeCleanModeAsync(BrowserTabState state)
    {
        if (state.Browser.CoreWebView2 is null)
        {
            return;
        }

        const string script = """
(() => {
    const styleId = 'aero-youtube-clean-style';
    const selectors = [
        'ytd-display-ad-renderer',
        'ytd-ad-slot-renderer',
        'ytd-promoted-sparkles-web-renderer',
        'ytd-promoted-video-renderer',
        'ytd-in-feed-ad-layout-renderer',
        '#player-ads',
        '.ytp-ad-overlay-container',
        '.ytp-ad-module',
        'tp-yt-paper-dialog ytd-mealbar-promo-renderer'
    ];
    const selectorList = selectors.join(',');

    let style = document.getElementById(styleId);
    if (!style) {
        style = document.createElement('style');
        style.id = styleId;
        style.textContent = selectorList +
            '{display:none!important;visibility:hidden!important;pointer-events:none!important;}';
        (document.head || document.documentElement).appendChild(style);
    }

    window.__aeroYoutubeShield?.stop?.();

    let cleanTimer = 0;
    let lastSkipClick = 0;

    const clean = () => {
        cleanTimer = 0;

        // Query once instead of walking the whole document once per selector.
        document.querySelectorAll(selectorList).forEach(node => node.remove());

        const now = Date.now();
        if (now - lastSkipClick >= 2000) {
            const skipButton = document.querySelector(
                '.ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-skip-ad-button');
            if (skipButton instanceof HTMLElement) {
                lastSkipClick = now;
                skipButton.click();
            }
        }
    };

    const scheduleClean = () => {
        if (cleanTimer) return;
        cleanTimer = window.setTimeout(clean, 350);
    };

    // YouTube is a very mutation-heavy SPA. Running full selector scans for every
    // mutation makes initial loading and scrolling noticeably slower.
    const observer = new MutationObserver(scheduleClean);
    observer.observe(document.documentElement, { childList: true, subtree: true });
    const timer = window.setInterval(scheduleClean, 3000);

    window.__aeroYoutubeShield = {
        stop() {
            observer.disconnect();
            window.clearInterval(timer);
            if (cleanTimer) {
                window.clearTimeout(cleanTimer);
                cleanTimer = 0;
            }
        }
    };

    clean();
})();
""";

        await state.Browser.CoreWebView2.ExecuteScriptAsync(script);
    }

    private static async Task RemoveYouTubeCleanModeAsync(BrowserTabState state)
    {
        if (state.Browser.CoreWebView2 is null)
        {
            return;
        }

        await state.Browser.CoreWebView2.ExecuteScriptAsync("""
(() => {
    window.__aeroYoutubeShield?.stop?.();
    delete window.__aeroYoutubeShield;
    document.getElementById('aero-youtube-clean-style')?.remove();
})();
""");
    }

    private static async Task RemoveShieldPageArtifactsAsync(BrowserTabState state)
    {
        try
        {
            if (state.Browser.CoreWebView2 is not CoreWebView2 core)
            {
                return;
            }

            await core.ExecuteScriptAsync("""
(() => {
    document.getElementById('aero-shield-cosmetic-style')?.remove();
    document.getElementById('aero-youtube-clean-style')?.remove();
    window.__aeroYoutubeShield?.stop?.();
    delete window.__aeroYoutubeShield;
})();
""");
        }
        catch
        {
            // The page can be navigating or already disposed.
        }
        finally
        {
            state.NativeCosmeticArtifactsApplied = false;
            state.YouTubeCleanArtifactsApplied = false;
        }
    }
}
