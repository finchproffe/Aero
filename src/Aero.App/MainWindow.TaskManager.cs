using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Windows.Graphics;
using Windows.UI;

namespace Aero.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<int, TaskManagerCpuSample> _taskManagerCpuSamples = [];
    private AeroTaskManagerWindow? _taskManagerWindow;
    private bool _taskManagerOwnerCloseHooked;
    private CoreWebView2Profile? _taskManagerExtensionQueryProfile;
    private Task<IReadOnlyList<CoreWebView2BrowserExtension>>? _taskManagerExtensionQuery;
    private DateTimeOffset _taskManagerExtensionsLastRefresh;

    private void TaskManagerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        OpenTaskManager();
    }

    private void TaskManagerAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenTaskManager();
    }

    private void OpenTaskManager()
    {
        if (_taskManagerWindow is not null)
        {
            _taskManagerWindow.Activate();
            return;
        }

        _taskManagerWindow = new AeroTaskManagerWindow(this);
        _taskManagerWindow.Closed += TaskManagerWindow_Closed;

        if (!_taskManagerOwnerCloseHooked)
        {
            _taskManagerOwnerCloseHooked = true;
            Closed += MainWindow_TaskManagerOwnerClosed;
        }

        _taskManagerWindow.Activate();
    }

    private void TaskManagerWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_taskManagerWindow is AeroTaskManagerWindow taskManager)
        {
            taskManager.StopRefreshing();
            taskManager.Closed -= TaskManagerWindow_Closed;
        }

        _taskManagerWindow = null;
    }

    private void MainWindow_TaskManagerOwnerClosed(object sender, WindowEventArgs args)
    {
        _taskManagerWindow?.Close();
        _taskManagerWindow = null;
        _taskManagerCpuSamples.Clear();
    }

    private async Task<IReadOnlyList<TaskManagerEntry>> BuildTaskManagerSnapshotAsync()
    {
        List<TaskManagerEntry> entries = [];
        if (_environment is null)
        {
            return entries;
        }

        IReadOnlyList<TaskManagerWebViewProcess> webViewProcesses;
        try
        {
            IReadOnlyList<CoreWebView2ProcessExtendedInfo> detailed =
                await _environment.GetProcessExtendedInfosAsync();
            webViewProcesses = detailed
                .Select(info => new TaskManagerWebViewProcess(
                    info.ProcessInfo,
                    info.AssociatedFrameInfos))
                .ToList();
        }
        catch
        {
            // Older or temporarily restarting WebView2 runtimes can reject the detailed
            // query. Fall back to the basic process list so the manager remains useful.
            webViewProcesses = _environment.GetProcessInfos()
                .Select(info => new TaskManagerWebViewProcess(info, []))
                .ToList();
        }

        Dictionary<int, ProcessResourceSnapshot> processResources = [];
        foreach (TaskManagerWebViewProcess info in webViewProcesses)
        {
            int processId = info.ProcessInfo.ProcessId;
            if (!processResources.ContainsKey(processId))
            {
                processResources[processId] = ReadProcessResources(processId);
            }
        }

        HashSet<int> liveProcessIds = processResources.Keys.ToHashSet();
        foreach (int staleProcessId in _taskManagerCpuSamples.Keys
                     .Where(processId => !liveProcessIds.Contains(processId))
                     .ToArray())
        {
            _taskManagerCpuSamples.Remove(staleProcessId);
        }

        Dictionary<uint, BrowserTabState> tabsByMainFrame = [];
        foreach (BrowserTabState tab in _tabs)
        {
            try
            {
                uint frameId = tab.Browser.CoreWebView2?.FrameId ?? 0;
                if (frameId != 0)
                {
                    tabsByMainFrame[frameId] = tab;
                }
            }
            catch
            {
                // A discarded or restarting tab currently has no valid main frame.
            }
        }

        Dictionary<BrowserTabState, MutableResourceTotal> tabTotals =
            _tabs.ToDictionary(tab => tab, _ => new MutableResourceTotal());
        Dictionary<string, MutableResourceTotal> extensionTotals =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (TaskManagerWebViewProcess extendedInfo in webViewProcesses)
        {
            CoreWebView2ProcessInfo processInfo = extendedInfo.ProcessInfo;
            if (!processInfo.Kind.ToString().Equals("Renderer", StringComparison.OrdinalIgnoreCase) ||
                !processResources.TryGetValue(processInfo.ProcessId, out ProcessResourceSnapshot resource))
            {
                continue;
            }

            HashSet<BrowserTabState> owningTabs = [];
            HashSet<string> owningExtensions = new(StringComparer.OrdinalIgnoreCase);

            foreach (CoreWebView2FrameInfo frameInfo in extendedInfo.AssociatedFrameInfos)
            {
                try
                {
                    uint rootFrameId = GetRootFrameId(frameInfo);
                    if (rootFrameId != 0 &&
                        tabsByMainFrame.TryGetValue(rootFrameId, out BrowserTabState? tab))
                    {
                        owningTabs.Add(tab);
                    }

                    if (TryGetExtensionId(frameInfo.Source, out string extensionId))
                    {
                        owningExtensions.Add(extensionId);
                    }
                }
                catch
                {
                    // Frame information is a snapshot and can become stale immediately.
                }
            }

            if (owningTabs.Count > 0)
            {
                double cpuShare = resource.CpuPercent / owningTabs.Count;
                long memoryShare = resource.MemoryBytes / owningTabs.Count;
                foreach (BrowserTabState tab in owningTabs)
                {
                    MutableResourceTotal total = tabTotals[tab];
                    total.CpuPercent += cpuShare;
                    total.MemoryBytes += memoryShare;
                    total.ProcessIds.Add(processInfo.ProcessId);
                }
            }

            if (owningExtensions.Count > 0)
            {
                double cpuShare = resource.CpuPercent / owningExtensions.Count;
                long memoryShare = resource.MemoryBytes / owningExtensions.Count;
                foreach (string extensionId in owningExtensions)
                {
                    if (!extensionTotals.TryGetValue(extensionId, out MutableResourceTotal? total))
                    {
                        total = new MutableResourceTotal();
                        extensionTotals[extensionId] = total;
                    }

                    total.CpuPercent += cpuShare;
                    total.MemoryBytes += memoryShare;
                    total.ProcessIds.Add(processInfo.ProcessId);
                }
            }
        }

        entries.Add(TaskManagerEntry.CreateHeader("Tabs"));
        foreach (BrowserTabState tab in _tabs)
        {
            MutableResourceTotal total = tabTotals[tab];
            string status = GetTaskManagerTabStatus(tab);
            string details = (tab.CurrentUri ?? tab.PendingUri)?.Host ??
                (tab.IsStartPage || tab.PendingStartPage ? "Aero Home" : "No active page");

            entries.Add(new TaskManagerEntry(
                Key: $"tab:{RuntimeHelpers.GetHashCode(tab)}",
                Kind: TaskManagerEntryKind.Tab,
                Name: string.IsNullOrWhiteSpace(tab.Title) ? "New tab" : tab.Title,
                Details: details,
                CpuPercent: total.CpuPercent,
                MemoryBytes: total.MemoryBytes,
                ProcessIds: total.ProcessIds.OrderBy(id => id).ToArray(),
                Status: status,
                Target: tab));
        }

        IReadOnlyList<CoreWebView2BrowserExtension> installedExtensions =
            await GetTaskManagerExtensionsAsync();
        entries.Add(TaskManagerEntry.CreateHeader("Extensions"));
        if (installedExtensions.Count == 0)
        {
            entries.Add(TaskManagerEntry.CreateMessage("No extensions installed"));
        }
        else
        {
            foreach (CoreWebView2BrowserExtension extension in installedExtensions
                         .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                extensionTotals.TryGetValue(extension.Id, out MutableResourceTotal? total);
                total ??= new MutableResourceTotal();

                entries.Add(new TaskManagerEntry(
                    Key: $"extension:{extension.Id}",
                    Kind: TaskManagerEntryKind.Extension,
                    Name: extension.Name,
                    Details: extension.Id,
                    CpuPercent: total.CpuPercent,
                    MemoryBytes: total.MemoryBytes,
                    ProcessIds: total.ProcessIds.OrderBy(id => id).ToArray(),
                    Status: extension.IsEnabled ? "Enabled" : "Disabled",
                    Target: extension));
            }
        }

        entries.Add(TaskManagerEntry.CreateHeader("WebView2 processes"));
        foreach (TaskManagerWebViewProcess extendedInfo in webViewProcesses
                     .OrderBy(info => GetTaskManagerProcessSortOrder(info.ProcessInfo.Kind.ToString()))
                     .ThenBy(info => info.ProcessInfo.ProcessId))
        {
            CoreWebView2ProcessInfo processInfo = extendedInfo.ProcessInfo;
            processResources.TryGetValue(processInfo.ProcessId, out ProcessResourceSnapshot resource);
            string kind = processInfo.Kind.ToString();
            string displayName = kind.Equals("GPU", StringComparison.OrdinalIgnoreCase)
                ? "GPU process"
                : $"WebView2 {GetFriendlyProcessKind(kind)}";
            string frameDetails = extendedInfo.AssociatedFrameInfos.Count == 0
                ? "Shared browser runtime"
                : $"{extendedInfo.AssociatedFrameInfos.Count} active frame(s)";

            entries.Add(new TaskManagerEntry(
                Key: $"process:{processInfo.ProcessId}",
                Kind: TaskManagerEntryKind.Process,
                Name: displayName,
                Details: frameDetails,
                CpuPercent: resource.CpuPercent,
                MemoryBytes: resource.MemoryBytes,
                ProcessIds: [processInfo.ProcessId],
                Status: "Running",
                Target: processInfo));
        }

        return entries;
    }

    private async Task<IReadOnlyList<CoreWebView2BrowserExtension>> GetTaskManagerExtensionsAsync()
    {
        CoreWebView2Profile? profile = null;
        foreach (BrowserTabState tab in _tabs)
        {
            try
            {
                profile = tab.Browser.CoreWebView2?.Profile;
                if (profile is not null)
                {
                    break;
                }
            }
            catch
            {
                // Closed and crashed controllers do not expose a profile.
            }
        }

        if (profile is null)
        {
            return _installedExtensions;
        }

        if (DateTimeOffset.UtcNow - _taskManagerExtensionsLastRefresh <
            TimeSpan.FromSeconds(10))
        {
            return _installedExtensions;
        }

        if (_taskManagerExtensionQuery is null ||
            !ReferenceEquals(_taskManagerExtensionQueryProfile, profile))
        {
            _taskManagerExtensionQueryProfile = profile;
            _taskManagerExtensionQuery = profile.GetBrowserExtensionsAsync().AsTask();
        }

        Task<IReadOnlyList<CoreWebView2BrowserExtension>> query =
            _taskManagerExtensionQuery!;
        Task completed = await Task.WhenAny(query, Task.Delay(TimeSpan.FromSeconds(2)));
        if (!ReferenceEquals(completed, query))
        {
            return _installedExtensions;
        }

        try
        {
            _installedExtensions = await query;
            _taskManagerExtensionsLastRefresh = DateTimeOffset.UtcNow;
            return _installedExtensions;
        }
        catch
        {
            return _installedExtensions;
        }
        finally
        {
            if (ReferenceEquals(_taskManagerExtensionQuery, query))
            {
                _taskManagerExtensionQuery = null;
                _taskManagerExtensionQueryProfile = null;
            }
        }
    }

    private ProcessResourceSnapshot ReadProcessResources(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.Refresh();

            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan processorTime = process.TotalProcessorTime;
            double cpuPercent = 0;

            if (_taskManagerCpuSamples.TryGetValue(processId, out TaskManagerCpuSample previous))
            {
                double wallSeconds = (now - previous.Timestamp).TotalSeconds;
                double cpuSeconds = (processorTime - previous.TotalProcessorTime).TotalSeconds;
                if (wallSeconds > 0 && cpuSeconds >= 0)
                {
                    cpuPercent = cpuSeconds / wallSeconds /
                        Math.Max(1, Environment.ProcessorCount) * 100;
                }
            }

            _taskManagerCpuSamples[processId] = new TaskManagerCpuSample(processorTime, now);
            return new ProcessResourceSnapshot(
                MemoryBytes: Math.Max(0, process.WorkingSet64),
                CpuPercent: Math.Clamp(cpuPercent, 0, 100));
        }
        catch
        {
            _taskManagerCpuSamples.Remove(processId);
            return default;
        }
    }

    private static uint GetRootFrameId(CoreWebView2FrameInfo frameInfo)
    {
        CoreWebView2FrameInfo current = frameInfo;
        int guard = 0;
        while (current.ParentFrameInfo is CoreWebView2FrameInfo parent && guard++ < 64)
        {
            current = parent;
        }

        return current.FrameId;
    }

    private static bool TryGetExtensionId(string? source, out string extensionId)
    {
        extensionId = string.Empty;
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals("chrome-extension", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        extensionId = uri.Host;
        return true;
    }

    private static string GetTaskManagerTabStatus(BrowserTabState tab)
    {
        if (tab.HasCrashed)
        {
            return "Stopped";
        }
        if (tab.IsDiscarded)
        {
            return "Unloaded";
        }
        if (tab.RestoreInProgress)
        {
            return "Restarting";
        }
        if (tab.IsLoading)
        {
            return "Loading";
        }

        bool isSuspended = tab.IsSuspendedByMemorySaver;
        if (!isSuspended)
        {
            try
            {
                isSuspended = tab.Browser.CoreWebView2?.IsSuspended == true;
            }
            catch
            {
                // A renderer can disappear between process enumeration and status read.
            }
        }

        if (isSuspended)
        {
            return "Sleeping";
        }
        if (tab.IsPlayingAudio)
        {
            return "Playing audio";
        }

        return "Running";
    }

    private static int GetTaskManagerProcessSortOrder(string kind) =>
        kind.ToUpperInvariant() switch
        {
            "BROWSER" => 0,
            "GPU" => 1,
            "RENDERER" => 2,
            "UTILITY" => 3,
            _ => 4
        };

    private static string GetFriendlyProcessKind(string kind) =>
        kind.ToUpperInvariant() switch
        {
            "BROWSER" => "browser process",
            "RENDERER" => "renderer",
            "UTILITY" => "utility process",
            "SANDBOXHELPER" => "sandbox helper",
            "PPAPIPLUGIN" => "plugin process",
            "PPAPIBROKER" => "plugin broker",
            _ => kind.ToLowerInvariant()
        };

    private Task<TaskManagerActionResult> StopTabFromTaskManagerAsync(BrowserTabState tab)
    {
        if (!_tabs.Contains(tab) || tab.IsClosing)
        {
            return Task.FromResult(new TaskManagerActionResult(
                false,
                "The tab is no longer available."));
        }

        try
        {
            Uri? restoreUri = tab.CurrentUri ?? tab.PendingUri;
            bool restoreStartPage = tab.IsStartPage || tab.PendingStartPage || restoreUri is null;

            if (!tab.IsDiscarded)
            {
                tab.PendingStartPage = restoreStartPage;
                tab.PendingUri = restoreStartPage ? null : restoreUri;
                tab.IsLoading = false;
                StopLoadingAnimation(tab);
                ReleaseTabWebView(tab, createReplacement: true);
                tab.IsDiscarded = true;
            }

            tab.HasCrashed = true;
            tab.RestoreInProgress = false;
            tab.SuspendInProgress = false;
            tab.IsSuspendedByMemorySaver = false;
            tab.LowMemoryTargetApplied = false;
            ShowSleepingTabVisual(tab, "Stopped by Aero Task Manager — restart to continue");
            UpdateVisibleBrowser();
            if (ReferenceEquals(tab, ActiveTab))
            {
                UpdateActiveChrome();
                UpdateRecoveryBar();
            }

            SaveSessionSnapshot();
            return Task.FromResult(new TaskManagerActionResult(
                true,
                "Tab stopped. Its address is preserved for restart."));
        }
        catch
        {
            return Task.FromResult(new TaskManagerActionResult(
                false,
                "Aero could not stop this tab safely."));
        }
    }

    private async Task<TaskManagerActionResult> UnloadTabFromTaskManagerAsync(BrowserTabState tab)
    {
        if (!_tabs.Contains(tab) || tab.IsClosing)
        {
            return new TaskManagerActionResult(false, "The tab is no longer available.");
        }
        if (tab.IsDiscarded)
        {
            return new TaskManagerActionResult(true, "The tab is already unloaded.");
        }

        try
        {
            if (IsSplitParticipant(tab))
            {
                BrowserTabState? partner = ReferenceEquals(tab, _splitLeftTab)
                    ? _splitRightTab
                    : _splitLeftTab;
                ExitSplitView(partner, saveSession: false);
            }

            if (ReferenceEquals(tab, ActiveTab))
            {
                BrowserTabState? replacement = _tabs.FirstOrDefault(candidate =>
                    !ReferenceEquals(candidate, tab) && !candidate.IsClosing);
                if (replacement is null)
                {
                    await CreateTabAsync(null, selectTab: true, showStartPage: true);
                }
                else
                {
                    SelectTab(replacement);
                }
            }

            bool unloaded = await DiscardTabAsync(tab);
            SaveSessionSnapshot();
            return unloaded
                ? new TaskManagerActionResult(true, "Tab unloaded. Click it or use Restart to load it again.")
                : new TaskManagerActionResult(false, "The tab could not be unloaded right now.");
        }
        catch
        {
            return new TaskManagerActionResult(false, "Aero could not unload this tab.");
        }
    }

    private async Task<TaskManagerActionResult> RestartTabFromTaskManagerAsync(BrowserTabState tab)
    {
        if (!_tabs.Contains(tab) || tab.IsClosing)
        {
            return new TaskManagerActionResult(false, "The tab is no longer available.");
        }

        TaskManagerActionResult stopped = await StopTabFromTaskManagerAsync(tab);
        if (!stopped.Success)
        {
            return stopped;
        }

        try
        {
            tab.HasCrashed = false;
            UpdateRecoveryBar();
            await RestoreDiscardedTabAsync(tab);
            SaveSessionSnapshot();
            return tab.IsDiscarded
                ? new TaskManagerActionResult(false, "The tab could not restart. Try again after WebView2 recovers.")
                : new TaskManagerActionResult(true, "Tab restarted.");
        }
        catch
        {
            return new TaskManagerActionResult(false, "The tab could not restart.");
        }
    }

    private sealed class AeroTaskManagerWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly DispatcherTimer _refreshTimer = new()
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        private readonly ListView _items = new()
        {
            SelectionMode = ListViewSelectionMode.Single,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        private readonly TextBlock _summaryText = new();
        private readonly TextBlock _statusText = new();
        private readonly Button _endTaskButton;
        private readonly Button _unloadButton;
        private readonly Button _restartButton;
        private bool _refreshing;
        private bool _suppressSelectionChanged;
        private string? _selectedKey;

        public AeroTaskManagerWindow(MainWindow owner)
        {
            _owner = owner;
            Title = "Aero Task Manager";
            Content = BuildContent();
            AppWindow.Resize(new SizeInt32(980, 680));

            _endTaskButton = BuildActionButton("End task", "\uE74D");
            _unloadButton = BuildActionButton("Unload", "\uE823");
            _restartButton = BuildActionButton("Restart", "\uE72C");

            Grid actionHost = (Grid)((Grid)Content).Children[2];
            StackPanel actionButtons = (StackPanel)actionHost.Children[1];
            actionButtons.Children.Add(_unloadButton);
            actionButtons.Children.Add(_restartButton);
            actionButtons.Children.Add(_endTaskButton);

            _items.SelectionChanged += Items_SelectionChanged;
            _endTaskButton.Click += async (_, _) =>
                await RunSelectedTabActionAsync(_owner.StopTabFromTaskManagerAsync);
            _unloadButton.Click += async (_, _) =>
                await RunSelectedTabActionAsync(_owner.UnloadTabFromTaskManagerAsync);
            _restartButton.Click += async (_, _) =>
                await RunSelectedTabActionAsync(_owner.RestartTabFromTaskManagerAsync);

            _refreshTimer.Tick += RefreshTimer_Tick;
            _refreshTimer.Start();
            _ = RefreshAsync();
        }

        public void StopRefreshing()
        {
            _refreshTimer.Stop();
            _refreshTimer.Tick -= RefreshTimer_Tick;
        }

        private UIElement BuildContent()
        {
            bool dark = _owner.RootGrid.ActualTheme != ElementTheme.Light;
            Color background = dark ? Color.FromArgb(255, 18, 18, 20) : Color.FromArgb(255, 248, 248, 250);
            Color surface = dark ? Color.FromArgb(255, 31, 31, 35) : Color.FromArgb(255, 255, 255, 255);
            Color text = dark ? Color.FromArgb(255, 244, 244, 246) : Color.FromArgb(255, 24, 24, 28);
            Color muted = dark ? Color.FromArgb(255, 168, 168, 178) : Color.FromArgb(255, 96, 96, 106);

            Grid root = new()
            {
                Background = new SolidColorBrush(background),
                RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Border top = new()
            {
                Padding = new Thickness(22, 17, 22, 13),
                Background = new SolidColorBrush(surface)
            };
            StackPanel titleStack = new() { Spacing = 3 };
            titleStack.Children.Add(new TextBlock
            {
                Text = "Aero Task Manager",
                FontSize = 21,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(text)
            });
            _summaryText.Text = "Reading WebView2 processes…";
            _summaryText.FontSize = 12;
            _summaryText.Foreground = new SolidColorBrush(muted);
            titleStack.Children.Add(_summaryText);
            top.Child = titleStack;
            root.Children.Add(top);

            Grid listHost = new() { Margin = new Thickness(18, 12, 18, 8) };
            listHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            listHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid header = BuildHeaderRow(muted);
            listHost.Children.Add(header);
            Grid.SetRow(_items, 1);
            listHost.Children.Add(_items);
            Grid.SetRow(listHost, 1);
            root.Children.Add(listHost);

            Grid bottom = new()
            {
                Padding = new Thickness(18, 10, 18, 16),
                Background = new SolidColorBrush(surface),
                ColumnSpacing = 12
            };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _statusText.Text = "Select a tab to manage it.";
            _statusText.VerticalAlignment = VerticalAlignment.Center;
            _statusText.FontSize = 12;
            _statusText.Foreground = new SolidColorBrush(muted);
            _statusText.TextTrimming = TextTrimming.CharacterEllipsis;
            bottom.Children.Add(_statusText);

            StackPanel actions = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };
            Grid.SetColumn(actions, 1);
            bottom.Children.Add(actions);
            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);

            return root;
        }

        private static Grid BuildHeaderRow(Color muted)
        {
            Grid header = CreateColumns();
            header.Padding = new Thickness(12, 0, 12, 7);
            AddCell(header, "Task", 0, muted, 11, FontWeights.SemiBold);
            AddCell(header, "Type / details", 1, muted, 11, FontWeights.SemiBold);
            AddCell(header, "CPU", 2, muted, 11, FontWeights.SemiBold, TextAlignment.Right);
            AddCell(header, "Memory", 3, muted, 11, FontWeights.SemiBold, TextAlignment.Right);
            AddCell(header, "PID", 4, muted, 11, FontWeights.SemiBold, TextAlignment.Right);
            AddCell(header, "Status", 5, muted, 11, FontWeights.SemiBold);
            return header;
        }

        private static Button BuildActionButton(string text, string glyph)
        {
            StackPanel content = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7
            };
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
            content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });

            return new Button
            {
                Height = 34,
                Padding = new Thickness(13, 0, 13, 0),
                Content = content,
                IsEnabled = false
            };
        }

        private async void RefreshTimer_Tick(object? sender, object e) => await RefreshAsync();

        private async Task RefreshAsync()
        {
            if (_refreshing)
            {
                return;
            }

            _refreshing = true;
            try
            {
                IReadOnlyList<TaskManagerEntry> entries =
                    await _owner.BuildTaskManagerSnapshotAsync();
                RenderEntries(entries);
            }
            catch
            {
                _statusText.Text = "Could not refresh process data. Aero will try again.";
            }
            finally
            {
                _refreshing = false;
                UpdateActionButtons();
            }
        }

        private void RenderEntries(IReadOnlyList<TaskManagerEntry> entries)
        {
            string? selectedKey = _selectedKey;
            ListViewItem? selectionToRestore = null;
            _suppressSelectionChanged = true;
            try
            {
                _items.Items.Clear();

                foreach (TaskManagerEntry entry in entries)
                {
                    ListViewItem item = new()
                    {
                        Tag = entry,
                        Content = BuildEntryRow(entry),
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        IsEnabled = entry.Kind is not TaskManagerEntryKind.Header and
                            not TaskManagerEntryKind.Message
                    };
                    _items.Items.Add(item);
                    if (entry.Key == selectedKey)
                    {
                        selectionToRestore = item;
                    }
                }

                if (selectionToRestore is not null)
                {
                    _items.SelectedItem = selectionToRestore;
                }
                else
                {
                    _selectedKey = null;
                    _statusText.Text = "Select a tab to manage it.";
                }
            }
            finally
            {
                _suppressSelectionChanged = false;
            }

            int tabCount = entries.Count(entry => entry.Kind == TaskManagerEntryKind.Tab);
            int processCount = entries.Count(entry => entry.Kind == TaskManagerEntryKind.Process);
            long totalMemory = entries
                .Where(entry => entry.Kind == TaskManagerEntryKind.Process)
                .Sum(entry => entry.MemoryBytes);
            _summaryText.Text =
                $"{tabCount} tab(s) • {processCount} WebView2 process(es) • {FormatMemory(totalMemory)} total working set";
            UpdateActionButtons();
        }

        private UIElement BuildEntryRow(TaskManagerEntry entry)
        {
            if (entry.Kind == TaskManagerEntryKind.Header)
            {
                return new TextBlock
                {
                    Text = entry.Name,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(4, 10, 4, 4),
                    Opacity = 0.72
                };
            }
            if (entry.Kind == TaskManagerEntryKind.Message)
            {
                return new TextBlock
                {
                    Text = entry.Name,
                    FontSize = 12,
                    Margin = new Thickness(12, 7, 12, 7),
                    Opacity = 0.62
                };
            }

            Grid row = CreateColumns();
            row.MinHeight = 48;
            row.Padding = new Thickness(12, 5, 12, 5);

            StackPanel nameStack = new() { Spacing = 1 };
            nameStack.Children.Add(new TextBlock
            {
                Text = entry.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            nameStack.Children.Add(new TextBlock
            {
                Text = entry.Kind.ToString(),
                FontSize = 10,
                Opacity = 0.58
            });
            row.Children.Add(nameStack);

            AddCell(row, entry.Details, 1, null, 11, FontWeights.Normal);
            AddCell(row, $"{entry.CpuPercent:0.0}%", 2, null, 12, FontWeights.Normal, TextAlignment.Right);
            AddCell(row, FormatMemory(entry.MemoryBytes), 3, null, 12, FontWeights.Normal, TextAlignment.Right);
            AddCell(
                row,
                entry.ProcessIds.Count == 0 ? "—" : string.Join(", ", entry.ProcessIds),
                4,
                null,
                11,
                FontWeights.Normal,
                TextAlignment.Right);
            AddCell(row, entry.Status, 5, null, 11, FontWeights.Normal);
            return row;
        }

        private static Grid CreateColumns()
        {
            Grid grid = new() { ColumnSpacing = 14 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.05, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.55, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(94) });
            return grid;
        }

        private static void AddCell(
            Grid grid,
            string text,
            int column,
            Color? foreground,
            double fontSize,
            Windows.UI.Text.FontWeight fontWeight,
            TextAlignment textAlignment = TextAlignment.Left)
        {
            TextBlock block = new()
            {
                Text = text,
                FontSize = fontSize,
                FontWeight = fontWeight,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = textAlignment,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (foreground is Color color)
            {
                block.Foreground = new SolidColorBrush(color);
            }
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }

        private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged)
            {
                return;
            }

            if (_items.SelectedItem is ListViewItem { Tag: TaskManagerEntry entry })
            {
                _selectedKey = entry.Key;
                _statusText.Text = entry.Kind == TaskManagerEntryKind.Tab
                    ? "End task keeps the address, Unload frees memory, Restart rebuilds WebView2 for this tab."
                    : "Only tab rows can be stopped, unloaded, or restarted.";
            }
            else
            {
                _selectedKey = null;
                _statusText.Text = "Select a tab to manage it.";
            }

            UpdateActionButtons();
        }

        private void UpdateActionButtons()
        {
            bool tabSelected = GetSelectedTab() is not null;
            _endTaskButton.IsEnabled = tabSelected && !_refreshing;
            _unloadButton.IsEnabled = tabSelected && !_refreshing;
            _restartButton.IsEnabled = tabSelected && !_refreshing;
        }

        private BrowserTabState? GetSelectedTab()
        {
            return _items.SelectedItem is ListViewItem
            {
                Tag: TaskManagerEntry
                {
                    Kind: TaskManagerEntryKind.Tab,
                    Target: BrowserTabState tab
                }
            }
                ? tab
                : null;
        }

        private async Task RunSelectedTabActionAsync(
            Func<BrowserTabState, Task<TaskManagerActionResult>> action)
        {
            BrowserTabState? tab = GetSelectedTab();
            if (tab is null || _refreshing)
            {
                return;
            }

            _refreshing = true;
            UpdateActionButtons();
            try
            {
                TaskManagerActionResult result = await action(tab);
                _statusText.Text = result.Message;
            }
            catch
            {
                _statusText.Text = "The tab action failed, but the Aero session is still running.";
            }
            finally
            {
                _refreshing = false;
                await RefreshAsync();
            }
        }

        private static string FormatMemory(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 MB";
            }

            double megabytes = bytes / 1024d / 1024d;
            return megabytes >= 1024
                ? $"{megabytes / 1024:0.00} GB"
                : $"{megabytes:0} MB";
        }
    }

    private sealed class MutableResourceTotal
    {
        public double CpuPercent { get; set; }
        public long MemoryBytes { get; set; }
        public HashSet<int> ProcessIds { get; } = [];
    }

    private enum TaskManagerEntryKind
    {
        Header,
        Message,
        Tab,
        Extension,
        Process
    }

    private sealed record TaskManagerEntry(
        string Key,
        TaskManagerEntryKind Kind,
        string Name,
        string Details,
        double CpuPercent,
        long MemoryBytes,
        IReadOnlyList<int> ProcessIds,
        string Status,
        object? Target)
    {
        public static TaskManagerEntry CreateHeader(string name) => new(
            $"header:{name}",
            TaskManagerEntryKind.Header,
            name,
            string.Empty,
            0,
            0,
            [],
            string.Empty,
            null);

        public static TaskManagerEntry CreateMessage(string message) => new(
            $"message:{message}",
            TaskManagerEntryKind.Message,
            message,
            string.Empty,
            0,
            0,
            [],
            string.Empty,
            null);
    }

    private readonly record struct TaskManagerCpuSample(
        TimeSpan TotalProcessorTime,
        DateTimeOffset Timestamp);

    private readonly record struct ProcessResourceSnapshot(
        long MemoryBytes,
        double CpuPercent);

    private readonly record struct TaskManagerActionResult(bool Success, string Message);

    private sealed record TaskManagerWebViewProcess(
        CoreWebView2ProcessInfo ProcessInfo,
        IReadOnlyList<CoreWebView2FrameInfo> AssociatedFrameInfos);
}
