namespace Aero.Shield;

/// <summary>
/// Filtering strength used by the native Shield engine. This enum is kept
/// independent from Aero.App settings so the request hot path has no UI
/// dependency.
/// </summary>
public enum ShieldLevel
{
    Standard,
    Strict
}

/// <summary>
/// Small WebView-agnostic resource classification consumed by Aero.Shield.
/// Aero.App maps CoreWebView2WebResourceContext to this enum at the event edge.
/// </summary>
public enum ShieldResourceContext
{
    Other,
    Document,
    Stylesheet,
    Image,
    Media,
    Font,
    Script,
    XmlHttpRequest,
    Fetch
}
