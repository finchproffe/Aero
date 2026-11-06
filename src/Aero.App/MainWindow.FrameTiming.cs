using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Aero.App;

public sealed partial class MainWindow
{
    private const double DefaultDisplayRefreshRate = 60;
    private const double MinimumTrackedRefreshRate = 24;
    private const double MaximumTrackedRefreshRate = 500;
    private const int InitialFrameCalibrationSamples = 18;
    private const int VerticalRefreshRateCapability = 116;

    private bool _renderFrameSubscribed;
    private bool _frameTimingDisposed;
    private int _frameCalibrationSamplesRemaining;
    private long _lastRenderFrameTimestamp;
    private double _estimatedDisplayRefreshRate = DefaultDisplayRefreshRate;

    private System.Threading.Timer? _tabDragHighRefreshTimer;
    private volatile bool _tabDragHighRefreshTimerRunning;
    private bool _highResolutionTimerEnabled;
    private long _lastTabDragTimerTimestamp;
    private int _tabDragFrameEnqueued;

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint windowHandle, nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(nint deviceContext, int index);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint periodMilliseconds);

    private void StartFrameRateCalibration(int samples = InitialFrameCalibrationSamples)
    {
        if (_frameTimingDisposed)
        {
            return;
        }

        // Prefer the monitor's nominal refresh rate. CompositionTarget.Rendering
        // can be throttled by WinUI even on a 240 Hz display, so it must not be the
        // source of truth for drag timing or frame-aligned animations.
        double nativeRefreshRate = GetWindowDisplayRefreshRate();
        if (nativeRefreshRate >= MinimumTrackedRefreshRate)
        {
            _estimatedDisplayRefreshRate = nativeRefreshRate;
            _frameCalibrationSamplesRemaining = 0;
            return;
        }

        _frameCalibrationSamplesRemaining = Math.Max(
            _frameCalibrationSamplesRemaining,
            Math.Max(1, samples));
        EnsureRenderFrameSubscription();
    }

    private void RequestTabDragFrame()
    {
        if (_frameTimingDisposed)
        {
            return;
        }

        if (_tabDragActive && EnsureHighRefreshTabDragLoop())
        {
            return;
        }

        // Compatibility fallback if the DispatcherQueue timer cannot be created.
        EnsureRenderFrameSubscription();
    }

    private bool EnsureHighRefreshTabDragLoop()
    {
        if (_frameTimingDisposed)
        {
            return false;
        }

        if (_tabDragHighRefreshTimerRunning)
        {
            return true;
        }

        try
        {
            DispatcherQueue? queue = DispatcherQueue.GetForCurrentThread();
            if (queue is null)
            {
                return false;
            }

            double refreshRate = Math.Clamp(
                GetWindowDisplayRefreshRate(),
                MinimumTrackedRefreshRate,
                MaximumTrackedRefreshRate);
            _estimatedDisplayRefreshRate = refreshRate;

            // A 240 Hz frame lasts 4.167 ms. Feeding the UI/compositor every
            // 4 ms ensures there is a fresh cursor sample for every 240 Hz VSync.
            int intervalMilliseconds = Math.Max(
                1,
                (int)Math.Floor(1000.0 / refreshRate));

            Debug.WriteLine(
                $"Aero tab drag: {refreshRate:0.#} Hz display, " +
                $"{intervalMilliseconds} ms input frame interval.");

            if (!_highResolutionTimerEnabled && timeBeginPeriod(1) == 0)
            {
                _highResolutionTimerEnabled = true;
            }

            _lastTabDragTimerTimestamp = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _tabDragFrameEnqueued, 0);
            _tabDragHighRefreshTimerRunning = true;

            _tabDragHighRefreshTimer?.Dispose();
            _tabDragHighRefreshTimer = new System.Threading.Timer(
                TabDragHighRefreshTimer_Tick,
                queue,
                dueTime: 0,
                period: intervalMilliseconds);
            return true;
        }
        catch
        {
            StopHighRefreshTabDragLoop();
            return false;
        }
    }

    private void TabDragHighRefreshTimer_Tick(object? state)
    {
        if (!_tabDragHighRefreshTimerRunning || state is not DispatcherQueue queue)
        {
            return;
        }

        // Never build a queue of stale cursor positions. At most one high-priority
        // UI update may be pending; the next timer pulse uses the newest position.
        if (Interlocked.Exchange(ref _tabDragFrameEnqueued, 1) != 0)
        {
            return;
        }

        bool enqueued = queue.TryEnqueue(
            DispatcherQueuePriority.High,
            () =>
            {
                Interlocked.Exchange(ref _tabDragFrameEnqueued, 0);

                if (_frameTimingDisposed ||
                    !_tabDragActive ||
                    !_tabDragHighRefreshTimerRunning)
                {
                    return;
                }

                long now = Stopwatch.GetTimestamp();
                double fallbackSeconds = 1.0 / Math.Clamp(
                    _estimatedDisplayRefreshRate,
                    MinimumTrackedRefreshRate,
                    MaximumTrackedRefreshRate);
                double elapsedSeconds = fallbackSeconds;

                if (_lastTabDragTimerTimestamp != 0)
                {
                    elapsedSeconds =
                        (now - _lastTabDragTimerTimestamp) /
                        (double)Stopwatch.Frequency;
                }

                _lastTabDragTimerTimestamp = now;
                ProcessTabDragFrame(elapsedSeconds, allowAutoScroll: true);
            });

        if (!enqueued)
        {
            Interlocked.Exchange(ref _tabDragFrameEnqueued, 0);
        }
    }

    private void StopHighRefreshTabDragLoop()
    {
        _tabDragHighRefreshTimerRunning = false;
        Interlocked.Exchange(ref _tabDragFrameEnqueued, 0);

        System.Threading.Timer? timer = Interlocked.Exchange(
            ref _tabDragHighRefreshTimer,
            null);
        timer?.Dispose();

        _lastTabDragTimerTimestamp = 0;

        if (_highResolutionTimerEnabled)
        {
            timeEndPeriod(1);
            _highResolutionTimerEnabled = false;
        }
    }

    private double GetWindowDisplayRefreshRate()
    {
        nint windowHandle = 0;
        nint deviceContext = 0;

        try
        {
            windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (windowHandle == 0)
            {
                return Math.Clamp(
                    _estimatedDisplayRefreshRate,
                    MinimumTrackedRefreshRate,
                    MaximumTrackedRefreshRate);
            }

            deviceContext = GetDC(windowHandle);
            if (deviceContext == 0)
            {
                return Math.Clamp(
                    _estimatedDisplayRefreshRate,
                    MinimumTrackedRefreshRate,
                    MaximumTrackedRefreshRate);
            }

            int refreshRate = GetDeviceCaps(deviceContext, VerticalRefreshRateCapability);
            if (refreshRate >= MinimumTrackedRefreshRate &&
                refreshRate <= MaximumTrackedRefreshRate)
            {
                return refreshRate;
            }
        }
        catch
        {
            // Fall through to the last known safe value.
        }
        finally
        {
            if (deviceContext != 0)
            {
                ReleaseDC(windowHandle, deviceContext);
            }
        }

        return Math.Clamp(
            _estimatedDisplayRefreshRate,
            MinimumTrackedRefreshRate,
            MaximumTrackedRefreshRate);
    }

    private void EnsureRenderFrameSubscription()
    {
        if (_renderFrameSubscribed || _frameTimingDisposed)
        {
            return;
        }

        _lastRenderFrameTimestamp = 0;
        CompositionTarget.Rendering += CompositionTarget_Rendering;
        _renderFrameSubscribed = true;
    }

    private void CompositionTarget_Rendering(object? sender, object args)
    {
        long now = Stopwatch.GetTimestamp();
        double fallbackFrameSeconds = 1.0 / Math.Clamp(
            _estimatedDisplayRefreshRate,
            MinimumTrackedRefreshRate,
            MaximumTrackedRefreshRate);
        double elapsedSeconds = fallbackFrameSeconds;

        if (_lastRenderFrameTimestamp != 0)
        {
            elapsedSeconds = (now - _lastRenderFrameTimestamp) / (double)Stopwatch.Frequency;
            TrackRenderedFrameInterval(elapsedSeconds);
        }

        _lastRenderFrameTimestamp = now;

        if (_frameCalibrationSamplesRemaining > 0)
        {
            _frameCalibrationSamplesRemaining--;
        }

        // Only use Rendering as a fallback. The normal drag path is driven by the
        // high-resolution DispatcherQueue timer and keeps the previously working
        // composition-translation behavior unchanged.
        bool needsDragFallback =
            _tabDragActive &&
            !_tabDragHighRefreshTimerRunning;

        if (needsDragFallback)
        {
            ProcessTabDragFrame(elapsedSeconds, allowAutoScroll: true);
        }

        StopRenderFrameSubscriptionIfIdle();
    }

    private void TrackRenderedFrameInterval(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
        {
            return;
        }

        double measuredRefreshRate = 1.0 / elapsedSeconds;
        if (measuredRefreshRate < MinimumTrackedRefreshRate ||
            measuredRefreshRate > MaximumTrackedRefreshRate)
        {
            return;
        }

        const double newSampleWeight = 0.18;
        _estimatedDisplayRefreshRate =
            (_estimatedDisplayRefreshRate * (1 - newSampleWeight)) +
            (measuredRefreshRate * newSampleWeight);
    }

    private void StopRenderFrameSubscriptionIfIdle()
    {
        if (!_renderFrameSubscribed)
        {
            return;
        }

        bool dragStillNeedsFallback =
            _tabDragActive &&
            !_tabDragHighRefreshTimerRunning;

        if (_frameCalibrationSamplesRemaining > 0 || dragStillNeedsFallback)
        {
            return;
        }

        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        _renderFrameSubscribed = false;
        _lastRenderFrameTimestamp = 0;
    }

    private int GetFrameAlignedAnimationDuration(int requestedMilliseconds)
    {
        if (_settings.ReduceMotion)
        {
            return 1;
        }

        StartFrameRateCalibration(samples: 6);

        if (requestedMilliseconds <= 0)
        {
            return 1;
        }

        double refreshRate = Math.Clamp(
            _estimatedDisplayRefreshRate,
            MinimumTrackedRefreshRate,
            MaximumTrackedRefreshRate);
        double frameMilliseconds = 1000.0 / refreshRate;
        int frameCount = Math.Max(
            1,
            (int)Math.Round(
                requestedMilliseconds / frameMilliseconds,
                MidpointRounding.AwayFromZero));

        return Math.Max(
            1,
            (int)Math.Round(
                frameCount * frameMilliseconds,
                MidpointRounding.AwayFromZero));
    }

    private void CleanupFrameRateSynchronization()
    {
        _frameTimingDisposed = true;
        _frameCalibrationSamplesRemaining = 0;

        StopHighRefreshTabDragLoop();

        if (_renderFrameSubscribed)
        {
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            _renderFrameSubscribed = false;
        }

        _lastRenderFrameTimestamp = 0;
    }
}
