using Aero.App.Data;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Foundation;

namespace Aero.App;

public sealed partial class MainWindow
{
    private const double TabDragThreshold = 4;
    private const int TabReorderAnimationDuration = 82;
    private const double TabReorderOverlapThreshold = 4;
    private const double TabReorderReverseHysteresis = 12;
    private const int TabDropAnimationDuration = 145;
    private const double TabAutoScrollEdgeSize = 52;
    private const double TabAutoScrollMaximumSpeed = 1200;
    private const double TabAutoScrollMinimumSpeed = 150;

    private BrowserTabState? _tabDragCandidate;
    private Border? _tabDragPlaceholder;
    private bool _tabDragActive;
    private double _tabDragStartX;
    private double _tabDragStartY;
    private double _tabDragPointerOffsetX;
    private double _tabDragPointerOffsetY;
    private double _tabDragLastViewportX;
    private double _tabDragLastViewportY;
    private double _tabDragLockedTop;
    private double _tabDragAutoScrollSpeed;
    private double _tabDragLastReorderLeftX = double.NaN;
    private double _tabDragLastSlotChangeLeftX = double.NaN;
    private int _tabDragLastReorderDirection;
    private Thickness _tabDragOriginalMargin;
    private bool _workspacesPaneOpen;

    // During a drag the real StackPanel order remains untouched. Other tabs are
    // translated on the compositor and the model/layout is committed only once,
    // when the pointer is released. This avoids repeated Measure/Arrange passes.
    private int _tabDragOriginalIndex = -1;
    private int _tabDragTargetIndex = -1;
    private int _tabDragSectionStartIndex;
    private readonly List<BrowserTabState> _tabDragSectionOrder = [];
    private readonly Dictionary<BrowserTabState, double> _tabDragSlotX = [];
    private readonly Dictionary<BrowserTabState, double> _tabDragNeighborTargets = [];

