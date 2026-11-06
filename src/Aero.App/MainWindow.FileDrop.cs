using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace Aero.App;

public sealed partial class MainWindow
{
    private const int MaximumDroppedFiles = 30;
    private const long ExternalFileDropDeduplicationWindowMilliseconds = 750;
    private const string ExternalDropObjectGroup = "aero-external-file-drop";

    private BrowserTabState? _lastExternalFileDropState;
    private string? _lastExternalFileDropFingerprint;
    private long _lastExternalFileDropTick;

    private void ConfigureExternalFileDrop(WebView2 browser)
    {
        // Aero owns Explorer-to-page drops through the routed WinUI handlers and
        // the CDP file-input bridge below. Leaving WebView2's native external-drop
        // path enabled at the same time uploads the same file twice on pages that
        // support both paths, so keep exactly one delivery path active.
        browser.AllowDrop = true;
        TrySetExternalDrop(browser, allow: false);
        TrySetExternalDropOnControllerMembers(browser, allow: false);
    }

    private void AttachBrowserContentFileDrop(WebView2 browser)
    {
        // Make attachment idempotent. A restored/discarded tab must never retain
        // two copies of the same routed-event handler.
        DetachBrowserContentFileDrop(browser);
        browser.AllowDrop = true;
        browser.DragEnter += BrowserContentFileDrop_DragEnter;
        browser.DragOver += BrowserContentFileDrop_DragOver;
        browser.DragLeave += BrowserContentFileDrop_DragLeave;
        browser.Drop += BrowserContentFileDrop_Drop;
    }

    private void DetachBrowserContentFileDrop(WebView2 browser)
    {
        browser.DragEnter -= BrowserContentFileDrop_DragEnter;
        browser.DragOver -= BrowserContentFileDrop_DragOver;
        browser.DragLeave -= BrowserContentFileDrop_DragLeave;
        browser.Drop -= BrowserContentFileDrop_Drop;
    }

    private void BrowserContentFileDrop_DragEnter(object sender, DragEventArgs e) =>
        UpdateBrowserContentDropFeedback(e);

    private void BrowserContentFileDrop_DragOver(object sender, DragEventArgs e) =>
        UpdateBrowserContentDropFeedback(e);

    private static void BrowserContentFileDrop_DragLeave(object sender, DragEventArgs e)
    {
        // The webpage owns its own hover state. Nothing needs to remain in Aero's
        // chrome after the pointer leaves the WebView.
    }

    private static void UpdateBrowserContentDropFeedback(DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Upload to this page";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        e.Handled = true;
    }

    private async void BrowserContentFileDrop_Drop(object sender, DragEventArgs e)
    {
        if (sender is not WebView2 browser ||
            browser.Tag is not BrowserTabState state)
        {
            return;
        }

        await HandleBrowserContentFileDropAsync(state, browser, e);
    }

    private void BrowserHostFileDrop_DragEnter(object sender, DragEventArgs e) =>
        UpdateBrowserContentDropFeedback(e);

    private void BrowserHostFileDrop_DragOver(object sender, DragEventArgs e) =>
        UpdateBrowserContentDropFeedback(e);

    private void BrowserHostFileDrop_DragLeave(object sender, DragEventArgs e)
    {
    }

    private async void BrowserHostFileDrop_Drop(object sender, DragEventArgs e)
    {
        BrowserTabState? state = ResolveBrowserHostDropTarget(e);
        if (state is null)
        {
            return;
        }

        await HandleBrowserContentFileDropAsync(state, state.Browser, e);
    }

    private BrowserTabState? ResolveBrowserHostDropTarget(DragEventArgs e)
    {
        if (!IsSplitViewActive)
        {
            return ActiveTab;
        }

        Point point = e.GetPosition(BrowserHost);
        double rightPaneStart = SplitLeftColumn.ActualWidth + SplitDividerColumn.ActualWidth;
        return point.X >= rightPaneStart
            ? _splitRightTab ?? ActiveTab
            : _splitLeftTab ?? ActiveTab;
    }

