using Aero.Downloads;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace Aero.App;

public sealed partial class MainWindow
{
    private readonly List<BrowserDownloadState> _downloads = [];
    private readonly List<CommandPaletteEntry> _commandResults = [];
    private readonly List<Button> _commandResultButtons = [];
    private readonly DispatcherTimer _downloadToastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly SemaphoreSlim _downloadPickerGate = new(1, 1);
    private readonly Lazy<DownloadRequestCoordinator> _downloadRequestCoordinator =
        new(static () => new DownloadRequestCoordinator());

    private bool _downloadsPaneOpen;
    private bool _commandPaletteOpen;
    private int _selectedCommandIndex;

    private void InitializeUserReadyFeatures()
    {
        _downloadToastTimer.Tick += (_, _) => HideDownloadToast();
    }

    private async void Browser_DownloadStarting(
        BrowserTabState state,
        CoreWebView2DownloadStartingEventArgs args)
    {
        var deferral = args.GetDeferral();
        args.Handled = true;
        if (!TryClaimDownloadOperation(args.DownloadOperation))
        {
            // WebView2 can surface the same operation more than once while the
            // first handler still owns its deferral. The original handler remains
            // authoritative; do not cancel or start a second save flow.
            deferral.Complete();
            return;
        }

        MarkDownloadNavigation(state, args);
        string? claimedFingerprint = null;
        bool retainOperationClaim = false;
        Action releaseMemorySaverProtection =
            ProtectTabWhileDownloadActive(state, args.DownloadOperation);

        try
        {
            if (!TryClaimDownloadRequest(args, out claimedFingerprint))
            {
                args.Cancel = true;
                return;
            }

            if (await TryHandleExtensionStoreDownloadAsync(state, args))
            {
                retainOperationClaim = !args.Cancel;
                return;
            }

            if (_settings.WarnDangerousFiles &&
                IsPotentiallyDangerousDownload(args.ResultFilePath, args.DownloadOperation.Uri))
            {
                ContentDialog warning = new()
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = "This file may be dangerous",
                    Content = "Executable and script files can change your computer. Download only if you trust the source.",
                    PrimaryButtonText = "Download anyway",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close
                };
                if (await warning.ShowAsync() != ContentDialogResult.Primary)
                {
                    args.Cancel = true;
                    return;
                }
            }

            await _downloadPickerGate.WaitAsync();
            try
            {
                string? destinationPath;
                if (_settings.AskDownloadPathEveryTime)
                {
                    StorageFile? destination = await PickDownloadDestinationAsync(args);
                    destinationPath = destination?.Path;
                }
                else
                {
                    destinationPath = CreateAutomaticDownloadPath(args);
                }

                if (string.IsNullOrWhiteSpace(destinationPath))
                {
                    args.Cancel = true;
                    return;
                }

                args.ResultFilePath = destinationPath;
                RegisterDownload(args.DownloadOperation, destinationPath, state);
                retainOperationClaim = true;
            }
            finally
            {
                _downloadPickerGate.Release();
            }
        }
        catch
        {
            args.Cancel = true;
        }
        finally
        {
            if (args.Cancel)
            {
                releaseMemorySaverProtection();
            }

            if (!retainOperationClaim)
            {
                ReleaseDownloadOperationClaim(args.DownloadOperation);
            }

            if (args.Cancel)
            {
                DownloadRequests.ReleaseRequest(claimedFingerprint);
            }

            deferral.Complete();
        }
    }

    private DownloadRequestCoordinator DownloadRequests =>
        _downloadRequestCoordinator.Value;

    private bool TryClaimDownloadOperation(CoreWebView2DownloadOperation operation) =>
        DownloadRequests.TryClaimOperation(operation);

    private void ReleaseDownloadOperationClaim(CoreWebView2DownloadOperation operation) =>
        DownloadRequests.ReleaseOperationClaim(operation);

    private bool TryClaimDownloadRequest(
        CoreWebView2DownloadStartingEventArgs args,
        out string fingerprint) =>
        DownloadRequests.TryClaimRequest(
            args.DownloadOperation.Uri,
            args.ResultFilePath,
            out fingerprint);

    private static bool UriEqualsIgnoringFragment(Uri left, Uri right) =>
        Uri.Compare(
            left,
            right,
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase) == 0;

    private static void MarkDownloadNavigation(
        BrowserTabState state,
        CoreWebView2DownloadStartingEventArgs args)
    {
        state.DownloadNavigationSuppressionUntil = DateTimeOffset.UtcNow.AddSeconds(15);
        state.DownloadNavigationUri = Uri.TryCreate(
            args.DownloadOperation.Uri,
            UriKind.Absolute,
            out Uri? downloadUri)
                ? downloadUri
                : state.LastNavigationUri;

        Uri? visiblePage = state.Browser.Source;
        if (IsBrowsableUri(visiblePage) &&
            (state.DownloadNavigationUri is null ||
             !UriEqualsIgnoringFragment(visiblePage!, state.DownloadNavigationUri)))
        {
            state.DownloadReturnUri = visiblePage;
        }
        else if (IsBrowsableUri(state.CurrentUri) &&
                 (state.DownloadNavigationUri is null ||
                  !UriEqualsIgnoringFragment(state.CurrentUri!, state.DownloadNavigationUri)))
        {
            state.DownloadReturnUri = state.CurrentUri;
        }

        state.TransientRetryCount = 0;
    }

    private async Task<StorageFile?> PickDownloadDestinationAsync(
        CoreWebView2DownloadStartingEventArgs args)
    {
        string safeFileName = DownloadPathPolicy.ResolveSafeFileName(
            args.ResultFilePath,
            args.DownloadOperation.Uri);
        string suggestedName = Path.GetFileNameWithoutExtension(safeFileName);
        string extension = Path.GetExtension(safeFileName);

        if (string.IsNullOrWhiteSpace(suggestedName))
        {
            suggestedName = "download";
        }

        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 16)
        {
            extension = ".download";
        }

        FileSavePicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = suggestedName
        };
        picker.FileTypeChoices.Add(
            $"{extension.TrimStart('.').ToUpperInvariant()} file",
            new List<string> { extension });

        nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        return await picker.PickSaveFileAsync();
    }

    private string CreateAutomaticDownloadPath(CoreWebView2DownloadStartingEventArgs args) =>
        DownloadPathPolicy.CreateAutomaticPath(
            _settings.DefaultDownloadFolder,
            args.ResultFilePath,
            args.DownloadOperation.Uri);

    private static bool IsPotentiallyDangerousDownload(string? suggestedPath, string? sourceUri) =>
        DownloadPathPolicy.IsPotentiallyDangerous(suggestedPath, sourceUri);

    private void RegisterDownload(
        CoreWebView2DownloadOperation operation,
        string resultFilePath,
        BrowserTabState sourceTab)
    {
        if (!DownloadRequests.TryRegisterOperation(operation))
        {
            return;
        }

        BrowserDownloadState download = new(
            operation,
            resultFilePath,
            sourceTab.CurrentUri ?? sourceTab.PendingUri);
        _downloads.Insert(0, download);

        void BytesReceivedChanged(object? sender, object args) => QueueDownloadRefresh(download);
        void EstimatedEndTimeChanged(object? sender, object args) => QueueDownloadRefresh(download);
        void StateChanged(object? sender, object args) => QueueDownloadRefresh(download, force: true);

        operation.BytesReceivedChanged += BytesReceivedChanged;
        operation.EstimatedEndTimeChanged += EstimatedEndTimeChanged;
        operation.StateChanged += StateChanged;

        download.EventDetachers.Add(() => operation.BytesReceivedChanged -= BytesReceivedChanged);
        download.EventDetachers.Add(() => operation.EstimatedEndTimeChanged -= EstimatedEndTimeChanged);
        download.EventDetachers.Add(() => operation.StateChanged -= StateChanged);

        QueueDownloadRefresh(download, force: true);
        ShowDownloadToast(download, completed: false);
        if (_settings.OpenDownloadsPanelAutomatically)
        {
            OpenDownloadsPane();
        }
    }

    private void QueueDownloadRefresh(BrowserDownloadState download, bool force = false)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!force && now - download.LastUiRefresh < TimeSpan.FromMilliseconds(140))
        {
            return;
        }

        download.UpdateTransferRate(now);
        download.LastUiRefresh = now;
        RootGrid.DispatcherQueue.TryEnqueue(() =>
        {
            if (_downloadsPaneOpen)
            {
                RefreshDownloadsPane();
            }

            if (download.Operation.State == CoreWebView2DownloadState.Completed && !download.CompletionToastShown)
            {
                download.CompletionToastShown = true;
                ShowDownloadToast(download, completed: true);
            }

            if (download.Operation.State == CoreWebView2DownloadState.Completed ||
                (download.Operation.State == CoreWebView2DownloadState.Interrupted && !download.Operation.CanResume))
            {
                download.DetachEvents();
                DownloadRequests.ReleaseRegisteredOperation(download.Operation);
            }
        });
    }
    private void OpenDownloadsPane()
    {
        CloseLibraryPane();
        CloseSettingsPane();
        CloseCommandPalette();
        ClosePasswordsPane();
        CloseExtensionsPane();
        CloseWorkspacesPane();

        if (_downloadsPaneOpen)
        {
            RefreshDownloadsPane();
            return;
        }

        _downloadsPaneOpen = true;
        DownloadsPane.Visibility = Visibility.Visible;
        DownloadsPane.Opacity = 0;
        DownloadsPaneTransform.TranslateX = 26;
        RefreshDownloadsPane();
        AnimateOpacity(DownloadsPane, 0, 1, StandardAnimationDuration);
        AnimateDouble(DownloadsPaneTransform, "TranslateX", 26, 0, StandardAnimationDuration);
    }

    private void CloseDownloadsPane()
    {
        if (!_downloadsPaneOpen)
        {
            return;
        }

        _downloadsPaneOpen = false;
        AnimateOpacity(DownloadsPane, DownloadsPane.Opacity, 0, FastAnimationDuration, () =>
        {
            if (!_downloadsPaneOpen)
            {
                DownloadsPane.Visibility = Visibility.Collapsed;
            }
        });
        AnimateDouble(DownloadsPaneTransform, "TranslateX", DownloadsPaneTransform.TranslateX, 24, FastAnimationDuration);
    }

    private void RefreshDownloadsPane()
    {
        DownloadsItemsPanel.Children.Clear();

        int activeCount = _downloads.Count(item => item.Operation.State == CoreWebView2DownloadState.InProgress);
        DownloadsSummaryText.Text = _downloads.Count == 0
            ? "No downloads yet"
            : activeCount > 0
                ? $"{activeCount} active · {_downloads.Count} total"
                : $"{_downloads.Count} recent";

        foreach (BrowserDownloadState item in _downloads.Take(100))
        {
            DownloadsItemsPanel.Children.Add(BuildDownloadItem(item));
        }

        DownloadsEmptyState.Visibility = _downloads.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private Border BuildDownloadItem(BrowserDownloadState item)
    {
        Grid root = new() { RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid header = new() { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        StackPanel titleStack = new() { Spacing = 2 };
        titleStack.Children.Add(new TextBlock
        {
            Text = item.FileName,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = BuildDownloadStatus(item),
            FontSize = 11,
            Foreground = GetMutedTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = item.SourceHost,
            FontSize = 10,
            Foreground = GetMutedTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        header.Children.Add(titleStack);

        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(actions, 1);

        CoreWebView2DownloadState state = item.Operation.State;
        if (state == CoreWebView2DownloadState.InProgress || item.Operation.CanResume)
        {
            Button pauseResume = CreateSmallDownloadButton(
                state == CoreWebView2DownloadState.InProgress ? "Pause" : "Resume",
                item);
            pauseResume.Click += DownloadPauseResumeButton_Click;
            actions.Children.Add(pauseResume);
        }

        if (state == CoreWebView2DownloadState.Completed)
        {
            Button open = CreateSmallDownloadButton("Open", item);
            open.Click += DownloadOpenButton_Click;
            actions.Children.Add(open);
        }

        Button folder = CreateSmallDownloadButton("Folder", item);
        folder.Click += DownloadFolderButton_Click;
        actions.Children.Add(folder);

        if (state == CoreWebView2DownloadState.InProgress)
        {
            Button cancel = CreateSmallDownloadButton("Cancel", item);
            cancel.Click += DownloadCancelButton_Click;
            actions.Children.Add(cancel);
        }
        else
        {
            Button remove = CreateSmallDownloadButton("Remove", item);
            remove.Click += DownloadRemoveButton_Click;
            actions.Children.Add(remove);
        }

        header.Children.Add(actions);
        root.Children.Add(header);

        long received = Math.Max(0, item.Operation.BytesReceived);
        long? totalBytes = item.Operation.TotalBytesToReceive;
        bool hasKnownTotal = totalBytes is > 0;

        ProgressBar progress = new()
        {
            Height = 3,
            Minimum = 0,
            Maximum = hasKnownTotal ? totalBytes!.Value : 1,
            Value = hasKnownTotal ? Math.Min(received, totalBytes!.Value) : 0,
            IsIndeterminate = state == CoreWebView2DownloadState.InProgress && !hasKnownTotal,
            Visibility = state == CoreWebView2DownloadState.Completed
                ? Visibility.Collapsed
                : Visibility.Visible
        };
        Grid.SetRow(progress, 1);
        root.Children.Add(progress);

        TextBlock path = new()
        {
            Text = item.ResultFilePath,
            FontSize = 10,
            Foreground = GetMutedTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(path, 2);
        root.Children.Add(path);

        return new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            Background = new SolidColorBrush(IsDarkTheme()
                ? Color.FromArgb(255, 29, 35, 40)
                : Color.FromArgb(255, 247, 249, 250)),
            BorderBrush = GetSoftBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = root
        };
    }

    private static Button CreateSmallDownloadButton(string text, BrowserDownloadState item) => new()
    {
        Content = text,
        Tag = item,
        Height = 28,
        MinWidth = 0,
        Padding = new Thickness(9, 0, 9, 0),
        FontSize = 11
    };

    private static string BuildDownloadStatus(BrowserDownloadState item)
    {
        CoreWebView2DownloadOperation operation = item.Operation;
        return operation.State switch
        {
            CoreWebView2DownloadState.Completed => $"Completed · {FormatBytes(operation.BytesReceived)}",
            CoreWebView2DownloadState.Interrupted when operation.CanResume => $"Paused · {FormatBytes(operation.BytesReceived)}",
            CoreWebView2DownloadState.Interrupted => $"Interrupted · {operation.InterruptReason}",
            _ => BuildActiveDownloadStatus(item)
        };
    }

    private static string BuildActiveDownloadStatus(BrowserDownloadState item)
    {
        CoreWebView2DownloadOperation operation = item.Operation;
        List<string> parts = [];
        long? total = operation.TotalBytesToReceive;
        if (total is > 0)
        {
            double percent = Math.Clamp(operation.BytesReceived * 100d / total.Value, 0, 100);
            parts.Add($"{FormatBytes(operation.BytesReceived)} of {FormatBytes(total.Value)}");
            parts.Add($"{percent:0}%");
        }
        else
        {
            parts.Add($"Downloading · {FormatBytes(operation.BytesReceived)}");
        }

        if (item.SpeedBytesPerSecond > 1)
        {
            parts.Add($"{FormatBytes((long)item.SpeedBytesPerSecond)}/s");
        }

        string estimatedEndTimeText = operation.EstimatedEndTime;
        DateTimeOffset now = DateTimeOffset.Now;
        if (DateTimeOffset.TryParse(
                estimatedEndTimeText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset estimatedEndTime) &&
            estimatedEndTime > now)
        {
            TimeSpan remaining = estimatedEndTime - now;
            parts.Add(remaining.TotalHours >= 1
                ? $"{remaining.TotalHours:0.#} h left"
                : remaining.TotalMinutes >= 1
                    ? $"{remaining.TotalMinutes:0} min left"
                    : $"{Math.Max(1, remaining.TotalSeconds):0} sec left");
        }

        return string.Join(" · ", parts);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    private void ShowDownloadToast(BrowserDownloadState item, bool completed)
    {
        DownloadToastTitle.Text = completed ? "Download complete" : "Download started";
        DownloadToastSubtitle.Text = item.FileName;
        DownloadToastViewButton.Visibility = Visibility.Visible;
        DownloadToast.Visibility = Visibility.Visible;
        DownloadToast.Opacity = 0;
        DownloadToastTransform.TranslateY = 12;
        AnimateOpacity(DownloadToast, 0, 1, StandardAnimationDuration);
        AnimateDouble(DownloadToastTransform, "TranslateY", 12, 0, StandardAnimationDuration);
        _downloadToastTimer.Stop();
        _downloadToastTimer.Start();
    }

    private void HideDownloadToast()
    {
        _downloadToastTimer.Stop();
        if (DownloadToast.Visibility != Visibility.Visible)
        {
            return;
        }

        AnimateOpacity(DownloadToast, DownloadToast.Opacity, 0, FastAnimationDuration, () =>
        {
            DownloadToast.Visibility = Visibility.Collapsed;
        });
        AnimateDouble(DownloadToastTransform, "TranslateY", DownloadToastTransform.TranslateY, 10, FastAnimationDuration);
    }

    private void DownloadsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        OpenDownloadsPane();
    }

    private void DownloadsAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenDownloadsPane();
    }

    private void CloseDownloadsButton_Click(object sender, RoutedEventArgs e) => CloseDownloadsPane();

    private void DownloadToastButton_Click(object sender, RoutedEventArgs e)
    {
        HideDownloadToast();
        OpenDownloadsPane();
    }

    private void OpenDownloadsFolderButton_Click(object sender, RoutedEventArgs e) => OpenDownloadsFolder();

    private void DownloadPauseResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BrowserDownloadState item })
        {
            return;
        }

        try
        {
            if (item.Operation.State == CoreWebView2DownloadState.InProgress)
            {
                item.Operation.Pause();
            }
            else if (item.Operation.CanResume)
            {
                item.Operation.Resume();
            }
        }
        catch
        {
            // The operation may have changed state between rendering and clicking.
        }

        RefreshDownloadsPane();
    }

    private void DownloadCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BrowserDownloadState item })
        {
            try
            {
                item.Operation.Cancel();
            }
            catch
            {
                // Cancellation is best-effort.
            }

            RefreshDownloadsPane();
        }
    }

    private void DownloadRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BrowserDownloadState item })
        {
            return;
        }

        RemoveDownloadItem(item);
        RefreshDownloadsPane();
    }

    private void RemoveDownloadItem(BrowserDownloadState item)
    {
        item.DetachEvents();
        DownloadRequests.ReleaseRegisteredOperation(item.Operation);
        _downloads.Remove(item);
    }

    private void RemoveInactiveDownloads()
    {
        foreach (BrowserDownloadState item in _downloads
                     .Where(static download =>
                         download.Operation.State != CoreWebView2DownloadState.InProgress)
                     .ToArray())
        {
            RemoveDownloadItem(item);
        }
    }

    private async void DownloadOpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BrowserDownloadState item })
        {
            await LaunchDownloadedFileAsync(item);
        }
    }

    private async Task LaunchDownloadedFileAsync(BrowserDownloadState item)
    {
        string path = item.ResultFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            await ShowErrorDialogAsync(
                "Downloaded file was not found",
                "The file may have been moved, deleted, or removed by security software.");
            RefreshDownloadsPane();
            return;
        }

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            if (await Launcher.LaunchFileAsync(file))
            {
                return;
            }
        }
        catch
        {
            // Some unpackaged WinUI configurations cannot launch through StorageFile.
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }
        catch
        {
            // Fall through to a visible error and reveal the file in Explorer.
        }

        OpenContainingFolder(path);
        await ShowErrorDialogAsync(
            "Windows could not open this file",
            "Aero revealed the file in its folder. Choose an application in Windows to open this file type.");
    }

    private void DownloadFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BrowserDownloadState item })
        {
            OpenContainingFolder(item.ResultFilePath);
        }
    }

    private void OpenDownloadsFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.DefaultDownloadFolder);
            OpenPath(_settings.DefaultDownloadFolder);
        }
        catch
        {
            string fallback = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            OpenPath(fallback);
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch
        {
            // Shell launching should never interrupt the browser.
        }
    }

    private void OpenContainingFolder(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                OpenDownloadsFolder();
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = File.Exists(filePath) ? $"/select,\"{filePath}\"" : $"\"{System.IO.Path.GetDirectoryName(filePath)}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            OpenDownloadsFolder();
        }
    }

    private async void CommandPaletteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        await Task.Delay(90);
        OpenCommandPalette();
    }

    private void CommandPaletteAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenCommandPalette();
    }

    private void OpenCommandPalette()
    {
        CloseDownloadsPane();
        CloseLibraryPane();
        CloseSettingsPane();
        ClosePasswordsPane();
        CloseExtensionsPane();
        CloseWorkspacesPane();
        CloseFindBar();

        _commandPaletteOpen = true;
        CommandPaletteScrim.Visibility = Visibility.Visible;
        CommandPalette.Visibility = Visibility.Visible;
        CommandPaletteScrim.Opacity = 0;
        CommandPalette.Opacity = 0;
        CommandPaletteTransform.TranslateY = -10;
        CommandPaletteTransform.ScaleX = 0.985;
        CommandPaletteTransform.ScaleY = 0.985;
        CommandPaletteBox.Text = string.Empty;
        RefreshCommandPaletteResults();

        AnimateOpacity(CommandPaletteScrim, 0, 1, FastAnimationDuration);
        AnimateOpacity(CommandPalette, 0, 1, StandardAnimationDuration);
        AnimateDouble(CommandPaletteTransform, "TranslateY", -10, 0, StandardAnimationDuration);
        AnimateDouble(CommandPaletteTransform, "ScaleX", 0.985, 1, StandardAnimationDuration);
        AnimateDouble(CommandPaletteTransform, "ScaleY", 0.985, 1, StandardAnimationDuration);
        CommandPaletteBox.Focus(FocusState.Programmatic);
        RootGrid.DispatcherQueue.TryEnqueue(() =>
        {
            if (_commandPaletteOpen)
            {
                CommandPaletteBox.Focus(FocusState.Programmatic);
                CommandPaletteBox.SelectionStart = CommandPaletteBox.Text.Length;
            }
        });
    }

    private void CloseCommandPalette()
    {
        if (!_commandPaletteOpen)
        {
            return;
        }

        _commandPaletteOpen = false;
        AnimateOpacity(CommandPaletteScrim, CommandPaletteScrim.Opacity, 0, FastAnimationDuration);
        AnimateOpacity(CommandPalette, CommandPalette.Opacity, 0, FastAnimationDuration, () =>
        {
            if (!_commandPaletteOpen)
            {
                CommandPaletteScrim.Visibility = Visibility.Collapsed;
                CommandPalette.Visibility = Visibility.Collapsed;
            }
        });
        AnimateDouble(CommandPaletteTransform, "TranslateY", CommandPaletteTransform.TranslateY, -8, FastAnimationDuration);
    }

    private void CommandPaletteScrim_Tapped(object sender, TappedRoutedEventArgs e) => CloseCommandPalette();

    private void CommandPaletteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_commandPaletteOpen)
        {
            RefreshCommandPaletteResults();
        }
    }

    private void CommandPaletteBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseCommandPalette();
            return;
        }

        if (_commandResults.Count == 0)
        {
            return;
        }

        if (e.Key == VirtualKey.Down)
        {
            e.Handled = true;
            _selectedCommandIndex = (_selectedCommandIndex + 1) % _commandResults.Count;
            UpdateCommandSelection();
        }
        else if (e.Key == VirtualKey.Up)
        {
            e.Handled = true;
            _selectedCommandIndex = (_selectedCommandIndex - 1 + _commandResults.Count) % _commandResults.Count;
            UpdateCommandSelection();
        }
        else if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = ExecuteCommandEntryAsync(_commandResults[_selectedCommandIndex]);
        }
    }

    private void RefreshCommandPaletteResults()
    {
        string query = CommandPaletteBox.Text.Trim();
        _commandResults.Clear();
        _commandResultButtons.Clear();
        CommandPaletteResultsPanel.Children.Clear();

        AddCommandActions(query);
        AddCommandTabs(query);
        AddCommandBookmarks(query);
        AddCommandHistory(query);

        if (_commandResults.Count == 0)
        {
            CommandPaletteResultsPanel.Children.Add(new TextBlock
            {
                Text = "No matching tabs, pages, or commands",
                Margin = new Thickness(12, 18, 12, 18),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = GetMutedTextBrush()
            });
            return;
        }

        _selectedCommandIndex = Math.Clamp(_selectedCommandIndex, 0, _commandResults.Count - 1);

        for (int index = 0; index < _commandResults.Count; index++)
        {
            CommandPaletteEntry entry = _commandResults[index];
            Button button = BuildCommandResultButton(entry, index);
            _commandResultButtons.Add(button);
            CommandPaletteResultsPanel.Children.Add(button);
        }

        UpdateCommandSelection();
    }

    private void AddCommandActions(string query)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            string searchQuery = query;
            _commandResults.Add(new CommandPaletteEntry(
                $"Search the web for ‘{searchQuery}’",
                "Open the query with the selected search engine",
                "\uE721",
                () =>
                {
                    if (ActiveTab is BrowserTabState state)
                    {
                        Navigate(state, BuildSearchUri(searchQuery));
                    }
                    return Task.CompletedTask;
                }));
        }

        CommandPaletteEntry[] actions =
        [
            new("New tab", "Open a fresh Aero tab", "\uE710", async () =>
            {
                await CreateTabAsync(null, selectTab: true, showStartPage: true);
            }),
            new("New private window", "Open an isolated temporary browsing profile", "\uE72E", () =>
            {
                MainWindow privateWindow = new(
                    isPrivate: true,
                    browserProfile: _browserProfile,
                    startupTransfer: null,
                    sharedPrivateUserDataFolder: null);
                privateWindow.Activate();
                return Task.CompletedTask;
            }),
            new("Downloads", "View active and recent downloads", "\uE896", () =>
            {
                OpenDownloadsPane();
                return Task.CompletedTask;
            }),
            new("History", "Search pages visited on this computer", "\uE81C", () =>
            {
                OpenLibraryPane(LibraryView.History);
                return Task.CompletedTask;
            }),
            new("Bookmarks", "Open saved pages", "\uE734", () =>
            {
                OpenLibraryPane(LibraryView.Bookmarks);
                return Task.CompletedTask;
            }),
            new("Passwords and autofill", "Manage sign-in saving and local autofill data", "\uE8D7", () =>
            {
                OpenPasswordsPane();
                return Task.CompletedTask;
            }),
            new("Extensions", "Manage Chromium extensions", "\uEA86", () =>
            {
                OpenExtensionsPane();
                return Task.CompletedTask;
            }),
            new("Settings", "Performance, appearance, and startup", "\uE713", () =>
            {
                OpenSettingsPane();
                return Task.CompletedTask;
            }),
            new("Sleep other tabs", "Pause background tabs now", "\uE823", async () =>
            {
                await SuspendOtherTabsNowAsync();
            }),
            new("Freeze background tabs", "Suspend pages without closing or reloading them", "\uE823", async () =>
            {
                await RunMemorySaverCycleAsync(forceImmediate: true);
            }),
            new("Reload active tab", "Reload the current page", "\uE72C", () =>
            {
                ReloadOrStop();
                return Task.CompletedTask;
            }),
            new("Mute or unmute active tab", "Toggle audio for the current page", "\uE767", () =>
            {
                if (ActiveTab is BrowserTabState state)
                {
                    ToggleTabMuted(state);
                }
                return Task.CompletedTask;
            }),
            new("Copy page address", "Copy the current URL to the clipboard", "\uE8C8", () =>
            {
                if (ActiveTab is BrowserTabState state)
                {
                    CopyTabAddress(state);
                }
                return Task.CompletedTask;
            }),
            new("Restore closed tab", "Reopen the most recently closed page", "\uE7A7", async () =>
            {
                await RestoreClosedTabAsync();
            }),
            new("Aero Home", "Open the start page", "\uE80F", () =>
            {
                if (ActiveTab is BrowserTabState state)
                {
                    ShowAeroHomePage(state);
                    UpdateActiveChrome();
                }
                return Task.CompletedTask;
            })
        ];

        foreach (CommandPaletteEntry action in actions)
        {
            if (MatchesCommandQuery(action.Title, action.Subtitle, query))
            {
                _commandResults.Add(action);
            }
        }
    }

    private void AddCommandTabs(string query)
    {
        foreach (BrowserTabState tab in _tabs
            .Where(tab => MatchesCommandQuery(tab.Title, tab.CurrentUri?.AbsoluteUri ?? tab.PendingUri?.AbsoluteUri, query))
            .Take(query.Length == 0 ? 5 : 10))
        {
            string subtitle = tab.IsStartPage
                ? "Open tab · Aero Home"
                : $"Open tab · {tab.CurrentUri?.Host ?? tab.PendingUri?.Host ?? "loading"}";
            _commandResults.Add(new CommandPaletteEntry(
                tab.Title.Length == 0 ? "New tab" : tab.Title,
                subtitle,
                IsTabEffectivelyPinned(tab) ? "\uE718" : "\uE774",
                () =>
                {
                    SelectTab(tab);
                    return Task.CompletedTask;
                }));
        }
    }

    private void AddCommandBookmarks(string query)
    {
        if (query.Length == 0)
        {
            return;
        }

        foreach (Aero.App.Data.BookmarkEntry bookmark in _dataStore.Library.Bookmarks
            .Where(item => MatchesCommandQuery(item.Title, item.Url, query))
            .Take(6))
        {
            _commandResults.Add(new CommandPaletteEntry(
                bookmark.Title,
                $"Bookmark · {bookmark.Url}",
                "\uE734",
                async () =>
                {
                    if (Uri.TryCreate(bookmark.Url, UriKind.Absolute, out Uri? uri))
                    {
                        await CreateTabAsync(uri, selectTab: true);
                    }
                }));
        }
    }

    private void AddCommandHistory(string query)
    {
        if (query.Length < 2)
        {
            return;
        }

        foreach (Aero.App.Data.HistoryEntry history in _dataStore.Library.History
            .Where(item => MatchesCommandQuery(item.Title, item.Url, query))
            .Take(6))
        {
            _commandResults.Add(new CommandPaletteEntry(
                history.Title,
                $"History · {history.Url}",
                "\uE81C",
                async () =>
                {
                    if (Uri.TryCreate(history.Url, UriKind.Absolute, out Uri? uri))
                    {
                        await CreateTabAsync(uri, selectTab: true);
                    }
                }));
        }
    }

    private Button BuildCommandResultButton(CommandPaletteEntry entry, int index)
    {
        Grid content = new() { Width = 572, ColumnSpacing = 10 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        content.Children.Add(new FontIcon
        {
            Glyph = entry.Glyph,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetMutedTextBrush()
        });

        StackPanel text = new() { Spacing = 1 };
        text.Children.Add(new TextBlock
        {
            Text = entry.Title,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = entry.Subtitle,
            FontSize = 10,
            Foreground = GetMutedTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        Button button = new()
        {
            Height = 48,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 0, 10, 0),
            Content = content,
            Tag = index,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(9)
        };
        button.Click += CommandResultButton_Click;
        button.PointerEntered += (_, _) =>
        {
            _selectedCommandIndex = index;
            UpdateCommandSelection();
        };
        return button;
    }

    private async void CommandResultButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index } && index >= 0 && index < _commandResults.Count)
        {
            await ExecuteCommandEntryAsync(_commandResults[index]);
        }
    }

    private async Task ExecuteCommandEntryAsync(CommandPaletteEntry entry)
    {
        CloseCommandPalette();
        try
        {
            await entry.ExecuteAsync();
        }
        catch (Exception exception)
        {
            await ShowErrorDialogAsync("Command failed", exception.Message);
        }
    }

    private void UpdateCommandSelection()
    {
        for (int index = 0; index < _commandResultButtons.Count; index++)
        {
            bool selected = index == _selectedCommandIndex;
            _commandResultButtons[index].Background = selected
                ? GetLibrarySelectorBrush(true)
                : GetTransparentBrush();
        }
    }

    private static bool MatchesCommandQuery(string? title, string? subtitle, string query)
    {
        if (query.Length == 0)
        {
            return true;
        }

        return (title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (subtitle?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private async Task SuspendOtherTabsNowAsync()
    {
        foreach (BrowserTabState tab in _tabs.ToArray())
        {
            if (ReferenceEquals(tab, ActiveTab) ||
                tab.IsLoading ||
                tab.IsStartPage ||
                tab.IsDiscarded ||
                tab.SuspendInProgress ||
                (_settings.KeepAudioTabsAwake && tab.IsPlayingAudio))
            {
                continue;
            }

            await SuspendTabForMemoryAsync(tab);
        }

        UpdateMemorySaverStatusText();
    }

    private void Browser_ProcessFailed(BrowserTabState state, CoreWebView2ProcessFailedEventArgs args)
    {
        RootGrid.DispatcherQueue.TryEnqueue(() =>
        {
            CancelPendingVaultFillOperations(state);
            ResetPasswordCaptureSharedBuffer(state);
            state.HasCrashed = true;
            state.IsLoading = false;
            StopLoadingAnimation(state);
            state.Icon.Glyph = "\uE783";
            if (ReferenceEquals(state, ActiveTab))
            {
                UpdateActiveChrome();
                UpdateRecoveryBar();
            }
        });
    }

    private void UpdateRecoveryBar()
    {
        RecoveryBar.Visibility = ActiveTab?.HasCrashed == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ReloadCrashedTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is not BrowserTabState state)
        {
            return;
        }

        state.HasCrashed = false;
        UpdateRecoveryBar();

        try
        {
            if (state.CurrentUri is Uri uri)
            {
                Navigate(state, uri);
            }
            else
            {
                state.Browser.Reload();
            }
        }
        catch
        {
            ShowAeroHomePage(state);
        }
    }

    private void DismissRecoveryBarButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is BrowserTabState state)
        {
            state.HasCrashed = false;
        }
        UpdateRecoveryBar();
    }

    private void TogglePinnedTab(BrowserTabState state)
    {
        int oldIndex = _tabs.IndexOf(state);
        if (oldIndex < 0)
        {
            return;
        }

        if (!state.IsPinned)
        {
            DetachTabFromGroupBeforePinning(state);
        }

        state.IsPinned = !state.IsPinned;
        NormalizePinnedTabOrder();
        RefreshTabStripOrder();
        UpdateTabWidths();
        UpdateAllTabVisuals();
        SaveSessionSnapshot();
    }

    private sealed class BrowserDownloadState
    {
        private readonly DownloadTransferRateEstimator _transferRate;
        private bool _eventsDetached;

        public BrowserDownloadState(
            CoreWebView2DownloadOperation operation,
            string resultFilePath,
            Uri? sourceUri)
        {
            Operation = operation;
            ResultFilePath = resultFilePath;
            SourceHost = sourceUri?.Host ??
                (Uri.TryCreate(operation.Uri, UriKind.Absolute, out Uri? operationUri)
                    ? operationUri.Host
                    : "Unknown source");
            FileName = Path.GetFileName(resultFilePath);
            if (string.IsNullOrWhiteSpace(FileName))
            {
                FileName = Uri.TryCreate(operation.Uri, UriKind.Absolute, out Uri? uri)
                    ? Path.GetFileName(uri.LocalPath)
                    : "Download";
            }

            _transferRate = new DownloadTransferRateEstimator(operation.BytesReceived);
        }

        public CoreWebView2DownloadOperation Operation { get; }

        public string ResultFilePath { get; }

        public string FileName { get; }

        public string SourceHost { get; }

        public DateTimeOffset LastUiRefresh { get; set; }

        public bool CompletionToastShown { get; set; }

        public double SpeedBytesPerSecond => _transferRate.BytesPerSecond;

        public List<Action> EventDetachers { get; } = [];

        public void UpdateTransferRate(DateTimeOffset now) =>
            _transferRate.Update(Operation.BytesReceived, now);

        public void DetachEvents()
        {
            if (_eventsDetached)
            {
                return;
            }

            _eventsDetached = true;
            foreach (Action detach in EventDetachers.ToArray())
            {
                try
                {
                    detach();
                }
                catch
                {
                    // Download operations can finish while the manager is closing.
                }
            }

            EventDetachers.Clear();
        }
    }

    private sealed record CommandPaletteEntry(
        string Title,
        string Subtitle,
        string Glyph,
        Func<Task> ExecuteAsync);
}
