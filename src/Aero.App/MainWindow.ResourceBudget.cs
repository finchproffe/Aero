namespace Aero.App;

public sealed partial class MainWindow
{
    private static readonly TimeSpan ResourceBudgetInterval = TimeSpan.FromSeconds(15);

    private DateTimeOffset _lastResourceBudgetCheck;
    private bool _resourceBudgetCheckRunning;

    /// <summary>
    /// Freezes a background tab that stays over the CPU or memory budget for two samples in a
    /// row (about 30 s), so one runaway site cannot slow the whole machine down.
    /// </summary>
    private async Task RunResourceBudgetCycleAsync()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!_settings.ResourceBudgetEnabled ||
            _resourceBudgetCheckRunning ||
            !_isInitialized ||
            now - _lastResourceBudgetCheck < ResourceBudgetInterval)
        {
            return;
        }

        _lastResourceBudgetCheck = now;
        _resourceBudgetCheckRunning = true;
        try
        {
            long memoryBudgetBytes = _settings.BackgroundMemoryBudgetMb * 1024L * 1024L;
            foreach (TaskManagerEntry entry in await BuildTaskManagerSnapshotAsync())
            {
                if (entry.Target is not BrowserTabState tab)
                {
                    continue;
                }

                bool overCpu = entry.CpuPercent > _settings.BackgroundCpuBudgetPercent;
                bool overMemory = entry.MemoryBytes > memoryBudgetBytes;
                if (!(overCpu || overMemory) || !CanEnforceResourceBudget(tab))
                {
                    tab.ResourceBudgetStrikes = 0;
                    continue;
                }

                if (++tab.ResourceBudgetStrikes < 2)
                {
                    continue;
                }

                tab.ResourceBudgetStrikes = 0;
                if (await SuspendTabForMemoryAsync(tab))
                {
                    string site = (tab.CurrentUri ?? tab.LastNavigationUri)?.Host ?? DisplayTabTitle(tab.Title);
                    string usage = overCpu
                        ? $"{entry.CpuPercent:0}% CPU"
                        : FormatBytes(entry.MemoryBytes);
                    ShowNoticeToast(
                        Localization.AeroLocalizer.Format("Notice.BudgetFrozen", site),
                        Localization.AeroLocalizer.Format("Notice.BudgetFrozenDetail", usage));
                }
            }
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // The runtime can restart between samples; the next cycle starts clean.
        }
        finally
        {
            _resourceBudgetCheckRunning = false;
        }
    }

    private bool CanEnforceResourceBudget(BrowserTabState tab)
    {
        try
        {
            return !ReferenceEquals(tab, ActiveTab) &&
                !IsSplitParticipant(tab) &&
                !tab.IsClosing &&
                !tab.IsDiscarded &&
                !tab.IsLoading &&
                tab.Browser.CoreWebView2 is { IsSuspended: false } &&
                !(_settings.KeepAudioTabsAwake && tab.IsPlayingAudio) &&
                !(_settings.KeepDownloadingTabsAwake && Volatile.Read(ref tab.ActiveDownloadCount) > 0) &&
                !HostMatchesAny((tab.CurrentUri ?? tab.LastNavigationUri)?.Host, _settings.MemorySaverSiteExceptions);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false; // Controller closed mid-check.
        }
    }

    private void ShowNoticeToast(string title, string subtitle)
    {
        DownloadToastTitle.Text = title;
        DownloadToastSubtitle.Text = subtitle;
        DownloadToastViewButton.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        DownloadToast.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        DownloadToast.Opacity = 0;
        DownloadToastTransform.TranslateY = 12;
        AnimateOpacity(DownloadToast, 0, 1, StandardAnimationDuration);
        AnimateDouble(DownloadToastTransform, "TranslateY", 12, 0, StandardAnimationDuration);
        _downloadToastTimer.Stop();
        _downloadToastTimer.Start();
    }
}
