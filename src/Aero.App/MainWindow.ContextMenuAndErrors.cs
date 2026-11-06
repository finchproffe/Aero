using Aero.Navigation;
using Aero.App.Media;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Net;
using Windows.ApplicationModel.DataTransfer;

namespace Aero.App;

public sealed partial class MainWindow
{
    private void Browser_ContextMenuRequested(
        BrowserTabState state,
        CoreWebView2ContextMenuRequestedEventArgs args)
    {
        if (!_tabs.Contains(state) || state.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        try
        {
            IList<CoreWebView2ContextMenuItem> items = args.MenuItems;
            if (items.Count > 0)
            {
                items.Add(core.Environment.CreateContextMenuItem(
                    string.Empty,
                    null!,
                    CoreWebView2ContextMenuItemKind.Separator));
            }

            Uri? pageUri = TryGetContextPageUri(args, state);
            if (pageUri is not null)
            {
                AddAeroContextMenuItem(
                    core,
                    items,
                    "Open page in new tab",
                    async () =>
                    {
                        await CreateTabAsync(pageUri, selectTab: true);
                    });

                AddAeroContextMenuItem(
                    core,
                    items,
                    "Copy page address",
                    () =>
                    {
                        CopyTextToClipboard(pageUri.AbsoluteUri);
                        return Task.CompletedTask;
                    });
            }

            if (args.ContextMenuTarget.HasLinkUri &&
                Uri.TryCreate(args.ContextMenuTarget.LinkUri, UriKind.Absolute, out Uri? linkUri) &&
                IsBrowsableUri(linkUri))
            {
                CoreWebView2ContextMenuItem containerMenu = core.Environment.CreateContextMenuItem(
                    Localization.AeroLocalizer.TranslateUi("Open link in container"),
                    null!,
                    CoreWebView2ContextMenuItemKind.Submenu);
                foreach (TabContainer container in TabContainers)
                {
                    string containerId = container.Id;
                    AddAeroContextMenuItem(
                        core,
                        containerMenu.Children,
                        Localization.AeroLocalizer.TranslateUi(container.Name),
                        () => OpenInContainerAsync(linkUri, containerId));
                }

                items.Add(containerMenu);
            }

            CoreWebView2ContextMenuTargetKind targetKind = args.ContextMenuTarget.Kind;
            if (targetKind is CoreWebView2ContextMenuTargetKind.Audio or CoreWebView2ContextMenuTargetKind.Video ||
                state.HasMedia)
            {
                AddAeroContextMenuItem(
                    core,
                    items,
                    "Download video with Aero",
                    () => DownloadMediaFromPageAsync(state, MediaDownloadKind.Video));

                AddAeroContextMenuItem(
                    core,
                    items,
                    "Download audio with Aero",
                    () => DownloadMediaFromPageAsync(state, MediaDownloadKind.Audio));
            }

            AddAeroContextMenuItem(
                core,
                items,
                "Save page as PDF",
                async () =>
                {
                    SelectTab(state);
                    await SaveCurrentPageAsPdfAsync();
                });

            AddAeroContextMenuItem(
                core,
                items,
                "Capture visible area",
                async () =>
                {
                    SelectTab(state);
                    await CapturePageAsync(fullPage: false);
                });

            AddAeroContextMenuItem(
                core,
                items,
                "Capture full page",
                async () =>
                {
                    SelectTab(state);
                    await CapturePageAsync(fullPage: true);
                });
        }
        catch
        {
            // A page can replace its context while the menu is being assembled.
        }
    }

    private void AddAeroContextMenuItem(
        CoreWebView2 core,
        IList<CoreWebView2ContextMenuItem> items,
        string label,
        Func<Task> action)
    {
        CoreWebView2ContextMenuItem item = core.Environment.CreateContextMenuItem(
            label,
            null!,
            CoreWebView2ContextMenuItemKind.Command);

        item.CustomItemSelected += (_, _) =>
        {
            RootGrid.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                }
                catch
                {
                    // Context commands are best-effort if the tab navigated meanwhile.
                }
            });
        };

        items.Add(item);
    }

    private static Uri? TryGetContextPageUri(
        CoreWebView2ContextMenuRequestedEventArgs args,
        BrowserTabState state)
    {
        if (Uri.TryCreate(args.ContextMenuTarget.PageUri, UriKind.Absolute, out Uri? contextUri) &&
            IsBrowsableUri(contextUri))
        {
            return contextUri;
        }

        Uri? stateUri = state.CurrentUri ?? state.PendingUri;
        return IsBrowsableUri(stateUri) ? stateUri : null;
    }

    private bool ShowNetworkErrorPage(
        BrowserTabState state,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        string status = args.WebErrorStatus.ToString();
        if (args.HttpStatusCode != 0 ||
            !NavigationCompletionPolicy.IsDefinitiveNetworkFailureStatus(status) ||
            state.Navigation.HasCommittedOrVerifiedDocument(args.NavigationId) ||
            string.Equals(status, "OperationCanceled", StringComparison.Ordinal) ||
            string.Equals(status, "ConnectionAborted", StringComparison.Ordinal) ||
            string.Equals(status, "ValidAuthenticationCredentialsRequired", StringComparison.Ordinal) ||
            string.Equals(status, "ValidProxyAuthenticationRequired", StringComparison.Ordinal) ||
            IsCertificateNavigationError(status))
        {
            // Only replace the page for a definitive transport failure. Unknown,
            // UnexpectedError, HTTP responses, committed documents, authentication
            // prompts and certificate interstitials remain owned by WebView2.
            return false;
        }

        Uri? failedUri = state.LastNavigationUri ?? state.CurrentUri ?? state.PendingUri;
        if (failedUri is null || !IsBrowsableUri(failedUri) || state.Browser.CoreWebView2 is null)
        {
            return false;
        }

        state.FailedNavigationUri = failedUri;
        state.CurrentUri = failedUri;
        state.IsShowingNetworkErrorPage = true;
        state.Title = "Connection problem";
        state.TitleText.Text = DisplayTabTitle("Connection problem");
        ToolTipService.SetToolTip(state.Header, failedUri.AbsoluteUri);

        string message = GetNetworkErrorMessage(status);
        state.Browser.NavigateToString(BuildNetworkErrorPageHtml(failedUri, message, status));
        return true;
    }


    private string BuildNetworkErrorPageHtml(Uri failedUri, string message, string status)
    {
        bool dark = IsDarkTheme();
        string background = GetChromeCssColor("AeroWindowBrush");
        string surface = GetChromeCssColor("AeroSurfaceBrush");
        string text = GetChromeCssColor("AeroTextBrush");
        string muted = GetChromeCssColor("AeroMutedTextBrush");
        string border = GetChromeCssColor("AeroSoftBorderBrush");
        string button = GetChromeCssColor("AeroAccentBrush");
        string buttonText = GetChromeCssColor("AeroOnAccentBrush");

        string safeUri = WebUtility.HtmlEncode(failedUri.AbsoluteUri);
        string safeMessage = WebUtility.HtmlEncode(message);
        string safeStatus = WebUtility.HtmlEncode(status);

        return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Connection problem</title>
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
    font-family: "Segoe UI", system-ui, sans-serif;
}
main {
    width: min(620px, 100%);
    padding: 34px;
    border: 1px solid {{border}};
    border-radius: 12px;
    background: {{surface}};
    box-shadow: 0 4px 20px rgba(0,0,0,.08);
}
.icon {
    width: 48px;
    height: 48px;
    display: grid;
    place-items: center;
    margin-bottom: 20px;
    border: 1px solid {{border}};
    border-radius: 14px;
    font-size: 22px;
}
h1 { margin: 0 0 10px; font-size: 27px; letter-spacing: -.02em; }
p { margin: 0; color: {{muted}}; line-height: 1.55; }
.url {
    margin-top: 18px;
    padding: 12px 14px;
    overflow: hidden;
    border: 1px solid {{border}};
    border-radius: 11px;
    color: {{muted}};
    font-size: 12px;
    text-overflow: ellipsis;
    white-space: nowrap;
}
.actions { display: flex; flex-wrap: wrap; gap: 9px; margin-top: 22px; }
button {
    min-height: 38px;
    padding: 0 17px;
    border: 1px solid {{border}};
    border-radius: 10px;
    background: transparent;
    color: {{text}};
    font: inherit;
    cursor: pointer;
}
button.primary { border-color: transparent; background: {{button}}; color: {{buttonText}}; font-weight: 600; }
button:focus-visible { outline: 2px solid {{button}}; outline-offset: 4px; }
button:hover { filter: brightness(1.08); }
@media(max-width:480px) { body { padding:16px; } main { padding:24px; } }
.details { margin-top: 22px; font-size: 12px; color: {{muted}}; }
</style>
</head>
<body>
<main>
    <div class="icon" aria-hidden="true">↻</div>
    <h1>This page could not be reached</h1>
    <p>{{safeMessage}}</p>
    <div class="url" title="{{safeUri}}">{{safeUri}}</div>
    <div class="actions">
        <button class="primary" onclick="send('retry')">Try again</button>
        <button onclick="send('home')">Aero Home</button>
        <button onclick="send('copy')">Copy address</button>
    </div>
    <div class="details">Network status: {{safeStatus}}</div>
