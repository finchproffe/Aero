namespace Aero.Navigation;

/// <summary>
/// Per-tab navigation identity and browser-verification state.
/// This type deliberately contains no WinUI or WebView2 objects.
/// </summary>
public sealed class NavigationLifecycleState
{
    public ulong ActiveNavigationId { get; private set; }

    public ulong LastDomContentLoadedNavigationId { get; private set; }

    public ulong BrowserVerificationNavigationId { get; private set; }

    public Uri? LastBrowserVerificationUri { get; private set; }

    public Guid DocumentIdentity { get; private set; } = Guid.NewGuid();

    public NavigationTimingState Timing { get; } = new();

    /// <summary>
    /// Starts or continues a WebView2 navigation. Redirects retain the same
    /// NavigationId and therefore do not create a new document identity.
    /// </summary>
    public bool Begin(ulong navigationId)
    {
        if (ActiveNavigationId == navigationId)
        {
            return false;
        }

        ActiveNavigationId = navigationId;
        LastDomContentLoadedNavigationId = 0;
        BrowserVerificationNavigationId = 0;
        LastBrowserVerificationUri = null;
        DocumentIdentity = Guid.NewGuid();
        return true;
    }

    public void MarkDomContentLoaded(ulong navigationId)
    {
        if (navigationId != 0)
        {
            LastDomContentLoadedNavigationId = navigationId;
        }
    }

    public void MarkBrowserVerification(Uri? requestUri)
    {
        if (ActiveNavigationId == 0)
        {
            return;
        }

        BrowserVerificationNavigationId = ActiveNavigationId;
        LastBrowserVerificationUri = requestUri;
    }

    public bool HasCommittedOrVerifiedDocument(ulong navigationId) =>
        LastDomContentLoadedNavigationId == navigationId ||
        BrowserVerificationNavigationId == navigationId;
}
