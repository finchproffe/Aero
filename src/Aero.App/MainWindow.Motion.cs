using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aero.App;

// Motion for the things people touch all the time: switching tabs and pages loading.
public sealed partial class MainWindow
{
    private const int TabSwitchFadeDuration = 150;
    private bool _pageProgressShown;
    private int _tabFadeGeneration;

    /// <summary>
    /// Fades the newly selected page in over the previous one. The old page stays visible
    /// underneath until the fade ends, so a switch never flashes the window background.
    /// </summary>
    private void CrossfadeTabContent(BrowserTabState from, BrowserTabState to)
    {
        if (_settings.ReduceMotion ||
            IsSplitViewActive ||
            from.IsDiscarded ||
            from.IsClosing ||
            !_tabs.Contains(from) ||
            to.Browser.Visibility != Visibility.Visible)
        {
            return;
        }

        int generation = ++_tabFadeGeneration;
        from.Browser.Visibility = Visibility.Visible;
        Canvas.SetZIndex(from.Browser, 0);
        Canvas.SetZIndex(to.Browser, 1); // still below the snapshot (10) and split overlays (90+)
        AnimateOpacity(to.Browser, 0, 1, TabSwitchFadeDuration, () =>
        {
            // A newer switch owns the z-order and the cleanup now.
            if (generation == _tabFadeGeneration)
            {
                Canvas.SetZIndex(to.Browser, 0);
                UpdateVisibleBrowser();
            }
        });
    }

    private void ShowPageProgress(bool show)
    {
        if (show == _pageProgressShown)
        {
            return;
        }

        _pageProgressShown = show;
        if (show)
        {
            PageProgress.Visibility = Visibility.Visible;
            AnimateOpacity(PageProgress, 0, 1, FastAnimationDuration);
            return;
        }

        AnimateOpacity(PageProgress, PageProgress.Opacity, 0, StandardAnimationDuration, () =>
        {
            if (!_pageProgressShown)
            {
                PageProgress.Visibility = Visibility.Collapsed;
            }
        });
    }
}
