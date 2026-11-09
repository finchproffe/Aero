using Microsoft.Web.WebView2.Core;

namespace Aero.App;

// Clicks "Reject all" on cookie-consent banners. Never clicks accept or settings:
// if a banner has no visible reject button, it is left alone.
public sealed partial class MainWindow
{
    private const string CookieBannerRejectScript = """
(() => {
    if (window.__aeroCookieReject) return;
    window.__aeroCookieReject = true;

    // Reject buttons of the common consent platforms.
    const known = [
        "#onetrust-reject-all-handler", "#CybotCookiebotDialogBodyButtonDecline",
        "#didomi-notice-disagree-button", "#truste-consent-required", ".cky-btn-reject",
        ".cmplz-btn.cmplz-deny", ".osano-cm-denyAll", ".iubenda-cs-reject-btn",
        "[data-testid='uc-deny-all-button']", "#tarteaucitronAllDenied2",
        ".fc-cta-do-not-consent", ".cmpboxbtnno"
    ];
    const words = new Set([
        "reject all", "reject all cookies", "reject cookies", "reject", "decline all", "decline",
        "deny all", "deny", "refuse all", "do not consent", "continue without accepting",
        "necessary cookies only", "only necessary cookies", "use necessary cookies only",
        "accept only necessary cookies", "accept necessary only", "only essential cookies",
        "essential cookies only",
        "отклонить все", "отклонить всё", "отклонить", "отказаться", "отказаться от всех",
        "только необходимые", "только обязательные", "запретить все", "відхилити все", "відхилити",
        "alle ablehnen", "ablehnen", "nur notwendige", "nur notwendige cookies", "nur essenzielle cookies",
        "tout refuser", "refuser", "continuer sans accepter", "rechazar todo", "rechazar todas",
        "rechazar", "rifiuta tutto", "rifiuta", "odrzuć wszystkie", "odrzuć", "alles weigeren",
        "weigeren", "rejeitar tudo", "rejeitar"
    ]);
    const banner = /cookie|consent|gdpr|privacy|cmp|didomi|onetrust|usercentrics|sp_message|qc-cmp|truste|cky-|cmplz/i;
    // Consent dialogs served in their own frame (Sourcepoint, consent.google.com, ...).
    const consentDocument = banner.test(location.hostname + location.pathname);

    const norm = s => (s || "").replace(/\s+/g, " ").trim().toLowerCase();
    const visible = el => el.getClientRects().length > 0 && getComputedStyle(el).visibility !== "hidden";
    const insideBanner = el => {
        if (consentDocument) return true;
        for (let n = el, depth = 0; n && depth < 30; depth++) {
            const label = (n.id || "") + " " + (typeof n.className === "string" ? n.className : "") + " " +
                (n.getAttribute?.("aria-label") || "");
            if (banner.test(label)) return true;
            n = n.parentElement || n.getRootNode?.().host;
        }
        return false;
    };
    const roots = () => [document,
        document.querySelector("#usercentrics-root")?.shadowRoot,
        document.querySelector("#cmpwrapper")?.shadowRoot].filter(Boolean);

    const tryReject = () => {
        for (const root of roots()) {
            for (const selector of known) {
                const el = root.querySelector(selector);
                if (el && visible(el)) { el.click(); return true; }
            }
            for (const el of root.querySelectorAll("button, [role=button], a, input[type=button], input[type=submit]")) {
                const text = norm(el.innerText || el.value || el.getAttribute("aria-label"));
                if (text.length < 40 && words.has(text) && visible(el) && insideBanner(el)) { el.click(); return true; }
            }
        }
        return false;
    };

    // Banners usually appear a moment after load; watch for 20 s, then stop.
    let timer = 0, observer = null;
    const stop = () => { observer?.disconnect(); clearTimeout(timer); };
    const run = () => { timer = 0; if (tryReject()) stop(); };
    const start = () => {
        if (tryReject()) return;
        observer = new MutationObserver(() => { if (!timer) timer = setTimeout(run, 300); });
        observer.observe(document.documentElement, { childList: true, subtree: true });
        setTimeout(stop, 20000);
    };
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start, { once: true });
    else start();
})();
""";

    // shortcut: registered when a tab's WebView is configured, so toggling the setting
    // affects new tabs only; store the script id and remove it if live toggling is wanted.
    private async Task RegisterCookieBannerRejectAsync(BrowserTabState state)
    {
        if (!_settings.RejectCookieBanners || state.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(CookieBannerRejectScript);
        }
        catch
        {
            // The controller can be replaced while a tab is closing or restoring.
        }
    }
}