</main>
<script>
function send(action) {
    chrome.webview.postMessage({ type: 'aero-network-error', action });
}
</script>
</body>
</html>
""";
    }


    private static bool IsCertificateNavigationError(string status) => status is
        "CertificateCommonNameIsIncorrect" or
        "CertificateExpired" or
        "ClientCertificateContainsErrors" or
        "CertificateRevoked" or
        "CertificateIsInvalid";

    private static string GetNetworkErrorMessage(string status) => status switch
    {
        "NameNotResolved" => "Aero could not find the server address. Check the spelling or your DNS connection.",
        "CannotConnect" => "The server refused the connection or is currently unavailable.",
        "ConnectionAborted" => "The connection was interrupted before the page finished loading.",
        "ConnectionReset" => "The server unexpectedly reset the connection.",
        "Disconnected" => "Your computer appears to be offline. Check the network connection.",
        "Timeout" => "The server took too long to respond.",
        "ServerUnreachable" => "The server could not be reached from this network.",
        _ => "Check your internet connection, firewall, proxy, or the website address, then try again."
    };

    private static void CopyTextToClipboard(string text)
    {
        try
        {
            DataPackage package = new();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch
        {
            // The Windows clipboard can be temporarily locked by another process.
        }
    }

    private void HandleNetworkErrorMessage(BrowserTabState state, string action)
    {
        if (!_tabs.Contains(state))
        {
            return;
        }

        switch (action)
        {
            case "retry":
                if (state.FailedNavigationUri is Uri retryUri)
                {
                    state.IsShowingNetworkErrorPage = false;
                    Navigate(state, retryUri);
                }
                break;
            case "home":
                state.IsShowingNetworkErrorPage = false;
                state.FailedNavigationUri = null;
                ShowAeroHomePage(state);
                if (ReferenceEquals(state, ActiveTab))
                {
                    UpdateActiveChrome();
                }
                break;
            case "copy":
                if (state.FailedNavigationUri is Uri copyUri)
                {
                    CopyTextToClipboard(copyUri.AbsoluteUri);
                }
                break;
        }
    }
}
