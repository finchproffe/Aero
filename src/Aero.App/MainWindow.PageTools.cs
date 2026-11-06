using Aero.App.Data;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace Aero.App;

public sealed partial class MainWindow
{
    private static readonly double[] PageZoomSteps =
    [
        0.25, 0.33, 0.50, 0.67, 0.75, 0.80, 0.90, 1.00,
        1.10, 1.25, 1.50, 1.75, 2.00, 2.50, 3.00, 4.00, 5.00
    ];

    private const string PageZoomBridgeScript = """
(() => {
    if (window.__aeroPageToolsInstalled) return;
    window.__aeroPageToolsInstalled = true;

    window.addEventListener('keydown', event => {
        if (!event.ctrlKey || event.altKey || event.metaKey) return;

        let action = null;
        if (event.key === '+' || event.key === '=') action = 'in';
        else if (event.key === '-' || event.key === '_') action = 'out';
        else if (event.key === '0') action = 'reset';
        if (!action) return;

        event.preventDefault();
        event.stopImmediatePropagation();
        chrome.webview.postMessage({ type: 'aero-page-zoom', action });
    }, true);

    let lastWheelZoomAt = 0;
    window.addEventListener('wheel', event => {
        if (!event.ctrlKey || event.altKey || event.metaKey || event.deltaY === 0) return;

        event.preventDefault();
        event.stopImmediatePropagation();

        const now = performance.now();
        if (now - lastWheelZoomAt < 85) return;
        lastWheelZoomAt = now;

        chrome.webview.postMessage({
            type: 'aero-page-zoom',
            action: event.deltaY < 0 ? 'in' : 'out'
        });
    }, { capture: true, passive: false });
})();
""";

    private const string MicrophoneSelectionBridgeScript = """
(() => {
    if (window.__aeroMicrophoneSelectionInstalled) return;
    const mediaDevices = navigator.mediaDevices;
    if (!mediaDevices || typeof mediaDevices.getUserMedia !== "function") return;

    window.__aeroMicrophoneSelectionInstalled = true;
    window.__aeroPreferredMicrophoneName = "";
    const originalGetUserMedia = mediaDevices.getUserMedia.bind(mediaDevices);
    const normalize = value => String(value || "")
        .toLocaleLowerCase()
        .replace(/\s+/g, " ")
        .trim();

    mediaDevices.getUserMedia = async constraints => {
        const audio = constraints && constraints.audio;
        const explicitDevice = audio && typeof audio === "object" && audio.deviceId;
        if (!audio || explicitDevice) {
            return originalGetUserMedia(constraints);
        }

        // The first request triggers Aero's native permission prompt. Once the
        // user chooses a microphone there, Aero writes the preferred device name
        // into this document before allowing the request to continue.
        const bootstrapStream = await originalGetUserMedia(constraints);
        const preferredName = normalize(window.__aeroPreferredMicrophoneName);
        if (!preferredName) {
            return bootstrapStream;
        }

        try {
            const devices = await mediaDevices.enumerateDevices();
            const microphones = devices.filter(device => device.kind === "audioinput");
            const selected = microphones.find(device => normalize(device.label) === preferredName) ||
                microphones.find(device => normalize(device.label).includes(preferredName)) ||
                microphones.find(device => preferredName.includes(normalize(device.label)));
            if (!selected || !selected.deviceId) {
                return bootstrapStream;
            }

            const requestedAudio = typeof audio === "object"
                ? { ...audio, deviceId: { exact: selected.deviceId } }
                : { deviceId: { exact: selected.deviceId } };
            const selectedStream = await originalGetUserMedia({ ...constraints, audio: requestedAudio });
            bootstrapStream.getTracks().forEach(track => track.stop());
            return selectedStream;
        } catch {
            return bootstrapStream;
        }
    };
})()
""";

    private readonly SemaphoreSlim _permissionPromptGate = new(1, 1);
    private readonly DispatcherTimer _zoomToastTimer = new() { Interval = TimeSpan.FromSeconds(2.2) };
    private bool _updatingPermissionUi;
    private bool _pageCaptureInProgress;

    private void InitializePageToolsFeatures()
    {
        _zoomToastTimer.Tick += (_, _) => HidePageZoomIndicator();
    }

