using Aero.App.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aero.App;

public sealed partial class MainWindow
{
    private readonly PasswordVaultService _passwordVaultService;
    private readonly PasswordBrokerClient _passwordBrokerClient;
    private const int VaultFillHeaderSize = 24;
    private static ReadOnlySpan<byte> VaultFillMagic => "AEROFIL1"u8;
    private readonly ConcurrentDictionary<Guid, PendingVaultFillOperation> _pendingVaultFills = new();
    private int _passwordVaultRefreshGeneration;
    private string? _passwordVaultDisplayedOrigin;

    private void InitializeSecureVaultFeatures()
    {
        PasswordVaultButton.Visibility = Visibility.Collapsed;
        PasswordVaultStatusText.Text = "Open an HTTPS sign-in page to use Aero Vault.";
        PasswordVaultPasswordBox.Password = string.Empty;
    }

    private void RefreshPasswordVaultForActiveTab()
    {
        int generation = ++_passwordVaultRefreshGeneration;
        BrowserTabState? state = ActiveTab;
        bool usable = TryGetPasswordVaultOrigin(state, out string? origin);
        string? securityWarning = GetPasswordVaultPageSecurityWarning(state);

        PasswordVaultButton.Visibility = usable || securityWarning is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!usable || origin is null)
        {
            _passwordVaultDisplayedOrigin = null;
            PasswordVaultUsernameBox.Text = string.Empty;
            PasswordVaultPasswordBox.Password = string.Empty;
            PasswordVaultSiteText.Text = securityWarning is null
                ? "HTTPS sign-in pages only"
                : "Password filling blocked";
            PasswordVaultStatusText.Text = securityWarning ??
                "Aero Vault is unavailable on this page.";
            PasswordVaultFillButton.IsEnabled = false;
            PasswordVaultGenerateButton.IsEnabled = false;
            PasswordVaultSaveButton.IsEnabled = false;
            PasswordVaultDeleteButton.IsEnabled = false;
            return;
        }

        if (!origin.Equals(_passwordVaultDisplayedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            _passwordVaultDisplayedOrigin = origin;
            PasswordVaultUsernameBox.Text = string.Empty;
            PasswordVaultPasswordBox.Password = string.Empty;
        }

        PasswordVaultSiteText.Text = new Uri(origin).Host;
        PasswordVaultSaveButton.IsEnabled = _settings.SavePasswords && !_isPrivateWindow;
        PasswordVaultGenerateButton.IsEnabled = _settings.SavePasswords && !_isPrivateWindow;
        PasswordVaultFillButton.IsEnabled = _settings.SavePasswords;
        _ = RefreshPasswordVaultSiteStateAsync(origin, generation);
    }

