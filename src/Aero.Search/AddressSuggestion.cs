namespace Aero.Search;

public sealed record AddressSuggestion(
    Uri Uri,
    string Title,
    string DisplayUrl,
    AddressSuggestionSource Source);
