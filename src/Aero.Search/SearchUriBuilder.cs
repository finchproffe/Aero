namespace Aero.Search;

public static class SearchUriBuilder
{
    private const string GoogleTemplate = "https://www.google.com/search?q={query}";
    private const string BingTemplate = "https://www.bing.com/search?q={query}";
    private const string DuckDuckGoTemplate = "https://duckduckgo.com/?q={query}";
    private const string BraveTemplate = "https://search.brave.com/search?q={query}";

    public static Uri Build(
        string? query,
        SearchEngineKind searchEngine,
        string? customSearchUrl)
    {
        string escapedQuery = Uri.EscapeDataString(query ?? string.Empty);
        string template = searchEngine switch
        {
            SearchEngineKind.Bing => BingTemplate,
            SearchEngineKind.DuckDuckGo => DuckDuckGoTemplate,
            SearchEngineKind.Brave => BraveTemplate,
            SearchEngineKind.Custom => string.IsNullOrWhiteSpace(customSearchUrl)
                ? GoogleTemplate
                : customSearchUrl.Trim(),
            _ => GoogleTemplate
        };

        if (!template.Contains("{query}", StringComparison.OrdinalIgnoreCase))
        {
            template += template.Contains('?')
                ? "&q={query}"
                : "?q={query}";
        }

        string address = template.Replace(
            "{query}",
            escapedQuery,
            StringComparison.OrdinalIgnoreCase);

        return Uri.TryCreate(address, UriKind.Absolute, out Uri? result) &&
               IsSupportedWebUri(result)
            ? result
            : new Uri(GoogleTemplate.Replace(
                "{query}",
                escapedQuery,
                StringComparison.Ordinal));
    }

    public static string GetAddressBarPlaceholder(SearchEngineKind searchEngine) =>
        searchEngine switch
        {
            SearchEngineKind.Bing => "Search Bing or enter an address",
            SearchEngineKind.DuckDuckGo => "Search DuckDuckGo or enter an address",
            SearchEngineKind.Brave => "Search Brave or enter an address",
            SearchEngineKind.Custom => "Search or enter an address",
            _ => "Search Google or enter an address"
        };

    private static bool IsSupportedWebUri(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
