using Aero.App.Data;
using Aero.App.Security;
using Aero.App.Settings;
using Aero.App.Speed;
using Aero.Core.Navigation;
using Aero.Navigation;
using Aero.Search;
using Aero.Shield;
using Aero.WebView;
using Aero.Extensions;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace Aero.App;

public sealed partial class MainWindow : Window
{
    private double AddTabButtonSpace => AddTabButton.Width + AddTabButton.Margin.Left + AddTabButton.Margin.Right + TabStripHost.Spacing;
    private const double TitleBarMinimumDragWidth = 140;
    private const int FastAnimationDuration = 120;
    private const int StandardAnimationDuration = 220;
    private const string HomeBackgroundVirtualHost = "aero-custom.local";
    private bool _compactNavigation;
    private bool _narrowNavigation;

    private void NavigationToolbarGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < 720;
        bool narrow = e.NewSize.Width < 560;
        if (_compactNavigation == compact && _narrowNavigation == narrow)
        {
            return;
        }

        _compactNavigation = compact;
        _narrowNavigation = narrow;
        ApplyResponsiveToolbar();
    }

    private void ApplyResponsiveToolbar()
    {
        HomeButton.Visibility = _settings.ShowHomeButton && !_compactNavigation
            ? Visibility.Visible
            : Visibility.Collapsed;
        ForwardButton.Visibility = _narrowNavigation
            ? Visibility.Collapsed
            : Visibility.Visible;
        PinnedExtensionsPanel.Visibility = _compactNavigation
            ? Visibility.Collapsed
            : Visibility.Visible;
        ExtensionsToolbarButton.Visibility = _narrowNavigation
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateMediaHubAvailability();
    }

    private int ConfiguredTabBarHeight => Math.Clamp(_settings.TabBarHeight, 36, 60);

    private double TabMinimumWidth => _settings.TabSize switch
    {
        TabSizeMode.Compact => 86,
        TabSizeMode.Large => 132,
        _ => 112
    };

    private double TabMaximumWidth => _settings.TabSize switch
    {
        TabSizeMode.Compact => 168,
        TabSizeMode.Large => 252,
        _ => 214
    };

    private const string NewTabPageTemplate = """
<!doctype html>
<html lang="__HOME_LANG__" class="__HOME_MOTION__">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>New tab</title>
    <style>
        /*<aero-font-face>*/
        :root {
            color-scheme: __COLOR_SCHEME__;
            font-family: Manrope, "Segoe UI Variable", "Segoe UI", Arial, sans-serif;
            background: __BACKGROUND__;
            color: __TEXT__;
        }

        * { box-sizing: border-box; }
        .sr-only { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }

        html, body {
            width: 100%;
            min-height: 100%;
            margin: 0;
            overflow: auto;
        }

        body {
            position: relative;
            display: grid;
            min-height: 100vh;
            min-height: 100dvh;
            place-items: center;
            isolation: isolate;
            background: __BACKGROUND__;
        }

        body::before,
        body::after {
            content: "";
            position: fixed;
            inset: 0;
            pointer-events: none;
        }

        body::before {
            z-index: -2;
            background-color: __HOME_BACKGROUND_COLOR__;
            background-image: __HOME_BACKGROUND_IMAGE__;
            background-size: __HOME_BACKGROUND_SIZE__;
            background-position: __HOME_BACKGROUND_POSITION__;
            background-repeat: __HOME_BACKGROUND_REPEAT__;
            opacity: __HOME_BACKGROUND_OPACITY__;
            filter: blur(__HOME_BACKGROUND_BLUR__px);
            transform: scale(__HOME_BACKGROUND_SCALE__);
            transform-origin: center;
        }

        body::after {
            z-index: -1;
            background: __HOME_BACKGROUND_OVERLAY__;
        }

        main {
            position: relative;
            z-index: 1;
            align-self: start;
            width: min(640px, calc(100vw - 48px));
            margin: 0 auto;
            padding: max(72px, 24vh) 0 64px;
        }

        .subtitle {
            margin: 0 0 12px;
            color: __MUTED__;
            font-size: 13px;
        }

        .subtitle:empty { display: none; }

        form {
            display: flex;
            align-items: center;
            gap: 10px;
            height: 44px;
            padding: 0 4px 0 14px;
            background: __SURFACE__;
            border: 1px solid __BORDER__;
            border-radius: 8px;
            transition: border-color 100ms ease;
        }

        form:hover { border-color: __FOCUS_BORDER__; }
        form:focus-within { border-color: __FOCUS_BORDER__; box-shadow: inset 0 -2px 0 __FOCUS_BORDER__; }

        .search-icon {
            position: relative;
            width: 12px;
            height: 12px;
            border: 1.5px solid currentColor;
            border-radius: 50%;
            flex: 0 0 auto;
        }

        .search-icon::after {
            content: "";
            position: absolute;
            width: 5px;
            height: 1.5px;
            right: -4px;
            bottom: -2px;
            background: currentColor;
            transform: rotate(45deg);
        }

        #aero-search-query {
            min-width: 0;
            flex: 1;
            height: 100%;
            padding: 0;
            border: 0;
            outline: 0;
            background: transparent;
            color: __TEXT__;
            font: inherit;
            font-size: 15px;
        }

        #aero-search-query::placeholder { color: __PLACEHOLDER__; }

        .search-button {
            order: -1;
            display: grid;
            place-items: center;
            width: 20px;
            height: 20px;
            padding: 0;
            border: 0;
            background: transparent;
            color: __MUTED__;
            cursor: pointer;
        }

        .search-button:hover { color: __TEXT__; }
        .search-button > span:last-child { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }

        #quick-links {
            display: __SHORTCUT_DISPLAY__;
            flex-wrap: wrap;
            align-items: flex-start;
            gap: 4px;
            margin: 20px -8px 0;
        }

        .shortcut {
            position: relative;
            width: 84px;
            min-width: 84px;
            height: 84px;
            padding: 10px 6px 8px;
            border-radius: 6px;
            color: __LINK_TEXT__;
            text-align: center;
            text-decoration: none;
            cursor: pointer;
            user-select: none;
            -webkit-user-drag: element;
        }

        .shortcut:hover { background: __SHORTCUT_HOVER__; }
        .shortcut.dragging { opacity: .45; }
        .shortcut.drop-target { box-shadow: inset 0 0 0 1px __FOCUS_BORDER__; }

        .shortcut-icon {
            display: grid;
            place-items: center;
            width: 36px;
            height: 36px;
            flex: 0 0 auto;
            overflow: hidden;
            border-radius: 6px;
            background: __ICON_BG__;
            font-size: 14px;
            font-weight: 600;
        }

        .shortcut-icon img {
            width: 20px;
            height: 20px;
            object-fit: contain;
        }

        .shortcut-label {
            overflow: hidden;
            white-space: nowrap;
            text-overflow: ellipsis;
            color: __MUTED__;
            font-size: 12px;
        }

        .shortcut-menu {
            position: absolute;
            top: 2px;
            right: 2px;
            width: 22px;
            height: 22px;
            padding: 0;
            border: 0;
            border-radius: 4px;
            background: transparent;
            color: __MUTED__;
            font: inherit;
            font-size: 16px;
            line-height: 1;
            cursor: pointer;
            opacity: 0;
        }

        .shortcut-menu:hover { background: __ICON_BG__; color: __TEXT__; }

        .shortcut:hover .shortcut-menu,
        .shortcut:focus-within .shortcut-menu,
        .shortcut-menu:focus { opacity: 1; }

        .add-shortcut {
            height: 32px;
            margin: 26px 8px;
            padding: 0 10px;
            border: 0;
            border-radius: 6px;
            background: transparent;
            color: __MUTED__;
            font: inherit;
            font-size: 13px;
            cursor: pointer;
        }

        .add-shortcut:hover {
            background: __SHORTCUT_HOVER__;
            color: __TEXT__;
        }

        dialog {
            width: min(400px, calc(100vw - 32px));
            padding: 0;
            border: 1px solid __BORDER__;
            border-radius: 8px;
            background: __SURFACE__;
            color: __TEXT__;
            box-shadow: 0 16px 48px __FOCUS_SHADOW__;
        }

        dialog::backdrop { background: rgba(0, 0, 0, .52); }

        .dialog-body { padding: 20px; text-align: left; }
        .dialog-body h2 { margin: 0 0 12px; font-size: 16px; font-weight: 600; }
        .field { display: block; margin-top: 12px; color: __MUTED__; font-size: 12px; }
        .field input {
            width: 100%;
            height: 34px;
            margin-top: 6px;
            padding: 0 12px;
            border: 1px solid __BORDER__;
            border-radius: 4px;
            outline: 0;
            background: __BACKGROUND__;
            color: __TEXT__;
            font: inherit;
            font-size: 14px;
        }
        .field input:focus { border-color: __FOCUS_BORDER__; }
        .dialog-actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 18px; }
        .dialog-actions button {
            min-height: 32px;
            padding: 0 14px;
            border: 1px solid __BORDER__;
            border-radius: 4px;
            background: transparent;
            color: __TEXT__;
            font: inherit;
            cursor: pointer;
        }
        .dialog-actions .primary { border-color: transparent; background: __BUTTON_BG__; color: __BUTTON_TEXT__; }
        .dialog-actions .danger { margin-right: auto; color: #d85b5b; }

        .home-toast {
            position: fixed;
            left: 50%;
            bottom: 24px;
            z-index: 20;
            max-width: min(520px, calc(100vw - 32px));
            padding: 10px 14px;
            border: 1px solid __BORDER__;
            border-radius: 6px;
            background: __SURFACE__;
            color: __TEXT__;
            font-size: 13px;
            opacity: 0;
            pointer-events: none;
            transform: translate(-50%, 12px);
            transition: opacity 140ms ease, transform 140ms ease;
        }
        .home-toast.show { opacity: 1; transform: translate(-50%, 0); }
        .home-toast.error { color: #d85b5b; }

        .shortcut-link { display:flex; flex-direction:column; align-items:center; justify-content:center; gap:8px; width:100%; height:100%; color:inherit; text-decoration:none; border-radius:10px; }
        .shortcut-label { display:block; max-width:100%; }
        button:focus-visible,a:focus-visible,input:focus-visible { outline:2px solid __FOCUS_BORDER__; outline-offset:4px; }
        .shortcut:focus-within { background:__SHORTCUT_HOVER__; }
        html.reduce-motion *,html.reduce-motion *::before,html.reduce-motion *::after { transition:none!important;animation:none!important;scroll-behavior:auto!important; }
        @media(prefers-reduced-motion:reduce) { *,*::before,*::after { transition:none!important;animation:none!important;scroll-behavior:auto!important; } }
        @media(max-height:520px) { main { padding-top:32px; } }
        @media(max-width:420px) { main { width:calc(100vw - 32px); } }
        @media(forced-colors:active) { form,.shortcut,dialog { border-color:CanvasText; } button:focus-visible,a:focus-visible,input:focus-visible { outline-color:Highlight; } }
    </style>
</head>
<body>
    <main>
        <h1 class="sr-only">Aero</h1>
        <p class="subtitle">__SUBTITLE__</p>

        <form id="search-form">
            <input id="aero-search-query" aria-label="Search the web or enter an address" autocomplete="off" autofocus placeholder="Search the web or enter a question">
            <button class="search-button" type="submit"><span class="search-icon" aria-hidden="true"></span><span>Search</span></button>
        </form>

        <nav id="quick-links" aria-label="Quick links"></nav>
    </main>

    <dialog id="link-dialog" aria-labelledby="dialog-title">
        <div class="dialog-body">
            <h2 id="dialog-title">Add quick link</h2>
            <label class="field">Name<input id="link-title" maxlength="80" placeholder="How it appears"></label>
            <label class="field">URL<input id="link-url" inputmode="url" placeholder="https://example.com"></label>
            <label class="field">Icon<input id="link-icon" maxlength="500" placeholder="Emoji, image URL, or leave empty for site icon"></label>
            <div class="dialog-actions">
                <button class="danger" id="link-delete" type="button">Delete</button>
                <button id="link-cancel" type="button">Cancel</button>
                <button class="primary" id="link-save" type="button">Save</button>
            </div>
        </div>
    </dialog>
    <div id="home-toast" class="home-toast" role="status" aria-live="polite"></div>

    <script>
        const labels = __HOME_LABELS_JSON__;
        const tr = value => labels[value] || value;
        const quickLinks = __QUICK_LINKS_JSON__;
        const post = message => window.chrome?.webview?.postMessage(message);
        const linksHost = document.getElementById('quick-links');
        const dialog = document.getElementById('link-dialog');
        const titleInput = document.getElementById('link-title');
        const urlInput = document.getElementById('link-url');
        const iconInput = document.getElementById('link-icon');
        const deleteButton = document.getElementById('link-delete');
        let editingId = '';
        let draggedId = '';
        let suppressShortcutClickUntil = 0;
        let toastTimer = 0;

        window.aeroHomeStatus = (message, isError = false) => {
            const toast = document.getElementById('home-toast');
            toast.textContent = message;
            toast.className = 'home-toast show' + (isError ? ' error' : '');
            clearTimeout(toastTimer);
            toastTimer = setTimeout(() => toast.className = 'home-toast', 3200);
        };

        document.getElementById('search-form').addEventListener('submit', event => {
            event.preventDefault();
            const query = document.getElementById('aero-search-query').value.trim();
            if (query) post({ type: 'aero-home-search', query });
        });

        function fallbackIcon(url) {
            try { return new URL('/favicon.ico', url).href; }
            catch { return ''; }
        }

        function serviceIcon(url) {
            try {
                const host = new URL(url).hostname;
                return host ? `https://icons.duckduckgo.com/ip3/${encodeURIComponent(host)}.ico` : '';
            } catch { return ''; }
        }

        function iconMarkup(link) {
            const icon = String(link.icon || '').trim();
            const service = serviceIcon(link.url);
            if (icon && /^(https?:|data:image\/)/i.test(icon)) {
                return `<img src="${escapeHtml(icon)}" data-fallback="${escapeHtml(service)}" alt="">`;
            }
            if (icon) return escapeHtml(icon);
            const favicon = fallbackIcon(link.url);
            return favicon ? `<img src="${escapeHtml(favicon)}" data-fallback="${escapeHtml(service)}" alt="">` : '↗';
        }

        function wireIconFallback(item) {
            const image = item.querySelector('.shortcut-icon img');
            if (!image) return;
            image.onerror = () => {
                const fallback = image.dataset.fallback || '';
                if (fallback && image.src !== fallback) {
                    image.dataset.fallback = '';
                    image.src = fallback;
                    return;
                }
                const host = image.closest('.shortcut-icon');
                if (host) host.textContent = '↗';
            };
        }

        function escapeHtml(value) {
            return String(value ?? '').replace(/[&<>"']/g, character => ({
                '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
            })[character]);
        }

        function render() {
            linksHost.innerHTML = '';
            for (const link of quickLinks) {
                const item = document.createElement('div');
                item.className = 'shortcut';
                item.draggable = true;
                item.dataset.id = link.id;
                item.title = link.url;
                item.innerHTML = `<a class="shortcut-link" href="${escapeHtml(link.url)}" aria-label="${escapeHtml(link.title)}"><span class="shortcut-icon">${iconMarkup(link)}</span><span class="shortcut-label">${escapeHtml(link.title)}</span></a><button class="shortcut-menu" type="button" aria-label="${escapeHtml(tr('Edit'))} ${escapeHtml(link.title)}">⋯</button>`;
                wireIconFallback(item);
                item.addEventListener('click', event => {
                    if (Date.now() < suppressShortcutClickUntil) {
                        event.preventDefault();
                        event.stopPropagation();
                    }
                });

                item.querySelector('.shortcut-menu').addEventListener('click', event => {
                    event.preventDefault();
                    event.stopPropagation();
                    openEditor(link);
                });
                item.addEventListener('keydown', event => {
                    if (!event.altKey || !['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
                    event.preventDefault();
                    const index = quickLinks.findIndex(x => x.id === link.id);
                    const target = quickLinks[index + (event.key === 'ArrowLeft' ? -1 : 1)];
                    if (!target) return;
                    moveRelative(link.id, target.id, event.key === 'ArrowRight');
                    [...linksHost.querySelectorAll('.shortcut')].find(x => x.dataset.id === link.id)?.querySelector('a').focus();
                });
                item.addEventListener('dragstart', event => {
                    draggedId = link.id;
                    item.classList.add('dragging');
                    event.dataTransfer.effectAllowed = 'move';
                    event.dataTransfer.setData('text/plain', link.id);
                });
                item.addEventListener('dragend', () => {
                    draggedId = '';
                    item.classList.remove('dragging');
                    document.querySelectorAll('.drop-target').forEach(node => node.classList.remove('drop-target'));
                });
                item.addEventListener('dragover', event => {
                    if (!draggedId || draggedId === link.id) return;
                    event.preventDefault();
                    item.classList.add('drop-target');
                });
                item.addEventListener('dragleave', () => item.classList.remove('drop-target'));
                item.addEventListener('drop', event => {
                    event.preventDefault();
                    item.classList.remove('drop-target');
                    const rect = item.getBoundingClientRect();
                    const after = event.clientX >= rect.left + rect.width / 2;
                    moveRelative(draggedId, link.id, after);
                });
                linksHost.appendChild(item);
            }

            const add = document.createElement('button');
            add.className = 'add-shortcut';
            add.type = 'button';
            add.setAttribute('aria-label', tr('Add shortcut'));
            add.title = tr('Add shortcut');
            add.textContent = '+  ' + tr('Add shortcut');
            add.addEventListener('click', () => openEditor(null));
            add.addEventListener('dragover', event => { if (draggedId) event.preventDefault(); });
            add.addEventListener('drop', event => {
                event.preventDefault();
                moveRelative(draggedId, '', true);
            });
            linksHost.appendChild(add);
        }

        function moveRelative(id, targetId, after) {
            if (!id || id === targetId) return;
            const from = quickLinks.findIndex(link => link.id === id);
            if (from < 0) return;
            const [link] = quickLinks.splice(from, 1);
            let to = targetId ? quickLinks.findIndex(item => item.id === targetId) : quickLinks.length;
            if (to < 0) to = quickLinks.length;
            if (targetId && after) to += 1;
            quickLinks.splice(Math.min(to, quickLinks.length), 0, link);
            suppressShortcutClickUntil = Date.now() + 250;
            render();
            post({ type: 'aero-home-quick-link', action: 'reorder', order: quickLinks.map(item => item.id) });
        }

        function openEditor(link) {
            editingId = link?.id || '';
            document.getElementById('dialog-title').textContent = tr(link ? 'Edit quick link' : 'Add quick link');
            titleInput.value = link?.title || '';
            urlInput.value = link?.url || '';
            iconInput.value = link?.icon || '';
            deleteButton.hidden = !link;
            dialog.showModal();
            setTimeout(() => (link ? titleInput : urlInput).focus(), 0);
        }

        document.getElementById('link-cancel').addEventListener('click', () => dialog.close());
        document.getElementById('link-save').addEventListener('click', () => {
            if (!urlInput.value.trim()) {
                urlInput.focus();
                return;
            }
            post({
                type: 'aero-home-quick-link',
                action: 'upsert',
                id: editingId,
                title: titleInput.value,
                url: urlInput.value,
                icon: iconInput.value
            });
            dialog.close();
        });
        deleteButton.addEventListener('click', () => {
            if (!editingId) return;
            post({ type: 'aero-home-quick-link', action: 'remove', id: editingId });
            dialog.close();
        });
        dialog.addEventListener('click', event => {
            const bounds = dialog.getBoundingClientRect();
            if (event.target === dialog && (event.clientX < bounds.left || event.clientX > bounds.right ||
                event.clientY < bounds.top || event.clientY > bounds.bottom)) dialog.close();
        });

        const subtitle = document.querySelector('.subtitle'); if (subtitle.textContent) subtitle.textContent = tr(subtitle.textContent);
        document.querySelector('.search-button > span:last-child').textContent = tr('Search');
        document.querySelector('.search-button').setAttribute('aria-label', tr('Search'));
        document.getElementById('dialog-title').textContent = tr('Add quick link');
        document.querySelectorAll('.field').forEach(label => { label.firstChild.textContent = tr(label.firstChild.textContent); });
        for (const [id, text] of [['link-delete','Delete'],['link-cancel','Cancel'],['link-save','Save']]) document.getElementById(id).textContent = tr(text);
        document.querySelectorAll('[placeholder],[aria-label]').forEach(el => {
            for (const attribute of ['placeholder','aria-label']) if (el.hasAttribute(attribute)) el.setAttribute(attribute, tr(el.getAttribute(attribute)));
        });
        render();
    </script>
</body>
</html>
""";

    private static readonly List<MainWindow> OpenWindows = [];

    private readonly List<BrowserTabState> _tabs = [];
    private readonly Stack<ClosedTabInfo> _closedTabs = [];
    private readonly BrowserProfile _browserProfile;
    private readonly BrowserDataStore _dataStore;
    private readonly SitePermissionStore _sitePermissionStore;
    private readonly RequestBlocker _requestBlocker = new();
    private readonly CancellationTokenSource _shieldCancellation = new();
    private long _shieldBlockedSessionTotal;
    private readonly DispatcherTimer _sleepTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _findTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DispatcherTimer _sessionSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(260) };
    private BrowserSettings _settings;

    private CoreWebView2Environment? _environment;
    private BrowserTabState? _activeTab;
    private LibraryView _libraryView = LibraryView.History;
    private bool _isInitialized;
    private bool _settingsPaneOpen;
    private bool _libraryPaneOpen;
    private bool _isRestoringSession;
    private bool _updatingShieldUi;
    private bool _findBarOpen;
    private bool _isFullscreen;
    private bool _tabWidthExpansionLocked;
    private int _tabWidthLockGeneration;

    private static readonly object SharedEnvironmentGate = new();
    private static readonly Dictionary<string, Task<CoreWebView2Environment>> SharedEnvironmentTasks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string>
        SessionDiagnosticQueue = new();
    private static readonly string SessionDiagnosticLogPath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "Aero",
        "Logs",
        "session-integrity.log");
    private static int _sessionDiagnosticDrainScheduled;
    private static readonly bool SessionDiagnosticsEnabled =
        string.Equals(
            System.Environment.GetEnvironmentVariable("AERO_SESSION_DIAGNOSTICS"),
            "1",
            StringComparison.Ordinal) ||
        string.Equals(
            System.Environment.GetEnvironmentVariable("AERO_SESSION_DIAGNOSTICS"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    public MainWindow(bool isPrivate = false)
        : this(
            isPrivate,
            BrowserProfileManager.LoadActiveProfile(),
            startupTransfer: null,
            sharedPrivateUserDataFolder: null)
    {
    }

    private MainWindow(
        bool isPrivate,
        BrowserProfile browserProfile,
        BrowserTabTransfer? startupTransfer,
        string? sharedPrivateUserDataFolder)
    {
        _isPrivateWindow = isPrivate;
        _browserProfile = browserProfile;
        _startupTransfer = startupTransfer;
        _privateUserDataFolder = isPrivate
            ? sharedPrivateUserDataFolder ?? CreatePrivateUserDataFolderPath()
            : null;
        if (!isPrivate && !browserProfile.IsGuest)
        {
            try
            {
                ProfileBackupService.ApplyPendingRestore(browserProfile.StorageRoot);
                _ = ProfileBackupService.TryRecoverCorruptedProfile(browserProfile);
            }
            catch
            {
                // A damaged pending restore must not prevent Aero from opening.
            }
        }
        _settings = BrowserSettings.Load(
            browserProfile.StorageRoot,
            persistToDisk: !browserProfile.IsGuest);
        if (browserProfile.IsGuest)
        {
            _settings.SavePasswords = false;
            _settings.AutofillForms = false;
            _settings.RestorePreviousSession = false;
            _settings.StartupBehavior = StartupBehavior.NewTab;
        }

        Localization.AeroLocalizer.Configure(_settings.AppLanguage);

        _dataStore = isPrivate || browserProfile.IsGuest
            ? BrowserDataStore.CreateEphemeral()
            : BrowserDataStore.Load(browserProfile.StorageRoot);
        _sitePermissionStore = SitePermissionStore.Load(
            persistToDisk: !isPrivate && !browserProfile.IsGuest,
            storageRoot: browserProfile.StorageRoot);
        _extensionRegistry = ExtensionRegistry.Load(browserProfile.StorageRoot);
        string securityDirectory = Path.Combine(browserProfile.StorageRoot, "Security");
        _passwordBrokerClient = new PasswordBrokerClient(securityDirectory);
        _passwordVaultService = new PasswordVaultService(
            securityDirectory,
            _passwordBrokerClient);

        InitializeComponent();
        RootGrid.SizeChanged += RootGrid_ChromeSizeChanged;
        InitializeCoreSitePermissionsUi();
        ApplyWindowIcon();
        ConfigurePrivateWindowChrome();

        ExtendsContentIntoTitleBar = true;
        ApplyTheme();
        ApplyLocalization();

        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
        _sleepTimer.Tick += SleepTimer_Tick;
        _sleepTimer.Start();
        _findTimer.Tick += FindTimer_Tick;
        _sessionSaveTimer.Tick += SessionSaveTimer_Tick;
        InitializeUserReadyFeatures();
        InitializePageToolsFeatures();
        InitializeMemorySaverFeatures();
        InitializeVaultExtensionsFeatures();
        InitializeSecureVaultFeatures();
        InitializeUBlockOriginIntegration();
        _requestBlocker.FiltersChanged += RequestBlocker_FiltersChanged;
        OpenWindows.Add(this);
        Closed += MainWindow_Closed;
    }

    private BrowserTabState? ActiveTab => _activeTab;

    private void ApplyWindowIcon()
    {
        try
        {
            string iconPath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "AeroIcon.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch
        {
            // The embedded executable icon remains available if the runtime file is missing.
        }
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        SetTitleBar(TitleBarDragRegion);
        UpdateTitleBarLayout();
        UpdateTitleBarColors();
        StartFrameRateCalibration();
        AttachAddTabContainerMenu();

        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;

        try
        {
            await ShowFirstRunExperienceIfNeededAsync();

            string userDataFolder = GetWebViewUserDataFolder();
            ApplyExtensionPreferenceOverridesBeforeWebViewStartup(userDataFolder);

            // WebView2 process/profile startup and Shield list loading are
            // independent. Running them together removes a fully sequential startup
            // delay without allowing the first page to bypass protection.
            Task<CoreWebView2Environment> environmentTask =
                GetOrCreateSharedEnvironmentAsync(userDataFolder);
            PrefetchCookieVault();
            Task shieldInitializationTask = _requestBlocker.InitializeAsync(
                _settings.ShieldCustomRules,
                _settings.ShieldAutoUpdate,
                _shieldCancellation.Token);

            await Task.WhenAll(environmentTask, shieldInitializationTask);
            _environment = await environmentTask;
            WriteSessionDiagnostic(
                $"environment-ready userDataFolder={userDataFolder} " +
                $"environmentId={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_environment)} " +
                $"logPath={SessionDiagnosticLogPath}");

            Uri? externalStartupUri = DequeuePendingExternalUri();

            if (_startupTransfer is not null)
            {
                BrowserTabState received = await CreateTransferredTabAsync(
                    _startupTransfer,
                    selectTab: true);
                _startupTransfer.Completion.TrySetResult(received);
            }
            else if (externalStartupUri is not null)
            {
                await CreateTabAsync(
                    externalStartupUri,
                    selectTab: true,
                    openedFromExternalApplication: true);
            }
            else
            {
                bool restored = _settings.StartupBehavior == StartupBehavior.RestoreSession &&
                    await RestorePreviousSessionAsync();
                if (!restored && _settings.StartupBehavior == StartupBehavior.HomePage)
                {
                    BrowserTabState homeTab = await CreateTabAsync(null, selectTab: true);
                    NavigateToConfiguredAddress(homeTab, _settings.HomePageUrl);
                }
                else if (!restored)
                {
                    await CreateTabAsync(null, selectTab: true, showStartPage: true);
                }
            }

            while (DequeuePendingExternalUri() is Uri pendingExternalUri)
            {
                await CreateTabAsync(
                    pendingExternalUri,
                    selectTab: true,
                    openedFromExternalApplication: true);
            }

            // Otherwise WinUI parks keyboard focus (and its focus ring) on the first title-bar button.
            ActiveTab?.Browser.Focus(FocusState.Programmatic);

            await RunPendingFirstRunActionsAsync();

            _ = RunAutomaticBackupIfDueAsync();
            _ = ReportAppHealthAsync();
        }
        catch (Exception exception)
        {
            _startupTransfer?.Completion.TrySetException(exception);
            await ShowErrorDialogAsync("Aero could not start WebView2", exception.Message);
        }
    }

    private async Task ReportAppHealthAsync()
    {
        if (AppHealth.TakeUnseenCrash())
        {
            ShowNoticeToast(Localization.AeroLocalizer.Get("Notice.CrashedLastTime"), AppHealth.CrashLogPath);
        }

        if (_settings.CheckForUpdates && await AppHealth.FindNewerReleaseAsync() is Version latest)
        {
            ShowNoticeToast(
                string.Format(Localization.AeroLocalizer.Get("Notice.UpdateAvailable"), latest),
                AppHealth.ReleasesPage);
        }
    }

    private static Task<CoreWebView2Environment> GetOrCreateSharedEnvironmentAsync(
        string userDataFolder)
    {
        string normalizedFolder = Path.GetFullPath(userDataFolder);
        lock (SharedEnvironmentGate)
        {
            if (!SharedEnvironmentTasks.TryGetValue(normalizedFolder, out Task<CoreWebView2Environment>? task))
            {
                task = WebViewEnvironmentFactory.CreateAsync(
                    normalizedFolder,
                    enableBrowserExtensions: true);
                SharedEnvironmentTasks[normalizedFolder] = task;
            }

            return task;
        }
    }

    private static void WriteSessionDiagnostic(string message)
    {
        if (!SessionDiagnosticsEnabled)
        {
            return;
        }

        string line = $"[Aero.Session] {DateTimeOffset.UtcNow:O} {message}";
        System.Diagnostics.Trace.WriteLine(line);
        SessionDiagnosticQueue.Enqueue(line);
        if (Interlocked.CompareExchange(ref _sessionDiagnosticDrainScheduled, 1, 0) == 0)
        {
            _ = Task.Run(DrainSessionDiagnosticQueueAsync);
        }
    }

    private static async Task DrainSessionDiagnosticQueueAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SessionDiagnosticLogPath)!);
            using StreamWriter writer = new(
                SessionDiagnosticLogPath,
                append: true,
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            while (SessionDiagnosticQueue.TryDequeue(out string? line))
            {
                await writer.WriteLineAsync(line);
            }
        }
        catch
        {
            // Diagnostics must never interfere with navigation or session state.
        }
        finally
        {
            Volatile.Write(ref _sessionDiagnosticDrainScheduled, 0);
            if (!SessionDiagnosticQueue.IsEmpty &&
                Interlocked.CompareExchange(ref _sessionDiagnosticDrainScheduled, 1, 0) == 0)
            {
                _ = Task.Run(DrainSessionDiagnosticQueueAsync);
            }
        }
    }

    private static string FormatDiagnosticUri(Uri? uri)
    {
        if (uri is null)
        {
            return "<none>";
        }

        if (string.IsNullOrEmpty(uri.Query))
        {
            return uri.AbsoluteUri;
        }

        // Reservation and authentication URLs often carry bearer-like values in
        // the query string. Keep the endpoint and parameter names while redacting
        // values so diagnostics cannot leak a live session.
        string[] parameterNames = uri.Query[1..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair =>
            {
                int separator = pair.IndexOf('=');
                return separator >= 0 ? pair[..separator] : pair;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string query = parameterNames.Length == 0
            ? string.Empty
            : "?" + string.Join("&", parameterNames.Select(static name => name + "=<redacted>"));
        return uri.GetLeftPart(UriPartial.Path) + query;
    }

#if DEBUG
    private const bool CookieInventoryDiagnostics = true;
#else
    // An env var any user process can set must not make release builds write a cookie inventory to disk.
    private const bool CookieInventoryDiagnostics = false;
#endif

    private static async Task TraceCookieMetadataAsync(
        BrowserTabState state,
        Uri uri,
        string stage)
    {
        if (!CookieInventoryDiagnostics ||
            !SessionDiagnosticsEnabled ||
            state.Browser.CoreWebView2 is not CoreWebView2 core ||
            !IsBrowsableUri(uri))
        {
            return;
        }

        try
        {
            IReadOnlyList<CoreWebView2Cookie> cookies =
                await core.CookieManager.GetCookiesAsync(uri.AbsoluteUri);
            WriteSessionDiagnostic(
                $"cookies stage={stage} uri={FormatDiagnosticUri(uri)} count={cookies.Count}");

            foreach (CoreWebView2Cookie cookie in cookies
                .OrderBy(static cookie => cookie.Domain, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static cookie => cookie.Name, StringComparer.OrdinalIgnoreCase))
            {
                string expires = cookie.IsSession
                    ? "session"
                    : Convert.ToString(cookie.Expires, CultureInfo.InvariantCulture) ?? "unknown";
                WriteSessionDiagnostic(
                    $"cookie stage={stage} name={cookie.Name} domain={cookie.Domain} " +
                    $"path={cookie.Path} secure={cookie.IsSecure} httpOnly={cookie.IsHttpOnly} " +
                    $"sameSite={cookie.SameSite} expires={expires}");
            }
        }
        catch (Exception exception)
        {
            WriteSessionDiagnostic(
                $"cookies stage={stage} uri={FormatDiagnosticUri(uri)} " +
                $"error={exception.GetType().Name}");
        }
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateTitleBarColors();
        UpdateAddressSurface();
        UpdateAllTabVisuals();
        RefreshStartPages();

        if (_libraryPaneOpen)
        {
            RefreshLibraryPane();
        }
    }

    private async Task<BrowserTabState> CreateTabAsync(
        Uri? initialUri,
        bool selectTab,
        bool showStartPage = false,
        bool deferWebViewInitialization = false,
        string? tabGroupId = null,
        bool deferWebViewConfiguration = false,
        bool openedFromExternalApplication = false,
        string? containerId = null,
        BrowserTabState? sameProfileAs = null)
    {
        if (_environment is null)
        {
            throw new InvalidOperationException("WebView2 environment is not ready.");
        }

        WebView2 browser = CreateBrowserControl();
        Image snapshotView = CreateSnapshotView();

        BrowserTabState state = new(browser)
        {
            SnapshotView = snapshotView,
            // Popups and duplicates must share the source tab's profile (and burner session).
            ContainerId = sameProfileAs is not null ? sameProfileAs.ContainerId : FindTabContainer(containerId)?.Id,
            BurnerProfileName = sameProfileAs?.BurnerProfileName,
            TabGroupId = FindTabGroup(tabGroupId) is null ? null : tabGroupId,
            ExternalAuthenticationStartedAt = openedFromExternalApplication
                ? DateTimeOffset.UtcNow
                : null
        };
        AttachBrowserPaneInput(state, browser);
        BuildTabHeader(state);

        _tabs.Add(state);
        PlaceNewTabInsideAssignedGroup(state);
        RefreshTabStripOrder();
        Canvas.SetZIndex(browser, 0);
        BrowserHost.Children.Add(browser);
        BrowserHost.Children.Add(snapshotView);
        UpdateTabWidths();

        if (selectTab)
        {
            SelectTab(state);
        }

        if (deferWebViewInitialization)
        {
            state.IsDiscarded = true;
            state.PendingStartPage = showStartPage;
            state.PendingUri = showStartPage ? null : initialUri;
            state.IsStartPage = showStartPage;
            ShowSleepingTabVisual(state, "Not loaded yet — click to open");
        }
        else
        {
            TraceWebViewInitialization(state, "started");
            await EnsureWebViewInitializedAsync(state);
            TraceWebViewInitialization(state, "completed");
            if (!deferWebViewConfiguration)
            {
                await ConfigureWebViewAsync(state);
            }

            if (showStartPage)
            {
                ShowStartPage(state);
            }
            else if (initialUri is not null)
            {
                Navigate(state, initialUri);
            }
        }

        UpdateVisibleBrowser();
        UpdateAllTabVisuals();
        AnimateTabEntrance(state);
        SaveSessionSnapshot();
        return state;
    }

    private void BuildTabHeader(BrowserTabState state)
    {
        Grid content = new()
        {
            Height = Math.Max(29, ConfiguredTabBarHeight - 7),
            ColumnSpacing = 7,
            Margin = new Thickness(0, 1, 0, 0)
        };
        ColumnDefinition iconColumn = new() { Width = new GridLength(16) };
        ColumnDefinition titleColumn = new() { Width = new GridLength(1, GridUnitType.Star) };
        ColumnDefinition closeColumn = new() { Width = new GridLength(22) };
        content.ColumnDefinitions.Add(iconColumn);
        content.ColumnDefinitions.Add(titleColumn);
        content.ColumnDefinitions.Add(closeColumn);

        RotateTransform iconTransform = new();
        FontIcon icon = new()
        {
            Glyph = "\uE774",
            FontSize = Math.Clamp(_settings.TabIconSize - 5, 8, 19),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = GetMutedTextBrush(),
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = iconTransform
        };

        Image faviconImage = new()
        {
            Width = Math.Clamp(_settings.TabIconSize, 10, 24),
            Height = Math.Clamp(_settings.TabIconSize, 10, 24),
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed
        };

        FontIcon audioIcon = new()
        {
            Glyph = "\uE767",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Button audioButton = new()
        {
            Style = (Style)Application.Current.Resources["ChromeIconButtonStyle"],
            Width = 20,
            Height = 20,
            MinWidth = 20,
            MinHeight = 20,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            Content = audioIcon,
            Tag = state,
            Visibility = Visibility.Collapsed
        };
        audioButton.Click += TabAudioButton_Click;
        ToolTipService.SetToolTip(audioButton, "Mute tab");

        Grid iconHost = new();
        iconHost.Children.Add(icon);
        iconHost.Children.Add(faviconImage);
        iconHost.Children.Add(audioButton);

        TextBlock title = new()
        {
            Text = DisplayTabTitle("New tab"),
            FontSize = Math.Clamp(_settings.TabFontSize, 9, 18),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = GetTextBrush()
        };
        Grid.SetColumn(title, 1);

        Button closeButton = new()
        {
            Style = (Style)Application.Current.Resources["TabCloseButtonStyle"],
            Content = new FontIcon { Glyph = "\uE711", FontSize = 9 },
            Visibility = Visibility.Collapsed,
            Tag = state
        };
        closeButton.Click += TabCloseButton_Click;
        Grid.SetColumn(closeButton, 2);

        content.Children.Add(iconHost);
        content.Children.Add(title);
        content.Children.Add(closeButton);

        ScaleTransform indicatorTransform = new() { ScaleX = 0.55, ScaleY = 1 };
        Border activeIndicator = new()
        {
            Height = 2,
            Margin = new Thickness(13, 0, 13, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(1),
            Opacity = 0,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = indicatorTransform
        };

        Border separator = new()
        {
            Width = 1,
            Height = 17,
            Margin = new Thickness(0, 0, -2, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Background = GetSoftBorderBrush(),
            Opacity = 0.72
        };

        // Container colour along the bottom edge, like a label on the tab.
        Border containerStripe = new()
        {
            Height = 2,
            Margin = new Thickness(6, 0, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(1),
            Visibility = Visibility.Collapsed
        };

        Grid visualRoot = new();
        visualRoot.Children.Add(content);
        visualRoot.Children.Add(activeIndicator);
        visualRoot.Children.Add(separator);
        visualRoot.Children.Add(containerStripe);

        CompositeTransform headerTransform = new()
        {
            ScaleX = 0.97,
            ScaleY = 0.97,
            TranslateY = 4
        };

        Border header = new()
        {
            Width = TabMaximumWidth,
            Height = Math.Max(33, ConfiguredTabBarHeight - 3),
            Margin = new Thickness(1, 2, 1, 0),
            Padding = new Thickness(9, 0, 5, 0),
            CornerRadius = new CornerRadius(11, 11, 3, 3),
            Background = GetTransparentBrush(),
            BorderBrush = GetTransparentBrush(),
            BorderThickness = new Thickness(0),
            Child = visualRoot,
            Tag = state,
            Opacity = 0,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = headerTransform
        };

        header.PointerPressed += TabHeader_PointerPressed;
        header.PointerReleased += TabHeader_PointerReleased;
        header.PointerEntered += TabHeader_PointerEntered;
        header.PointerExited += TabHeader_PointerExited;
        header.ContextFlyout = BuildTabContextFlyout(state);

        state.Header = header;
        state.HeaderTransform = headerTransform;
        state.ContentGrid = content;
        state.IconColumn = iconColumn;
        state.CloseColumn = closeColumn;
        state.IconHost = iconHost;
        state.Icon = icon;
        state.FaviconImage = faviconImage;
        state.AudioButton = audioButton;
        state.AudioIcon = audioIcon;
        state.IconTransform = iconTransform;
        faviconImage.ImageFailed += (_, _) => TryUseFallbackTabFavicon(state);
        state.TitleText = title;
        state.CloseButton = closeButton;
        state.ActiveIndicator = activeIndicator;
        state.ActiveIndicatorTransform = indicatorTransform;
        state.Separator = separator;
        state.ContainerStripe = containerStripe;
        ApplyContainerVisual(state);
        ToolTipService.SetToolTip(header, DisplayTabTitle("New tab"));
        UpdateTabAccessibility(state);
    }

    private async Task ConfigureWebViewAsync(
        BrowserTabState state,
        bool ensureCurrentDocumentBridges = false)
    {
        WebView2 browser = state.Browser;
        CoreWebView2 core = browser.CoreWebView2;
        // Sign-ins must be back in the profile before this tab's first request.
        await EnsureCookieVaultRestoredAsync(state);
        ConfigureExternalFileDrop(browser);

        // A discarded tab can receive a fresh WebView2 control. Always clear any
        // stale detach actions before wiring the new controller.
        DetachWebViewEvents(state);
        if (state.WebResourceFilterAttached)
        {
            try
            {
                core.RemoveWebResourceRequestedFilter(
                    "*",
                    CoreWebView2WebResourceContext.All);
            }
            catch
            {
                // A previous controller may already have removed the filter.
            }
            finally
            {
                state.WebResourceFilterAttached = false;
            }
        }

        CoreWebView2Settings settings = core.Settings;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultContextMenusEnabled = true;
        // DevTools stay disabled unless the user opens them through Aero's explicit
        // command. That command marks the document and blocks Vault operations.
        settings.AreDevToolsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = true;
        settings.IsZoomControlEnabled = false;
        settings.IsWebMessageEnabled = true;
        ApplyPasswordAndAutofillSettings(core);

        try
        {
            string customizationFolder = Path.Combine(
                _browserProfile.StorageRoot,
                "Customization");
            Directory.CreateDirectory(customizationFolder);
            core.SetVirtualHostNameToFolderMapping(
                HomeBackgroundVirtualHost,
                customizationFolder,
                CoreWebView2HostResourceAccessKind.DenyCors);
        }
        catch
        {
            // The start page falls back to its built-in background if mapping fails.
        }

        core.AddWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.All);
        state.WebResourceFilterAttached = true;

        void WebResourceRequestedHandler(object? sender, CoreWebView2WebResourceRequestedEventArgs args) =>
            Browser_WebResourceRequested(state, args);
        void NavigationStartingHandler(WebView2 sender, CoreWebView2NavigationStartingEventArgs args) =>
            Browser_NavigationStarting(state, args);
        void NavigationCompletedHandler(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            Browser_NavigationCompleted(state, args);
            if (ReferenceEquals(ActiveTab, state))
            {
                QueueRefreshCoreSitePermissionsUi();
            }
        }
        void ContentLoadingHandler(object? sender, CoreWebView2ContentLoadingEventArgs args) =>
            TraceContentLoading(state, args);
        async void DomContentLoadedHandler(object? sender, CoreWebView2DOMContentLoadedEventArgs args)
        {
            state.Navigation.MarkDomContentLoaded(args.NavigationId);
            TraceDomContentLoaded(state, args.NavigationId, "received");

            try
            {
                Task cosmeticTask = ApplyCosmeticFilteringAsync(state);
                Task zoomTask = Math.Abs(state.PageZoomFactor - 1.0) > 0.001
                    ? ApplyPageZoomAsync(state)
                    : Task.CompletedTask;
                Task captureBufferTask = PreparePasswordCaptureSharedBufferAsync(state);
                await Task.WhenAll(cosmeticTask, zoomTask, captureBufferTask);
            }
            catch
            {
                // Navigation can finish while a tab is being closed.
            }
            finally
            {
                TraceDomContentLoaded(state, args.NavigationId, "work-completed");
            }
        }
        void SourceChangedHandler(object? sender, CoreWebView2SourceChangedEventArgs args)
        {
            WriteSessionDiagnostic(
                $"source-changed newDocument={args.IsNewDocument} " +
                $"uri={FormatDiagnosticUri(Uri.TryCreate(core.Source, UriKind.Absolute, out Uri? sourceUri) ? sourceUri : null)}");
            Browser_SourceChanged(state);
            if (ReferenceEquals(ActiveTab, state))
            {
                QueueRefreshCoreSitePermissionsUi();
            }
        }
        void HistoryChangedHandler(object? sender, object args) =>
            Browser_HistoryChanged(state);
        void DocumentTitleChangedHandler(object? sender, object args) =>
            Browser_DocumentTitleChanged(state);
        void FaviconChangedHandler(object? sender, object args) =>
            Browser_FaviconChanged(state);
        void AudioStateChangedHandler(object? sender, object args) =>
            Browser_AudioStateChanged(state);
        void WebMessageReceivedHandler(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            WriteSessionDiagnostic(
                $"web-message source={FormatDiagnosticUri(Uri.TryCreate(args.Source, UriKind.Absolute, out Uri? messageSource) ? messageSource : null)}");
            Browser_WebMessageReceived(state, args);
        }
        void NewWindowRequestedHandler(object? sender, CoreWebView2NewWindowRequestedEventArgs args) =>
            Browser_NewWindowRequested(state, args);
        void WindowCloseRequestedHandler(object? sender, object args) =>
            Browser_WindowCloseRequested(state);
        void DownloadStartingHandler(object? sender, CoreWebView2DownloadStartingEventArgs args) =>
            Browser_DownloadStarting(state, args);
        void ProcessFailedHandler(object? sender, CoreWebView2ProcessFailedEventArgs args)
        {
            WriteSessionDiagnostic($"process-failed kind={args.ProcessFailedKind}");
            Browser_ProcessFailed(state, args);
        }
        void PermissionRequestedHandler(object? sender, CoreWebView2PermissionRequestedEventArgs args) =>
            Browser_CorePermissionRequested(state, core, args);
        void ContextMenuRequestedHandler(object? sender, CoreWebView2ContextMenuRequestedEventArgs args) =>
            Browser_ContextMenuRequested(state, args);

        state.WebViewEvents.Bind(browser, core, bindings =>
        {
            bindings.Add(
                () => core.WebResourceRequested += WebResourceRequestedHandler,
                () => core.WebResourceRequested -= WebResourceRequestedHandler);
            bindings.Add(
                () => browser.NavigationStarting += NavigationStartingHandler,
                () => browser.NavigationStarting -= NavigationStartingHandler);
            bindings.Add(
                () => browser.NavigationCompleted += NavigationCompletedHandler,
                () => browser.NavigationCompleted -= NavigationCompletedHandler);
            bindings.Add(
                () => core.ContentLoading += ContentLoadingHandler,
                () => core.ContentLoading -= ContentLoadingHandler);
            bindings.Add(
                () => core.DOMContentLoaded += DomContentLoadedHandler,
                () => core.DOMContentLoaded -= DomContentLoadedHandler);
            bindings.Add(
                () => core.SourceChanged += SourceChangedHandler,
                () => core.SourceChanged -= SourceChangedHandler);
            bindings.Add(
                () => core.HistoryChanged += HistoryChangedHandler,
                () => core.HistoryChanged -= HistoryChangedHandler);
            bindings.Add(
                () => core.DocumentTitleChanged += DocumentTitleChangedHandler,
                () => core.DocumentTitleChanged -= DocumentTitleChangedHandler);
            bindings.Add(
                () => core.FaviconChanged += FaviconChangedHandler,
                () => core.FaviconChanged -= FaviconChangedHandler);
            bindings.Add(
                () => core.IsDocumentPlayingAudioChanged += AudioStateChangedHandler,
                () => core.IsDocumentPlayingAudioChanged -= AudioStateChangedHandler);
            bindings.Add(
                () => core.IsMutedChanged += AudioStateChangedHandler,
                () => core.IsMutedChanged -= AudioStateChangedHandler);
            bindings.Add(
                () => core.WebMessageReceived += WebMessageReceivedHandler,
                () => core.WebMessageReceived -= WebMessageReceivedHandler);
            bindings.Add(
                () => core.NewWindowRequested += NewWindowRequestedHandler,
                () => core.NewWindowRequested -= NewWindowRequestedHandler);
            bindings.Add(
                () => core.WindowCloseRequested += WindowCloseRequestedHandler,
                () => core.WindowCloseRequested -= WindowCloseRequestedHandler);
            bindings.Add(
                () => core.DownloadStarting += DownloadStartingHandler,
                () => core.DownloadStarting -= DownloadStartingHandler);
            bindings.Add(
                () => core.ProcessFailed += ProcessFailedHandler,
                () => core.ProcessFailed -= ProcessFailedHandler);
            bindings.Add(
                () => core.PermissionRequested += PermissionRequestedHandler,
                () => core.PermissionRequested -= PermissionRequestedHandler);
            bindings.Add(
                () => core.ContextMenuRequested += ContextMenuRequestedHandler,
                () => core.ContextMenuRequested -= ContextMenuRequestedHandler);
        });

        Task passwordBridgeTask = RegisterPasswordCaptureBridgeAsync(state);
        Task zoomBridgeTask = RegisterPageZoomBridgeAsync(state);
        Task storeBridgeTask = RegisterExtensionStoreBridgeAsync(state);
        await Task.WhenAll(passwordBridgeTask, zoomBridgeTask, storeBridgeTask);

        if (ensureCurrentDocumentBridges)
        {
            await EnsurePasswordCaptureBridgeOnCurrentDocumentAsync(state);
            await PreparePasswordCaptureSharedBufferAsync(state);
            if (Math.Abs(state.PageZoomFactor - 1.0) > 0.001)
            {
                await ApplyPageZoomAsync(state);
            }
        }

        WriteSessionDiagnostic(
            $"webview-configured environmentId={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(core.Environment)} " +
            $"sharedEnvironment={ReferenceEquals(core.Environment, _environment)}");
        InitializeExtensionToolbarForProfile(core.Profile);
    }

    private void DetachWebViewEvents(BrowserTabState state)
    {
        CancelPendingVaultFillOperations(state);
        ResetPasswordCaptureSharedBuffer(state);
        state.WebViewEvents.Detach();
    }

    private void Browser_FaviconChanged(BrowserTabState state)
    {
        if (RootGrid.DispatcherQueue.HasThreadAccess)
        {
            UpdateBrowserFavicon(state);
            return;
        }

        RootGrid.DispatcherQueue.TryEnqueue(() => UpdateBrowserFavicon(state));
    }

    private void UpdateBrowserFavicon(BrowserTabState state)
    {
        if (!_tabs.Contains(state) || state.Browser.CoreWebView2 is not CoreWebView2 coreWebView)
        {
            return;
        }

        string faviconUri = coreWebView.FaviconUri;
        if (string.Equals(state.LastFaviconUri, faviconUri, StringComparison.Ordinal) &&
            state.FaviconImage.Source is not null)
        {
            return;
        }

        state.LastFaviconUri = faviconUri;
        state.FaviconFallbackAttempted = false;
        if (!Uri.TryCreate(faviconUri, UriKind.Absolute, out Uri? uri))
        {
            state.FaviconImage.Source = null;
            TryUseFallbackTabFavicon(state);
            return;
        }

        try
        {
            state.FaviconImage.Source = new BitmapImage(uri);
            UpdateTabIconPresentation(state);
        }
        catch
        {
            state.FaviconImage.Source = null;
            TryUseFallbackTabFavicon(state);
        }
    }

    private void TryUseFallbackTabFavicon(BrowserTabState state)
    {
        if (state.FaviconFallbackAttempted)
        {
            state.FaviconImage.Source = null;
            UpdateTabIconPresentation(state);
            return;
        }

        Uri? pageUri = state.CurrentUri ?? state.PendingUri;
        if (!IsBrowsableUri(pageUri) || string.IsNullOrWhiteSpace(pageUri!.Host))
        {
            state.FaviconImage.Source = null;
            UpdateTabIconPresentation(state);
            return;
        }

        state.FaviconFallbackAttempted = true;
        try
        {
            string host = Uri.EscapeDataString(pageUri.Host);
            state.FaviconImage.Source = new BitmapImage(
                new Uri($"https://icons.duckduckgo.com/ip3/{host}.ico"));
            UpdateTabIconPresentation(state);
        }
        catch
        {
            state.FaviconImage.Source = null;
            UpdateTabIconPresentation(state);
        }
    }

    private static void UpdateTabIconPresentation(BrowserTabState state)
    {
        bool showAudio = state.AudioButton.Visibility == Visibility.Visible;
        bool showFavicon = !showAudio && state.FaviconImage.Source is not null;
        state.FaviconImage.Visibility = showFavicon ? Visibility.Visible : Visibility.Collapsed;
        state.Icon.Visibility = !showAudio && !showFavicon ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Browser_AudioStateChanged(BrowserTabState state)
    {
        if (RootGrid.DispatcherQueue.HasThreadAccess)
        {
            UpdateBrowserAudioState(state);
            return;
        }

        RootGrid.DispatcherQueue.TryEnqueue(() => UpdateBrowserAudioState(state));
    }

    private void UpdateBrowserAudioState(BrowserTabState state)
    {
        if (!_tabs.Contains(state) || state.Browser.CoreWebView2 is not CoreWebView2 coreWebView)
        {
            return;
        }

        state.IsPlayingAudio = coreWebView.IsDocumentPlayingAudio;
        if (state.IsPlayingAudio)
        {
            _ = EnsureMediaBridgeOnCurrentDocumentAsync(state);
        }

        string title = DisplayTabTitle(state.Title.Length == 0 ? "New tab" : state.Title);
        if (state.IsPlayingAudio)
        {
            state.HasMedia = true;
            state.MediaIsPlaying = true;
            state.MediaIsMuted = coreWebView.IsMuted;
            state.MediaTitle = string.IsNullOrWhiteSpace(state.MediaTitle) ? title : state.MediaTitle;
            state.LastMediaUpdate = DateTimeOffset.UtcNow;
        }
        else
        {
            state.MediaIsPlaying = false;
            state.MediaIsMuted = coreWebView.IsMuted;
        }

        bool showAudioControl = state.IsPlayingAudio || coreWebView.IsMuted;
        state.AudioButton.Visibility = showAudioControl
            ? Visibility.Visible
            : Visibility.Collapsed;
        state.AudioIcon.Glyph = coreWebView.IsMuted ? "\uE74F" : "\uE767";
        ToolTipService.SetToolTip(
            state.AudioButton,
            coreWebView.IsMuted ? "Unmute tab" : "Mute tab");
        UpdateTabIconPresentation(state);

        ToolTipService.SetToolTip(
            state.Header,
            state.IsPlayingAudio
                ? coreWebView.IsMuted ? $"{title} — muted" : $"{title} — playing audio"
                : coreWebView.IsMuted ? $"{title} — muted" : title);
        UpdateMediaHubAvailability();
        UpdateMediaHubUi();
    }

    private void Browser_WebResourceRequested(
        BrowserTabState state,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        Uri? documentUri = state.ResourceDocumentUri ?? state.CurrentUri ?? state.PendingUri;
        string? normalizedDocumentHost = state.ResourceDocumentHost;
        bool googleSessionDocument = state.ResourceDocumentIsGoogleSessionCritical;
        bool compatibilityDocument = state.ResourceDocumentIsCompatibilitySensitive;

        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out Uri? requestUri))
        {
            return;
        }

        // Runs for every subresource on the UI thread: build diagnostic strings (two COM
        // property reads and a URI format) only when diagnostics are actually on.
        void LogDecision(bool blocked, string reason)
        {
            if (SessionDiagnosticsEnabled)
            {
                WriteSessionDiagnostic(
                    $"request method={args.Request.Method} context={args.ResourceContext} " +
                    $"uri={FormatDiagnosticUri(requestUri)} blocked={blocked} reason={reason}");
            }
        }

        if (requestUri.Host.Equals(
                HomeBackgroundVirtualHost,
                StringComparison.OrdinalIgnoreCase))
        {
            LogDecision(blocked: false, "aero-home-resource");
            return;
        }

        bool googleSessionRequest = IsGoogleSessionCriticalHost(requestUri.Host);
        bool compatibilityRequest = IsCompatibilitySensitiveUri(requestUri);
        if (compatibilityRequest)
        {
            // Remember that this navigation reached a real browser-verification
            // resource. Some anti-bot handoffs end with WebErrorStatus.Unknown
            // even though the verification document is already visible.
            state.Navigation.MarkBrowserVerification(requestUri);
        }

        if (!IsPrivacyException(requestUri.Host) &&
            !googleSessionDocument &&
            !googleSessionRequest &&
            !compatibilityDocument &&
            !compatibilityRequest)
        {
            if (_settings.DoNotTrack)
            {
                args.Request.Headers.SetHeader("DNT", "1");
                args.Request.Headers.SetHeader("Sec-GPC", "1");
            }

            if (!_settings.CacheEnabled)
            {
                args.Request.Headers.SetHeader("Cache-Control", "no-store");
                args.Request.Headers.SetHeader("Pragma", "no-cache");
            }

            if (!_settings.CookiesEnabled)
            {
                // This remains an explicit user choice. Do not combine it with the
                // third-party-cookie policy below: WebResourceRequested has no
                // frame-origin information, so comparing every request with the
                // top-level document misclassifies a reservation/payment iframe's
                // own same-origin requests as third-party and destroys its session.
                args.Request.Headers.SetHeader("Cookie", string.Empty);
                LogDecision(blocked: false, "cookie-header-cleared-user-disabled-cookies");
            }
            else if (ShouldBlockThirdPartyCookie(documentUri, requestUri))
            {
                // Never rewrite Cookie for a normal third-party request. Chromium
                // must evaluate SameSite=None; Secure, CHIPS/partitioned cookies,
                // iframe context and profile tracking prevention itself. Aero Shield
                // still blocks matching tracker requests; it just no longer corrupts
                // cookies on requests that are allowed to reach the network.
                LogDecision(blocked: false, "cookie-managed-by-webview2");
            }
        }

        if (!_settings.BlockAdsAndTrackers)
        {
            LogDecision(blocked: false, "shield-disabled");
            return;
        }

        if (state.ResourceDocumentShieldAllowed)
        {
            LogDecision(blocked: false, "site-shield-allowlist");
            return;
        }

        if (googleSessionDocument || googleSessionRequest)
        {
            LogDecision(blocked: false, "google-session-critical");
            return;
        }

        if (compatibilityDocument || compatibilityRequest)
        {
            LogDecision(blocked: false, "browser-verification-service");
            return;
        }

        if (!state.ResourceNativeShieldEnabled)
        {
            LogDecision(blocked: false, "native-shield-inactive");
            return;
        }

        ShieldResourceContext shieldContext = ToShieldResourceContext(args.ResourceContext);
        bool shouldBlock = state.ResourceUseShieldFastLane
            ? RequestBlocker.ShouldBlockFastLane(
                requestUri,
                normalizedDocumentHost,
                shieldContext)
            : _requestBlocker.ShouldBlock(
                requestUri,
                normalizedDocumentHost,
                shieldContext,
                ToShieldLevel(_settings.ShieldMode));

        if (!shouldBlock)
        {
            LogDecision(blocked: false, "shield-allowed");
            return;
        }

        args.Response = state.Browser.CoreWebView2.Environment.CreateWebResourceResponse(
            null,
            204,
            "Blocked by Aero Shield",
            "Cache-Control: no-store\r\nContent-Length: 0");

        state.BlockedRequestCount++;
        state.NativeBlockedRequestCount++;
        Interlocked.Increment(ref _shieldBlockedSessionTotal);
        LogDecision(blocked: true, "native-shield-rule");
    }

    private void StartDeferredNavigationIfNeeded(BrowserTabState state)
    {
        if (state.PendingStartPage)
        {
            state.PendingStartPage = false;
            ShowStartPage(state);
            return;
        }

        if (state.PendingUri is Uri pendingUri)
        {
            state.PendingUri = null;
            Navigate(state, pendingUri);
        }
    }

    private void ResumeTabIfNeeded(BrowserTabState state)
    {
        try
        {
            if (state.Browser.CoreWebView2 is not CoreWebView2 core)
            {
                return;
            }

            if (core.IsSuspended)
            {
                core.Resume();
            }
            else
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            }

            state.IsSuspendedByMemorySaver = false;
            state.LowMemoryTargetApplied = false;
            if (!state.IsStartPage)
            {
                state.Icon.Glyph = "\uE774";
                Browser_FaviconChanged(state);
            }
        }
        catch
        {
            // A tab will also resume automatically when it becomes visible.
        }
    }

    private async void SleepTimer_Tick(object? sender, object e)
    {
        await RunMemorySaverCycleAsync();
        await RunResourceBudgetCycleAsync();
        await RunCookieVaultSweepAsync();
        await RunFolderSyncAsync();
    }

    private void SelectTab(BrowserTabState state)
    {
        if (!_tabs.Contains(state))
        {
            return;
        }

        if (IsSplitViewActive && !IsSplitParticipant(state))
        {
            ExitSplitView(state, saveSession: false);
        }

        EnsureTabGroupExpandedForSelection(state);
        DateTimeOffset activationTime = DateTimeOffset.UtcNow;
        if (_activeTab is BrowserTabState previous && !ReferenceEquals(previous, state))
        {
            previous.LastBackgroundedAt = activationTime;
        }

        bool tabChanged = !ReferenceEquals(_activeTab, state);
        _activeTab = state;
        if (tabChanged)
        {
            HideAddressSuggestions();
            SetAddressTextWithoutSuggestions(state.HasAddressDraft
                ? state.AddressDraft
                : AddressBox.FocusState != FocusState.Unfocused
                    ? (state.CurrentUri ?? state.PendingUri)?.AbsoluteUri ?? string.Empty
                    : FormatAddressForDisplay(state.CurrentUri ?? state.PendingUri));
            if (AddressBox.FocusState != FocusState.Unfocused)
            {
                AddressBox.SelectAll();
            }
        }
        state.LastActivatedAt = activationTime;
        state.LastBackgroundedAt = null;
        _ = SyncAdvancedShieldForUriAsync(state.CurrentUri ?? state.PendingUri);

        if (state.IsDiscarded)
        {
            UpdateVisibleBrowser();
            _ = RestoreDiscardedTabAsync(state);
        }
        else
        {
            ResumeTabIfNeeded(state);
            StartDeferredNavigationIfNeeded(state);
            UpdateVisibleBrowser();
        }
        UpdateActiveChrome();
        RefreshPasswordVaultForActiveTab();
        UpdateAllTabVisuals();
        SaveSessionSnapshot();

        if (_findBarOpen && FindBox.Text.Length > 0)
        {
            RestartFindTimer();
        }
    }

    private void ShowStartPage(BrowserTabState state)
    {
        if (_settings.NewTabMode == NewTabMode.Custom)
        {
            NavigateToConfiguredAddress(state, _settings.CustomNewTabUrl);
            return;
        }

        if (_settings.NewTabMode == NewTabMode.Google)
        {
            Navigate(state, new Uri("https://www.google.com/"));
            return;
        }

        ShowAeroHomePage(state);
    }

    private void ShowAeroHomePage(BrowserTabState state)
    {
        ClearAddressDraft(state);
        state.InternalPageAddress = null;
        if (state.IsDiscarded)
        {
            state.PendingUri = null;
            state.PendingStartPage = true;
            state.IsStartPage = true;
            state.CurrentUri = null;
            state.Title = "New tab";
            state.TitleText.Text = DisplayTabTitle("New tab");
            _ = RestoreDiscardedTabAsync(state);
            return;
        }

        state.PendingUri = null;
        state.PendingStartPage = false;
        state.IsStartPage = true;
        state.CurrentUri = null;
        state.Title = "New tab";
        state.TitleText.Text = DisplayTabTitle("New tab");
        state.FaviconImage.Source = null;
        state.FaviconFallbackAttempted = false;
        state.AudioButton.Visibility = Visibility.Collapsed;
        UpdateTabIconPresentation(state);
        state.Icon.Glyph = "\uE80F";
        ToolTipService.SetToolTip(state.Header, DisplayTabTitle("New tab"));
        state.Browser.NavigateToString(BuildNewTabPageHtml());
        SaveSessionSnapshot();
    }

    private void Navigate(BrowserTabState state, Uri uri)
    {
        ClearAddressDraft(state);
        if (TryNavigateInternalPage(state, uri.AbsoluteUri))
        {
            return;
        }

        state.InternalPageAddress = null;
        Uri secureUri = UpgradeToHttpsIfNeeded(uri);
        Uri target = _settings.CleanTrackingLinks &&
            !IsGoogleSessionCriticalHost(secureUri.Host) &&
            !IsCompatibilitySensitiveUri(secureUri)
                ? TrackingUrlCleaner.Clean(secureUri)
                : secureUri;

        if (state.IsDiscarded)
        {
            state.PendingUri = target;
            state.PendingStartPage = false;
            state.IsStartPage = false;
            state.CurrentUri = target;
            _ = RestoreDiscardedTabAsync(state);
            return;
        }

        state.PendingUri = null;
        state.PendingStartPage = false;
        state.IsStartPage = false;

        // Dispatch directly through CoreWebView2 when it is ready. This avoids
        // an extra XAML Source-property round trip on the hottest address-bar
        // path while preserving the same history and navigation events.
        if (state.Browser.CoreWebView2 is CoreWebView2 coreWebView)
        {
            coreWebView.Navigate(target.AbsoluteUri);
        }
        else
        {
            state.Browser.Source = target;
        }
    }

    private void NavigateFromAddressBox()
    {
        BrowserTabState? state = ActiveTab;
        if (state is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(AddressBox.Text))
        {
            ShowAeroHomePage(state);
            UpdateActiveChrome();
            return;
        }

        string rawAddress = AddressBox.Text.Trim();
        HideAddressSuggestions();
        ClearAddressDraft(state);
        if (TryNavigateInternalPage(state, rawAddress))
        {
            state.Browser.Focus(FocusState.Programmatic);
            return;
        }

        NavigationTarget target = AddressInputResolver.Resolve(
            rawAddress,
            _settings.SearchEngine,
            _settings.CustomSearchUrl);
        Navigate(state, target.Uri);
        state.Browser.Focus(FocusState.Programmatic);
    }

    private void Browser_NavigationStarting(
        BrowserTabState state,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (TryHandleDesktopAuthenticationNavigation(state, args) ||
            TryInterceptLockedSiteNavigation(state, args))
        {
            return;
        }

        TraceNavigationStarting(state, args);
        if (state.ContainerId is null && CookieVaultAppliesToWindow)
        {
            // Navigations are when sites set sign-in cookies: sweep soon after them.
            GetCookieVaultSession().NavigatedSinceSweep = true;
        }

        Uri? diagnosticNavigationUri =
            Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? parsedDiagnosticNavigationUri)
                ? parsedDiagnosticNavigationUri
                : null;
        Uri? previousDocumentUri = IsBrowsableUri(state.CurrentUri)
            ? state.CurrentUri
            : state.LastNavigationUri;
        WriteSessionDiagnostic(
            $"navigation-start id={args.NavigationId} redirected={args.IsRedirected} " +
            $"from={FormatDiagnosticUri(previousDocumentUri)} " +
            $"to={FormatDiagnosticUri(diagnosticNavigationUri)}");
        if (previousDocumentUri is not null)
        {
            _ = TraceCookieMetadataAsync(state, previousDocumentUri, "navigation-start-source");
        }
        if (diagnosticNavigationUri is not null &&
            !diagnosticNavigationUri.Equals(previousDocumentUri))
        {
            _ = TraceCookieMetadataAsync(state, diagnosticNavigationUri, "navigation-start-target");
        }

        // WebView2 keeps one NavigationId through an HTTP redirect chain. The
        // navigation module owns document identity and stale-completion tracking.
        bool isNewNavigation = state.Navigation.Begin(args.NavigationId);
        if (isNewNavigation)
        {
            CancelPendingVaultFillOperations(state);
            ResetPasswordCaptureSharedBuffer(state);
            state.HasUnsavedFormData = false;
            state.DevToolsOpenedForCurrentDocument = false;
            state.NativeCosmeticArtifactsApplied = false;
            state.YouTubeCleanArtifactsApplied = false;
            try
            {
                state.Browser.CoreWebView2!.Settings.AreDevToolsEnabled = false;
            }
            catch
            {
                // A controller can disappear while a discarded tab is restoring.
            }
        }

        // Do not enable/disable the uBlock extension from NavigationStarting.
        // Changing extension state while Chromium is committing an anti-bot
        // redirect can itself abort the navigation and surface as Unknown/0.
        // Shield synchronization is deferred until NavigationCompleted.

        ResetMediaState(state);
        NavigationDocumentClassification documentClassification =
            NavigationDocumentClassifier.Classify(
                args.Uri,
                !string.IsNullOrWhiteSpace(state.InternalPageAddress),
                state.IsStartPage);
        bool isAboutBlank = documentClassification.IsAboutBlank;
        bool isAeroHomeDocumentNavigation =
            documentClassification.IsAeroHomeDocumentNavigation;
        if (documentClassification.ShouldClearInternalPageAddress)
        {
            state.InternalPageAddress = null;
        }

        if (args.IsRedirected &&
            IsBrowsableUri(previousDocumentUri) &&
            Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? redirectTarget) &&
            IsBrowsableUri(redirectTarget) &&
            !GetCookieSiteKey(previousDocumentUri!.Host).Equals(
                GetCookieSiteKey(redirectTarget.Host),
                StringComparison.OrdinalIgnoreCase) &&
            SitePermissionStore.NormalizeOrigin(previousDocumentUri.AbsoluteUri) is string redirectSourceOrigin)
        {
            SitePermissionDecision redirectDecision =
                _sitePermissionStore.GetDecision(
                    redirectSourceOrigin,
                    SitePermissionKind.Redirects);
            if (redirectDecision == SitePermissionDecision.Ask)
            {
                redirectDecision = GetDefaultPermissionDecision(SitePermissionKind.Redirects);
            }

            if (redirectDecision == SitePermissionDecision.Block)
            {
                WriteSessionDiagnostic(
                    $"redirect blocked=true from={FormatDiagnosticUri(previousDocumentUri)} " +
                    $"to={FormatDiagnosticUri(redirectTarget)} reason=site-permission");
                args.Cancel = true;
                if (ReferenceEquals(state, ActiveTab))
                {
                    SitePermissionsHintText.Text =
                        $"Blocked a redirect from {previousDocumentUri.Host} to {redirectTarget.Host}.";
                }
                return;
            }
        }

        state.ResourceDocumentUri = null;
        state.ResourceDocumentHost = null;
        state.ResourceDocumentIsGoogleSessionCritical = false;
        state.ResourceDocumentIsCompatibilitySensitive = false;
        state.ResourceDocumentShieldAllowed = false;
        state.ResourceNativeShieldEnabled = false;
        state.ResourceUseShieldFastLane = false;

        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? navigationUri) &&
            IsBrowsableUri(navigationUri))
        {
            state.ResourceDocumentUri = navigationUri;
            state.ResourceDocumentHost = navigationUri.IdnHost.Trim().TrimEnd('.').ToLowerInvariant();
            state.ResourceDocumentIsGoogleSessionCritical =
                IsGoogleSessionCriticalHost(navigationUri.Host);
            state.ResourceDocumentIsCompatibilitySensitive =
                IsCompatibilitySensitiveUri(navigationUri);
            state.ResourceDocumentShieldAllowed =
                _settings.IsShieldAllowedForHost(navigationUri.Host);

            bool advancedShieldReady = HasAdvancedShield(state);
            bool nativeShieldEnabled = _settings.ShieldEngineMode switch
            {
                ShieldEngineMode.Native => true,
                ShieldEngineMode.Advanced => !advancedShieldReady,
                _ => true
            };
            state.ResourceNativeShieldEnabled = nativeShieldEnabled &&
                !(_settings.SpeedMode == SpeedMode.Compatibility && advancedShieldReady);
            state.ResourceUseShieldFastLane =
                _settings.ShieldEngineMode == ShieldEngineMode.Hybrid && advancedShieldReady;

            if (state.TransientRetryUri is null ||
                !state.TransientRetryUri.Equals(navigationUri))
            {
                state.TransientRetryUri = navigationUri;
                state.TransientRetryCount = 0;
            }

            state.LastNavigationUri = navigationUri;
            state.FailedNavigationUri = null;
            state.IsShowingNetworkErrorPage = false;
        }

        if (_settings.CleanTrackingLinks &&
            !args.IsRedirected &&
            Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? requestedUri) &&
            !IsGoogleSessionCriticalHost(requestedUri.Host) &&
            !IsCompatibilitySensitiveUri(requestedUri))
        {
            Uri cleanedUri = TrackingUrlCleaner.Clean(UpgradeToHttpsIfNeeded(requestedUri));
            if (!cleanedUri.Equals(requestedUri))
            {
                WriteSessionDiagnostic(
                    $"navigation-rewrite from={FormatDiagnosticUri(requestedUri)} " +
                    $"to={FormatDiagnosticUri(cleanedUri)} reason=tracking-parameter-cleanup");
                args.Cancel = true;
                state.Browser.Source = cleanedUri;
                return;
            }
        }

        state.BlockedRequestCount = 0;
        state.ThirdPartyCookieBlockCount = 0;

        if (!isAeroHomeDocumentNavigation && !isAboutBlank)
        {
            state.IsStartPage = false;
        }

        state.IsLoading = true;
        state.LastFaviconUri = null;
        state.FaviconFallbackAttempted = false;
        state.FaviconImage.Source = null;
        state.AudioButton.Visibility = Visibility.Collapsed;
        UpdateTabIconPresentation(state);
        state.Icon.Glyph = "\uE895";
        StartLoadingAnimation(state);

        if (ReferenceEquals(state, ActiveTab))
        {
            UpdateActiveChrome();
        }
    }

    private async void Browser_NavigationCompleted(
        BrowserTabState state,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        LogNavigationCompletion(state, args, "received");
        Uri? completedUri = state.CurrentUri ?? state.LastNavigationUri;
        WriteSessionDiagnostic(
            $"navigation-completed id={args.NavigationId} success={args.IsSuccess} " +
            $"httpStatus={args.HttpStatusCode} webError={args.WebErrorStatus} " +
            $"uri={FormatDiagnosticUri(completedUri)}");
        if (completedUri is not null)
        {
            await TraceCookieMetadataAsync(state, completedUri, "navigation-completed");
        }

        NavigationCompletionSnapshot completion =
            CreateNavigationCompletionSnapshot(state, args);
        NavigationPreProbeDecision preProbeDecision =
            NavigationCompletionPolicy.EvaluateBeforeDocumentProbe(completion);

        // A completion from a page that was replaced by a redirect, script
        // navigation, address-bar navigation, or reload must not affect the newer
        // document. Chromium treats an aborted navigation as abandoned, not as a
        // reason to replace the current page with an error document.
        if (preProbeDecision == NavigationPreProbeDecision.IgnoreStaleNavigation)
        {
            LogNavigationCompletion(state, args, "ignored-stale-navigation");
            return;
        }

        state.IsLoading = false;
        StopLoadingAnimation(state);
        state.Icon.Glyph = state.IsStartPage
            ? "\uE80F"
            : string.Equals(state.InternalPageAddress, SettingsAddress, StringComparison.OrdinalIgnoreCase)
                ? "\uE713"
                : "\uE774";
        if (!state.IsStartPage)
        {
            Browser_FaviconChanged(state);
        }

        if (!args.IsSuccess)
        {
            if (TryConsumeExpectedDownloadNavigationAbort(state, args))
            {
                // A top-level download commonly aborts the document navigation after
                // WebView2 has handed the response to DownloadStarting. This is a
                // successful download transition, not a network failure.
            }
            else if (preProbeDecision == NavigationPreProbeDecision.PreserveDocument)
            {
                // Keep documents returned by the server, intentional cancellations,
                // and browser-check handoffs proven by DOM/resource observations.
                PreserveCompletedDocumentAfterAmbiguousFailure(state);
                LogNavigationCompletion(state, args, "preserved-by-navigation-policy");
            }
            else if (await HasUsableDocumentAfterFailedNavigationAsync(state, args))
            {
                PreserveCompletedDocumentAfterAmbiguousFailure(state);
                LogNavigationCompletion(state, args, "preserved-committed-document");
            }
            else if (NavigationCompletionPolicy.EvaluateAfterFailedDocumentProbe(completion) ==
                NavigationPostProbeDecision.ShowNetworkErrorPage &&
                ShowNetworkErrorPage(state, args))
            {
                LogNavigationCompletion(state, args, "aero-network-error-page");
                if (ReferenceEquals(state, ActiveTab))
                {
                    Title = GetAeroWindowTitle(DisplayTabTitle("Connection problem"));
                }
            }
            else
            {
                // Unknown and other ambiguous statuses are not proof of a network
                // outage. Leave WebView2's document/interstitial intact.
                PreserveCompletedDocumentAfterAmbiguousFailure(state);
                LogNavigationCompletion(state, args, "preserved-ambiguous-status");
            }
        }
        else if (!state.IsShowingNetworkErrorPage)
        {
            state.TransientRetryCount = 0;
            // DOMContentLoaded normally applies cosmetic filtering first. Only
            // run the fallback here when that event did not arrive for this
            // navigation, avoiding duplicate CSS generation/injection.
            if (state.LastDomContentLoadedNavigationId != args.NavigationId)
            {
                await ApplyCosmeticFilteringAsync(state);
            }

            if (state.Browser.CoreWebView2 is CoreWebView2 core &&
                !IsPrivacyException(state.CurrentUri?.Host) &&
                !IsGoogleSessionCriticalHost(state.CurrentUri?.Host) &&
                !IsGoogleSessionCriticalHost(state.LastNavigationUri?.Host) &&
                !IsCompatibilitySensitiveUri(state.CurrentUri) &&
                !IsCompatibilitySensitiveUri(state.LastNavigationUri))
            {
                Uri? currentUri = state.CurrentUri ?? state.LastNavigationUri;
                if (!_settings.CookiesEnabled && currentUri is not null)
                {
                    await DeleteCookiesForOriginAsync(
                        core.CookieManager,
                        currentUri.GetLeftPart(UriPartial.Authority));
                }
                else if (currentUri is not null)
                {
                    await EnforceTemporaryCookiesForSiteAsync(core, currentUri);
                }
            }
        }

        if (!state.IsShowingNetworkErrorPage &&
            IsBrowsableUri(state.CurrentUri ?? state.LastNavigationUri) &&
            state.Navigation.ActiveNavigationId == args.NavigationId)
        {
            _ = SyncAdvancedShieldWithoutBlockingNavigationAsync(
                state,
                (state.CurrentUri ?? state.LastNavigationUri)!,
                args.NavigationId);
        }

        await CompleteTabRestoreAsync(state);

        if (ReferenceEquals(state, ActiveTab))
        {
            UpdateActiveChrome();

            if (_findBarOpen && FindBox.Text.Length > 0)
            {
                RestartFindTimer();
            }
        }
    }

    private static NavigationCompletionSnapshot CreateNavigationCompletionSnapshot(
        BrowserTabState state,
        CoreWebView2NavigationCompletedEventArgs args) =>
        new(
            args.NavigationId,
            state.Navigation.ActiveNavigationId,
            state.Navigation.LastDomContentLoadedNavigationId,
            state.Navigation.BrowserVerificationNavigationId,
            args.IsSuccess,
            args.WebErrorStatus.ToString(),
            args.HttpStatusCode);

    private static bool TryConsumeExpectedDownloadNavigationAbort(
        BrowserTabState state,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (state.DownloadNavigationSuppressionUntil is not DateTimeOffset suppressionUntil ||
            DateTimeOffset.UtcNow > suppressionUntil)
        {
            state.DownloadNavigationSuppressionUntil = null;
            state.DownloadNavigationUri = null;
            state.DownloadReturnUri = null;
            return false;
        }

        string status = args.WebErrorStatus.ToString();
        if (!string.Equals(status, "ConnectionAborted", StringComparison.Ordinal) &&
            !string.Equals(status, "OperationCanceled", StringComparison.Ordinal))
        {
            return false;
        }

        Uri? returnUri = state.DownloadReturnUri;
        state.DownloadNavigationSuppressionUntil = null;
        state.DownloadNavigationUri = null;
        state.DownloadReturnUri = null;
        state.TransientRetryCount = 0;
        state.IsShowingNetworkErrorPage = false;
        state.FailedNavigationUri = null;

        if (IsBrowsableUri(returnUri))
        {
            state.CurrentUri = returnUri;
            state.PendingUri = null;
            state.LastNavigationUri = returnUri;
            ToolTipService.SetToolTip(state.Header, returnUri!.AbsoluteUri);
        }

        return true;
    }

    private static void PreserveCompletedDocumentAfterAmbiguousFailure(BrowserTabState state)
    {
        state.TransientRetryCount = 0;
        state.FailedNavigationUri = null;
        state.IsShowingNetworkErrorPage = false;
    }

    private static async Task<bool> HasUsableDocumentAfterFailedNavigationAsync(
        BrowserTabState state,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (state.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return false;
        }

        try
        {
            string json = await core.ExecuteScriptAsync(CommittedDocumentProbe.Script);
            if (!CommittedDocumentProbe.TryReadUsableDocumentUri(json, out Uri? documentUri))
            {
                return false;
            }

            state.Navigation.MarkDomContentLoaded(args.NavigationId);
            state.CurrentUri = documentUri;
            return true;
        }
        catch
        {
            // Chromium's own network error document does not expose a normal
            // scriptable http(s) page. In that case a definitive status may still
            // be handled by ShowNetworkErrorPage.
            return false;
        }
    }

    private async Task ApplyCosmeticFilteringAsync(BrowserTabState state)
    {
        Uri? uri = state.CurrentUri ?? state.PendingUri;
        CoreWebView2? core = state.Browser.CoreWebView2;
        bool shieldUnavailable = !_settings.BlockAdsAndTrackers ||
            !IsBrowsableUri(uri) ||
            _settings.IsShieldAllowedForHost(uri!.Host) ||
            IsGoogleSessionCriticalHost(uri.Host) ||
            IsCompatibilitySensitiveUri(uri);

        if (shieldUnavailable || core is null)
        {
            if (state.NativeCosmeticArtifactsApplied || state.YouTubeCleanArtifactsApplied)
            {
                await RemoveShieldPageArtifactsAsync(state);
                state.NativeCosmeticArtifactsApplied = false;
                state.YouTubeCleanArtifactsApplied = false;
            }
            return;
        }

        bool advancedReady = HasAdvancedShield(state);
        bool useFullNativeCosmetics = _settings.ShieldCosmeticFiltering &&
            _settings.SpeedMode != SpeedMode.Compatibility &&
            (_settings.ShieldEngineMode == ShieldEngineMode.Native || !advancedReady);

        try
        {
            if (useFullNativeCosmetics)
            {
                string script = _requestBlocker.BuildCosmeticInjectionScript(uri!.Host, ToShieldLevel(_settings.ShieldMode));
                if (script.Length > 0)
                {
                    await core.ExecuteScriptAsync(script);
                    state.NativeCosmeticArtifactsApplied = true;
                }
                else if (state.NativeCosmeticArtifactsApplied)
                {
                    await core.ExecuteScriptAsync(
                        "document.getElementById('aero-shield-cosmetic-style')?.remove();");
                    state.NativeCosmeticArtifactsApplied = false;
                }
            }
            else if (state.NativeCosmeticArtifactsApplied)
            {
                await core.ExecuteScriptAsync(
                    "document.getElementById('aero-shield-cosmetic-style')?.remove();");
                state.NativeCosmeticArtifactsApplied = false;
            }

            // uBlock Origin already handles YouTube when the advanced engine is ready.
            // Running Aero's DOM cleaner at the same time duplicates work and can slow
            // YouTube's highly dynamic single-page interface.
            bool shouldUseYouTubeCleanMode = _settings.ShieldYouTubeCleanMode &&
                IsYouTubeHost(uri!.Host) &&
                !advancedReady;
            if (shouldUseYouTubeCleanMode)
            {
                await ApplyYouTubeCleanModeAsync(state);
                state.YouTubeCleanArtifactsApplied = true;
            }
            else if (state.YouTubeCleanArtifactsApplied)
            {
                await RemoveYouTubeCleanModeAsync(state);
                state.YouTubeCleanArtifactsApplied = false;
            }
        }
        catch
        {
            // Shield page cleanup is best-effort and must never break navigation.
        }
    }

    private void Browser_SourceChanged(BrowserTabState state)
    {
        if (state.IsShowingNetworkErrorPage && state.FailedNavigationUri is Uri failedUri)
        {
            state.CurrentUri = failedUri;
        }
        else if (state.IsStartPage)
        {
            state.CurrentUri = null;
        }
        else if (!string.IsNullOrWhiteSpace(state.InternalPageAddress) &&
            Uri.TryCreate(state.InternalPageAddress, UriKind.Absolute, out Uri? internalUri))
        {
            state.CurrentUri = internalUri;
        }
        else if (Uri.TryCreate(state.Browser.CoreWebView2.Source, UriKind.Absolute, out Uri? currentUri))
        {
            state.CurrentUri = currentUri;
        }

        if (ReferenceEquals(state, ActiveTab))
        {
            UpdateActiveChrome();
            RefreshPasswordVaultForActiveTab();
        }

        SaveSessionSnapshot();
    }

    private void Browser_HistoryChanged(BrowserTabState state)
    {
        if (ReferenceEquals(state, ActiveTab))
        {
            UpdateNavigationButtons();
        }
    }

    private void Browser_DocumentTitleChanged(BrowserTabState state)
    {
        if (state.IsShowingNetworkErrorPage)
        {
            state.Title = "Connection problem";
            state.TitleText.Text = DisplayTabTitle("Connection problem");
            if (ReferenceEquals(state, ActiveTab))
            {
                Title = GetAeroWindowTitle(DisplayTabTitle("Connection problem"));
            }
            return;
        }

        if (state.IsStartPage)
        {
            state.Title = "New tab";
            state.TitleText.Text = DisplayTabTitle("New tab");
            state.Icon.Glyph = "\uE80F";
            ToolTipService.SetToolTip(state.Header, DisplayTabTitle("New tab"));

            if (ReferenceEquals(state, ActiveTab))
            {
                Title = GetAeroWindowTitle();
            }

            return;
        }

        string pageTitle = state.Browser.CoreWebView2.DocumentTitle?.Trim() ?? string.Empty;
        if (pageTitle.Length == 0)
        {
            pageTitle = state.CurrentUri?.Host ?? "New tab";
        }

        bool titleChanged = !string.Equals(state.Title, pageTitle, StringComparison.Ordinal);
        bool firstHistoryUpdateForNavigation =
            state.LastHistoryNavigationId != state.ActiveNavigationId;

        if (titleChanged)
        {
            state.Title = pageTitle;
            state.TitleText.Text = TrimTabTitle(pageTitle);
            ToolTipService.SetToolTip(state.Header, pageTitle);
            Browser_AudioStateChanged(state);
        }

        bool historyUpdated = false;
        if (_settings.HistoryEnabled && (firstHistoryUpdateForNavigation || titleChanged))
        {
            _dataStore.AddHistory(
                state.CurrentUri,
                pageTitle,
                refreshVisitTimestamp: firstHistoryUpdateForNavigation);
            state.LastHistoryNavigationId = state.ActiveNavigationId;
            historyUpdated = true;
        }
        if (historyUpdated && _libraryPaneOpen && _libraryView == LibraryView.History)
        {
            RefreshLibraryPane();
        }

        if (titleChanged && ReferenceEquals(state, ActiveTab))
        {
            Title = GetAeroWindowTitle(pageTitle);
        }
    }

    private static bool IsGoogleAuthenticationHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string normalized = host.Trim().TrimEnd('.');
        return normalized.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("accounts.youtube.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("myaccount.google.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("consent.google.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("oauth2.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("oauthaccountmanager.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("apis.google.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGoogleSessionCriticalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string normalized = host.Trim().TrimEnd('.');
        return IsGoogleAuthenticationHost(normalized) ||
            normalized.Equals("mail.google.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("gmail.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".gmail.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("www.google.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("ogs.google.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("clients.google.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("clients6.google.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".clients6.google.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("googleapis.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("gstatic.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".gstatic.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("googleusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompatibilitySensitiveHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string normalized = host.Trim().TrimEnd('.').ToLowerInvariant();

        static bool Matches(string current, string domain) =>
            current.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            current.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase);

        return Matches(normalized, "market.yandex.ru") ||
            Matches(normalized, "market.yandex.com") ||
            Matches(normalized, "market.yandex.kz") ||
            Matches(normalized, "market.yandex.by") ||
            Matches(normalized, "market.yandex.uz") ||
            Matches(normalized, "wildberries.ru") ||
            Matches(normalized, "wildberries.by") ||
            Matches(normalized, "wildberries.kz") ||
            Matches(normalized, "wildberries.am") ||
            Matches(normalized, "wildberries.kg") ||
            Matches(normalized, "wildberries.uz") ||
            Matches(normalized, "wildberries.ge") ||
            Matches(normalized, "challenges.cloudflare.com") ||
            Matches(normalized, "hcaptcha.com") ||
            Matches(normalized, "recaptcha.net") ||
            Matches(normalized, "arkoselabs.com") ||
            Matches(normalized, "funcaptcha.com") ||
            Matches(normalized, "geetest.com") ||
            Matches(normalized, "geevisit.com") ||
            Matches(normalized, "datadome.co") ||
            Matches(normalized, "incapsula.com");
    }

    private static bool IsCompatibilitySensitiveRequest(Uri requestUri)
    {
        string path = requestUri.AbsolutePath;
        return path.StartsWith("/cdn-cgi/challenge-platform", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/cdn-cgi/turnstile", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/captcha/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/recaptcha/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompatibilitySensitiveUri(Uri? uri) =>
        IsBrowsableUri(uri) &&
        (IsCompatibilitySensitiveHost(uri!.Host) ||
         IsCompatibilitySensitiveRequest(uri) ||
         RequestBlocker.IsBrowserVerificationResource(uri));

    private async void Browser_NewWindowRequested(
        BrowserTabState sourceState,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();

        try
        {
            Uri? requestedUri = Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? parsed)
                ? parsed
                : null;
            bool isGoogleAuthenticationPopup =
                IsGoogleAuthenticationHost(requestedUri?.Host) ||
                IsGoogleSessionCriticalHost(sourceState.CurrentUri?.Host) ||
                IsGoogleSessionCriticalHost(sourceState.PendingUri?.Host);
            string? origin = SitePermissionStore.NormalizeOrigin(sourceState.CurrentUri?.AbsoluteUri);
            SitePermissionDecision popupDecision = origin is null
                ? GetDefaultPermissionDecision(SitePermissionKind.Popups)
                : _sitePermissionStore.GetDecision(origin, SitePermissionKind.Popups);
            if (popupDecision == SitePermissionDecision.Ask)
            {
                popupDecision = GetDefaultPermissionDecision(SitePermissionKind.Popups);
            }

            if (popupDecision == SitePermissionDecision.Ask &&
                !args.IsUserInitiated &&
                !isGoogleAuthenticationPopup &&
                origin is not null)
            {
                await _permissionPromptGate.WaitAsync();
                try
                {
                    PermissionPromptResult result = await ShowPermissionPromptAsync(
                        sourceState,
                        origin,
                        SitePermissionKind.Popups,
                        isUserInitiated: false);
                    popupDecision = result.Decision;
                    if (result.Remember && result.Decision != SitePermissionDecision.Ask)
                    {
                        _sitePermissionStore.SetDecision(
                            origin,
                            SitePermissionKind.Popups,
                            result.Decision);
                    }
                }
                finally
                {
                    _permissionPromptGate.Release();
                }
            }

            if (popupDecision == SitePermissionDecision.Block)
            {
                args.Handled = true;
                return;
            }

            if (popupDecision == SitePermissionDecision.Ask &&
                !args.IsUserInitiated &&
                !isGoogleAuthenticationPopup)
            {
                args.Handled = true;
                return;
            }

            if (!isGoogleAuthenticationPopup &&
                _settings.OpenLinksBehavior == OpenLinksBehavior.CurrentTab &&
                requestedUri is not null)
            {
                Navigate(sourceState, requestedUri);
                args.Handled = true;
                return;
            }

            if (!isGoogleAuthenticationPopup &&
                _settings.OpenLinksBehavior == OpenLinksBehavior.NewWindow &&
                requestedUri is not null)
            {
                BrowserTabTransfer transfer = new(
                    requestedUri,
                    isStartPage: false,
                    isPinned: false,
                    pageZoomFactor: 1,
                    isMuted: false,
                    title: requestedUri.Host,
                    externalAuthenticationStartedAt:
                        IsDesktopAuthenticationContextActive(sourceState)
                            ? sourceState.ExternalAuthenticationStartedAt
                            : null);
                MainWindow newWindow = new(
                    _isPrivateWindow,
                    _browserProfile,
                    transfer,
                    _isPrivateWindow ? _privateUserDataFolder : null);
                newWindow.Activate();
                args.Handled = true;
                return;
            }

            BrowserTabState newState = await CreateTabAsync(
                null,
                selectTab: true,
                deferWebViewConfiguration: true,
                sameProfileAs: sourceState);
            InheritDesktopAuthenticationContext(sourceState, newState);
            newState.IsPopup = true;

            // WebView2 requires the untouched, initialized view to be assigned first.
            // Request filters and navigation handlers are connected only afterwards.
            args.NewWindow = newState.Browser.CoreWebView2;
            args.Handled = true;
            await ConfigureWebViewAsync(newState, ensureCurrentDocumentBridges: true);
        }
        catch
        {
            args.Handled = false;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void Browser_WindowCloseRequested(BrowserTabState state)
    {
        if (!state.IsPopup || !_tabs.Contains(state) || state.IsClosing)
        {
            return;
        }

        RootGrid.DispatcherQueue.TryEnqueue(async () =>
        {
            if (_tabs.Contains(state) && !state.IsClosing)
            {
                await CloseTabAsync(state);
            }
        });
    }

    private MenuFlyout BuildTabContextFlyout(BrowserTabState state)
    {
        MenuFlyout flyout = new();

        MenuFlyoutItem pinItem = new() { Text = "Pin tab" };
        pinItem.Click += (_, _) => TogglePinnedItemFromContext(state);
        flyout.Items.Add(pinItem);

        MenuFlyoutItem movePinnedLeftItem = new() { Text = "Move pinned tab left" };
        movePinnedLeftItem.Click += (_, _) => MovePinnedTab(state, -1);
        flyout.Items.Add(movePinnedLeftItem);

        MenuFlyoutItem movePinnedRightItem = new() { Text = "Move pinned tab right" };
        movePinnedRightItem.Click += (_, _) => MovePinnedTab(state, 1);
        flyout.Items.Add(movePinnedRightItem);

        MenuFlyoutSubItem groupItem = new() { Text = "Add tab to group" };
        flyout.Items.Add(groupItem);

        MenuFlyoutItem removeFromGroupItem = new() { Text = "Remove from group" };
        removeFromGroupItem.Click += (_, _) => RemoveTabFromGroup(state);
        flyout.Items.Add(removeFromGroupItem);

        MenuFlyoutItem duplicateItem = new() { Text = "Duplicate tab" };
        duplicateItem.Click += async (_, _) => await DuplicateTabAsync(state);
        flyout.Items.Add(duplicateItem);

        MenuFlyoutSubItem containerItem = new()
        {
            Text = Localization.AeroLocalizer.TranslateUi("Open in container")
        };
        foreach (MenuFlyoutItem item in BuildContainerMenuItems(() =>
                     IsBrowsableUri(state.CurrentUri) ? state.CurrentUri : null))
        {
            containerItem.Items.Add(item);
        }

        flyout.Items.Add(containerItem);

        MenuFlyoutItem splitViewItem = new() { Text = "Open in Split View" };
        splitViewItem.Click += (_, _) =>
        {
            if (IsSplitParticipant(state))
            {
                ExitSplitView(state);
            }
            else
            {
                OpenTabInSplitViewFromContext(state);
            }
        };
        flyout.Items.Add(splitViewItem);

        MenuFlyoutItem moveToNewWindowItem = new() { Text = "Move tab to new window" };
        moveToNewWindowItem.Click += async (_, _) =>
            await MoveTabToNewWindowFromContextAsync(state);
        flyout.Items.Add(moveToNewWindowItem);

        MenuFlyoutItem reloadItem = new() { Text = "Reload" };
        reloadItem.Click += (_, _) => ReloadTab(state);
        flyout.Items.Add(reloadItem);

        MenuFlyoutItem muteItem = new() { Text = "Mute tab" };
        muteItem.Click += (_, _) => ToggleTabMuted(state);
        flyout.Items.Add(muteItem);

        MenuFlyoutItem copyAddressItem = new() { Text = "Copy page address" };
        copyAddressItem.Click += (_, _) => CopyTabAddress(state);
        flyout.Items.Add(copyAddressItem);

        MenuFlyoutItem unloadItem = new() { Text = "Unload tab to save memory" };
        unloadItem.Click += async (_, _) => await DiscardTabAsync(state);
        flyout.Items.Add(unloadItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem closeItem = new() { Text = "Close tab" };
        closeItem.Click += async (_, _) => await CloseTabAsync(state);
        flyout.Items.Add(closeItem);

        MenuFlyoutItem closeOthersItem = new() { Text = "Close other tabs" };
        closeOthersItem.Click += async (_, _) => await CloseOtherTabsAsync(state);
        flyout.Items.Add(closeOthersItem);

        MenuFlyoutItem closeLeftItem = new() { Text = "Close tabs to the left" };
        closeLeftItem.Click += async (_, _) => await CloseTabsToLeftAsync(state);
        flyout.Items.Add(closeLeftItem);

        MenuFlyoutItem closeRightItem = new() { Text = "Close tabs to the right" };
        closeRightItem.Click += async (_, _) => await CloseTabsToRightAsync(state);
        flyout.Items.Add(closeRightItem);

        flyout.Opening += (_, _) =>
        {
            int index = _tabs.IndexOf(state);
            TabGroupState? group = FindTabGroup(state);
            pinItem.Text = GetPinnedContextText(state);
            bool standalonePinned = state.IsPinned && group is null;
            movePinnedLeftItem.Visibility = standalonePinned
                ? Visibility.Visible
                : Visibility.Collapsed;
            movePinnedRightItem.Visibility = standalonePinned
                ? Visibility.Visible
                : Visibility.Collapsed;
            movePinnedLeftItem.IsEnabled = standalonePinned && CanMovePinnedTab(state, -1);
            movePinnedRightItem.IsEnabled = standalonePinned && CanMovePinnedTab(state, 1);
            groupItem.Text = group is null ? "Add tab to group" : "Move tab to group";
            groupItem.IsEnabled = index >= 0 && !state.IsPinned;
            removeFromGroupItem.Visibility = group is null
                ? Visibility.Collapsed
                : Visibility.Visible;
            PopulateTabGroupSubmenu(state, groupItem);
            duplicateItem.IsEnabled = index >= 0;
            splitViewItem.Text = IsSplitParticipant(state)
                ? "Exit Split View"
                : "Open in Split View";
            splitViewItem.IsEnabled = index >= 0 &&
                (IsSplitParticipant(state) || _tabs.Count > 1);
            moveToNewWindowItem.IsEnabled = index >= 0;
            reloadItem.IsEnabled = index >= 0 && !state.IsStartPage;
            muteItem.Text = state.Browser.CoreWebView2?.IsMuted == true ? "Unmute tab" : "Mute tab";
            muteItem.IsEnabled = state.Browser.CoreWebView2 is not null;
            copyAddressItem.IsEnabled = state.CurrentUri is not null || state.PendingUri is not null;
            unloadItem.IsEnabled = index >= 0 &&
                !ReferenceEquals(state, ActiveTab) &&
                !IsSplitParticipant(state) &&
                !IsTabEffectivelyPinned(state) &&
                !state.IsDiscarded &&
                state.Browser.CoreWebView2 is not null;
            closeItem.IsEnabled = index >= 0 && !IsTabCloseProtected(state);
            closeOthersItem.IsEnabled = index >= 0 && _tabs.Any(tab =>
                !ReferenceEquals(tab, state) && !IsTabCloseProtected(tab));
            closeLeftItem.IsEnabled = index > 0 &&
                _tabs.Take(index).Any(tab => !IsTabCloseProtected(tab));
            closeRightItem.IsEnabled = index >= 0 &&
                _tabs.Skip(index + 1).Any(tab => !IsTabCloseProtected(tab));
        };

        return flyout;
    }

    private async Task DuplicateTabAsync(BrowserTabState state)
    {
        if (!_tabs.Contains(state))
        {
            return;
        }

        Uri? uri = state.CurrentUri ?? state.PendingUri;
        bool showStartPage = state.IsStartPage || state.PendingStartPage || uri is null;
        await CreateTabAsync(
            uri,
            selectTab: true,
            showStartPage: showStartPage,
            tabGroupId: state.TabGroupId,
            sameProfileAs: state);
    }

    private void ReloadTab(BrowserTabState state)
    {
        if (state.IsDiscarded)
        {
            _ = RestoreDiscardedTabAsync(state);
            return;
        }

        if (state.Browser.CoreWebView2 is not null && !state.IsStartPage)
        {
            state.Browser.Reload();
        }
    }

    private void TabAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BrowserTabState state })
        {
            return;
        }

        ToggleTabMuted(state);
        UpdateBrowserAudioState(state);
    }

    private static void ToggleTabMuted(BrowserTabState state)
    {
        if (state.Browser.CoreWebView2 is CoreWebView2 coreWebView)
        {
            coreWebView.IsMuted = !coreWebView.IsMuted;
        }
    }

    private static void CopyTabAddress(BrowserTabState state)
    {
        Uri? uri = state.CurrentUri ?? state.PendingUri;
        if (uri is null)
        {
            return;
        }

        DataPackage package = new();
        package.SetText(uri.AbsoluteUri);
        Clipboard.SetContent(package);
    }

    private async Task CloseOtherTabsAsync(BrowserTabState state)
    {
        if (!_tabs.Contains(state))
        {
            return;
        }

        foreach (BrowserTabState other in _tabs.Where(tab =>
            !ReferenceEquals(tab, state) && !IsTabCloseProtected(tab)).ToList())
        {
            await CloseTabAsync(other);
        }

        SelectTab(state);
    }

    private async Task CloseTabsToLeftAsync(BrowserTabState state)
    {
        int index = _tabs.IndexOf(state);
        if (index <= 0)
        {
            return;
        }

        foreach (BrowserTabState other in _tabs.Take(index)
            .Where(tab => !IsTabCloseProtected(tab))
            .ToList())
        {
            await CloseTabAsync(other);
        }

        SelectTab(state);
    }

    private async Task CloseTabsToRightAsync(BrowserTabState state)
    {
        int index = _tabs.IndexOf(state);
        if (index < 0)
        {
            return;
        }

        foreach (BrowserTabState other in _tabs.Skip(index + 1)
            .Where(tab => !IsTabCloseProtected(tab))
            .ToList())
        {
            await CloseTabAsync(other);
        }

        SelectTab(state);
    }

    private async void TabHeader_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border header || header.Tag is not BrowserTabState state)
        {
            return;
        }

        var properties = e.GetCurrentPoint(header).Properties;
        if (properties.IsMiddleButtonPressed)
        {
            e.Handled = true;
            await CloseTabAsync(state);
            return;
        }

        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        state.IsPointerPressed = true;
        UpdateTabVisual(state);
        BeginTabDragCandidate(header, state, e);
        e.Handled = true;
    }

    private void TabHeader_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border { Tag: BrowserTabState state })
        {
            state.IsPointerPressed = false;
            UpdateTabVisual(state);
        }
    }

    private void TabHeader_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border { Tag: BrowserTabState state })
        {
            state.IsPointerOver = true;
            UpdateTabVisual(state);
        }
    }

    private void TabHeader_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border { Tag: BrowserTabState state })
        {
            state.IsPointerOver = false;
            UpdateTabVisual(state);
        }
    }

    private async void TabCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BrowserTabState state })
        {
            await CloseTabAsync(state);
        }
    }

    private async Task CloseTabAsync(BrowserTabState state)
    {
        if (ReferenceEquals(state, _tabDragCandidate))
        {
            FinishTabDrag(animateDrop: false);
        }

        int index = _tabs.IndexOf(state);
        if (index < 0 || state.IsClosing)
        {
            return;
        }

        if (IsTabCloseProtected(state))
        {
            ShowPinnedTabProtection(state);
            return;
        }

        HandleSplitTabRemoval(state);
        state.IsClosing = true;
        _tabWidthExpansionLocked = true;
        int widthLockGeneration = ++_tabWidthLockGeneration;
        await AnimateTabExitAsync(state);

        RememberClosedTab(new ClosedTabInfo(
            state.CurrentUri ?? state.PendingUri,
            state.IsStartPage || state.PendingStartPage,
            state.IsPinned,
            state.TabGroupId));
        RestoreClosedTabMenuItem.IsEnabled = true;

        bool wasActive = ReferenceEquals(state, ActiveTab);

        TabStripPanel.Children.Remove(state.Header);
        _tabs.RemoveAt(index);
        RemoveEmptyTabGroups(state.TabGroupId);
        RefreshTabStripOrder();
        UpdateMediaHubAvailability();
        UpdateMediaHubUi();
        StopLoadingAnimation(state);
        ReleaseTabHeader(state);
        state.FaviconImage.Source = null;
        state.SnapshotView.Source = null;
        BrowserHost.Children.Remove(state.SnapshotView);
        DeleteTabSnapshot(state);
        ReleaseTabWebView(state, createReplacement: false);
        UpdateTabWidths();
        _ = ReleaseTabWidthExpansionLockAsync(widthLockGeneration);

        if (_tabs.Count == 0)
        {
            _activeTab = null;
            await CreateTabAsync(null, selectTab: true, showStartPage: true);
            return;
        }

        if (wasActive)
        {
            SelectTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }
        else
        {
            UpdateAllTabVisuals();
        }

        SaveSessionSnapshot();
    }

    private async Task ReleaseTabWidthExpansionLockAsync(int generation)
    {
        await Task.Delay(700);
        if (generation != _tabWidthLockGeneration)
        {
            return;
        }

        _tabWidthExpansionLocked = false;
        UpdateTabWidths();
    }


    private void ReleaseTabHeader(BrowserTabState state)
    {
        state.CloseButton.Click -= TabCloseButton_Click;
        state.CloseButton.Tag = null;

        state.Header.PointerPressed -= TabHeader_PointerPressed;
        state.Header.PointerReleased -= TabHeader_PointerReleased;
        state.Header.PointerEntered -= TabHeader_PointerEntered;
        state.Header.PointerExited -= TabHeader_PointerExited;
        state.Header.ContextFlyout = null;
        state.Header.Tag = null;
        ToolTipService.SetToolTip(state.Header, null);
    }

    private void TabStripPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_tabDragActive || _tabGroupDragActive)
        {
            return;
        }

        _tabWidthLockGeneration++;
        _tabWidthExpansionLocked = false;
        UpdateTabWidths();
    }

    private void UpdateVisibleBrowser()
    {
        ApplySplitViewLayout();
        foreach (BrowserTabState state in _tabs)
        {
            bool isVisible = IsSplitViewActive
                ? IsSplitParticipant(state)
                : ReferenceEquals(state, ActiveTab);
            state.Browser.Visibility = isVisible && !state.IsDiscarded
                ? Visibility.Visible
                : Visibility.Collapsed;
            state.SnapshotView.Visibility = isVisible &&
                state.SnapshotView.Source is not null &&
                (state.IsDiscarded || state.RestoreInProgress)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
    }

    private void UpdateActiveChrome()
    {
        BrowserTabState? state = ActiveTab;

        if (state is null)
        {
            AddressBox.Text = string.Empty;
            PageProgress.Visibility = Visibility.Collapsed;
            BackButton.IsEnabled = false;
            ForwardButton.IsEnabled = false;
            ReloadButton.IsEnabled = false;
            BookmarkButton.IsEnabled = false;
            BookmarkIcon.Glyph = "\uE734";
            ShieldButton.IsEnabled = false;
            RecoveryBar.Visibility = Visibility.Collapsed;
            Title = GetAeroWindowTitle();
            UpdateSitePermissionsUiWithProfileRefresh();
            UpdatePageToolsUi();
            return;
        }

        if (AddressBox.FocusState == FocusState.Unfocused)
        {
            SetAddressTextWithoutSuggestions(state.HasAddressDraft
                ? state.AddressDraft
                : FormatAddressForDisplay(state.CurrentUri ?? state.PendingUri));
        }

        PageProgress.Visibility = state.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        ReloadIcon.Glyph = state.IsLoading ? "\uE711" : "\uE72C";
        ToolTipService.SetToolTip(ReloadButton, state.IsLoading ? "Stop" : "Reload (Ctrl+R)");
        ReloadButton.IsEnabled = true;

        UpdateNavigationButtons();
        UpdateSiteIndicator(state.CurrentUri ?? state.PendingUri);
        UpdateBookmarkButton();
        UpdateShieldUi();
        UpdateRecoveryBar();
        UpdateSitePermissionsUiWithProfileRefresh();
        UpdatePageToolsUi();

        string title = state.Title.Length == 0 ? "Aero" : state.Title;
        Title = GetAeroWindowTitle(title);
    }

    private void UpdateNavigationButtons()
    {
        BrowserTabState? state = ActiveTab;
        BackButton.IsEnabled = state?.Browser.CanGoBack == true;
        ForwardButton.IsEnabled = state?.Browser.CanGoForward == true;
    }

    private void UpdateSiteIndicator(Uri? uri)
    {
        bool canProtect = IsBrowsableUri(uri);
        bool advancedReady = HasAdvancedShield(ActiveTab);
        bool siteAllowed = canProtect && _settings.IsShieldAllowedForHost(uri!.Host);
        bool compatibilityMode = canProtect && IsCompatibilitySensitiveUri(uri);
        bool shieldEnabled = _settings.BlockAdsAndTrackers &&
            canProtect &&
            !siteAllowed &&
            !compatibilityMode;

        ShieldButton.IsEnabled = canProtect || advancedReady || _uBlockOrigin.Extension is not null;
        ShieldIcon.Glyph = shieldEnabled ? "\uE83D" : "\uE785";
        string engineName = _settings.ShieldEngineMode switch
        {
            ShieldEngineMode.Advanced => advancedReady ? "Advanced" : "Advanced fallback",
            ShieldEngineMode.Native => "Native",
            _ => advancedReady ? "Hybrid" : "Hybrid fallback"
        };
        string shieldToolTip = shieldEnabled
            ? $"Aero Shield {engineName} — Ctrl+Shift+S"
            : compatibilityMode
                ? "Aero Shield is paused for site compatibility"
                : siteAllowed
                    ? "Aero Shield is off for this site"
                    : "Aero Shield is off";
        ToolTipService.SetToolTip(ShieldButton, shieldToolTip);
    }

    private void UpdateShieldUi()
    {
        BrowserTabState? state = ActiveTab;
        Uri? uri = state?.CurrentUri ?? state?.PendingUri;
        bool canProtect = IsBrowsableUri(uri);
        bool advancedReady = HasAdvancedShield(state);
        bool siteAllowed = canProtect && _settings.IsShieldAllowedForHost(uri!.Host);
        bool compatibilityMode = canProtect && IsCompatibilitySensitiveUri(uri);
        bool siteShieldEnabled = _settings.BlockAdsAndTrackers &&
            canProtect &&
            !siteAllowed &&
            !compatibilityMode;

        _updatingShieldUi = true;
        try
        {
            UpdateUBlockOriginUi();
            ShieldSiteText.Text = canProtect ? uri!.Host : "Open a website to manage protection";
            int blocked = state?.NativeBlockedRequestCount ?? 0;
            ShieldBlockedText.Text = blocked.ToString("N0");
            ShieldSessionText.Text = $"native fast blocks • {Interlocked.Read(ref _shieldBlockedSessionTotal):N0} this session";

            bool fastLane = _settings.ShieldEngineMode == ShieldEngineMode.Hybrid && advancedReady;
            ShieldRuleCountText.Text = (fastLane
                ? RequestBlocker.FastRuleCount
                : _requestBlocker.NetworkRuleCount).ToString("N0");
            ShieldRuleLabelText.Text = fastLane ? "fast-lane rules" : "native network rules";

            SiteShieldToggle.IsEnabled = canProtect &&
                _settings.BlockAdsAndTrackers &&
                !compatibilityMode;
            SiteShieldToggle.IsOn = siteShieldEnabled;
            ShieldEngineComboBox.SelectedIndex = (int)_settings.ShieldEngineMode;
            ShieldModeComboBox.SelectedIndex = (int)_settings.ShieldMode;
            ShieldCosmeticToggle.IsOn = _settings.ShieldCosmeticFiltering;
            ShieldYoutubeToggle.IsOn = _settings.ShieldYouTubeCleanMode;

            string engineSummary = _settings.ShieldEngineMode switch
            {
                ShieldEngineMode.Native => "Aero native engine is active",
                ShieldEngineMode.Advanced when advancedReady => $"uBlock Origin {UBlockOriginBootstrapper.Version} is active",
                ShieldEngineMode.Advanced => "Advanced engine is unavailable — native fallback is active",
                _ when advancedReady => $"Hybrid active: Aero fast lane + uBlock Origin {UBlockOriginBootstrapper.Version}",
                _ => "Hybrid fallback: native engine is active while uBlock Origin starts"
            };

            if (!_settings.BlockAdsAndTrackers)
            {
                ShieldStatusText.Text = "Shield is off globally.";
            }
            else if (compatibilityMode)
            {
                ShieldStatusText.Text =
                    "Compatibility mode: Shield is paused for this store so its redirects and anti-bot checks can load.";
            }
            else if (siteAllowed)
            {
                ShieldStatusText.Text = "Shield is off for this site.";
            }
            else if (_requestBlocker.IsUpdating)
            {
                ShieldStatusText.Text = engineSummary + " • updating native lists…";
            }
            else if (!string.IsNullOrWhiteSpace(_requestBlocker.LastError))
            {
                ShieldStatusText.Text = engineSummary + " • cached native lists retained";
            }
            else
            {
                ShieldStatusText.Text = engineSummary;
            }
        }
        finally
        {
            _updatingShieldUi = false;
        }
    }

    private void ShieldFlyout_Opened(object sender, object e)
    {
        ShieldRulesTextBox.Text = string.Join(Environment.NewLine, _settings.ShieldCustomRules);
        UpdateShieldUi();
    }

    private async void SiteShieldToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingShieldUi)
        {
            return;
        }

        Uri? uri = ActiveTab?.CurrentUri ?? ActiveTab?.PendingUri;
        if (uri is null || !IsBrowsableUri(uri) || IsCompatibilitySensitiveUri(uri))
        {
            return;
        }

        _settings.SetShieldAllowedForHost(uri.Host, allowed: !SiteShieldToggle.IsOn);
        _settings.Save();
        await SyncAdvancedShieldForUriAsync(uri);

        if (ActiveTab is BrowserTabState state)
        {
            try
            {
                if (SiteShieldToggle.IsOn)
                {
                    await ApplyCosmeticFilteringAsync(state);
                }
                else
                {
                    await RemoveShieldPageArtifactsAsync(state);
                    state.NativeCosmeticArtifactsApplied = false;
                    state.YouTubeCleanArtifactsApplied = false;
                }

                state.Browser.Reload();
            }
            catch
            {
                // The page can already be navigating while protection changes.
            }
        }

        UpdateSiteIndicator(uri);
        UpdateShieldUi();
    }

    private async void ShieldModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingShieldUi || ShieldModeComboBox.SelectedIndex < 0)
        {
            return;
        }

        _settings.ShieldMode = (ShieldMode)ShieldModeComboBox.SelectedIndex;
        _settings.Save();
        if (ActiveTab is BrowserTabState state)
        {
            await ApplyCosmeticFilteringAsync(state);
        }
        UpdateShieldUi();
    }

    private async void ShieldCosmeticToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingShieldUi)
        {
            return;
        }

        _settings.ShieldCosmeticFiltering = ShieldCosmeticToggle.IsOn;
        _settings.Save();
        if (ActiveTab is BrowserTabState state)
        {
            if (_settings.ShieldCosmeticFiltering)
            {
                await ApplyCosmeticFilteringAsync(state);
            }
            else
            {
                try
                {
                    await state.Browser.CoreWebView2.ExecuteScriptAsync(
                        "document.getElementById('aero-shield-cosmetic-style')?.remove();");
                }
                catch
                {
                    // The page may be navigating while the option changes.
                }
            }
        }
    }

    private void ApplyShieldRulesButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShieldCustomRules = ShieldRulesTextBox.Text
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        _settings.Save();
        _requestBlocker.ApplyCustomRules(_settings.ShieldCustomRules);
        UpdateShieldUi();
    }

    private async void UpdateShieldListsButton_Click(object sender, RoutedEventArgs e)
    {
        await _requestBlocker.RefreshAsync(_settings.ShieldCustomRules, _shieldCancellation.Token);
        UpdateShieldUi();
        if (ActiveTab is BrowserTabState state)
        {
            await ApplyCosmeticFilteringAsync(state);
        }
    }

    private async void UblockOriginLink_Click(object sender, RoutedEventArgs e)
    {
        ShieldFlyout.Hide();
        await CreateTabAsync(
            new Uri("https://github.com/gorhill/uBlock"),
            selectTab: true,
            showStartPage: false);
    }

    private void RequestBlocker_FiltersChanged(object? sender, EventArgs e)
    {
        RootGrid.DispatcherQueue.TryEnqueue(async () =>
        {
            UpdateShieldUi();
            if (ActiveTab is BrowserTabState state)
            {
                await ApplyCosmeticFilteringAsync(state);
            }
        });
    }

    private void ReloadShieldButton_Click(object sender, RoutedEventArgs e)
    {
        ShieldFlyout.Hide();
        ActiveTab?.Browser.Reload();
    }

    private async Task<bool> RestorePreviousSessionAsync()
    {
        if (_isPrivateWindow || !_settings.RestorePreviousSession)
        {
            return false;
        }

        BrowserSessionData session = _dataStore.LoadSession();
        if (session.Tabs.Count == 0)
        {
            return false;
        }

        _isRestoringSession = true;
        RestoreTabGroupsFromSession(session.TabGroups);

        try
        {
            int activeIndex = Math.Clamp(session.ActiveTabIndex, 0, session.Tabs.Count - 1);
            int index = 0;

            foreach (SessionTabEntry entry in session.Tabs.Take(30))
            {
                Uri? uri = null;
                if (!entry.IsStartPage &&
                    Uri.TryCreate(entry.Url, UriKind.Absolute, out Uri? parsedUri) &&
                    IsRestorableUri(parsedUri))
                {
                    uri = parsedUri;
                }

                bool isActiveEntry = index == activeIndex;
                BrowserTabState state = await CreateTabAsync(
                    isActiveEntry ? uri : null,
                    selectTab: false,
                    showStartPage: isActiveEntry && (entry.IsStartPage || uri is null),
                    deferWebViewInitialization: !isActiveEntry,
                    tabGroupId: entry.GroupId,
                    containerId: entry.ContainerId == BurnerContainerId ? null : entry.ContainerId);

                state.IsPinned = entry.IsPinned;
                if (state.IsPinned)
                {
                    state.TabGroupId = null;
                }

                if (!isActiveEntry)
                {
                    state.PendingStartPage = entry.IsStartPage || uri is null;
                    state.PendingUri = state.PendingStartPage ? null : uri;
                    state.IsStartPage = state.PendingStartPage;
                    state.Title = state.PendingStartPage ? "New tab" : uri!.Host;
                    state.TitleText.Text = TrimTabTitle(DisplayTabTitle(state.Title));
                    ShowSleepingTabVisual(state, "Not loaded yet — click to open");
                }

                index++;
            }

            RemoveEmptyTabGroups();
            NormalizePinnedTabOrder();
            RefreshTabStripOrder();
            UpdateTabWidths();
            UpdateAllTabVisuals();

            if (_tabs.Count == 0)
            {
                return false;
            }

            RestoreSplitViewFromSession(session);
            activeIndex = Math.Clamp(activeIndex, 0, _tabs.Count - 1);
            SelectTab(_tabs[activeIndex]);
            return true;
        }
        finally
        {
            _isRestoringSession = false;
            SaveSessionSnapshot();
        }
    }

    private void SaveSessionSnapshot()
    {
        if (_isPrivateWindow || _isRestoringSession || !_isInitialized || _tabs.Count == 0)
        {
            return;
        }

        // SourceChanged, tab selection, drag/reorder and group operations can fire
        // in bursts. Coalesce them so the UI thread never serializes session.json
        // while a page is starting or committing a navigation.
        _sessionSaveTimer.Stop();
        _sessionSaveTimer.Start();
    }

    private void SessionSaveTimer_Tick(object? sender, object e)
    {
        _sessionSaveTimer.Stop();
        PersistSessionSnapshot();
    }

    private void PersistSessionSnapshot()
    {
        if (_isPrivateWindow || _isRestoringSession || !_isInitialized || _tabs.Count == 0)
        {
            return;
        }

        BrowserSessionData session = new()
        {
            ActiveTabIndex = Math.Max(0, _activeTab is null ? 0 : _tabs.IndexOf(_activeTab)),
            SplitLeftTabIndex = IsSplitViewActive && _splitLeftTab is not null
                ? _tabs.IndexOf(_splitLeftTab)
                : null,
            SplitRightTabIndex = IsSplitViewActive && _splitRightTab is not null
                ? _tabs.IndexOf(_splitRightTab)
                : null,
            SplitRatio = _splitRatio,
            TabGroups = GetTabGroupsInStripOrder()
                .Where(group => GetTabsInGroup(group).Count > 0)
                .Select(group => new SessionTabGroupEntry
                {
                    Id = group.Id,
                    Name = group.Name,
                    Color = group.ColorKey,
                    IsCollapsed = group.IsCollapsed,
                    IsPinned = group.IsPinned
                }).ToList(),
            // A burner tab keeps its slot (indexes above refer to it) but never writes its URL.
            Tabs = _tabs.Select(state => IsBurnerTab(state)
                ? new SessionTabEntry { IsStartPage = true }
                : new SessionTabEntry
                {
                    Url = (state.CurrentUri ?? state.PendingUri)?.AbsoluteUri,
                    IsStartPage = state.IsStartPage || state.PendingStartPage ||
                        (state.CurrentUri is null && state.PendingUri is null),
                    IsPinned = state.IsPinned,
                    GroupId = state.IsPinned ? null : state.TabGroupId,
                    ContainerId = state.ContainerId
                }).ToList()
        };

        _dataStore.SaveSession(session);
    }

    private void UpdateBookmarkButton()
    {
        BrowserTabState? state = ActiveTab;
        bool canBookmark = state is not null && !state.IsStartPage && IsBrowsableUri(state.CurrentUri);
        bool isBookmarked = canBookmark && _dataStore.IsBookmarked(state!.CurrentUri);

        BookmarkButton.IsEnabled = canBookmark;
        BookmarkIcon.Glyph = isBookmarked ? "\uE735" : "\uE734";
        ToolTipService.SetToolTip(
            BookmarkButton,
            isBookmarked ? "Remove bookmark (Ctrl+D)" : "Bookmark this page (Ctrl+D)");
    }

    private void ToggleActiveBookmark()
    {
        BrowserTabState? state = ActiveTab;
        if (state is null || state.IsStartPage || !IsBrowsableUri(state.CurrentUri))
        {
            return;
        }

        _dataStore.ToggleBookmark(state.CurrentUri, state.Title);
        UpdateBookmarkButton();
        RenderBookmarksBar();

        if (_libraryPaneOpen && _libraryView == LibraryView.Bookmarks)
        {
            RefreshLibraryPane();
        }
    }

    private void OpenLibraryPane(LibraryView view)
    {
        CloseDownloadsPane();
        CloseCommandPalette();
        ClosePasswordsPane();
        CloseExtensionsPane();
        CloseWorkspacesPane();

        if (_settingsPaneOpen)
        {
            CloseSettingsPane();
        }

        bool wasOpen = _libraryPaneOpen;
        _libraryView = view;
        _libraryPaneOpen = true;
        LibraryPane.Visibility = Visibility.Visible;
        LibrarySearchBox.Text = string.Empty;
        RefreshLibraryPane();

        if (!wasOpen)
        {
            LibraryPane.Opacity = 0;
            LibraryPaneTransform.TranslateX = 24;
            AnimateOpacity(LibraryPane, 0, 1, StandardAnimationDuration);
            AnimateDouble(LibraryPaneTransform, "TranslateX", 24, 0, StandardAnimationDuration);
        }
    }

    private void CloseLibraryPane()
    {
        if (!_libraryPaneOpen)
        {
            return;
        }

        _libraryPaneOpen = false;
        AnimateOpacity(LibraryPane, LibraryPane.Opacity, 0, FastAnimationDuration, () =>
        {
            if (!_libraryPaneOpen)
            {
                LibraryPane.Visibility = Visibility.Collapsed;
            }
        });
        AnimateDouble(LibraryPaneTransform, "TranslateX", LibraryPaneTransform.TranslateX, 22, FastAnimationDuration);
    }

    private void RefreshLibraryPane()
    {
        if (!_libraryPaneOpen)
        {
            return;
        }

        string query = LibrarySearchBox.Text.Trim();
        LibraryItemsPanel.Children.Clear();

        bool showingHistory = _libraryView == LibraryView.History;
        LibraryTitleText.Text = showingHistory ? "History" : "Bookmarks";
        ClearHistoryButton.Visibility = showingHistory ? Visibility.Visible : Visibility.Collapsed;

        HistoryLibraryButton.Background = GetLibrarySelectorBrush(showingHistory);
        BookmarksLibraryButton.Background = GetLibrarySelectorBrush(!showingHistory);

        if (showingHistory)
        {
            IEnumerable<HistoryEntry> entries = _dataStore.Library.History;
            if (query.Length > 0)
            {
                entries = entries.Where(entry =>
                    entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    entry.Url.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            foreach (HistoryEntry entry in entries.Take(250))
            {
                string visited = entry.VisitedAt.ToLocalTime().ToString("dd MMM · HH:mm");
                LibraryItemsPanel.Children.Add(BuildLibraryItem(
                    entry.Title,
                    entry.Url,
                    visited,
                    isBookmark: false));
            }
        }
        else
        {
            IEnumerable<BookmarkEntry> entries = _dataStore.Library.Bookmarks;
            if (query.Length > 0)
            {
                entries = entries.Where(entry =>
                    entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    entry.Url.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            foreach (BookmarkEntry entry in entries)
            {
                string added = $"Saved {entry.AddedAt.ToLocalTime():dd MMM}";
                LibraryItemsPanel.Children.Add(BuildLibraryItem(
                    entry.Title,
                    entry.Url,
                    added,
                    isBookmark: true));
            }
        }

        bool isEmpty = LibraryItemsPanel.Children.Count == 0;
        LibraryEmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.IsEnabled = showingHistory && _dataStore.Library.History.Count > 0;
    }

    private Border BuildLibraryItem(
        string title,
        string url,
        string metadata,
        bool isBookmark)
    {
        Grid content = new()
        {
            ColumnSpacing = 8
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        StackPanel labels = new()
        {
            Spacing = 3
        };
        labels.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(title) ? url : title,
            FontSize = 13,
            Foreground = GetTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        labels.Children.Add(new TextBlock
        {
            Text = url,
            FontSize = 11,
            Foreground = GetMutedTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        labels.Children.Add(new TextBlock
        {
            Text = metadata,
            FontSize = 11,
            Foreground = GetMutedTextBrush()
        });

        Button openButton = new()
        {
            MinHeight = 66,
            Padding = new Thickness(8, 5, 8, 5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = GetTransparentBrush(),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Content = labels,
            Tag = url
        };
        openButton.Click += LibraryOpenButton_Click;

        Button removeButton = new()
        {
            Style = (Style)Application.Current.Resources["ChromeIconButtonStyle"],
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 },
            Tag = new LibraryItemTag(url, isBookmark)
        };
        ToolTipService.SetToolTip(removeButton, isBookmark ? "Remove bookmark" : "Remove from history");
        removeButton.Click += LibraryRemoveButton_Click;
        Grid.SetColumn(removeButton, 1);

        content.Children.Add(openButton);
        content.Children.Add(removeButton);

        return new Border
        {
            Padding = new Thickness(4),
            Background = new SolidColorBrush(IsDarkTheme()
                ? Color.FromArgb(255, 27, 32, 36)
                : Color.FromArgb(255, 245, 247, 248)),
            BorderBrush = new SolidColorBrush(IsDarkTheme()
                ? Color.FromArgb(255, 45, 52, 58)
                : Color.FromArgb(255, 220, 225, 228)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = content
        };
    }

    private async void LibraryOpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } ||
            !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            !IsBrowsableUri(uri))
        {
            return;
        }

        CloseLibraryPane();

        if (ActiveTab is BrowserTabState state)
        {
            Navigate(state, uri);
            state.Browser.Focus(FocusState.Programmatic);
        }
        else
        {
            await CreateTabAsync(uri, selectTab: true);
        }
    }

    private void LibraryRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LibraryItemTag item })
        {
            return;
        }

        if (item.IsBookmark)
        {
            _dataStore.RemoveBookmark(item.Url);
            UpdateBookmarkButton();
            RenderBookmarksBar();
        }
        else
        {
            _dataStore.RemoveHistoryEntry(item.Url);
        }

        RefreshLibraryPane();
    }

    private Brush GetLibrarySelectorBrush(bool selected)
    {
        if (!selected)
        {
            return GetTransparentBrush();
        }

        return new SolidColorBrush(IsDarkTheme()
            ? Color.FromArgb(255, 48, 55, 61)
            : Color.FromArgb(255, 225, 230, 233));
    }

    private static bool IsBrowsableUri(Uri? uri) =>
        uri is not null &&
        uri.IsAbsoluteUri &&
        (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private static bool IsRestorableUri(Uri? uri) =>
        IsBrowsableUri(uri) ||
        uri is not null &&
        !uri.AbsoluteUri.TrimEnd('/').Equals(
            DesktopAuthenticationPageAddress,
            StringComparison.OrdinalIgnoreCase) &&
        (uri.Scheme.Equals("aero", StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase));

    private void UpdateTabWidths()
    {
        if (_tabs.Count == 0)
        {
            return;
        }

        double pinnedWidth = Math.Max(Math.Clamp(_settings.PinnedTabWidth, 36, 80), ConfiguredTabBarHeight);
        List<BrowserTabState> visibleTabs = _tabs.Where(IsTabVisibleInStrip).ToList();
        int pinnedCount = visibleTabs.Count(IsTabEffectivelyPinned);
        int regularCount = visibleTabs.Count - pinnedCount;
        int groupCount = GetVisibleTabGroupCount();
        int visualItemCount = visibleTabs.Count + groupCount;
        double spacing = Math.Max(0, (visualItemCount - 1) * Math.Clamp(_settings.TabSpacing, 0, 14));
        double pinnedSpace = pinnedCount * pinnedWidth;
        double groupSpace = GetVisibleTabGroupHeaderSpace();

        double viewportWidth = TabScrollViewer.MaxWidth;
        if (double.IsNaN(viewportWidth) || double.IsInfinity(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = TabScrollViewer.ActualWidth;
        }

        if (viewportWidth <= 0)
        {
            return;
        }

        // Tabs keep their full size until the strip genuinely runs out of room.
        // Only then do regular tabs shrink together; pinned tabs always stay fixed.
        double requiredAtFullWidth =
            pinnedSpace +
            groupSpace +
            regularCount * TabMaximumWidth +
            spacing +
            Math.Clamp(_settings.TabStripPadding, 0, 40) +
            AddTabButtonSpace;

        double targetWidth = TabMaximumWidth;
        if (regularCount > 0 && requiredAtFullWidth > viewportWidth)
        {
            double availableForRegularTabs = Math.Max(
                0,
                viewportWidth - pinnedSpace - groupSpace - spacing -
                Math.Clamp(_settings.TabStripPadding, 0, 40) - AddTabButtonSpace);

            targetWidth = Math.Clamp(
                Math.Floor(availableForRegularTabs / regularCount),
                TabMinimumWidth,
                TabMaximumWidth);
        }

        if (_tabWidthExpansionLocked)
        {
            double currentWidth = visibleTabs
                .Where(tab =>
                    !IsTabEffectivelyPinned(tab) &&
                    !tab.IsClosing &&
                    tab.Header.Width > 0)
                .Select(tab => tab.Header.Width)
                .DefaultIfEmpty(targetWidth)
                .Min();
            targetWidth = Math.Min(targetWidth, currentWidth);
        }

        foreach (BrowserTabState state in _tabs)
        {
            state.Header.Width = IsTabEffectivelyPinned(state)
                ? pinnedWidth
                : targetWidth;
        }

        if (_tabDragActive &&
            _tabDragCandidate is BrowserTabState draggedState &&
            _tabDragPlaceholder is Border placeholder)
        {
            placeholder.Width = draggedState.Header.Width;
            placeholder.Height = draggedState.Header.Height;
        }
    }

    private void UpdateAllTabVisuals()
    {
        foreach (BrowserTabState state in _tabs)
        {
            UpdateTabVisual(state);
        }

        UpdateAllTabGroupVisuals();
    }

    private void UpdateTabVisual(BrowserTabState state)
    {
        UpdateTabAccessibility(state);
        bool isActive = ReferenceEquals(state, ActiveTab);
        bool isPinned = IsTabEffectivelyPinned(state);

        state.TitleText.Visibility = isPinned ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(state.IconHost, isPinned ? 3 : 1);
        state.Icon.HorizontalAlignment = HorizontalAlignment.Center;
        ApplyTabHeaderMetrics(state, isActive, isPinned);

        state.Header.Background = BuildConfiguredTabBrush(
            isActive,
            state.IsPointerOver,
            state.IsPointerPressed);
        ApplyConfiguredTabBorder(state, isActive, state.IsPointerOver);

        Color indicator = GetConfiguredAccentColor();
        state.ActiveIndicator.Background = new SolidColorBrush(indicator);
        state.CloseButton.Visibility = ShouldShowTabCloseButton(state, isActive, isPinned)
            ? Visibility.Visible
            : Visibility.Collapsed;
        state.Separator.Background = GetSoftBorderBrush();
        state.Separator.Visibility = _settings.ShowTabSeparators &&
            !isActive &&
            !state.IsPointerOver &&
            IsTabVisibleInStrip(state) &&
            !ReferenceEquals(state, GetLastVisibleTab())
                ? Visibility.Visible
                : Visibility.Collapsed;
        state.TitleText.Foreground = isActive && _chromeAccessibilitySettings.HighContrast
            ? GetChromeBrush("AeroOnAccentBrush")
            : isActive && _settings.TabUseAccentText
            ? new SolidColorBrush(indicator)
            : isActive
                ? GetTextBrush()
                : GetSecondaryTextBrush();
        state.Icon.Foreground = isActive && _chromeAccessibilitySettings.HighContrast
            ? GetChromeBrush("AeroOnAccentBrush")
            : isActive && _settings.TabUseAccentText
            ? new SolidColorBrush(indicator)
            : isActive
                ? GetTextBrush()
                : GetMutedTextBrush();
        state.AudioIcon.Foreground = state.Icon.Foreground;
        state.Icon.Opacity = isActive ? 1 : 0.82;
        state.AudioIcon.Opacity = state.Icon.Opacity;

        double targetTranslateY = GetTabTargetTranslateY(isActive, state.IsPointerOver);
        double targetScale = GetTabTargetScale(isActive, state.IsPointerOver);
        if (_tabDragActive && ReferenceEquals(state, _tabDragCandidate))
        {
            state.ActiveIndicator.Opacity = isActive &&
                state.ActiveIndicator.Visibility == Visibility.Visible ? 1 : 0;
            state.ActiveIndicatorTransform.ScaleX = isActive ? 1 : 0.55;
            state.IsVisualActive = isActive;
            return;
        }

        if (_settings.ReduceMotion || _settings.TabAnimation == TabAnimationStyle.None)
        {
            state.IsVisualActive = isActive;
            state.HeaderTransform.ScaleX = targetScale;
            state.HeaderTransform.ScaleY = targetScale;
            state.HeaderTransform.TranslateX = 0;
            state.HeaderTransform.TranslateY = targetTranslateY;
            if (state.ActiveIndicator.Visibility == Visibility.Visible)
            {
                state.ActiveIndicator.Opacity = isActive ? 1 : 0;
                state.ActiveIndicatorTransform.ScaleX = isActive ? 1 : 0.55;
            }
            return;
        }

        if (state.IsVisualActive != isActive)
        {
            state.IsVisualActive = isActive;
            AnimateTabSelection(state, isActive);
        }
        else
        {
            if (state.ActiveIndicator.Visibility == Visibility.Visible)
            {
                state.ActiveIndicator.Opacity = isActive ? 1 : 0;
                state.ActiveIndicatorTransform.ScaleX = isActive ? 1 : 0.55;
            }
            int duration = ConfiguredTabAnimationDuration;
            AnimateTabDouble(
                state.HeaderTransform,
                "ScaleX",
                state.HeaderTransform.ScaleX,
                targetScale,
                duration);
            AnimateTabDouble(
                state.HeaderTransform,
                "ScaleY",
                state.HeaderTransform.ScaleY,
                targetScale,
                duration);
            AnimateTabDouble(
                state.HeaderTransform,
                "TranslateY",
                state.HeaderTransform.TranslateY,
                targetTranslateY,
                duration);
        }
    }

    private async void AddTabButton_Click(object sender, RoutedEventArgs e)
    {
        await CreateTabAsync(null, selectTab: true, showStartPage: true);
    }

    private async void AddTabButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(AddTabButton).Properties.IsMiddleButtonPressed && _closedTabs.Count > 0)
        {
            e.Handled = true;
            await RestoreClosedTabAsync();
        }
    }

    private void TabListButton_Click(object sender, RoutedEventArgs e)
    {
        MenuFlyout flyout = new();

        foreach (BrowserTabState state in _tabs)
        {
            TabGroupState? group = FindTabGroup(state);
            string title = state.Title.Length == 0 ? "New tab" : TrimTabTitle(state.Title);
            MenuFlyoutItem item = new()
            {
                Text = group is null ? title : $"{group.Name}: {title}"
            };
            item.Click += (_, _) => SelectTab(state);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(TabListButton);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => GoBack();

    private void ForwardButton_Click(object sender, RoutedEventArgs e) => GoForward();

    private void ReloadButton_Click(object sender, RoutedEventArgs e) => ReloadOrStop();

    private void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is BrowserTabState state)
        {
            NavigateToConfiguredAddress(state, _settings.HomePageUrl);
            UpdateActiveChrome();
        }
    }

    private void BookmarkButton_Click(object sender, RoutedEventArgs e) => ToggleActiveBookmark();

    private void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HandleAddressSuggestionKeyDown(e))
        {
            return;
        }

        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        HideAddressSuggestions();
        NavigateFromAddressBox();
    }

    private void AddressSurface_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        UpdateAddressSurface(isPointerOver: true);

    private void AddressSurface_PointerExited(object sender, PointerRoutedEventArgs e) =>
        UpdateAddressSurface();

    private void AddressBox_GotFocus(object sender, RoutedEventArgs e)
    {
        UpdateAddressSurface();
        SetAddressTextWithoutSuggestions(
            ActiveTab is { HasAddressDraft: true } draftState
                ? draftState.AddressDraft
                : (ActiveTab?.CurrentUri ?? ActiveTab?.PendingUri)?.AbsoluteUri ?? string.Empty);
        AddressBox.SelectAll();
        RefreshAddressSuggestions(AddressBox.Text);
    }

    private void AddressBox_LostFocus(object sender, RoutedEventArgs e)
    {
        UpdateAddressSurface();
        ScheduleHideAddressSuggestions();
        if (ActiveTab is not { HasAddressDraft: true })
        {
            SetAddressTextWithoutSuggestions(
                FormatAddressForDisplay(ActiveTab?.CurrentUri ?? ActiveTab?.PendingUri));
        }
    }

    private static string FormatAddressForDisplay(Uri? uri)
    {
        if (uri is null)
        {
            return string.Empty;
        }

        string value = uri.AbsoluteUri;
        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            value = value[8..];
        }
        else if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            value = value[7..];
        }

        if (value.EndsWith("/", StringComparison.Ordinal) && uri.Query.Length == 0 && uri.Fragment.Length == 0)
        {
            value = value[..^1];
        }

        return value;
    }

    private async void NewTabMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        await CreateTabAsync(null, selectTab: true, showStartPage: true);
    }

    private void NewWindowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        MainWindow newWindow = new(
            isPrivate: false,
            browserProfile: _browserProfile,
            startupTransfer: null,
            sharedPrivateUserDataFolder: null);
        newWindow.Activate();
    }

    private void NewPrivateWindowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        MainWindow privateWindow = new(
            isPrivate: true,
            browserProfile: _browserProfile,
            startupTransfer: null,
            sharedPrivateUserDataFolder: null);
        privateWindow.Activate();
    }

    private async void RestoreClosedTabMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        await RestoreClosedTabAsync();
    }

    private void HistoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        OpenLibraryPane(LibraryView.History);
    }

    private void BookmarksMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        OpenLibraryPane(LibraryView.Bookmarks);
    }

    private void CloseLibraryButton_Click(object sender, RoutedEventArgs e) => CloseLibraryPane();

    private void HistoryLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        _libraryView = LibraryView.History;
        RefreshLibraryPane();
    }

    private void BookmarksLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        _libraryView = LibraryView.Bookmarks;
        RefreshLibraryPane();
    }

    private void LibrarySearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshLibraryPane();

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _dataStore.ClearHistory();
        RefreshLibraryPane();
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        OpenSettingsPane();
    }

    private void FindMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        OpenFindBar();
    }

    private void FullscreenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        ToggleFullscreen();
    }

    private void DeveloperToolsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        BrowserTabState? state = ActiveTab;
        if (state?.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        state.DevToolsOpenedForCurrentDocument = true;
        core.Settings.AreDevToolsEnabled = true;
        Uri? uri = state.CurrentUri ?? state.PendingUri;
        _passwordVaultService.RecordSecurityEvent(
            "devtools",
            "opened-vault-blocked",
            uri?.IsAbsoluteUri == true ? uri.GetLeftPart(UriPartial.Authority) : null);
        _passwordVaultService.Lock();
        RefreshPasswordVaultForActiveTab();
        core.OpenDevToolsWindow();
    }

    private async void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();

        ContentDialog dialog = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Aero",
            Content = $"A lightweight open-source browser in early development.\n\nVersion {typeof(MainWindow).Assembly.GetName().Version}" +
                (_isPrivateWindow ? "\nPrivate window" : string.Empty),
            CloseButtonText = "Close"
        };

        await dialog.ShowAsync();
    }

    private async void OpenSettingsPane()
    {
        CloseDownloadsPane();
        CloseCommandPalette();
        ClosePasswordsPane();
        CloseExtensionsPane();
        CloseWorkspacesPane();
        CloseLibraryPane();
        BrowserTabState state = ActiveTab ??
            await CreateTabAsync(null, selectTab: true, showStartPage: true);
        ShowSettingsPage(state);
    }

    private void CloseSettingsPane()
    {
        if (!_settingsPaneOpen)
        {
            return;
        }

        _settingsPaneOpen = false;
        AnimateOpacity(SettingsScrim, SettingsScrim.Opacity, 0, FastAnimationDuration);
        AnimateOpacity(SettingsPane, SettingsPane.Opacity, 0, FastAnimationDuration, () =>
        {
            if (!_settingsPaneOpen)
            {
                SettingsScrim.Visibility = Visibility.Collapsed;
                SettingsPane.Visibility = Visibility.Collapsed;
            }
        });
        AnimateDouble(SettingsPaneTransform, "TranslateX", SettingsPaneTransform.TranslateX, 24, FastAnimationDuration);
    }

    // Fluent "decelerate": one curve for enter and exit. Picking EaseIn by value direction
    // made panels sliding 28→0 start slow and stop hard.
    private static ExponentialEase DecelerateEase() =>
        new() { EasingMode = EasingMode.EaseOut, Exponent = 6 };

    private void AnimateOpacity(
        UIElement target,
        double from,
        double to,
        int durationMilliseconds,
        Action? completed = null)
    {
        DoubleAnimation animation = new()
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(
                GetFrameAlignedAnimationDuration(durationMilliseconds))),
            EasingFunction = DecelerateEase()
        };

        Storyboard storyboard = new();
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);

        if (completed is not null)
        {
            storyboard.Completed += (_, _) => completed();
        }

        storyboard.Begin();
    }

    private void AnimateDouble(
        DependencyObject target,
        string property,
        double from,
        double to,
        int durationMilliseconds,
        Action? completed = null)
    {
        DoubleAnimation animation = new()
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(
                GetFrameAlignedAnimationDuration(durationMilliseconds))),
            EasingFunction = DecelerateEase()
        };

        Storyboard storyboard = new();
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);

        if (completed is not null)
        {
            storyboard.Completed += (_, _) => completed();
        }

        storyboard.Begin();
    }

    private void AnimateTabEntrance(BrowserTabState state)
    {
        bool isActive = ReferenceEquals(state, ActiveTab);
        double targetScale = GetTabTargetScale(isActive, state.IsPointerOver);
        double targetTranslateY = GetTabTargetTranslateY(isActive, state.IsPointerOver);
        if (_settings.ReduceMotion || _settings.TabAnimation == TabAnimationStyle.None)
        {
            state.Header.Opacity = 1;
            state.HeaderTransform.ScaleX = targetScale;
            state.HeaderTransform.ScaleY = targetScale;
            state.HeaderTransform.TranslateX = 0;
            state.HeaderTransform.TranslateY = targetTranslateY;
            return;
        }

        (double scale, double translateX, double translateY) = _settings.TabAnimation switch
        {
            TabAnimationStyle.Scale => (0.86, 0, 0),
            TabAnimationStyle.Slide => (1, 18, 0),
            TabAnimationStyle.Bounce => (0.82, 0, 4),
            _ => (1, 0, 6)
        };
        int duration = ConfiguredTabAnimationDuration;
        state.Header.Opacity = 0;
        state.HeaderTransform.ScaleX = scale;
        state.HeaderTransform.ScaleY = scale;
        state.HeaderTransform.TranslateX = translateX;
        state.HeaderTransform.TranslateY = translateY;

        AnimateOpacity(state.Header, 0, 1, duration);
        AnimateTabDouble(state.HeaderTransform, "ScaleX", scale, targetScale, duration, entering: true);
        AnimateTabDouble(state.HeaderTransform, "ScaleY", scale, targetScale, duration, entering: true);
        AnimateTabDouble(state.HeaderTransform, "TranslateX", translateX, 0, duration, entering: true);
        AnimateTabDouble(state.HeaderTransform, "TranslateY", translateY, targetTranslateY, duration, entering: true);
    }

    private Task AnimateTabExitAsync(BrowserTabState state)
    {
        double width = state.Header.ActualWidth > 0 ? state.Header.ActualWidth : state.Header.Width;
        if (_settings.ReduceMotion || _settings.TabAnimation == TabAnimationStyle.None)
        {
            state.Header.Opacity = 0;
            state.Header.Width = 0;
            return Task.CompletedTask;
        }

        TaskCompletionSource<bool> completion = new();
        int duration = ConfiguredTabAnimationDuration;
        double targetScale = _settings.TabAnimation switch
        {
            TabAnimationStyle.Bounce => 0.78,
            TabAnimationStyle.Scale => 0.90,
            _ => 1
        };
        double targetTranslateX = _settings.TabAnimation == TabAnimationStyle.Slide ? 18 : 0;
        double targetTranslateY = 0;

        AnimateOpacity(state.Header, state.Header.Opacity, 0, Math.Max(70, duration / 2));
        AnimateTabDouble(state.HeaderTransform, "ScaleX", state.HeaderTransform.ScaleX, targetScale, duration);
        AnimateTabDouble(state.HeaderTransform, "ScaleY", state.HeaderTransform.ScaleY, targetScale, duration);
        AnimateTabDouble(state.HeaderTransform, "TranslateX", state.HeaderTransform.TranslateX, targetTranslateX, duration);
        AnimateTabDouble(state.HeaderTransform, "TranslateY", state.HeaderTransform.TranslateY, targetTranslateY, duration);
        AnimateTabDouble(
            state.Header,
            "Width",
            width,
            0,
            duration,
            completed: () => completion.TrySetResult(true));
        return completion.Task;
    }

    private void AnimateTabSelection(BrowserTabState state, bool isActive)
    {
        int duration = ConfiguredTabAnimationDuration;
        double targetScale = GetTabTargetScale(isActive, state.IsPointerOver);
        double targetTranslateY = GetTabTargetTranslateY(isActive, state.IsPointerOver);

        if (state.ActiveIndicator.Visibility == Visibility.Visible)
        {
            double targetIndicatorOpacity = isActive ? 1 : 0;
            double targetIndicatorScale = isActive ? 1 : 0.55;
            AnimateOpacity(
                state.ActiveIndicator,
                state.ActiveIndicator.Opacity,
                targetIndicatorOpacity,
                duration);
            AnimateTabDouble(
                state.ActiveIndicatorTransform,
                "ScaleX",
                state.ActiveIndicatorTransform.ScaleX,
                targetIndicatorScale,
                duration);
        }

        AnimateTabDouble(
            state.HeaderTransform,
            "ScaleX",
            state.HeaderTransform.ScaleX,
            targetScale,
            duration);
        AnimateTabDouble(
            state.HeaderTransform,
            "ScaleY",
            state.HeaderTransform.ScaleY,
            targetScale,
            duration);
        AnimateTabDouble(
            state.HeaderTransform,
            "TranslateX",
            state.HeaderTransform.TranslateX,
            0,
            duration);
        AnimateTabDouble(
            state.HeaderTransform,
            "TranslateY",
            state.HeaderTransform.TranslateY,
            targetTranslateY,
            duration);
    }

    private void StartLoadingAnimation(BrowserTabState state)
    {
        state.LoadingStoryboard?.Stop();
        state.IconTransform.Angle = 0;

        DoubleAnimation rotation = new()
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromMilliseconds(
                GetFrameAlignedAnimationDuration(850))),
            RepeatBehavior = RepeatBehavior.Forever
        };

        Storyboard storyboard = new();
        Storyboard.SetTarget(rotation, state.IconTransform);
        Storyboard.SetTargetProperty(rotation, "Angle");
        storyboard.Children.Add(rotation);
        state.LoadingStoryboard = storyboard;
        storyboard.Begin();
    }

    private static void StopLoadingAnimation(BrowserTabState state)
    {
        state.LoadingStoryboard?.Stop();
        state.LoadingStoryboard = null;
        state.IconTransform.Angle = 0;
    }

    private void SettingsScrim_Tapped(object sender, TappedRoutedEventArgs e) => CloseSettingsPane();

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e) => CloseSettingsPane();

    private void CancelSettingsButton_Click(object sender, RoutedEventArgs e) => CloseSettingsPane();

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        bool wasBlockingEnabled = _settings.BlockAdsAndTrackers;

        _settings.SpeedMode = (SpeedMode)Math.Max(0, SpeedModeComboBox.SelectedIndex);
        _settings.BlockAdsAndTrackers = TrackerBlockingToggle.IsOn;
        ShieldEngineMode previousEngineMode = _settings.ShieldEngineMode;
        _settings.ShieldEngineMode = (ShieldEngineMode)Math.Max(0, ShieldEngineSettingsComboBox.SelectedIndex);
        _settings.ShieldMode = (ShieldMode)Math.Max(0, ShieldModeSettingsComboBox.SelectedIndex);
        _settings.ShieldCosmeticFiltering = ShieldCosmeticSettingsToggle.IsOn;
        _settings.ShieldAutoUpdate = ShieldAutoUpdateToggle.IsOn;
        _settings.ShieldYouTubeCleanMode = ShieldYoutubeSettingsToggle.IsOn;
        _settings.CleanTrackingLinks = CleanLinksToggle.IsOn;
        _settings.SleepBackgroundTabs = SleepTabsToggle.IsOn;
        _settings.MemorySaverMode = (MemorySaverMode)Math.Max(0, MemorySaverModeComboBox.SelectedIndex);
        _settings.KeepAudioTabsAwake = KeepAudioTabsAwakeToggle.IsOn;
        _settings.ThemeMode = (AeroThemeMode)Math.Max(0, ThemeComboBox.SelectedIndex);
        _settings.NewTabMode = (NewTabMode)Math.Max(0, NewTabComboBox.SelectedIndex);
        _settings.ShowQuickLinks = QuickLinksToggle.IsOn;
        _settings.RestorePreviousSession = RestoreSessionToggle.IsOn;
        _settings.SavePasswords = SavePasswordsToggle.IsOn;
        _settings.AutofillForms = AutofillFormsToggle.IsOn;
        _settings.Save();
        ApplyMemorySaverSettings();
        ApplyPasswordAndAutofillSettingsToAllTabs();

        if (wasBlockingEnabled != _settings.BlockAdsAndTrackers ||
            previousEngineMode != _settings.ShieldEngineMode)
        {
            await ApplyShieldEngineModeAsync();
        }

        foreach (BrowserTabState tab in _tabs)
        {
            await ApplyCosmeticFilteringAsync(tab);
        }

        ApplyTheme();
        RefreshStartPages();
        UpdateActiveChrome();
        UpdateShieldUi();
        CloseSettingsPane();
    }

    private void NextTabAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SelectRelativeTab(1);
    }

    private void PreviousTabAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SelectRelativeTab(-1);
    }

    private void SelectRelativeTab(int direction)
    {
        if (_tabs.Count < 2 || ActiveTab is null)
        {
            return;
        }

        int currentIndex = _tabs.IndexOf(ActiveTab);
        int nextIndex = (currentIndex + direction + _tabs.Count) % _tabs.Count;
        SelectTab(_tabs[nextIndex]);
    }

    private async void NewTabAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await CreateTabAsync(null, selectTab: true, showStartPage: true);
    }

    private void NewWindowAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        MainWindow newWindow = new(
            isPrivate: false,
            browserProfile: _browserProfile,
            startupTransfer: null,
            sharedPrivateUserDataFolder: null);
        newWindow.Activate();
    }

    private void NewPrivateWindowAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        MainWindow privateWindow = new(
            isPrivate: true,
            browserProfile: _browserProfile,
            startupTransfer: null,
            sharedPrivateUserDataFolder: null);
        privateWindow.Activate();
    }

    private async void RestoreTabAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RestoreClosedTabAsync();
    }

    private async void CloseTabAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (ActiveTab is BrowserTabState state)
        {
            await CloseTabAsync(state);
        }
    }

    private void MuteTabAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ActiveTab is BrowserTabState state)
        {
            ToggleTabMuted(state);
        }
    }

    private void FocusAddressAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        AddressBox.Focus(FocusState.Programmatic);
        AddressBox.SelectAll();
    }

    private void BookmarkAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleActiveBookmark();
    }

    private void HistoryAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenLibraryPane(LibraryView.History);
    }

    private void FindAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenFindBar();
    }

    private async void FindNextAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (!_findBarOpen)
        {
            OpenFindBar();
            return;
        }

        await PerformFindAsync(backwards: false, resetSelection: false);
    }

    private async void FindPreviousAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (!_findBarOpen)
        {
            OpenFindBar();
            return;
        }

        await PerformFindAsync(backwards: true, resetSelection: false);
    }

    private void FullscreenAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleFullscreen();
    }

    private void ReloadAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ReloadOrStop();
    }

    private void BackAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        GoBack();
    }

    private void ForwardAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        GoForward();
    }

    private void StopAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (AddressBox.FocusState != FocusState.Unfocused)
        {
            args.Handled = true;
            HideAddressSuggestions();
            if (ActiveTab is BrowserTabState addressTab)
            {
                ClearAddressDraft(addressTab);
                SetAddressTextWithoutSuggestions((addressTab.CurrentUri ?? addressTab.PendingUri)?.AbsoluteUri ?? string.Empty);
                addressTab.Browser.Focus(FocusState.Programmatic);
            }
        }
        else if (_commandPaletteOpen)
        {
            args.Handled = true;
            CloseCommandPalette();
        }
        else if (_extensionsPaneOpen)
        {
            args.Handled = true;
            CloseExtensionsPane();
        }
        else if (_passwordsPaneOpen)
        {
            args.Handled = true;
            ClosePasswordsPane();
        }
        else if (_downloadsPaneOpen)
        {
            args.Handled = true;
            CloseDownloadsPane();
        }
        else if (_findBarOpen)
        {
            args.Handled = true;
            CloseFindBar();
        }
        else if (ActiveTab?.IsLoading == true)
        {
            args.Handled = true;
            ActiveTab.Browser.CoreWebView2.Stop();
        }
        else if (_settingsPaneOpen)
        {
            args.Handled = true;
            CloseSettingsPane();
        }
        else if (_libraryPaneOpen)
        {
            args.Handled = true;
            CloseLibraryPane();
        }
    }

    private void OpenFindBar()
    {
        if (_findBarOpen)
        {
            FindBox.Focus(FocusState.Programmatic);
            FindBox.SelectAll();
            return;
        }

        _findBarOpen = true;
        FindBar.Visibility = Visibility.Visible;
        FindBar.Opacity = 0;
        FindBarTransform.TranslateY = -8;
        AnimateOpacity(FindBar, 0, 1, StandardAnimationDuration);
        AnimateDouble(FindBarTransform, "TranslateY", -8, 0, StandardAnimationDuration);
        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
    }

    private async void CloseFindBar()
    {
        if (!_findBarOpen)
        {
            return;
        }

        _findBarOpen = false;
        _findTimer.Stop();
        AnimateOpacity(FindBar, FindBar.Opacity, 0, FastAnimationDuration, () =>
        {
            if (!_findBarOpen)
            {
                FindBar.Visibility = Visibility.Collapsed;
            }
        });
        AnimateDouble(FindBarTransform, "TranslateY", FindBarTransform.TranslateY, -8, FastAnimationDuration);

        if (ActiveTab?.Browser.CoreWebView2 is not null)
        {
            try
            {
                await ActiveTab.Browser.CoreWebView2.ExecuteScriptAsync(
                    "window.getSelection && window.getSelection().removeAllRanges();");
            }
            catch
            {
                // The page may be navigating while the find bar closes.
            }
        }
    }

    private void RestartFindTimer()
    {
        _findTimer.Stop();
        _findTimer.Start();
    }

    private async void FindTimer_Tick(object? sender, object e)
    {
        _findTimer.Stop();
        await PerformFindAsync(backwards: false, resetSelection: true);
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FindStatusText.Text = FindBox.Text.Length == 0 ? "Find" : "…";
        RestartFindTimer();
    }

    private async void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await PerformFindAsync(backwards: false, resetSelection: false);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseFindBar();
        }
    }

    private async void FindPreviousButton_Click(object sender, RoutedEventArgs e) =>
        await PerformFindAsync(backwards: true, resetSelection: false);

    private async void FindNextButton_Click(object sender, RoutedEventArgs e) =>
        await PerformFindAsync(backwards: false, resetSelection: false);

    private void CloseFindButton_Click(object sender, RoutedEventArgs e) => CloseFindBar();

    private async Task PerformFindAsync(bool backwards, bool resetSelection)
    {
        BrowserTabState? state = ActiveTab;
        string query = FindBox.Text;

        if (state?.Browser.CoreWebView2 is null || query.Length == 0)
        {
            FindStatusText.Text = query.Length == 0 ? "Find" : "—";
            return;
        }

        try
        {
            if (resetSelection)
            {
                await state.Browser.CoreWebView2.ExecuteScriptAsync(
                    "window.getSelection && window.getSelection().removeAllRanges();");
            }

            string encodedQuery = JsonSerializer.Serialize(query);
            string backwardsValue = backwards ? "true" : "false";
            string result = await state.Browser.CoreWebView2.ExecuteScriptAsync(
                $"window.find({encodedQuery}, false, {backwardsValue}, true, false, true, false);");

            FindStatusText.Text = result.Equals("true", StringComparison.OrdinalIgnoreCase)
                ? "Found"
                : "No match";
        }
        catch
        {
            FindStatusText.Text = "Unavailable";
        }
    }

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
        AppWindow.SetPresenter(_isFullscreen
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Overlapped);

        TitleBarRow.Height = new GridLength(_isFullscreen ? 0 : ConfiguredTabBarHeight);
        ToolbarRow.Height = new GridLength(
            _isFullscreen
                ? 0
                : ConfiguredToolbarHeight);
        BookmarksBarRow.Height = new GridLength(
            _isFullscreen || !_settings.ShowBookmarksBar || _isPrivateWindow ? 0 : 32);
        ProgressRow.Height = new GridLength(_isFullscreen ? 0 : 2);
    }

    private async Task RestoreClosedTabAsync()
    {
        if (_closedTabs.Count == 0)
        {
            return;
        }

        ClosedTabInfo closedTab = _closedTabs.Pop();
        RestoreClosedTabMenuItem.IsEnabled = _closedTabs.Count > 0;

        BrowserTabState restored = await CreateTabAsync(
            closedTab.Uri,
            selectTab: true,
            showStartPage: closedTab.WasStartPage,
            tabGroupId: closedTab.TabGroupId);
        if (closedTab.WasPinned)
        {
            TogglePinnedTab(restored);
        }
        UpdateTabWidths();
        UpdateAllTabVisuals();
    }

    private void GoBack()
    {
        if (ActiveTab?.Browser.CanGoBack == true)
        {
            ActiveTab.Browser.GoBack();
        }
    }

    private void GoForward()
    {
        if (ActiveTab?.Browser.CanGoForward == true)
        {
            ActiveTab.Browser.GoForward();
        }
    }

    private void ReloadOrStop()
    {
        BrowserTabState? state = ActiveTab;
        if (state is null)
        {
            return;
        }

        if (state.IsDiscarded)
        {
            _ = RestoreDiscardedTabAsync(state);
        }
        else if (state.IsLoading)
        {
            state.Browser.CoreWebView2.Stop();
        }
        else if (string.Equals(
            state.InternalPageAddress,
            "aero://settings",
            StringComparison.OrdinalIgnoreCase))
        {
            ShowSettingsPage(state);
        }
        else
        {
            state.Browser.Reload();
        }
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = _settings.ThemeMode switch
        {
            AeroThemeMode.Light => ElementTheme.Light,
            AeroThemeMode.System => ElementTheme.Default,
            _ => ElementTheme.Dark
        };

        UpdateTitleBarColors();
        UpdateAllTabVisuals();
        ApplyProfileSettingsUi();
        UpdateAddressSurface();
    }

    private bool IsDarkTheme()
    {
        return _settings.ThemeMode switch
        {
            AeroThemeMode.Light => false,
            AeroThemeMode.Dark => true,
            _ => RootGrid.ActualTheme != ElementTheme.Light
        };
    }

    private void TitleBarSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTitleBarLayout();
        UpdateTabWidths();
    }

    private void UpdateTitleBarLayout()
    {
        if (TitleBarSurface.XamlRoot is null)
        {
            return;
        }

        double scaleAdjustment = TitleBarSurface.XamlRoot.RasterizationScale;
        if (scaleAdjustment <= 0)
        {
            scaleAdjustment = 1;
        }

        double leftInset = AppWindow.TitleBar.LeftInset / scaleAdjustment;
        double rightInset = AppWindow.TitleBar.RightInset / scaleAdjustment;

        LeftTitleBarInsetColumn.Width = new GridLength(leftInset);
        RightTitleBarInsetColumn.Width = new GridLength(rightInset);

        // Keep a real Chrome-like empty area after the last tab.
        // This area is always draggable and never gets swallowed by the tab scroller.
        double availableWidth = Math.Max(0, TitleBarSurface.ActualWidth - leftInset - rightInset - TitleBarSurface.ColumnDefinitions[1].Width.Value);
        double dragWidth = Math.Min(TitleBarMinimumDragWidth, Math.Max(32, availableWidth * 0.20));
        TitleBarDragRegion.MinWidth = dragWidth;
        TitleBarSurface.ColumnDefinitions[3].MinWidth = dragWidth;
        TabScrollViewer.MaxWidth = Math.Max(0, availableWidth - dragWidth);
        TabDragOverlay.Width = Math.Max(RootGrid.ActualWidth, TitleBarSurface.ActualWidth);
        TabDragOverlay.Height = Math.Max(
            RootGrid.ActualHeight,
            ConfiguredTabBarHeight + 4);

        UpdateTabWidths();
    }

    private void UpdateTitleBarColors()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        bool isDarkTheme = IsDarkTheme();
        AppWindowTitleBar titleBar = AppWindow.TitleBar;

        Color systemBarColor = GetChromeColor("AeroTitleBarBrush");
        Color barColor = systemBarColor;
        if (_settings.TabBarBackgroundStyle != TabBarBackgroundStyle.Default ||
            _settings.TabBarBlur != TabBarBlurStyle.Off)
        {
            double opacity = Math.Clamp(_settings.TabBarBackgroundOpacity, 20, 100) / 100d;
            barColor = BlendColors(
                systemBarColor,
                GetConfiguredTabBarBackgroundColor(),
                opacity);
        }

        double luminance =
            (0.2126 * barColor.R + 0.7152 * barColor.G + 0.0722 * barColor.B) / 255d;
        bool useLightButtons = luminance < 0.56;

        titleBar.BackgroundColor = barColor;
        titleBar.InactiveBackgroundColor = barColor;
        titleBar.ButtonBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);

        if (useLightButtons)
        {
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 241, 243, 244);
            titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 255, 255, 255);
            titleBar.ButtonPressedForegroundColor = Color.FromArgb(255, 255, 255, 255);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 155, 165, 173);
            titleBar.ButtonHoverBackgroundColor = BlendColors(
                barColor,
                Color.FromArgb(255, 255, 255, 255),
                0.13);
            titleBar.ButtonPressedBackgroundColor = BlendColors(
                barColor,
                Color.FromArgb(255, 255, 255, 255),
                0.21);
        }
        else
        {
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 28, 33, 37);
            titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 17, 20, 23);
            titleBar.ButtonPressedForegroundColor = Color.FromArgb(255, 17, 20, 23);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 105, 113, 119);
            titleBar.ButtonHoverBackgroundColor = BlendColors(
                barColor,
                Color.FromArgb(255, 0, 0, 0),
                0.10);
            titleBar.ButtonPressedBackgroundColor = BlendColors(
                barColor,
                Color.FromArgb(255, 0, 0, 0),
                0.18);
        }
    }

    private void RefreshStartPages()
    {
        if (_environment is null)
        {
            return;
        }

        foreach (BrowserTabState state in _tabs)
        {
            if (state.IsStartPage)
            {
                ShowAeroHomePage(state);
            }
        }
    }

    private string BuildNewTabPageHtml()
    {
        bool isDark = IsDarkTheme();
        string quickLinkDisplay = _settings.ShowQuickLinks ? "flex" : "none";
        string quickLinksJson = JsonSerializer.Serialize(
            _dataStore.Library.QuickLinks,
            SettingsJsonOptions);

        Dictionary<string, string> colors = isDark
            ? new Dictionary<string, string>
            {
                ["__COLOR_SCHEME__"] = "dark",
                ["__BACKGROUND__"] = "#0b0e11",
                ["__TEXT__"] = "#f1f3f4",
                ["__MUTED__"] = "#929ca4",
                ["__PLACEHOLDER__"] = "#69737b",
                ["__SURFACE__"] = "rgba(24, 29, 33, .97)",
                ["__BORDER__"] = "#30373d",
                ["__FOCUS_BORDER__"] = "#64717b",
                ["__BUTTON_BG__"] = "#e2e7ea",
                ["__BUTTON_TEXT__"] = "#111519",
                ["__LINK_TEXT__"] = "#dce1e4",
                ["__SHORTCUT_HOVER__"] = "#171c20",
                ["__ICON_BG__"] = "#20262b",
                ["__SHADOW__"] = "rgba(0, 0, 0, .34)",
                ["__FOCUS_SHADOW__"] = "rgba(0, 0, 0, .46)",
                ["__MARK__"] = "#e2e7ea",
                ["__MARK_SHADOW__"] = "rgba(0, 0, 0, .34)"
            }
            : new Dictionary<string, string>
            {
                ["__COLOR_SCHEME__"] = "light",
                ["__BACKGROUND__"] = "#f3f5f6",
                ["__TEXT__"] = "#1c2125",
                ["__MUTED__"] = "#707a82",
                ["__PLACEHOLDER__"] = "#929aa1",
                ["__SURFACE__"] = "rgba(255, 255, 255, .98)",
                ["__BORDER__"] = "#d0d6da",
                ["__FOCUS_BORDER__"] = "#9aa5ad",
                ["__BUTTON_BG__"] = "#22282d",
                ["__BUTTON_TEXT__"] = "#ffffff",
                ["__LINK_TEXT__"] = "#343b41",
                ["__SHORTCUT_HOVER__"] = "#ffffff",
                ["__ICON_BG__"] = "#e5e9ec",
                ["__SHADOW__"] = "rgba(24, 30, 38, .10)",
                ["__FOCUS_SHADOW__"] = "rgba(24, 30, 38, .15)",
                ["__MARK__"] = "#22282d",
                ["__MARK_SHADOW__"] = "rgba(24, 30, 38, .13)"
            };

        colors["__BACKGROUND__"] = GetChromeCssColor("AeroWindowBrush");
        colors["__TEXT__"] = GetChromeCssColor("AeroTextBrush");
        colors["__MUTED__"] = GetChromeCssColor("AeroMutedTextBrush");
        colors["__PLACEHOLDER__"] = GetChromeCssColor("AeroMutedTextBrush");
        colors["__BORDER__"] = GetChromeCssColor("AeroBorderBrush");
        colors["__FOCUS_BORDER__"] = GetChromeCssColor("AeroTabIndicatorBrush");
        colors["__BUTTON_BG__"] = GetChromeCssColor("AeroAccentBrush");
        colors["__BUTTON_TEXT__"] = GetChromeCssColor("AeroOnAccentBrush");
        colors["__LINK_TEXT__"] = GetChromeCssColor("AeroTextBrush");
        colors["__SHORTCUT_HOVER__"] = GetChromeCssColor("AeroHoverBrush");
        colors["__ICON_BG__"] = GetChromeCssColor("AeroRaisedSurfaceBrush");
        colors["__SURFACE__"] = GetChromeCssColor("AeroSurfaceBrush");

        bool hasCustomBackground =
            _settings.HomeBackgroundStyle != HomeBackgroundStyle.Default;
        if (hasCustomBackground)
        {
            colors["__SURFACE__"] = isDark
                ? "rgba(24, 29, 33, .78)"
                : "rgba(255, 255, 255, .80)";
            colors["__SHORTCUT_HOVER__"] = isDark
                ? "rgba(23, 28, 32, .76)"
                : "rgba(255, 255, 255, .76)";
            colors["__ICON_BG__"] = isDark
                ? "rgba(32, 38, 43, .82)"
                : "rgba(229, 233, 236, .82)";
        }

        HomeBackgroundPresentation homeBackground =
            BuildHomeBackgroundPresentation(isDark, colors);
        string homeBackgroundOpacity = (
            Math.Clamp(_settings.HomeBackgroundOpacity, 20, 100) / 100d)
            .ToString("0.##", CultureInfo.InvariantCulture);

        string html = NewTabPageTemplate
            .Replace("__HOME_LANG__", Localization.AeroLocalizer.Language)
            .Replace("__HOME_LABELS_JSON__", BuildHomeLabelsJson())
            .Replace("__HOME_MOTION__", _settings.ReduceMotion ? "reduce-motion" : string.Empty)
            .Replace("__HOME_BACKGROUND_COLOR__", homeBackground.Color)
            .Replace("__HOME_BACKGROUND_IMAGE__", homeBackground.Image)
            .Replace("__HOME_BACKGROUND_SIZE__", homeBackground.Size)
            .Replace("__HOME_BACKGROUND_POSITION__", homeBackground.Position)
            .Replace("__HOME_BACKGROUND_REPEAT__", homeBackground.Repeat)
            .Replace("__HOME_BACKGROUND_OPACITY__", homeBackgroundOpacity)
            .Replace("__HOME_BACKGROUND_BLUR__", Math.Clamp(_settings.HomeBackgroundBlur, 0, 24).ToString(CultureInfo.InvariantCulture))
            .Replace("__HOME_BACKGROUND_SCALE__", _settings.HomeBackgroundBlur > 0 ? "1.06" : "1")
            .Replace("__HOME_BACKGROUND_OVERLAY__", BuildHomeBackgroundOverlay(isDark))
            .Replace("__SHORTCUT_DISPLAY__", quickLinkDisplay)
            .Replace("__QUICK_LINKS_JSON__", quickLinksJson)
            .Replace(
                "__SUBTITLE__",
                _isPrivateWindow
                    ? "Private browsing. Local session data is removed when this window closes."
                    : _browserProfile.IsGuest
                        ? "Guest browsing. This profile is removed when its last window closes."
                        : string.Empty);
        foreach ((string token, string value) in colors)
        {
            html = html.Replace(token, value);
        }

        // Last, so the large font payload is not rescanned by every token replacement above.
        return html.Replace(AeroFontFaceToken, GetAeroFontFaceCss(), StringComparison.Ordinal);
    }

    private readonly record struct HomeBackgroundPresentation(
        string Color,
        string Image,
        string Size,
        string Position,
        string Repeat);

    private HomeBackgroundPresentation BuildHomeBackgroundPresentation(
        bool isDark,
        IReadOnlyDictionary<string, string> colors)
    {
        string defaultColor = colors["__BACKGROUND__"];
        string primary = _settings.HomeBackgroundColor;
        string secondary = _settings.HomeBackgroundSecondaryColor;

        return _settings.HomeBackgroundStyle switch
        {
            HomeBackgroundStyle.Solid => new(
                primary,
                "none",
                "auto",
                "center",
                "no-repeat"),
            HomeBackgroundStyle.Gradient => new(
                primary,
                $"linear-gradient(135deg, {primary} 0%, {secondary} 100%)",
                "cover",
                "center",
                "no-repeat"),
            HomeBackgroundStyle.Mountains => new(
                primary,
                $"url(\"{BuildMountainBackgroundDataUri(primary, secondary, isDark)}\"), linear-gradient(180deg, {secondary} 0%, {primary} 100%)",
                "cover, cover",
                "center bottom, center",
                "no-repeat, no-repeat"),
            HomeBackgroundStyle.Image when
                !string.IsNullOrWhiteSpace(GetHomeBackgroundImageUri()) =>
                BuildImageHomeBackground(primary),
            _ => new(
                defaultColor,
                "none",
                "auto",
                "center",
                "no-repeat")
        };
    }

    private HomeBackgroundPresentation BuildImageHomeBackground(string color)
    {
        string imageUri = GetHomeBackgroundImageUri();
        (string size, string repeat) = _settings.HomeBackgroundImageFit switch
        {
            HomeBackgroundImageFit.Contain => ("contain", "no-repeat"),
            HomeBackgroundImageFit.Center => ("auto", "no-repeat"),
            HomeBackgroundImageFit.Tile => ("auto", "repeat"),
            _ => ("cover", "no-repeat")
        };

        return new HomeBackgroundPresentation(
            color,
            $"url(\"{imageUri}\")",
            size,
            "center",
            repeat);
    }

    private string BuildHomeBackgroundOverlay(bool isDark)
    {
        if (!_settings.HomeBackgroundAdaptiveOverlay ||
            _settings.HomeBackgroundStyle == HomeBackgroundStyle.Default)
        {
            return "transparent";
        }

        return isDark
            ? "linear-gradient(180deg, rgba(4, 7, 10, .12), rgba(4, 7, 10, .42))"
            : "linear-gradient(180deg, rgba(255, 255, 255, .04), rgba(255, 255, 255, .24))";
    }

    private string GetHomeBackgroundImageUri()
    {
        string configuredPath = _settings.HomeBackgroundImagePath;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return string.Empty;
        }

        try
        {
            string customizationFolder = Path.GetFullPath(Path.Combine(
                _browserProfile.StorageRoot,
                "Customization"));
            string fullPath = Path.GetFullPath(configuredPath);
            string requiredPrefix = customizationFolder.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(fullPath))
            {
                return string.Empty;
            }

            FileInfo file = new(fullPath);
            if (file.Length is <= 0 or > 32L * 1024 * 1024)
            {
                return string.Empty;
            }

            string extension = file.Extension.ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp"))
            {
                return string.Empty;
            }

            return $"https://{HomeBackgroundVirtualHost}/{Uri.EscapeDataString(file.Name)}?v={file.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildMountainBackgroundDataUri(
        string primary,
        string secondary,
        bool isDark)
    {
        string sky = secondary;
        string back = isDark ? "#314457" : "#AFC7D8";
        string middle = isDark ? "#223445" : "#7898AD";
        string front = primary;
        string moon = isDark ? "#D8E6EF" : "#FFF5D6";
        string svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1600 900" preserveAspectRatio="xMidYMid slice">
              <defs>
                <linearGradient id="sky" x1="0" y1="0" x2="0" y2="1">
                  <stop offset="0" stop-color="{sky}"/>
                  <stop offset="1" stop-color="{primary}"/>
                </linearGradient>
              </defs>
              <rect width="1600" height="900" fill="url(#sky)"/>
              <circle cx="1280" cy="180" r="74" fill="{moon}" opacity=".64"/>
              <path d="M0 650L190 430L340 575L530 320L730 560L900 380L1130 590L1330 350L1600 620V900H0Z" fill="{back}" opacity=".72"/>
              <path d="M0 720L250 500L455 690L700 430L930 675L1160 470L1390 660L1600 520V900H0Z" fill="{middle}" opacity=".86"/>
              <path d="M0 790L260 650L470 760L730 575L980 755L1230 610L1450 740L1600 680V900H0Z" fill="{front}"/>
            </svg>
            """;
        return "data:image/svg+xml;base64," +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }

    private Brush GetTextBrush() => GetChromeBrush("AeroTextBrush");

    private Brush GetMutedTextBrush() => GetChromeBrush("AeroMutedTextBrush");

    private Brush GetSecondaryTextBrush() => GetChromeBrush("AeroSecondaryTextBrush");

    private Brush GetSoftBorderBrush() => GetChromeBrush("AeroSoftBorderBrush");

    private static Brush GetTransparentBrush() => new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "Close"
        };

        await dialog.ShowAsync();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _startupTransfer?.Completion.TrySetResult(null);
        _sleepTimer.Stop();
        _findTimer.Stop();
        _sessionSaveTimer.Stop();
        _downloadToastTimer.Stop();
        _zoomToastTimer.Stop();
        foreach (BrowserDownloadState download in _downloads)
        {
            download.DetachEvents();
        }
        if (_downloadRequestCoordinator.IsValueCreated)
        {
            _downloadRequestCoordinator.Value.Clear();
        }
        _shieldCancellation.Cancel();
        _requestBlocker.FiltersChanged -= RequestBlocker_FiltersChanged;
        _uBlockOrigin.StateChanged -= UBlockOrigin_StateChanged;
        PersistSessionSnapshot();
        ClearConfiguredDataOnClose();
        _dataStore.Dispose();
        PreparePrivateProfileCleanup();
        CleanupMemorySaverState();
        CleanupMediaHub();
        CleanupFrameRateSynchronization();
        DisposeAddressSuggestionService();
        _passwordVaultService.Dispose();
        _passwordBrokerClient.Dispose();
        OpenWindows.Remove(this);
        CleanupGuestProfileAfterClose();
        CleanupDeletedProfileAfterClose();
    }

    private static string TrimTabTitle(string title)
    {
        const int maximumLength = 44;
        return title.Length <= maximumLength
            ? title
            : title[..(maximumLength - 1)] + "…";
    }

    private enum LibraryView
    {
        History,
        Bookmarks
    }

    private readonly record struct LibraryItemTag(string Url, bool IsBookmark);

    private readonly record struct ClosedTabInfo(
        Uri? Uri,
        bool WasStartPage,
        bool WasPinned,
        string? TabGroupId);

    private sealed class BrowserTabState
    {
        public BrowserTabState(WebView2 browser)
        {
            Browser = browser;
        }

        public WebView2 Browser { get; set; }

        public Image SnapshotView { get; set; } = null!;

        public Border Header { get; set; } = null!;

        public CompositeTransform HeaderTransform { get; set; } = null!;

        public Grid ContentGrid { get; set; } = null!;

        public ColumnDefinition IconColumn { get; set; } = null!;

        public ColumnDefinition CloseColumn { get; set; } = null!;

        public Grid IconHost { get; set; } = null!;

        public FontIcon Icon { get; set; } = null!;

        public Image FaviconImage { get; set; } = null!;

        public Button AudioButton { get; set; } = null!;

        public FontIcon AudioIcon { get; set; } = null!;

        public bool FaviconFallbackAttempted { get; set; }

        public string? LastFaviconUri { get; set; }

        public RotateTransform IconTransform { get; set; } = null!;

        public Storyboard? LoadingStoryboard { get; set; }

        public TextBlock TitleText { get; set; } = null!;

        public Button CloseButton { get; set; } = null!;

        public Border ActiveIndicator { get; set; } = null!;

        public ScaleTransform ActiveIndicatorTransform { get; set; } = null!;

        public Border Separator { get; set; } = null!;

        public Border ContainerStripe { get; set; } = null!;

        public int ResourceBudgetStrikes { get; set; }

        public string? ContainerId { get; set; }

        public string? BurnerProfileName { get; set; }

        public Uri? CurrentUri { get; set; }

        public Uri? ResourceDocumentUri { get; set; }

        public string? ResourceDocumentHost { get; set; }

        public bool ResourceDocumentIsGoogleSessionCritical { get; set; }

        public bool ResourceDocumentIsCompatibilitySensitive { get; set; }

        public bool ResourceDocumentShieldAllowed { get; set; }

        public bool ResourceNativeShieldEnabled { get; set; }

        public bool ResourceUseShieldFastLane { get; set; }

        public string? InternalPageAddress { get; set; }

        public Uri? PendingUri { get; set; }

        public string AddressDraft { get; set; } = string.Empty;

        public bool HasAddressDraft { get; set; }

        public Uri? LastNavigationUri { get; set; }

        public Uri? FailedNavigationUri { get; set; }

        public Uri? TransientRetryUri { get; set; }

        public DateTimeOffset? DownloadNavigationSuppressionUntil { get; set; }

        public Uri? DownloadNavigationUri { get; set; }

        public Uri? DownloadReturnUri { get; set; }

        public NavigationLifecycleState Navigation { get; } = new();

        // Temporary compatibility accessors for security/history code. The
        // mutable navigation state itself now lives in Aero.Navigation.
        public ulong ActiveNavigationId => Navigation.ActiveNavigationId;

        public Guid SecurityDocumentIdentity => Navigation.DocumentIdentity;

        public ulong LastHistoryNavigationId { get; set; }

        public ulong LastDomContentLoadedNavigationId =>
            Navigation.LastDomContentLoadedNavigationId;

        public ulong BrowserVerificationNavigationId =>
            Navigation.BrowserVerificationNavigationId;

        public Uri? LastBrowserVerificationUri =>
            Navigation.LastBrowserVerificationUri;

        public int TransientRetryCount { get; set; }

        public bool IsShowingNetworkErrorPage { get; set; }

        public string Title { get; set; } = "New tab";

        public bool IsLoading { get; set; }

        public bool IsStartPage { get; set; }

        public bool IsPopup { get; set; }

        public bool PendingStartPage { get; set; }

        public bool IsPointerOver { get; set; }

        public bool IsPointerPressed { get; set; }

        public bool IsVisualActive { get; set; }

        public bool IsClosing { get; set; }

        public bool IsPinned { get; set; }

        public string? TabGroupId { get; set; }

        public bool IsPlayingAudio { get; set; }

        public int ActiveDownloadCount;

        public double PageZoomFactor { get; set; } = 1.0;

        public bool HasMedia { get; set; }

        public bool MediaIsPlaying { get; set; }

        public bool MediaIsMuted { get; set; }

        public string MediaTitle { get; set; } = string.Empty;

        public string MediaArtist { get; set; } = string.Empty;

        public string MediaArtwork { get; set; } = string.Empty;

        public string? MediaBridgeScriptId { get; set; }

        public CoreWebView2SharedBuffer? PasswordCaptureBuffer { get; set; }

        public Guid PasswordCaptureBufferId { get; set; }

        public Guid PasswordCaptureDocumentIdentity { get; set; }

        public string? PasswordCaptureOrigin { get; set; }

        public DateTimeOffset? LastMediaUpdate { get; set; }

        public bool HasCrashed { get; set; }

        public bool SuspendInProgress { get; set; }

        public bool DiscardInProgress { get; set; }

        public bool RestoreInProgress { get; set; }

        public bool IsDiscarded { get; set; }

        public bool IsSuspendedByMemorySaver { get; set; }

        public bool LowMemoryTargetApplied { get; set; }

        public bool WebResourceFilterAttached { get; set; }

        public WebViewEventBinder WebViewEvents { get; } = new();

        public string? SnapshotPath { get; set; }

        public double RestoreScrollX { get; set; }

        public double RestoreScrollY { get; set; }

        public bool HasRestoreScrollPosition { get; set; }

        public bool HasUnsavedFormData { get; set; }

        public bool DevToolsOpenedForCurrentDocument { get; set; }

        public DateTimeOffset? DiscardedAt { get; set; }

        public bool NativeCosmeticArtifactsApplied { get; set; }

        public bool YouTubeCleanArtifactsApplied { get; set; }

        public int BlockedRequestCount { get; set; }

        public int NativeBlockedRequestCount { get; set; }

        public int ThirdPartyCookieBlockCount { get; set; }

        public DateTimeOffset LastActivatedAt { get; set; } = DateTimeOffset.UtcNow;

        public DateTimeOffset? LastBackgroundedAt { get; set; } = DateTimeOffset.UtcNow;

        public DateTimeOffset? ExternalAuthenticationStartedAt { get; set; }

        public bool ExternalAuthenticationCallbackInProgress { get; set; }

        public bool ExternalAuthenticationCompleted { get; set; }
    }
}