    private async Task RefreshPasswordVaultSiteStateAsync(string origin, int generation)
    {
        try
        {
            PasswordVaultLookupState lookup =
                await _passwordVaultService.ContainsAsync(origin);
            if (generation != _passwordVaultRefreshGeneration ||
                !TryGetPasswordVaultOrigin(ActiveTab, out string? currentOrigin) ||
                !origin.Equals(currentOrigin, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            PasswordVaultDeleteButton.IsEnabled =
                lookup != PasswordVaultLookupState.NotFound && !_isPrivateWindow;
            PasswordVaultFillButton.IsEnabled =
                lookup != PasswordVaultLookupState.NotFound && _settings.SavePasswords;
            PasswordVaultStatusText.Text = lookup switch
            {
                PasswordVaultLookupState.Found =>
                    "Protected sign-in found. Each protected operation uses a one-time key lease.",
                PasswordVaultLookupState.Locked =>
                    "Aero Vault has no persistent unlock state. Filling requires Windows Hello PIN or biometrics.",
                _ when _isPrivateWindow =>
                    "No saved credential. Private windows can fill existing entries but do not save new ones.",
                _ => "No saved credential for this site."
            };
        }
        catch (Exception exception)
        {
            if (generation == _passwordVaultRefreshGeneration)
            {
                PasswordVaultStatusText.Text = $"Vault status unavailable: {exception.Message}";
            }
        }
    }

    private void PasswordVaultFlyout_Opened(object sender, object e)
    {
        PasswordVaultPasswordBox.Password = string.Empty;
        RefreshPasswordVaultForActiveTab();
    }

    private async void PasswordVaultSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPrivateWindow)
        {
            PasswordVaultStatusText.Text = "Saving passwords is disabled in private windows.";
            return;
        }

        BrowserTabState? state = ActiveTab;
        if (!_settings.SavePasswords ||
            !TryGetPasswordVaultOrigin(state, out string? origin) ||
            origin is null)
        {
            RecordBlockedVaultPageAttempt("save", state);
            PasswordVaultStatusText.Text = GetPasswordVaultPageSecurityWarning(state) ??
                "Open a trusted HTTPS page and enable password saving first.";
            return;
        }

        ulong navigationId = state!.ActiveNavigationId;
        if (!await VerifyLivePasswordVaultOriginAsync(state, origin, navigationId))
        {
            BlockVaultForOriginChange(origin);
            return;
        }

        string username = PasswordVaultUsernameBox.Text.Trim();
        string passwordText = PasswordVaultPasswordBox.Password;
        PasswordVaultPasswordBox.Password = string.Empty;

        if (passwordText.Length == 0)
        {
            PasswordVaultStatusText.Text = "Enter a password before saving.";
            return;
        }

        byte[]? usernameBytes = null;
        byte[]? passwordBytes = null;

        try
        {
            IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (!await VerifyLivePasswordVaultOriginAsync(state, origin, navigationId))
            {
                passwordText = string.Empty;
                BlockVaultForOriginChange(origin);
                return;
            }

            usernameBytes = Encoding.UTF8.GetBytes(username);
            passwordBytes = Encoding.UTF8.GetBytes(passwordText);
            passwordText = string.Empty;

            await _passwordVaultService.SavePasswordAsync(
                origin,
                usernameBytes,
                passwordBytes,
                windowHandle);

            PasswordVaultStatusText.Text =
                "Credential saved with the non-exportable Windows Hello key.";
            await RefreshPasswordVaultCountAsync();
            RefreshPasswordVaultForActiveTab();
        }
        catch (Exception exception)
        {
            PasswordVaultStatusText.Text = $"Could not save credential: {exception.Message}";
        }
        finally
        {
            if (usernameBytes is not null)
            {
                CryptographicOperations.ZeroMemory(usernameBytes);
            }

            if (passwordBytes is not null)
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
        }
    }

    private async void PasswordVaultFillButton_Click(object sender, RoutedEventArgs e)
    {
        BrowserTabState? state = ActiveTab;
        if (!_settings.SavePasswords ||
            !TryGetPasswordVaultOrigin(state, out string? origin) ||
            origin is null ||
            state?.Browser.CoreWebView2 is null)
        {
            RecordBlockedVaultPageAttempt("fill", state);
            PasswordVaultStatusText.Text = GetPasswordVaultPageSecurityWarning(state) ??
                "Aero Vault fills trusted HTTPS pages only.";
            return;
        }

        ulong navigationId = state.ActiveNavigationId;
        if (!await VerifyLivePasswordVaultOriginAsync(state, origin, navigationId))
        {
            BlockVaultForOriginChange(origin);
            return;
        }

        await ExecuteProtectedCredentialFillAsync(
            state,
            origin,
            navigationId,
            PasswordBrokerOperation.Fill);
    }

