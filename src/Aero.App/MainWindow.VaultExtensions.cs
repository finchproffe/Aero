using Aero.App.Data;
using Aero.Extensions;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using System.Text.Json.Nodes;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace Aero.App;

public sealed partial class MainWindow
{
    private readonly ExtensionRegistry _extensionRegistry;
    private IReadOnlyList<CoreWebView2BrowserExtension> _installedExtensions = [];

    private bool _passwordsPaneOpen;
    private bool _extensionsPaneOpen;
    private bool _updatingPasswordUi;
    private bool _updatingExtensionUi;
    private bool _extensionToolbarInitialized;
    private bool _extensionRefreshRunning;
    private bool _extensionImportRunning;
    private CoreWebView2Profile? _extensionRefreshQueryProfile;
    private Task<IReadOnlyList<CoreWebView2BrowserExtension>>? _extensionRefreshQuery;
    private ExtensionPopupWindow? _extensionPopupWindow;
    private Task? _legacyWebView2PasswordCleanupTask;

    private bool IsBuiltInShieldExtension(CoreWebView2BrowserExtension extension)
    {
        if (_uBlockOrigin.Extension is CoreWebView2BrowserExtension shieldExtension &&
            extension.Id.Equals(shieldExtension.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return extension.Name.Equals("uBlock Origin", StringComparison.OrdinalIgnoreCase) ||
            extension.Name.Equals("uBlock Origin Lite", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExtensionHostAccess(ExtensionPackage? package) =>
        package?.Permissions.Any(permission =>
            permission.Equals("<all_urls>", StringComparison.OrdinalIgnoreCase) ||
            permission.Contains("://", StringComparison.Ordinal)) == true;

    private static bool HasUserScriptsPermission(ExtensionPackage? package) =>
        package?.Permissions.Any(permission =>
            permission.Equals("userScripts", StringComparison.OrdinalIgnoreCase)) == true;

    private void ApplyExtensionPreferenceOverridesBeforeWebViewStartup(string userDataFolder)
    {
        try
        {
            string[] candidates =
            [
                Path.Combine(userDataFolder, "EBWebView", "Default", "Preferences"),
                Path.Combine(userDataFolder, "Default", "Preferences"),
                Path.Combine(userDataFolder, "Preferences")
            ];
            string? preferencesPath = candidates.FirstOrDefault(File.Exists);
            if (preferencesPath is null)
            {
                return;
            }

            JsonNode? root = JsonNode.Parse(File.ReadAllText(preferencesPath));
            JsonObject? extensionSettings = root?["extensions"]?["settings"] as JsonObject;
            if (root is null || extensionSettings is null)
            {
                return;
            }

            bool changed = false;
            foreach ((string extensionId, bool enabled) in _extensionRegistry.UserScriptsEnabled)
            {
                if (extensionSettings[extensionId] is not JsonObject extensionPrefs)
                {
                    continue;
                }

                extensionPrefs["user_scripts_enabled"] = enabled;
                changed = true;
            }

            foreach ((string extensionId, bool enabled) in _extensionRegistry.AllSitesAccessEnabled)
            {
                if (extensionSettings[extensionId] is not JsonObject extensionPrefs)
                {
                    continue;
                }

                extensionPrefs["extension_can_script_all_urls"] = enabled;
                extensionPrefs["has_set_script_all_urls"] = true;
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            string temporaryPath = preferencesPath + ".aero.tmp";
            File.WriteAllText(temporaryPath, root.ToJsonString());
            File.Move(temporaryPath, preferencesPath, overwrite: true);
        }
        catch
        {
            // Chromium preferences are best-effort. Aero still starts if a runtime
            // changes its profile layout or the preference file is temporarily locked.
        }
    }

    private void InitializeVaultExtensionsFeatures()
    {
        PasswordsStatusText.Text = "Aero Vault is initializing.";
        ExtensionsStatusText.Text = "Chromium extension support is initializing.";
        CleanupPendingExtensionDownloads();
    }

    private void InitializeExtensionToolbarForProfile(CoreWebView2Profile profile)
    {
        if (_extensionToolbarInitialized)
        {
            return;
        }

        _extensionToolbarInitialized = true;
        _ = InitializeExtensionFeaturesAsync(profile);
    }

    private async Task InitializeExtensionFeaturesAsync(CoreWebView2Profile profile)
    {
        _legacyWebView2PasswordCleanupTask ??=
            ClearLegacyWebView2PasswordStoreOnceAsync(profile);
        await _legacyWebView2PasswordCleanupTask;
        await RefreshExtensionToolbarAsync();
        EnsureUBlockOriginForProfile(profile);
    }

    private async Task ClearLegacyWebView2PasswordStoreOnceAsync(CoreWebView2Profile profile)
    {
        if (_isPrivateWindow || _browserProfile.IsGuest)
        {
            return;
        }

        string securityDirectory = Path.Combine(_browserProfile.StorageRoot, "Security");
        string markerPath = Path.Combine(
            securityDirectory,
            ".webview-password-autosave-cleared-v1");
        if (File.Exists(markerPath))
        {
            return;
        }

        try
        {
            await profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.PasswordAutosave);
            Directory.CreateDirectory(securityDirectory);
            await File.WriteAllTextAsync(
                markerPath,
                DateTimeOffset.UtcNow.ToString("O"));
            _passwordVaultService.RecordSecurityEvent(
                "legacy-password-store",
                "cleared",
                origin: null);
        }
        catch
        {
            _passwordVaultService.RecordSecurityEvent(
                "legacy-password-store",
                "cleanup-failed",
                origin: null);
        }
    }

    private void ApplyPasswordAndAutofillSettings(CoreWebView2 coreWebView)
    {
        try
        {
            // Aero uses its own Windows Hello CNG-protected vault. Keep WebView2 password
            // persistence disabled so credentials are not duplicated in a second store.
            coreWebView.Settings.IsPasswordAutosaveEnabled = false;
            coreWebView.Settings.IsGeneralAutofillEnabled =
                !_isPrivateWindow && _settings.AutofillForms;
        }
        catch
        {
            // Older WebView2 runtimes can ignore these settings until updated.
        }
    }

    private void ApplyPasswordAndAutofillSettingsToAllTabs()
    {
        foreach (BrowserTabState tab in _tabs)
        {
            if (tab.Browser.CoreWebView2 is CoreWebView2 coreWebView)
            {
                ApplyPasswordAndAutofillSettings(coreWebView);
            }
        }

        RefreshPasswordsPaneState();
    }

    private async Task<string?> GetVaultExtensionIsolationBlockReasonAsync(
        BrowserTabState state)
    {
        CoreWebView2Profile? profile = state.Browser.CoreWebView2?.Profile;
        if (profile is null)
        {
            return "Aero could not verify the extension environment for this tab.";
        }

        try
        {
            IReadOnlyList<CoreWebView2BrowserExtension> extensions = await
                profile.GetBrowserExtensionsAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(4));
            string? trustedShieldId = _uBlockOrigin.Extension?.Id;
            List<CoreWebView2BrowserExtension> untrusted = extensions
                .Where(extension =>
                    extension.IsEnabled &&
                    (trustedShieldId is null ||
                     !extension.Id.Equals(trustedShieldId, StringComparison.OrdinalIgnoreCase)))
                .Take(4)
                .ToList();

            if (untrusted.Count == 0)
            {
                return null;
            }

            string names = string.Join(", ", untrusted.Select(extension => extension.Name));
            return
                $"Protected filling is blocked while page-reading extensions are enabled: {names}. " +
                "Disable them and reload the page before using Aero Vault.";
        }
        catch (Exception exception)
        {
            _passwordVaultService.RecordSecurityEvent(
                "extension-isolation",
                "verification-failed",
                origin: null);
            return
                "Aero could not verify the extension environment. Protected filling was blocked " +
                $"({exception.GetType().Name}).";
        }
    }

    private CoreWebView2Profile? GetActiveProfile() =>
        ActiveTab?.Browser.CoreWebView2?.Profile ??
        _tabs.Select(tab => tab.Browser.CoreWebView2?.Profile).FirstOrDefault(profile => profile is not null);

    private void PasswordsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        OpenPasswordsPane();
    }

    private void OpenPasswordsFromSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        CloseSettingsPane();
        OpenPasswordsPane();
    }

    private void OpenPasswordsPane()
    {
        CloseDownloadsPane();
        CloseLibraryPane();
        CloseSettingsPane();
        CloseCommandPalette();
        CloseExtensionsPane();
        CloseWorkspacesPane();
        CloseFindBar();

        if (_passwordsPaneOpen)
        {
            RefreshPasswordsPaneState();
            return;
        }

        _passwordsPaneOpen = true;
        RefreshPasswordsPaneState();
        PasswordsPane.Visibility = Visibility.Visible;
        PasswordsPane.Opacity = 0;
        PasswordsPaneTransform.TranslateX = 28;
        AnimateOpacity(PasswordsPane, 0, 1, StandardAnimationDuration);
        AnimateDouble(PasswordsPaneTransform, "TranslateX", 28, 0, StandardAnimationDuration);
    }

    private void ClosePasswordsPane()
    {
        if (!_passwordsPaneOpen)
        {
            return;
        }

        _passwordsPaneOpen = false;
        AnimateOpacity(PasswordsPane, PasswordsPane.Opacity, 0, FastAnimationDuration, () =>
        {
            if (!_passwordsPaneOpen)
            {
                PasswordsPane.Visibility = Visibility.Collapsed;
            }
        });
        AnimateDouble(PasswordsPaneTransform, "TranslateX", PasswordsPaneTransform.TranslateX, 24, FastAnimationDuration);
    }

    private void ClosePasswordsButton_Click(object sender, RoutedEventArgs e) => ClosePasswordsPane();

    private void RefreshPasswordsPaneState()
    {
        _updatingPasswordUi = true;
        PasswordsSaveToggle.IsOn = _settings.SavePasswords;
        PasswordsAutofillToggle.IsOn = _settings.AutofillForms;
        SavePasswordsToggle.IsOn = _settings.SavePasswords;
        AutofillFormsToggle.IsOn = _settings.AutofillForms;
        _updatingPasswordUi = false;

        bool runtimeReady = GetActiveProfile() is not null;
        PasswordsStatusText.Text = _isPrivateWindow
            ? "Private windows can fill existing Aero Vault entries after Windows Hello, but cannot save new passwords."
            : _settings.SavePasswords
                ? "Aero Vault 2.2 is enabled. Windows Hello authorizes one protected operation; decrypted key material is destroyed immediately afterward."
                : "Aero Vault is disabled. Existing encrypted entries remain on disk until you clear them.";

        ClearAutofillDataButton.IsEnabled = runtimeReady;
        _ = RefreshPasswordVaultCountAsync();
        RefreshPasswordVaultForActiveTab();
    }

    private void PasswordFeatureToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingPasswordUi)
        {
            return;
        }

