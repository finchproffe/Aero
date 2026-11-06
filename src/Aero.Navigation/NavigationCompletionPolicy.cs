namespace Aero.Navigation;

public enum NavigationPreProbeDecision
{
    ContinueSuccessfulNavigation,
    IgnoreStaleNavigation,
    PreserveDocument,
    ProbeDocument
}

public enum NavigationPostProbeDecision
{
    PreserveDocument,
    ShowNetworkErrorPage
}

public readonly record struct NavigationCompletionSnapshot(
    ulong NavigationId,
    ulong ActiveNavigationId,
    ulong LastDomContentLoadedNavigationId,
    ulong BrowserVerificationNavigationId,
    bool IsSuccess,
    string WebErrorStatus,
    int HttpStatusCode);

/// <summary>
/// Pure navigation-completion policy. It intentionally treats ambiguous
/// WebView2 failures conservatively so browser-check and anti-bot documents
/// are not replaced by an Aero error page.
/// </summary>
public static class NavigationCompletionPolicy
{
    public static NavigationPreProbeDecision EvaluateBeforeDocumentProbe(
        in NavigationCompletionSnapshot snapshot)
    {
        if (snapshot.ActiveNavigationId != 0 &&
            snapshot.NavigationId != snapshot.ActiveNavigationId)
        {
            return NavigationPreProbeDecision.IgnoreStaleNavigation;
        }

        if (snapshot.IsSuccess)
        {
            return NavigationPreProbeDecision.ContinueSuccessfulNavigation;
        }

        // An HTTP response means the server returned a document. A 4xx/5xx
        // response can be a login, CAPTCHA, rate-limit or browser-check page.
        if (snapshot.HttpStatusCode != 0)
        {
            return NavigationPreProbeDecision.PreserveDocument;
        }

        if (IsExpectedCancellation(snapshot.WebErrorStatus))
        {
            return NavigationPreProbeDecision.PreserveDocument;
        }

        // WebView2 can report Unknown/0 after a browser-check document commits
        // and then stops or replaces a provisional navigation.
        if (string.Equals(
                snapshot.WebErrorStatus,
                "Unknown",
                StringComparison.Ordinal) &&
            (snapshot.LastDomContentLoadedNavigationId == snapshot.NavigationId ||
             snapshot.BrowserVerificationNavigationId == snapshot.NavigationId))
        {
            return NavigationPreProbeDecision.PreserveDocument;
        }

        return NavigationPreProbeDecision.ProbeDocument;
    }

    public static NavigationPostProbeDecision EvaluateAfterFailedDocumentProbe(
        in NavigationCompletionSnapshot snapshot)
    {
        return IsDefinitiveNetworkFailureStatus(snapshot.WebErrorStatus)
            ? NavigationPostProbeDecision.ShowNetworkErrorPage
            : NavigationPostProbeDecision.PreserveDocument;
    }

    public static bool IsExpectedCancellation(string status) =>
        string.Equals(status, "OperationCanceled", StringComparison.Ordinal) ||
        string.Equals(status, "ConnectionAborted", StringComparison.Ordinal);

    public static bool IsDefinitiveNetworkFailureStatus(string status) =>
        string.Equals(status, "ServerUnreachable", StringComparison.Ordinal) ||
        string.Equals(status, "Timeout", StringComparison.Ordinal) ||
        string.Equals(status, "ErrorHttpInvalidServerResponse", StringComparison.Ordinal) ||
        string.Equals(status, "ConnectionReset", StringComparison.Ordinal) ||
        string.Equals(status, "Disconnected", StringComparison.Ordinal) ||
        string.Equals(status, "CannotConnect", StringComparison.Ordinal) ||
        string.Equals(status, "HostNameNotResolved", StringComparison.Ordinal) ||
        string.Equals(status, "RedirectFailed", StringComparison.Ordinal);
}
