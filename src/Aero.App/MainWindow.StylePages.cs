using Aero.App.Settings;
using Windows.UI;

namespace Aero.App;

// The look of Aero's own pages (settings, home) for each interface style. The CSS is appended
// after the page's own rules, so it overrides them; fonts are inlined like Manrope because
// internal pages load through NavigateToString.
public sealed partial class MainWindow
{
    private static readonly Dictionary<string, string> _styleFontCss = new();

    private static string InlineFont(string family, string file, string weights = "100 1000")
    {
        string key = family + "|" + file;
        if (_styleFontCss.TryGetValue(key, out string? css))
        {
            return css;
        }

        try
        {
            byte[] font = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", file));
            css = $"@font-face{{font-family:\"{family}\";src:url(data:font/ttf;base64,{Convert.ToBase64String(font)}) format(\"truetype\");font-weight:{weights};font-display:block}}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            css = string.Empty; // Falls back to the next font in the stack.
        }

        _styleFontCss[key] = css;
        return css;
    }

    // With Liquid Glass, Aero's pages are transparent and sit on the window's own material;
    // websites keep WebView2's default white, since many never set a background.
    private void ApplyPageBackdrop(BrowserTabState state, bool aeroPage)
    {
        bool transparent = aeroPage && EffectiveInterfaceStyle == InterfaceStyle.LiquidGlass;
        state.Browser.DefaultBackgroundColor = transparent
            ? Color.FromArgb(0, 0, 0, 0)
            : Color.FromArgb(255, 255, 255, 255);
    }

    private string InjectStylePageCss(string html, string page)
    {
        string css = BuildStylePageCss(page);
        int end = html.IndexOf("</style>", StringComparison.Ordinal);
        return css.Length == 0 || end < 0 ? html : html.Insert(end, css);
    }

    private string BuildStylePageCss(string page)
    {
        bool dark = IsDarkTheme();
        bool defaultHomeBackground = _settings.HomeBackgroundStyle == HomeBackgroundStyle.Default;
        return EffectiveInterfaceStyle switch
        {
            InterfaceStyle.Material3 => BuildMaterialPageCss(page, BuildMaterialScheme(GetConfiguredAccentColor(), dark), dark, defaultHomeBackground),
            InterfaceStyle.LiquidGlass => BuildGlassPageCss(page, dark, defaultHomeBackground),
            InterfaceStyle.Fluent => ":root{font-family:\"Segoe UI Variable Text\",\"Segoe UI\",sans-serif}",
            InterfaceStyle.Oled when dark && page == "settings" =>
                ":root:root,html[data-theme][data-theme]{--bg:#000;--side:#000;--card:#0a0a0a;--raised:#141414;--line:#1f1f1f;--control-line:#333}input,select,textarea,button.action{border-radius:6px}",
            InterfaceStyle.Sidebar when page == "settings" =>
                "nav button,input,select,textarea,button.action{border-radius:10px}aside{border-right:0}",
            _ => string.Empty
        };
    }