    private async void PasswordVaultGenerateButton_Click(object sender, RoutedEventArgs e)
    {
        BrowserTabState? state = ActiveTab;
        if (_isPrivateWindow ||
            !_settings.SavePasswords ||
            !TryGetPasswordVaultOrigin(state, out string? origin) ||
            origin is null ||
            state?.Browser.CoreWebView2 is null)
        {
            RecordBlockedVaultPageAttempt("generate", state);
            PasswordVaultStatusText.Text = _isPrivateWindow
                ? "Password generation and saving are disabled in private windows."
                : GetPasswordVaultPageSecurityWarning(state) ??
                  "Open a trusted HTTPS page before generating a password.";
            return;
        }

        ulong navigationId = state.ActiveNavigationId;
        if (!await VerifyLivePasswordVaultOriginAsync(state, origin, navigationId))
        {
            BlockVaultForOriginChange(origin);
            return;
        }

        await ExecuteProtectedCredentialFillAsync(
            state,
            origin,
            navigationId,
            PasswordBrokerOperation.GenerateAndSave);
    }

    private async Task ExecuteProtectedCredentialFillAsync(
        BrowserTabState state,
        string origin,
        ulong navigationId,
        PasswordBrokerOperation operation)
    {
        string? extensionBlockReason =
            await GetVaultExtensionIsolationBlockReasonAsync(state);
        if (extensionBlockReason is not null)
        {
            _passwordVaultService.RecordSecurityEvent(
                "extension-isolation",
                operation == PasswordBrokerOperation.GenerateAndSave
                    ? "generate-blocked"
                    : "fill-blocked",
                origin);
            PasswordVaultStatusText.Text = extensionBlockReason;
            return;
        }

        PasswordVaultFillButton.IsEnabled = false;
        PasswordVaultGenerateButton.IsEnabled = false;
        PasswordVaultStatusText.Text = operation == PasswordBrokerOperation.GenerateAndSave
            ? "Generating inside the hardened Broker and waiting for Windows Hello…"
            : "Waiting for Windows Hello…";

        byte[]? generatedUsernameUtf8 = null;
        try
        {
            if (operation == PasswordBrokerOperation.GenerateAndSave)
            {
                generatedUsernameUtf8 = Encoding.UTF8.GetBytes(
                    PasswordVaultUsernameBox.Text.Trim());
            }

            // Install/refresh only the static bridge before asking Broker for a secret, so
            // the protected lease is not kept alive while ExecuteScriptAsync completes.
            await EnsurePasswordCaptureBridgeOnCurrentDocumentAsync(state);

            IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Guid documentIdentity = state.SecurityDocumentIdentity;
            ReadOnlyMemory<byte> requestedUsername = generatedUsernameUtf8 is null
                ? ReadOnlyMemory<byte>.Empty
                : generatedUsernameUtf8;
            Task<string> fillCompletion;
            using (BrokerCredentialLease? lease = await _passwordBrokerClient.GetCredentialAsync(
                operation,
                origin,
                windowHandle,
                navigationId,
                documentIdentity,
                requestedUsername))
            {
                if (lease is null)
                {
                    PasswordVaultStatusText.Text = "No credential is saved for this site.";
                    return;
                }

                if (state.SecurityDocumentIdentity != documentIdentity ||
                    !await VerifyLivePasswordVaultOriginAsync(state, origin, navigationId))
                {
                    BlockVaultForOriginChange(origin);
                    return;
                }

                extensionBlockReason = await GetVaultExtensionIsolationBlockReasonAsync(state);
                if (extensionBlockReason is not null)
                {
                    _passwordVaultService.RecordSecurityEvent(
                        "extension-isolation",
                        operation == PasswordBrokerOperation.GenerateAndSave
                            ? "generate-blocked-after-auth"
                            : "fill-blocked-after-auth",
                        origin);
                    PasswordVaultStatusText.Text = extensionBlockReason;
                    return;
                }

                if (state.SecurityDocumentIdentity != documentIdentity ||
                    !await VerifyLivePasswordVaultOriginAsync(state, origin, navigationId))
                {
                    BlockVaultForOriginChange(origin);
                    return;
                }

                extensionBlockReason = await GetVaultExtensionIsolationBlockReasonAsync(state);
                if (extensionBlockReason is not null)
                {
                    _passwordVaultService.RecordSecurityEvent(
                        "extension-isolation",
                        operation == PasswordBrokerOperation.GenerateAndSave
                            ? "generate-blocked-final-check"
                            : "fill-blocked-final-check",
                        origin);
                    PasswordVaultStatusText.Text = extensionBlockReason;
                    return;
                }

                // The secret is copied once into the one-use WebView2 mapping. This method
                // returns the non-secret completion task synchronously; leaving this using
                // block destroys the locked Broker lease before Aero waits for page feedback.
                fillCompletion = FillCredentialThroughSharedBufferAsync(
                    state,
                    origin,
                    documentIdentity,
                    lease.UsernameUtf8,
                    lease.PasswordUtf8);
            }

            string result = await fillCompletion;
            if (result is "origin-mismatch" or "form-action-mismatch" or "navigation-changed")
            {
                BlockVaultForOriginChange(origin);
            }
            else if (result == "filled")
            {
                PasswordVaultStatusText.Text = operation == PasswordBrokerOperation.GenerateAndSave
                    ? "A strong password was generated inside Broker, saved and filled without keyboard input."
                    : "Credential filled. Aero released its one-use shared-buffer handle.";
            }
            else if (result == "no-password-field")
            {
                PasswordVaultStatusText.Text = operation == PasswordBrokerOperation.GenerateAndSave
                    ? "Password was generated and saved, but no visible password field was found."
                    : "No visible password field was found on this page.";
            }
            else
            {
                _passwordVaultService.RecordSecurityEvent("shared-buffer-fill", "failed-closed", origin);
                PasswordVaultStatusText.Text =
                    "Aero Vault stopped the fill operation because the protected page bridge did not complete safely.";
            }
        }
        catch (PasswordBrokerException exception)
        {
            PasswordVaultStatusText.Text = exception.Message;
        }
        catch (PasswordVaultVerificationException exception)
        {
            PasswordVaultStatusText.Text = exception.Message;
        }
        catch (Exception)
        {
            _passwordVaultService.RecordSecurityEvent(
                operation == PasswordBrokerOperation.GenerateAndSave ? "generate" : "fill",
                "failed-closed",
                origin);
            PasswordVaultStatusText.Text = operation == PasswordBrokerOperation.GenerateAndSave
                ? "Could not generate the password. Aero Vault failed closed."
                : "Could not fill the credential. Aero Vault failed closed.";
        }
        finally
        {
            if (generatedUsernameUtf8 is not null)
            {
                CryptographicOperations.ZeroMemory(generatedUsernameUtf8);
            }
            RefreshPasswordVaultForActiveTab();
        }
    }

