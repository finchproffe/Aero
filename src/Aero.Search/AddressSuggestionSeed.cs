namespace Aero.Search;

public sealed record AddressSuggestionSeed(
    string Url,
    string? Title,
    AddressSuggestionSource Source,
    DateTimeOffset Timestamp);