    private string BuildMaterialPageCss(string page, MaterialScheme m, bool dark, bool defaultHomeBackground)
    {
        string fonts =
            InlineFont("Google Sans Flex", "GoogleSansFlex.ttf") +
            InlineFont("Roboto Flex", "RobotoFlex.ttf") +
            InlineFont("Aero Expressive Icons", "AeroExpressiveIcons.ttf", "400");
        string roles = $$"""
:root:root,html[data-theme][data-theme]{color-scheme:{{(dark ? "dark" : "light")}};--bg:{{Css(m.Surface)}};--side:{{Css(m.Surface)}};--card:{{Css(m.Container)}};--raised:{{Css(m.ContainerHigh)}};--line:{{Css(m.OutlineVariant)}};--control-line:transparent;--text:{{Css(m.OnSurface)}};--muted:{{Css(m.OnSurfaceVariant)}};--accent:{{Css(m.Primary)}};--on-accent:{{Css(m.OnPrimary)}};--md-low:{{Css(m.ContainerLow)}};--md-c:{{Css(m.Container)}};--md-high:{{Css(m.ContainerHigh)}};--md-highest:{{Css(m.ContainerHighest)}};--md-pc:{{Css(m.PrimaryContainer)}};--md-onpc:{{Css(m.OnPrimaryContainer)}};--md-sc:{{Css(m.SecondaryContainer)}};--md-onsc:{{Css(m.OnSecondaryContainer)}};--md-tc:{{Css(m.TertiaryContainer)}};--md-ontc:{{Css(m.OnTertiaryContainer)}};--md-outline:{{Css(m.Outline)}};--md-inverse:{{Css(m.InverseSurface)}};--md-oninverse:{{Css(m.InverseOnSurface)}};--spring:cubic-bezier(.2,1.5,.35,1);font-family:"Google Sans Flex","Roboto Flex","Segoe UI Variable Text",sans-serif}
.sym{font-family:"Aero Expressive Icons";font-style:normal;font-weight:400;line-height:1;-webkit-font-smoothing:antialiased}
aside{scrollbar-width:none}html{scrollbar-width:thin;scrollbar-color:var(--md-outline) transparent}
""";

        if (page == "home")
        {
            string background = defaultHomeBackground
                ? $"body::before{{background:{Css(m.Surface)}}}body::after{{background:radial-gradient(900px 520px at 50% 0%,{Css(m.PrimaryContainer)}55,transparent 70%)}}"
                : string.Empty;
            return fonts + roles + background + """
main{padding-top:max(88px,22vh)}
.subtitle{font-size:14px;color:var(--muted)}
form{height:64px;gap:16px;padding:0 10px 0 26px;border:0;border-radius:32px;background:var(--md-high);transition:background-color .2s,border-radius .4s var(--spring)}
form:hover{border-color:transparent;background:var(--md-highest)}
form:focus-within{border-radius:20px;background:var(--md-highest);box-shadow:inset 0 0 0 2px var(--accent)}
#aero-search-query{font-size:18px;color:var(--text)}
#aero-search-query::placeholder{color:var(--muted)}
#quick-links{gap:10px;margin:36px 0 0;justify-content:center}
.shortcut{width:108px;height:116px;padding:14px 8px 10px;border-radius:28px;transition:background-color .2s,border-radius .45s var(--spring)}
.shortcut:hover{background:var(--md-high);border-radius:18px}
.shortcut-icon{width:60px;height:60px;border-radius:20px;background:var(--md-pc);color:var(--md-onpc);font-size:22px;font-weight:600;transition:border-radius .45s var(--spring),transform .45s var(--spring)}
.shortcut:nth-child(3n+2) .shortcut-icon{border-radius:50%;background:var(--md-sc);color:var(--md-onsc)}
.shortcut:nth-child(3n) .shortcut-icon{border-radius:30px 12px;background:var(--md-tc);color:var(--md-ontc)}
.shortcut:hover .shortcut-icon{border-radius:14px;transform:scale(1.06)}
.shortcut-icon img{width:30px;height:30px}
.shortcut-label{margin-top:4px;font-size:13px;font-weight:500;color:var(--muted)}
.shortcut-menu{border-radius:50%}
dialog{border:0;border-radius:28px;background:var(--md-high);color:var(--text)}
.dialog-body h2{font-size:24px;font-weight:500}
.field input{min-height:52px;border:0;border-radius:16px;background:var(--md-highest);color:var(--text)}
.field input:focus{box-shadow:inset 0 0 0 2px var(--accent)}
.dialog-actions button{min-height:40px;padding:0 22px;border:0;border-radius:20px;font-weight:600}
.dialog-actions .primary{background:var(--accent);color:var(--on-accent)}
.home-toast{border:0;border-radius:16px;background:var(--md-inverse);color:var(--md-oninverse)}
""";
        }

        return fonts + roles + """
body{grid-template-columns:304px minmax(0,1fr)}
aside{padding:16px 12px 20px;background:var(--bg);border-right:0}
.brand{padding:14px 16px 18px;font-size:24px;font-weight:600;letter-spacing:-.2px}
.settings-search{margin:0 4px 16px}
.settings-search input{height:56px;min-height:56px;padding:0 24px;border:0;border-radius:28px;background:var(--md-high);font-size:16px}
nav{gap:2px}
nav button{min-height:56px;padding:0 18px;gap:14px;border-radius:28px;font-size:15px;font-weight:500;color:var(--muted);transition:background-color .2s,border-radius .45s var(--spring)}
nav button:hover{background:color-mix(in srgb,var(--text) 8%,transparent);color:var(--text)}
nav button.active{background:var(--md-sc);color:var(--md-onsc);border-radius:18px}
nav button.active:before{display:none}
nav button .nav-icon,nav button.active .nav-icon{color:inherit}
nav .sym{width:24px;font-size:24px;justify-content:center}
.version{padding:16px 18px 0}
main{width:min(940px,100%);padding:40px clamp(24px,4vw,56px) 120px}
h1{font-size:44px;line-height:52px;font-weight:500;letter-spacing:-.8px;margin:8px 0 8px}
.lead{margin:0 0 32px;font-size:16px;color:var(--muted)}
h2{font-size:22px;line-height:28px;font-weight:500}
main .card,main .card:last-child{display:grid;gap:3px;margin:0 0 36px;border:0;background:none}
.card-head{margin:4px 6px 14px}
.card>.hint{margin:6px 8px 12px}
.row{gap:28px;min-height:76px;padding:16px 24px;border:0;border-radius:6px;background:var(--md-c);transition:background-color .15s}
.card>.row:first-child,.card>:not(.row)+.row{border-top-left-radius:28px;border-top-right-radius:28px}
.card>.row:last-child,.card>.row:has(+:not(.row)){border-bottom-left-radius:28px;border-bottom-right-radius:28px}
.row:hover{background:color-mix(in srgb,var(--md-c) 93%,var(--text))}
.label b{font-size:16px;font-weight:500}
.label span{font-size:14px;color:var(--muted)}
input,select,textarea{min-height:52px;padding:0 18px;border:0;border-radius:16px;background:var(--md-highest);color:var(--text);font-size:15px}
textarea{padding:14px 18px}
input:focus,select:focus,textarea:focus{border-color:transparent;box-shadow:inset 0 0 0 2px var(--accent)}
input[type=color]{height:52px;padding:6px}
button.action{min-height:44px;padding:0 24px;border:0;border-radius:22px;background:var(--md-sc);color:var(--md-onsc);font-weight:600;transition:border-radius .35s var(--spring),background-color .2s}
button.action:active:not(:disabled){border-radius:10px}
button.primary{background:var(--accent);color:var(--on-accent)}
.row>input[type=checkbox]{width:52px;height:32px;min-height:32px;border:2px solid var(--md-outline);border-radius:16px;background:var(--md-highest)}
.row>input[type=checkbox]:before{top:6px;left:6px;width:16px;height:16px;background:var(--md-outline);transition:transform .4s var(--spring),width .2s,height .2s,top .2s,left .2s}
.row>input[type=checkbox]:checked{border-color:var(--accent);background:var(--accent)}
.row>input[type=checkbox]:checked:before{top:2px;left:2px;width:24px;height:24px;background:var(--on-accent);transform:translateX(20px)}
.toast{border:0;border-radius:16px;padding:14px 20px;background:var(--md-inverse);color:var(--md-oninverse);box-shadow:0 6px 18px #0004}
.list-item{grid-template-columns:40px minmax(0,1fr) auto;margin-top:3px;padding:14px 20px;border:0;border-radius:6px;background:var(--md-c)}
.list-item:first-child{border-top-left-radius:24px;border-top-right-radius:24px}
.list-item:last-child{border-bottom-left-radius:24px;border-bottom-right-radius:24px}
.avatar{width:40px;height:40px;border-radius:50%;background:var(--md-pc);color:var(--md-onpc)}
.chip{padding:4px 12px;border:1px solid var(--md-outline);border-radius:8px;background:transparent}
.check-grid{padding:16px 24px;border-radius:28px;background:var(--md-c)}
""";
    }

