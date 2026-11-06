using System.Globalization;
using System.Text;

namespace Aero.Downloads;

public readonly record struct ExportedCookie(
    string Domain,
    string Path,
    bool IsSecure,
    bool IsHttpOnly,
    double? ExpiresUnixSeconds,
    string Name,
    string Value);

/// <summary>Netscape cookies.txt, the format yt-dlp's <c>--cookies</c> reads.</summary>
public static class NetscapeCookieFile
{
    public static string Format(IEnumerable<ExportedCookie> cookies)
    {
        StringBuilder builder = new("# Netscape HTTP Cookie File\n");
        foreach (ExportedCookie cookie in cookies)
        {
            // Tabs or newlines in any field would let one cookie forge extra lines/columns.
            if (string.IsNullOrEmpty(cookie.Domain) ||
                string.IsNullOrEmpty(cookie.Name) ||
                HasControl(cookie.Domain) || HasControl(cookie.Path) ||
                HasControl(cookie.Name) || HasControl(cookie.Value))
            {
                continue;
            }

            long expires = cookie.ExpiresUnixSeconds is double seconds && seconds > 0
                ? (long)seconds
                : 0;
            builder
                .Append(cookie.IsHttpOnly ? "#HttpOnly_" : string.Empty).Append(cookie.Domain).Append('\t')
                .Append(cookie.Domain.StartsWith('.') ? "TRUE" : "FALSE").Append('\t')
                .Append(string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path).Append('\t')
                .Append(cookie.IsSecure ? "TRUE" : "FALSE").Append('\t')
                .Append(expires.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(cookie.Name).Append('\t')
                .Append(cookie.Value).Append('\n');
        }

        return builder.ToString();
    }

    private static bool HasControl(string? value) =>
        value is not null && value.AsSpan().IndexOfAny('\t', '\r', '\n') >= 0;
}
