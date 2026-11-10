using System.Text;
using System.Text.Json;
using Aero.Downloads;
using Microsoft.Web.WebView2.Core;
using Windows.Security.Credentials.UI;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Aero.App;

// Exports every cookie of the current profile as Netscape cookies.txt (yt-dlp, curl, wget,
// most "import cookies" extensions) or Cookie-Editor JSON (Chrome/Firefox extensions).
// A cookie file signs anyone into these accounts, so Windows Hello confirms the export.
public sealed partial class MainWindow
{
    private async Task ExportCookiesAsync(BrowserTabState state, string format)
    {
        bool json = format == "json";
        if (state.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        if (!await ConfirmCookieExportAsync())
        {
            await PostSettingsStatusAsync(state, "Cookie export cancelled: Windows Hello did not confirm it.", isError: true);
            return;
        }

        // Cookies held in the Hello-protected vault only reach WebView2 after it is unlocked.
        await EnsureCookieVaultRestoredAsync(state);
        IReadOnlyList<CoreWebView2Cookie> cookies = await core.CookieManager.GetCookiesAsync(string.Empty);

        FileSavePicker picker = new()
        {
            SuggestedFileName = $"Aero-cookies-{DateTime.Now:yyyy-MM-dd}"
        };
        if (json)
        {
            picker.FileTypeChoices.Add("Cookie-Editor JSON", [".json"]);
        }
        else
        {
            picker.FileTypeChoices.Add("Netscape cookies.txt", [".txt"]);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        string content = json ? FormatCookieEditorJson(cookies) : NetscapeCookieFile.Format(cookies.Select(static cookie => new ExportedCookie(
            cookie.Domain,
            cookie.Path,
            cookie.IsSecure,
            cookie.IsHttpOnly,
            cookie.IsSession ? null : cookie.Expires,
            cookie.Name,
            cookie.Value)));
        await File.WriteAllTextAsync(file.Path, content, new UTF8Encoding(false));
        await PostSettingsStatusAsync(state, $"{cookies.Count} cookies exported to {file.Name}. Keep this file private: it signs anyone into your accounts.");
    }

    private async Task<bool> ConfirmCookieExportAsync()
    {
        try
        {
            if (await UserConsentVerifier.CheckAvailabilityAsync() != UserConsentVerifierAvailability.Available)
            {
                // No Hello on this PC: the Windows account itself is the boundary, as for the profile files.
                return true;
            }

            UserConsentVerificationResult result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(
                WinRT.Interop.WindowNative.GetWindowHandle(this),
                Localization.AeroLocalizer.TranslateUi("Export all cookies from Aero?"));
            return result == UserConsentVerificationResult.Verified;
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
    }

    // The Cookie-Editor / EditThisCookie layout most browser cookie extensions import.
    internal static string FormatCookieEditorJson(IEnumerable<CoreWebView2Cookie> cookies) =>
        JsonSerializer.Serialize(cookies.Select(static cookie => new Dictionary<string, object?>
        {
            ["domain"] = cookie.Domain,
            ["expirationDate"] = cookie.IsSession ? null : cookie.Expires,
            ["hostOnly"] = !cookie.Domain.StartsWith('.'),
            ["httpOnly"] = cookie.IsHttpOnly,
            ["name"] = cookie.Name,
            ["path"] = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path,
            ["sameSite"] = cookie.SameSite switch
            {
                CoreWebView2CookieSameSiteKind.Strict => "strict",
                CoreWebView2CookieSameSiteKind.Lax => "lax",
                _ => "no_restriction"
            },
            ["secure"] = cookie.IsSecure,
            ["session"] = cookie.IsSession,
            ["storeId"] = null,
            ["value"] = cookie.Value
        }), new JsonSerializerOptions { WriteIndented = true });
}
