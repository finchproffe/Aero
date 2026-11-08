using Aero.App.Security;
using Microsoft.Web.WebView2.Core;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aero.App;

public sealed partial class MainWindow
{
    // Shorter than Chromium's 30 s cookie-store commit, so most persistent cookies are turned into
    // session cookies before they are ever written to the on-disk Cookies DB.
    private static readonly TimeSpan CookieVaultSweepInterval = TimeSpan.FromSeconds(15);
    // Enumerating every cookie costs a WebView2 round trip and one COM object per cookie, so idle
    // windows sweep rarely. shortcut: a cookie set by background JS without any navigation can sit
    // in the Cookies DB for up to this long; lower it if a site is found to rely on that.
    private static readonly TimeSpan CookieVaultIdleSweepInterval = TimeSpan.FromMinutes(2);

    private sealed class CookieVaultSession
    {
        public required string KeyName { get; init; }
        public required string VaultPath { get; init; }
        public required string WrappedKeyPath { get; init; }
        public required string Context { get; init; }
        public byte[]? DataKey { get; set; }
        public CookieVaultLedger? Ledger { get; set; }
        public Task<bool>? Load { get; set; }
        public Task<bool>? Unlock { get; set; }
        public DateTimeOffset LastSweep { get; set; }
        public bool SweepRunning { get; set; }
        public volatile bool NavigatedSinceSweep;
    }

    // One session per Aero profile, shared by all of its windows: one unlock per launch.
    private static readonly Dictionary<string, CookieVaultSession> CookieVaults = new(StringComparer.OrdinalIgnoreCase);

    private bool CookieVaultAppliesToWindow => !_isPrivateWindow && !_browserProfile.IsGuest;

    // On by default wherever the AeroAppBound service exists (installed MSIX); Hello is the
    // stricter opt-in. Dev builds have no service, so their cookies stay as WebView2 keeps them.
    private bool CookieVaultEnabled => _settings.ProtectCookiesWithHello || AppBoundKey.IsInstalled;

    /// <summary>
    /// Starts reading the key and the vault while WebView2 is still starting, so the service
    /// round trip (cold start included) is hidden behind environment creation.
    /// </summary>
    private void PrefetchCookieVault()
    {
        if (!CookieVaultAppliesToWindow)
        {
            return;
        }

        CookieVaultSession session = GetCookieVaultSession();
        if (!CookieVaultEnabled && !File.Exists(session.WrappedKeyPath))
        {
            return;
        }

        lock (session)
        {
            session.Load ??= LoadCookieVaultAsync(session);
        }
    }

    private CookieVaultSession GetCookieVaultSession()
    {
        lock (CookieVaults)
        {
            if (!CookieVaults.TryGetValue(_browserProfile.Id, out CookieVaultSession? session))
            {
                // Under Security\ so profile backups and restores never carry it.
                string folder = Path.Combine(_browserProfile.StorageRoot, "Security");
                session = new CookieVaultSession
                {
                    KeyName = CookieVaultKey.KeyNameFor(_browserProfile.StorageRoot),
                    VaultPath = Path.Combine(folder, "cookie-vault.bin"),
                    WrappedKeyPath = Path.Combine(folder, "cookie-vault.key"),
                    Context = "aero-cookie-vault-v1:" + _browserProfile.Id
                };
                CookieVaults[_browserProfile.Id] = session;
            }

            return session;
        }
    }

