using Aero.WebView.Permissions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Aero.App;

public sealed partial class MainWindow
{
    private readonly SitePermissionService _coreSitePermissionService = new();
    private readonly SemaphoreSlim _coreSitePermissionMutationGate = new(1, 1);
    private readonly SemaphoreSlim _coreSitePermissionPromptGate = new(1, 1);
    private long _coreSitePermissionRefreshVersion;
    private bool _applyingCoreSitePermissionUi;

    private void InitializeCoreSitePermissionsUi()
    {
        ApplyUnavailableCoreSitePermissionUi();
    }

    private async void CoreSitePermissionsFlyout_Opened(object sender, object args)
    {
        _applyingCoreSitePermissionUi = true;
        try
        {
            SitePermissionsFlyout_Opened(sender, args);
        }
        finally
        {
            _applyingCoreSitePermissionUi = false;
        }

        await RefreshCoreSitePermissionsUiAsync();
    }

    private async void CoreSitePermissionComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (_applyingCoreSitePermissionUi ||
            sender is not ComboBox comboBox ||
            (!comboBox.IsDropDownOpen && comboBox.FocusState == FocusState.Unfocused) ||
            !SitePermissionService.TryGetPermissionKind(
                Convert.ToString(comboBox.Tag),
                out CoreWebView2PermissionKind permissionKind))
        {
            return;
        }

        BrowserTabState? state = ActiveTab;
        if (!TryGetCoreSiteContext(
                state,
                out CoreWebView2? core,
                out string? origin))
        {
            await RefreshCoreSitePermissionsUiAsync();
            return;
        }

        SitePermissionValue requestedState = comboBox.SelectedIndex switch
        {
            1 => SitePermissionValue.Allow,
            2 => SitePermissionValue.Block,
            _ => SitePermissionValue.Ask
        };

        await _coreSitePermissionMutationGate.WaitAsync();
        try
        {
            if (!IsSameCoreSiteContext(state!, core!, origin!))
            {
                await RefreshCoreSitePermissionsUiAsync();
                return;
            }

            await _coreSitePermissionService.SetStateAsync(
                core!.Profile,
                origin!,
                permissionKind,
                requestedState);
        }
        catch
        {
            if (IsSameCoreSiteContext(state!, core!, origin!))
            {
                SitePermissionsHintText.Text =
                    "Aero could not change this permission. The actual WebView2 profile state was restored.";
            }
        }
        finally
        {
            _coreSitePermissionMutationGate.Release();
        }

