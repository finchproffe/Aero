using Aero.App.Localization;
using Aero.App.Security;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aero.App;

public sealed partial class MainWindow
{
    private const int PasswordCaptureHeaderSize = 24;
    private const int PasswordCaptureUsernameCapacity = 4 * 1024;
    private const int PasswordCapturePasswordCapacity = 16 * 1024;
    private const int PasswordCaptureBufferSize =
        PasswordCaptureHeaderSize +
        PasswordCaptureUsernameCapacity +
        PasswordCapturePasswordCapacity;
    private const int PasswordCaptureReadyState = 1;
    private static ReadOnlySpan<byte> PasswordCaptureMagic => "AEROCAP1"u8;

    // This bridge never sends a password through WebMessage, JSON, Base64, a script
    // argument, command line, environment variable or file. The host gives the current
    // top-level HTTPS document a one-use writable shared buffer. On submit the page writes
    // UTF-8 bytes into that buffer and posts only a non-secret readiness token.
    //
    // The same bridge receives read-only one-use buffers for protected Fill. Password bytes
    // are therefore no longer embedded in an ExecuteScriptAsync string. Plaintext still has
    // to exist briefly inside the renderer because the destination password field belongs to
    // the web page; Aero's host process no longer creates the previous JSON/Base64 copies.
    private const string PasswordCaptureBridgeScript = """
(() => {
    if (window.top !== window || window.__aeroVaultSharedBridgeInstalled) return;
    window.__aeroVaultSharedBridgeInstalled = true;

    const CAPTURE_KIND = "aero-password-capture-v1";
    const FILL_KIND = "aero-vault-fill-v1";
    const CAPTURE_HEADER = 24;
    const CAPTURE_USERNAME_CAPACITY = 4096;
    const CAPTURE_PASSWORD_CAPACITY = 16384;
    const CAPTURE_TOTAL = CAPTURE_HEADER + CAPTURE_USERNAME_CAPACITY + CAPTURE_PASSWORD_CAPACITY;
    const encoder = new TextEncoder();
    const decoder = new TextDecoder("utf-8", { fatal: false });

    let capture = null;
    let pendingCapture = null;
    let lastSentAt = 0;
    let lastUsername = "";

    const parseMetadata = value => {
        if (value && typeof value === "object") return value;
        if (typeof value !== "string") return null;
        try { return JSON.parse(value); } catch { return null; }
    };

    const release = buffer => {
        try { window.chrome.webview.releaseBuffer(buffer); } catch { }
    };

    const destroyCapture = item => {
        if (!item?.buffer) return;
        try { new Uint8Array(item.buffer).fill(0); } catch { }
        release(item.buffer);
    };

    const writeAscii = (bytes, offset, value) => {
        for (let index = 0; index < value.length; index++) {
            bytes[offset + index] = value.charCodeAt(index);
        }
    };

    const hasAscii = (bytes, offset, value) => {
        for (let index = 0; index < value.length; index++) {
            if (bytes[offset + index] !== value.charCodeAt(index)) return false;
        }
        return true;
    };

    const isUsable = field =>
        field instanceof HTMLInputElement &&
        !field.disabled &&
        !field.readOnly;

    const isVisible = element => {
        if (!isUsable(element)) return false;
        const style = getComputedStyle(element);
        const rect = element.getBoundingClientRect();
        return style.display !== "none" &&
            style.visibility !== "hidden" &&
            rect.width > 0 &&
            rect.height > 0;
    };

    const isUsernameField = field => {
        if (!isUsable(field) || field.type === "password") return false;
        const autocomplete = (field.autocomplete || "").toLowerCase();
        const name = `${field.name || ""} ${field.id || ""}`.toLowerCase();
        return autocomplete === "username" ||
            field.type === "email" ||
            name.includes("user") ||
            name.includes("email") ||
            name.includes("login");
    };

    const setValue = (element, value) => {
        const prototype = Object.getPrototypeOf(element);
        const descriptor = Object.getOwnPropertyDescriptor(prototype, "value");
        if (descriptor?.set) descriptor.set.call(element, value);
        else element.value = value;
        element.dispatchEvent(new Event("input", { bubbles: true }));
        element.dispatchEvent(new Event("change", { bubbles: true }));
    };

    const postFillResult = (operationId, result) => {
        try {
            window.chrome.webview.postMessage({
                type: "aero-vault-fill-result",
                operationId,
                result
            });
        } catch { }
    };

    const handleFillBuffer = (buffer, metadata) => {
        let result = "invalid-buffer";
        let username = "";
        let password = "";
        try {
            if (!metadata || typeof metadata.operationId !== "string" ||
                typeof metadata.origin !== "string" ||
                location.protocol !== "https:" ||
                location.origin.toLowerCase() !== metadata.origin.toLowerCase()) {
                result = "origin-mismatch";
                return;
            }

            const bytes = new Uint8Array(buffer);
            if (bytes.byteLength < 24 || !hasAscii(bytes, 0, "AEROFIL1")) return;

            const view = new DataView(buffer);
            const version = view.getUint32(8, true);
            const usernameLength = view.getUint32(12, true);
            const passwordLength = view.getUint32(16, true);
            const expectedLength = 24 + usernameLength + passwordLength;
            if (version !== 1 || passwordLength === 0 || expectedLength !== bytes.byteLength) return;

            const passwordField = [...document.querySelectorAll('input[type="password"]')]
                .find(isVisible);
            if (!passwordField) {
                result = "no-password-field";
                return;
            }

            const form = passwordField.form || passwordField.closest("form") || document;
            if (form instanceof HTMLFormElement && form.action) {
                const action = new URL(form.action, location.href);
                if (action.protocol !== "https:" ||
                    action.origin.toLowerCase() !== metadata.origin.toLowerCase()) {
                    result = "form-action-mismatch";
                    return;
                }
            }

            const usernameField = [
                ...form.querySelectorAll(
                    'input[autocomplete="username"], input[type="email"], input[name*="user" i], input[name*="email" i], input[type="text"]')
            ].find(isVisible);

            username = decoder.decode(bytes.subarray(24, 24 + usernameLength));
            password = decoder.decode(bytes.subarray(24 + usernameLength, expectedLength));
            if (usernameField && username.length > 0) setValue(usernameField, username);
            setValue(passwordField, password);
            result = "filled";
        } catch {
            result = "bridge-error";
        } finally {
            username = "";
            password = "";
            release(buffer);
            postFillResult(metadata?.operationId || "", result);
        }
    };

    const acceptCaptureBuffer = (buffer, metadata) => {
        try {
            if (!metadata || typeof metadata.captureId !== "string" ||
                typeof metadata.origin !== "string" ||
                location.protocol !== "https:" ||
                location.origin.toLowerCase() !== metadata.origin.toLowerCase() ||
                buffer.byteLength !== CAPTURE_TOTAL) {
                release(buffer);
                return;
            }

            if (capture?.buffer) release(capture.buffer);
            capture = {
                buffer,
                captureId: metadata.captureId,
                origin: metadata.origin
            };
        } catch {
            release(buffer);
        }
    };

    window.chrome?.webview?.addEventListener("sharedbufferreceived", event => {
        let buffer = null;
        try {
            const metadata = parseMetadata(event.additionalData);
            buffer = event.getBuffer();
            if (metadata?.kind === FILL_KIND) {
                handleFillBuffer(buffer, metadata);
                buffer = null;
                return;
            }
            if (metadata?.kind === CAPTURE_KIND) {
                acceptCaptureBuffer(buffer, metadata);
                buffer = null;
                return;
            }
        } catch { }
        if (buffer) release(buffer);
    });

    window.chrome?.webview?.addEventListener("message", event => {
        const message = parseMetadata(event.data);
        if (message?.type !== "aero-password-buffer-consumed" ||
            typeof message.captureId !== "string" ||
            pendingCapture?.captureId !== message.captureId) return;

        const consumed = pendingCapture;
        pendingCapture = null;
        destroyCapture(consumed);
    });

    document.addEventListener("input", event => {
        const field = event.target;
        if (isUsernameField(field) && field.value) lastUsername = field.value;
    }, true);

    const report = submittedForm => {
        let consumedCapture = null;
        let committed = false;
        try {
            if (!capture || location.protocol !== "https:" || !window.chrome?.webview) return;

            const now = Date.now();
            if (now - lastSentAt < 1500) return;

            const form = submittedForm instanceof HTMLFormElement ? submittedForm : null;
            const scope = form || document;
            const passwordField = [...scope.querySelectorAll('input[type="password"]')]
                .filter(isUsable)
                .find(field => field.value.length > 0);
            if (!passwordField) return;

            const actualForm = form || passwordField.form || passwordField.closest("form");
            if (actualForm?.action) {
                const action = new URL(actualForm.action, location.href);
                if (action.protocol !== "https:" ||
                    action.origin.toLowerCase() !== location.origin.toLowerCase()) return;
            }

            const usernameFields = [...(actualForm || document).querySelectorAll("input")]
                .filter(isUsernameField);
            const usernameField = usernameFields.find(field => field.value.length > 0);
            const usernameValue = usernameField?.value || lastUsername || "";

            consumedCapture = capture;
            capture = null;
            const bytes = new Uint8Array(consumedCapture.buffer);
            const view = new DataView(consumedCapture.buffer);
            bytes.fill(0);

            const usernameResult = encoder.encodeInto(
                usernameValue,
                bytes.subarray(CAPTURE_HEADER, CAPTURE_HEADER + CAPTURE_USERNAME_CAPACITY));
            const passwordResult = encoder.encodeInto(
                passwordField.value,
                bytes.subarray(
                    CAPTURE_HEADER + CAPTURE_USERNAME_CAPACITY,
                    CAPTURE_TOTAL));
            if (passwordResult.written === 0) return;

            writeAscii(bytes, 0, "AEROCAP1");
            view.setUint32(8, 1, true);
            view.setUint32(16, usernameResult.written, true);
            view.setUint32(20, passwordResult.written, true);
            // State is committed last so the host never accepts a partially written record.
            view.setUint32(12, 1, true);

            lastSentAt = now;
            lastUsername = "";
            window.chrome.webview.postMessage({
                type: "aero-password-buffer-ready",
                captureId: consumedCapture.captureId
            });
            committed = true;

            if (pendingCapture) destroyCapture(pendingCapture);
            pendingCapture = consumedCapture;
            const pendingCaptureId = consumedCapture.captureId;
            consumedCapture = null;
            // The host normally acknowledges immediately after copying and zeroing.
            // This timeout is a renderer-side fallback if the host message handler dies.
            setTimeout(() => {
                if (pendingCapture?.captureId !== pendingCaptureId) return;
                const expired = pendingCapture;
                pendingCapture = null;
                destroyCapture(expired);
            }, 5000);
        } catch {
            // Password detection must never interfere with the page's sign-in.
        } finally {
            if (consumedCapture?.buffer) destroyCapture(consumedCapture);
        }
    };

    document.addEventListener("submit", event => report(event.target), true);
    document.addEventListener("keydown", event => {
        if (event.key !== "Enter") return;
        const field = event.target;
        if (!(field instanceof HTMLInputElement)) return;
        report(field.form || field.closest("form"));
    }, true);
    document.addEventListener("click", event => {
        const target = event.target instanceof Element ? event.target : null;
        const control = target?.closest(
            'button, input[type="submit"], input[type="image"], [role="button"]');
        if (!control) return;

        const isSubmit = control instanceof HTMLInputElement ||
            (control instanceof HTMLButtonElement && control.type === "submit") ||
            control.getAttribute("role") === "button";
        if (!isSubmit) return;

        report(control.form || control.closest("form"));
    }, true);
})();
""";

