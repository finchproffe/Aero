using Aero.Core.Navigation;

namespace Aero.Search;

public static class AddressInputResolver
{
    private static readonly Uri DefaultHomeUri = new("https://www.google.com/");

    public static NavigationTarget Resolve(
        string? rawInput,
        SearchEngineKind searchEngine,
        string? customSearchUrl)
    {
        string input = rawInput?.Trim() ?? string.Empty;

        if (input.Length == 0)
        {
            return new NavigationTarget(DefaultHomeUri, false);
        }

        if (TryCreateSupportedAbsoluteUri(input, out Uri? absoluteUri) && absoluteUri is not null)
        {
            return new NavigationTarget(absoluteUri, false);
        }

        if (LooksLikeLocalAddress(input) &&
            Uri.TryCreate($"http://{input}", UriKind.Absolute, out Uri? localUri))
        {
            return new NavigationTarget(localUri, false);
        }

        if (LooksLikeHostName(input) &&
            Uri.TryCreate($"https://{input}", UriKind.Absolute, out Uri? hostUri))
        {
            return new NavigationTarget(hostUri, false);
        }

        return new NavigationTarget(
            SearchUriBuilder.Build(input, searchEngine, customSearchUrl),
            true);
    }

    private static bool TryCreateSupportedAbsoluteUri(string input, out Uri? uri)
    {
        if (Uri.TryCreate(input, UriKind.Absolute, out Uri? candidate) &&
            (candidate.Scheme == Uri.UriSchemeHttp || candidate.Scheme == Uri.UriSchemeHttps))
        {
            uri = candidate;
            return true;
        }

        uri = null;
        return false;
    }

    private static bool LooksLikeLocalAddress(string input)
    {
        return !input.Contains(' ') &&
               (input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("127.", StringComparison.Ordinal) ||
                input.StartsWith("[::1]", StringComparison.Ordinal));
    }

    private static bool LooksLikeHostName(string input)
    {
        if (input.Contains(' ') || !input.Contains('.'))
        {
            return false;
        }

        ReadOnlySpan<char> hostPart = input.AsSpan();
        int slashIndex = hostPart.IndexOf('/');
        if (slashIndex >= 0)
        {
            hostPart = hostPart[..slashIndex];
        }

        int portSeparatorIndex = hostPart.LastIndexOf(':');
        if (portSeparatorIndex > 0)
        {
            hostPart = hostPart[..portSeparatorIndex];
        }

        return Uri.CheckHostName(hostPart.ToString()) != UriHostNameType.Unknown;
    }
}
