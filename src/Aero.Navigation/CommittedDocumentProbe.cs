using System.Text.Json;

namespace Aero.Navigation;

public static class CommittedDocumentProbe
{
    public const string Script =
        "(() => [location.href, document.readyState, " +
        "document.title || '', document.documentElement?.outerHTML?.length || 0])()";

    public static bool TryReadUsableDocumentUri(string? json, out Uri? documentUri)
    {
        documentUri = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 4)
            {
                return false;
            }

            string? href = root[0].ValueKind == JsonValueKind.String
                ? root[0].GetString()
                : null;
            string? readyState = root[1].ValueKind == JsonValueKind.String
                ? root[1].GetString()
                : null;
            int documentLength = root[3].ValueKind == JsonValueKind.Number &&
                root[3].TryGetInt32(out int parsedLength)
                    ? parsedLength
                    : 0;

            bool usable = Uri.TryCreate(href, UriKind.Absolute, out Uri? parsedUri) &&
                IsBrowsableUri(parsedUri) &&
                documentLength > 32 &&
                !string.Equals(
                    readyState,
                    "uninitialized",
                    StringComparison.OrdinalIgnoreCase);
            if (!usable)
            {
                return false;
            }

            documentUri = parsedUri;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsBrowsableUri(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
