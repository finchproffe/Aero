using Aero.App.Security;
using Microsoft.Web.WebView2.Core;

namespace Aero.App;

public sealed partial class MainWindow
{
    // An unlock covers the whole site for 30 minutes and is extended by every visit to it.
    private static readonly TimeSpan SiteUnlockLifetime = TimeSpan.FromMinutes(30);

    // Shared by all windows: one Windows Hello confirmation unlocks the site everywhere.
    private static readonly Dictionary<string, DateTimeOffset> UnlockedSites =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _siteUnlockPromptOpen;

    /// <summary>Cancels a top-level navigation to a locked site until Windows Hello confirms the user.</summary>
    private bool TryInterceptLockedSiteNavigation(
        BrowserTabState state,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (_settings.LockedSites.Count == 0 ||
            !Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? uri) ||
            !IsBrowsableUri(uri) ||
            FindLockedSiteEntry(uri.Host) is not string entry)
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (UnlockedSites)
        {
            if (UnlockedSites.TryGetValue(entry, out DateTimeOffset until) && until > now)
            {
                UnlockedSites[entry] = now + SiteUnlockLifetime;
                return false;
            }
        }

        args.Cancel = true;
        _ = UnlockSiteAndNavigateAsync(state, uri, entry);
        return true;
    }

    private async Task UnlockSiteAndNavigateAsync(BrowserTabState state, Uri uri, string entry)
    {
        if (_siteUnlockPromptOpen)
        {
            return;
        }

        _siteUnlockPromptOpen = true;
        try
        {
            await PasswordVaultService.VerifySiteUnlockAsync(
                WinRT.Interop.WindowNative.GetWindowHandle(this),
                entry);
            lock (UnlockedSites)
            {
                UnlockedSites[entry] = DateTimeOffset.UtcNow + SiteUnlockLifetime;
            }

            if (_tabs.Contains(state))
            {
                Navigate(state, uri);
            }
        }
        catch (PasswordVaultVerificationException exception)
        {
            ShowNoticeToast(
                Localization.AeroLocalizer.Format("Notice.SiteLocked", entry),
                exception.Message);
        }
        finally
        {
            _siteUnlockPromptOpen = false;
        }
    }

    private string? FindLockedSiteEntry(string host)
    {
        foreach (string raw in _settings.LockedSites)
        {
            string entry = NormalizeSiteEntry(raw);
            if (entry.Length > 0 && HostMatchesAny(host, [entry]))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>"https://www.Bank.com/login" → "bank.com", so pasted URLs work as list entries.</summary>
    internal static string NormalizeSiteEntry(string raw)
    {
        string value = raw.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host))
        {
            value = uri.Host;
        }
        else
        {
            int slash = value.IndexOf('/');
            value = slash >= 0 ? value[..slash] : value;
        }

        value = value.Trim().TrimEnd('.').ToLowerInvariant();
        return value.StartsWith("www.", StringComparison.Ordinal) ? value[4..] : value;
    }

    // Locked sites can also lose their login when Aero closes: they reuse the temporary-cookie
    // mechanism, which turns their cookies into session cookies and deletes them on close.
    private bool IsTemporaryCookieHost(string? host) =>
        HostMatchesAny(host, _settings.TemporaryCookieSites) ||
        _settings.WipeLockedSiteSessions &&
        HostMatchesAny(host, _settings.LockedSites.Select(NormalizeSiteEntry).Where(entry => entry.Length > 0));

    private bool HasTemporaryCookieSites =>
        _settings.TemporaryCookieSites.Count > 0 ||
        _settings.WipeLockedSiteSessions && _settings.LockedSites.Count > 0;
}