    // The dragged tab is positioned directly on the compositor once per rendered
    // frame. Pointer events only update the latest desired coordinates, so movement
    // stays refresh-rate synchronized without causing XAML Measure/Arrange work.
    private bool _tabDragUsesCompositionTranslation;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativeCursorPoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint windowHandle, ref NativeCursorPoint point);

    private void TabScrollViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (HandleTabGroupPointerMoved(e))
        {
            return;
        }

        BrowserTabState? state = _tabDragCandidate;
        if (state is null || !_tabs.Contains(state))
        {
            return;
        }

        var currentPoint = e.GetCurrentPoint(TabScrollViewer);
        Point viewportPoint = currentPoint.Position;
        _tabDragLastViewportX = viewportPoint.X;
        _tabDragLastViewportY = viewportPoint.Y;

        if (!currentPoint.Properties.IsLeftButtonPressed)
        {
            FinishTabDrag(e);
            return;
        }

        if (!_tabDragActive)
        {
            double deltaX = viewportPoint.X - _tabDragStartX;
            double deltaY = viewportPoint.Y - _tabDragStartY;
            if ((deltaX * deltaX) + (deltaY * deltaY) <
                TabDragThreshold * TabDragThreshold)
            {
                return;
            }

            StartTabDrag(state, viewportPoint);
        }

        RequestTabDragFrame();
        e.Handled = true;
    }

    private void TabScrollViewer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (FinishTabGroupDrag(e))
        {
            return;
        }

        FinishTabDrag(e);
    }

    private void TabScrollViewer_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (FinishTabGroupDrag(e, releaseCapture: false, toggleIfClick: false))
        {
            return;
        }

        FinishTabDrag(e, releaseCapture: false);
    }

    private void BeginTabDragCandidate(Border header, BrowserTabState state, PointerRoutedEventArgs e)
    {
        PrepareTabDragDrop(state);
        Point viewportPoint = e.GetCurrentPoint(TabScrollViewer).Position;
        Point headerPoint = e.GetCurrentPoint(header).Position;

        _tabDragCandidate = state;
        _tabDragActive = false;
        _tabDragStartX = viewportPoint.X;
        _tabDragStartY = viewportPoint.Y;
        _tabDragPointerOffsetX = headerPoint.X;
        _tabDragPointerOffsetY = headerPoint.Y;
        _tabDragLastViewportX = viewportPoint.X;
        _tabDragLastViewportY = viewportPoint.Y;
        _tabDragAutoScrollSpeed = 0;
        _tabDragLastReorderLeftX = double.NaN;
        _tabDragLastSlotChangeLeftX = double.NaN;
        _tabDragLastReorderDirection = 0;

        TabScrollViewer.CapturePointer(e.Pointer);
    }

    private void StartTabDrag(BrowserTabState state, Point viewportPoint)
    {
        int childIndex = TabStripPanel.Children.IndexOf(state.Header);
        if (childIndex < 0)
        {
            return;
        }

        CaptureTabDragSlotGeometry(state);
        if (_tabDragSlotX.TryGetValue(state, out double originalSlotX))
        {
            _tabDragLastReorderLeftX = originalSlotX;
            _tabDragLastSlotChangeLeftX = originalSlotX;
        }

        _tabDragActive = true;
        _tabDragOriginalMargin = state.Header.Margin;

        double headerHeight = state.Header.ActualHeight > 0
            ? state.Header.ActualHeight
            : state.Header.Height;
        try
        {
            Point headerOrigin = state.Header
                .TransformToVisual(TabDragOverlay)
                .TransformPoint(new Point(0, 0));
            _tabDragLockedTop = headerOrigin.Y;
        }
        catch
        {
            _tabDragLockedTop = 0;
        }

        _tabDragPlaceholder = new Border
        {
            Width = state.Header.ActualWidth > 0 ? state.Header.ActualWidth : state.Header.Width,
            Height = state.Header.ActualHeight > 0 ? state.Header.ActualHeight : state.Header.Height,
            Margin = state.Header.Margin,
            Background = GetTransparentBrush(),
            BorderBrush = GetTransparentBrush(),
            BorderThickness = new Thickness(0),
            IsHitTestVisible = false,
            Opacity = 0
        };

        TabStripPanel.Children.RemoveAt(childIndex);
        TabStripPanel.Children.Insert(childIndex, _tabDragPlaceholder);

        state.Header.Margin = new Thickness(0);
        state.HeaderTransform.TranslateX = 0;
        state.HeaderTransform.TranslateY = 0;
        state.HeaderTransform.ScaleX = 1;
        state.HeaderTransform.ScaleY = 1;

        TabDragOverlay.Width = Math.Max(RootGrid.ActualWidth, TabScrollViewer.ActualWidth);
        TabDragOverlay.Height = Math.Max(RootGrid.ActualHeight, headerHeight);
        Canvas.SetLeft(state.Header, 0);
        Canvas.SetTop(state.Header, 0);
        TabDragOverlay.Children.Add(state.Header);
        Canvas.SetZIndex(state.Header, 100);

        _tabDragUsesCompositionTranslation = InitializeDraggedTabComposition(state);
        PositionDraggedTabUnderPointer(state, viewportPoint);

        RequestTabDragFrame();
        AnimateOpacity(state.Header, state.Header.Opacity, 0.97, 70);
        AnimateDouble(state.HeaderTransform, "ScaleX", 1, 1.025, 85);
        AnimateDouble(state.HeaderTransform, "ScaleY", 1, 1.025, 85);

        UpdateAllTabVisuals();
    }

    private void CaptureTabDragSlotGeometry(BrowserTabState state)
    {
        _tabDragSectionOrder.Clear();
        _tabDragSlotX.Clear();
        _tabDragNeighborTargets.Clear();

        _tabDragOriginalIndex = _tabs.IndexOf(state);
        _tabDragSectionStartIndex = 0;

        foreach (BrowserTabState tab in _tabs.Where(tab =>
            IsTabEffectivelyPinned(tab) == IsTabEffectivelyPinned(state) &&
            CanDragTabIndependently(tab)))
        {
            _tabDragSectionOrder.Add(tab);
            try
            {
                Point origin = tab.Header
                    .TransformToVisual(TabStripPanel)
                    .TransformPoint(new Point(0, 0));
                _tabDragSlotX[tab] = origin.X;
            }
            catch
            {
                _tabDragSlotX[tab] = 0;
            }
        }

        _tabDragTargetIndex = Math.Max(0, _tabDragSectionOrder.IndexOf(state));
    }

    private bool InitializeDraggedTabComposition(BrowserTabState state)
    {
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(state.Header, true);
            Microsoft.UI.Composition.Visual visual =
                ElementCompositionPreview.GetElementVisual(state.Header);
            visual.StopAnimation("Translation");
            visual.StopAnimation("Translation.X");
            visual.StopAnimation("Translation.Y");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void StopDraggedTabComposition(BrowserTabState? state)
    {
        if (state is not null)
        {
            try
            {
                Microsoft.UI.Composition.Visual visual =
                    ElementCompositionPreview.GetElementVisual(state.Header);
                visual.StopAnimation("Translation");
                visual.StopAnimation("Translation.X");
                visual.StopAnimation("Translation.Y");
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
            }
            catch
            {
                // The header can already be detached during window shutdown.
            }
        }

        _tabDragUsesCompositionTranslation = false;
    }

    private void FinishTabDrag(
        PointerRoutedEventArgs? e = null,
        bool releaseCapture = true,
        bool animateDrop = true)
    {
        BrowserTabState? state = _tabDragCandidate;
        bool wasDragging = _tabDragActive;
        Border? placeholder = _tabDragPlaceholder;
        int finalIndex = _tabDragTargetIndex;
        TabDragDropResolution dropResolution = new(TabDragDropKind.Reorder);

        if (wasDragging && state is not null)
        {
            if (e is not null)
            {
                Point releasePoint = e.GetCurrentPoint(TabScrollViewer).Position;
                _tabDragLastViewportX = releasePoint.X;
                _tabDragLastViewportY = releasePoint.Y;
            }

            ProcessTabDragFrame(0, allowAutoScroll: false);
            dropResolution = animateDrop || e is not null
                ? ResolveTabDragDrop(state)
                : new TabDragDropResolution(TabDragDropKind.Cancel);
        }

        StopHighRefreshTabDragLoop();
        StopDraggedTabComposition(state);
        if (state is not null)
        {
            state.IsPointerPressed = false;
        }

        _tabDragCandidate = null;
        _tabDragActive = false;
        _tabDragPlaceholder = null;
        _tabDragAutoScrollSpeed = 0;
        _tabDragLastReorderLeftX = double.NaN;
        _tabDragLastSlotChangeLeftX = double.NaN;
        _tabDragLastReorderDirection = 0;
        StopRenderFrameSubscriptionIfIdle();

        if (releaseCapture)
        {
            try
            {
                if (e is not null)
                {
                    TabScrollViewer.ReleasePointerCapture(e.Pointer);
                }
                else
                {
                    TabScrollViewer.ReleasePointerCaptures();
                }
            }
            catch
            {
                // Pointer capture can already be released by the window manager.
            }
        }

        if (state is null || !_tabs.Contains(state))
        {
            if (placeholder is not null)
            {
                TabStripPanel.Children.Remove(placeholder);
            }

            ResetTabDragNeighborTranslations();
            ClearTabDragGeometry();
            _tabDragSplitTarget = null;
            return;
        }

        if (!wasDragging || placeholder is null)
        {
            ClearTabDragGeometry();
            _tabDragSplitTarget = null;
            SelectTab(state);
            return;
        }

        Point currentOverlayPoint = GetTabDragOverlayPoint(
            new Point(_tabDragLastViewportX, _tabDragLastViewportY));
        double currentLeft = currentOverlayPoint.X - _tabDragPointerOffsetX;
        double currentTop = currentOverlayPoint.Y - _tabDragPointerOffsetY;

        TabDragOverlay.Children.Remove(state.Header);
        TabStripPanel.Children.Remove(placeholder);

        if (dropResolution.Kind != TabDragDropKind.Reorder)
        {
            state.Header.Margin = _tabDragOriginalMargin;
            state.HeaderTransform.TranslateX = 0;
            state.HeaderTransform.TranslateY = 0;
            state.HeaderTransform.ScaleX = 1;
            state.HeaderTransform.ScaleY = 1;
            state.Header.Opacity = 1;
            Canvas.SetZIndex(state.Header, 0);
            RefreshTabStripOrder();
            UpdateTabWidths();
            ResetTabDragNeighborTranslations();
            ClearTabDragGeometry();
            CompleteNonReorderTabDrop(state, dropResolution);
            return;
        }

        int currentIndex = _tabs.IndexOf(state);
        if (currentIndex >= 0)
        {
            _tabs.RemoveAt(currentIndex);
        }

        int modelInsertionIndex = GetStandaloneTabInsertionIndex(
            state.IsPinned,
            finalIndex);
        _tabs.Insert(modelInsertionIndex, state);

        state.Header.Margin = _tabDragOriginalMargin;
        state.HeaderTransform.TranslateX = 0;
        state.HeaderTransform.TranslateY = 0;
        RefreshTabStripOrder();
        UpdateTabWidths();
        TabStripPanel.UpdateLayout();

        // The XAML layout now matches the compositor-only preview, so translations
        // can be removed in the same UI tick without a visible jump.
        ResetTabDragNeighborTranslations();
        ClearTabDragGeometry();

        Point layoutOrigin;
        try
        {
            layoutOrigin = state.Header
                .TransformToVisual(TabScrollViewer)
                .TransformPoint(new Point(0, 0));
        }
        catch
        {
            layoutOrigin = new Point(currentLeft, currentTop);
        }

        double targetTranslateY = ReferenceEquals(state, ActiveTab)
            ? 0
            : state.IsPointerOver ? 0.35 : 1;

        state.HeaderTransform.TranslateX = currentLeft - layoutOrigin.X;
        state.HeaderTransform.TranslateY = currentTop - layoutOrigin.Y;

        if (!animateDrop)
        {
            state.HeaderTransform.TranslateX = 0;
            state.HeaderTransform.TranslateY = targetTranslateY;
            state.HeaderTransform.ScaleX = 1;
            state.HeaderTransform.ScaleY = 1;
            state.Header.Opacity = 1;
            Canvas.SetZIndex(state.Header, 0);
            _tabDragSplitTarget = null;
            SelectTab(state);
            UpdateAllTabVisuals();
            SaveSessionSnapshot();
            return;
        }

        AnimateDouble(
            state.HeaderTransform,
            "TranslateX",
            state.HeaderTransform.TranslateX,
            0,
            TabDropAnimationDuration,
            () =>
            {
                state.HeaderTransform.TranslateX = 0;
                Canvas.SetZIndex(state.Header, 0);
                _tabDragSplitTarget = null;
                UpdateAllTabVisuals();
            });
        AnimateDouble(
            state.HeaderTransform,
            "TranslateY",
            state.HeaderTransform.TranslateY,
            targetTranslateY,
            TabDropAnimationDuration);
        AnimateDouble(
            state.HeaderTransform,
            "ScaleX",
            state.HeaderTransform.ScaleX,
            1,
            TabDropAnimationDuration);
        AnimateDouble(
            state.HeaderTransform,
            "ScaleY",
            state.HeaderTransform.ScaleY,
            1,
            TabDropAnimationDuration);
        AnimateOpacity(
            state.Header,
            state.Header.Opacity,
            1,
            TabDropAnimationDuration);

        SelectTab(state);
        SaveSessionSnapshot();
    }

    private int GetStandaloneTabInsertionIndex(bool isPinned, int localInsertionIndex)
    {
        List<BrowserTabState> candidates = _tabs.Where(tab =>
            IsTabEffectivelyPinned(tab) == isPinned &&
            CanDragTabIndependently(tab)).ToList();
        int target = Math.Clamp(localInsertionIndex, 0, candidates.Count);

        if (target < candidates.Count)
        {
            return _tabs.IndexOf(candidates[target]);
        }

        if (candidates.Count > 0)
        {
            return _tabs.IndexOf(candidates[^1]) + 1;
        }

        return isPinned
            ? 0
            : _tabs.Count(IsTabEffectivelyPinned);
    }

    private void ReorderDraggedTab(BrowserTabState state, double draggedLeftX)
    {
        if (!_tabDragActive || _tabDragPlaceholder is null || _tabDragSectionOrder.Count == 0)
        {
            return;
        }

        double previousLeftX = _tabDragLastReorderLeftX;
        _tabDragLastReorderLeftX = draggedLeftX;

        if (!double.IsFinite(previousLeftX))
        {
            return;
        }

        double movement = draggedLeftX - previousLeftX;
        if (Math.Abs(movement) < 0.05)
        {
            return;
        }

        int movementDirection = movement > 0 ? 1 : -1;
        if (_tabDragLastReorderDirection != 0 &&
            movementDirection != _tabDragLastReorderDirection &&
            double.IsFinite(_tabDragLastSlotChangeLeftX) &&
            Math.Abs(draggedLeftX - _tabDragLastSlotChangeLeftX) <
                GetTabReorderReverseHysteresis(state))
        {
            return;
        }

        // The target index is the insertion position among all tabs in this
        // pinned/unpinned section except the dragged tab itself. Moving right
        // checks the tab immediately to the right; moving left checks the tab
        // immediately to the left. A neighbour starts moving after only a few
        // DIPs of real overlap, so the strip reacts as soon as the user reaches it.
        List<BrowserTabState> neighbours = _tabDragSectionOrder
            .Where(tab => !ReferenceEquals(tab, state))
            .ToList();

        int targetLocalIndex = Math.Clamp(
            _tabDragTargetIndex - _tabDragSectionStartIndex,
            0,
            neighbours.Count);

        double draggedWidth = GetTabDragWidth(state);
        double draggedRightX = draggedLeftX + draggedWidth;
        int newTargetLocalIndex = targetLocalIndex;

        if (movement > 0)
        {
            while (newTargetLocalIndex < neighbours.Count)
            {
                BrowserTabState neighbour = neighbours[newTargetLocalIndex];
                double neighbourLeftX = GetTabDragVisualLeft(neighbour);
                double overlapThreshold = GetTabReorderOverlapThreshold(
                    draggedWidth,
                    GetTabDragWidth(neighbour));

                if (draggedRightX < neighbourLeftX + overlapThreshold)
                {
                    break;
                }

                newTargetLocalIndex++;
            }
        }
        else
        {
            while (newTargetLocalIndex > 0)
            {
                BrowserTabState neighbour = neighbours[newTargetLocalIndex - 1];
                double neighbourRightX =
                    GetTabDragVisualLeft(neighbour) + GetTabDragWidth(neighbour);
                double overlapThreshold = GetTabReorderOverlapThreshold(
                    draggedWidth,
                    GetTabDragWidth(neighbour));

                if (draggedLeftX > neighbourRightX - overlapThreshold)
                {
                    break;
                }

                newTargetLocalIndex--;
            }
        }

        if (newTargetLocalIndex == targetLocalIndex)
        {
            return;
        }

        _tabDragTargetIndex = _tabDragSectionStartIndex + newTargetLocalIndex;
        _tabDragLastReorderDirection = movementDirection;
        _tabDragLastSlotChangeLeftX = draggedLeftX;
        AnimateTabDragGap(state, newTargetLocalIndex);
    }

    private double GetTabDragVisualLeft(BrowserTabState state)
    {
        if (!_tabDragSlotX.TryGetValue(state, out double leftX))
        {
            return 0;
        }

        if (_tabDragNeighborTargets.TryGetValue(state, out double translation))
        {
            leftX += translation;
        }

        return leftX;
    }

    private static double GetTabDragWidth(BrowserTabState state)
    {
        double width = state.Header.ActualWidth;
        if (!double.IsFinite(width) || width <= 0)
        {
            width = state.Header.Width;
        }

        return double.IsFinite(width) && width > 0 ? width : 40;
    }

    private static double GetTabReorderOverlapThreshold(
        double draggedWidth,
        double neighbourWidth)
    {
        double compactThreshold = Math.Min(draggedWidth, neighbourWidth) * 0.04;
        return Math.Clamp(compactThreshold, 2, TabReorderOverlapThreshold);
    }

    private static double GetTabReorderReverseHysteresis(BrowserTabState state)
    {
        double responsiveDistance = GetTabDragWidth(state) * 0.06;
        return Math.Clamp(responsiveDistance, 6, TabReorderReverseHysteresis);
    }

    private void AnimateTabDragGap(BrowserTabState draggedState, int targetLocalIndex)
    {
        int originalLocalIndex = _tabDragSectionOrder.IndexOf(draggedState);
        if (originalLocalIndex < 0)
        {
            return;
        }

        for (int oldLocalIndex = 0; oldLocalIndex < _tabDragSectionOrder.Count; oldLocalIndex++)
        {
            BrowserTabState tab = _tabDragSectionOrder[oldLocalIndex];
            if (ReferenceEquals(tab, draggedState))
            {
                continue;
            }

            int newLocalIndex = oldLocalIndex;
            if (targetLocalIndex > originalLocalIndex &&
                oldLocalIndex > originalLocalIndex &&
                oldLocalIndex <= targetLocalIndex)
            {
                newLocalIndex = oldLocalIndex - 1;
            }
            else if (targetLocalIndex < originalLocalIndex &&
                     oldLocalIndex >= targetLocalIndex &&
                     oldLocalIndex < originalLocalIndex)
            {
                newLocalIndex = oldLocalIndex + 1;
            }

            double translation = 0;
            BrowserTabState targetSlot = _tabDragSectionOrder[newLocalIndex];
            if (_tabDragSlotX.TryGetValue(tab, out double oldX) &&
                _tabDragSlotX.TryGetValue(targetSlot, out double newX))
            {
                translation = newX - oldX;
            }

            AnimateTabCompositionTranslation(tab, translation);
        }
    }

    private void AnimateTabCompositionTranslation(BrowserTabState tab, double target)
    {
        bool alreadyTracked = _tabDragNeighborTargets.TryGetValue(tab, out double previousTarget);
        if (alreadyTracked && Math.Abs(previousTarget - target) < 0.25)
        {
            return;
        }

        _tabDragNeighborTargets[tab] = target;

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(tab.Header, true);
            Microsoft.UI.Composition.Visual visual =
                ElementCompositionPreview.GetElementVisual(tab.Header);

            if (!alreadyTracked)
            {
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
            }

            ScalarKeyFrameAnimation animation = visual.Compositor.CreateScalarKeyFrameAnimation();
            animation.InsertExpressionKeyFrame(0, "this.StartingValue");
            animation.InsertKeyFrame(
                1,
                (float)target,
                visual.Compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.2f, 0),
                    new Vector2(0, 1)));
            animation.Duration = TimeSpan.FromMilliseconds(
                GetFrameAlignedAnimationDuration(TabReorderAnimationDuration));

            visual.StartAnimation("Translation.X", animation);
        }
        catch
        {
            // Composition should be available on supported Windows versions, but
            // retain a harmless XAML fallback for unusual graphics configurations.
            AnimateDouble(
                tab.HeaderTransform,
                "TranslateX",
                tab.HeaderTransform.TranslateX,
                target,
                TabReorderAnimationDuration);
        }
    }

    private void ResetTabDragNeighborTranslations()
    {
        foreach (BrowserTabState tab in _tabDragNeighborTargets.Keys.ToArray())
        {
            try
            {
                Microsoft.UI.Composition.Visual visual =
                    ElementCompositionPreview.GetElementVisual(tab.Header);
                visual.StopAnimation("Translation.X");
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
            }
            catch
            {
                tab.HeaderTransform.TranslateX = 0;
            }
        }

        _tabDragNeighborTargets.Clear();
    }

    private void ClearTabDragGeometry()
    {
        _tabDragOriginalIndex = -1;
        _tabDragTargetIndex = -1;
        _tabDragSectionStartIndex = 0;
        _tabDragLockedTop = 0;
        _tabDragSectionOrder.Clear();
        _tabDragSlotX.Clear();
    }

    private bool TrySampleSystemCursorForTabDrag()
    {
        if (!_tabDragActive)
        {
            return false;
        }

        try
        {
            nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (windowHandle == 0 ||
                !GetCursorPos(out NativeCursorPoint cursorPoint) ||
                !ScreenToClient(windowHandle, ref cursorPoint))
            {
                return false;
            }

            double rasterizationScale = TabScrollViewer.XamlRoot?.RasterizationScale ?? 1;
            if (!double.IsFinite(rasterizationScale) || rasterizationScale <= 0)
            {
                rasterizationScale = 1;
            }

            Point scrollOrigin = TabScrollViewer
                .TransformToVisual(null)
                .TransformPoint(new Point(0, 0));

            _tabDragLastViewportX =
                (cursorPoint.X / rasterizationScale) - scrollOrigin.X;
            _tabDragLastViewportY =
                (cursorPoint.Y / rasterizationScale) - scrollOrigin.Y;
            return true;
        }
        catch
        {
            // Pointer events remain the fallback if Win32 cursor sampling is unavailable.
            return false;
        }
    }

    private void PositionDraggedTabUnderPointer(BrowserTabState state, Point viewportPoint)
    {
        if (!_tabDragActive ||
            !_tabs.Contains(state) ||
            TabDragOverlay.Children.IndexOf(state.Header) < 0)
        {
            return;
        }

        Point overlayPoint = GetTabDragOverlayPoint(viewportPoint);
        double left = overlayPoint.X - _tabDragPointerOffsetX;
        double top = overlayPoint.Y - _tabDragPointerOffsetY;

        if (_tabDragUsesCompositionTranslation)
        {
            try
            {
                Microsoft.UI.Composition.Visual visual =
                    ElementCompositionPreview.GetElementVisual(state.Header);
                visual.Properties.InsertVector3(
                    "Translation",
                    new Vector3((float)left, (float)top, 0));
                return;
            }
            catch
            {
                _tabDragUsesCompositionTranslation = false;
            }
        }

        // Compatibility fallback. This is used only if composition translation is
        // unavailable; normal drag never changes Canvas coordinates per frame.
        Canvas.SetLeft(state.Header, left);
        Canvas.SetTop(state.Header, top);
    }

    private void UpdateTabDragAutoScroll(double viewportX)
    {
        double width = TabScrollViewer.ActualWidth;
        if (!_tabDragActive || width <= 0 || TabScrollViewer.ScrollableWidth <= 0)
        {
            _tabDragAutoScrollSpeed = 0;
            return;
        }

        if (viewportX < TabAutoScrollEdgeSize)
        {
            double strength = Math.Clamp(
                (TabAutoScrollEdgeSize - viewportX) / TabAutoScrollEdgeSize,
                0,
                1);
            _tabDragAutoScrollSpeed = -Math.Max(
                TabAutoScrollMinimumSpeed,
                TabAutoScrollMaximumSpeed * strength * strength);
        }
        else if (viewportX > width - TabAutoScrollEdgeSize)
        {
            double strength = Math.Clamp(
                (viewportX - (width - TabAutoScrollEdgeSize)) / TabAutoScrollEdgeSize,
                0,
                1);
            _tabDragAutoScrollSpeed = Math.Max(
                TabAutoScrollMinimumSpeed,
                TabAutoScrollMaximumSpeed * strength * strength);
        }
        else
        {
            _tabDragAutoScrollSpeed = 0;
        }
    }

    private void ProcessTabDragFrame(double elapsedSeconds, bool allowAutoScroll)
    {
        BrowserTabState? state = _tabDragCandidate;
        if (!_tabDragActive || state is null || !_tabs.Contains(state))
        {
            return;
        }

        // PointerMoved can be coalesced by Windows. Sample the system cursor once
        // per actual render frame so a 120/144/165 Hz display is not reduced to
        // the delivery rate of routed pointer events.
        TrySampleSystemCursorForTabDrag();
        Point viewportPoint = new(_tabDragLastViewportX, _tabDragLastViewportY);

        // Move the grabbed tab first on every real display frame. Slot selection
        // below uses the same coordinates, so neighbouring tabs can never react
        // while the grabbed tab appears stationary.
        PositionDraggedTabUnderPointer(state, viewportPoint);
        UpdateTabDragDropIndicator(state);
        UpdateTabDragAutoScroll(viewportPoint.X);

        double effectiveOffset = TabScrollViewer.HorizontalOffset;
        if (allowAutoScroll && Math.Abs(_tabDragAutoScrollSpeed) >= 0.1)
        {
            double refreshRate = Math.Clamp(
                _estimatedDisplayRefreshRate,
                MinimumTrackedRefreshRate,
                MaximumTrackedRefreshRate);
            double safeElapsedSeconds = Math.Clamp(
                elapsedSeconds,
                1.0 / MaximumTrackedRefreshRate,
                Math.Max(1.0 / MinimumTrackedRefreshRate, 2.0 / refreshRate));
            double oldOffset = TabScrollViewer.HorizontalOffset;
            double newOffset = Math.Clamp(
                oldOffset + (_tabDragAutoScrollSpeed * safeElapsedSeconds),
                0,
                TabScrollViewer.ScrollableWidth);

            if (Math.Abs(newOffset - oldOffset) >= 0.1)
            {
                TabScrollViewer.ChangeView(newOffset, null, null, disableAnimation: true);
                effectiveOffset = newOffset;
            }
        }

        ReorderDraggedTab(
            state,
            viewportPoint.X +
            effectiveOffset -
            _tabDragPointerOffsetX);
    }

    private void RememberClosedTab(ClosedTabInfo closedTab)
    {
        _closedTabs.Push(closedTab);
        if (_closedTabs.Count <= 30)
        {
            return;
        }

        ClosedTabInfo[] newest = _closedTabs.Take(30).Reverse().ToArray();
        _closedTabs.Clear();
        foreach (ClosedTabInfo item in newest)
        {
            _closedTabs.Push(item);
        }
    }

    private void ShowPinnedTabProtection(BrowserTabState state)
    {
        if (ShowPinnedGroupProtectionForTab(state))
        {
            return;
        }

        SelectTab(state);
        ToolTipService.SetToolTip(
            state.Header,
            "Pinned tab — unpin it before closing");

        AnimateDouble(state.HeaderTransform, "ScaleX", 1, 1.04, 80, () =>
            AnimateDouble(state.HeaderTransform, "ScaleX", 1.04, 1, 100));
        AnimateDouble(state.HeaderTransform, "ScaleY", 1, 1.04, 80, () =>
            AnimateDouble(state.HeaderTransform, "ScaleY", 1.04, 1, 100));
    }

    private void WorkspacesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        OpenWorkspacesPane();
    }

    private void OpenWorkspacesPane()
    {
        CloseDownloadsPane();
        CloseLibraryPane();
        CloseSettingsPane();
        CloseCommandPalette();
        ClosePasswordsPane();
        CloseExtensionsPane();
        CloseFindBar();

        RefreshWorkspacesPane();
        if (_workspacesPaneOpen)
        {
            return;
        }

        _workspacesPaneOpen = true;
        WorkspacesPane.Visibility = Visibility.Visible;
        WorkspacesPane.Opacity = 0;
        WorkspacesPaneTransform.TranslateX = 28;
        AnimateOpacity(WorkspacesPane, 0, 1, StandardAnimationDuration);
        AnimateDouble(WorkspacesPaneTransform, "TranslateX", 28, 0, StandardAnimationDuration);
    }

    private void CloseWorkspacesPane()
    {
        if (!_workspacesPaneOpen)
        {
            return;
        }

        _workspacesPaneOpen = false;
        AnimateOpacity(WorkspacesPane, WorkspacesPane.Opacity, 0, FastAnimationDuration, () =>
        {
            if (!_workspacesPaneOpen)
            {
                WorkspacesPane.Visibility = Visibility.Collapsed;
            }
        });
        AnimateDouble(
            WorkspacesPaneTransform,
            "TranslateX",
            WorkspacesPaneTransform.TranslateX,
            24,
            FastAnimationDuration);
    }

    private void CloseWorkspacesButton_Click(object sender, RoutedEventArgs e) =>
        CloseWorkspacesPane();

    private void SaveWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        List<WorkspaceTabEntry> entries = _tabs
            .Where(tab => !tab.IsClosing)
            .Select(tab => new WorkspaceTabEntry
            {
                Url = (tab.CurrentUri ?? tab.PendingUri)?.AbsoluteUri,
                IsStartPage = tab.IsStartPage || tab.PendingStartPage,
                IsPinned = IsTabEffectivelyPinned(tab)
            })
            .ToList();

        if (entries.Count == 0)
        {
            WorkspacesStatusText.Text = "There are no tabs to save.";
            return;
        }

        WorkspaceEntry workspace = _dataStore.SaveWorkspace(
            WorkspaceNameBox.Text,
            entries);
        WorkspaceNameBox.Text = string.Empty;
        WorkspacesStatusText.Text = $"Saved {workspace.Tabs.Count} tabs as “{workspace.Name}”.";
        RefreshWorkspacesPane();
    }

    private void RefreshWorkspacesPane()
    {
        WorkspacesListPanel.Children.Clear();
        IReadOnlyList<WorkspaceEntry> workspaces = _dataStore.Library.Workspaces;
        WorkspacesEmptyState.Visibility = workspaces.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        foreach (WorkspaceEntry workspace in workspaces)
        {
            WorkspacesListPanel.Children.Add(BuildWorkspaceCard(workspace));
        }

        if (workspaces.Count == 0)
        {
            WorkspacesStatusText.Text = _isPrivateWindow
                ? "Private workspaces last only until this window closes."
                : "Save the current tab set to reopen it later.";
        }
    }

    private Border BuildWorkspaceCard(WorkspaceEntry workspace)
    {
        Grid root = new() { ColumnSpacing = 10 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        StackPanel text = new() { Spacing = 3 };
        text.Children.Add(new TextBlock
        {
            Text = workspace.Name,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = $"{workspace.Tabs.Count} tabs · {workspace.CreatedAt.ToLocalTime():g}",
            FontSize = 10,
            Foreground = GetMutedTextBrush()
        });

        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        Button open = new()
        {
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            Content = "Open",
            Tag = workspace
        };
        open.Click += OpenWorkspaceButton_Click;

        Button remove = new()
        {
            Width = 30,
            Height = 30,
            Style = (Style)Application.Current.Resources["ChromeIconButtonStyle"],
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 },
            Tag = workspace
        };
        remove.Click += DeleteWorkspaceButton_Click;
        ToolTipService.SetToolTip(remove, "Delete workspace");

        actions.Children.Add(open);
        actions.Children.Add(remove);
        Grid.SetColumn(actions, 1);
        root.Children.Add(text);
        root.Children.Add(actions);

        return new Border
        {
            Padding = new Thickness(12),
            Background = (Brush)Application.Current.Resources["AeroRaisedSurfaceBrush"],
            CornerRadius = new CornerRadius(10),
            Child = root
        };
    }

    private async void OpenWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WorkspaceEntry workspace })
        {
            return;
        }

        List<BrowserTabState> opened = [];
        int position = 0;

        foreach (WorkspaceTabEntry entry in workspace.Tabs)
        {
            Uri? uri = Uri.TryCreate(entry.Url, UriKind.Absolute, out Uri? parsed)
                ? parsed
                : null;
            bool startPage = entry.IsStartPage || uri is null;
            BrowserTabState tab = await CreateTabAsync(
                uri,
                selectTab: false,
                showStartPage: startPage,
                deferWebViewInitialization: position >= 5);

            if (entry.IsPinned)
            {
                TogglePinnedTab(tab);
            }

            opened.Add(tab);
            position++;
        }

        if (opened.Count > 0)
        {
            SelectTab(opened[0]);
            WorkspacesStatusText.Text = $"Opened “{workspace.Name}”.";
        }
    }

    private void DeleteWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WorkspaceEntry workspace })
        {
            return;
        }

        _dataStore.RemoveWorkspace(workspace.Id);
        WorkspacesStatusText.Text = $"Deleted “{workspace.Name}”.";
        RefreshWorkspacesPane();
    }
}