        await RefreshCoreSitePermissionsUiAsync();
    }

    private async void CoreResetSitePermissionsButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        _applyingCoreSitePermissionUi = true;
        try
        {
            ResetSitePermissionsButton_Click(sender, args);
        }
        finally
        {
            _applyingCoreSitePermissionUi = false;
        }

        BrowserTabState? state = ActiveTab;
        if (!TryGetCoreSiteContext(
                state,
                out CoreWebView2? core,
                out string? origin))
        {
            await RefreshCoreSitePermissionsUiAsync();
            return;
        }

        await _coreSitePermissionMutationGate.WaitAsync();
        try
        {
            if (!IsSameCoreSiteContext(state!, core!, origin!))
            {
                return;
            }

            await _coreSitePermissionService.ResetAsync(core!.Profile, origin!);
        }
        catch
        {
            if (IsSameCoreSiteContext(state!, core!, origin!))
            {
                SitePermissionsHintText.Text =
                    "Aero could not reset every WebView2 permission. The actual profile state was restored.";
            }
        }
        finally
        {
            _coreSitePermissionMutationGate.Release();
        }

        await RefreshCoreSitePermissionsUiAsync();
    }

    private void UpdateSitePermissionsUiWithProfileRefresh()
    {
        _applyingCoreSitePermissionUi = true;
        try
        {
            UpdateSitePermissionsUi();
        }
        finally
        {
            _applyingCoreSitePermissionUi = false;
        }

        QueueRefreshCoreSitePermissionsUi();
    }

    private void QueueRefreshCoreSitePermissionsUi()
    {
        _ = RefreshCoreSitePermissionsUiAsync();
    }

    private async Task RefreshCoreSitePermissionsUiAsync()
    {
        long refreshVersion = Interlocked.Increment(ref _coreSitePermissionRefreshVersion);
        BrowserTabState? state = ActiveTab;

        if (!TryGetCoreSiteContext(
                state,
                out CoreWebView2? core,
                out string? origin))
        {
            if (refreshVersion == Volatile.Read(ref _coreSitePermissionRefreshVersion))
            {
                ApplyUnavailableCoreSitePermissionUi();
            }

            return;
        }

        try
        {
            SitePermissionSnapshot snapshot =
                await _coreSitePermissionService.GetSnapshotAsync(core!.Profile, origin!);

            if (refreshVersion != Volatile.Read(ref _coreSitePermissionRefreshVersion) ||
                !IsSameCoreSiteContext(state!, core, snapshot.Origin))
            {
                return;
            }

            ApplyCoreSitePermissionSnapshot(snapshot);
        }
        catch
        {
            if (refreshVersion == Volatile.Read(ref _coreSitePermissionRefreshVersion) &&
                IsSameCoreSiteContext(state!, core!, origin!))
            {
                ApplyUnavailableCoreSitePermissionUi();
                SitePermissionsHintText.Text =
                    "Aero could not read the WebView2 permission profile for this site.";
            }
        }
    }

    private void ApplyCoreSitePermissionSnapshot(SitePermissionSnapshot snapshot)
    {
        _applyingCoreSitePermissionUi = true;
        try
        {
            foreach ((ComboBox comboBox, CoreWebView2PermissionKind permissionKind) in
                EnumerateCoreSitePermissionControls())
            {
                comboBox.IsEnabled = true;
                comboBox.SelectedIndex = snapshot.GetState(permissionKind) switch
                {
                    SitePermissionValue.Allow => 1,
                    SitePermissionValue.Block => 2,
                    _ => 0
                };
            }
        }
        finally
        {
            _applyingCoreSitePermissionUi = false;
        }

        string[] activePermissions = SitePermissionService.SupportedPermissionKinds
            .Where(kind => snapshot.GetState(kind) == SitePermissionValue.Allow)
            .Select(GetCorePermissionDisplayName)
            .ToArray();
        SiteActivePermissionsText.Text = activePermissions.Length == 0
            ? "Active WebView2 permissions: none"
            : $"Active WebView2 permissions: {string.Join(", ", activePermissions)}";
        SitePermissionsHintText.Text =
            "Camera, microphone, location, notifications, clipboard and automatic downloads use the WebView2 profile. Pop-ups and redirects use Aero controls.";
    }

    private void ApplyUnavailableCoreSitePermissionUi()
    {
        _applyingCoreSitePermissionUi = true;
        try
        {
            foreach ((ComboBox comboBox, _) in EnumerateCoreSitePermissionControls())
            {
                comboBox.SelectedIndex = 0;
                comboBox.IsEnabled = false;
            }
        }
        finally
        {
            _applyingCoreSitePermissionUi = false;
        }
    }

    private IEnumerable<(ComboBox ComboBox, CoreWebView2PermissionKind PermissionKind)>
        EnumerateCoreSitePermissionControls()
    {
        yield return (CameraPermissionComboBox, CoreWebView2PermissionKind.Camera);
        yield return (MicrophonePermissionComboBox, CoreWebView2PermissionKind.Microphone);
        yield return (LocationPermissionComboBox, CoreWebView2PermissionKind.Geolocation);
        yield return (NotificationsPermissionComboBox, CoreWebView2PermissionKind.Notifications);
        yield return (ClipboardReadPermissionComboBox, CoreWebView2PermissionKind.ClipboardRead);
        yield return (
            AutomaticDownloadsPermissionComboBox,
            CoreWebView2PermissionKind.MultipleAutomaticDownloads);
    }

    private static string GetCorePermissionDisplayName(
        CoreWebView2PermissionKind permissionKind) => permissionKind switch
    {
        CoreWebView2PermissionKind.Camera => "camera",
        CoreWebView2PermissionKind.Microphone => "microphone",
        CoreWebView2PermissionKind.Geolocation => "location",
        CoreWebView2PermissionKind.Notifications => "notifications",
        CoreWebView2PermissionKind.ClipboardRead => "clipboard read",
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => "automatic downloads",
        _ => "permission"
    };

    private static string GetCorePermissionRequestAction(
        CoreWebView2PermissionKind permissionKind) => permissionKind switch
    {
        CoreWebView2PermissionKind.Camera => "use your camera",
        CoreWebView2PermissionKind.Microphone => "use your microphone",
        CoreWebView2PermissionKind.Geolocation => "know your location",
        CoreWebView2PermissionKind.Notifications => "show notifications",
        CoreWebView2PermissionKind.ClipboardRead => "read your clipboard",
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => "download multiple files",
        _ => "use this permission"
    };

    private static string GetCorePermissionPromptTitle(
        CoreWebView2PermissionKind permissionKind) => permissionKind switch
    {
        CoreWebView2PermissionKind.Camera => "Use your camera?",
        CoreWebView2PermissionKind.Microphone => "Use your microphone?",
        CoreWebView2PermissionKind.Geolocation => "Know your location?",
        CoreWebView2PermissionKind.Notifications => "Show notifications?",
        CoreWebView2PermissionKind.ClipboardRead => "Read your clipboard?",
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => "Download multiple files?",
        _ => "Allow permission?"
    };

    private static bool TryGetCoreSiteContext(
        BrowserTabState? state,
        out CoreWebView2? core,
        out string? origin)
    {
        core = null;
        origin = null;
        if (state is null || state.IsClosing || state.IsDiscarded)
        {
            return false;
        }

        try
        {
            core = state.Browser.CoreWebView2;
            Uri? uri = state.CurrentUri ?? state.PendingUri;
            if (uri is null && Uri.TryCreate(core.Source, UriKind.Absolute, out Uri? sourceUri))
            {
                uri = sourceUri;
            }

            origin = SitePermissionService.NormalizeOrigin(uri);
            return origin is not null;
        }
        catch
        {
            core = null;
            origin = null;
            return false;
        }
    }

    private bool IsSameCoreSiteContext(
        BrowserTabState state,
        CoreWebView2 core,
        string origin)
    {
        if (!ReferenceEquals(ActiveTab, state) || !_tabs.Contains(state))
        {
            return false;
        }

        try
        {
            if (!ReferenceEquals(state.Browser.CoreWebView2, core))
            {
                return false;
            }

            return TryGetCoreSiteContext(state, out _, out string? currentOrigin) &&
                string.Equals(currentOrigin, origin, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async void Browser_CorePermissionRequested(
        BrowserTabState state,
        CoreWebView2 core,
        CoreWebView2PermissionRequestedEventArgs args)
    {
        if (!SitePermissionService.IsSupported(args.PermissionKind))
        {
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            args.SavesInProfile = false;
            string? requestOrigin = SitePermissionService.NormalizeOrigin(args.Uri);
            TryGetCoreSiteContext(state, out _, out string? documentOrigin);
            if (requestOrigin is null ||
                !IsSamePermissionRequestContext(state, core, documentOrigin))
            {
                ApplyOneTimePermissionResult(
                    args,
                    CoreWebView2PermissionState.Deny);
                return;
            }

            await _coreSitePermissionPromptGate.WaitAsync();
            try
            {
                if (!IsSamePermissionRequestContext(state, core, documentOrigin))
                {
                    ApplyOneTimePermissionResult(
                        args,
                        CoreWebView2PermissionState.Deny);
                    return;
                }

                PermissionPromptChoice choice = await ShowCorePermissionPromptAsync(
                    requestOrigin,
                    args.PermissionKind,
                    args.IsUserInitiated);

                if (!IsSamePermissionRequestContext(state, core, documentOrigin))
                {
                    ApplyOneTimePermissionResult(
                        args,
                        CoreWebView2PermissionState.Deny);
                    return;
                }

                args.State = choice.State;
                args.SavesInProfile = choice.SaveInProfile;
                args.Handled = true;

                if (choice.SaveInProfile &&
                    string.Equals(
                        requestOrigin,
                        documentOrigin,
                        StringComparison.OrdinalIgnoreCase))
                {
                    QueueRefreshCoreSitePermissionsUi();
                }
            }
            finally
            {
                _coreSitePermissionPromptGate.Release();
            }
        }
        catch
        {
            try
            {
                ApplyOneTimePermissionResult(
                    args,
                    CoreWebView2PermissionState.Deny);
            }
            catch
            {
                // The requesting tab may have been closed while the prompt was open.
            }
        }
        finally
        {
            try
            {
                deferral.Complete();
            }
            catch
            {
                // The WebView can be disposed while an asynchronous permission prompt is open.
            }
        }
    }

    private bool IsSamePermissionRequestContext(
        BrowserTabState state,
        CoreWebView2 core,
        string? originalDocumentOrigin)
    {
        if (!ReferenceEquals(ActiveTab, state) || !_tabs.Contains(state))
        {
            return false;
        }

        try
        {
            if (!ReferenceEquals(state.Browser.CoreWebView2, core))
            {
                return false;
            }

            TryGetCoreSiteContext(state, out _, out string? currentDocumentOrigin);
            return string.Equals(
                currentDocumentOrigin,
                originalDocumentOrigin,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<PermissionPromptChoice> ShowCorePermissionPromptAsync(
        string requestOrigin,
        CoreWebView2PermissionKind permissionKind,
        bool isUserInitiated)
    {
        CheckBox rememberChoice = new()
        {
            Content = "Remember this decision for this site",
            IsChecked = false
        };
        StackPanel content = new() { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = $"{requestOrigin} wants to {GetCorePermissionRequestAction(permissionKind)}.",
            TextWrapping = TextWrapping.Wrap
        });
        if (!isUserInitiated)
        {
            content.Children.Add(new TextBlock
            {
                Text = "This request was not triggered directly by a click or key press.",
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap
            });
        }
        content.Children.Add(rememberChoice);

        ContentDialog dialog = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = GetCorePermissionPromptTitle(permissionKind),
            Content = content,
            PrimaryButtonText = "Allow",
            SecondaryButtonText = "Block",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        ContentDialogResult result = await dialog.ShowAsync();
        bool saveInProfile = rememberChoice.IsChecked == true;
        return result switch
        {
            ContentDialogResult.Primary => new PermissionPromptChoice(
                CoreWebView2PermissionState.Allow,
                saveInProfile),
            ContentDialogResult.Secondary => new PermissionPromptChoice(
                CoreWebView2PermissionState.Deny,
                saveInProfile),
            _ => new PermissionPromptChoice(
                CoreWebView2PermissionState.Deny,
                SaveInProfile: false)
        };
    }

    private static void ApplyOneTimePermissionResult(
        CoreWebView2PermissionRequestedEventArgs args,
        CoreWebView2PermissionState state)
    {
        args.State = state;
        args.SavesInProfile = false;
        args.Handled = true;
    }

    private readonly record struct PermissionPromptChoice(
        CoreWebView2PermissionState State,
        bool SaveInProfile);
}
