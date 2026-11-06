using Aero.App.Data;
using Aero.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace Aero.App;

public sealed partial class MainWindow
{
    private const int MaximumAddressSuggestions = 8;
    private const int AddressSuggestionDelayMilliseconds = 28;

    private readonly Lazy<AddressSuggestionService> _addressSuggestionService = new(
        () => new AddressSuggestionService(
            TimeSpan.FromMilliseconds(AddressSuggestionDelayMilliseconds)),
        LazyThreadSafetyMode.None);

    private bool _suppressAddressSuggestionRefresh;
    private bool _addressSuggestionInteractionPending;

    private void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressAddressSuggestionRefresh || AddressBox.FocusState == FocusState.Unfocused)
        {
            return;
        }

        if (ActiveTab is BrowserTabState state)
        {
            state.AddressDraft = AddressBox.Text;
            state.HasAddressDraft = true;
        }

        ScheduleAddressSuggestionsRefresh(AddressBox.Text);
    }

    private async void ScheduleAddressSuggestionsRefresh(string rawQuery)
    {
        bool shouldRefresh;
        try
        {
            shouldRefresh = await _addressSuggestionService.Value.WaitForRefreshAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (!shouldRefresh ||
            AddressBox.FocusState == FocusState.Unfocused ||
            !string.Equals(AddressBox.Text, rawQuery, StringComparison.Ordinal))
        {
            return;
        }

        RefreshAddressSuggestions(rawQuery);
    }

    private void CancelPendingAddressSuggestionRefresh()
    {
        if (_addressSuggestionService.IsValueCreated)
        {
            _addressSuggestionService.Value.CancelPendingRefresh();
        }
    }

    private static void ClearAddressDraft(BrowserTabState state)
    {
        state.AddressDraft = string.Empty;
        state.HasAddressDraft = false;
    }

    private void SetAddressTextWithoutSuggestions(string value)
    {
        _suppressAddressSuggestionRefresh = true;
        try
        {
            AddressBox.Text = value;
        }
        finally
        {
            _suppressAddressSuggestionRefresh = false;
        }
    }

    private void RefreshAddressSuggestions(string? rawQuery)
    {
        if (AddressBox.FocusState == FocusState.Unfocused || RootGrid.XamlRoot is null)
        {
            HideAddressSuggestions();
            return;
        }

        List<AddressSuggestionItem> suggestions = BuildAddressSuggestions(rawQuery);
        if (suggestions.Count == 0)
        {
            HideAddressSuggestions();
            return;
        }

        AddressSuggestionsList.ItemsSource = suggestions;
        AddressSuggestionsList.SelectedIndex = -1;
        PositionAddressSuggestionsPopup();
        AddressSuggestionsPopup.IsOpen = true;
    }

    private List<AddressSuggestionItem> BuildAddressSuggestions(string? rawQuery)
    {
        AddressSuggestionService service = _addressSuggestionService.Value;
        long revision = _dataStore.LibraryRevision;
        if (service.Revision != revision)
        {
            service.Synchronize(revision, EnumerateAddressSuggestionSeeds());
        }

        IReadOnlyList<AddressSuggestion> matches = service.Search(
            rawQuery,
            MaximumAddressSuggestions);
        List<AddressSuggestionItem> suggestions = new(matches.Count);

        foreach (AddressSuggestion match in matches)
        {
            suggestions.Add(new AddressSuggestionItem
            {
                Uri = match.Uri,
                Title = match.Title,
                DisplayUrl = match.DisplayUrl,
                SourceLabel = GetAddressSuggestionSourceLabel(match.Source),
                Glyph = GetAddressSuggestionGlyph(match.Source)
            });
        }

        return suggestions;
    }

    private IEnumerable<AddressSuggestionSeed> EnumerateAddressSuggestionSeeds()
    {
        foreach (BookmarkEntry bookmark in _dataStore.Library.Bookmarks)
        {
            yield return new AddressSuggestionSeed(
                bookmark.Url,
                bookmark.Title,
                AddressSuggestionSource.Bookmark,
                bookmark.AddedAt);
        }

        foreach (HistoryEntry historyEntry in _dataStore.Library.History)
        {
            yield return new AddressSuggestionSeed(
                historyEntry.Url,
                historyEntry.Title,
                AddressSuggestionSource.History,
                historyEntry.VisitedAt);
        }
    }

    private static string GetAddressSuggestionSourceLabel(AddressSuggestionSource source) =>
        source switch
        {
            AddressSuggestionSource.Bookmark => "Bookmark",
            AddressSuggestionSource.Tab => "Tab",
            _ => "History"
        };

    private static string GetAddressSuggestionGlyph(AddressSuggestionSource source) =>
        source switch
        {
            AddressSuggestionSource.Bookmark => "\uE734",
            AddressSuggestionSource.Tab => "\uE8A7",
            _ => "\uE81C"
        };

    private void PositionAddressSuggestionsPopup()
    {
        try
        {
            Point addressPosition = AddressSurface
                .TransformToVisual(RootGrid)
                .TransformPoint(new Point(0, 0));

            double availableWidth = Math.Max(0, RootGrid.ActualWidth - 16);
            double popupWidth = Math.Min(AddressSurface.ActualWidth, availableWidth);
            double horizontalOffset = Math.Clamp(
                addressPosition.X,
                8,
                Math.Max(8, RootGrid.ActualWidth - popupWidth - 8));

            double top = addressPosition.Y + AddressSurface.ActualHeight + 4;
            double availableHeight = Math.Max(0, RootGrid.ActualHeight - top - 8);
            if (popupWidth < 80 || availableHeight < 56)
            {
                HideAddressSuggestions();
                return;
            }
            AddressSuggestionsPanel.Width = popupWidth;
            AddressSuggestionsList.MaxHeight = Math.Min(360, Math.Max(0, availableHeight - 14));
            AddressSuggestionsPopup.HorizontalOffset = horizontalOffset;
            AddressSuggestionsPopup.VerticalOffset = top;
        }
        catch
        {
            HideAddressSuggestions();
        }
    }

    private bool HandleAddressSuggestionKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && AddressSuggestionsPopup.IsOpen)
        {
            e.Handled = true;
            HideAddressSuggestions();
            return true;
        }

        if (!AddressSuggestionsPopup.IsOpen || AddressSuggestionsList.Items.Count == 0)
        {
            return false;
        }

        if (e.Key == VirtualKey.Down)
        {
            int nextIndex = AddressSuggestionsList.SelectedIndex < 0
                ? 0
                : Math.Min(AddressSuggestionsList.SelectedIndex + 1, AddressSuggestionsList.Items.Count - 1);
            SelectAddressSuggestion(nextIndex);
            e.Handled = true;
            return true;
        }

        if (e.Key == VirtualKey.Up)
        {
            int nextIndex = AddressSuggestionsList.SelectedIndex < 0
                ? AddressSuggestionsList.Items.Count - 1
                : Math.Max(AddressSuggestionsList.SelectedIndex - 1, 0);
            SelectAddressSuggestion(nextIndex);
            e.Handled = true;
            return true;
        }

        if (e.Key == VirtualKey.Enter &&
            AddressSuggestionsList.SelectedItem is AddressSuggestionItem suggestion)
        {
            e.Handled = true;
            OpenAddressSuggestion(suggestion);
            return true;
        }

        return false;
    }

    private void SelectAddressSuggestion(int index)
    {
        AddressSuggestionsList.SelectedIndex = index;
        AddressSuggestionsList.ScrollIntoView(AddressSuggestionsList.SelectedItem);
    }

    private void AddressSuggestionsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        _addressSuggestionInteractionPending = false;
        if (e.ClickedItem is AddressSuggestionItem suggestion)
        {
            OpenAddressSuggestion(suggestion);
        }
    }

    private async void AddressSuggestionsList_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        _addressSuggestionInteractionPending = true;
        await Task.Delay(500);
        _addressSuggestionInteractionPending = false;
    }

    private void OpenAddressSuggestion(AddressSuggestionItem suggestion)
    {
        if (ActiveTab is not BrowserTabState state)
        {
            HideAddressSuggestions();
            return;
        }

        HideAddressSuggestions();
        ClearAddressDraft(state);
        SetAddressTextWithoutSuggestions(suggestion.Uri.AbsoluteUri);
        Navigate(state, suggestion.Uri);
        state.Browser.Focus(FocusState.Programmatic);
    }

    private async void ScheduleHideAddressSuggestions()
    {
        await Task.Delay(220);

        if (!_addressSuggestionInteractionPending &&
            AddressBox.FocusState == FocusState.Unfocused)
        {
            HideAddressSuggestions();
        }
    }

    private void HideAddressSuggestions()
    {
        CancelPendingAddressSuggestionRefresh();
        AddressSuggestionsPopup.IsOpen = false;
        AddressSuggestionsList.SelectedIndex = -1;
        AddressSuggestionsList.ItemsSource = null;
    }

    private void DisposeAddressSuggestionService()
    {
        if (_addressSuggestionService.IsValueCreated)
        {
            _addressSuggestionService.Value.Dispose();
        }
    }

    private sealed class AddressSuggestionItem
    {
        public required Uri Uri { get; init; }

        public required string Title { get; init; }

        public required string DisplayUrl { get; init; }

        public required string SourceLabel { get; init; }

        public required string Glyph { get; init; }
    }
}
