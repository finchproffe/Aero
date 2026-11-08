using Aero.App.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Aero.App;

/// <summary>
/// Free sync through the user's own cloud folder (see <see cref="FolderSync"/>). Bookmarks sync on
/// their own; passwords only on request, because every Aero Vault read or write needs Windows Hello.
/// shortcut: history, quick links and bookmark order are not synced; add them as SyncItem kinds.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan FolderSyncInterval = TimeSpan.FromMinutes(2);
    private static DateTimeOffset _lastFolderSync;
    private static bool _folderSyncRunning;

    private string SyncKeyPath => Path.Combine(_browserProfile.StorageRoot, "Security", "sync-library.key");

    private bool FolderSyncApplies =>
        !_isPrivateWindow && !_browserProfile.IsGuest && !string.IsNullOrEmpty(_settings.SyncFolder);

    private string EnsureSyncDeviceId()
    {
        if (string.IsNullOrEmpty(_settings.SyncDeviceId))
        {
            _settings.SyncDeviceId = Guid.NewGuid().ToString("N");
            _settings.Save();
        }

        return _settings.SyncDeviceId;
    }

    private async Task RunFolderSyncAsync(bool force = false)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string folder = _settings.SyncFolder;
        if (!FolderSyncApplies ||
            _folderSyncRunning ||
            !force && now - _lastFolderSync < FolderSyncInterval ||
            !Directory.Exists(folder) ||
            !File.Exists(SyncKeyPath))
        {
            return;
        }

        _folderSyncRunning = true;
        _lastFolderSync = now;
        byte[]? key = null;
        try
        {
            string deviceId = EnsureSyncDeviceId();
            string ownPath = FolderSync.FilePath(folder, deviceId, FolderSync.LibraryExtension);
            string context = FolderSync.Context(FolderSync.LibraryExtension, deviceId);
            byte[] wrapped = await File.ReadAllBytesAsync(SyncKeyPath);
            key = await Task.Run(() => UnwrapSyncKey(wrapped));
            byte[] libraryKey = key;
            (List<SyncItem> previous, List<List<SyncItem>> others) = await Task.Run(() => (
                File.Exists(ownPath) ? FolderSync.ReadFile(ownPath, context, libraryKey) : [],
                FolderSync.ReadOthers(folder, deviceId, FolderSync.LibraryExtension, libraryKey)));

            Dictionary<string, byte[]> current = new(StringComparer.Ordinal);
            foreach (Data.BookmarkEntry bookmark in _dataStore.Library.Bookmarks)
            {
                current.TryAdd(bookmark.Url, Encoding.UTF8.GetBytes(bookmark.Title));
            }

            Dictionary<string, SyncItem> merged = FolderSync.Merge(
                [FolderSync.Stamp(current, previous, now.ToUnixTimeSeconds()), .. others]);
            if (ApplySyncedBookmarks(merged, current))
            {
                RenderBookmarksBar();
                UpdateBookmarkButton();
                if (_libraryPaneOpen)
                {
                    RefreshLibraryPane();
                }
            }

            List<SyncItem> publish = [.. merged.Values.OrderBy(item => item.Key, StringComparer.Ordinal)];
            // Rewrite only on change: every write is an upload for the user's cloud client.
            if (!FolderSync.Serialize(publish).AsSpan().SequenceEqual(FolderSync.Serialize(previous)))
            {
                await Task.Run(() => FolderSync.WriteFile(ownPath, context, libraryKey, publish));
            }
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or
            UnauthorizedAccessException or InvalidDataException or TimeoutException or
            System.ComponentModel.Win32Exception)
        {
            // The cloud folder can be offline or mid-upload; the next round retries.
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            _folderSyncRunning = false;
        }
    }

    // Returns whether the local bookmarks changed.
    private bool ApplySyncedBookmarks(Dictionary<string, SyncItem> merged, Dictionary<string, byte[]> current)
    {
        bool changed = false;
        foreach (SyncItem item in merged.Values.ToList())
        {
            bool present = current.TryGetValue(item.Key, out byte[]? localTitle);
            if (item.Deleted)
            {
                if (present)
                {
                    _dataStore.RemoveBookmark(item.Key);
                    changed = true;
                }

                continue;
            }

            if (present && localTitle!.AsSpan().SequenceEqual(item.Payload))
            {
                continue;
            }

            try
            {
                string title = Encoding.UTF8.GetString(item.Payload);
                if (_dataStore.Library.Bookmarks.FirstOrDefault(b => b.Url == item.Key) is { } existing)
                {
                    _dataStore.UpdateBookmark(existing.Id, existing.Url, title, existing.Icon);
                }
                else
                {
                    _dataStore.AddBookmark(item.Key, title);
                }

                changed = true;
            }
            catch (Exception exception) when (exception is ArgumentException or UriFormatException or InvalidOperationException)
            {
                // Not publishing it keeps a bookmark Aero cannot store from turning into a deletion later.
                merged.Remove(item.Key);
            }
        }

        return changed;
    }

    private async Task SetupFolderSyncAsync(BrowserTabState state)
    {
        FolderPicker picker = new();
        picker.FileTypeFilter.Add("*");
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
        StorageFolder? picked = await picker.PickSingleFolderAsync();
        if (picked is null)
        {
            return;
        }

        string folder = Path.Combine(picked.Path, "Aero Sync");
        bool joining = File.Exists(Path.Combine(folder, FolderSync.HeaderFileName));
        string? passphrase = await AskSyncPassphraseAsync(confirm: !joining);
        if (passphrase is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            using FolderSync.Keys keys = await Task.Run(() => FolderSync.OpenOrCreate(folder, passphrase));
            byte[] wrapped = await Task.Run(() => WrapSyncKey(keys.Library));
            Directory.CreateDirectory(Path.GetDirectoryName(SyncKeyPath)!);
            await File.WriteAllBytesAsync(SyncKeyPath, wrapped);
        }
        catch (CryptographicException)
        {
            await PostSettingsStatusAsync(state, "Фраза не подходит к этой папке синхронизации.", isError: true);
            return;
        }

        _settings.SyncFolder = folder;
        EnsureSyncDeviceId();
        _settings.Save();
        await RunFolderSyncAsync(force: true);
        await RefreshSettingsPageAsync(state, joining
            ? "Компьютер подключён. Закладки синхронизируются, пароли — кнопкой «Синхронизировать пароли»."
            : "Синхронизация включена. На других компьютерах выберите ту же папку и ту же фразу.");
    }

    private async Task DisableFolderSyncAsync(BrowserTabState state)
    {
        _settings.SyncFolder = string.Empty;
        _settings.Save();
        try
        {
            File.Delete(SyncKeyPath);
        }
        catch (IOException)
        {
        }

        await RefreshSettingsPageAsync(state, "Синхронизация отключена. Файлы в облачной папке не тронуты.");
    }

    /// <summary>
    /// Two-way password sync. The passphrase is asked every time because the password key is never
    /// stored; Windows Hello confirms reading and changing the vault. On a new PC this is also the
    /// recovery path: an empty vault is refilled from the other devices and never wipes them.
    /// </summary>
    private async Task SyncPasswordsAsync(BrowserTabState state)
    {
        string folder = _settings.SyncFolder;
        if (!FolderSyncApplies || !Directory.Exists(folder))
        {
            await PostSettingsStatusAsync(state, "Сначала выберите папку синхронизации.", isError: true);
            return;
        }

        string? passphrase = await AskSyncPassphraseAsync(confirm: false);
        if (passphrase is null)
        {
            return;
        }

        FolderSync.Keys keys;
        try
        {
            keys = await Task.Run(() => FolderSync.OpenOrCreate(folder, passphrase));
        }
        catch (CryptographicException)
        {
            await PostSettingsStatusAsync(state, "Фраза не подходит к этой папке синхронизации.", isError: true);
            return;
        }

        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        string deviceId = EnsureSyncDeviceId();
        string ownPath = FolderSync.FilePath(folder, deviceId, FolderSync.PasswordsExtension);
        string context = FolderSync.Context(FolderSync.PasswordsExtension, deviceId);
        Dictionary<string, byte[]> current = new(StringComparer.Ordinal);
        List<SyncItem> previous = [];
        List<List<SyncItem>> others = [];
        int saved = 0;
        int deleted = 0;
        try
        {
            if (await _passwordVaultService.CountAsync() > 0)
            {
                using PasswordExportLease lease = await _passwordVaultService.GetAllPasswordsJitAsync(handle);
                foreach (PasswordExportEntryLease entry in lease.Entries)
                {
                    current[Encoding.UTF8.GetString(entry.OriginUtf8.Span)] =
                        PackCredential(entry.UsernameUtf8.Span, entry.PasswordUtf8);
                }
            }

            byte[] passwordKey = keys.Passwords;
            (previous, others) = await Task.Run(() => (
                File.Exists(ownPath) ? FolderSync.ReadFile(ownPath, context, passwordKey) : [],
                FolderSync.ReadOthers(folder, deviceId, FolderSync.PasswordsExtension, passwordKey)));
            Dictionary<string, SyncItem> merged = FolderSync.Merge(
                [FolderSync.Stamp(current, previous, DateTimeOffset.UtcNow.ToUnixTimeSeconds()), .. others]);

            // Another device's file is input from outside this PC: same origin rules as a CSV import.
            foreach (SyncItem item in merged.Values.Where(item => !item.Deleted && !IsSyncableOrigin(item.Key)).ToList())
            {
                merged.Remove(item.Key);
            }

            List<SyncItem> toSave = [.. merged.Values.Where(item =>
                !item.Deleted &&
                !(current.TryGetValue(item.Key, out byte[]? local) && local.AsSpan().SequenceEqual(item.Payload)))];
            if (toSave.Count > 0)
            {
                await using PasswordVaultService.PasswordVaultWriteSession session =
                    await _passwordVaultService.BeginWriteSessionAsync(
                        handle,
                        "Sync protected sign-ins from your other computers",
                        forceReauthentication: true);
                foreach (SyncItem item in toSave)
                {
                    int userLength = BinaryPrimitives.ReadUInt16LittleEndian(item.Payload);
                    await session.SavePasswordAsync(
                        item.Key,
                        item.Payload.AsMemory(2, userLength),
                        item.Payload.AsMemory(2 + userLength));
                    saved++;
                }

                await session.CommitAsync();
            }

            foreach (SyncItem item in merged.Values.Where(item => item.Deleted && current.ContainsKey(item.Key)))
            {
                if (await _passwordVaultService.DeletePasswordAsync(item.Key, handle))
                {
                    deleted++;
                }
            }

            List<SyncItem> publish = [.. merged.Values.OrderBy(item => item.Key, StringComparer.Ordinal)];
            await Task.Run(() => FolderSync.WriteFile(ownPath, context, passwordKey, publish));
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or
            UnauthorizedAccessException or InvalidDataException or COMException or
            PasswordVaultVerificationException or OperationCanceledException)
        {
            await PostSettingsStatusAsync(state, "Пароли не синхронизированы: " + exception.Message, isError: true);
            return;
        }
        finally
        {
            foreach (byte[] payload in current.Values.Concat(previous.Select(i => i.Payload)).Concat(others.SelectMany(s => s).Select(i => i.Payload)))
            {
                CryptographicOperations.ZeroMemory(payload);
            }

            keys.Dispose();
        }

        await RefreshPasswordVaultCountAsync();
        await PostSettingsStatusAsync(state, $"Пароли синхронизированы: получено {saved}, удалено {deleted}.");
    }

    private static bool IsSyncableOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 &&
        !PasswordVaultService.IsSuspiciousHost(uri.IdnHost, out _);

    // username length (u16) | username | password
    private static byte[] PackCredential(ReadOnlySpan<byte> username, ReadOnlySpan<byte> password)
    {
        byte[] packed = new byte[2 + username.Length + password.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packed, checked((ushort)username.Length));
        username.CopyTo(packed.AsSpan(2));
        password.CopyTo(packed.AsSpan(2 + username.Length));
        return packed;
    }

    // The stored key only opens bookmarks, which Aero keeps unencrypted on disk anyway, so plain
    // DPAPI is an acceptable fallback on builds without the app-bound service.
    private static byte[] WrapSyncKey(byte[] key) => AppBoundKey.IsInstalled
        ? [(byte)'A', .. AppBoundKey.Wrap(key)]
        : [(byte)'D', .. ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser)];

    private static byte[] UnwrapSyncKey(byte[] wrapped) => (wrapped.Length > 1 ? wrapped[0] : 0) switch
    {
        (byte)'A' => AppBoundKey.Unwrap(wrapped[1..]),
        (byte)'D' => ProtectedData.Unprotect(wrapped[1..], null, DataProtectionScope.CurrentUser),
        _ => throw new CryptographicException("Unknown sync key format.")
    };

    private async Task<string?> AskSyncPassphraseAsync(bool confirm)
    {
        PasswordBox first = new() { Header = "Фраза синхронизации" };
        PasswordBox second = new() { Header = "Повторите фразу" };
        TextBlock error = new() { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] };
        StackPanel content = new() { Spacing = 10, MinWidth = 360 };
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = confirm
                ? "Придумайте фразу не короче 10 символов. Она шифрует всё в облачной папке и нужна на каждом компьютере. Aero её не хранит и не сможет восстановить."
                : "Введите фразу синхронизации, которую вы задали на первом компьютере."
        });
        content.Children.Add(first);
        if (confirm)
        {
            content.Children.Add(second);
        }

        content.Children.Add(error);
        ContentDialog dialog = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Синхронизация Aero",
            Content = content,
            PrimaryButtonText = "Продолжить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!FolderSync.IsPassphraseStrongEnough(first.Password))
            {
                error.Text = "Нужно не меньше 10 символов.";
                args.Cancel = true;
            }
            else if (confirm && first.Password != second.Password)
            {
                error.Text = "Фразы не совпадают.";
                args.Cancel = true;
            }
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? first.Password : null;
    }
}