    private async void Browser_PermissionRequested(
        BrowserTabState state,
        CoreWebView2PermissionRequestedEventArgs args)
    {
        if (!TryMapPermissionKind(args.PermissionKind, out SitePermissionKind permissionKind))
        {
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            args.SavesInProfile = false;

            if (permissionKind == SitePermissionKind.Microphone &&
                state.Browser.CoreWebView2 is CoreWebView2 microphoneCore)
            {
                await EnsureMicrophoneSelectionBridgeAsync(state, microphoneCore);
            }

            string? origin = SitePermissionStore.NormalizeOrigin(args.Uri);
            if (origin is null)
            {
                args.State = CoreWebView2PermissionState.Deny;
                return;
            }

            SitePermissionDecision storedDecision = _sitePermissionStore.GetDecision(origin, permissionKind);
            if (storedDecision == SitePermissionDecision.Ask)
            {
                storedDecision = GetDefaultPermissionDecision(permissionKind);
            }
            if (storedDecision != SitePermissionDecision.Ask)
            {
                if (permissionKind == SitePermissionKind.Microphone &&
                    storedDecision == SitePermissionDecision.Allow)
                {
                    await ApplyPreferredMicrophoneChoiceAsync(
                        state,
                        _settings.PreferredMicrophoneName);
                }

                args.State = ToWebViewPermissionState(storedDecision);
                return;
            }

            await _permissionPromptGate.WaitAsync();
            try
            {
                PermissionPromptResult result = await ShowPermissionPromptAsync(
                    state,
                    origin,
                    permissionKind,
                    args.IsUserInitiated);

                if (permissionKind == SitePermissionKind.Microphone &&
                    result.Decision == SitePermissionDecision.Allow)
                {
                    _settings.PreferredMicrophoneName = result.PreferredMicrophoneName ?? string.Empty;
                    _settings.Save();
                    await ApplyPreferredMicrophoneChoiceAsync(
                        state,
                        _settings.PreferredMicrophoneName);
                }

                args.State = ToWebViewPermissionState(result.Decision);
                if (result.Remember && result.Decision != SitePermissionDecision.Ask)
                {
                    _sitePermissionStore.SetDecision(origin, permissionKind, result.Decision);
                }
            }
            finally
            {
                _permissionPromptGate.Release();
            }

            if (ReferenceEquals(state, ActiveTab))
            {
                UpdateSitePermissionsUi();
            }
        }
        catch
        {
            args.State = CoreWebView2PermissionState.Deny;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task<PermissionPromptResult> ShowPermissionPromptAsync(
        BrowserTabState state,
        string origin,
        SitePermissionKind permissionKind,
        bool isUserInitiated)
    {
        string permissionName = GetPermissionDisplayName(permissionKind);
        ComboBox? microphoneSelector = permissionKind == SitePermissionKind.Microphone
            ? await BuildMicrophoneSelectorAsync()
            : null;
        CheckBox rememberChoice = new()
        {
            IsChecked = true,
            Content = _isPrivateWindow
                ? "Remember until this private window closes"
                : "Remember for this site"
        };

        StackPanel content = new() { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = origin,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = isUserInitiated
                ? $"This site wants to use your {permissionName.ToLowerInvariant()}."
                : $"This site requested your {permissionName.ToLowerInvariant()} without a recent click or key press.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Tab: {state.Title}",
            Foreground = GetMutedTextBrush(),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        if (microphoneSelector is not null)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Microphone",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 3, 0, -4)
            });
            content.Children.Add(microphoneSelector);
            content.Children.Add(new TextBlock
            {
                Text = "Aero will ask the page to use this input device. Sites that explicitly choose their own device keep control.",
                Foreground = GetMutedTextBrush(),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
        }
        content.Children.Add(rememberChoice);

        ContentDialog dialog = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"Allow {permissionName.ToLowerInvariant()}?",
            Content = content,
            PrimaryButtonText = "Allow",
            SecondaryButtonText = "Block",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Close
        };

        ContentDialogResult dialogResult = await dialog.ShowAsync();
        SitePermissionDecision decision = dialogResult switch
        {
            ContentDialogResult.Primary => SitePermissionDecision.Allow,
            ContentDialogResult.Secondary => SitePermissionDecision.Block,
            _ => SitePermissionDecision.Ask
        };

        string? preferredMicrophoneName = microphoneSelector?.SelectedItem is ComboBoxItem selectedItem &&
            selectedItem.Tag is string selectedName
                ? selectedName
                : null;
        return new PermissionPromptResult(
            decision,
            rememberChoice.IsChecked == true,
            preferredMicrophoneName);
    }

    private async Task<ComboBox?> BuildMicrophoneSelectorAsync()
    {
        try
        {
            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(
                MediaDevice.GetAudioCaptureSelector());
            if (devices.Count == 0)
            {
                return null;
            }

            ComboBox selector = new()
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 280
            };
            selector.Items.Add(new ComboBoxItem
            {
                Content = "System default",
                Tag = string.Empty
            });

            string defaultId = MediaDevice.GetDefaultAudioCaptureId(
                AudioDeviceRole.Communications);
            int selectedIndex = 0;
            foreach (DeviceInformation device in devices)
            {
                string label = device.Id.Equals(defaultId, StringComparison.OrdinalIgnoreCase)
                    ? $"{device.Name} (Windows default)"
                    : device.Name;
                selector.Items.Add(new ComboBoxItem
                {
                    Content = label,
                    Tag = device.Name
                });

                if (!string.IsNullOrWhiteSpace(_settings.PreferredMicrophoneName) &&
                    device.Name.Equals(
                        _settings.PreferredMicrophoneName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = selector.Items.Count - 1;
                }
            }

            selector.SelectedIndex = selectedIndex;
            return selector;
        }
        catch
        {
            return null;
        }
    }

    private async Task EnsureMicrophoneSelectionBridgeAsync(
        BrowserTabState state,
        CoreWebView2 core)
    {
        try
        {
            if (_tabs.Contains(state))
            {
                await core.ExecuteScriptAsync(MicrophoneSelectionBridgeScript);
            }
        }
        catch
        {
            // Device selection remains optional if a page blocks script injection.
        }
    }

    private static async Task ApplyPreferredMicrophoneChoiceAsync(
        BrowserTabState state,
        string? preferredMicrophoneName)
    {
        if (state.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        string serializedName = JsonSerializer.Serialize(preferredMicrophoneName ?? string.Empty);
        try
        {
            await core.ExecuteScriptAsync(
                $"window.__aeroPreferredMicrophoneName = {serializedName};");
        }
        catch
        {
            // The requesting frame may have navigated before the choice was applied.
        }
    }

    private async void SitePermissionsFlyout_Opened(object sender, object e)
    {
        UpdateSitePermissionsUi();
        await RefreshSitePrivacyDataAsync();
    }

    private void SitePermissionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPermissionUi ||
            sender is not ComboBox comboBox ||
            comboBox.Tag is not string kindText ||
            !Enum.TryParse(kindText, ignoreCase: false, out SitePermissionKind permissionKind) ||
            GetActiveSiteOrigin() is not string origin)
        {
            return;
        }

        SitePermissionDecision decision = comboBox.SelectedIndex switch
        {
            1 => SitePermissionDecision.Allow,
            2 => SitePermissionDecision.Block,
            _ => SitePermissionDecision.Ask
        };

        _sitePermissionStore.SetDecision(origin, permissionKind, decision);
        SitePermissionsHintText.Text = decision == SitePermissionDecision.Ask
            ? "Aero will ask the next time this permission is requested."
            : "The new choice applies to future requests. Reload the page to stop an active device session.";
        if (ActiveTab is BrowserTabState state && Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
        {
            UpdateSitePrivacySummary(state, origin, uri);
        }
    }

    private void ResetSitePermissionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetActiveSiteOrigin() is not string origin)
        {
            return;
        }

