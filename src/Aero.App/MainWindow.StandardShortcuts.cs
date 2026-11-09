using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace Aero.App;

// Shortcuts every mainstream browser has, so muscle memory from Chrome/Edge/Firefox works.
public sealed partial class MainWindow
{
    // Ctrl+1..8 selects that tab, Ctrl+9 the last one.
    private void SelectTabByNumberAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_tabs.Count == 0)
        {
            return;
        }

        int number = sender.Key - VirtualKey.Number0;
        int index = number == 9 ? _tabs.Count - 1 : number - 1;
        if (index >= 0 && index < _tabs.Count)
        {
            SelectTab(_tabs[index]);
        }
    }

    private async void HardReloadAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        BrowserTabState? state = ActiveTab;
        if (state is null ||
            state.IsDiscarded ||
            state.IsLoading ||
            state.InternalPageAddress is not null ||
            state.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            ReloadOrStop();
            return;
        }

        try
        {
            await core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}");
        }
        catch
        {
            state.Browser.Reload();
        }
    }

    private void HomeAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        HomeButton_Click(sender, new RoutedEventArgs());
    }

    private void ToggleBookmarksBarAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _settings.ShowBookmarksBar = !_settings.ShowBookmarksBar;
        _settings.Save();
        ApplyProfileSettingsUi();
    }

    private void BookmarksLibraryAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenLibraryPane(LibraryView.Bookmarks);
    }

    private async void ClearBrowsingDataAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _pendingSettingsSection = "clear";
        BrowserTabState state = await CreateTabAsync(null, selectTab: true, showStartPage: true);
        ShowSettingsPage(state);
    }

    private void CloseWindowAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }

    private static bool IsModifierDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    // Ctrl+Enter: "example" -> "www.example.com", like every desktop browser.
    internal static string CompleteDotComAddress(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length == 0 || trimmed.IndexOfAny(['.', ':', '/', ' ']) >= 0
            ? text
            : $"www.{trimmed}.com";
    }

    // Alt+Enter: open what was typed in a new foreground tab and leave the current page alone.
    private async void NavigateFromAddressBoxInNewTab()
    {
        string text = AddressBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (ActiveTab is BrowserTabState current)
        {
            ClearAddressDraft(current);
        }

        await CreateTabAsync(null, selectTab: true, showStartPage: true);
        SetAddressTextWithoutSuggestions(text);
        NavigateFromAddressBox();
    }

    private MenuFlyout BuildAddressBoxContextFlyout()
    {
        MenuFlyout flyout = new();
        MenuFlyoutItem cut = new() { Text = Localization.AeroLocalizer.TranslateUi("Cut") };
        cut.Click += (_, _) => AddressBox.CutSelectionToClipboard();
        MenuFlyoutItem copy = new() { Text = Localization.AeroLocalizer.TranslateUi("Copy") };
        copy.Click += (_, _) => AddressBox.CopySelectionToClipboard();
        MenuFlyoutItem paste = new() { Text = Localization.AeroLocalizer.TranslateUi("Paste") };
        paste.Click += (_, _) => AddressBox.PasteFromClipboard();
        MenuFlyoutItem pasteAndGo = new() { Text = Localization.AeroLocalizer.TranslateUi("Paste and go") };
        pasteAndGo.Click += async (_, _) =>
        {
            try
            {
                DataPackageView content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Text))
                {
                    return;
                }

                string text = (await content.GetTextAsync()).Trim();
                if (text.Length > 0)
                {
                    SetAddressTextWithoutSuggestions(text);
                    NavigateFromAddressBox();
                }
            }
            catch
            {
                // Another app may hold the clipboard open.
            }
        };
        MenuFlyoutItem selectAll = new() { Text = Localization.AeroLocalizer.TranslateUi("Select all") };
        selectAll.Click += (_, _) => AddressBox.SelectAll();

        flyout.Opening += (_, _) =>
        {
            bool hasSelection = AddressBox.SelectionLength > 0;
            cut.IsEnabled = hasSelection;
            copy.IsEnabled = hasSelection;
            bool hasText = false;
            try
            {
                hasText = Clipboard.GetContent().Contains(StandardDataFormats.Text);
            }
            catch
            {
                // Clipboard busy: leave paste disabled.
            }

            paste.IsEnabled = hasText;
            pasteAndGo.IsEnabled = hasText;
        };

        flyout.Items.Add(cut);
        flyout.Items.Add(copy);
        flyout.Items.Add(paste);
        flyout.Items.Add(pasteAndGo);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(selectAll);
        return flyout;
    }

    private void MoveTabLeftAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        MoveActiveTab(-1);
    }

    private void MoveTabRightAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        MoveActiveTab(1);
    }

    // Moves within the tab's own section: pinned tabs among pinned, grouped tabs inside their group.
    private void MoveActiveTab(int direction)
    {
        if (ActiveTab is not BrowserTabState state)
        {
            return;
        }

        if (state.IsPinned)
        {
            MovePinnedTab(state, direction);
            return;
        }

        if (FindTabGroup(state) is TabGroupState group)
        {
            int index = _tabs.IndexOf(state);
            int target = index + direction;
            if (target < 0 || target >= _tabs.Count || !ReferenceEquals(FindTabGroup(_tabs[target]), group))
            {
                return;
            }

            (_tabs[index], _tabs[target]) = (_tabs[target], _tabs[index]);
        }
        else
        {
            List<List<BrowserTabState>> blocks = BuildTabStripBlocks();
            int index = blocks.FindIndex(block => block.Count == 1 && ReferenceEquals(block[0], state));
            int target = index + direction;
            if (index < 0 || target < 0 || target >= blocks.Count || IsPinnedBlock(blocks[target]))
            {
                return;
            }

            (blocks[index], blocks[target]) = (blocks[target], blocks[index]);
            _tabs.Clear();
            foreach (List<BrowserTabState> block in blocks)
            {
                _tabs.AddRange(block);
            }
        }

        RefreshTabStripOrder();
        UpdateTabWidths();
        UpdateAllTabVisuals();
        SaveSessionSnapshot();
    }

    private void SearchTabsAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        TabListButton_Click(TabListButton, new RoutedEventArgs());
    }

    private void MainMenuAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        AeroMenuFlyout.ShowAt(MenuButton);
    }

    private void DevToolsAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        // Same path as the menu item, so opening DevTools still locks Aero Vault.
        DeveloperToolsMenuItem_Click(sender, new RoutedEventArgs());
    }
}
