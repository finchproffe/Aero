using Aero.App.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;

namespace Aero.App;

public sealed partial class MainWindow
{
    private const double SplitDividerWidth = 8;
    private const double MinimumSplitPaneWidth = 180;

    private readonly BrowserTabTransfer? _startupTransfer;
    private BrowserTabState? _splitLeftTab;
    private BrowserTabState? _splitRightTab;
    private BrowserTabState? _tabDragSplitTarget;
    private double _splitRatio = 0.5;
    private bool _splitDividerDragging;

    private bool IsSplitViewActive =>
        _splitLeftTab is not null &&
        _splitRightTab is not null &&
        !ReferenceEquals(_splitLeftTab, _splitRightTab) &&
        _tabs.Contains(_splitLeftTab) &&
        _tabs.Contains(_splitRightTab);

    private bool IsSplitParticipant(BrowserTabState state) =>
        IsSplitViewActive &&
        (ReferenceEquals(state, _splitLeftTab) ||
         ReferenceEquals(state, _splitRightTab));

    private void AttachBrowserPaneInput(BrowserTabState state, WebView2 browser)
    {
        browser.Tag = state;
        browser.GotFocus += SplitBrowser_GotFocus;
        browser.PointerPressed += SplitBrowser_PointerPressed;
        AttachBrowserContentFileDrop(browser);
    }

    private void DetachBrowserPaneInput(WebView2 browser)
    {
        browser.GotFocus -= SplitBrowser_GotFocus;
        browser.PointerPressed -= SplitBrowser_PointerPressed;
        DetachBrowserContentFileDrop(browser);
        browser.Tag = null;
    }