    /// <summary>
    /// Runs before a main-profile tab navigates. With protection on: one unlock per launch (silent,
    /// or a Hello prompt), then the vault's cookies return to WebView2 as in-memory session cookies.
    /// With it off but a vault left behind: they are written back as normal persistent cookies.
    /// </summary>
    private async Task EnsureCookieVaultRestoredAsync(BrowserTabState state)
    {
        if (!CookieVaultAppliesToWindow ||
            state.ContainerId is not null ||
            state.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        CookieVaultSession session = GetCookieVaultSession();
        if (!CookieVaultEnabled && session.DataKey is null && !File.Exists(session.WrappedKeyPath))
        {
            return;
        }

        Task<bool> unlock;
        lock (session)
        {
            unlock = session.Unlock ??= UnlockCookieVaultAsync(session, core);
        }

        if (await unlock && !CookieVaultEnabled)
        {
            await DisableCookieVaultAsync(session, core);
        }
    }

    private async Task<bool> UnlockCookieVaultAsync(CookieVaultSession session, CoreWebView2 core)
    {
        Task<bool> load;
        lock (session)
        {
            load = session.Load ??= LoadCookieVaultAsync(session);
        }

        if (!await load || session.Ledger is null)
        {
            return false;
        }

        bool clearCookiesOnClose = _settings.ClearDataOnClose && _settings.ClearCookiesOnClose;
        foreach (VaultCookie cookie in session.Ledger.Live(DateTimeOffset.UtcNow))
        {
            // Mirror "clear cookies on close": only privacy exceptions survive a restart.
            if (!clearCookiesOnClose ||
                HostMatchesAny(cookie.Domain.TrimStart('.'), _settings.PrivacySiteExceptions))
            {
                AddVaultCookie(core.CookieManager, cookie, persistent: !CookieVaultEnabled);
            }
        }

        return true;
    }

    private async Task<bool> LoadCookieVaultAsync(CookieVaultSession session)
    {
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        bool useHello = _settings.ProtectCookiesWithHello;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(session.VaultPath)!);
            byte[] dataKey;
            if (File.Exists(session.WrappedKeyPath))
            {
                byte[] wrapped = await File.ReadAllBytesAsync(session.WrappedKeyPath);
                dataKey = await Task.Run(() => CookieVaultKey.Unwrap(session.KeyName, wrapped, window));
                if (CookieVaultKey.IsHelloWrapped(wrapped) != useHello && CookieVaultEnabled)
                {
                    await RewrapCookieVaultKeyAsync(session, dataKey);
                }
            }
            else
            {
                dataKey = RandomNumberGenerator.GetBytes(32);
                await RewrapCookieVaultKeyAsync(session, dataKey);
            }

            byte[]? sealedVault = File.Exists(session.VaultPath)
                ? await File.ReadAllBytesAsync(session.VaultPath)
                : null;
            session.Ledger = new CookieVaultLedger(sealedVault is null
                ? []
                : CookieVaultFile.Open(dataKey, sealedVault, session.Context));
            session.DataKey = dataKey;
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or
            UnauthorizedAccessException or COMException or JsonException or PlatformNotSupportedException or
            TimeoutException or System.ComponentModel.Win32Exception)
        {
            // Hello cancelled, service unreachable, or a damaged vault. The vault file is left
            // untouched and no sweep runs this session, so nothing in it can be lost.
            DispatcherQueue.TryEnqueue(() =>
                ShowNoticeToast(Localization.AeroLocalizer.Get("Notice.CookieVaultLocked"), exception.Message));
            return false;
        }
    }

    // The data key never changes; only its wrapping follows the Hello setting.
    private async Task RewrapCookieVaultKeyAsync(CookieVaultSession session, byte[] dataKey)
    {
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        bool useHello = _settings.ProtectCookiesWithHello;
        byte[] wrapped = await Task.Run(() => CookieVaultKey.Wrap(dataKey, useHello, session.KeyName, window));
        await WriteFileAtomicallyAsync(session.WrappedKeyPath, wrapped);
    }

    private async Task RunCookieVaultSweepAsync()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!CookieVaultEnabled || !CookieVaultAppliesToWindow)
        {
            return;
        }

        CookieVaultSession session = GetCookieVaultSession();
        TimeSpan interval = session.NavigatedSinceSweep ? CookieVaultSweepInterval : CookieVaultIdleSweepInterval;
        if (session.DataKey is null ||
            session.Ledger is null ||
            session.SweepRunning ||
            now - session.LastSweep < interval ||
            GetMainProfileCore() is not CoreWebView2 core)
        {
            return;
        }

        session.SweepRunning = true;
        session.LastSweep = now;
        session.NavigatedSinceSweep = false;
        try
        {
            IReadOnlyList<CoreWebView2Cookie> cookies = await core.CookieManager.GetCookiesAsync(string.Empty);
            List<ObservedCookie> observed = [];
            List<CoreWebView2Cookie> persistent = [];
            foreach (CoreWebView2Cookie cookie in cookies)
            {
                // Temporary-cookie and locked sites are meant to sign out on close: never vault them.
                if (IsTemporaryCookieHost(cookie.Domain.TrimStart('.')))
                {
                    continue;
                }

                observed.Add(new ObservedCookie(ToVaultCookie(cookie), cookie.IsSession));
                if (!cookie.IsSession)
                {
                    persistent.Add(cookie);
                }
            }

            CookieVaultReconcileResult result = session.Ledger.Reconcile(observed, now);

            // No await between reading and rewriting: a site could rotate a token in that gap.
            // shortcut: a Set-Cookie landing during the GetCookiesAsync round trip (ms) can still be
            // overwritten by the value read just before; WebView2 has no compare-and-set to avoid it.
            foreach (CoreWebView2Cookie cookie in persistent)
            {
                if (cookie.Expires > now.ToUnixTimeSeconds())
                {
                    CoreWebView2Cookie sessionCopy = core.CookieManager.CopyCookie(cookie);
                    sessionCopy.Expires = -1; // WebView2: -1 = session cookie, kept in memory only.
                    core.CookieManager.AddOrUpdateCookie(sessionCopy);
                }
            }

            foreach (VaultCookie expired in result.ToDelete)
            {
                core.CookieManager.DeleteCookiesWithDomainAndPath(expired.Name, expired.Domain, expired.Path);
            }

            // The session copies live until Aero closes, so saving right after loses nothing.
            if (result.Changed)
            {
                await WriteFileAtomicallyAsync(
                    session.VaultPath,
                    CookieVaultFile.Seal(session.DataKey, session.Ledger.Entries, session.Context));
            }
        }
        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException)
        {
            // Retried on the next tick; the ledger in memory is still the newest state.
        }
        finally
        {
            session.SweepRunning = false;
        }
    }

    /// <summary>Called when the user switches Windows Hello protection on or off in Settings.</summary>
    private async Task OnCookieVaultSettingChangedAsync()
    {
        if (!CookieVaultAppliesToWindow || GetMainProfileCore() is not CoreWebView2 core)
        {
            return;
        }

        CookieVaultSession session = GetCookieVaultSession();
        if (session.DataKey is byte[] dataKey)
        {
            if (!CookieVaultEnabled)
            {
                await DisableCookieVaultAsync(session, core);
                return;
            }

            try
            {
                await RewrapCookieVaultKeyAsync(session, dataKey);
            }
            catch (Exception exception) when (exception is CryptographicException or IOException or
                UnauthorizedAccessException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                // The old wrapping stays valid; the next launch retries the switch.
                ShowNoticeToast(Localization.AeroLocalizer.Get("Notice.CookieVaultLocked"), exception.Message);
            }
        }
        else if (CookieVaultEnabled)
        {
            lock (session)
            {
                // A failed unlock earlier this session gets a fresh attempt.
                if (session.Unlock is { IsCompleted: true })
                {
                    session.Load = null;
                    session.Unlock = null;
                }
            }

            BrowserTabState? tab = _tabs.FirstOrDefault(tab => ReferenceEquals(tab.Browser.CoreWebView2, core));
            if (tab is not null)
            {
                await EnsureCookieVaultRestoredAsync(tab);
            }
        }
    }

    // Turning protection off must not sign the user out: cookies go back as normal persistent ones.
    private async Task DisableCookieVaultAsync(CookieVaultSession session, CoreWebView2 core)
    {
        if (session.Ledger is not null)
        {
            foreach (VaultCookie cookie in session.Ledger.Live(DateTimeOffset.UtcNow))
            {
                AddVaultCookie(core.CookieManager, cookie, persistent: true);
            }
        }

        // Give WebView2 a moment to queue the writes before the only other copy disappears.
        await Task.Delay(TimeSpan.FromSeconds(2));
        TryDeleteFile(session.VaultPath);
        TryDeleteFile(session.VaultPath + ".bak");
        TryDeleteFile(session.WrappedKeyPath);
        if (session.DataKey is not null)
        {
            CryptographicOperations.ZeroMemory(session.DataKey);
        }

        session.DataKey = null;
        session.Ledger = null;
        session.Load = null;
        session.Unlock = null;
    }

    private CoreWebView2? GetMainProfileCore()
    {
        foreach (BrowserTabState tab in _tabs)
        {
            if (tab.ContainerId is null && !tab.IsDiscarded && tab.Browser.CoreWebView2 is CoreWebView2 core)
            {
                return core;
            }
        }

        return null;
    }

    private static VaultCookie ToVaultCookie(CoreWebView2Cookie cookie) => new(
        cookie.Name,
        cookie.Value,
        cookie.Domain,
        cookie.Path,
        cookie.IsSession ? 0 : cookie.Expires,
        cookie.IsSecure,
        cookie.IsHttpOnly,
        (int)cookie.SameSite);

    private static void AddVaultCookie(CoreWebView2CookieManager manager, VaultCookie stored, bool persistent)
    {
        try
        {
            // A leading dot keeps a domain cookie; no dot keeps it host-only (needed for __Host-).
            CoreWebView2Cookie cookie = manager.CreateCookie(stored.Name, stored.Value, stored.Domain, stored.Path);
            cookie.IsSecure = stored.IsSecure;
            cookie.IsHttpOnly = stored.IsHttpOnly;
            cookie.SameSite = (CoreWebView2CookieSameSiteKind)stored.SameSite;
            cookie.Expires = persistent ? stored.ExpiresUnixSeconds : -1;
            manager.AddOrUpdateCookie(cookie);
        }
        catch (ArgumentException)
        {
            // Chromium rejects a few exotic cookies; skipping one beats aborting the restore.
        }
    }

    private static async Task WriteFileAtomicallyAsync(string path, byte[] data)
    {
        string temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, data);
        if (File.Exists(path))
        {
            File.Replace(temporary, path, path + ".bak");
        }
        else
        {
            File.Move(temporary, path);
        }
    }
}
