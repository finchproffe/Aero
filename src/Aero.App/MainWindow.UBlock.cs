using Aero.App.Data;
using Aero.App.Speed;
using Aero.App.Settings;
using Aero.Extensions;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace Aero.App;

public sealed partial class MainWindow
{
    private readonly UBlockOriginBootstrapper _uBlockOrigin = new();
    private bool _uBlockInstallStarted;
    private readonly SemaphoreSlim _shieldEngineStateGate = new(1, 1);

    private void InitializeUBlockOriginIntegration()
    {
        _uBlockOrigin.StateChanged += UBlockOrigin_StateChanged;
    }

    private void EnsureUBlockOriginForProfile(CoreWebView2Profile profile)
    {
        if (_uBlockInstallStarted ||
            !_settings.BlockAdsAndTrackers ||
            _settings.ShieldEngineMode == ShieldEngineMode.Native)
        {
            return;
        }

        _uBlockInstallStarted = true;
        _ = InstallUBlockOriginAsync(profile, forceRedownload: false);
    }

    private async Task InstallUBlockOriginAsync(
        CoreWebView2Profile profile,
        bool forceRedownload)
    {
        try
        {
            await _uBlockOrigin.EnsureInstalledAsync(
                profile,
                _extensionRegistry,
                _shieldCancellation.Token,
                forceRedownload);

            if (_uBlockOrigin.IsReady)
            {
                await RefreshInstalledExtensionsAsync();
                await RefreshExtensionToolbarAsync(refreshProfile: false);

                if (ActiveTab is BrowserTabState activeTab &&
                    activeTab.Browser.CoreWebView2 is not null)
                {
                    try
                    {
                        await activeTab.Browser.CoreWebView2.ExecuteScriptAsync(
                            "document.getElementById('aero-shield-cosmetic-style')?.remove();");
                    }
                    catch
                    {
                        // The page can be navigating while the engine switches.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Window shutdown cancels background installation quietly.
        }
        finally
        {
            RootGrid.DispatcherQueue.TryEnqueue(UpdateShieldUi);
        }
    }

    private void UBlockOrigin_StateChanged(object? sender, EventArgs e)
    {
        RootGrid.DispatcherQueue.TryEnqueue(() =>
        {
            UpdateShieldUi();
            UpdateSiteIndicator(ActiveTab?.CurrentUri ?? ActiveTab?.PendingUri);
        });
    }

    private async void OpenUBlockControlsButton_Click(object sender, RoutedEventArgs e)
    {
        CoreWebView2BrowserExtension? extension = _uBlockOrigin.Extension;
        if (extension is null || !extension.IsEnabled)
        {
            await RetryUBlockInstallAsync(forceRedownload: false);
            return;
        }

        ExtensionPackage? package = GetManagedExtensionPackage(extension);
        string actionPage = package?.ActionPage ?? "popup-fenix.html";
        if (!Uri.TryCreate(
                $"chrome-extension://{extension.Id}/{actionPage.TrimStart('/')}",
                UriKind.Absolute,
                out Uri? popupUri))
        {
            await ShowErrorDialogAsync(
                "Could not open uBlock Origin",
                "The extension popup address was invalid.");
            return;
        }

        ShieldFlyout.Hide();
        await ShowExtensionPopupAsync(extension, popupUri, ShieldButton);
    }

    private async void RetryUBlockButton_Click(object sender, RoutedEventArgs e) =>
        await RetryUBlockInstallAsync(forceRedownload: true);

    private async Task RetryUBlockInstallAsync(bool forceRedownload)
    {
        CoreWebView2Profile? profile = GetActiveProfile();
        if (profile is null)
        {
            await ShowErrorDialogAsync(
                "Aero Shield is not ready",
                "Open a browser tab first, then try installing the uBlock Origin engine again.");
            return;
        }

        RetryUBlockButton.IsEnabled = false;
        _uBlockInstallStarted = true;
        await InstallUBlockOriginAsync(profile, forceRedownload);
        RetryUBlockButton.IsEnabled = true;
    }

    private bool ShouldEnableAdvancedShieldForUri(Uri? uri) =>
        _settings.BlockAdsAndTrackers &&
        _settings.ShieldEngineMode != ShieldEngineMode.Native;

    private bool NeedsAdvancedShieldSyncForUri(Uri? uri) =>
        _uBlockOrigin.Extension is CoreWebView2BrowserExtension extension &&
        extension.IsEnabled != ShouldEnableAdvancedShieldForUri(uri);

    private async Task SyncAdvancedShieldWithoutBlockingNavigationAsync(
        BrowserTabState state,
        Uri uri,
        ulong navigationId)
    {
        try
        {
            // Extension enable/disable is profile-wide, not site-specific. Never
            // toggle uBlock from the current host allowlist; doing that made the
            // global Shield switch drift out of sync across tabs. Synchronize only
            // after navigation has settled so Chromium is not interrupted mid-commit.
            await Task.Yield();
            if (state.IsLoading ||
                state.IsShowingNetworkErrorPage ||
                state.ActiveNavigationId != navigationId)
            {
                return;
            }

            if (NeedsAdvancedShieldSyncForUri(uri))
            {
                await SyncAdvancedShieldForUriAsync(uri);
            }
        }
        catch
        {
            // Shield state is secondary to navigation. Never cancel, restart, or
            // fail a page because the extension was still changing state.
        }
    }

    private async Task SyncAdvancedShieldForUriAsync(Uri? uri)
    {
        await _shieldEngineStateGate.WaitAsync();
        try
        {
            bool shouldEnable = ShouldEnableAdvancedShieldForUri(uri);
            CoreWebView2BrowserExtension? extension = _uBlockOrigin.Extension;

            if (extension is null)
            {
                if (shouldEnable &&
                    (!_uBlockInstallStarted ||
                     _uBlockOrigin.State is UBlockOriginState.Idle or UBlockOriginState.Failed))
                {
                    CoreWebView2Profile? profile = GetActiveProfile();
                    if (profile is not null)
                    {
                        _uBlockInstallStarted = true;
                        await InstallUBlockOriginAsync(profile, forceRedownload: false);
                    }
                }

                return;
            }

            if (extension.IsEnabled != shouldEnable)
            {
                await _uBlockOrigin.SetEnabledAsync(shouldEnable);
            }
        }
        finally
        {
            _shieldEngineStateGate.Release();
        }
    }

    private bool IsUsingUBlockOriginEngine() => _uBlockOrigin.IsReady;

    private void UpdateUBlockOriginUi()
    {
        bool ready = _uBlockOrigin.IsReady;
        bool busy = _uBlockOrigin.State is
            UBlockOriginState.Checking or
            UBlockOriginState.Downloading or
            UBlockOriginState.Installing;

        UBlockEngineStatusText.Text = _uBlockOrigin.StatusText;
        UBlockEngineBadgeText.Text = _settings.ShieldEngineMode switch
        {
            ShieldEngineMode.Native => "Native",
            ShieldEngineMode.Advanced when ready => $"uBO {UBlockOriginBootstrapper.Version}",
            ShieldEngineMode.Advanced => busy ? "Installing" : "Fallback",
            _ when ready => "Hybrid",
            _ => busy ? "Starting hybrid" : "Fallback"
        };

        OpenUBlockControlsButton.Visibility = ready &&
            _settings.ShieldEngineMode != ShieldEngineMode.Native
            ? Visibility.Visible
            : Visibility.Collapsed;
        RetryUBlockButton.Visibility = _settings.ShieldEngineMode != ShieldEngineMode.Native &&
            _uBlockOrigin.State == UBlockOriginState.Failed
            ? Visibility.Visible
            : Visibility.Collapsed;
        RetryUBlockButton.IsEnabled = !busy;

        NativeShieldControls.Visibility = Visibility.Visible;
        SiteShieldToggle.Visibility = Visibility.Visible;

        UBlockEngineHelpText.Text = _settings.ShieldEngineMode switch
        {
            ShieldEngineMode.Native => "Only Aero's native EasyList/EasyPrivacy engine is used.",
            ShieldEngineMode.Advanced when ready => "uBlock Origin handles network, cosmetic and scriptlet filtering. Native filtering remains available as an automatic fallback.",
            ShieldEngineMode.Advanced => "Aero is using the native fallback until uBlock Origin is ready.",
            _ when ready => "Aero blocks high-confidence trackers before WebView2 hands the request to uBlock Origin. uBO handles the full rule set and scriptlets.",
            _ => "Aero's native engine protects the page while the advanced uBlock Origin layer starts."
        };
    }
}
