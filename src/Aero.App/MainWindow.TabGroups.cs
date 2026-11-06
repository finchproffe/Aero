using Aero.App.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using Windows.UI;

namespace Aero.App;

public sealed partial class MainWindow
{
    private const int MaximumTabGroups = 20;
    private const int MaximumTabGroupNameLength = 36;

    private static readonly TabGroupColorOption[] TabGroupColors =
    [
        new("Grey", "Grey"),
        new("Blue", "Blue"),
        new("Cyan", "Cyan"),
        new("Green", "Green"),
        new("Yellow", "Yellow"),
        new("Orange", "Orange"),
        new("Red", "Red"),
        new("Pink", "Pink"),
        new("Purple", "Purple")
    ];

    private readonly List<TabGroupState> _tabGroups = [];
    private TabGroupState? _tabGroupDragCandidate;
    private bool _tabGroupDragActive;
    private double _tabGroupDragStartX;
    private double _tabGroupDragAnchorX;

    private TabGroupState? FindTabGroup(BrowserTabState state) =>
        FindTabGroup(state.TabGroupId);

    private TabGroupState? FindTabGroup(string? groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return null;
        }

        return _tabGroups.FirstOrDefault(group =>
            group.Id.Equals(groupId, StringComparison.Ordinal));
    }

    private bool IsTabEffectivelyPinned(BrowserTabState state) =>
        state.IsPinned || FindTabGroup(state)?.IsPinned == true;

    private bool IsTabCloseProtected(BrowserTabState state) =>
        IsTabEffectivelyPinned(state);

    private bool CanDragTabIndependently(BrowserTabState state) =>
        FindTabGroup(state) is null;

    private bool IsTabVisibleInStrip(BrowserTabState state) =>
        FindTabGroup(state)?.IsCollapsed != true;

    private BrowserTabState? GetLastVisibleTab() =>
        _tabs.LastOrDefault(IsTabVisibleInStrip);

    private double GetVisibleTabGroupHeaderSpace() =>
        GetTabGroupsInStripOrder().Sum(GetTabGroupHeaderWidth);

    private int GetVisibleTabGroupCount() =>
        GetTabGroupsInStripOrder().Count;

    private void RestoreTabGroupsFromSession(IEnumerable<SessionTabGroupEntry>? entries)
    {
        foreach (TabGroupState existing in _tabGroups.ToArray())
        {
            ReleaseTabGroupHeader(existing);
        }

        _tabGroups.Clear();
        if (entries is null)
        {
            return;
        }

        HashSet<string> usedIds = new(StringComparer.Ordinal);
        foreach (SessionTabGroupEntry entry in entries.Take(MaximumTabGroups))
        {
            string id = string.IsNullOrWhiteSpace(entry.Id) ||
                        !usedIds.Add(entry.Id)
                ? Guid.NewGuid().ToString("N")
                : entry.Id;

            usedIds.Add(id);
            _tabGroups.Add(CreateTabGroupState(
                id,
                entry.Name,
                entry.Color,
                entry.IsCollapsed,
                entry.IsPinned));
        }
    }

    private TabGroupState CreateTabGroupState(
        string id,
        string? name,
        string? color,
        bool isCollapsed,
        bool isPinned)
    {
        TabGroupState group = new()
        {
            Id = id,
            Name = NormalizeTabGroupName(name),
            ColorKey = NormalizeTabGroupColor(color),
            IsCollapsed = isCollapsed,
            IsPinned = isPinned
        };

        BuildTabGroupHeader(group);
        return group;
    }

    private void BuildTabGroupHeader(TabGroupState group)
    {
        Border colorDot = new()
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };

        TextBlock title = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(title, 1);

        TextBlock count = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10,
            Opacity = 0.78
        };
        Grid.SetColumn(count, 2);

        FontIcon pinIcon = new()
        {
            Glyph = "\uE718",
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        Grid.SetColumn(pinIcon, 3);

        FontIcon chevron = new()
        {
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(chevron, 4);

        Grid content = new()
        {
            Height = Math.Max(28, ConfiguredTabBarHeight - 10),
            ColumnSpacing = 6
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        content.Children.Add(colorDot);
        content.Children.Add(title);
        content.Children.Add(count);
        content.Children.Add(pinIcon);
        content.Children.Add(chevron);

        ScaleTransform headerTransform = new()
        {
            ScaleX = 1,
            ScaleY = 1
        };

        Border header = new()
        {
            Height = Math.Max(31, ConfiguredTabBarHeight - 7),
            Margin = new Thickness(3, 4, 1, 0),
            Padding = new Thickness(9, 0, 7, 0),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Child = content,
            Tag = group,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = headerTransform
        };

        header.PointerPressed += TabGroupHeader_PointerPressed;
        header.PointerEntered += TabGroupHeader_PointerEntered;
        header.PointerExited += TabGroupHeader_PointerExited;
        header.ContextFlyout = BuildTabGroupContextFlyout(group);

        group.Header = header;
        group.ColorDot = colorDot;
        group.TitleText = title;
        group.CountText = count;
        group.PinIcon = pinIcon;
        group.ChevronIcon = chevron;
        group.HeaderTransform = headerTransform;
        UpdateTabGroupVisual(group);
    }

    private MenuFlyout BuildTabGroupContextFlyout(TabGroupState group)
    {
        MenuFlyout flyout = new();

        MenuFlyoutItem newTabItem = new() { Text = "New tab in group" };
        newTabItem.Click += async (_, _) =>
        {
            group.IsCollapsed = false;
            RefreshTabStripOrder();
            await CreateTabAsync(
                null,
                selectTab: true,
                showStartPage: true,
                tabGroupId: group.Id);
        };
        flyout.Items.Add(newTabItem);

        MenuFlyoutItem renameItem = new() { Text = "Rename group…" };
        renameItem.Click += async (_, _) => await RenameTabGroupAsync(group);
        flyout.Items.Add(renameItem);

        MenuFlyoutItem pinGroupItem = new() { Text = "Pin group" };
        pinGroupItem.Click += (_, _) => TogglePinnedTabGroup(group);
        flyout.Items.Add(pinGroupItem);

        MenuFlyoutSubItem colorItem = new() { Text = "Group color" };
        foreach (TabGroupColorOption option in TabGroupColors)
        {
            MenuFlyoutItem choice = new()
            {
                Text = option.DisplayName,
                Tag = option.Key
            };
            choice.Click += (_, _) => ChangeTabGroupColor(group, option.Key);
            colorItem.Items.Add(choice);
        }
        flyout.Items.Add(colorItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem collapseItem = new() { Text = "Collapse group" };
        collapseItem.Click += (_, _) => ToggleTabGroupCollapsed(group);
        flyout.Items.Add(collapseItem);

        MenuFlyoutItem moveLeftItem = new() { Text = "Move group left" };
        moveLeftItem.Click += (_, _) => MoveTabGroup(group, -1);
        flyout.Items.Add(moveLeftItem);

        MenuFlyoutItem moveRightItem = new() { Text = "Move group right" };
        moveRightItem.Click += (_, _) => MoveTabGroup(group, 1);
        flyout.Items.Add(moveRightItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem ungroupItem = new() { Text = "Remove group, keep tabs" };
        ungroupItem.Click += (_, _) => UngroupTabGroup(group);
        flyout.Items.Add(ungroupItem);

        MenuFlyoutItem closeGroupItem = new() { Text = "Close group" };
        closeGroupItem.Click += async (_, _) => await CloseTabGroupAsync(group);
        flyout.Items.Add(closeGroupItem);

        flyout.Opening += (_, _) =>
        {
            bool exists = _tabGroups.Contains(group);
            pinGroupItem.Text = group.IsPinned ? "Unpin group" : "Pin group";
            collapseItem.Text = group.IsCollapsed ? "Expand group" : "Collapse group";
            moveLeftItem.IsEnabled = exists && CanMoveTabGroup(group, -1);
            moveRightItem.IsEnabled = exists && CanMoveTabGroup(group, 1);
            ungroupItem.Text = group.IsPinned
                ? "Remove group, keep tabs pinned"
                : "Remove group, keep tabs";
            ungroupItem.IsEnabled = exists;
            closeGroupItem.Text = group.IsPinned
                ? "Close group (unpin first)"
                : "Close group";
            closeGroupItem.IsEnabled =
                exists &&
                !group.IsPinned &&
                GetTabsInGroup(group).Count > 0;

            foreach (MenuFlyoutItem choice in colorItem.Items.OfType<MenuFlyoutItem>())
            {
                string key = choice.Tag as string ?? string.Empty;
                TabGroupColorOption option = TabGroupColors.First(item =>
                    item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                choice.Text = group.ColorKey.Equals(key, StringComparison.OrdinalIgnoreCase)
                    ? $"✓ {option.DisplayName}"
                    : option.DisplayName;
            }
        };

        return flyout;
    }

    private void PopulateTabGroupSubmenu(
        BrowserTabState state,
        MenuFlyoutSubItem submenu)
    {
        submenu.Items.Clear();

        MenuFlyoutItem createItem = new() { Text = "New group…" };
        createItem.Click += async (_, _) => await CreateTabGroupForTabAsync(state);
        submenu.Items.Add(createItem);

        IReadOnlyList<TabGroupState> groups = GetTabGroupsInStripOrder();
        if (groups.Count == 0)
        {
            return;
        }

        submenu.Items.Add(new MenuFlyoutSeparator());
        foreach (TabGroupState group in groups)
        {
            MenuFlyoutItem groupItem = new()
            {
                Text = group.IsPinned ? $"Pinned · {group.Name}" : group.Name,
                IsEnabled = !group.Id.Equals(state.TabGroupId, StringComparison.Ordinal)
            };
            groupItem.Click += (_, _) => MoveTabToGroup(state, group);
            submenu.Items.Add(groupItem);
        }
    }

    private async Task CreateTabGroupForTabAsync(BrowserTabState state)
    {
        if (!_tabs.Contains(state) || state.IsPinned)
        {
            return;
        }

        TabGroupDialogResult? result = await ShowCreateTabGroupDialogAsync();
        if (result is null)
        {
            return;
        }

        if (_tabGroups.Count >= MaximumTabGroups)
        {
            await ShowErrorDialogAsync(
                "Too many tab groups",
                $"Aero supports up to {MaximumTabGroups} groups in one window.");
            return;
        }

        TabGroupState group = CreateTabGroupState(
            Guid.NewGuid().ToString("N"),
            result.Value.Name,
            result.Value.ColorKey,
            isCollapsed: false,
            isPinned: false);
        _tabGroups.Add(group);
        MoveTabToGroup(state, group);
    }

    private async Task<TabGroupDialogResult?> ShowCreateTabGroupDialogAsync()
    {
        TextBox nameBox = new()
        {
            Header = "Group name",
            PlaceholderText = "New group",
            MaxLength = MaximumTabGroupNameLength
        };

        ComboBox colorBox = new()
        {
            Header = "Color",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 1
        };

        foreach (TabGroupColorOption option in TabGroupColors)
        {
            Border dot = new()
            {
                Width = 10,
                Height = 10,
                Margin = new Thickness(0, 0, 7, 0),
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(GetTabGroupColor(option.Key))
            };
            TextBlock label = new()
            {
                Text = option.DisplayName,
                VerticalAlignment = VerticalAlignment.Center
            };
            StackPanel row = new()
            {
                Orientation = Orientation.Horizontal
            };
            row.Children.Add(dot);
            row.Children.Add(label);
            colorBox.Items.Add(new ComboBoxItem
            {
                Content = row,
                Tag = option.Key
            });
        }

        StackPanel content = new()
        {
            Width = 330,
            Spacing = 12
        };
        content.Children.Add(nameBox);
        content.Children.Add(colorBox);

        ContentDialog dialog = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Create tab group",
            Content = content,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        ContentDialogResult dialogResult = await dialog.ShowAsync();
        if (dialogResult != ContentDialogResult.Primary)
        {
            return null;
        }

        string colorKey = (colorBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Blue";
        return new TabGroupDialogResult(
            NormalizeTabGroupName(nameBox.Text),
            NormalizeTabGroupColor(colorKey));
    }

    private async Task RenameTabGroupAsync(TabGroupState group)
    {
        if (!_tabGroups.Contains(group))
        {
            return;
        }

        TextBox nameBox = new()
        {
            Text = group.Name,
            Header = "Group name",
            MaxLength = MaximumTabGroupNameLength,
            SelectionStart = group.Name.Length
        };

        ContentDialog dialog = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Rename tab group",
            Content = nameBox,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        group.Name = NormalizeTabGroupName(nameBox.Text);
        UpdateTabGroupVisual(group);
        UpdateTabWidths();
        SaveSessionSnapshot();
    }

    private void ChangeTabGroupColor(TabGroupState group, string colorKey)
    {
        if (!_tabGroups.Contains(group))
        {
            return;
        }

        group.ColorKey = NormalizeTabGroupColor(colorKey);
        UpdateTabGroupVisual(group);
        SaveSessionSnapshot();
    }

    private void TogglePinnedItemFromContext(BrowserTabState state)
    {
        TabGroupState? group = FindTabGroup(state);
        if (group?.IsPinned == true)
        {
            TogglePinnedTabGroup(group);
            return;
        }

        TogglePinnedTab(state);
    }

    private string GetPinnedContextText(BrowserTabState state)
    {
        TabGroupState? group = FindTabGroup(state);
        if (group?.IsPinned == true)
        {
            return "Unpin group";
        }

        return state.IsPinned ? "Unpin tab" : "Pin tab";
    }

    private void TogglePinnedTabGroup(TabGroupState group)
    {
        if (!_tabGroups.Contains(group))
        {
            return;
        }

        group.IsPinned = !group.IsPinned;
        foreach (BrowserTabState tab in GetTabsInGroup(group))
        {
            tab.IsPinned = false;
        }

        NormalizePinnedTabOrder();
        RefreshTabStripOrder();
        UpdateTabWidths();
        UpdateAllTabVisuals();
        SaveSessionSnapshot();
    }

    private void MoveTabToGroup(BrowserTabState state, TabGroupState group)
    {
        if (!_tabs.Contains(state) ||
            state.IsPinned ||
            !_tabGroups.Contains(group) ||
            group.Id.Equals(state.TabGroupId, StringComparison.Ordinal))
        {
            return;
        }

        string? previousGroupId = state.TabGroupId;
        int previousIndex = _tabs.IndexOf(state);
        _tabs.Remove(state);
        state.TabGroupId = group.Id;

        int lastGroupIndex = _tabs.FindLastIndex(tab =>
            group.Id.Equals(tab.TabGroupId, StringComparison.Ordinal));
        int insertionIndex = lastGroupIndex >= 0
            ? lastGroupIndex + 1
            : Math.Clamp(previousIndex, 0, _tabs.Count);
        _tabs.Insert(insertionIndex, state);

        group.IsCollapsed = false;
        RemoveEmptyTabGroups(previousGroupId);
        NormalizePinnedTabOrder();
        RefreshTabStripOrder();
        UpdateTabWidths();
        UpdateAllTabVisuals();
        SaveSessionSnapshot();
    }

    private void RemoveTabFromGroup(BrowserTabState state)
    {
        TabGroupState? group = FindTabGroup(state);
        if (group is null)
        {
            return;
        }

        int previousIndex = _tabs.IndexOf(state);
        _tabs.Remove(state);
        state.TabGroupId = null;

        int lastGroupIndex = _tabs.FindLastIndex(tab =>
            group.Id.Equals(tab.TabGroupId, StringComparison.Ordinal));
        int insertionIndex = lastGroupIndex >= 0
            ? lastGroupIndex + 1
            : Math.Clamp(previousIndex, 0, _tabs.Count);
        _tabs.Insert(insertionIndex, state);

        RemoveEmptyTabGroups(group.Id);
        NormalizePinnedTabOrder();
        RefreshTabStripOrder();
        UpdateTabWidths();
        UpdateAllTabVisuals();
        SaveSessionSnapshot();
    }

    private void DetachTabFromGroupBeforePinning(BrowserTabState state)
    {
        string? previousGroupId = state.TabGroupId;
        state.TabGroupId = null;
        RemoveEmptyTabGroups(previousGroupId);
    }

    private void PlaceNewTabInsideAssignedGroup(BrowserTabState state)
    {
        TabGroupState? group = FindTabGroup(state);
        if (group is null)
        {
            state.TabGroupId = null;
            return;
        }

        int lastExistingIndex = _tabs.FindLastIndex(tab =>
            !ReferenceEquals(tab, state) &&
            group.Id.Equals(tab.TabGroupId, StringComparison.Ordinal));
        if (lastExistingIndex < 0)
        {
            return;
        }

        _tabs.Remove(state);
        lastExistingIndex = _tabs.FindLastIndex(tab =>
            group.Id.Equals(tab.TabGroupId, StringComparison.Ordinal));
        _tabs.Insert(lastExistingIndex + 1, state);
    }

    private void ToggleTabGroupCollapsed(TabGroupState group)
    {
        if (!_tabGroups.Contains(group))
        {
            return;
        }

        group.IsCollapsed = !group.IsCollapsed;
        RefreshTabStripOrder();
        UpdateTabWidths();
        UpdateAllTabVisuals();
        SaveSessionSnapshot();
    }

    private void EnsureTabGroupExpandedForSelection(BrowserTabState state)
    {
        if (_isRestoringSession)
        {
            return;
        }

        TabGroupState? group = FindTabGroup(state);
        if (group?.IsCollapsed != true)
        {
            return;
        }

        group.IsCollapsed = false;
        RefreshTabStripOrder();
        UpdateTabWidths();
    }

    private void UngroupTabGroup(TabGroupState group)
    {
        if (!_tabGroups.Contains(group))
        {
            return;
        }

        foreach (BrowserTabState tab in GetTabsInGroup(group))
        {
            tab.TabGroupId = null;
            tab.IsPinned = group.IsPinned;
        }

        _tabGroups.Remove(group);
        ReleaseTabGroupHeader(group);
        NormalizePinnedTabOrder();
        RefreshTabStripOrder();
        UpdateTabWidths();
        UpdateAllTabVisuals();
        SaveSessionSnapshot();
    }

    private async Task CloseTabGroupAsync(TabGroupState group)
    {
        if (group.IsPinned)
        {
            ShowPinnedTabGroupProtection(group);
            return;
        }

        foreach (BrowserTabState tab in GetTabsInGroup(group).ToList())
        {
            await CloseTabAsync(tab);
        }
    }

    private void MoveTabGroup(TabGroupState group, int direction)
    {
        if (direction == 0)
        {
            return;
        }

        List<List<BrowserTabState>> blocks = BuildTabStripBlocks();
        int groupBlockIndex = blocks.FindIndex(block =>
            block.Any(tab => group.Id.Equals(tab.TabGroupId, StringComparison.Ordinal)));
        int targetIndex = groupBlockIndex + Math.Sign(direction);
        if (groupBlockIndex < 0 ||
            targetIndex < 0 ||
            targetIndex >= blocks.Count ||
            IsPinnedBlock(blocks[groupBlockIndex]) != IsPinnedBlock(blocks[targetIndex]))
        {
            return;
        }

        (blocks[groupBlockIndex], blocks[targetIndex]) =
            (blocks[targetIndex], blocks[groupBlockIndex]);

        _tabs.Clear();
        foreach (List<BrowserTabState> block in blocks)
        {
            _tabs.AddRange(block);
        }

        RefreshTabStripOrder();
        UpdateTabWidths();
        UpdateAllTabVisuals();
        SaveSessionSnapshot();
    }

    private bool CanMoveTabGroup(TabGroupState group, int direction)
    {
        List<List<BrowserTabState>> blocks = BuildTabStripBlocks();
        int index = blocks.FindIndex(block =>
            block.Any(tab => group.Id.Equals(tab.TabGroupId, StringComparison.Ordinal)));
        int target = index + Math.Sign(direction);
        return index >= 0 &&
            target >= 0 &&
            target < blocks.Count &&
            IsPinnedBlock(blocks[index]) == IsPinnedBlock(blocks[target]);
    }

    private bool CanMovePinnedTab(BrowserTabState state, int direction)
    {
        if (!state.IsPinned || FindTabGroup(state) is not null || direction == 0)
        {
            return false;
        }

        List<List<BrowserTabState>> blocks = BuildTabStripBlocks();
        int index = blocks.FindIndex(block =>
            block.Count == 1 && ReferenceEquals(block[0], state));
        int target = index + Math.Sign(direction);
        return index >= 0 &&
            target >= 0 &&
            target < blocks.Count &&
            IsPinnedBlock(blocks[target]);
    }

    private void MovePinnedTab(BrowserTabState state, int direction)
    {
        if (!CanMovePinnedTab(state, direction))
        {
            return;
        }

        List<List<BrowserTabState>> blocks = BuildTabStripBlocks();
        int index = blocks.FindIndex(block =>
            block.Count == 1 && ReferenceEquals(block[0], state));
        int target = index + Math.Sign(direction);
        (blocks[index], blocks[target]) = (blocks[target], blocks[index]);

        _tabs.Clear();
        foreach (List<BrowserTabState> block in blocks)
        {
            _tabs.AddRange(block);
        }

        RefreshTabStripOrder();
        UpdateTabWidths();
        UpdateAllTabVisuals();
        SaveSessionSnapshot();
    }

    private void NormalizePinnedTabOrder()
    {
        List<List<BrowserTabState>> blocks = BuildTabStripBlocks();
        List<List<BrowserTabState>> ordered = blocks
            .Where(block => IsPinnedBlock(block))
            .Concat(blocks.Where(block => !IsPinnedBlock(block)))
            .ToList();

        _tabs.Clear();
        foreach (List<BrowserTabState> block in ordered)
        {
            _tabs.AddRange(block);
        }
    }

    private List<List<BrowserTabState>> BuildTabStripBlocks()
    {
        List<List<BrowserTabState>> blocks = [];
        HashSet<string> includedGroups = new(StringComparer.Ordinal);

        foreach (BrowserTabState tab in _tabs)
        {
            TabGroupState? group = FindTabGroup(tab);
            if (group is null)
            {
                blocks.Add([tab]);
                continue;
            }

            if (!includedGroups.Add(group.Id))
            {
                continue;
            }

            blocks.Add(_tabs.Where(candidate =>
                group.Id.Equals(candidate.TabGroupId, StringComparison.Ordinal)).ToList());
        }

        return blocks;
    }

    private bool IsPinnedBlock(IReadOnlyList<BrowserTabState> block)
    {
        if (block.Count == 0)
        {
            return false;
        }

        TabGroupState? group = FindTabGroup(block[0]);
        return group?.IsPinned == true || (group is null && block[0].IsPinned);
    }

    private void RefreshTabStripOrder()
    {
        if (_tabDragActive)
        {
            return;
        }

        TabStripPanel.Children.Clear();
        HashSet<string> renderedGroups = new(StringComparer.Ordinal);

        foreach (BrowserTabState tab in _tabs)
        {
            if (tab.IsPinned)
            {
                tab.TabGroupId = null;
            }

            TabGroupState? group = FindTabGroup(tab);
            if (group is not null && renderedGroups.Add(group.Id))
            {
                TabStripPanel.Children.Add(group.Header);
            }

            tab.Header.Visibility = group?.IsCollapsed == true
                ? Visibility.Collapsed
                : Visibility.Visible;
            TabStripPanel.Children.Add(tab.Header);
        }

        UpdateAllTabGroupVisuals();
    }

    private void RemoveEmptyTabGroups(string? preferredGroupId = null)
    {
        foreach (TabGroupState group in _tabGroups.Where(group =>
            !string.IsNullOrWhiteSpace(preferredGroupId)
                ? group.Id.Equals(preferredGroupId, StringComparison.Ordinal) &&
                  !_tabs.Any(tab => group.Id.Equals(tab.TabGroupId, StringComparison.Ordinal))
                : !_tabs.Any(tab => group.Id.Equals(tab.TabGroupId, StringComparison.Ordinal)))
            .ToList())
        {
            _tabGroups.Remove(group);
            ReleaseTabGroupHeader(group);
        }
    }

    private void ReleaseTabGroupHeader(TabGroupState group)
    {
        group.Header.PointerPressed -= TabGroupHeader_PointerPressed;
        group.Header.PointerEntered -= TabGroupHeader_PointerEntered;
        group.Header.PointerExited -= TabGroupHeader_PointerExited;
        group.Header.ContextFlyout = null;
        TabStripPanel.Children.Remove(group.Header);
    }

    private void CleanupTabGroupState()
    {
        FinishTabGroupDrag(toggleIfClick: false);
        foreach (TabGroupState group in _tabGroups.ToArray())
        {
            ReleaseTabGroupHeader(group);
        }

        _tabGroups.Clear();
    }

    private IReadOnlyList<BrowserTabState> GetTabsInGroup(TabGroupState group) =>
        _tabs.Where(tab =>
            group.Id.Equals(tab.TabGroupId, StringComparison.Ordinal)).ToList();

    private IReadOnlyList<TabGroupState> GetTabGroupsInStripOrder()
    {
        List<TabGroupState> ordered = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (BrowserTabState tab in _tabs)
        {
            TabGroupState? group = FindTabGroup(tab);
            if (group is not null && seen.Add(group.Id))
            {
                ordered.Add(group);
            }
        }

        return ordered;
    }

    private void UpdateAllTabGroupVisuals()
    {
        foreach (TabGroupState group in _tabGroups)
        {
            UpdateTabGroupVisual(group);
        }
    }

    private void UpdateTabGroupVisual(TabGroupState group)
    {
        if (group.Header is null)
        {
            return;
        }

        Color color = GetTabGroupColor(group.ColorKey);
        bool containsActiveTab = ActiveTab is BrowserTabState active &&
            group.Id.Equals(active.TabGroupId, StringComparison.Ordinal);
        byte backgroundAlpha = containsActiveTab
            ? (byte)70
            : group.IsPointerOver ? (byte)54 : group.IsPinned ? (byte)44 : (byte)34;
        byte borderAlpha = containsActiveTab
            ? (byte)225
            : group.IsPinned ? (byte)180 : (byte)145;
        int tabCount = GetTabsInGroup(group).Count;

        group.Header.Width = GetTabGroupHeaderWidth(group);
        group.Header.Background = new SolidColorBrush(Color.FromArgb(
            backgroundAlpha,
            color.R,
            color.G,
            color.B));
        group.Header.BorderBrush = new SolidColorBrush(Color.FromArgb(
            borderAlpha,
            color.R,
            color.G,
            color.B));
        group.ColorDot.Background = new SolidColorBrush(color);
        group.TitleText.Text = group.Name;
        group.TitleText.Foreground = new SolidColorBrush(color);
        group.CountText.Text = tabCount.ToString();
        group.CountText.Foreground = new SolidColorBrush(color);
        group.PinIcon.Visibility = group.IsPinned
            ? Visibility.Visible
            : Visibility.Collapsed;
        group.PinIcon.Foreground = new SolidColorBrush(color);
        group.ChevronIcon.Glyph = group.IsCollapsed ? "\uE76C" : "\uE70D";
        group.ChevronIcon.Foreground = new SolidColorBrush(color);

        ToolTipService.SetToolTip(
            group.Header,
            $"{(group.IsPinned ? "Pinned group · " : string.Empty)}" +
            $"{group.Name} · {tabCount} tab{(tabCount == 1 ? string.Empty : "s")} · " +
            $"click to {(group.IsCollapsed ? "expand" : "collapse")}");
    }

    private static double GetTabGroupHeaderWidth(TabGroupState group)
    {
        int countDigits = Math.Max(1, group.CountText?.Text?.Length ?? 1);
        double pinSpace = group.IsPinned ? 14 : 0;
        return Math.Clamp(
            72 + (group.Name.Length * 6.1) + (countDigits * 5) + pinSpace,
            88,
            190);
    }

    private void ShowPinnedTabGroupProtection(TabGroupState group)
    {
        ToolTipService.SetToolTip(
            group.Header,
            "Pinned group — unpin it before closing its tabs");

        AnimateDouble(group.HeaderTransform, "ScaleX", 1, 1.045, 80, () =>
            AnimateDouble(group.HeaderTransform, "ScaleX", 1.045, 1, 105));
        AnimateDouble(group.HeaderTransform, "ScaleY", 1, 1.045, 80, () =>
            AnimateDouble(group.HeaderTransform, "ScaleY", 1.045, 1, 105));
    }

    private bool ShowPinnedGroupProtectionForTab(BrowserTabState state)
    {
        TabGroupState? group = FindTabGroup(state);
        if (group?.IsPinned != true)
        {
            return false;
        }

        ShowPinnedTabGroupProtection(group);
        return true;
    }

    private Color GetTabGroupColor(string colorKey)
    {
        bool dark = IsDarkTheme();
        return NormalizeTabGroupColor(colorKey) switch
        {
            "Grey" => dark
                ? Color.FromArgb(255, 180, 190, 198)
                : Color.FromArgb(255, 92, 103, 112),
            "Cyan" => dark
                ? Color.FromArgb(255, 85, 211, 225)
                : Color.FromArgb(255, 0, 125, 151),
            "Green" => dark
                ? Color.FromArgb(255, 105, 207, 142)
                : Color.FromArgb(255, 30, 132, 77),
            "Yellow" => dark
                ? Color.FromArgb(255, 236, 198, 92)
                : Color.FromArgb(255, 151, 108, 0),
            "Orange" => dark
                ? Color.FromArgb(255, 241, 155, 89)
                : Color.FromArgb(255, 181, 83, 18),
            "Red" => dark
                ? Color.FromArgb(255, 244, 112, 119)
                : Color.FromArgb(255, 188, 47, 55),
            "Pink" => dark
                ? Color.FromArgb(255, 239, 127, 188)
                : Color.FromArgb(255, 171, 52, 118),
            "Purple" => dark
                ? Color.FromArgb(255, 185, 139, 244)
                : Color.FromArgb(255, 111, 70, 184),
            _ => dark
                ? Color.FromArgb(255, 112, 176, 255)
                : Color.FromArgb(255, 45, 112, 207)
        };
    }

    private static string NormalizeTabGroupName(string? name)
    {
        string resolved = string.IsNullOrWhiteSpace(name) ? "New group" : name.Trim();
        return resolved.Length <= MaximumTabGroupNameLength
            ? resolved
            : resolved[..MaximumTabGroupNameLength];
    }

    private static string NormalizeTabGroupColor(string? color)
    {
        TabGroupColorOption? option = TabGroupColors.FirstOrDefault(item =>
            item.Key.Equals(color, StringComparison.OrdinalIgnoreCase));
        return option?.Key ?? "Blue";
    }

    private void TabGroupHeader_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border { Tag: TabGroupState group } header ||
            !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        _tabGroupDragCandidate = group;
        _tabGroupDragActive = false;
        _tabGroupDragStartX = e.GetCurrentPoint(TabScrollViewer).Position.X;
        _tabGroupDragAnchorX = _tabGroupDragStartX;
        TabScrollViewer.CapturePointer(e.Pointer);
    }

    private bool HandleTabGroupPointerMoved(PointerRoutedEventArgs e)
    {
        TabGroupState? group = _tabGroupDragCandidate;
        if (group is null || !_tabGroups.Contains(group))
        {
            return false;
        }

        var point = e.GetCurrentPoint(TabScrollViewer);
        if (!point.Properties.IsLeftButtonPressed)
        {
            FinishTabGroupDrag(e);
            return true;
        }

        double pointerX = point.Position.X;
        if (!_tabGroupDragActive)
        {
            if (Math.Abs(pointerX - _tabGroupDragStartX) < 6)
            {
                e.Handled = true;
                return true;
            }

            _tabGroupDragActive = true;
        }

        double step = Math.Clamp(GetTabGroupHeaderWidth(group) * 0.42, 38, 76);
        double delta = pointerX - _tabGroupDragAnchorX;
        int direction = delta > 0 ? 1 : -1;
        while (Math.Abs(delta) >= step && CanMoveTabGroup(group, direction))
        {
            MoveTabGroup(group, direction);
            _tabGroupDragAnchorX += direction * step;
            delta = pointerX - _tabGroupDragAnchorX;
        }

        e.Handled = true;
        return true;
    }

    private bool FinishTabGroupDrag(
        PointerRoutedEventArgs? e = null,
        bool releaseCapture = true,
        bool toggleIfClick = true)
    {
        TabGroupState? group = _tabGroupDragCandidate;
        if (group is null)
        {
            return false;
        }

        bool wasDragging = _tabGroupDragActive;
        _tabGroupDragCandidate = null;
        _tabGroupDragActive = false;
        _tabGroupDragStartX = 0;
        _tabGroupDragAnchorX = 0;

        if (releaseCapture)
        {
            try
            {
                if (e is not null)
                {
                    TabScrollViewer.ReleasePointerCapture(e.Pointer);
                }
                else
                {
                    TabScrollViewer.ReleasePointerCaptures();
                }
            }
            catch
            {
                // The window manager can release capture first.
            }
        }

        if (!wasDragging && toggleIfClick && _tabGroups.Contains(group))
        {
            ToggleTabGroupCollapsed(group);
        }

        return true;
    }

    private void TabGroupHeader_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border { Tag: TabGroupState group })
        {
            group.IsPointerOver = true;
            UpdateTabGroupVisual(group);
        }
    }

    private void TabGroupHeader_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border { Tag: TabGroupState group })
        {
            group.IsPointerOver = false;
            UpdateTabGroupVisual(group);
        }
    }

    private readonly record struct TabGroupDialogResult(string Name, string ColorKey);

    private sealed record TabGroupColorOption(string Key, string DisplayName);

    private sealed class TabGroupState
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "New group";

        public string ColorKey { get; set; } = "Blue";

        public bool IsCollapsed { get; set; }

        public bool IsPinned { get; set; }

        public bool IsPointerOver { get; set; }

        public Border Header { get; set; } = null!;

        public Border ColorDot { get; set; } = null!;

        public TextBlock TitleText { get; set; } = null!;

        public TextBlock CountText { get; set; } = null!;

        public FontIcon PinIcon { get; set; } = null!;

        public FontIcon ChevronIcon { get; set; } = null!;

        public ScaleTransform HeaderTransform { get; set; } = null!;
    }
}
