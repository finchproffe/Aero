namespace Aero.Navigation;

public readonly record struct NavigationDocumentClassification(
    bool IsAboutBlank,
    bool IsDataHtmlDocument,
    bool IsSyntheticInternalNavigation,
    bool IsAeroHomeDocumentNavigation)
{
    public bool ShouldClearInternalPageAddress =>
        !IsSyntheticInternalNavigation &&
        !IsAeroHomeDocumentNavigation &&
        !IsAboutBlank;
}

public static class NavigationDocumentClassifier
{
    public static NavigationDocumentClassification Classify(
        string? uri,
        bool hasInternalPageAddress,
        bool isStartPage)
    {
        string value = uri ?? string.Empty;
        bool isAboutBlank = value.Equals(
            "about:blank",
            StringComparison.OrdinalIgnoreCase);
        bool isDataHtmlDocument = value.StartsWith(
            "data:text/html",
            StringComparison.OrdinalIgnoreCase);
        bool isSyntheticInternalNavigation =
            hasInternalPageAddress && (isAboutBlank || isDataHtmlDocument);
        bool isAeroHomeDocumentNavigation =
            isStartPage && (isAboutBlank || isDataHtmlDocument);

        return new NavigationDocumentClassification(
            isAboutBlank,
            isDataHtmlDocument,
            isSyntheticInternalNavigation,
            isAeroHomeDocumentNavigation);
    }
}