    private int _passwordSavePromptOpen;
    private string? _lastPasswordPromptOrigin;
    private DateTimeOffset _lastPasswordPromptUtc;

    private async Task RegisterPasswordCaptureBridgeAsync(BrowserTabState state)
    {
        if (state.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PasswordCaptureBridgeScript);
        }
        catch
        {
            // The controller may be replaced while a discarded tab is restored.
        }
    }

    private async Task EnsurePasswordCaptureBridgeOnCurrentDocumentAsync(BrowserTabState state)
    {
        if (!_tabs.Contains(state) || state.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        try
        {
            // Static bridge only. No password, username or secret token is included.
            await core.ExecuteScriptAsync(PasswordCaptureBridgeScript);
        }
        catch
        {
            // Internal and browser error documents may reject script execution.
        }
    }

    private Task PreparePasswordCaptureSharedBufferAsync(BrowserTabState state)
    {
        ResetPasswordCaptureSharedBuffer(state);
        if (_isPrivateWindow ||
            !_settings.SavePasswords ||
            !_tabs.Contains(state) ||
            state.Browser.CoreWebView2 is not CoreWebView2 core ||
            !TryNormalizeCapturedOrigin(core.Source, out string origin))
        {
            return Task.CompletedTask;
        }

        Uri originUri = new(origin);
        if (PasswordVaultService.IsSuspiciousHost(originUri.IdnHost, out _))
        {
            return Task.CompletedTask;
        }

        CoreWebView2SharedBuffer? buffer = null;
        try
        {
            buffer = core.Environment.CreateSharedBuffer((ulong)PasswordCaptureBufferSize);
            ZeroSharedBuffer(buffer, PasswordCaptureBufferSize);

            Guid captureId = Guid.NewGuid();
            state.PasswordCaptureBuffer = buffer;
            state.PasswordCaptureBufferId = captureId;
            state.PasswordCaptureDocumentIdentity = state.SecurityDocumentIdentity;
            state.PasswordCaptureOrigin = origin;

            string metadata = JsonSerializer.Serialize(new
            {
                kind = "aero-password-capture-v1",
                captureId = captureId.ToString("N"),
                origin
            });
            core.PostSharedBufferToScript(
                buffer,
                CoreWebView2SharedBufferAccess.ReadWrite,
                metadata);
            buffer = null;
        }
        catch
        {
            if (ReferenceEquals(state.PasswordCaptureBuffer, buffer))
            {
                ResetPasswordCaptureSharedBuffer(state);
            }
            else if (buffer is not null)
            {
                ZeroAndDisposeSharedBuffer(buffer, PasswordCaptureBufferSize);
            }
        }

        return Task.CompletedTask;
    }

    private bool TryHandlePasswordCaptureMessage(
        BrowserTabState state,
        CoreWebView2WebMessageReceivedEventArgs args,
        JsonElement root,
        string? messageType)
    {
        // Fail closed if an obsolete bridge or injected script tries the old plaintext
        // JSON channel. The payload is never accepted or copied into Aero Vault.
        if (string.Equals(messageType, "aero-password-submitted", StringComparison.Ordinal))
        {
            _passwordVaultService.RecordSecurityEvent("save-offer", "blocked-legacy-json", args.Source);
            return true;
        }

        if (!string.Equals(messageType, "aero-password-buffer-ready", StringComparison.Ordinal))
        {
            return false;
        }

        if (_isPrivateWindow || !_settings.SavePasswords || !_tabs.Contains(state))
        {
            ResetPasswordCaptureSharedBuffer(state);
            return true;
        }

        string captureIdText = root.TryGetProperty("captureId", out JsonElement captureIdElement) &&
            captureIdElement.ValueKind == JsonValueKind.String
                ? captureIdElement.GetString() ?? string.Empty
                : string.Empty;
        if (!Guid.TryParseExact(captureIdText, "N", out Guid captureId) ||
            captureId != state.PasswordCaptureBufferId ||
            state.PasswordCaptureBuffer is not CoreWebView2SharedBuffer buffer ||
            state.PasswordCaptureOrigin is not string expectedOrigin ||
            state.PasswordCaptureDocumentIdentity != state.SecurityDocumentIdentity ||
            !TryNormalizeCapturedOrigin(args.Source, out string sourceOrigin) ||
            !expectedOrigin.Equals(sourceOrigin, StringComparison.OrdinalIgnoreCase))
        {
            _passwordVaultService.RecordSecurityEvent("save-offer", "blocked-buffer-binding", args.Source);
            ResetPasswordCaptureSharedBuffer(state);
            return true;
        }

        // Consume exactly once before reading. A duplicate WebMessage cannot make Aero read
        // the same shared-memory record twice.
        state.PasswordCaptureBuffer = null;
        state.PasswordCaptureBufferId = Guid.Empty;
        state.PasswordCaptureDocumentIdentity = Guid.Empty;
        state.PasswordCaptureOrigin = null;

        byte[]? usernameBytes = null;
        byte[]? passwordBytes = null;
        try
        {
            using var randomAccessStream = buffer.OpenStream();
            using Stream stream = randomAccessStream.AsStream();
            Span<byte> header = stackalloc byte[PasswordCaptureHeaderSize];
            stream.Position = 0;
            stream.ReadExactly(header);

            int version = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
            int stateValue = BinaryPrimitives.ReadInt32LittleEndian(header[12..16]);
            int usernameLength = BinaryPrimitives.ReadInt32LittleEndian(header[16..20]);
            int passwordLength = BinaryPrimitives.ReadInt32LittleEndian(header[20..24]);
            if (!header[..8].SequenceEqual(PasswordCaptureMagic) ||
                version != 1 ||
                stateValue != PasswordCaptureReadyState ||
                usernameLength < 0 ||
                usernameLength > PasswordCaptureUsernameCapacity ||
                passwordLength <= 0 ||
                passwordLength > PasswordCapturePasswordCapacity)
            {
                _passwordVaultService.RecordSecurityEvent("save-offer", "blocked-invalid-buffer", expectedOrigin);
                return true;
            }

            usernameBytes = new byte[usernameLength];
            passwordBytes = new byte[passwordLength];
            stream.Position = PasswordCaptureHeaderSize;
            stream.ReadExactly(usernameBytes);
            stream.Position = PasswordCaptureHeaderSize + PasswordCaptureUsernameCapacity;
            stream.ReadExactly(passwordBytes);
        }
        catch
        {
            if (usernameBytes is not null)
            {
                CryptographicOperations.ZeroMemory(usernameBytes);
            }
            if (passwordBytes is not null)
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
            _passwordVaultService.RecordSecurityEvent("save-offer", "blocked-buffer-read", expectedOrigin);
            return true;
        }
        finally
        {
            ZeroAndDisposeSharedBuffer(buffer, PasswordCaptureBufferSize);
            AcknowledgePasswordCaptureBuffer(state, captureId);
        }

        Uri originUri = new(expectedOrigin);
        if (PasswordVaultService.IsSuspiciousHost(originUri.IdnHost, out _))
        {
            CryptographicOperations.ZeroMemory(usernameBytes);
            CryptographicOperations.ZeroMemory(passwordBytes);
            _passwordVaultService.RecordSecurityEvent("save-offer", "blocked", expectedOrigin);
            return true;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (string.Equals(_lastPasswordPromptOrigin, expectedOrigin, StringComparison.OrdinalIgnoreCase) &&
            now - _lastPasswordPromptUtc < TimeSpan.FromSeconds(15))
        {
            CryptographicOperations.ZeroMemory(usernameBytes);
            CryptographicOperations.ZeroMemory(passwordBytes);
            return true;
        }

        if (System.Threading.Interlocked.CompareExchange(
                ref _passwordSavePromptOpen,
                1,
                0) != 0)
        {
            CryptographicOperations.ZeroMemory(usernameBytes);
            CryptographicOperations.ZeroMemory(passwordBytes);
            return true;
        }

        _lastPasswordPromptOrigin = expectedOrigin;
        _lastPasswordPromptUtc = now;

        bool queued = RootGrid.DispatcherQueue.TryEnqueue(async () =>
            await PromptToSaveCapturedPasswordAsync(
                state,
                expectedOrigin,
                usernameBytes,
                passwordBytes));
        if (!queued)
        {
            CryptographicOperations.ZeroMemory(usernameBytes);
            CryptographicOperations.ZeroMemory(passwordBytes);
            System.Threading.Interlocked.Exchange(ref _passwordSavePromptOpen, 0);
        }

        return true;
    }

    private async Task PromptToSaveCapturedPasswordAsync(
        BrowserTabState sourceTab,
        string origin,
        byte[] usernameBytes,
        byte[] passwordBytes)
    {
        try
        {
            await Task.Delay(250);
            if (_isPrivateWindow ||
                !_settings.SavePasswords ||
                !_tabs.Contains(sourceTab) ||
                RootGrid.XamlRoot is null)
            {
                return;
            }

            string host = new Uri(origin).IdnHost;
            string username = Encoding.UTF8.GetString(usernameBytes);
            string usernameLabel = string.IsNullOrWhiteSpace(username)
                ? AeroLocalizer.Get("Vault.NoUsername")
                : AeroLocalizer.Format("Vault.SavePromptUsername", username);
            username = string.Empty;

            ContentDialog dialog = new()
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = AeroLocalizer.Format("Vault.SavePromptTitle", host),
                Content = new StackPanel
                {
                    Spacing = 7,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = AeroLocalizer.Get("Vault.SavePromptBody"),
                            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = usernameLabel,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                        }
                    }
                },
                PrimaryButtonText = AeroLocalizer.Get("Vault.SaveAction"),
                CloseButtonText = AeroLocalizer.Get("Vault.NotNow"),
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                _passwordVaultService.RecordSecurityEvent("save-offer", "dismissed", origin);
                return;
            }

            IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            await _passwordVaultService.SavePasswordAsync(
                origin,
                usernameBytes,
                passwordBytes,
                windowHandle);

            string savedStatus = AeroLocalizer.Format("Vault.SavedFor", host);
            PasswordVaultStatusText.Text = savedStatus;
            PasswordsStatusText.Text = savedStatus;
            await RefreshPasswordVaultCountAsync();
            RefreshPasswordVaultForActiveTab();
        }
        catch
        {
            string message = AeroLocalizer.Get("Vault.SaveFailedGeneric");
            PasswordVaultStatusText.Text = message;
            PasswordsStatusText.Text = message;
            _passwordVaultService.RecordSecurityEvent("save-offer", "failed", origin);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(usernameBytes);
            CryptographicOperations.ZeroMemory(passwordBytes);
            System.Threading.Interlocked.Exchange(ref _passwordSavePromptOpen, 0);

            if (_tabs.Contains(sourceTab) &&
                sourceTab.PasswordCaptureDocumentIdentity == Guid.Empty)
            {
                await EnsurePasswordCaptureBridgeOnCurrentDocumentAsync(sourceTab);
                await PreparePasswordCaptureSharedBufferAsync(sourceTab);
            }
        }
    }

    private static void AcknowledgePasswordCaptureBuffer(
        BrowserTabState state,
        Guid captureId)
    {
        try
        {
            state.Browser.CoreWebView2?.PostWebMessageAsJson(
                JsonSerializer.Serialize(new
                {
                    type = "aero-password-buffer-consumed",
                    captureId = captureId.ToString("N")
                }));
        }
        catch
        {
            // The renderer-side five-second fallback still zeroes/releases its view.
        }
    }

    private static void ResetPasswordCaptureSharedBuffer(BrowserTabState state)
    {
        CoreWebView2SharedBuffer? buffer = state.PasswordCaptureBuffer;
        state.PasswordCaptureBuffer = null;
        state.PasswordCaptureBufferId = Guid.Empty;
        state.PasswordCaptureDocumentIdentity = Guid.Empty;
        state.PasswordCaptureOrigin = null;
        if (buffer is not null)
        {
            ZeroAndDisposeSharedBuffer(buffer, PasswordCaptureBufferSize);
        }
    }

    private static bool TryNormalizeCapturedOrigin(string value, out string origin)
    {
        origin = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
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
}
