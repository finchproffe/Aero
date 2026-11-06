using Aero.Navigation;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Aero.App;

public sealed partial class MainWindow
{
    [Conditional("DEBUG")]
    private static void TraceWebViewInitialization(BrowserTabState state, string stage)
    {
        NavigationTimingRecorder.TraceWebViewInitialization(
            state.Navigation.Timing,
            RuntimeHelpers.GetHashCode(state),
            stage);
    }

    [Conditional("DEBUG")]
    private static void TraceNavigationStarting(
        BrowserTabState state,
        CoreWebView2NavigationStartingEventArgs args)
    {
        NavigationTimingRecorder.TraceNavigationStarting(
            state.Navigation.Timing,
            RuntimeHelpers.GetHashCode(state),
            args.NavigationId,
            args.Uri,
            args.IsRedirected);
    }

    [Conditional("DEBUG")]
    private static void TraceContentLoading(
        BrowserTabState state,
        CoreWebView2ContentLoadingEventArgs args)
    {
        NavigationTimingRecorder.TraceContentLoading(
            state.Navigation.Timing,
            RuntimeHelpers.GetHashCode(state),
            args.NavigationId,
            args.IsErrorPage);
    }

    [Conditional("DEBUG")]
    private static void TraceDomContentLoaded(
        BrowserTabState state,
        ulong navigationId,
        string stage)
    {
        NavigationTimingRecorder.TraceDomContentLoaded(
            state.Navigation.Timing,
            RuntimeHelpers.GetHashCode(state),
            navigationId,
            stage);
    }

    [Conditional("DEBUG")]
    private static void LogNavigationCompletion(
        BrowserTabState state,
        CoreWebView2NavigationCompletedEventArgs args,
        string decision)
    {
        NavigationTimingRecorder.TraceNavigationCompleted(
            state.Navigation.Timing,
            RuntimeHelpers.GetHashCode(state),
            args.NavigationId,
            args.IsSuccess,
            args.WebErrorStatus.ToString(),
            args.HttpStatusCode,
            decision,
            TryGetCurrentNavigationSource(state));
    }

    private static string? TryGetCurrentNavigationSource(BrowserTabState state)
    {
        try
        {
            return state.Browser.CoreWebView2?.Source ??
                state.Browser.Source?.AbsoluteUri ??
                state.LastNavigationUri?.AbsoluteUri;
        }
        catch
        {
            return state.LastNavigationUri?.AbsoluteUri;
        }
    }
}