    private string BuildGlassPageCss(string page, bool dark, bool defaultHomeBackground)
    {
        // Windows 11 flyout look, like the tray menu: pages sit on the window's acrylic (see
        // ApplyPageBackdrop), hairline strokes, 8/4 px radii, subtle fills, accent is the only colour.
        Color accent = GetConfiguredAccentColor();
        (_, double chroma, double hue) = ToLch(accent);
        Color selection = FromLch(dark ? 46 : 50, Math.Max(chroma, 45), hue); // keeps white text readable
        string fonts = InlineFont("Aero Glass Icons", "AeroGlassIcons.ttf", "400");
        string roles = $$"""
:root:root,html[data-theme][data-theme]{color-scheme:{{(dark ? "dark" : "light")}};--bg:transparent;--side:transparent;--card:var(--g-group);--raised:var(--g-control);--line:var(--g-sep);--control-line:transparent;--text:{{(dark ? "rgba(255,255,255,.88)" : "rgba(0,0,0,.85)")}};--muted:{{(dark ? "rgba(255,255,255,.55)" : "rgba(0,0,0,.5)")}};--accent:{{Css(selection)}};--on-accent:#fff;--g-group:{{(dark ? "rgba(255,255,255,.05)" : "rgba(255,255,255,.7)")}};--g-panel:{{(dark ? "rgba(44,44,44,.92)" : "rgba(249,249,249,.92)")}};--g-edge:{{(dark ? "rgba(255,255,255,.09)" : "rgba(0,0,0,.06)")}};--g-rim:transparent;--g-sep:{{(dark ? "rgba(255,255,255,.08)" : "rgba(0,0,0,.06)")}};--g-control:{{(dark ? "rgba(255,255,255,.06)" : "rgba(255,255,255,.7)")}};--g-control-hover:{{(dark ? "rgba(255,255,255,.08)" : "rgba(249,249,249,.5)")}};font:14px/1.4 "Segoe UI Variable Text","Segoe UI",sans-serif}
html,html[data-theme],body{background:transparent}
.sym{font-family:"Aero Glass Icons";font-style:normal;font-weight:400;line-height:1}
::-webkit-scrollbar{width:10px}::-webkit-scrollbar-thumb{border:3px solid transparent;border-radius:6px;background:var(--g-control-hover) padding-box}::-webkit-scrollbar-track{background:transparent}
""";

        if (page == "home")
        {
            string background = defaultHomeBackground ? "body::before,body::after{background:none}" : string.Empty;
            return fonts + roles + background + """
main{padding-top:max(96px,24vh)}
.subtitle{color:var(--muted)}
form{height:40px;gap:10px;padding:0 6px 0 14px;border:1px solid var(--g-edge);border-radius:8px;background:var(--g-group);transition:background-color .15s}
form:hover{border-color:var(--g-edge);background:var(--g-control)}
form:focus-within{border-color:var(--accent);box-shadow:0 0 0 3px color-mix(in srgb,var(--accent) 35%,transparent)}
#aero-search-query{font-size:15px;color:var(--text)}
#aero-search-query::placeholder{color:var(--muted)}
#quick-links{gap:6px;margin:28px 0 0;justify-content:center}
.shortcut{width:88px;height:96px;padding:10px 4px 6px;border-radius:8px}
.shortcut:hover{background:var(--g-control)}
.shortcut-icon{width:48px;height:48px;border:1px solid var(--g-edge);border-radius:8px;background:var(--g-group);box-shadow:inset 0 1px 0 var(--g-rim);color:var(--text);font-size:18px}
.shortcut-icon img{width:26px;height:26px}
.shortcut-label{margin-top:4px;font-size:12px;color:var(--text)}
.shortcut-menu{border-radius:6px}
dialog{border:1px solid var(--g-edge);border-radius:8px;background:var(--g-panel);backdrop-filter:blur(30px) saturate(160%);color:var(--text)}
.field input{min-height:28px;border:1px solid var(--g-edge);border-radius:4px;background:var(--g-control);color:var(--text)}
.field input:focus{border-color:var(--accent);box-shadow:0 0 0 3px color-mix(in srgb,var(--accent) 35%,transparent)}
.dialog-actions button{min-height:28px;padding:0 14px;border:0;border-radius:4px;background:var(--g-control);color:var(--text);font-weight:500}
.dialog-actions .primary{background:var(--accent);color:#fff}
.home-toast{border:1px solid var(--g-edge);border-radius:8px;background:var(--g-panel);backdrop-filter:blur(30px);color:var(--text)}
""";
        }

        return fonts + roles + """
body{grid-template-columns:244px minmax(0,1fr)}
aside{margin:8px 0 8px 8px;height:calc(100vh - 16px);padding:10px 8px;border:1px solid var(--g-edge);border-radius:8px;background:var(--g-group);scrollbar-width:none}
.brand{padding:6px 8px 10px;font-size:12px;font-weight:600;color:var(--muted)}
.settings-search{margin:0 0 10px}
.settings-search input{height:28px;min-height:28px;padding:0 10px;border:0;border-radius:4px;background:var(--g-control)}
nav{gap:1px}
nav button{min-height:32px;padding:4px 8px;gap:10px;border-radius:4px;font-size:14px;color:var(--text)}
nav button:hover{background:var(--g-control)}
nav button.active{position:relative;background:var(--g-control-hover)}
nav button.active:before{content:"";display:block;position:absolute;left:0;top:50%;width:3px;height:16px;margin-top:-8px;border-radius:2px;background:var(--accent)}
nav .sym{display:grid;place-items:center;width:20px;height:20px;color:var(--text);font-size:16px;background:transparent}
.version{padding:12px 8px 0;font-size:11px}
main{width:min(720px,100%);padding:28px clamp(20px,4vw,40px) 80px}
h1{font-size:22px;line-height:28px;font-weight:700;letter-spacing:-.3px;margin:0 0 2px}
.lead{margin:0 0 18px;font-size:13px}
h2{font-size:13px;font-weight:600}
.hint,.meta,.label span{font-size:11.5px}
main .card,main .card:last-child{margin:0 0 20px;padding:0;border:1px solid var(--g-edge);border-radius:8px;background:var(--g-group)}
.card-head{margin:10px 12px 6px}
.card>.hint,.card>.actions,.card>.chips,.card>.stack,.card>.check-grid{padding-left:12px;padding-right:12px}
.row{grid-template-columns:minmax(0,1fr) minmax(150px,240px);gap:16px;min-height:42px;padding:7px 12px;border-top:1px solid var(--g-sep)}
.card>.row:first-child,.card>:not(.row)+.row{border-top:0}
.label b{font-weight:400}
input,select,textarea{min-height:28px;padding:2px 8px;border:1px solid var(--g-edge);border-radius:4px;background:var(--g-control);color:var(--text);font-size:14px}
select{padding-right:4px}
input:hover,select:hover{background:var(--g-control-hover)}
input:focus,select:focus,textarea:focus{border-color:var(--accent);box-shadow:0 0 0 3px color-mix(in srgb,var(--accent) 35%,transparent)}
input[type=color]{height:24px;padding:2px}
button.action{min-height:28px;padding:0 12px;border:1px solid var(--g-edge);border-radius:4px;background:var(--g-control);color:var(--text);font-weight:500}
button.action:hover:not(:disabled){filter:none;background:var(--g-control-hover)}
button.primary,button.primary:hover:not(:disabled){background:var(--accent);color:#fff}
.row>input[type=checkbox]{width:32px;height:18px;min-height:18px;border:0;border-radius:9px;background:var(--g-control-hover)}
.row>input[type=checkbox]:before{top:2px;left:2px;width:14px;height:14px;background:#fff;box-shadow:0 1px 2px rgba(0,0,0,.3);transition:transform .2s cubic-bezier(.3,1.3,.5,1)}
.row>input[type=checkbox]:checked{background:var(--accent)}
.row>input[type=checkbox]:checked:before{background:#fff;transform:translateX(14px)}
.toast{border:1px solid var(--g-edge);border-radius:8px;padding:8px 12px;background:var(--g-panel);backdrop-filter:blur(30px) saturate(160%);box-shadow:0 8px 24px rgba(0,0,0,.25)}
.list-item{padding:8px 12px;border-top:1px solid var(--g-sep)}
.list-item:first-child{border-top:0}
.avatar{width:28px;height:28px;border-radius:4px;background:var(--g-control)}
.chip{border-radius:4px;background:var(--g-control)}
""";
    }
}
