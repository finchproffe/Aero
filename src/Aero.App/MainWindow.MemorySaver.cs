using Aero.App.Settings;
using Aero.WebView;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Aero.App;

public sealed partial class MainWindow
{
    private string TabSnapshotFolder => System.IO.Path.Combine(
        _isPrivateWindow && !string.IsNullOrWhiteSpace(_privateUserDataFolder)
            ? _privateUserDataFolder
            : _browserProfile.StorageRoot,
        "TabSnapshots");

    private bool _memorySaverCycleInProgress;
    private int _discardedTabsThisSession;
    private long _lastMemorySavingsEstimateBytes;
    private int _lastProtectedDownloads;
    private int _lastProtectedForms;
    private int _lastProtectedAudio;

    private void InitializeMemorySaverFeatures()
    {
        try
        {
            Directory.CreateDirectory(TabSnapshotFolder);
            DateTime staleBefore = DateTime.UtcNow.AddDays(-1);
            foreach (string file in Directory.EnumerateFiles(TabSnapshotFolder, "*.jpg"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < staleBefore)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // A stale preview is harmless and can be removed next time.
                }
            }
        }
        catch
        {
            // Memory saving still works without previews.
        }
    }

    private static WebView2 CreateBrowserControl() =>
        WebViewControlFactory.CreateBrowserControl();

    private static Image CreateSnapshotView()
    {
        Image image = new()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Stretch = Stretch.UniformToFill,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        Canvas.SetZIndex(image, 10);
        return image;
    }

    private async Task RunMemorySaverCycleAsync(bool forceImmediate = false)
    {
        if (_memorySaverCycleInProgress ||
            !_isInitialized ||
            (!forceImmediate &&
             (!_settings.SleepBackgroundTabs ||
              _settings.MemorySaverMode == MemorySaverMode.Off)))
        {
            return;
        }

        _memorySaverCycleInProgress = true;
        try
        {
            bool manualRequest = forceImmediate;
            long workingSetBefore = GetBrowserWorkingSetBytes();
            bool isOverMemoryLimit =
                _settings.MemoryLimitMb > 0 &&
                workingSetBefore > _settings.MemoryLimitMb * 1024L * 1024L;
            bool aggressiveCycle = forceImmediate || isOverMemoryLimit;
            MemorySaverForecast forecast = CalculateMemorySaverForecast(aggressiveCycle);
            _lastMemorySavingsEstimateBytes = forecast.EstimatedSavingsBytes;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan lowMemoryAfter;
            TimeSpan suspendAfter;

            if (aggressiveCycle || _settings.MemorySaverMode == MemorySaverMode.Maximum)
            {
                lowMemoryAfter = TimeSpan.FromSeconds(12);
                suspendAfter = TimeSpan.FromSeconds(_settings.BackgroundTabSleepSeconds);
            }
            else
            {
                // Low memory target is cheap and lossless (no freeze, no reload), so apply it soon.
                lowMemoryAfter = TimeSpan.FromSeconds(30);
                suspendAfter = TimeSpan.FromMinutes(3);
            }

            int suspended = 0;
            _lastProtectedDownloads = 0;
            _lastProtectedForms = 0;
            _lastProtectedAudio = 0;
            long targetSavingsBytes = isOverMemoryLimit
                ? Math.Max(0, workingSetBefore - _settings.MemoryLimitMb * 1024L * 1024L)
                : long.MaxValue;
            long estimatedSavingsApplied = 0;
            long estimatedPerTabBytes = (long)(workingSetBefore /
                (double)(Math.Max(1, _tabs.Count(tab => !tab.IsDiscarded)) + 2));

            foreach (BrowserTabState state in _tabs
                         .OrderBy(tab => IsTabEffectivelyPinned(tab))
                         .ThenBy(tab => tab.LastBackgroundedAt ?? tab.LastActivatedAt)
                         .ToArray())
            {
                if (!manualRequest &&
                    isOverMemoryLimit &&
                    estimatedSavingsApplied >= targetSavingsBytes)
                {
                    break;
                }

                if (_settings.KeepDownloadingTabsAwake &&
                    Volatile.Read(ref state.ActiveDownloadCount) > 0)
                {
                    _lastProtectedDownloads++;
                    continue;
                }

                if (_settings.KeepAudioTabsAwake && state.IsPlayingAudio)
                {
                    _lastProtectedAudio++;
                    continue;
                }

                if (!CanMemorySaveTab(state, now, aggressiveCycle))
                {
                    continue;
                }

                TimeSpan idle = GetBackgroundIdleTime(state, now);

                if (aggressiveCycle || idle >= suspendAfter)
                {
                    if (await SuspendTabForMemoryAsync(state))
                    {
                        suspended++;
                        estimatedSavingsApplied += (long)(estimatedPerTabBytes * 0.18);
                    }
                    continue;
                }

                if (idle >= lowMemoryAfter)
                {
                    ApplyLowMemoryTarget(state);
                }
            }

            if (manualRequest || isOverMemoryLimit)
            {
                string protectedSummary = BuildProtectedTabsSummary();
                string estimate = FormatBytes(_lastMemorySavingsEstimateBytes);
                MemorySaverStatusText.Text = suspended > 0
                    ? $"Estimated saving {estimate}: {suspended} background tabs frozen without reloading.{protectedSummary}"
                    : $"Nothing else can be freed right now.{protectedSummary}";

                GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
            }
            else
            {
                UpdateMemorySaverStatusText();
            }
        }
        finally
        {
            _memorySaverCycleInProgress = false;
        }
    }

    private bool CanMemorySaveTab(BrowserTabState state, DateTimeOffset now, bool forceImmediate)
    {
        if (ReferenceEquals(state, ActiveTab) ||
            IsSplitParticipant(state) ||
            state.IsClosing ||
            state.IsDiscarded ||
            state.RestoreInProgress ||
            state.DiscardInProgress ||
            state.SuspendInProgress ||
            state.IsLoading ||
            state.PendingUri is not null ||
            state.PendingStartPage)
        {
            return false;
        }

        if (_settings.KeepAudioTabsAwake && state.IsPlayingAudio)
        {
            return false;
        }

        if (_settings.KeepDownloadingTabsAwake &&
            Volatile.Read(ref state.ActiveDownloadCount) > 0)
        {
            return false;
        }

        if (HostMatchesAny(
            (state.CurrentUri ?? state.LastNavigationUri)?.Host,
            _settings.MemorySaverSiteExceptions))
        {
            return false;
        }

        return forceImmediate || GetBackgroundIdleTime(state, now) >= TimeSpan.FromSeconds(10);
    }

    private static TimeSpan GetBackgroundIdleTime(
        BrowserTabState state,
        DateTimeOffset now) =>
        now - (state.LastBackgroundedAt ?? state.LastActivatedAt);

    private static void ApplyLowMemoryTarget(BrowserTabState state)
    {
        try
        {
            CoreWebView2? core = state.Browser.CoreWebView2;
            if (core is null || core.IsSuspended || state.LowMemoryTargetApplied)
            {
                return;
            }

            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            state.LowMemoryTargetApplied = true;
        }
        catch
        {
            // Older runtimes can ignore the newer memory target API.
        }
    }

    private async Task<bool> SuspendTabForMemoryAsync(BrowserTabState state)
    {
        if (state.Browser.CoreWebView2 is not CoreWebView2 core || core.IsSuspended)
        {
            return false;
        }

        try
        {
            if (!_settings.KeepAudioTabsAwake && state.IsPlayingAudio)
            {
                await PauseMediaBeforeSleepingAsync(core);
            }

            // TrySuspendAsync sets the target to Low itself. Reset a previous manual
            // Low target first so the two memory-management modes aren't mixed.
            if (state.LowMemoryTargetApplied)
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                state.LowMemoryTargetApplied = false;
            }

            state.SuspendInProgress = true;
            bool suspended = await core.TrySuspendAsync();
            if (!suspended)
            {
                ApplyLowMemoryTarget(state);
                return false;
            }

            state.IsSuspendedByMemorySaver = true;
            ShowSleepingTabVisual(state, "Sleeping — click to resume");
            return true;
        }
        catch
        {
            ApplyLowMemoryTarget(state);
            return false;
        }
        finally
        {
            state.SuspendInProgress = false;
        }
    }

    private static async Task PauseMediaBeforeSleepingAsync(CoreWebView2 core)
    {
        try
        {
            await core.ExecuteScriptAsync("""
                (() => {
                    for (const media of document.querySelectorAll('video,audio')) {
                        try { media.pause(); } catch (_) {}
                    }
                })();
                """);
        }
        catch
        {
            // Sites can deny script execution; suspension still continues.
        }
    }

    private async Task<bool> DiscardTabAsync(BrowserTabState state)
    {
        // Discarding closes the WebView, which would wipe a burner tab's InPrivate session.
        if (IsBurnerTab(state) ||
            ReferenceEquals(state, ActiveTab) ||
            IsSplitParticipant(state) ||
            state.IsDiscarded ||
            state.DiscardInProgress ||
            state.Browser.CoreWebView2 is null ||
            (_settings.KeepDownloadingTabsAwake &&
             Volatile.Read(ref state.ActiveDownloadCount) > 0) ||
            (_settings.KeepAudioTabsAwake && state.IsPlayingAudio))
        {
            return false;
        }

        state.DiscardInProgress = true;
        try
        {
            if (_settings.ProtectFilledForms &&
                await HasUnsavedFormDataAsync(state))
            {
                return false;
            }

            await CaptureTabRestoreStateAsync(state);
            await CaptureTabSnapshotAsync(state);

            Uri? restoreUri = state.CurrentUri ?? state.PendingUri;
            bool restoreStartPage = state.IsStartPage || state.PendingStartPage || restoreUri is null;
            state.PendingStartPage = restoreStartPage;
            state.PendingUri = restoreStartPage ? null : restoreUri;
            state.IsLoading = false;
            StopLoadingAnimation(state);

            ReleaseTabWebView(state, createReplacement: true);
            state.IsDiscarded = true;
            state.IsSuspendedByMemorySaver = false;
            state.LowMemoryTargetApplied = false;
            state.DiscardedAt = DateTimeOffset.UtcNow;
            _discardedTabsThisSession++;

            ShowSleepingTabVisual(state, "Unloaded to save memory — click to restore");
            UpdateVisibleBrowser();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            state.DiscardInProgress = false;
        }
    }

    private static async Task<bool> HasUnsavedFormDataAsync(BrowserTabState state)
    {
        if (state.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            state.HasUnsavedFormData = false;
            return false;
        }

        try
        {
            string result = await core.ExecuteScriptAsync("""
                (() => {
                    const controls = document.querySelectorAll(
                        'input:not([type="hidden"]):not([type="button"]):not([type="submit"]):not([type="reset"]), textarea, select');
                    for (const control of controls) {
                        if (control.disabled || control.readOnly) continue;
                        if (control instanceof HTMLInputElement &&
                            (control.type === 'checkbox' || control.type === 'radio')) {
                            if (control.checked !== control.defaultChecked) return true;
                            continue;
                        }
                        if (control instanceof HTMLSelectElement) {
                            if (Array.from(control.options).some(option =>
                                option.selected !== option.defaultSelected)) return true;
                            continue;
                        }
                        if (control.value !== control.defaultValue) return true;
                    }
                    return false;
                })()
                """);
            state.HasUnsavedFormData = string.Equals(
                result,
                "true",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // If the document is navigating or blocks script execution, keep the
            // last known value. A false negative must not overwrite prior evidence.
        }

        return state.HasUnsavedFormData;
    }

    private static async Task CaptureTabRestoreStateAsync(BrowserTabState state)
    {
        state.HasRestoreScrollPosition = false;
        if (state.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        try
        {
            string json = await core.ExecuteScriptAsync(
                "(() => ({ x: window.scrollX || 0, y: window.scrollY || 0 }))()");
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("x", out JsonElement x) ||
                !root.TryGetProperty("y", out JsonElement y) ||
                !x.TryGetDouble(out double scrollX) ||
                !y.TryGetDouble(out double scrollY))
            {
                return;
            }

            state.RestoreScrollX = scrollX;
            state.RestoreScrollY = scrollY;
            state.HasRestoreScrollPosition = true;
        }
        catch
        {
            // URL and zoom are still restored when scroll capture is unavailable.
        }
    }

    private static async Task RestoreTabPageStateAsync(BrowserTabState state)
    {
        if (!state.HasRestoreScrollPosition ||
            state.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        double x = state.RestoreScrollX;
        double y = state.RestoreScrollY;
        state.HasRestoreScrollPosition = false;
        try
        {
            string scrollX = x.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string scrollY = y.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string script =
                "(() => { const restore = () => window.scrollTo(" +
                scrollX + ", " + scrollY +
                "); restore(); requestAnimationFrame(restore); " +
                "setTimeout(restore, 250); setTimeout(restore, 900); })()";
            await core.ExecuteScriptAsync(script);
        }
        catch
        {
            // A dynamic page may replace itself while restoration is running.
        }
    }

    private async Task CaptureTabSnapshotAsync(BrowserTabState state)
    {
        DeleteTabSnapshot(state);
        if (state.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(TabSnapshotFolder);
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(TabSnapshotFolder);
            StorageFile file = await folder.CreateFileAsync(
                $"{Guid.NewGuid():N}.jpg",
                CreationCollisionOption.ReplaceExisting);

            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, stream);
            state.SnapshotPath = file.Path;
        }
        catch
        {
            state.SnapshotPath = null;
        }
    }

    private void LoadTabSnapshot(BrowserTabState state)
    {
        state.SnapshotView.Source = null;
        if (string.IsNullOrWhiteSpace(state.SnapshotPath) || !File.Exists(state.SnapshotPath))
        {
            return;
        }

        try
        {
            Uri fileUri = new UriBuilder
            {
                Scheme = Uri.UriSchemeFile,
                Path = state.SnapshotPath
            }.Uri;
            state.SnapshotView.Source = new BitmapImage(fileUri);
        }
        catch
        {
            state.SnapshotView.Source = null;
        }
    }

    private async Task RestoreDiscardedTabAsync(BrowserTabState state)
    {
        if (!state.IsDiscarded || state.RestoreInProgress || _environment is null)
        {
            return;
        }

        state.RestoreInProgress = true;
        LoadTabSnapshot(state);
        UpdateVisibleBrowser();
        ShowSleepingTabVisual(state, "Restoring tab…");

        try
        {
            await EnsureWebViewInitializedAsync(state);
            await ConfigureWebViewAsync(state);
            state.IsDiscarded = false;
            state.LowMemoryTargetApplied = false;

            Uri? restoreUri = state.PendingUri ?? state.CurrentUri;
            bool restoreStartPage = state.PendingStartPage ||
                state.IsStartPage ||
                restoreUri is null;

            if (restoreStartPage)
            {
                ShowStartPage(state);
            }
            else
            {
                Navigate(state, restoreUri!);
            }

            UpdateVisibleBrowser();
        }
        catch
        {
            state.IsDiscarded = true;
            state.RestoreInProgress = false;
            ShowSleepingTabVisual(state, "Could not restore — click to retry");
            UpdateVisibleBrowser();
        }
    }

    private async Task CompleteTabRestoreAsync(BrowserTabState state)
    {
        if (!state.RestoreInProgress)
        {
            return;
        }

        await RestoreTabPageStateAsync(state);
        state.RestoreInProgress = false;
        state.SnapshotView.Source = null;
        DeleteTabSnapshot(state);
        UpdateVisibleBrowser();
        UpdateMemorySaverStatusText();
    }

    private void ReleaseTabWebView(BrowserTabState state, bool createReplacement)
    {
        WebView2 oldBrowser = state.Browser;
        CoreWebView2? oldCore = oldBrowser.CoreWebView2;
        DetachBrowserPaneInput(oldBrowser);

        // Event handlers capture BrowserTabState. Detach them before Close() so a
        // closed controller cannot keep the tab, its visual tree, or this window alive.
        DetachWebViewEvents(state);

        if (state.WebResourceFilterAttached)
        {
            try
            {
                oldCore?.RemoveWebResourceRequestedFilter(
                    "*",
                    CoreWebView2WebResourceContext.All);
            }
            catch
            {
                // The filter may already be unavailable after a renderer crash.
            }
            finally
            {
                state.WebResourceFilterAttached = false;
            }
        }

        oldBrowser.Visibility = Visibility.Collapsed;
        WebViewControllerLifecycle.TryStop(oldBrowser);

        BrowserHost.Children.Remove(oldBrowser);
        WebViewControllerLifecycle.TryClose(oldBrowser);

        state.IsPlayingAudio = false;
        state.MediaBridgeScriptId = null;
        ResetMediaState(state);
        state.IsSuspendedByMemorySaver = false;
        state.LowMemoryTargetApplied = false;

        if (!createReplacement)
        {
            return;
        }

        WebView2 replacement = CreateBrowserControl();
        Canvas.SetZIndex(replacement, 0);
        state.Browser = replacement;
        AttachBrowserPaneInput(state, replacement);
        BrowserHost.Children.Add(replacement);
        ApplySplitViewLayout();
    }

    private static void ShowSleepingTabVisual(BrowserTabState state, string tooltip)
    {
        state.FaviconImage.Source = null;
        state.FaviconFallbackAttempted = false;
        state.AudioButton.Visibility = Visibility.Collapsed;
        state.FaviconImage.Visibility = Visibility.Collapsed;
        state.Icon.Visibility = Visibility.Visible;
        state.Icon.Glyph = "\uE823";
        ToolTipService.SetToolTip(state.Header, $"{state.Title} — {tooltip}");
    }

    private void DeleteTabSnapshot(BrowserTabState state)
    {
        string? path = state.SnapshotPath;
        state.SnapshotPath = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The preview is temporary and can be cleaned on the next start.
        }
    }

    private Action ProtectTabWhileDownloadActive(
        BrowserTabState state,
        CoreWebView2DownloadOperation operation)
    {
        int stopped = 0;
        int protectionApplied = 1;
        Interlocked.Increment(ref state.ActiveDownloadCount);

        void SetProtection(bool enabled)
        {
            int desired = enabled ? 1 : 0;
            if (Interlocked.Exchange(ref protectionApplied, desired) == desired)
            {
                return;
            }

            if (enabled)
            {
                Interlocked.Increment(ref state.ActiveDownloadCount);
            }
            else
            {
                Interlocked.Decrement(ref state.ActiveDownloadCount);
            }
        }

        void StopTracking()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0)
            {
                return;
            }

            try
            {
                operation.StateChanged -= StateChanged;
            }
            catch
            {
                // The download controller can disappear during shutdown.
            }

            SetProtection(enabled: false);
        }

        void StateChanged(object? sender, object args)
        {
            try
            {
                CoreWebView2DownloadState downloadState = operation.State;
                SetProtection(downloadState == CoreWebView2DownloadState.InProgress);
                if (downloadState == CoreWebView2DownloadState.Completed ||
                    downloadState == CoreWebView2DownloadState.Interrupted &&
                    !operation.CanResume)
                {
                    StopTracking();
                }
            }
            catch
            {
                StopTracking();
            }
        }

        operation.StateChanged += StateChanged;
        StateChanged(null, EventArgs.Empty);
        return StopTracking;
    }

    private long GetBrowserWorkingSetBytes()
    {
        HashSet<int> processIds = [Environment.ProcessId];
        try
        {
            if (_environment is not null)
            {
                foreach (CoreWebView2ProcessInfo processInfo in _environment.GetProcessInfos())
                {
                    processIds.Add(processInfo.ProcessId);
                }
            }
        }
        catch
        {
            // A restarting WebView runtime may briefly reject process queries.
        }

        long total = 0;
        foreach (int processId in processIds)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                total += Math.Max(0, process.WorkingSet64);
            }
            catch
            {
                // Processes can exit between enumeration and the memory read.
            }
        }

        return total;
    }

    private MemorySaverForecast CalculateMemorySaverForecast(bool forceImmediate)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BrowserTabState[] candidates = _tabs
            .Where(tab => CanMemorySaveTab(tab, now, forceImmediate))
            .ToArray();
        int suspendOnly = candidates.Length;
        int loadedTabs = Math.Max(1, _tabs.Count(tab => !tab.IsDiscarded));
        long workingSet = GetBrowserWorkingSetBytes();

        // Shared browser/GPU processes cannot be attributed to one tab without an
        // asynchronous renderer snapshot. Use a conservative fraction of the live
        // browser working set so the forecast remains useful without overpromising.
        double estimatedPerTab = workingSet / (double)(loadedTabs + 2);
        long estimatedSavings = (long)Math.Min(
            workingSet * 0.80,
            estimatedPerTab * suspendOnly * 0.18);

        return new MemorySaverForecast(
            workingSet,
            Math.Max(0, estimatedSavings),
            0,
            suspendOnly,
            _tabs.Count(tab =>
                _settings.KeepDownloadingTabsAwake &&
                Volatile.Read(ref tab.ActiveDownloadCount) > 0),
            _tabs.Count(tab => _settings.KeepAudioTabsAwake && tab.IsPlayingAudio),
            _tabs.Count(tab => _settings.ProtectFilledForms && tab.HasUnsavedFormData));
    }

    private object BuildMemorySaverOverview()
    {
        MemorySaverForecast forecast = CalculateMemorySaverForecast(forceImmediate: true);
        return new
        {
            currentBytes = forecast.WorkingSetBytes,
            estimatedSavingsBytes = forecast.EstimatedSavingsBytes,
            eligibleTabs = forecast.UnloadableTabCount + forecast.SuspendOnlyTabCount,
            protectedDownloads = forecast.ProtectedDownloadTabs,
            protectedAudio = forecast.ProtectedAudioTabs,
            protectedForms = forecast.ProtectedFormTabs,
            sleepingTabs = _tabs.Count(IsTabSleepingForMemorySaver),
            unloadedTabs = _tabs.Count(tab => tab.IsDiscarded)
        };
    }

    private static bool IsTabSleepingForMemorySaver(BrowserTabState state)
    {
        if (state.IsSuspendedByMemorySaver)
        {
            return true;
        }

        try
        {
            return state.Browser.CoreWebView2?.IsSuspended == true;
        }
        catch
        {
            return false;
        }
    }

    private string BuildProtectedTabsSummary()
    {
        List<string> reasons = [];
        if (_lastProtectedDownloads > 0)
        {
            reasons.Add($"{_lastProtectedDownloads} downloading");
        }
        if (_lastProtectedAudio > 0)
        {
            reasons.Add($"{_lastProtectedAudio} playing audio");
        }
        if (_lastProtectedForms > 0)
        {
            reasons.Add($"{_lastProtectedForms} with filled forms");
        }

        return reasons.Count == 0
            ? string.Empty
            : $" Protected: {string.Join(", ", reasons)}.";
    }

    private async void FreeMemoryNowButton_Click(object sender, RoutedEventArgs e)
    {
        MemorySaverStatusText.Text = "Freezing background tabs without closing them…";
        await RunMemorySaverCycleAsync(forceImmediate: true);
    }

    private void UpdateMemorySaverStatusText()
    {
        if (MemorySaverStatusText is null)
        {
            return;
        }

        int sleeping = _tabs.Count(IsTabSleepingForMemorySaver);
        int unloaded = _tabs.Count(tab => tab.IsDiscarded);
        MemorySaverForecast forecast = CalculateMemorySaverForecast(forceImmediate: true);
        string currentMemory = FormatBytes(forecast.WorkingSetBytes);
        string estimatedSavings = FormatBytes(forecast.EstimatedSavingsBytes);
        string memoryLimit = _settings.MemoryLimitMb > 0
            ? $" of {_settings.MemoryLimitMb} MB limit"
            : string.Empty;

        MemorySaverStatusText.Text = _settings.MemorySaverMode switch
        {
            MemorySaverMode.Off => "Memory Saver is off.",
            _ when sleeping == 0 && unloaded == 0 =>
                $"Using {currentMemory}{memoryLimit}. Estimated saving: {estimatedSavings}; the active tab stays prioritized.",
            _ => $"Using {currentMemory}{memoryLimit}: {sleeping} sleeping, {unloaded} unloaded. Estimated saving: {estimatedSavings}."
        };
    }


    private void ApplyMemorySaverSettings()
    {
        if (_settings.SleepBackgroundTabs && _settings.MemorySaverMode != MemorySaverMode.Off)
        {
            UpdateMemorySaverStatusText();
            return;
        }

        foreach (BrowserTabState state in _tabs)
        {
            try
            {
                if (state.Browser.CoreWebView2 is not CoreWebView2 core)
                {
                    continue;
                }

                if (core.IsSuspended)
                {
                    core.Resume();
                }
                else
                {
                    core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                }

                state.IsSuspendedByMemorySaver = false;
                state.LowMemoryTargetApplied = false;
                if (!state.IsStartPage)
                {
                    Browser_FaviconChanged(state);
                }
            }
            catch
            {
                // A discarded or crashed tab can be restored when selected.
            }
        }

        UpdateMemorySaverStatusText();
    }

    private void CleanupMemorySaverState()
    {
        CleanupTabGroupState();
        FinishTabDrag(animateDrop: false);
        TabDragOverlay.Children.Clear();

        foreach (BrowserTabState state in _tabs.ToArray())
        {
            StopLoadingAnimation(state);
            ReleaseTabHeader(state);
            state.FaviconImage.Source = null;
            state.SnapshotView.Source = null;
            BrowserHost.Children.Remove(state.SnapshotView);
            DeleteTabSnapshot(state);
            ReleaseTabWebView(state, createReplacement: false);
        }

        _tabs.Clear();
        _activeTab = null;
        BrowserHost.Children.Clear();
        TabStripPanel.Children.Clear();
    }

    private readonly record struct MemorySaverForecast(
        long WorkingSetBytes,
        long EstimatedSavingsBytes,
        int UnloadableTabCount,
        int SuspendOnlyTabCount,
        int ProtectedDownloadTabs,
        int ProtectedAudioTabs,
        int ProtectedFormTabs);
}
