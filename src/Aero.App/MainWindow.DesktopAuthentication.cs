using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Net;
using System.Net.Http;
using Windows.System;

namespace Aero.App;

public sealed partial class MainWindow
{
    private static readonly TimeSpan DesktopAuthenticationLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LoopbackCallbackRetryDelay = TimeSpan.FromMilliseconds(250);
    private const int LoopbackCallbackRetryCount = 28;
    private const int LoopbackCallbackRedirectLimit = 5;
    private const string DesktopAuthenticationPageAddress = "aero://desktop-authentication";

    private bool TryHandleDesktopAuthenticationNavigation(
        BrowserTabState state,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsDesktopAuthenticationContextActive(state) ||
            !Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? requestedUri))
        {
            return false;
        }

        bool isLoopbackCallback = IsLoopbackCallbackUri(requestedUri);
        bool isExternalApplicationScheme = IsExternalApplicationScheme(requestedUri);
        if (!isLoopbackCallback && !isExternalApplicationScheme)
        {
            return false;
        }

        // The callback contains a short-lived authorization code. Keep it out of
        // WebView2 history and complete the handoff from Aero's full-trust process.
        args.Cancel = true;

        if (state.ExternalAuthenticationCallbackInProgress)
        {
            return true;
        }

        state.ExternalAuthenticationCallbackInProgress = true;
        if (isLoopbackCallback)
        {
            _ = CompleteLoopbackAuthenticationAsync(state, requestedUri);
        }
        else
        {
            _ = CompleteExternalSchemeAuthenticationAsync(state, requestedUri);
        }

        return true;
    }

    private static bool IsDesktopAuthenticationContextActive(BrowserTabState state)
    {
        if (state.ExternalAuthenticationCompleted ||
            state.ExternalAuthenticationStartedAt is not DateTimeOffset startedAt)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - startedAt <= DesktopAuthenticationLifetime;
    }

    private static void InheritDesktopAuthenticationContext(
        BrowserTabState sourceState,
        BrowserTabState destinationState)
    {
        if (!IsDesktopAuthenticationContextActive(sourceState))
        {
            return;
        }

        destinationState.ExternalAuthenticationStartedAt =
            sourceState.ExternalAuthenticationStartedAt;
    }

    private static bool IsLoopbackCallbackUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (uri.IsLoopback)
        {
            return true;
        }

        return IsLoopbackHost(uri.IdnHost);
    }

    private static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string normalized = host.Trim().Trim('[', ']').TrimEnd('.');
        return normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(normalized, out IPAddress? address) &&
            IPAddress.IsLoopback(address);
    }

    private static bool IsExternalApplicationScheme(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        string scheme = uri.Scheme;
        return !scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase) &&
            !scheme.Equals("about", StringComparison.OrdinalIgnoreCase) &&
            !scheme.Equals("aero", StringComparison.OrdinalIgnoreCase) &&
            !scheme.Equals("blob", StringComparison.OrdinalIgnoreCase) &&
            !scheme.Equals("data", StringComparison.OrdinalIgnoreCase) &&
            !scheme.Equals("javascript", StringComparison.OrdinalIgnoreCase);
    }

    private async Task CompleteLoopbackAuthenticationAsync(
        BrowserTabState state,
        Uri callbackUri)
    {
        LoopbackRelayResult result = await RelayLoopbackCallbackAsync(callbackUri);
        if (!_tabs.Contains(state) || state.IsClosing)
        {
            return;
        }

        state.ExternalAuthenticationCallbackInProgress = false;
        if (!result.Delivered)
        {
            state.ExternalAuthenticationCompleted = true;
            ShowDesktopAuthenticationResult(
                state,
                success: false,
                detail: "The desktop application did not accept the local sign-in callback. " +
                "Return to the application and start sign-in again.");
            return;
        }

        state.ExternalAuthenticationCompleted = true;
        if (result.RedirectUri is Uri redirectUri)
        {
            if (IsExternalApplicationScheme(redirectUri))
            {
                bool launched = await TryLaunchExternalUriAsync(redirectUri);
                if (!_tabs.Contains(state) || state.IsClosing)
                {
                    return;
                }

                ShowDesktopAuthenticationResult(
                    state,
                    launched,
                    launched
                        ? "The sign-in result was returned to the desktop application."
                        : "The callback was delivered, but Windows could not open the application link.");
                return;
            }

            if (redirectUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                redirectUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                Navigate(state, redirectUri);
                return;
            }
        }

        ShowDesktopAuthenticationResult(
            state,
            success: true,
            detail: "The sign-in result was returned to the desktop application.");
    }

    private async Task CompleteExternalSchemeAuthenticationAsync(
        BrowserTabState state,
        Uri applicationUri)
    {
        bool launched = await TryLaunchExternalUriAsync(applicationUri);
        if (!_tabs.Contains(state) || state.IsClosing)
        {
            return;
        }

        state.ExternalAuthenticationCallbackInProgress = false;
        state.ExternalAuthenticationCompleted = true;
        ShowDesktopAuthenticationResult(
            state,
            launched,
            launched
                ? "The sign-in result was returned to the desktop application."
                : "Windows could not find an application registered for this sign-in link.");
    }

    private static async Task<bool> TryLaunchExternalUriAsync(Uri uri)
    {
        try
        {
            return await Launcher.LaunchUriAsync(uri);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<LoopbackRelayResult> RelayLoopbackCallbackAsync(Uri callbackUri)
    {
        using SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(1.5),
            UseCookies = false,
            UseProxy = false
        };
        using HttpClient client = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(2.5)
        };

        Uri currentUri = callbackUri;
        for (int redirectIndex = 0;
             redirectIndex < LoopbackCallbackRedirectLimit;
             redirectIndex++)
        {
            HttpResponseMessage? response = null;
            for (int attempt = 0; attempt < LoopbackCallbackRetryCount; attempt++)
            {
                try
                {
                    using HttpRequestMessage request = new(HttpMethod.Get, currentUri);
                    request.Headers.TryAddWithoutValidation(
                        "User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Aero/0.26.0.0");
                    request.Headers.TryAddWithoutValidation(
                        "Accept",
                        "text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");
                    request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");

                    response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead);
                    break;
                }
                catch (HttpRequestException) when (attempt + 1 < LoopbackCallbackRetryCount)
                {
                    await Task.Delay(LoopbackCallbackRetryDelay);
                }
                catch (TaskCanceledException) when (attempt + 1 < LoopbackCallbackRetryCount)
                {
                    await Task.Delay(LoopbackCallbackRetryDelay);
                }
                catch
                {
                    return new LoopbackRelayResult(false, null);
                }
            }

            if (response is null)
            {
                return new LoopbackRelayResult(false, null);
            }

            using (response)
            {
                int statusCode = (int)response.StatusCode;
                Uri? location = response.Headers.Location;
                if (statusCode is >= 300 and < 400 && location is not null)
                {
                    Uri redirectUri = location.IsAbsoluteUri
                        ? location
                        : new Uri(currentUri, location);
                    if (IsLoopbackCallbackUri(redirectUri))
                    {
                        currentUri = redirectUri;
                        continue;
                    }

                    return new LoopbackRelayResult(true, redirectUri);
                }

                // Any HTTP response proves that the local listener received the
                // authorization payload. The desktop application owns the status code.
                return new LoopbackRelayResult(true, null);
            }
        }

        return new LoopbackRelayResult(true, null);
    }

    private void ShowDesktopAuthenticationResult(
        BrowserTabState state,
        bool success,
        string detail)
    {
        state.InternalPageAddress = DesktopAuthenticationPageAddress;
        state.PendingUri = null;
        state.PendingStartPage = false;
        state.IsStartPage = false;
        state.IsShowingNetworkErrorPage = false;
        state.FailedNavigationUri = null;
        state.IsLoading = false;
        StopLoadingAnimation(state);

        string title = success ? "Sign-in complete" : "Sign-in could not be completed";
        state.Title = title;
        state.TitleText.Text = title;
        ToolTipService.SetToolTip(state.Header, title);
        state.Browser.NavigateToString(BuildDesktopAuthenticationResultHtml(success, detail));

        if (ReferenceEquals(state, ActiveTab))
        {
            UpdateActiveChrome();
            Title = GetAeroWindowTitle(title);
        }

        SaveSessionSnapshot();
    }

    private string BuildDesktopAuthenticationResultHtml(bool success, string detail)
    {
        bool dark = IsDarkTheme();
        bool russian = _settings.AppLanguage.Equals("ru", StringComparison.OrdinalIgnoreCase);
        string background = dark ? "#111518" : "#f3f5f6";
        string surface = dark ? "#1b2125" : "#ffffff";
        string text = dark ? "#f1f3f4" : "#1c2125";
        string muted = dark ? "#9aa5ac" : "#65717a";
        string border = dark ? "#30383e" : "#d8dde1";
        string accent = success ? "#43c781" : "#ef6a6a";
        string heading = success
            ? russian ? "Вход завершён" : "Sign-in complete"
            : russian ? "Не удалось завершить вход" : "Sign-in could not be completed";
        string message = success
            ? russian
                ? "Результат входа передан обратно в приложение. Можно вернуться в него."
                : "The sign-in result was returned to the application. You can go back to it."
            : russian
                ? "Локальное приложение не приняло результат входа. Вернитесь в него и повторите попытку."
                : "The local application did not accept the sign-in result. Return to it and try again.";

        string safeHeading = WebUtility.HtmlEncode(heading);
        string safeMessage = WebUtility.HtmlEncode(message);
        string safeDetail = WebUtility.HtmlEncode(detail);

        return $$"""
<!doctype html>
<html lang="{{(russian ? "ru" : "en")}}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{safeHeading}}</title>
<style>
:root { color-scheme: {{(dark ? "dark" : "light")}}; }
* { box-sizing: border-box; }
body {
    margin: 0;
    min-height: 100vh;
    display: grid;
    place-items: center;
    padding: 32px;
    background: {{background}};
    color: {{text}};
    font-family: "Segoe UI Variable", "Segoe UI", system-ui, sans-serif;
}
main {
    width: min(610px, 100%);
    padding: 36px;
    border: 1px solid {{border}};
    border-radius: 20px;
    background: {{surface}};
    box-shadow: 0 24px 70px rgba(0,0,0,.18);
}
.icon {
    width: 54px;
    height: 54px;
    display: grid;
    place-items: center;
    margin-bottom: 22px;
    border-radius: 50%;
    border: 2px solid {{accent}};
    color: {{accent}};
    font-size: 28px;
    font-weight: 700;
}
h1 { margin: 0 0 12px; font-size: 28px; line-height: 1.2; }
p { margin: 0; color: {{muted}}; font-size: 16px; line-height: 1.6; }
.detail { margin-top: 16px; font-size: 13px; opacity: .78; }
</style>
</head>
<body>
<main>
    <div class="icon">{{(success ? "✓" : "!")}}</div>
    <h1>{{safeHeading}}</h1>
    <p>{{safeMessage}}</p>
    <p class="detail">{{safeDetail}}</p>
</main>
</body>
</html>
""";
    }

    private readonly record struct LoopbackRelayResult(
        bool Delivered,
        Uri? RedirectUri);
}