    private async Task HandleBrowserContentFileDropAsync(
        BrowserTabState state,
        WebView2 browser,
        DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        // WinUI may otherwise turn a dropped archive into a file:// navigation or
        // silently swallow the operation before Chromium reaches the upload UI.
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;

        try
        {
            Point dropPoint = e.GetPosition(browser);
            var items = await e.DataView.GetStorageItemsAsync();
            string[] paths = items
                .OfType<StorageFile>()
                .Select(file => file.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumDroppedFiles)
                .ToArray();

            if (paths.Length == 0 || !TryClaimExternalFileDrop(state, paths))
            {
                return;
            }

            bool injected = await TryInjectDroppedFilesIntoPageAsync(
                state,
                paths,
                dropPoint);

            if (!injected)
            {
                await ShowErrorDialogAsync(
                    "This page cannot accept the dropped file",
                    "Aero could not find a file-upload control on this page. Use the site's upload button for this page.");
            }
        }
        catch (Exception exception)
        {
            await ShowErrorDialogAsync("Could not upload dropped files", exception.Message);
        }
    }

    private bool TryClaimExternalFileDrop(
        BrowserTabState state,
        IReadOnlyList<string> filePaths)
    {
        long now = Environment.TickCount64;
        string fingerprint = string.Join("\0", filePaths);
        long elapsed = now - _lastExternalFileDropTick;

        bool duplicate =
            ReferenceEquals(_lastExternalFileDropState, state) &&
            string.Equals(
                _lastExternalFileDropFingerprint,
                fingerprint,
                StringComparison.OrdinalIgnoreCase) &&
            elapsed >= 0 &&
            elapsed < ExternalFileDropDeduplicationWindowMilliseconds;

        _lastExternalFileDropState = state;
        _lastExternalFileDropFingerprint = fingerprint;
        _lastExternalFileDropTick = now;
        return !duplicate;
    }