        _settings.SavePasswords = PasswordsSaveToggle.IsOn;
        _settings.AutofillForms = PasswordsAutofillToggle.IsOn;
        _settings.Save();
        ApplyPasswordAndAutofillSettingsToAllTabs();
    }

    private async void ClearSavedPasswordsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPrivateWindow)
        {
            return;
        }

        ContentDialog confirm = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Clear Aero Vault?",
            Content = "This permanently removes every password encrypted in Aero's local vault.bin file. This cannot be undone.",
            PrimaryButtonText = "Clear vault",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await ClearPasswordVaultAsync();
            PasswordsStatusText.Text = "Aero Vault was cleared.";
        }
        catch (Exception exception)
        {
            await ShowErrorDialogAsync("Could not clear Aero Vault", exception.Message);
        }
    }

    private async void ClearAutofillDataButton_Click(object sender, RoutedEventArgs e)
    {
        CoreWebView2Profile? profile = GetActiveProfile();
        if (profile is null)
        {
            return;
        }

        try
        {
            await profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.GeneralAutofill |
                CoreWebView2BrowsingDataKinds.PasswordAutosave);
            PasswordsStatusText.Text =
                "Saved form autofill data and any legacy WebView2 passwords were cleared.";
        }
        catch (Exception exception)
        {
            await ShowErrorDialogAsync("Could not clear autofill data", exception.Message);
        }
    }

    private void ExtensionsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        OpenExtensionsPane();
    }

    private void ExtensionsAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenExtensionsPane();
    }

    private void OpenExtensionsPane() => _ = OpenExtensionsPaneAsync();

    private async Task OpenExtensionsPaneAsync()
    {
        CloseDownloadsPane();
        CloseLibraryPane();
        CloseSettingsPane();
        CloseCommandPalette();
        ClosePasswordsPane();
        CloseWorkspacesPane();
        CloseFindBar();

        if (!_extensionsPaneOpen)
        {
            _extensionsPaneOpen = true;
            ExtensionsPane.Visibility = Visibility.Visible;
            ExtensionsPane.Opacity = 0;
            ExtensionsPaneTransform.TranslateX = 28;
            AnimateOpacity(ExtensionsPane, 0, 1, StandardAnimationDuration);
            AnimateDouble(ExtensionsPaneTransform, "TranslateX", 28, 0, StandardAnimationDuration);
        }

        await RefreshExtensionsPaneAsync();
    }

    private void CloseExtensionsPane()
    {
        if (!_extensionsPaneOpen)
        {
            return;
        }

        _extensionsPaneOpen = false;
        AnimateOpacity(ExtensionsPane, ExtensionsPane.Opacity, 0, FastAnimationDuration, () =>
        {
            if (!_extensionsPaneOpen)
            {
                ExtensionsPane.Visibility = Visibility.Collapsed;
            }
        });
        AnimateDouble(ExtensionsPaneTransform, "TranslateX", ExtensionsPaneTransform.TranslateX, 24, FastAnimationDuration);
    }

    private void CloseExtensionsButton_Click(object sender, RoutedEventArgs e) => CloseExtensionsPane();

    private async void ExtensionsToolbarFlyout_Opened(object sender, object e) =>
        await RefreshExtensionToolbarAsync();

    private void ManageExtensionsToolbarButton_Click(object sender, RoutedEventArgs e)
    {
        ExtensionsToolbarFlyout.Hide();
        OpenExtensionsPane();
    }

    private async void ExtensionsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        await Task.Yield();
        RenderExtensionsPaneItems();
    }

    private async Task RefreshExtensionsPaneAsync()
    {
        await RefreshInstalledExtensionsAsync();
        RenderExtensionsPaneItems();
        await RefreshExtensionToolbarAsync(refreshProfile: false);
    }

    private async Task<bool> RefreshInstalledExtensionsAsync()
    {
        CoreWebView2Profile? profile = GetActiveProfile();
        if (profile is null)
        {
            _installedExtensions = [];
            ExtensionsStatusText.Text = "Open any tab to initialize the extension profile.";
            return false;
        }

        if (_extensionRefreshRunning)
        {
            return false;
        }

        _extensionRefreshRunning = true;
        UpdateExtensionActionButtons();
        ExtensionsStatusText.Text = "Checking installed extensions…";
        try
        {
            if (_extensionRefreshQuery is null ||
                !ReferenceEquals(_extensionRefreshQueryProfile, profile))
            {
                _extensionRefreshQueryProfile = profile;
                _extensionRefreshQuery = profile.GetBrowserExtensionsAsync().AsTask();
            }

            Task<IReadOnlyList<CoreWebView2BrowserExtension>> query =
                _extensionRefreshQuery!;
            Task completed = await Task.WhenAny(
                query,
                Task.Delay(TimeSpan.FromSeconds(12)));
            if (!ReferenceEquals(completed, query))
            {
                ExtensionsStatusText.Text =
                    "The extension service did not respond. Click Refresh to try again.";
                ExtensionsEmptyDescription.Text =
                    "Aero stopped waiting after 12 seconds so the interface stays responsive.";
                return false;
            }

            _installedExtensions = await query;
            _extensionRefreshQuery = null;
            _extensionRefreshQueryProfile = null;
            int visibleCount = _installedExtensions.Count(extension => !IsBuiltInShieldExtension(extension));
            ExtensionsStatusText.Text = visibleCount == 0
                ? "No extensions installed"
                : $"{visibleCount} installed";
            return true;
        }
        catch (Exception exception)
        {
            _extensionRefreshQuery = null;
            _extensionRefreshQueryProfile = null;
            ExtensionsStatusText.Text = "Extension API is unavailable in the installed WebView2 Runtime.";
            ExtensionsEmptyDescription.Text = exception.Message;
            return false;
        }
        finally
        {
            _extensionRefreshRunning = false;
            UpdateExtensionActionButtons();
        }
    }

    private void UpdateExtensionActionButtons()
    {
        RefreshExtensionsButton.IsEnabled = !_extensionRefreshRunning;
        ImportChromeExtensionsButton.IsEnabled =
            !_extensionRefreshRunning && !_extensionImportRunning;
    }

    private async void RefreshExtensionsButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshExtensionsPaneAsync();

    private void RenderExtensionsPaneItems()
    {
        ExtensionsItemsPanel.Children.Clear();

        string filter = ExtensionsSearchBox.Text.Trim();
        IReadOnlyList<CoreWebView2BrowserExtension> visibleExtensions = _installedExtensions
            .Where(extension => !IsBuiltInShieldExtension(extension))
            .Where(extension => ExtensionMatchesFilter(extension, filter))
            .OrderByDescending(extension => _extensionRegistry.IsPinned(extension.Id))
            .ThenBy(extension => extension.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (CoreWebView2BrowserExtension extension in visibleExtensions)
        {
            ExtensionsItemsPanel.Children.Add(BuildExtensionItem(extension));
        }

        ExtensionsEmptyState.Visibility = visibleExtensions.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        int installedUserExtensionCount = _installedExtensions.Count(
            extension => !IsBuiltInShieldExtension(extension));
        if (installedUserExtensionCount > 0 && visibleExtensions.Count == 0)
        {
            ExtensionsEmptyDescription.Text = "No installed extensions match this search.";
        }
        else if (installedUserExtensionCount == 0)
        {
            ExtensionsEmptyDescription.Text = "Install from Web Store, import from Chrome, or load an unpacked Chromium extension folder.";
        }
    }

    private bool ExtensionMatchesFilter(CoreWebView2BrowserExtension extension, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        ExtensionPackage? package = GetManagedExtensionPackage(extension);
        return extension.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
               extension.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               (package?.Description.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    private Border BuildExtensionItem(CoreWebView2BrowserExtension extension)
    {
        ExtensionPackage? package = GetManagedExtensionPackage(extension);

        Grid root = new() { RowSpacing = 10 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid header = new() { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Border badge = BuildExtensionBadge(extension, 34, 12);
        header.Children.Add(badge);

        StackPanel text = new() { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = extension.Name,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = package is null
                ? $"ID: {extension.Id}"
                : $"Version {package.Version}  ·  {extension.Id}",
            FontSize = 10,
            Foreground = GetMutedTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrWhiteSpace(package?.Description))
        {
            text.Children.Add(new TextBlock
            {
                Text = package.Description,
                MaxLines = 2,
                FontSize = 11,
                Foreground = GetSecondaryTextBrush(),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap
            });
        }
        Grid.SetColumn(text, 1);
        header.Children.Add(text);

        ToggleSwitch enabledToggle = new()
        {
            IsOn = extension.IsEnabled,
            OnContent = "On",
            OffContent = "Off",
            Tag = extension,
            VerticalAlignment = VerticalAlignment.Top
        };
        enabledToggle.Toggled += ExtensionEnabledToggle_Toggled;
        Grid.SetColumn(enabledToggle, 2);
        header.Children.Add(enabledToggle);
        root.Children.Add(header);

        Grid actions = new() { ColumnSpacing = 7 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        actions.Children.Add(new TextBlock
        {
            Text = _extensionRegistry.GetPath(extension.Id) is not null ? "Managed by Aero" : "Profile extension",
            FontSize = 11,
            Foreground = GetMutedTextBrush(),
            VerticalAlignment = VerticalAlignment.Center
        });

        Button open = new()
        {
            Content = package?.ActionPage is not null ? "Open" : "Options",
            Tag = extension,
            Height = 30,
            Padding = new Thickness(11, 0, 11, 0),
            IsEnabled = extension.IsEnabled && (package?.ActionPage is not null || package?.OptionsPage is not null)
        };
        open.Click += OpenExtensionActionButton_Click;
        Grid.SetColumn(open, 1);
        actions.Children.Add(open);

        Button pin = new()
        {
            Content = _extensionRegistry.IsPinned(extension.Id) ? "Unpin" : "Pin",
            Tag = extension,
            Height = 30,
            Padding = new Thickness(11, 0, 11, 0)
        };
        pin.Click += ExtensionPinButton_Click;
        Grid.SetColumn(pin, 2);
        actions.Children.Add(pin);

        Button remove = new()
        {
            Content = "Remove",
            Tag = extension,
            Height = 30,
            Padding = new Thickness(11, 0, 11, 0)
        };
        remove.Click += RemoveExtensionButton_Click;
        Grid.SetColumn(remove, 3);
        actions.Children.Add(remove);

        Grid.SetRow(actions, 1);
        root.Children.Add(actions);

        StackPanel permissions = new()
        {
            Spacing = 7,
            Margin = new Thickness(0, 3, 0, 0)
        };
        string[] requestedPermissions = package?.Permissions
            .Where(permission => !string.IsNullOrWhiteSpace(permission))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        permissions.Children.Add(new TextBlock
        {
            Text = requestedPermissions.Length == 0
                ? "Permissions: none declared"
                : "Permissions: " + string.Join(", ", requestedPermissions.Take(8)) +
                    (requestedPermissions.Length > 8 ? " …" : string.Empty),
            FontSize = 10,
            Foreground = GetMutedTextBrush(),
            TextWrapping = TextWrapping.Wrap
        });

        if (HasExtensionHostAccess(package))
        {
            ToggleSwitch siteAccessToggle = new()
            {
                Header = "Allow access to requested sites",
                IsOn = _extensionRegistry.IsAllSitesAccessEnabled(extension.Id),
                OnContent = "Allowed",
                OffContent = "On click / withheld",
                Tag = extension
            };
            siteAccessToggle.Toggled += ExtensionAllSitesAccessToggle_Toggled;
            permissions.Children.Add(siteAccessToggle);
        }

        if (HasUserScriptsPermission(package))
        {
            ToggleSwitch userScriptsToggle = new()
            {
                Header = "Allow user scripts",
                IsOn = _extensionRegistry.IsUserScriptsEnabled(extension.Id),
                OnContent = "Allowed",
                OffContent = "Blocked",
                Tag = extension
            };
            userScriptsToggle.Toggled += ExtensionUserScriptsToggle_Toggled;
            permissions.Children.Add(userScriptsToggle);
        }

        if (HasExtensionHostAccess(package) || HasUserScriptsPermission(package))
        {
            permissions.Children.Add(new TextBlock
            {
                Text = "Permission changes apply after restarting Aero.",
                FontSize = 10,
                Foreground = GetMutedTextBrush()
            });
        }

        Grid.SetRow(permissions, 2);
        root.Children.Add(permissions);

        return new Border
        {
            Padding = new Thickness(12),
            Background = new SolidColorBrush(IsDarkTheme()
                ? Color.FromArgb(255, 29, 35, 40)
                : Color.FromArgb(255, 247, 249, 250)),
            BorderBrush = GetSoftBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Child = root
        };
    }

    private async Task RefreshExtensionToolbarAsync(bool refreshProfile = true)
    {
        if (refreshProfile)
        {
            await RefreshInstalledExtensionsAsync();
        }

        PinnedExtensionsPanel.Children.Clear();
        ExtensionsToolbarItemsPanel.Children.Clear();

        IReadOnlyList<CoreWebView2BrowserExtension> ordered = _installedExtensions
            .Where(extension => !IsBuiltInShieldExtension(extension))
            .OrderByDescending(extension => _extensionRegistry.IsPinned(extension.Id))
            .ThenBy(extension => extension.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (CoreWebView2BrowserExtension extension in ordered)
        {
            ExtensionsToolbarItemsPanel.Children.Add(BuildExtensionToolbarRow(extension));

            if (_extensionRegistry.IsPinned(extension.Id) && extension.IsEnabled)
            {
                PinnedExtensionsPanel.Children.Add(BuildPinnedExtensionButton(extension));
            }
        }

        ExtensionsToolbarStatusText.Text = ordered.Count == 0
            ? "No extensions installed"
            : $"{ordered.Count} installed";
        ExtensionsToolbarEmptyText.Visibility = ordered.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ExtensionsToolbarButton.Visibility = _narrowNavigation
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private Border BuildExtensionToolbarRow(CoreWebView2BrowserExtension extension)
    {
        Grid row = new() { Height = 42, ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(BuildExtensionBadge(extension, 28, 10));

        StackPanel text = new() { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = extension.Name,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = extension.IsEnabled ? "Enabled" : "Disabled",
            FontSize = 10,
            Foreground = GetMutedTextBrush()
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        Button pin = new()
        {
            Width = 28,
            Height = 28,
            Style = (Style)Application.Current.Resources["ChromeIconButtonStyle"],
            Content = new FontIcon
            {
                Glyph = _extensionRegistry.IsPinned(extension.Id) ? "\uE840" : "\uE718",
                FontSize = 11
            },
            Tag = extension
        };
        ToolTipService.SetToolTip(pin, _extensionRegistry.IsPinned(extension.Id) ? "Unpin" : "Pin to toolbar");
        pin.Click += ExtensionPinButton_Click;
        Grid.SetColumn(pin, 2);
        row.Children.Add(pin);

        Button open = new()
        {
            Width = 28,
            Height = 28,
            Style = (Style)Application.Current.Resources["ChromeIconButtonStyle"],
            Content = new FontIcon { Glyph = "\uE72A", FontSize = 11 },
            Tag = extension,
            IsEnabled = extension.IsEnabled
        };
        ToolTipService.SetToolTip(open, "Open extension");
        open.Click += OpenExtensionActionButton_Click;
        Grid.SetColumn(open, 3);
        row.Children.Add(open);

        return new Border
        {
            Padding = new Thickness(6, 0, 4, 0),
            CornerRadius = new CornerRadius(8),
            Background = GetTransparentBrush(),
            Child = row
        };
    }

    private Button BuildPinnedExtensionButton(CoreWebView2BrowserExtension extension)
    {
        Button button = new()
        {
            Width = 30,
            Height = 30,
            Style = (Style)Application.Current.Resources["ChromeIconButtonStyle"],
            Content = BuildExtensionIcon(extension, 19) ?? BuildExtensionInitial(extension, 11),
            Tag = extension
        };
        button.Click += OpenExtensionActionButton_Click;
        ToolTipService.SetToolTip(button, extension.Name);
        return button;
    }

    private Border BuildExtensionBadge(
        CoreWebView2BrowserExtension extension,
        double size,
        double fontSize)
    {
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(Math.Max(7, size / 3.2)),
            Background = new SolidColorBrush(IsDarkTheme()
                ? Color.FromArgb(255, 48, 57, 64)
                : Color.FromArgb(255, 225, 231, 235)),
            Child = BuildExtensionIcon(extension, Math.Max(16, size - 8)) ??
                BuildExtensionInitial(extension, fontSize)
        };
    }

    private FrameworkElement? BuildExtensionIcon(
        CoreWebView2BrowserExtension extension,
        double size)
    {
        string? iconPath = GetManagedExtensionPackage(extension)?.IconPath;
        if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
        {
            return null;
        }

        try
        {
            Uri fileUri = new UriBuilder
            {
                Scheme = Uri.UriSchemeFile,
                Path = iconPath
            }.Uri;
            return new Image
            {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false,
                Source = new BitmapImage(fileUri)
            };
        }
        catch
        {
            return null;
        }
    }

    private static TextBlock BuildExtensionInitial(
        CoreWebView2BrowserExtension extension,
        double fontSize) =>
        new()
        {
            Text = GetExtensionInitial(extension.Name),
            FontSize = fontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

    private static string GetExtensionInitial(string name)
    {
        char first = name.FirstOrDefault(character => char.IsLetterOrDigit(character));
        return first == default ? "E" : char.ToUpperInvariant(first).ToString();
    }

    private void ExtensionAllSitesAccessToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingExtensionUi ||
            sender is not ToggleSwitch { Tag: CoreWebView2BrowserExtension extension } toggle)
        {
            return;
        }

        _extensionRegistry.SetAllSitesAccessEnabled(extension.Id, toggle.IsOn);
        ExtensionsStatusText.Text = $"{extension.Name} site access saved. Restart Aero to apply it.";
    }

    private void ExtensionUserScriptsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingExtensionUi ||
            sender is not ToggleSwitch { Tag: CoreWebView2BrowserExtension extension } toggle)
        {
            return;
        }

        _extensionRegistry.SetUserScriptsEnabled(extension.Id, toggle.IsOn);
        ExtensionsStatusText.Text = $"{extension.Name} user scripts setting saved. Restart Aero to apply it.";
    }

    private async void ExtensionEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingExtensionUi || sender is not ToggleSwitch { Tag: CoreWebView2BrowserExtension extension } toggle)
        {
            return;
        }

        try
        {
            await extension.EnableAsync(toggle.IsOn);
            ExtensionsStatusText.Text = toggle.IsOn
                ? $"{extension.Name} enabled"
                : $"{extension.Name} disabled";
            await RefreshExtensionToolbarAsync();
        }
        catch (Exception exception)
        {
            _updatingExtensionUi = true;
            toggle.IsOn = extension.IsEnabled;
            _updatingExtensionUi = false;
            await ShowErrorDialogAsync("Could not update extension", exception.Message);
        }
    }

    private async void ExtensionPinButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CoreWebView2BrowserExtension extension })
        {
            return;
        }

        bool pinned = !_extensionRegistry.IsPinned(extension.Id);
        _extensionRegistry.SetPinned(extension.Id, pinned);
        ExtensionsStatusText.Text = pinned
            ? $"{extension.Name} pinned to the toolbar"
            : $"{extension.Name} removed from the toolbar";

        await RefreshExtensionToolbarAsync(refreshProfile: false);
        if (_extensionsPaneOpen)
        {
            RenderExtensionsPaneItems();
        }
    }

    private async void OpenExtensionActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CoreWebView2BrowserExtension extension } button)
        {
            return;
        }

        await OpenExtensionPageAsync(extension, button);
    }

    private async Task OpenExtensionPageAsync(
        CoreWebView2BrowserExtension extension,
        FrameworkElement? anchor = null)
    {
        if (!extension.IsEnabled)
        {
            await ShowErrorDialogAsync("Extension is disabled", "Enable the extension before opening it.");
            return;
        }

        ExtensionPackage? package = GetManagedExtensionPackage(extension);
        if (package is null)
        {
            ExtensionsToolbarFlyout.Hide();
            OpenExtensionsPane();
            ExtensionsStatusText.Text = $"Aero cannot find the local files for {extension.Name}.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(package.ActionPage))
        {
            string actionPage = package.ActionPage.TrimStart('/');
            if (Uri.TryCreate(
                    $"chrome-extension://{extension.Id}/{actionPage}",
                    UriKind.Absolute,
                    out Uri? popupUri))
            {
                await ShowExtensionPopupAsync(extension, popupUri, anchor);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(package.OptionsPage))
        {
            string optionsPage = package.OptionsPage.TrimStart('/');
            if (Uri.TryCreate(
                    $"chrome-extension://{extension.Id}/{optionsPage}",
                    UriKind.Absolute,
                    out Uri? optionsUri))
            {
                ExtensionsToolbarFlyout.Hide();
                CloseExtensionsPane();
                await CreateTabAsync(optionsUri, selectTab: true);
            }

            return;
        }

        ExtensionsToolbarFlyout.Hide();
        OpenExtensionsPane();
        ExtensionsStatusText.Text = $"{extension.Name} runs in the background and has no toolbar popup.";
    }

    private async Task ShowExtensionPopupAsync(
        CoreWebView2BrowserExtension extension,
        Uri popupUri,
        FrameworkElement? anchor)
    {
        if (_environment is null)
        {
            await ShowErrorDialogAsync("Extensions are not ready", "Open a browser tab and try again.");
            return;
        }

        const int popupWidth = 390;
        const int popupHeight = 520;

        PointInt32 popupPosition = CalculateExtensionPopupPosition(
            anchor,
            popupWidth,
            popupHeight);

        ExtensionsToolbarFlyout.Hide();
        CloseExtensionsPane();

        _extensionPopupWindow?.Close();

        ExtensionPopupWindow popup = new(
            _environment,
            RootGrid.ActualTheme,
            async uri =>
            {
                await CreateTabAsync(uri, selectTab: true);
            });

        _extensionPopupWindow = popup;
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_extensionPopupWindow, popup))
            {
                _extensionPopupWindow = null;
            }
        };

        try
        {
            await popup.ShowAsync(
                popupUri,
                popupPosition,
                new SizeInt32(popupWidth, popupHeight));
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_extensionPopupWindow, popup))
            {
                _extensionPopupWindow = null;
            }

            popup.Close();
            await ShowErrorDialogAsync(
                $"Could not open {extension.Name}",
                exception.Message);
        }
    }

    private PointInt32 CalculateExtensionPopupPosition(
        FrameworkElement? requestedAnchor,
        int popupWidth,
        int popupHeight)
    {
        FrameworkElement anchor = requestedAnchor ?? ExtensionsToolbarButton;
        Point anchorBottomRight;

        try
        {
            GeneralTransform transform = anchor.TransformToVisual(RootGrid);
            anchorBottomRight = transform.TransformPoint(
                new Point(anchor.ActualWidth, anchor.ActualHeight));
        }
        catch
        {
            anchor = ExtensionsToolbarButton;
            GeneralTransform transform = anchor.TransformToVisual(RootGrid);
            anchorBottomRight = transform.TransformPoint(
                new Point(anchor.ActualWidth, anchor.ActualHeight));
        }

        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        int x = AppWindow.Position.X +
                (int)Math.Round(anchorBottomRight.X * scale) -
                popupWidth;
        int y = AppWindow.Position.Y +
                (int)Math.Round(anchorBottomRight.Y * scale) +
                6;

        DisplayArea displayArea = DisplayArea.GetFromWindowId(
            AppWindow.Id,
            DisplayAreaFallback.Nearest);
        RectInt32 workArea = displayArea.WorkArea;

        int maximumX = Math.Max(workArea.X + 8, workArea.X + workArea.Width - popupWidth - 8);
        int maximumY = Math.Max(workArea.Y + 8, workArea.Y + workArea.Height - popupHeight - 8);

        x = Math.Clamp(x, workArea.X + 8, maximumX);
        y = Math.Clamp(y, workArea.Y + 8, maximumY);

        return new PointInt32(x, y);
    }

    private ExtensionPackage? GetManagedExtensionPackage(CoreWebView2BrowserExtension extension)
    {
        string? path = _extensionRegistry.GetPath(extension.Id);
        return path is null ? null : ExtensionCatalog.ReadPackage(path, extension.Id);
    }

    private async void RemoveExtensionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CoreWebView2BrowserExtension extension })
        {
            return;
        }

        ContentDialog confirm = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"Remove {extension.Name}?",
            Content = "The extension will stop running in every Aero tab.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await extension.RemoveAsync();
            string? managedPath = _extensionRegistry.Remove(extension.Id);
            TryDeleteManagedExtensionFolder(managedPath);
            await RefreshExtensionsPaneAsync();
        }
        catch (Exception exception)
        {
            await ShowErrorDialogAsync("Could not remove extension", exception.Message);
        }
    }

    private async void ImportChromeExtensionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_extensionImportRunning)
        {
            return;
        }

        CoreWebView2Profile? profile = GetActiveProfile();
        if (profile is null)
        {
            await ShowErrorDialogAsync("Extensions are not ready", "Open a browser tab and try again.");
            return;
        }

        _extensionImportRunning = true;
        UpdateExtensionActionButtons();
        try
        {
            await RefreshInstalledExtensionsAsync();
            ExtensionsStatusText.Text = "Scanning Chrome profiles…";
            IReadOnlyList<ExtensionPackage> packages =
                await Task.Run(ExtensionCatalog.ScanChromeProfiles);
            HashSet<string> installedIds = _installedExtensions
                .Select(extension => extension.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            packages = packages.Where(package => !installedIds.Contains(package.Id)).ToList();

            if (packages.Count == 0)
            {
                ExtensionsStatusText.Text = "No additional Chrome extensions were found.";
                await ShowErrorDialogAsync(
                    "Nothing to import",
                    "Aero could not find additional extensions in Chrome's Default or Profile folders.");
                return;
            }

            StackPanel choices = new() { Spacing = 6 };
            foreach (ExtensionPackage package in packages.Take(80))
            {
                CheckBox checkBox = new()
                {
                    IsChecked = true,
                    Tag = package,
                    Content = new StackPanel
                    {
                        Spacing = 1,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = package.Name,
                                FontSize = 12,
                                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                            },
                            new TextBlock
                            {
                                Text = $"Version {package.Version}",
                                FontSize = 10,
                                Foreground = GetMutedTextBrush()
                            }
                        }
                    }
                };
                choices.Children.Add(checkBox);
            }

            ContentDialog dialog = new()
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Import extensions from Chrome",
                Content = new ScrollViewer
                {
                    MaxHeight = 420,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = choices
                },
                PrimaryButtonText = "Import selected",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                ExtensionsStatusText.Text = "Chrome extension import cancelled.";
                return;
            }

            List<ExtensionPackage> selected = choices.Children
                .OfType<CheckBox>()
                .Where(checkBox => checkBox.IsChecked == true)
                .Select(checkBox => checkBox.Tag)
                .OfType<ExtensionPackage>()
                .ToList();

            if (selected.Count == 0)
            {
                ExtensionsStatusText.Text = "No Chrome extensions were selected.";
                return;
            }

            int imported = 0;
            List<string> failures = [];
            for (int index = 0; index < selected.Count; index++)
            {
                ExtensionPackage package = selected[index];
                ExtensionsStatusText.Text =
                    $"Importing {index + 1} of {selected.Count}: {package.Name}";
                string managedPath = CreateManagedExtensionPath(package.Name);
                try
                {
                    await Task.Run(() => CopyDirectory(package.FolderPath, managedPath));
                    CoreWebView2BrowserExtension extension =
                        await profile.AddBrowserExtensionAsync(managedPath);
                    _extensionRegistry.SetPath(extension.Id, managedPath);
                    imported++;
                }
                catch (Exception exception)
                {
                    TryDeleteManagedExtensionFolder(managedPath);
                    failures.Add($"{package.Name}: {exception.Message}");
                }
            }

            await RefreshExtensionsPaneAsync();
            ExtensionsStatusText.Text = failures.Count == 0
                ? $"Imported {imported} extension{(imported == 1 ? string.Empty : "s")} from Chrome"
                : $"Imported {imported}; {failures.Count} could not be added";

            if (failures.Count > 0)
            {
                await ShowErrorDialogAsync(
                    "Some extensions were not imported",
                    string.Join(Environment.NewLine, failures.Take(6)));
            }
        }
        catch (Exception exception)
        {
            await ShowErrorDialogAsync(
                "Could not import Chrome extensions",
                exception.Message);
            ExtensionsStatusText.Text = "Chrome extension import failed.";
        }
        finally
        {
            _extensionImportRunning = false;
            UpdateExtensionActionButtons();
        }
    }

    private async void InstallUnpackedExtensionButton_Click(object sender, RoutedEventArgs e)
    {
        CoreWebView2Profile? profile = GetActiveProfile();
        if (profile is null)
        {
            await ShowErrorDialogAsync("Extensions are not ready", "Open a browser tab and try again.");
            return;
        }

        FolderPicker picker = new()
        {
            CommitButtonText = "Load extension"
        };
        picker.FileTypeFilter.Add("*");
        nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        string manifestPath = System.IO.Path.Combine(folder.Path, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            await ShowErrorDialogAsync(
                "Not an unpacked extension",
                "Choose the top-level extension folder that contains manifest.json.");
            return;
        }

        string managedPath = CreateManagedExtensionPath(folder.Name);
        try
        {
            CopyDirectory(folder.Path, managedPath);
            CoreWebView2BrowserExtension extension = await profile.AddBrowserExtensionAsync(managedPath);
            _extensionRegistry.SetPath(extension.Id, managedPath);
            ExtensionsStatusText.Text = $"{extension.Name} installed";
            await RefreshExtensionsPaneAsync();
        }
        catch (Exception exception)
        {
            TryDeleteManagedExtensionFolder(managedPath);
            await ShowErrorDialogAsync("Could not install extension", exception.Message);
        }
    }

    private async void OpenChromeWebStoreButton_Click(object sender, RoutedEventArgs e)
    {
        await CreateTabAsync(
            new Uri("https://chromewebstore.google.com/"),
            selectTab: true);
        CloseExtensionsPane();
    }

    private string CreateManagedExtensionPath(string folderName)
    {
        string safeName = string.Concat(folderName.Select(character =>
            System.IO.Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        safeName = safeName.TrimStart('_');
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "extension";
        }

        string root = System.IO.Path.Combine(_browserProfile.StorageRoot, "Extensions");
        Directory.CreateDirectory(root);
        return System.IO.Path.Combine(root, $"{safeName}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}");
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (string filePath in Directory.EnumerateFiles(sourcePath))
        {
            string destinationFile = System.IO.Path.Combine(
                destinationPath,
                System.IO.Path.GetFileName(filePath));
            File.Copy(filePath, destinationFile, overwrite: true);
        }

        foreach (string directoryPath in Directory.EnumerateDirectories(sourcePath))
        {
            string destinationDirectory = System.IO.Path.Combine(
                destinationPath,
                System.IO.Path.GetFileName(directoryPath));
            CopyDirectory(directoryPath, destinationDirectory);
        }
    }

    private static void TryDeleteManagedExtensionFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // WebView2 can keep files open briefly; stale managed folders are harmless.
        }
    }
}
