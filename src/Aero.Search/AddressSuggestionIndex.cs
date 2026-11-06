namespace Aero.Search;

public sealed class AddressSuggestionIndex
{
    private const int DefaultMaximumResults = 8;

    private Dictionary<string, SearchEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private SearchEntry[] _snapshot = [];
    private long _revision = -1;

    public long Revision => _revision;

    public int Count => _snapshot.Length;

    public void Synchronize(long revision, IEnumerable<AddressSuggestionSeed> seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);

        if (revision == _revision)
        {
            return;
        }

        Dictionary<string, SearchEntry> next =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (AddressSuggestionSeed seed in seeds)
        {
            if (!TryCreateEntry(seed, out SearchEntry? candidate) || candidate is null)
            {
                continue;
            }

            string key = candidate.Suggestion.Uri.AbsoluteUri;
            if (next.TryGetValue(key, out SearchEntry? current) &&
                !ShouldReplace(current, candidate))
            {
                continue;
            }

            // Reuse the existing immutable normalized entry whenever the source
            // data did not change. Library revisions then update the index
            // incrementally instead of rebuilding every normalized string.
            if (_entries.TryGetValue(key, out SearchEntry? existing) &&
                existing.HasSameSourceData(candidate))
            {
                next[key] = existing;
            }
            else
            {
                next[key] = candidate;
            }
        }

        SearchEntry[] snapshot = new SearchEntry[next.Count];
        next.Values.CopyTo(snapshot, 0);

        _entries = next;
        _snapshot = snapshot;
        _revision = revision;
    }

    public IReadOnlyList<AddressSuggestion> Search(
        string? rawQuery,
        int maximumResults = DefaultMaximumResults)
    {
        if (maximumResults <= 0 || _snapshot.Length == 0)
        {
            return [];
        }

        string query = NormalizeQuery(rawQuery);
        string[] queryWords = query.Contains(' ')
            ? query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];

        List<RankedEntry> ranked = new(Math.Min(maximumResults, _snapshot.Length));
        foreach (SearchEntry candidate in _snapshot)
        {
            int score = Score(query, queryWords, candidate);
            if (score < 0)
            {
                continue;
            }

            RankedEntry item = new(candidate, score);
            int insertionIndex = 0;
            while (insertionIndex < ranked.Count &&
                   !ShouldRankBefore(item, ranked[insertionIndex]))
            {
                insertionIndex++;
            }

            if (insertionIndex >= maximumResults)
            {
                continue;
            }

            ranked.Insert(insertionIndex, item);
            if (ranked.Count > maximumResults)
            {
                ranked.RemoveAt(ranked.Count - 1);
            }
        }

        List<AddressSuggestion> suggestions = new(ranked.Count);
        foreach (RankedEntry entry in ranked)
        {
            suggestions.Add(entry.Candidate.Suggestion);
        }

        return suggestions;
    }

    private static bool TryCreateEntry(
        AddressSuggestionSeed seed,
        out SearchEntry? entry)
    {
        if (!Uri.TryCreate(seed.Url, UriKind.Absolute, out Uri? uri) ||
            !IsSupportedWebUri(uri))
        {
            entry = null;
            return false;
        }

        string displayUrl = FormatUrl(uri);
        string resolvedTitle = string.IsNullOrWhiteSpace(seed.Title)
            ? uri.Host
            : seed.Title.Trim();
        string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;

        entry = new SearchEntry(
            new AddressSuggestion(uri, resolvedTitle, displayUrl, seed.Source),
            host.ToLowerInvariant(),
            displayUrl.ToLowerInvariant(),
            resolvedTitle.ToLowerInvariant(),
            seed.Source,
            seed.Timestamp);
        return true;
    }

    private static bool ShouldReplace(SearchEntry current, SearchEntry candidate) =>
        candidate.Source > current.Source ||
        candidate.Timestamp > current.Timestamp;

    private static bool ShouldRankBefore(RankedEntry left, RankedEntry right)
    {
        if (left.Score != right.Score)
        {
            return left.Score > right.Score;
        }

        if (left.Candidate.Source != right.Candidate.Source)
        {
            return left.Candidate.Source > right.Candidate.Source;
        }

        return left.Candidate.Timestamp > right.Candidate.Timestamp;
    }

    private static int Score(
        string query,
        string[] queryWords,
        SearchEntry candidate)
    {
        if (query.Length == 0)
        {
            return 100;
        }

        if (candidate.NormalizedHost.Equals(query, StringComparison.Ordinal))
        {
            return 1000;
        }

        if (candidate.NormalizedUrl.Equals(query, StringComparison.Ordinal))
        {
            return 980;
        }

        if (candidate.NormalizedHost.StartsWith(query, StringComparison.Ordinal))
        {
            return 930;
        }

        if (candidate.NormalizedUrl.StartsWith(query, StringComparison.Ordinal))
        {
            return 900;
        }

        if (candidate.NormalizedTitle.StartsWith(query, StringComparison.Ordinal))
        {
            return 860;
        }

        if (candidate.NormalizedHost.Contains(query, StringComparison.Ordinal))
        {
            return 800;
        }

        if (candidate.NormalizedUrl.Contains(query, StringComparison.Ordinal))
        {
            return 760;
        }

        if (candidate.NormalizedTitle.Contains(query, StringComparison.Ordinal))
        {
            return 720;
        }

        if (queryWords.Length > 1 && AllWordsMatch(queryWords, candidate))
        {
            return 680;
        }

        return -1;
    }

    private static bool AllWordsMatch(string[] queryWords, SearchEntry candidate)
    {
        foreach (string word in queryWords)
        {
            if (!candidate.NormalizedTitle.Contains(word, StringComparison.Ordinal) &&
                !candidate.NormalizedUrl.Contains(word, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeQuery(string? value)
    {
        string query = (value ?? string.Empty).Trim().ToLowerInvariant();

        if (query.StartsWith("https://", StringComparison.Ordinal))
        {
            query = query[8..];
        }
        else if (query.StartsWith("http://", StringComparison.Ordinal))
        {
            query = query[7..];
        }

        if (query.StartsWith("www.", StringComparison.Ordinal))
        {
            query = query[4..];
        }

        return query;
    }

    private static string FormatUrl(Uri uri)
    {
        string value = uri.AbsoluteUri;
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = value[8..];
        }
        else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            value = value[7..];
        }

        if (value.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
        }

        if (value.EndsWith('/') &&
            uri.Query.Length == 0 &&
            uri.Fragment.Length == 0)
        {
            value = value[..^1];
        }

        return value;
    }

    private static bool IsSupportedWebUri(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private readonly record struct RankedEntry(SearchEntry Candidate, int Score);

    private sealed record SearchEntry(
        AddressSuggestion Suggestion,
        string NormalizedHost,
        string NormalizedUrl,
        string NormalizedTitle,
        AddressSuggestionSource Source,
        DateTimeOffset Timestamp)
    {
        public bool HasSameSourceData(SearchEntry other) =>
            Source == other.Source &&
            Timestamp == other.Timestamp &&
            Suggestion.Title.Equals(other.Suggestion.Title, StringComparison.Ordinal) &&
            Suggestion.DisplayUrl.Equals(other.Suggestion.DisplayUrl, StringComparison.Ordinal);
    }
}
