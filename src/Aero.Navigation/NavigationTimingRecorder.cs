using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Aero.Navigation;

/// <summary>
/// Optional DEBUG-only navigation timing. Writes are queued and never block
/// the UI or navigation callback that produced the measurement.
/// </summary>
public static class NavigationTimingRecorder
{
#if DEBUG
    private static readonly bool Enabled =
        string.Equals(
            Environment.GetEnvironmentVariable("AERO_NAV_TIMING"),
            "1",
            StringComparison.Ordinal);
    private static readonly ConcurrentQueue<string> Queue = new();
    private static int _writerRunning;
#endif

    [Conditional("DEBUG")]
    public static void TraceWebViewInitialization(
        NavigationTimingState state,
        int tabIdentity,
        string stage)
    {
#if DEBUG
        if (!Enabled)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (stage.Equals("started", StringComparison.Ordinal))
        {
            state.WebViewInitializationStartedTimestamp = now;
        }

        double elapsedMilliseconds = state.WebViewInitializationStartedTimestamp == 0
            ? 0
            : Stopwatch.GetElapsedTime(
                state.WebViewInitializationStartedTimestamp,
                now).TotalMilliseconds;
        Enqueue(
            tabIdentity,
            navigationId: 0,
            stage: "webview-" + stage,
            elapsedMilliseconds,
            source: null,
            detail: null);
#endif
    }

    [Conditional("DEBUG")]
    public static void TraceNavigationStarting(
        NavigationTimingState state,
        int tabIdentity,
        ulong navigationId,
        string? source,
        bool isRedirected)
    {
#if DEBUG
        if (!Enabled)
        {
            return;
        }

        state.NavigationId = navigationId;
        state.NavigationStartedTimestamp = Stopwatch.GetTimestamp();
        state.ContentLoadingTimestamp = 0;
        state.DomContentLoadedTimestamp = 0;
        Enqueue(
            tabIdentity,
            navigationId,
            "navigation-starting",
            0,
            source,
            isRedirected ? "redirect" : null);
#endif
    }

    [Conditional("DEBUG")]
    public static void TraceContentLoading(
        NavigationTimingState state,
        int tabIdentity,
        ulong navigationId,
        bool isErrorPage)
    {
#if DEBUG
        if (!Enabled || state.NavigationId != navigationId)
        {
            return;
        }

        state.ContentLoadingTimestamp = Stopwatch.GetTimestamp();
        Enqueue(
            tabIdentity,
            navigationId,
            "content-loading",
            GetElapsedMilliseconds(state),
            source: null,
            detail: isErrorPage ? "error-page" : null);
#endif
    }

    [Conditional("DEBUG")]
    public static void TraceDomContentLoaded(
        NavigationTimingState state,
        int tabIdentity,
        ulong navigationId,
        string stage)
    {
#if DEBUG
        if (!Enabled || state.NavigationId != navigationId)
        {
            return;
        }

        if (stage.Equals("received", StringComparison.Ordinal))
        {
            state.DomContentLoadedTimestamp = Stopwatch.GetTimestamp();
        }

        Enqueue(
            tabIdentity,
            navigationId,
            "dom-content-loaded-" + stage,
            GetElapsedMilliseconds(state),
            source: null,
            detail: null);
#endif
    }

    [Conditional("DEBUG")]
    public static void TraceNavigationCompleted(
        NavigationTimingState state,
        int tabIdentity,
        ulong navigationId,
        bool isSuccess,
        string webErrorStatus,
        int httpStatusCode,
        string decision,
        string? source)
    {
#if DEBUG
        if (!Enabled)
        {
            return;
        }

        string detail = string.Create(
            CultureInfo.InvariantCulture,
            $"decision={decision};success={isSuccess};status={webErrorStatus};http={httpStatusCode}");
        Enqueue(
            tabIdentity,
            navigationId,
            "navigation-completed",
            GetElapsedMilliseconds(state),
            source,
            detail);
#endif
    }

#if DEBUG
    private static double GetElapsedMilliseconds(NavigationTimingState state)
    {
        return state.NavigationStartedTimestamp == 0
            ? 0
            : Stopwatch.GetElapsedTime(
                state.NavigationStartedTimestamp).TotalMilliseconds;
    }

    private static void Enqueue(
        int tabIdentity,
        ulong navigationId,
        string stage,
        double elapsedMilliseconds,
        string? source,
        string? detail)
    {
        string safeOrigin = GetSafeOrigin(source);
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:O} tab={tabIdentity:X8} nav={navigationId} stage={stage} elapsedMs={elapsedMilliseconds:F2} origin={safeOrigin} {detail ?? string.Empty}");
        Queue.Enqueue(line);
        Debug.WriteLine(line);

        if (Interlocked.CompareExchange(ref _writerRunning, 1, 0) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem<object?>(
                static _ => DrainQueue(),
                null,
                preferLocal: false);
        }
    }

    private static string GetSafeOrigin(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "<internal>";
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static void DrainQueue()
    {
        try
        {
            StringBuilder batch = new();
            while (Queue.TryDequeue(out string? line))
            {
                batch.AppendLine(line);
                if (batch.Length >= 32 * 1024)
                {
                    AppendBatch(batch);
                    batch.Clear();
                }
            }

            if (batch.Length > 0)
            {
                AppendBatch(batch);
            }
        }
        catch
        {
            // Optional diagnostics must never affect the browser.
        }
        finally
        {
            Interlocked.Exchange(ref _writerRunning, 0);
            if (!Queue.IsEmpty &&
                Interlocked.CompareExchange(ref _writerRunning, 1, 0) == 0)
            {
                ThreadPool.UnsafeQueueUserWorkItem<object?>(
                    static _ => DrainQueue(),
                    null,
                    preferLocal: false);
            }
        }
    }

    private static void AppendBatch(StringBuilder batch)
    {
        string logFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Aero",
            "Logs");
        Directory.CreateDirectory(logFolder);
        string logPath = Path.Combine(logFolder, "navigation-timing.log");
        if (File.Exists(logPath) && new FileInfo(logPath).Length > 4 * 1024 * 1024)
        {
            string previousPath = Path.Combine(
                logFolder,
                "navigation-timing.previous.log");
            File.Move(logPath, previousPath, overwrite: true);
        }

        File.AppendAllText(logPath, batch.ToString(), Encoding.UTF8);
    }
#endif
}
