using Aero.Search;

VerifyAddressResolution();
VerifySearchEngineSelection();
VerifySuggestionRanking();
await VerifyDebounceCancellationAsync();

Console.WriteLine("Aero.Search smoke tests passed.");

static void VerifyAddressResolution()
{
    Uri direct = AddressInputResolver.Resolve(
        "https://example.com/path",
        SearchEngineKind.Google,
        null).Uri;
    AssertEqual("https://example.com/path", direct.AbsoluteUri.TrimEnd('/'), "absolute URL");

    Uri host = AddressInputResolver.Resolve(
        "example.com/docs",
        SearchEngineKind.Google,
        null).Uri;
    AssertEqual("https://example.com/docs", host.AbsoluteUri.TrimEnd('/'), "bare host");

    Uri local = AddressInputResolver.Resolve(
        "localhost:8080",
        SearchEngineKind.Google,
        null).Uri;
    AssertEqual("http://localhost:8080", local.AbsoluteUri.TrimEnd('/'), "local address");
}

static void VerifySearchEngineSelection()
{
    Uri bing = SearchUriBuilder.Build("aero browser", SearchEngineKind.Bing, null);
    AssertEqual(
        "https://www.bing.com/search?q=aero%20browser",
        bing.AbsoluteUri,
        "Bing search");

    Uri custom = SearchUriBuilder.Build(
        "aero browser",
        SearchEngineKind.Custom,
        "https://search.example/find/{query}");
    AssertEqual(
        "https://search.example/find/aero%20browser",
        custom.AbsoluteUri,
        "custom search");
}

static void VerifySuggestionRanking()
{
    DateTimeOffset now = DateTimeOffset.UtcNow;
    AddressSuggestionIndex index = new();
    index.Synchronize(
        1,
        [
            new AddressSuggestionSeed(
                "https://example.com/",
                "Example history",
                AddressSuggestionSource.History,
                now),
            new AddressSuggestionSeed(
                "https://example.com/",
                "Example bookmark",
                AddressSuggestionSource.Bookmark,
                now),
            new AddressSuggestionSeed(
                "https://docs.example.net/",
                "Aero documentation",
                AddressSuggestionSource.History,
                now.AddMinutes(-2))
        ]);

    IReadOnlyList<AddressSuggestion> results = index.Search("example", 8);
    Assert(results.Count == 2, "expected two deduplicated suggestions");
    Assert(results[0].Source == AddressSuggestionSource.Bookmark, "bookmark should win duplicate URL");
    AssertEqual("Example bookmark", results[0].Title, "bookmark title");
}

static async Task VerifyDebounceCancellationAsync()
{
    using SearchDebouncer debouncer = new(TimeSpan.FromMilliseconds(15));
    Task<bool> first = debouncer.WaitAsync();
    Task<bool> second = debouncer.WaitAsync();

    bool firstResult = await first;
    bool secondResult = await second;

    Assert(!firstResult, "older debounce request must be cancelled");
    Assert(secondResult, "latest debounce request must continue");
}

static void AssertEqual(string expected, string actual, string label)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"{label}: expected '{expected}', actual '{actual}'.");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