        _sitePermissionStore.Reset(origin);
        UpdateSitePermissionsUi();
        SitePermissionsHintText.Text = "Permissions were reset. Aero will ask again when the site requests access.";
    }

    private void UpdateSitePermissionsUi()
    {
        string? origin = GetActiveSiteOrigin();
        bool available = origin is not null;

        SitePermissionsButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        SitePermissionsButton.IsEnabled = available;

        if (origin is null)
        {
            SitePermissionsSiteText.Text = "Open a website to manage permissions";
            SetPermissionCombo(CameraPermissionComboBox, SitePermissionDecision.Ask);
            SetPermissionCombo(MicrophonePermissionComboBox, SitePermissionDecision.Ask);
            SetPermissionCombo(LocationPermissionComboBox, SitePermissionDecision.Ask);
            SetPermissionCombo(NotificationsPermissionComboBox, SitePermissionDecision.Ask);
            SetPermissionCombo(PopupsPermissionComboBox, SitePermissionDecision.Ask);
            SetPermissionCombo(RedirectsPermissionComboBox, SitePermissionDecision.Ask);
            ResetSitePermissionsButton.IsEnabled = false;
            ResetSiteDataButton.IsEnabled = false;
            DeleteSiteCookiesButton.IsEnabled = false;
            return;
        }

        Uri uri = new(origin);
        bool secure = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        SitePermissionsIcon.Glyph = secure ? "\uE72E" : "\uE7BA";
        ToolTipService.SetToolTip(
            SitePermissionsButton,
            secure ? "Site information and permissions" : "Not secure — site permissions");

        SitePermissionsSiteText.Text = origin;
        SiteConnectionText.Text = secure
            ? "Connection uses HTTPS"
            : "Connection is not encrypted";
        SitePermissionsHintText.Text = _isPrivateWindow
            ? "Choices are kept only until this private window closes."
            : "Choices are stored locally in your Aero profile.";

        _updatingPermissionUi = true;
        try
        {
            SetPermissionCombo(
                CameraPermissionComboBox,
                _sitePermissionStore.GetDecision(origin, SitePermissionKind.Camera));
            SetPermissionCombo(
                MicrophonePermissionComboBox,
                _sitePermissionStore.GetDecision(origin, SitePermissionKind.Microphone));
            SetPermissionCombo(
                LocationPermissionComboBox,
                _sitePermissionStore.GetDecision(origin, SitePermissionKind.Geolocation));
            SetPermissionCombo(
                NotificationsPermissionComboBox,
                _sitePermissionStore.GetDecision(origin, SitePermissionKind.Notifications));
            SetPermissionCombo(
                PopupsPermissionComboBox,
                _sitePermissionStore.GetDecision(origin, SitePermissionKind.Popups));
            SetPermissionCombo(
                RedirectsPermissionComboBox,
                _sitePermissionStore.GetDecision(origin, SitePermissionKind.Redirects));
        }
        finally
        {
            _updatingPermissionUi = false;
        }

        ResetSitePermissionsButton.IsEnabled = true;
        ResetSiteDataButton.IsEnabled = true;
        if (ActiveTab is BrowserTabState state)
        {
            UpdateSitePrivacySummary(state, origin, uri);
            UpdateSitePrivacyPolicyControls(uri);
        }
    }

    private static void SetPermissionCombo(
        ComboBox comboBox,
        SitePermissionDecision decision)
    {
        comboBox.SelectedIndex = decision switch
        {
            SitePermissionDecision.Allow => 1,
            SitePermissionDecision.Block => 2,
            _ => 0
        };
    }

    private string? GetActiveSiteOrigin()
    {
        Uri? uri = ActiveTab?.CurrentUri ?? ActiveTab?.PendingUri;
        return SitePermissionStore.NormalizeOrigin(uri?.AbsoluteUri);
    }

    private static bool TryMapPermissionKind(
        CoreWebView2PermissionKind permissionKind,
        out SitePermissionKind mappedKind)
    {
        switch (permissionKind)
        {
            case CoreWebView2PermissionKind.Camera:
                mappedKind = SitePermissionKind.Camera;
                return true;
            case CoreWebView2PermissionKind.Microphone:
                mappedKind = SitePermissionKind.Microphone;
                return true;
            case CoreWebView2PermissionKind.Geolocation:
                mappedKind = SitePermissionKind.Geolocation;
                return true;
            case CoreWebView2PermissionKind.Notifications:
                mappedKind = SitePermissionKind.Notifications;
                return true;
            case CoreWebView2PermissionKind.ClipboardRead:
                mappedKind = SitePermissionKind.Clipboard;
                return true;
            case CoreWebView2PermissionKind.MidiSystemExclusiveMessages:
                mappedKind = SitePermissionKind.Midi;
                return true;
            case CoreWebView2PermissionKind.MultipleAutomaticDownloads:
                mappedKind = SitePermissionKind.AutomaticDownloads;
                return true;
            default:
                mappedKind = default;
                return false;
        }
    }

    private static CoreWebView2PermissionState ToWebViewPermissionState(
        SitePermissionDecision decision) => decision switch
    {
        SitePermissionDecision.Allow => CoreWebView2PermissionState.Allow,
        SitePermissionDecision.Block => CoreWebView2PermissionState.Deny,
        // "Ask" means ask again next time. The current request still has to
        // finish without falling through to WebView2's second built-in prompt.
        _ => CoreWebView2PermissionState.Deny
    };

    private static string GetPermissionDisplayName(SitePermissionKind kind) => kind switch
    {
        SitePermissionKind.Camera => "Camera",
        SitePermissionKind.Microphone => "Microphone",
        SitePermissionKind.Geolocation => "Location",
        SitePermissionKind.Notifications => "Notifications",
        SitePermissionKind.Popups => "Pop-ups",
        SitePermissionKind.Redirects => "Redirects",
        SitePermissionKind.Clipboard => "Clipboard",
        SitePermissionKind.Midi => "MIDI devices",
        SitePermissionKind.AutomaticDownloads => "Automatic downloads",
        _ => "Permission"
    };

    private void RootGrid_PageToolsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        CoreVirtualKeyStates controlState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if ((controlState & CoreVirtualKeyStates.Down) == 0)
        {
            return;
        }

        switch ((int)e.Key)
        {
            case 187: // VK_OEM_PLUS, the +/= key on the main keyboard.
            case (int)VirtualKey.Add:
                e.Handled = true;
                ChangePageZoom(1);
                break;
            case 189: // VK_OEM_MINUS, the -/_ key on the main keyboard.
            case (int)VirtualKey.Subtract:
                e.Handled = true;
                ChangePageZoom(-1);
                break;
            case (int)VirtualKey.Number0:
            case (int)VirtualKey.NumberPad0:
                e.Handled = true;
                ResetPageZoom();
                break;
        }
    }

    private void PageZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        ChangePageZoom(-1);
    }

    private void PageZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        ChangePageZoom(1);
    }

    private void PageZoomResetButton_Click(object sender, RoutedEventArgs e)
    {
        ResetPageZoom();
    }

    private void AddressZoomButton_Click(object sender, RoutedEventArgs e)
    {
        ResetPageZoom();
    }

    private void ZoomOutAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ChangePageZoom(-1);
    }

    private void ZoomInAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ChangePageZoom(1);
    }

    private void ZoomResetAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ResetPageZoom();
    }

    private void ChangePageZoom(int direction)
    {
        ChangePageZoom(ActiveTab, direction);
    }

    private void ChangePageZoom(BrowserTabState? state, int direction)
    {
        if (state is null || !_tabs.Contains(state))
        {
            return;
        }

        int currentIndex = FindNearestZoomStepIndex(state.PageZoomFactor);
        int nextIndex = Math.Clamp(currentIndex + Math.Sign(direction), 0, PageZoomSteps.Length - 1);
        double previousFactor = state.PageZoomFactor;
        state.PageZoomFactor = PageZoomSteps[nextIndex];
        if (ReferenceEquals(state, ActiveTab))
        {
            UpdatePageToolsUi();
            if (Math.Abs(previousFactor - state.PageZoomFactor) > 0.001)
            {
                ShowPageZoomIndicator(state);
            }
        }

        _ = ApplyPageZoomAsync(state);
    }

    private void ResetPageZoom()
    {
        ResetPageZoom(ActiveTab);
    }

    private void ResetPageZoom(BrowserTabState? state)
    {
        if (state is null || !_tabs.Contains(state))
        {
            return;
        }

        double previousFactor = state.PageZoomFactor;
        state.PageZoomFactor = 1.0;
        if (ReferenceEquals(state, ActiveTab))
        {
            UpdatePageToolsUi();
            if (Math.Abs(previousFactor - state.PageZoomFactor) > 0.001)
            {
                ShowPageZoomIndicator(state);
            }
        }

        _ = ApplyPageZoomAsync(state);
    }

    private void HandlePageZoomMessage(BrowserTabState state, string action)
    {
        switch (action)
        {
            case "in":
                ChangePageZoom(state, 1);
                break;
            case "out":
                ChangePageZoom(state, -1);
                break;
            case "reset":
                ResetPageZoom(state);
                break;
        }
    }

    private static int FindNearestZoomStepIndex(double factor)
    {
        int bestIndex = 0;
        double bestDistance = double.MaxValue;
        for (int index = 0; index < PageZoomSteps.Length; index++)
        {
            double distance = Math.Abs(PageZoomSteps[index] - factor);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private static async Task RegisterPageZoomBridgeAsync(BrowserTabState state)
    {
        if (state.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PageZoomBridgeScript);
        }
        catch
        {
            // The controller can be replaced while a tab is closing or restoring.
        }
    }

    private async Task ApplyPageZoomAsync(BrowserTabState state)
    {
        if (state.IsDiscarded ||
            state.IsClosing ||
            state.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        int percent = (int)Math.Round(state.PageZoomFactor * 100, MidpointRounding.AwayFromZero);
        string zoomValue = JsonSerializer.Serialize($"{percent}%");
        string script = Math.Abs(state.PageZoomFactor - 1.0) <= 0.001
            ? "document.documentElement?.style.removeProperty('zoom');"
            : $"document.documentElement?.style.setProperty('zoom', {zoomValue}, 'important');";

        try
        {
            await core.ExecuteScriptAsync(script);
        }
        catch
        {
            // The page may be navigating or the WebView may be closing.
        }
    }

    private void ShowPageZoomIndicator(BrowserTabState state)
    {
        if (!ReferenceEquals(state, ActiveTab))
        {
            return;
        }

        int percent = (int)Math.Round(state.PageZoomFactor * 100, MidpointRounding.AwayFromZero);
        ZoomToastText.Text = $"Page zoom: {percent}%";
        ZoomToast.Visibility = Visibility.Visible;
        ZoomToast.Opacity = 0;
        ZoomToastTransform.TranslateY = -8;
        AnimateOpacity(ZoomToast, 0, 1, FastAnimationDuration);
        AnimateDouble(ZoomToastTransform, "TranslateY", -8, 0, FastAnimationDuration);
        _zoomToastTimer.Stop();
        _zoomToastTimer.Start();
    }

    private void HidePageZoomIndicator()
    {
        _zoomToastTimer.Stop();
        if (ZoomToast.Visibility != Visibility.Visible)
        {
            return;
        }

        AnimateOpacity(ZoomToast, ZoomToast.Opacity, 0, FastAnimationDuration, () =>
        {
            ZoomToast.Visibility = Visibility.Collapsed;
        });
        AnimateDouble(ZoomToastTransform, "TranslateY", ZoomToastTransform.TranslateY, -8, FastAnimationDuration);
    }

    private void UpdatePageToolsUi()
    {
        BrowserTabState? state = ActiveTab;
        bool available = state is not null && !state.IsDiscarded && state.Browser.CoreWebView2 is not null;
        double factor = state?.PageZoomFactor ?? 1.0;
        int currentIndex = FindNearestZoomStepIndex(factor);

        string percentText = $"{Math.Round(factor * 100):0}%";
        PageZoomText.Text = percentText;
        AddressZoomText.Text = percentText;
        AddressZoomButton.Visibility = available && Math.Abs(factor - 1.0) > 0.001
            ? Visibility.Visible
            : Visibility.Collapsed;
        PageZoomOutButton.IsEnabled = available && currentIndex > 0;
        PageZoomInButton.IsEnabled = available && currentIndex < PageZoomSteps.Length - 1;
        PageZoomResetButton.IsEnabled = available && Math.Abs(factor - 1.0) > 0.001;
        PrintPageMenuButton.IsEnabled = available;
        SavePdfMenuButton.IsEnabled = available;
        CaptureVisibleMenuButton.IsEnabled = available && !_pageCaptureInProgress;
        CaptureFullPageMenuButton.IsEnabled = available && !_pageCaptureInProgress;
    }

    private void PrintPageMenuButton_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        ShowPrintUi();
    }

    private void PrintPageAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ShowPrintUi();
    }

    private void ShowPrintUi()
    {
        if (ActiveTab?.Browser.CoreWebView2 is not CoreWebView2 core || ActiveTab.IsDiscarded)
        {
            return;
        }

        try
        {
            core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
        }
        catch (Exception exception)
        {
            _ = ShowErrorDialogAsync("Could not open printing", exception.Message);
        }
    }

    private async void SavePdfMenuButton_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        await SaveCurrentPageAsPdfAsync();
    }

    private async void SavePdfAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SaveCurrentPageAsPdfAsync();
    }

    private async Task SaveCurrentPageAsPdfAsync()
    {
        BrowserTabState? state = ActiveTab;
        if (state?.Browser.CoreWebView2 is not CoreWebView2 core || state.IsDiscarded)
        {
            return;
        }

        StorageFile? file = await PickOutputFileAsync(
            "PDF document",
            ".pdf",
            BuildSuggestedFileName(state, "page"));
        if (file is null)
        {
            return;
        }

        try
        {
            CoreWebView2PrintSettings printSettings = core.Environment.CreatePrintSettings();
            bool saved = await core.PrintToPdfAsync(file.Path, printSettings);
            if (!saved)
            {
                throw new InvalidOperationException("WebView2 did not create the PDF file.");
            }

            await ShowSavedFileDialogAsync("PDF saved", file);
        }
        catch (Exception exception)
        {
            await TryDeleteOutputFileAsync(file);
            await ShowErrorDialogAsync("Could not save PDF", exception.Message);
        }
    }

    private async void CaptureVisibleMenuButton_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        await CapturePageAsync(fullPage: false);
    }

    private async void CaptureFullPageMenuButton_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        await CapturePageAsync(fullPage: true);
    }

    private async Task CapturePageAsync(bool fullPage)
    {
        BrowserTabState? state = ActiveTab;
        if (_pageCaptureInProgress ||
            state?.Browser.CoreWebView2 is not CoreWebView2 core ||
            state.IsDiscarded)
        {
            return;
        }

        StorageFile? file = await PickOutputFileAsync(
            "PNG image",
            ".png",
            BuildSuggestedFileName(state, fullPage ? "full-page" : "visible"));
        if (file is null)
        {
            return;
        }

        _pageCaptureInProgress = true;
        UpdatePageToolsUi();
        try
        {
            if (fullPage)
            {
                const string parameters = "{\"format\":\"png\",\"fromSurface\":true,\"captureBeyondViewport\":true}";
                string response = await core.CallDevToolsProtocolMethodAsync(
                    "Page.captureScreenshot",
                    parameters);
                using JsonDocument document = JsonDocument.Parse(response);
                string? base64 = document.RootElement.GetProperty("data").GetString();
                if (string.IsNullOrWhiteSpace(base64))
                {
                    throw new InvalidOperationException("The page did not return screenshot data.");
                }

                await FileIO.WriteBytesAsync(file, Convert.FromBase64String(base64));
            }
            else
            {
                using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                stream.Size = 0;
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            }

            await ShowSavedFileDialogAsync(
                fullPage ? "Full-page screenshot saved" : "Screenshot saved",
                file);
        }
        catch (Exception exception)
        {
            await TryDeleteOutputFileAsync(file);
            await ShowErrorDialogAsync("Could not capture the page", exception.Message);
        }
        finally
        {
            _pageCaptureInProgress = false;
            UpdatePageToolsUi();
        }
    }

    private async Task<StorageFile?> PickOutputFileAsync(
        string fileDescription,
        string extension,
        string suggestedFileName)
    {
        FileSavePicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = suggestedFileName
        };
        picker.FileTypeChoices.Add(fileDescription, new List<string> { extension });

        nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        return await picker.PickSaveFileAsync();
    }

    private static async Task TryDeleteOutputFileAsync(StorageFile file)
    {
        try
        {
            await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }
        catch
        {
            // The user may have moved the file or another app may already hold it.
        }
    }

    private async Task ShowSavedFileDialogAsync(string title, StorageFile file)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = file.Path,
            PrimaryButtonText = "Show in folder",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            OpenContainingFolder(file.Path);
        }
    }

    private static string BuildSuggestedFileName(BrowserTabState state, string suffix)
    {
        string title = string.IsNullOrWhiteSpace(state.Title) ? "Aero page" : state.Title.Trim();
        foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
        {
            title = title.Replace(invalidCharacter, '_');
        }

        title = title.Trim(' ', '.');
        if (title.Length == 0)
        {
            title = "Aero page";
        }

        if (title.Length > 72)
        {
            title = title[..72].TrimEnd();
        }

        return $"{title} - {suffix}";
    }

    private readonly record struct PermissionPromptResult(
        SitePermissionDecision Decision,
        bool Remember,
        string? PreferredMicrophoneName = null);
}
