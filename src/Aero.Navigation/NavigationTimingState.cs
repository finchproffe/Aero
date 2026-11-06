namespace Aero.Navigation;

public sealed class NavigationTimingState
{
    public ulong NavigationId { get; internal set; }

    public long WebViewInitializationStartedTimestamp { get; internal set; }

    public long NavigationStartedTimestamp { get; internal set; }

    public long ContentLoadingTimestamp { get; internal set; }

    public long DomContentLoadedTimestamp { get; internal set; }
}
