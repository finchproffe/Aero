using Aero.App.Settings;
using Aero.Shield;
using Microsoft.Web.WebView2.Core;

namespace Aero.App;

public sealed partial class MainWindow
{
    private static ShieldLevel ToShieldLevel(ShieldMode mode) => mode switch
    {
        ShieldMode.Strict => ShieldLevel.Strict,
        _ => ShieldLevel.Standard
    };

    private static ShieldResourceContext ToShieldResourceContext(
        CoreWebView2WebResourceContext context) => context switch
    {
        CoreWebView2WebResourceContext.Document => ShieldResourceContext.Document,
        CoreWebView2WebResourceContext.Stylesheet => ShieldResourceContext.Stylesheet,
        CoreWebView2WebResourceContext.Image => ShieldResourceContext.Image,
        CoreWebView2WebResourceContext.Media => ShieldResourceContext.Media,
        CoreWebView2WebResourceContext.Font => ShieldResourceContext.Font,
        CoreWebView2WebResourceContext.Script => ShieldResourceContext.Script,
        CoreWebView2WebResourceContext.XmlHttpRequest => ShieldResourceContext.XmlHttpRequest,
        CoreWebView2WebResourceContext.Fetch => ShieldResourceContext.Fetch,
        _ => ShieldResourceContext.Other
    };
}