    private static async Task<bool> TryInjectDroppedFilesIntoPageAsync(
        BrowserTabState state,
        IReadOnlyList<string> filePaths,
        Point dropPoint)
    {
        CoreWebView2? core = state.Browser.CoreWebView2;
        if (core is null || filePaths.Count == 0)
        {
            return false;
        }

        string x = dropPoint.X.ToString("0.###", CultureInfo.InvariantCulture);
        string y = dropPoint.Y.ToString("0.###", CultureInfo.InvariantCulture);

        // Find the upload input associated with the point under the pointer. This
        // searches open shadow roots too because modern upload components often
        // hide their input inside a web component.
        string locateScript = $$"""
            (() => {
                const dropX = {{x}};
                const dropY = {{y}};
                const pointTarget = document.elementFromPoint(dropX, dropY);
                const allInputs = [];

                const collectInputs = root => {
                    if (!root || !root.querySelectorAll) return;
                    for (const input of root.querySelectorAll('input[type="file"]')) {
                        allInputs.push(input);
                    }
                    for (const element of root.querySelectorAll('*')) {
                        if (element.shadowRoot) collectInputs(element.shadowRoot);
                    }
                };

                collectInputs(document);

                const isFileInput = value =>
                    value instanceof HTMLInputElement &&
                    value.type === 'file' &&
                    !value.disabled;

                let input = isFileInput(pointTarget) ? pointTarget : null;
                let cursor = pointTarget;
                while (!input && cursor) {
                    if (cursor instanceof HTMLLabelElement && isFileInput(cursor.control)) {
                        input = cursor.control;
                        break;
                    }

                    if (cursor.querySelector) {
                        const nested = cursor.querySelector('input[type="file"]:not(:disabled)');
                        if (nested) {
                            input = nested;
                            break;
                        }
                    }

                    cursor = cursor.parentElement ||
                        (cursor.getRootNode && cursor.getRootNode().host) ||
                        null;
                }

                if (!input) {
                    const usable = allInputs.filter(isFileInput);
                    const visible = usable.filter(candidate => {
                        const rect = candidate.getBoundingClientRect();
                        const style = getComputedStyle(candidate);
                        return rect.width > 0 && rect.height > 0 &&
                            style.display !== 'none' && style.visibility !== 'hidden';
                    });

                    const pool = visible.length ? visible : usable;
                    input = pool
                        .map(candidate => {
                            const rect = candidate.getBoundingClientRect();
                            const cx = rect.left + rect.width / 2;
                            const cy = rect.top + rect.height / 2;
                            const distance = Math.hypot(cx - dropX, cy - dropY);
                            return { candidate, distance };
                        })
                        .sort((left, right) => left.distance - right.distance)[0]?.candidate || null;
                }

                window.__aeroExternalDropInput = input;
                window.__aeroExternalDropTarget = pointTarget || input;

                return input
                    ? { found: true, multiple: Boolean(input.multiple) }
                    : { found: false, multiple: false };
            })();
            """;

        string locateResult = await core.ExecuteScriptAsync(locateScript);
        using JsonDocument locateDocument = JsonDocument.Parse(locateResult);
        JsonElement locateRoot = locateDocument.RootElement;
        if (locateRoot.ValueKind != JsonValueKind.Object ||
            !locateRoot.TryGetProperty("found", out JsonElement foundElement) ||
            !foundElement.GetBoolean())
        {
            await ClearExternalDropPageStateAsync(core);
            return false;
        }

        bool multiple = locateRoot.TryGetProperty("multiple", out JsonElement multipleElement) &&
            multipleElement.GetBoolean();
        string[] selectedPaths = multiple
            ? filePaths.ToArray()
            : [filePaths[0]];

        try
        {
            string evaluateParameters = JsonSerializer.Serialize(new
            {
                expression = "window.__aeroExternalDropInput",
                objectGroup = ExternalDropObjectGroup,
                includeCommandLineAPI = false,
                silent = true,
                returnByValue = false
            });

            string evaluateResult = await core.CallDevToolsProtocolMethodAsync(
                "Runtime.evaluate",
                evaluateParameters);
            using JsonDocument evaluateDocument = JsonDocument.Parse(evaluateResult);
            if (!evaluateDocument.RootElement.TryGetProperty("result", out JsonElement remoteObject) ||
                !remoteObject.TryGetProperty("objectId", out JsonElement objectIdElement))
            {
                return false;
            }

            string? objectId = objectIdElement.GetString();
            if (string.IsNullOrWhiteSpace(objectId))
            {
                return false;
            }

            string setFilesParameters = JsonSerializer.Serialize(new
            {
                files = selectedPaths,
                objectId
            });
            await core.CallDevToolsProtocolMethodAsync(
                "DOM.setFileInputFiles",
                setFilesParameters);

            string dispatchScript = """
                (() => {
                    const input = window.__aeroExternalDropInput;
                    if (!(input instanceof HTMLInputElement) || input.type !== 'file') {
                        return false;
                    }

                    try {
                        input.dispatchEvent(new Event('input', {
                            bubbles: true,
                            cancelable: false,
                            composed: true
                        }));
                        input.dispatchEvent(new Event('change', {
                            bubbles: true,
                            cancelable: false,
                            composed: true
                        }));
                        return Boolean(input.files && input.files.length);
                    } finally {
                        delete window.__aeroExternalDropInput;
                        delete window.__aeroExternalDropTarget;
                    }
                })();
                """;

            string dispatchResult = await core.ExecuteScriptAsync(dispatchScript);
            return string.Equals(dispatchResult, "true", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await ReleaseExternalDropObjectGroupAsync(core);
            await ClearExternalDropPageStateAsync(core);
        }
    }

    private static async Task ReleaseExternalDropObjectGroupAsync(CoreWebView2 core)
    {
        try
        {
            await core.CallDevToolsProtocolMethodAsync(
                "Runtime.releaseObjectGroup",
                JsonSerializer.Serialize(new { objectGroup = ExternalDropObjectGroup }));
        }
        catch
        {
            // The page may have navigated while the user was dropping the file.
        }
    }

    private static async Task ClearExternalDropPageStateAsync(CoreWebView2 core)
    {
        try
        {
            await core.ExecuteScriptAsync("""
                (() => {
                    delete window.__aeroExternalDropInput;
                    delete window.__aeroExternalDropTarget;
                })();
                """);
        }
        catch
        {
            // A navigation can destroy the old document between drag and drop.
        }
    }

    private static void TrySetExternalDropOnControllerMembers(object owner, bool allow)
    {
        Type type = owner.GetType();

        try
        {
            PropertyInfo? controllerProperty = type.GetProperty(
                "CoreWebView2Controller",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object? controller = controllerProperty?.GetValue(owner);
            if (controller is not null)
            {
                TrySetExternalDrop(controller, allow);
            }
        }
        catch
        {
            // Continue with the field/property probe below.
        }

        foreach (PropertyInfo property in type.GetProperties(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!property.Name.Contains("Controller", StringComparison.OrdinalIgnoreCase) ||
                property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            try
            {
                object? candidate = property.GetValue(owner);
                if (candidate is not null)
                {
                    TrySetExternalDrop(candidate, allow);
                }
            }
            catch
            {
                // Some projected WinRT properties throw until fully initialized.
            }
        }

        foreach (FieldInfo field in type.GetFields(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!field.Name.Contains("Controller", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                object? candidate = field.GetValue(owner);
                if (candidate is not null)
                {
                    TrySetExternalDrop(candidate, allow);
                }
            }
            catch
            {
                // A runtime-specific backing field is optional.
            }
        }
    }

    private static void TrySetExternalDrop(object target, bool allow)
    {
        try
        {
            PropertyInfo? property = target.GetType().GetProperty(
                "AllowExternalDrop",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.CanWrite == true && property.PropertyType == typeof(bool))
            {
                property.SetValue(target, allow);
            }
        }
        catch
        {
            // External drop defaults to enabled in supported WebView2 runtimes.
        }
    }

    // These handlers belong only to Aero's title/tab strip. A drop here opens
    // the file as a tab; a drop over the webpage is handled above as an upload.
    private void BrowserFileDrop_DragEnter(object sender, DragEventArgs e) =>
        UpdateFileDropFeedback(e);

    private void BrowserFileDrop_DragOver(object sender, DragEventArgs e) =>
        UpdateFileDropFeedback(e);

    private void BrowserFileDrop_DragLeave(object sender, DragEventArgs e) =>
        HideFileDropBadge();

    private void UpdateFileDropFeedback(DragEventArgs e)
    {
        bool hasFiles = e.DataView.Contains(StandardDataFormats.StorageItems);
        bool hasLink = e.DataView.Contains(StandardDataFormats.WebLink) ||
            e.DataView.Contains(StandardDataFormats.Text);

        if (!hasFiles && !hasLink)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            HideFileDropBadge();
            return;
        }

        e.AcceptedOperation = hasFiles
            ? DataPackageOperation.Copy
            : DataPackageOperation.Link;
        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsContentVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;

        FileDropBadgeText.Text = hasFiles
            ? "Drop files on the tab bar to open them in Aero"
            : "Drop the link on the tab bar to open it in Aero";
        ShowFileDropBadge();
        e.Handled = true;
    }

    private async void BrowserFileDrop_Drop(object sender, DragEventArgs e)
    {
        HideFileDropBadge(immediate: true);

        try
        {
            List<Uri> targets = [];
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (StorageFile file in items.OfType<StorageFile>().Take(MaximumDroppedFiles))
                {
                    if (!string.IsNullOrWhiteSpace(file.Path))
                    {
                        targets.Add(CreateLocalFileUri(file.Path));
                    }
                }
            }
            else if (e.DataView.Contains(StandardDataFormats.WebLink))
            {
                Uri link = await e.DataView.GetWebLinkAsync();
                targets.Add(link);
            }
            else if (e.DataView.Contains(StandardDataFormats.Text))
            {
                string text = (await e.DataView.GetTextAsync()).Trim();
                if (Uri.TryCreate(text, UriKind.Absolute, out Uri? link) &&
                    link.Scheme is "http" or "https" or "file")
                {
                    targets.Add(link);
                }
            }

            if (targets.Count == 0)
            {
                return;
            }

            bool reuseCurrentTab = ActiveTab is BrowserTabState active &&
                (active.IsStartPage ||
                 active.CurrentUri is null && active.PendingUri is null ||
                 string.Equals(active.InternalPageAddress, HomeAddress, StringComparison.OrdinalIgnoreCase));

            for (int index = 0; index < targets.Count; index++)
            {
                Uri target = targets[index];
                if (index == 0 && reuseCurrentTab && ActiveTab is BrowserTabState current)
                {
                    Navigate(current, target);
                    SelectTab(current);
                }
                else
                {
                    await CreateTabAsync(target, selectTab: index == 0);
                }
            }

            e.Handled = true;
        }
        catch (Exception exception)
        {
            await ShowErrorDialogAsync("Could not open dropped files", exception.Message);
        }
    }

    private static Uri CreateLocalFileUri(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return new UriBuilder(Uri.UriSchemeFile, string.Empty)
        {
            Path = fullPath
        }.Uri;
    }

    private void ShowFileDropBadge()
    {
        FileDropBadge.Visibility = Visibility.Visible;
        if (_settings.ReduceMotion)
        {
            FileDropBadge.Opacity = 1;
            return;
        }

        AnimateOpacity(FileDropBadge, FileDropBadge.Opacity, 1, 90);
    }

    private void HideFileDropBadge(bool immediate = false)
    {
        if (FileDropBadge.Visibility != Visibility.Visible)
        {
            return;
        }

        if (immediate || _settings.ReduceMotion)
        {
            FileDropBadge.Opacity = 0;
            FileDropBadge.Visibility = Visibility.Collapsed;
            return;
        }

        AnimateOpacity(FileDropBadge, FileDropBadge.Opacity, 0, 90, () =>
        {
            if (FileDropBadge.Opacity <= 0.01)
            {
                FileDropBadge.Visibility = Visibility.Collapsed;
            }
        });
    }
}
