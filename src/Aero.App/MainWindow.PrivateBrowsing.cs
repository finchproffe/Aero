using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Aero.WebView;

namespace Aero.App;

public sealed partial class MainWindow
{
    private const string PrivateProfileName = "AeroPrivate";
    private static int _stalePrivateProfileCleanupStarted;

    private readonly bool _isPrivateWindow;
    private readonly string? _privateUserDataFolder;

    private static string CreatePrivateUserDataFolderPath()
    {
        string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Aero",
            "Private");

        return System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
    }

    private string GetWebViewUserDataFolder()
    {
        CleanupStalePrivateProfiles();

        if (_isPrivateWindow && !string.IsNullOrWhiteSpace(_privateUserDataFolder))
        {
            return _privateUserDataFolder;
        }

        return _browserProfile.WebViewDataFolder;
    }

    private async Task EnsureWebViewInitializedAsync(BrowserTabState state)
    {
        if (_environment is null)
        {
            throw new InvalidOperationException("WebView2 environment is not ready.");
        }

        await WebViewControllerInitializer.EnsureInitializedAsync(
            state.Browser,
            _environment,
            GetWebViewProfileOptions(state));
    }

    private void ConfigurePrivateWindowChrome()
    {
        PrivateModeBadge.Visibility = _isPrivateWindow || _browserProfile.IsGuest
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProfileModeBadgeText.Text = _isPrivateWindow ? "Private" : "Guest";

        Title = GetAeroWindowTitle();
    }

    private string GetAeroWindowTitle(string? pageTitle = null)
    {
        string aeroTitle = _isPrivateWindow
            ? $"Aero Private · {_browserProfile.Name}"
            : _browserProfile.IsGuest
                ? "Aero Guest"
                : _browserProfile.IsDefault
                    ? "Aero"
                    : $"Aero · {_browserProfile.Name}";
        if (string.IsNullOrWhiteSpace(pageTitle) ||
            pageTitle.Equals("Aero", StringComparison.OrdinalIgnoreCase) ||
            pageTitle.Equals("New tab", StringComparison.OrdinalIgnoreCase))
        {
            return aeroTitle;
        }

        return $"{pageTitle} — {aeroTitle}";
    }

    private void PreparePrivateProfileCleanup()
    {
        if (!_isPrivateWindow ||
            _environment is null ||
            string.IsNullOrWhiteSpace(_privateUserDataFolder))
        {
            return;
        }

        if (OpenWindows.Any(window =>
            !ReferenceEquals(window, this) &&
            window._isPrivateWindow &&
            string.Equals(
                window._privateUserDataFolder,
                _privateUserDataFolder,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        CoreWebView2Environment environment = _environment;
        string profileFolder = _privateUserDataFolder;

        void BrowserProcessExitedHandler(
            CoreWebView2Environment sender,
            CoreWebView2BrowserProcessExitedEventArgs args)
        {
            sender.BrowserProcessExited -= BrowserProcessExitedHandler;
            _ = DeletePrivateProfileFolderAsync(profileFolder, initialDelayMilliseconds: 100);
        }

        environment.BrowserProcessExited += BrowserProcessExitedHandler;
        _ = DeletePrivateProfileIfNoProcessAsync(environment, profileFolder);
    }

    private static async Task DeletePrivateProfileIfNoProcessAsync(
        CoreWebView2Environment environment,
        string profileFolder)
    {
        await Task.Delay(1000);

        try
        {
            if (environment.GetProcessInfos().Count == 0)
            {
                await DeletePrivateProfileFolderAsync(
                    profileFolder,
                    initialDelayMilliseconds: 0);
            }
        }
        catch
        {
            // BrowserProcessExited remains the authoritative cleanup path.
        }
    }

    private static async Task DeletePrivateProfileFolderAsync(
        string profileFolder,
        int initialDelayMilliseconds)
    {
        if (initialDelayMilliseconds > 0)
        {
            await Task.Delay(initialDelayMilliseconds);
        }

        for (int attempt = 0; attempt < 24; attempt++)
        {
            try
            {
                if (!Directory.Exists(profileFolder))
                {
                    return;
                }

                Directory.Delete(profileFolder, recursive: true);
                TryDeleteEmptyPrivateRoot(System.IO.Path.GetDirectoryName(profileFolder));
                return;
            }
            catch (IOException)
            {
                // WebView2 can hold files briefly while its browser process exits.
            }
            catch (UnauthorizedAccessException)
            {
                // Antivirus or the runtime can briefly keep profile files locked.
            }

            await Task.Delay(Math.Min(2000, 150 + (attempt * 100)));
        }
    }

    private static void CleanupStalePrivateProfiles()
    {
        if (Interlocked.Exchange(ref _stalePrivateProfileCleanupStarted, 1) != 0)
        {
            return;
        }

        string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Aero",
            "Private");

        if (!Directory.Exists(root))
        {
            return;
        }

        DateTime staleBefore = DateTime.UtcNow.AddDays(-2);
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(folder) < staleBefore)
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch
            {
                // A folder can belong to another running Aero process.
            }
        }

        TryDeleteEmptyPrivateRoot(root);
    }

    private static void TryDeleteEmptyPrivateRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(root).Any())
            {
                Directory.Delete(root);
            }
        }
        catch
        {
            // The root is shared by private windows and can remain harmlessly.
        }
    }
}
