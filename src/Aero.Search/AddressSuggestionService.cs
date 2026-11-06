namespace Aero.Search;

public sealed class AddressSuggestionService : IDisposable
{
    private readonly AddressSuggestionIndex _index = new();
    private readonly SearchDebouncer _debouncer;

    public AddressSuggestionService(TimeSpan debounceDelay)
    {
        _debouncer = new SearchDebouncer(debounceDelay);
    }

    public long Revision => _index.Revision;

    public Task<bool> WaitForRefreshAsync() => _debouncer.WaitAsync();

    public void CancelPendingRefresh() => _debouncer.Cancel();

    public void Synchronize(long revision, IEnumerable<AddressSuggestionSeed> seeds) =>
        _index.Synchronize(revision, seeds);

    public IReadOnlyList<AddressSuggestion> Search(string? rawQuery, int maximumResults) =>
        _index.Search(rawQuery, maximumResults);

    public void Dispose() => _debouncer.Dispose();
}