    private async void PasswordVaultDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        BrowserTabState? state = ActiveTab;
        if (_isPrivateWindow ||
            !TryGetPasswordVaultOrigin(state, out string? origin) ||
            origin is null)
        {
            return;
        }

        try
        {
            ulong navigationId = state!.ActiveNavigationId;
            if (!await VerifyLivePasswordVaultOriginAsync(state, origin, navigationId))
            {
                BlockVaultForOriginChange(origin);
                return;
            }

            IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (!await VerifyLivePasswordVaultOriginAsync(state, origin, navigationId))
            {
                BlockVaultForOriginChange(origin);
                return;
            }

            bool deleted = await _passwordVaultService.DeletePasswordAsync(
                origin,
                windowHandle);
            PasswordVaultUsernameBox.Text = string.Empty;
            PasswordVaultPasswordBox.Password = string.Empty;
            PasswordVaultStatusText.Text = deleted
                ? "Saved credential removed."
                : "No saved credential was found.";
            await RefreshPasswordVaultCountAsync();
            RefreshPasswordVaultForActiveTab();
        }
        catch (Exception exception)
        {
            PasswordVaultStatusText.Text = $"Could not remove credential: {exception.Message}";
        }
    }

    private Task<string> FillCredentialThroughSharedBufferAsync(
        BrowserTabState state,
        string origin,
        Guid documentIdentity,
        ReadOnlySpan<byte> usernameUtf8,
        ReadOnlySpan<byte> passwordUtf8)
    {
        if (state.Browser.CoreWebView2 is not CoreWebView2 core ||
            passwordUtf8.IsEmpty)
        {
            return Task.FromResult("no-password-field");
        }

        int payloadSize = checked(
            VaultFillHeaderSize + usernameUtf8.Length + passwordUtf8.Length);
        CoreWebView2SharedBuffer? buffer = null;
        Guid operationId = Guid.NewGuid();
        TaskCompletionSource<string> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PendingVaultFillOperation pending = new(
            state,
            documentIdentity,
            origin,
            completion);

        if (!_pendingVaultFills.TryAdd(operationId, pending))
        {
            throw new InvalidOperationException("Aero Vault could not allocate a fill operation.");
        }

        try
        {
            buffer = core.Environment.CreateSharedBuffer((ulong)payloadSize);
            using var randomAccessStream = buffer.OpenStream();
            using (Stream stream = randomAccessStream.AsStream())
            {
                Span<byte> header = stackalloc byte[VaultFillHeaderSize];
                header.Clear();
                VaultFillMagic.CopyTo(header);
                BinaryPrimitives.WriteInt32LittleEndian(header[8..12], 1);
                BinaryPrimitives.WriteInt32LittleEndian(header[12..16], usernameUtf8.Length);
                BinaryPrimitives.WriteInt32LittleEndian(header[16..20], passwordUtf8.Length);

                stream.Position = 0;
                stream.Write(header);
                stream.Write(usernameUtf8);
                stream.Write(passwordUtf8);
                stream.Flush();
            }

            string metadata = JsonSerializer.Serialize(new
            {
                kind = "aero-vault-fill-v1",
                operationId = operationId.ToString("N"),
                origin
            });
            core.PostSharedBufferToScript(
                buffer,
                CoreWebView2SharedBufferAccess.ReadOnly,
                metadata);

            // WebView2's one-time shared-buffer pattern allows the host object to be
            // closed immediately after posting. The renderer view remains valid until
            // script calls releaseBuffer, while Aero no longer retains the mapping.
            buffer.Dispose();
            buffer = null;
            return AwaitVaultFillResultAsync(operationId, completion);
        }
        catch
        {
            _pendingVaultFills.TryRemove(operationId, out _);
            if (buffer is not null)
            {
                ZeroAndDisposeSharedBuffer(buffer, payloadSize);
            }
            throw;
        }
    }

    private async Task<string> AwaitVaultFillResultAsync(
        Guid operationId,
        TaskCompletionSource<string> completion)
    {
        try
        {
            try
            {
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(12));
            }
            catch (TimeoutException)
            {
                return "bridge-timeout";
            }
        }
        finally
        {
            _pendingVaultFills.TryRemove(operationId, out _);
        }
    }

    private bool TryHandleVaultFillResultMessage(
        BrowserTabState state,
        CoreWebView2WebMessageReceivedEventArgs args,
        JsonElement root,
        string? messageType)
    {
        if (!string.Equals(messageType, "aero-vault-fill-result", StringComparison.Ordinal))
        {
            return false;
        }

        string operationIdText = root.TryGetProperty("operationId", out JsonElement idElement) &&
            idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString() ?? string.Empty
                : string.Empty;
        if (!Guid.TryParseExact(operationIdText, "N", out Guid operationId) ||
            !_pendingVaultFills.TryGetValue(operationId, out PendingVaultFillOperation? pending))
        {
            return true;
        }

        if (!ReferenceEquals(pending.State, state) ||
            pending.DocumentIdentity != state.SecurityDocumentIdentity ||
            !TryNormalizeCapturedOrigin(args.Source, out string sourceOrigin) ||
            !pending.Origin.Equals(sourceOrigin, StringComparison.OrdinalIgnoreCase))
        {
            pending.Completion.TrySetResult("origin-mismatch");
            return true;
        }

        string result = root.TryGetProperty("result", out JsonElement resultElement) &&
            resultElement.ValueKind == JsonValueKind.String
                ? resultElement.GetString() ?? string.Empty
                : string.Empty;
        result = result switch
        {
            "filled" => result,
            "no-password-field" => result,
            "origin-mismatch" => result,
            "form-action-mismatch" => result,
            "invalid-buffer" => result,
            "bridge-error" => result,
            _ => "bridge-error"
        };
        pending.Completion.TrySetResult(result);
        return true;
    }

    private void CancelPendingVaultFillOperations(BrowserTabState state)
    {
        foreach (KeyValuePair<Guid, PendingVaultFillOperation> pair in _pendingVaultFills)
        {
            if (ReferenceEquals(pair.Value.State, state))
            {
                pair.Value.Completion.TrySetResult("navigation-changed");
            }
        }
    }

    private static void ZeroSharedBuffer(CoreWebView2SharedBuffer buffer, int length)
    {
        using var randomAccessStream = buffer.OpenStream();
        using Stream stream = randomAccessStream.AsStream();
        stream.Position = 0;
        byte[] zeros = new byte[Math.Min(length, 8192)];
        int remaining = length;
        while (remaining > 0)
        {
            int count = Math.Min(remaining, zeros.Length);
            stream.Write(zeros, 0, count);
            remaining -= count;
        }
        stream.Flush();
    }

    private static void ZeroAndDisposeSharedBuffer(
        CoreWebView2SharedBuffer buffer,
        int length)
    {
        try
        {
            ZeroSharedBuffer(buffer, length);
        }
        catch
        {
            // The renderer or controller may already be gone. Dispose still revokes access.
        }
        finally
        {
            buffer.Dispose();
        }
    }

    private sealed record PendingVaultFillOperation(
        BrowserTabState State,
        Guid DocumentIdentity,
        string Origin,
        TaskCompletionSource<string> Completion);

    private static bool TryGetPasswordVaultOrigin(BrowserTabState? state, out string? origin)
    {
        origin = null;
        Uri? uri = state?.CurrentUri ?? state?.PendingUri;
        if (state is null ||
            uri is null ||
            state.IsLoading ||
            state.DevToolsOpenedForCurrentDocument ||
            state.IsDiscarded ||
            state.IsShowingNetworkErrorPage ||
            !TryNormalizeVaultUri(uri, out string? normalizedOrigin))
        {
            return false;
        }

        try
        {
            Uri? browserSource = state.Browser.Source;
            string? coreSource = state.Browser.CoreWebView2?.Source;
            if (browserSource is null ||
                !TryNormalizeVaultUri(browserSource, out string? browserOrigin) ||
                !Uri.TryCreate(coreSource, UriKind.Absolute, out Uri? coreUri) ||
                !TryNormalizeVaultUri(coreUri, out string? coreOrigin) ||
                !string.Equals(normalizedOrigin, browserOrigin, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(normalizedOrigin, coreOrigin, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            origin = normalizedOrigin;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryNormalizeVaultUri(Uri uri, out string? origin)
    {
        origin = null;
        if (!uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            origin = PasswordVaultService.NormalizeHttpsOrigin(
                uri.GetLeftPart(UriPartial.Authority));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? GetPasswordVaultPageSecurityWarning(BrowserTabState? state)
    {
        Uri? uri = state?.CurrentUri ?? state?.PendingUri;
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return null;
        }

        if (state?.DevToolsOpenedForCurrentDocument == true)
        {
            return "Developer tools were opened for this document. Aero Vault is blocked until the page is reloaded.";
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return "Warning: this page uses HTTP. Aero Vault will never save or fill a password over an unencrypted connection.";
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            PasswordVaultService.IsSuspiciousHost(uri.IdnHost, out string reason))
        {
            return $"Suspicious domain: {reason} Password filling is blocked.";
        }

        if (state?.IsLoading == true)
        {
            return "The page identity is still changing. Wait for navigation to finish before using Aero Vault.";
        }

        return null;
    }

    private async Task<bool> VerifyLivePasswordVaultOriginAsync(
        BrowserTabState state,
        string expectedOrigin,
        ulong navigationId)
    {
        if (!ReferenceEquals(state, ActiveTab) ||
            state.ActiveNavigationId != navigationId ||
            !TryGetPasswordVaultOrigin(state, out string? currentOrigin) ||
            !expectedOrigin.Equals(currentOrigin, StringComparison.OrdinalIgnoreCase) ||
            state.Browser.CoreWebView2 is not Microsoft.Web.WebView2.Core.CoreWebView2 core)
        {
            return false;
        }

        try
        {
            string json = await core.ExecuteScriptAsync("""
                (() => ({
                    origin: location.origin,
                    href: location.href,
                    isTop: window.top === window
                }))()
                """);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("origin", out JsonElement originElement) ||
                !root.TryGetProperty("href", out JsonElement hrefElement) ||
                !root.TryGetProperty("isTop", out JsonElement topElement) ||
                topElement.ValueKind != JsonValueKind.True ||
                !Uri.TryCreate(originElement.GetString(), UriKind.Absolute, out Uri? liveOriginUri) ||
                !Uri.TryCreate(hrefElement.GetString(), UriKind.Absolute, out Uri? livePageUri) ||
                !TryNormalizeVaultUri(liveOriginUri, out string? liveOrigin) ||
                !TryNormalizeVaultUri(livePageUri, out string? livePageOrigin) ||
                !expectedOrigin.Equals(liveOrigin, StringComparison.OrdinalIgnoreCase) ||
                !expectedOrigin.Equals(livePageOrigin, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return ReferenceEquals(state, ActiveTab) &&
                state.ActiveNavigationId == navigationId &&
                TryGetPasswordVaultOrigin(state, out currentOrigin) &&
                expectedOrigin.Equals(currentOrigin, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void BlockVaultForOriginChange(string origin)
    {
        _passwordVaultService.RecordSecurityEvent(
            "origin-check",
            "blocked",
            origin);
        _passwordVaultService.Lock();
        PasswordVaultStatusText.Text =
            "Blocked: the page origin changed during the protected operation. Aero Vault was locked immediately.";
    }

    private void RecordBlockedVaultPageAttempt(string action, BrowserTabState? state)
    {
        Uri? uri = state?.CurrentUri ?? state?.PendingUri;
        string? origin = uri?.IsAbsoluteUri == true
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
        _passwordVaultService.RecordSecurityEvent(action, "blocked", origin);
    }

    private async Task RefreshPasswordVaultCountAsync()
    {
        try
        {
            int count = await _passwordVaultService.CountAsync();
            PasswordVaultCountText.Text = count == 1
                ? "1 protected sign-in"
                : $"{count} protected sign-ins";
            ViewSavedPasswordsButton.IsEnabled = count > 0;
            ClearSavedPasswordsButton.IsEnabled = count > 0 && !_isPrivateWindow;
        }
        catch (Exception exception)
        {
            PasswordVaultCountText.Text = $"Vault unavailable: {exception.Message}";
            ViewSavedPasswordsButton.IsEnabled = false;
            ClearSavedPasswordsButton.IsEnabled = false;
        }
    }

    private async Task ClearPasswordVaultAsync()
    {
        IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        await _passwordVaultService.ClearAsync(windowHandle);
        await RefreshPasswordVaultCountAsync();
        RefreshPasswordVaultForActiveTab();
    }
}
