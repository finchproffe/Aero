using Aero.App.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;
using System.Collections.Concurrent;

namespace Aero.App;

public sealed partial class MainWindow
{
    private bool _updatingSitePrivacyUi;
    private int _sitePrivacyRefreshGeneration;

    private async Task RefreshSitePrivacyDataAsync()
    {
        int generation = ++_sitePrivacyRefreshGeneration;
        BrowserTabState? state = ActiveTab;
        string? origin = GetActiveSiteOrigin();
        if (state?.Browser.CoreWebView2 is not CoreWebView2 core ||
            origin is null ||
            !Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
        {
            SiteCookiesSummaryText.Text = "Cookies: unavailable";
            SiteLocalStorageText.Text = "localStorage: unavailable";
            SiteCookiesPanel.Children.Clear();
            DeleteSiteCookiesButton.IsEnabled = false;
            ResetSiteDataButton.IsEnabled = false;
            return;
        }

        UpdateSitePrivacyPolicyControls(uri);
        UpdateSitePrivacySummary(state, origin, uri);

        try
        {
            await EnforceTemporaryCookiesForSiteAsync(core, uri);
            IReadOnlyList<CoreWebView2Cookie> cookies =
                await core.CookieManager.GetCookiesAsync(origin);
            if (generation != _sitePrivacyRefreshGeneration ||
                !ReferenceEquals(state, ActiveTab) ||
                !string.Equals(origin, GetActiveSiteOrigin(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            RenderSiteCookies(core.CookieManager, cookies);
            string cookieCount = cookies.Count == 1
                ? "Cookies: 1 for this site"
                : $"Cookies: {cookies.Count} for this site";
            SiteCookiesSummaryText.Text = state.ThirdPartyCookieBlockCount > 0
                ? $"{cookieCount} · {state.ThirdPartyCookieBlockCount:N0} third-party sends blocked"
                : cookieCount;
            DeleteSiteCookiesButton.IsEnabled = cookies.Count > 0;
        }
        catch (Exception exception)
        {
            SiteCookiesPanel.Children.Clear();
            SiteCookiesSummaryText.Text = $"Cookies unavailable: {exception.Message}";
            DeleteSiteCookiesButton.IsEnabled = false;
        }

        try
        {
            string json = await core.ExecuteScriptAsync("""
                (() => ({
                    entries: localStorage.length,
                    sessionEntries: sessionStorage.length
                }))()
                """);
            using JsonDocument document = JsonDocument.Parse(json);
            int localEntries = document.RootElement.GetProperty("entries").GetInt32();
            int sessionEntries = document.RootElement.GetProperty("sessionEntries").GetInt32();
            SiteLocalStorageText.Text =
                $"localStorage: {localEntries} entries · sessionStorage: {sessionEntries}";
        }
        catch
        {
            SiteLocalStorageText.Text = "localStorage: unavailable for this document";
        }

        ResetSiteDataButton.IsEnabled = true;
    }

    private void UpdateSitePrivacyPolicyControls(Uri uri)
    {
        _updatingSitePrivacyUi = true;
        try
        {
            SiteThirdPartyCookiesToggle.IsOn = _settings.BlockThirdPartyCookies;
            SiteThirdPartyCookieExceptionToggle.IsOn =
                HostMatchesAny(uri.Host, _settings.ThirdPartyCookieExceptions);
            SiteThirdPartyCookieExceptionToggle.IsEnabled = _settings.BlockThirdPartyCookies;
            SiteTemporaryCookiesToggle.IsOn =
                IsTemporaryCookieHost(uri.Host);
            SiteCookieCleanupText.Text = _settings.ClearDataOnClose && _settings.ClearCookiesOnClose
                ? "Automatic cleanup is enabled: cookies are deleted when the last Aero window for this profile closes. Listed privacy exceptions are preserved."
                : "Automatic cookie cleanup on close is off. Use temporary cookies for this site or enable cleanup in Settings → Privacy.";
        }
        finally
        {
            _updatingSitePrivacyUi = false;
        }
    }

    private void UpdateSitePrivacySummary(BrowserTabState state, string origin, Uri uri)
    {
        List<string> active = [];
        foreach (SitePermissionKind kind in new[]
        {
            SitePermissionKind.Camera,
            SitePermissionKind.Microphone,
            SitePermissionKind.Geolocation,
            SitePermissionKind.Notifications,
            SitePermissionKind.Popups,
            SitePermissionKind.Redirects
        })
        {
            SitePermissionDecision storedDecision = _sitePermissionStore.GetDecision(origin, kind);
            SitePermissionDecision effectiveDecision = storedDecision == SitePermissionDecision.Ask
                ? GetDefaultPermissionDecision(kind)
                : storedDecision;
            if (effectiveDecision != SitePermissionDecision.Ask)
            {
                string source = storedDecision == SitePermissionDecision.Ask ? " (default)" : string.Empty;
                active.Add(
                    $"{GetPermissionDisplayName(kind)}: {effectiveDecision.ToString().ToLowerInvariant()}{source}");
            }
        }

        SiteActivePermissionsText.Text = active.Count == 0
            ? "Active permissions: default / ask"
            : "Active permissions: " + string.Join(", ", active);

        bool compatibilityMode = IsCompatibilitySensitiveUri(uri);
        bool shieldAllowed = _settings.IsShieldAllowedForHost(uri.Host);
        bool shieldEnabled = _settings.BlockAdsAndTrackers && !shieldAllowed && !compatibilityMode;
        bool previousUpdatingState = _updatingSitePrivacyUi;
        _updatingSitePrivacyUi = true;
        SitePrivacyShieldToggle.IsEnabled = _settings.BlockAdsAndTrackers && !compatibilityMode;
        SitePrivacyShieldToggle.IsOn = shieldEnabled;
        _updatingSitePrivacyUi = previousUpdatingState;
        SiteShieldSummaryText.Text = shieldEnabled
            ? $"Shield: active · {state.NativeBlockedRequestCount:N0} native requests blocked on this page"
            : compatibilityMode
                ? "Shield: paused for site compatibility"
                : shieldAllowed
                    ? "Shield: disabled for this site"
                    : "Shield: disabled globally";
    }

    private void RenderSiteCookies(
        CoreWebView2CookieManager cookieManager,
        IReadOnlyList<CoreWebView2Cookie> cookies)
    {
        SiteCookiesPanel.Children.Clear();
        if (cookies.Count == 0)
        {
            SiteCookiesPanel.Children.Add(new TextBlock
            {
                Text = "No cookies are stored for this site.",
                FontSize = 10,
                Foreground = GetMutedTextBrush()
            });
            return;
        }

        foreach (CoreWebView2Cookie cookie in cookies.Take(50))
        {
            Grid row = new() { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string lifetime = cookie.IsSession
                ? "session"
                : FormatCookieExpiration(cookie.Expires);
            string flags = string.Join(", ", new[]
            {
                cookie.IsSecure ? "Secure" : string.Empty,
                cookie.IsHttpOnly ? "HttpOnly" : string.Empty,
                cookie.SameSite.ToString()
            }.Where(value => value.Length > 0));
            StackPanel details = new() { Spacing = 1 };
            details.Children.Add(new TextBlock
            {
                Text = cookie.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            details.Children.Add(new TextBlock
            {
                Text = $"{cookie.Domain}{cookie.Path} · {lifetime} · {flags}",
                FontSize = 9,
                Foreground = GetMutedTextBrush(),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            row.Children.Add(details);

            Button delete = new() { Content = "Delete", Padding = new Thickness(9, 3, 9, 3) };
            Grid.SetColumn(delete, 1);
            delete.Click += async (_, _) =>
            {
                delete.IsEnabled = false;
                try
                {
                    cookieManager.DeleteCookie(cookie);
                    await RefreshSitePrivacyDataAsync();
                }
                catch (Exception exception)
                {
                    SiteCookiesSummaryText.Text = $"Could not delete cookie: {exception.Message}";
                    delete.IsEnabled = true;
                }
            };
            row.Children.Add(delete);

            Border border = new()
            {
                Padding = new Thickness(8),
                BorderBrush = GetSoftBorderBrush(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = row
            };
            SiteCookiesPanel.Children.Add(border);
        }

        if (cookies.Count > 50)
        {
            SiteCookiesPanel.Children.Add(new TextBlock
            {
                Text = $"Showing the first 50 of {cookies.Count} cookies.",
                FontSize = 9,
                Foreground = GetMutedTextBrush()
            });
        }
    }

    private void SiteThirdPartyCookiesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingSitePrivacyUi)
        {
            return;
        }

        _settings.BlockThirdPartyCookies = SiteThirdPartyCookiesToggle.IsOn;
        _settings.Save();
        if (ActiveTab?.CurrentUri is Uri uri)
        {
            UpdateSitePrivacyPolicyControls(uri);
        }
    }

    private async void SitePrivacyShieldToggle_Toggled(object sender, RoutedEventArgs e)
    {
        BrowserTabState? state = ActiveTab;
        if (_updatingSitePrivacyUi ||
            state?.CurrentUri is not Uri uri ||
            !IsBrowsableUri(uri) ||
            IsCompatibilitySensitiveUri(uri))
        {
            return;
        }

        _settings.SetShieldAllowedForHost(uri.Host, allowed: !SitePrivacyShieldToggle.IsOn);
        _settings.Save();
        try
        {
            await SyncAdvancedShieldForUriAsync(uri);
            if (ReferenceEquals(state, ActiveTab))
            {
                if (SitePrivacyShieldToggle.IsOn)
                {
                    await ApplyCosmeticFilteringAsync(state);
                }
                else
                {
                    await RemoveShieldPageArtifactsAsync(state);
                }

                state.Browser.Reload();
            }

            UpdateSiteIndicator(uri);
            UpdateShieldUi();
            if (ReferenceEquals(state, ActiveTab))
            {
                UpdateSitePrivacySummary(state, uri.GetLeftPart(UriPartial.Authority), uri);
            }
        }
        catch (Exception exception)
        {
            SiteShieldSummaryText.Text = $"Could not change Shield: {exception.Message}";
        }
    }

    private void SiteThirdPartyCookieExceptionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingSitePrivacyUi || ActiveTab?.CurrentUri is not Uri uri)
        {
            return;
        }

        SetHostListEntry(
            _settings.ThirdPartyCookieExceptions,
            uri.Host,
            SiteThirdPartyCookieExceptionToggle.IsOn);
        _settings.Save();
    }

    private async void SiteTemporaryCookiesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingSitePrivacyUi ||
            ActiveTab?.CurrentUri is not Uri uri ||
            ActiveTab.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        SetHostListEntry(
            _settings.TemporaryCookieSites,
            uri.Host,
            SiteTemporaryCookiesToggle.IsOn);
        _settings.Save();
        if (SiteTemporaryCookiesToggle.IsOn)
        {
            try
            {
                await EnforceTemporaryCookiesForSiteAsync(core, uri);
                await RefreshSitePrivacyDataAsync();
            }
            catch (Exception exception)
            {
                SiteCookiesSummaryText.Text =
                    $"Could not convert cookies to temporary cookies: {exception.Message}";
            }
        }
    }

    private async void DeleteSiteCookiesButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab?.Browser.CoreWebView2 is not CoreWebView2 core ||
            GetActiveSiteOrigin() is not string origin)
        {
            return;
        }

        DeleteSiteCookiesButton.IsEnabled = false;
        try
        {
            await DeleteCookiesForOriginAsync(core.CookieManager, origin);
            await RefreshSitePrivacyDataAsync();
        }
        catch (Exception exception)
        {
            SiteCookiesSummaryText.Text = $"Could not delete site cookies: {exception.Message}";
            DeleteSiteCookiesButton.IsEnabled = true;
        }
    }

    private async void ResetSiteDataButton_Click(object sender, RoutedEventArgs e)
    {
        BrowserTabState? state = ActiveTab;
        if (state?.Browser.CoreWebView2 is not CoreWebView2 core ||
            GetActiveSiteOrigin() is not string origin ||
            !Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        ResetSiteDataButton.IsEnabled = false;
        try
        {
            await DeleteCookiesForOriginAsync(core.CookieManager, origin);
            await core.ExecuteScriptAsync("""
                (async () => {
                    localStorage.clear();
                    sessionStorage.clear();
                    if (globalThis.caches) {
                        for (const key of await caches.keys()) await caches.delete(key);
                    }
                    if (indexedDB.databases) {
                        const databases = await indexedDB.databases();
                        await Promise.all(databases.filter(database => database.name).map(database =>
                            new Promise(resolve => {
                                const request = indexedDB.deleteDatabase(database.name);
                                request.onsuccess = request.onerror = request.onblocked = () => resolve();
                            })));
                    }
                    if (navigator.serviceWorker) {
                        for (const registration of await navigator.serviceWorker.getRegistrations()) {
                            await registration.unregister();
                        }
                    }
                    return "cleared";
                })()
                """);
            _sitePermissionStore.Reset(origin);
            SetHostListEntry(_settings.TemporaryCookieSites, uri.Host, enabled: false);
            SetHostListEntry(_settings.ThirdPartyCookieExceptions, uri.Host, enabled: false);
            _settings.Save();
            SitePermissionsHintText.Text =
                "Cookies, localStorage, IndexedDB, Cache Storage, service workers and saved permissions were reset.";
            UpdateSitePermissionsUi();
            await RefreshSitePrivacyDataAsync();
        }
        catch (Exception exception)
        {
            SitePermissionsHintText.Text = $"Could not reset all site data: {exception.Message}";
        }
        finally
        {
            ResetSiteDataButton.IsEnabled = true;
        }
    }

    private static async Task DeleteCookiesForOriginAsync(
        CoreWebView2CookieManager cookieManager,
        string origin)
    {
        IReadOnlyList<CoreWebView2Cookie> cookies = await cookieManager.GetCookiesAsync(origin);
        foreach (CoreWebView2Cookie cookie in cookies)
        {
            cookieManager.DeleteCookie(cookie);
        }
    }

    private async Task ClearConfiguredCookiesOnCloseAsync(
        CoreWebView2CookieManager cookieManager,
        bool clearNonExceptionCookies)
    {
        IReadOnlyList<CoreWebView2Cookie> cookies = await cookieManager.GetCookiesAsync(string.Empty);
        foreach (CoreWebView2Cookie cookie in cookies)
        {
            string host = cookie.Domain.Trim().TrimStart('.');
            bool temporary = IsTemporaryCookieHost(host);
            bool protectedByException = HostMatchesAny(host, _settings.PrivacySiteExceptions);
            if (temporary || clearNonExceptionCookies && !protectedByException)
            {
                cookieManager.DeleteCookie(cookie);
            }
        }
    }

    private async Task EnforceTemporaryCookiesForSiteAsync(CoreWebView2 core, Uri uri)
    {
        if (!IsTemporaryCookieHost(uri.Host))
        {
            return;
        }

        IReadOnlyList<CoreWebView2Cookie> cookies =
            await core.CookieManager.GetCookiesAsync(uri.GetLeftPart(UriPartial.Authority));
        foreach (CoreWebView2Cookie cookie in cookies.Where(cookie => !cookie.IsSession))
        {
            CoreWebView2Cookie temporary = core.CookieManager.CopyCookie(cookie);
            temporary.Expires = 0;
            core.CookieManager.AddOrUpdateCookie(temporary);
        }
    }

    private bool ShouldBlockThirdPartyCookie(Uri? documentUri, Uri requestUri)
    {
        if (!_settings.BlockThirdPartyCookies ||
            documentUri is null ||
            !IsBrowsableUri(documentUri) ||
            HostMatchesAny(documentUri.Host, _settings.ThirdPartyCookieExceptions) ||
            HostMatchesAny(requestUri.Host, _settings.ThirdPartyCookieExceptions))
        {
            return false;
        }

        return !GetCookieSiteKey(documentUri.Host)
            .Equals(GetCookieSiteKey(requestUri.Host), StringComparison.OrdinalIgnoreCase);
    }

    private static readonly ConcurrentDictionary<string, string> CookieSiteKeyCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static string GetCookieSiteKey(string host)
    {
        string normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        return CookieSiteKeyCache.GetOrAdd(normalized, static currentHost =>
        {
            string[] labels = currentHost.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (labels.Length <= 2)
            {
                return string.Join('.', labels);
            }

            bool compoundSuffix = labels[^1].Length == 2 &&
                labels[^2] is "co" or "com" or "net" or "org" or "gov" or "ac";
            int count = compoundSuffix ? 3 : 2;
            return string.Join('.', labels.Skip(Math.Max(0, labels.Length - count)));
        });
    }

    private static string FormatCookieExpiration(double unixSeconds)
    {
        try
        {
            return $"expires {DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds).LocalDateTime:g}";
        }
        catch (ArgumentOutOfRangeException)
        {
            return "persistent";
        }
    }

    private static void SetHostListEntry(List<string> entries, string host, bool enabled)
    {
        string normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        entries.RemoveAll(entry => entry.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (enabled)
        {
            entries.Add(normalized);
        }
    }
}
