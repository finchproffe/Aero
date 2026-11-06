using Aero.Navigation;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static NavigationCompletionSnapshot Snapshot(
    ulong navigationId = 10,
    ulong activeNavigationId = 10,
    ulong domNavigationId = 0,
    ulong verificationNavigationId = 0,
    bool isSuccess = false,
    string status = "Unknown",
    int httpStatus = 0) =>
    new(
        navigationId,
        activeNavigationId,
        domNavigationId,
        verificationNavigationId,
        isSuccess,
        status,
        httpStatus);

NavigationLifecycleState lifecycle = new();
Assert(lifecycle.Begin(10), "First NavigationId must create a document.");
Guid firstIdentity = lifecycle.DocumentIdentity;
Assert(!lifecycle.Begin(10), "A redirect with the same NavigationId must not reset identity.");
Assert(lifecycle.DocumentIdentity == firstIdentity, "Redirect changed document identity.");
lifecycle.MarkDomContentLoaded(10);
Assert(lifecycle.HasCommittedOrVerifiedDocument(10), "DOMContentLoaded evidence was not stored.");
Assert(lifecycle.Begin(11), "A new NavigationId must create a new document.");
Assert(lifecycle.DocumentIdentity != firstIdentity, "New navigation reused document identity.");
Assert(!lifecycle.HasCommittedOrVerifiedDocument(11), "Old document evidence leaked into new navigation.");

NavigationPreProbeDecision stale = NavigationCompletionPolicy.EvaluateBeforeDocumentProbe(
    Snapshot(navigationId: 9, activeNavigationId: 10));
Assert(stale == NavigationPreProbeDecision.IgnoreStaleNavigation, "Stale completion was not ignored.");

NavigationPreProbeDecision httpChallenge = NavigationCompletionPolicy.EvaluateBeforeDocumentProbe(
    Snapshot(status: "Unknown", httpStatus: 503));
Assert(httpChallenge == NavigationPreProbeDecision.PreserveDocument, "HTTP challenge page was not preserved.");

NavigationPreProbeDecision browserCheck = NavigationCompletionPolicy.EvaluateBeforeDocumentProbe(
    Snapshot(domNavigationId: 10));
Assert(browserCheck == NavigationPreProbeDecision.PreserveDocument, "Unknown/0 committed browser-check was not preserved.");

NavigationPreProbeDecision ambiguous = NavigationCompletionPolicy.EvaluateBeforeDocumentProbe(
    Snapshot());
Assert(ambiguous == NavigationPreProbeDecision.ProbeDocument, "Ambiguous Unknown/0 must be probed.");

NavigationPostProbeDecision ambiguousAfterProbe =
    NavigationCompletionPolicy.EvaluateAfterFailedDocumentProbe(Snapshot());
Assert(ambiguousAfterProbe == NavigationPostProbeDecision.PreserveDocument, "Unknown/0 became a false network error page.");

NavigationPostProbeDecision dnsFailure =
    NavigationCompletionPolicy.EvaluateAfterFailedDocumentProbe(
        Snapshot(status: "HostNameNotResolved"));
Assert(dnsFailure == NavigationPostProbeDecision.ShowNetworkErrorPage, "Definitive DNS failure was not classified.");

NavigationDocumentClassification internalDocument =
    NavigationDocumentClassifier.Classify("about:blank", hasInternalPageAddress: true, isStartPage: false);
Assert(internalDocument.IsSyntheticInternalNavigation, "Internal about:blank document was not recognized.");
Assert(!internalDocument.ShouldClearInternalPageAddress, "Internal page address would be cleared.");

string usableProbe = "[\"https://example.com/login\",\"interactive\",\"Login\",128]";
Assert(
    CommittedDocumentProbe.TryReadUsableDocumentUri(usableProbe, out Uri? probedUri) &&
    probedUri?.Host == "example.com",
    "Committed document probe did not recognize a usable HTTPS document.");
Assert(
    !CommittedDocumentProbe.TryReadUsableDocumentUri(
        "[\"chrome-error://chromewebdata/\",\"complete\",\"\",128]",
        out _),
    "Internal Chromium error document was treated as a browsable page.");

Console.WriteLine("Aero.Navigation smoke tests passed.");