    private void SplitBrowser_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is WebView2 { Tag: BrowserTabState state } &&
            IsSplitParticipant(state) &&
            !ReferenceEquals(state, ActiveTab))
        {
            SelectTab(state);
        }
    }

    private void SplitBrowser_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is WebView2 { Tag: BrowserTabState state } &&
            IsSplitParticipant(state) &&
            !ReferenceEquals(state, ActiveTab))
        {
            SelectTab(state);
        }
    }

    private bool TryEnterSplitView(
        BrowserTabState existingTab,
        BrowserTabState addedTab,
        SplitDropSide addedSide,
        bool focusAddedTab = true)
    {
        if (!_tabs.Contains(existingTab) ||
            !_tabs.Contains(addedTab) ||
            ReferenceEquals(existingTab, addedTab))
        {
            return false;
        }

        if (addedSide == SplitDropSide.Left)
        {
            _splitLeftTab = addedTab;
            _splitRightTab = existingTab;
        }
        else
        {
            _splitLeftTab = existingTab;
            _splitRightTab = addedTab;
        }

        _splitRatio = 0.5;
        if (focusAddedTab)
        {
            _activeTab = addedTab;
            addedTab.LastActivatedAt = DateTimeOffset.UtcNow;
        }

        if (existingTab.IsDiscarded)
        {
            _ = RestoreDiscardedTabAsync(existingTab);
        }
        else
        {
            ResumeTabIfNeeded(existingTab);
        }
        if (addedTab.IsDiscarded)
        {
            _ = RestoreDiscardedTabAsync(addedTab);
        }
        else
        {
            ResumeTabIfNeeded(addedTab);
        }

        UpdateVisibleBrowser();
        UpdateActiveChrome();
        UpdateAllTabVisuals();
        SaveSessionSnapshot();
        return true;
    }

    private void ExitSplitView(BrowserTabState? preferredTab = null, bool saveSession = true)
    {
        if (!IsSplitViewActive && _splitLeftTab is null && _splitRightTab is null)
        {
            return;
        }

        BrowserTabState? fallback = preferredTab is not null && _tabs.Contains(preferredTab)
            ? preferredTab
            : ActiveTab is not null && _tabs.Contains(ActiveTab)
                ? ActiveTab
                : _splitLeftTab is not null && _tabs.Contains(_splitLeftTab)
                    ? _splitLeftTab
                    : _splitRightTab is not null && _tabs.Contains(_splitRightTab)
                        ? _splitRightTab
                        : _tabs.FirstOrDefault();

        _splitLeftTab = null;
        _splitRightTab = null;
        _splitRatio = 0.5;
        _splitDividerDragging = false;
        if (fallback is not null)
        {
            _activeTab = fallback;
        }

        UpdateVisibleBrowser();
        UpdateActiveChrome();
        UpdateAllTabVisuals();
        if (saveSession)
        {
            SaveSessionSnapshot();
        }
    }

    private void HandleSplitTabRemoval(BrowserTabState state)
    {
        if (!IsSplitParticipant(state))
        {
            return;
        }

        BrowserTabState? partner = ReferenceEquals(state, _splitLeftTab)
            ? _splitRightTab
            : _splitLeftTab;
        ExitSplitView(partner, saveSession: false);
    }

    private void ApplySplitViewLayout()
    {
        if (!IsSplitViewActive)
        {
            SplitLeftColumn.Width = new GridLength(1, GridUnitType.Star);
            SplitDividerColumn.Width = new GridLength(0);
            SplitRightColumn.Width = new GridLength(0);
            SplitDivider.Visibility = Visibility.Collapsed;
            SplitViewToolbar.Visibility = Visibility.Collapsed;
            SplitLeftActiveOutline.Visibility = Visibility.Collapsed;
            SplitRightActiveOutline.Visibility = Visibility.Collapsed;

            foreach (BrowserTabState tab in _tabs)
            {
                Grid.SetColumn(tab.Browser, 0);
                Grid.SetColumn(tab.SnapshotView, 0);
            }

            return;
        }

        _splitRatio = Math.Clamp(_splitRatio, 0.2, 0.8);
        SplitLeftColumn.Width = new GridLength(_splitRatio, GridUnitType.Star);
        SplitDividerColumn.Width = new GridLength(SplitDividerWidth);
        SplitRightColumn.Width = new GridLength(1 - _splitRatio, GridUnitType.Star);
        SplitDivider.Visibility = Visibility.Visible;
        SplitViewToolbar.Visibility = Visibility.Visible;

        foreach (BrowserTabState tab in _tabs)
        {
            int column = ReferenceEquals(tab, _splitRightTab) ? 2 : 0;
            Grid.SetColumn(tab.Browser, column);
            Grid.SetColumn(tab.SnapshotView, column);
        }

        SplitLeftActiveOutline.Visibility = ReferenceEquals(ActiveTab, _splitLeftTab)
            ? Visibility.Visible
            : Visibility.Collapsed;
        SplitRightActiveOutline.Visibility = ReferenceEquals(ActiveTab, _splitRightTab)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RestoreSplitViewFromSession(BrowserSessionData session)
    {
        if (session.SplitLeftTabIndex is not int leftIndex ||
            session.SplitRightTabIndex is not int rightIndex ||
            leftIndex == rightIndex ||
            leftIndex < 0 ||
            rightIndex < 0 ||
            leftIndex >= _tabs.Count ||
            rightIndex >= _tabs.Count)
        {
            return;
        }

        _splitLeftTab = _tabs[leftIndex];
        _splitRightTab = _tabs[rightIndex];
        _splitRatio = double.IsFinite(session.SplitRatio)
            ? Math.Clamp(session.SplitRatio, 0.2, 0.8)
            : 0.5;
        if (_splitLeftTab.IsDiscarded)
        {
            _ = RestoreDiscardedTabAsync(_splitLeftTab);
        }
        if (_splitRightTab.IsDiscarded)
        {
            _ = RestoreDiscardedTabAsync(_splitRightTab);
        }
        ApplySplitViewLayout();
    }

    private void SplitDivider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsSplitViewActive ||
            !e.GetCurrentPoint(SplitDivider).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _splitDividerDragging = true;
        SplitDivider.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void SplitDivider_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitDividerDragging || !IsSplitViewActive)
        {
            return;
        }

        var point = e.GetCurrentPoint(BrowserHost);
        if (!point.Properties.IsLeftButtonPressed)
        {
            FinishSplitDividerDrag(e);
            return;
        }

        double availableWidth = Math.Max(0, BrowserHost.ActualWidth - SplitDividerWidth);
        if (availableWidth <= 0)
        {
            return;
        }

        double minimum = Math.Min(MinimumSplitPaneWidth, availableWidth * 0.4);
        double leftWidth = Math.Clamp(
            point.Position.X - (SplitDividerWidth / 2),
            minimum,
            Math.Max(minimum, availableWidth - minimum));
        _splitRatio = Math.Clamp(leftWidth / availableWidth, 0.2, 0.8);
        ApplySplitViewLayout();
        e.Handled = true;
    }

    private void SplitDivider_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        FinishSplitDividerDrag(e);

    private void SplitDivider_PointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        FinishSplitDividerDrag(e, releaseCapture: false);

    private void FinishSplitDividerDrag(
        PointerRoutedEventArgs? e = null,
        bool releaseCapture = true)
    {
        if (!_splitDividerDragging)
        {
            return;
        }

        _splitDividerDragging = false;
        if (releaseCapture)
        {
            try
            {
                if (e is not null)
                {
                    SplitDivider.ReleasePointerCapture(e.Pointer);
                }
                else
                {
                    SplitDivider.ReleasePointerCaptures();
                }
            }
            catch
            {
                // Pointer capture can already be released by the window manager.
            }
        }

        SaveSessionSnapshot();
    }

    private void SwapSplitPanesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsSplitViewActive)
        {
            return;
        }

        (_splitLeftTab, _splitRightTab) = (_splitRightTab, _splitLeftTab);
        _splitRatio = 1 - _splitRatio;
        UpdateVisibleBrowser();
        SaveSessionSnapshot();
    }

    private void ExitSplitViewButton_Click(object sender, RoutedEventArgs e) =>
        ExitSplitView();

    private void PrepareTabDragDrop(BrowserTabState state)
    {
        _tabDragSplitTarget = ActiveTab is not null &&
                              !ReferenceEquals(ActiveTab, state)
            ? ActiveTab
            : IsSplitParticipant(state)
                ? ReferenceEquals(state, _splitLeftTab)
                    ? _splitRightTab
                    : _splitLeftTab
                : null;
    }

    private Point GetTabDragOverlayPoint(Point tabViewportPoint)
    {
        try
        {
            return TabScrollViewer
                .TransformToVisual(TabDragOverlay)
                .TransformPoint(tabViewportPoint);
        }
        catch
        {
            return tabViewportPoint;
        }
    }

    private bool TryGetRootPointFromScreen(
        NativeCursorPoint screenPoint,
        out Point rootPoint)
    {
        rootPoint = default;
        try
        {
            nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            NativeCursorPoint clientPoint = screenPoint;
            if (windowHandle == 0 || !ScreenToClient(windowHandle, ref clientPoint))
            {
                return false;
            }

            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
            if (!double.IsFinite(scale) || scale <= 0)
            {
                scale = 1;
            }

            rootPoint = new Point(clientPoint.X / scale, clientPoint.Y / scale);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetBrowserPointFromScreen(
        NativeCursorPoint screenPoint,
        out Point browserPoint)
    {
        browserPoint = default;
        if (!TryGetRootPointFromScreen(screenPoint, out Point rootPoint))
        {
            return false;
        }

        try
        {
            Point browserOrigin = BrowserHost
                .TransformToVisual(RootGrid)
                .TransformPoint(new Point(0, 0));
            browserPoint = new Point(
                rootPoint.X - browserOrigin.X,
                rootPoint.Y - browserOrigin.Y);
            return browserPoint.X >= 0 &&
                   browserPoint.Y >= 0 &&
                   browserPoint.X <= BrowserHost.ActualWidth &&
                   browserPoint.Y <= BrowserHost.ActualHeight;
        }
        catch
        {
            return false;
        }
    }

    private bool IsPointInsideWindow(NativeCursorPoint screenPoint)
    {
        return TryGetRootPointFromScreen(screenPoint, out Point rootPoint) &&
               rootPoint.X >= 0 &&
               rootPoint.Y >= 0 &&
               rootPoint.X <= RootGrid.ActualWidth &&
               rootPoint.Y <= RootGrid.ActualHeight;
    }

    private MainWindow? GetAeroWindowUnderCursor(NativeCursorPoint screenPoint)
    {
        foreach (MainWindow window in OpenWindows.AsEnumerable().Reverse())
        {
            if (window.IsPointInsideWindow(screenPoint))
            {
                return window;
            }
        }

        return null;
    }

    private bool CanAcceptTransferredTabFrom(MainWindow sourceWindow) =>
        _isPrivateWindow == sourceWindow._isPrivateWindow &&
        (!_isPrivateWindow ||
         string.Equals(
             _privateUserDataFolder,
             sourceWindow._privateUserDataFolder,
             StringComparison.OrdinalIgnoreCase));

    private void UpdateTabDragDropIndicator(BrowserTabState state)
    {
        foreach (MainWindow window in OpenWindows)
        {
            window.HideSplitDropIndicator();
        }

        if (!GetCursorPos(out NativeCursorPoint screenPoint))
        {
            return;
        }

        MainWindow? destination = GetAeroWindowUnderCursor(screenPoint);
        if (destination is null ||
            !destination.CanAcceptTransferredTabFrom(this) ||
            !destination.TryGetBrowserPointFromScreen(screenPoint, out Point browserPoint))
        {
            return;
        }

        BrowserTabState? target = ReferenceEquals(destination, this)
            ? _tabDragSplitTarget
            : destination.ActiveTab;
        if (target is null ||
            ReferenceEquals(target, state) ||
            !destination._tabs.Contains(target))
        {
            return;
        }

        destination.ShowSplitDropIndicator(browserPoint);
    }

    private void ShowSplitDropIndicator(Point browserPoint)
    {
        try
        {
            Point browserOrigin = BrowserHost
                .TransformToVisual(TabDragOverlay)
                .TransformPoint(new Point(0, 0));
            double halfWidth = Math.Max(0, BrowserHost.ActualWidth / 2);
            bool left = browserPoint.X < BrowserHost.ActualWidth / 2;
            SplitDropIndicator.Width = Math.Max(0, halfWidth - 14);
            SplitDropIndicator.Height = Math.Max(0, BrowserHost.ActualHeight - 24);
            Canvas.SetLeft(
                SplitDropIndicator,
                browserOrigin.X + (left ? 8 : halfWidth + 6));
            Canvas.SetTop(SplitDropIndicator, browserOrigin.Y + 12);
            SplitDropIndicator.Visibility = Visibility.Visible;
        }
        catch
        {
            SplitDropIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void HideSplitDropIndicator() =>
        SplitDropIndicator.Visibility = Visibility.Collapsed;

    private TabDragDropResolution ResolveTabDragDrop(BrowserTabState state)
    {
        foreach (MainWindow window in OpenWindows)
        {
            window.HideSplitDropIndicator();
        }

        if (!GetCursorPos(out NativeCursorPoint screenPoint))
        {
            return new TabDragDropResolution(
                FindTabGroup(state) is null
                    ? TabDragDropKind.Reorder
                    : TabDragDropKind.Cancel);
        }

        MainWindow? destination = GetAeroWindowUnderCursor(screenPoint);
        if (ReferenceEquals(destination, this))
        {
            if (TryGetBrowserPointFromScreen(screenPoint, out Point browserPoint) &&
                _tabDragSplitTarget is not null &&
                _tabs.Contains(_tabDragSplitTarget) &&
                !ReferenceEquals(_tabDragSplitTarget, state))
            {
                SplitDropSide side = browserPoint.X < BrowserHost.ActualWidth / 2
                    ? SplitDropSide.Left
                    : SplitDropSide.Right;
                return new TabDragDropResolution(
                    TabDragDropKind.SplitCurrentWindow,
                    this,
                    _tabDragSplitTarget,
                    side);
            }

            return new TabDragDropResolution(
                TryGetBrowserPointFromScreen(screenPoint, out _)
                    ? TabDragDropKind.Cancel
                    : FindTabGroup(state) is null &&
                      TryGetRootPointFromScreen(screenPoint, out Point rootPoint) &&
                      rootPoint.Y <= TitleBarSurface.ActualHeight
                        ? TabDragDropKind.Reorder
                        : TabDragDropKind.Cancel);
        }

        if (destination is not null && destination.CanAcceptTransferredTabFrom(this))
        {
            if (destination.TryGetBrowserPointFromScreen(screenPoint, out Point browserPoint) &&
                destination.ActiveTab is BrowserTabState target)
            {
                SplitDropSide side = browserPoint.X < destination.BrowserHost.ActualWidth / 2
                    ? SplitDropSide.Left
                    : SplitDropSide.Right;
                return new TabDragDropResolution(
                    TabDragDropKind.TransferToSplit,
                    destination,
                    target,
                    side);
            }

            return new TabDragDropResolution(
                TabDragDropKind.TransferToWindow,
                destination);
        }

        return new TabDragDropResolution(TabDragDropKind.DetachToNewWindow);
    }

    private void CompleteNonReorderTabDrop(
        BrowserTabState state,
        TabDragDropResolution resolution)
    {
        _tabDragSplitTarget = null;
        switch (resolution.Kind)
        {
            case TabDragDropKind.SplitCurrentWindow
                when resolution.TargetTab is not null:
                TryEnterSplitView(
                    resolution.TargetTab,
                    state,
                    resolution.Side,
                    focusAddedTab: true);
                break;

            case TabDragDropKind.TransferToWindow
                when resolution.TargetWindow is not null:
                _ = TransferTabToWindowAsync(
                    state,
                    resolution.TargetWindow,
                    splitTarget: null,
                    splitSide: resolution.Side);
                break;

            case TabDragDropKind.TransferToSplit
                when resolution.TargetWindow is not null &&
                     resolution.TargetTab is not null:
                _ = TransferTabToWindowAsync(
                    state,
                    resolution.TargetWindow,
                    resolution.TargetTab,
                    resolution.Side);
                break;

            case TabDragDropKind.DetachToNewWindow:
                _ = DetachTabToNewWindowAsync(state);
                break;

            case TabDragDropKind.Cancel:
                UpdateVisibleBrowser();
                UpdateAllTabVisuals();
                break;
        }
    }

    private BrowserTabTransfer CaptureTabTransfer(BrowserTabState state)
    {
        bool muted = state.Browser.CoreWebView2?.IsMuted == true;
        return new BrowserTabTransfer(
            state.CurrentUri ?? state.PendingUri,
            state.IsStartPage || state.PendingStartPage ||
                (state.CurrentUri is null && state.PendingUri is null),
            IsTabEffectivelyPinned(state),
            state.PageZoomFactor,
            muted,
            state.Title,
            IsDesktopAuthenticationContextActive(state)
                ? state.ExternalAuthenticationStartedAt
                : null);
    }

    private async Task<BrowserTabState> CreateTransferredTabAsync(
        BrowserTabTransfer transfer,
        bool selectTab)
    {
        BrowserTabState state = await CreateTabAsync(
            transfer.Uri,
            selectTab: false,
            showStartPage: transfer.IsStartPage,
            openedFromExternalApplication:
                transfer.ExternalAuthenticationStartedAt is not null);
        state.ExternalAuthenticationStartedAt =
            transfer.ExternalAuthenticationStartedAt;
        state.IsPinned = transfer.IsPinned;
        state.PageZoomFactor = transfer.PageZoomFactor;
        if (!transfer.IsStartPage && !string.IsNullOrWhiteSpace(transfer.Title))
        {
            state.Title = transfer.Title;
            state.TitleText.Text = TrimTabTitle(transfer.Title);
            ToolTipService.SetToolTip(state.Header, transfer.Title);
        }
        if (state.Browser.CoreWebView2 is CoreWebView2 core)
        {
            core.IsMuted = transfer.IsMuted;
            await ApplyPageZoomAsync(state);
        }

        NormalizePinnedTabOrder();
        RefreshTabStripOrder();
        UpdateTabWidths();
        UpdateAllTabVisuals();
        if (selectTab)
        {
            SelectTab(state);
        }
        else
        {
            SaveSessionSnapshot();
        }

        return state;
    }

    private async Task TransferTabToWindowAsync(
        BrowserTabState state,
        MainWindow destination,
        BrowserTabState? splitTarget,
        SplitDropSide splitSide)
    {
        if (!_tabs.Contains(state) ||
            ReferenceEquals(destination, this) ||
            !destination.CanAcceptTransferredTabFrom(this))
        {
            return;
        }

        BrowserTabTransfer transfer = CaptureTabTransfer(state);
        try
        {
            BrowserTabState received = await destination.CreateTransferredTabAsync(
                transfer,
                selectTab: splitTarget is null);
            if (splitTarget is not null && destination._tabs.Contains(splitTarget))
            {
                destination.TryEnterSplitView(
                    splitTarget,
                    received,
                    splitSide,
                    focusAddedTab: true);
            }

            RemoveTransferredTab(state);
            destination.Activate();
        }
        catch (Exception exception)
        {
            await ShowErrorDialogAsync("Could not move tab", exception.Message);
        }
    }

    private async Task DetachTabToNewWindowAsync(BrowserTabState state)
    {
        if (!_tabs.Contains(state))
        {
            return;
        }

        BrowserTabTransfer transfer = CaptureTabTransfer(state);
        MainWindow newWindow = new(
            _isPrivateWindow,
            _browserProfile,
            transfer,
            _isPrivateWindow ? _privateUserDataFolder : null);
        newWindow.Activate();

        try
        {
            BrowserTabState? received = await transfer.Completion.Task;
            if (received is not null && _tabs.Contains(state))
            {
                RemoveTransferredTab(state);
            }
        }
        catch (Exception exception)
        {
            await ShowErrorDialogAsync("Could not detach tab", exception.Message);
        }
    }

    private void RemoveTransferredTab(BrowserTabState state)
    {
        int index = _tabs.IndexOf(state);
        if (index < 0)
        {
            return;
        }

        HandleSplitTabRemoval(state);
        bool wasActive = ReferenceEquals(state, ActiveTab);
        state.IsClosing = true;

        TabStripPanel.Children.Remove(state.Header);
        _tabs.RemoveAt(index);
        RemoveEmptyTabGroups(state.TabGroupId);
        RefreshTabStripOrder();
        UpdateMediaHubAvailability();
        UpdateMediaHubUi();
        StopLoadingAnimation(state);
        ReleaseTabHeader(state);
        state.FaviconImage.Source = null;
        state.SnapshotView.Source = null;
        BrowserHost.Children.Remove(state.SnapshotView);
        DeleteTabSnapshot(state);
        ReleaseTabWebView(state, createReplacement: false);
        UpdateTabWidths();

        if (_tabs.Count == 0)
        {
            _activeTab = null;
            Close();
            return;
        }

        if (wasActive || ActiveTab is null || !_tabs.Contains(ActiveTab))
        {
            SelectTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }
        else
        {
            UpdateVisibleBrowser();
            UpdateAllTabVisuals();
            SaveSessionSnapshot();
        }
    }

    private async Task MoveTabToNewWindowFromContextAsync(BrowserTabState state)
    {
        if (_tabs.Contains(state))
        {
            await DetachTabToNewWindowAsync(state);
        }
    }

    private void OpenTabInSplitViewFromContext(BrowserTabState state)
    {
        BrowserTabState? target = ActiveTab;
        if (target is null ||
            ReferenceEquals(target, state) ||
            !_tabs.Contains(target))
        {
            target = _tabs
                .Where(tab => !ReferenceEquals(tab, state))
                .OrderByDescending(tab => tab.LastActivatedAt)
                .FirstOrDefault();
        }

        if (target is not null)
        {
            TryEnterSplitView(target, state, SplitDropSide.Right);
        }
    }

    private sealed class BrowserTabTransfer(
        Uri? uri,
        bool isStartPage,
        bool isPinned,
        double pageZoomFactor,
        bool isMuted,
        string title,
        DateTimeOffset? externalAuthenticationStartedAt = null)
    {
        public Uri? Uri { get; } = uri;

        public bool IsStartPage { get; } = isStartPage;

        public bool IsPinned { get; } = isPinned;

        public double PageZoomFactor { get; } = pageZoomFactor;

        public bool IsMuted { get; } = isMuted;

        public string Title { get; } = title;

        public DateTimeOffset? ExternalAuthenticationStartedAt { get; } =
            externalAuthenticationStartedAt;

        public TaskCompletionSource<BrowserTabState?> Completion { get; } = new();
    }

    private enum SplitDropSide
    {
        Left,
        Right
    }

    private enum TabDragDropKind
    {
        Reorder,
        Cancel,
        SplitCurrentWindow,
        TransferToWindow,
        TransferToSplit,
        DetachToNewWindow
    }

    private readonly record struct TabDragDropResolution(
        TabDragDropKind Kind,
        MainWindow? TargetWindow = null,
        BrowserTabState? TargetTab = null,
        SplitDropSide Side = SplitDropSide.Right);
}
