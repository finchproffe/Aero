using System.Security.Cryptography;
using System.Text;

namespace Aero.Security.TestLab.Scanning;

internal sealed record MarkerPattern(string Name, byte[] Bytes);

internal sealed class MarkerSet
{
    private MarkerSet(string fingerprint, int markerLength, IReadOnlyList<MarkerPattern> patterns)
    {
        Fingerprint = fingerprint;
        MarkerLength = markerLength;
        Patterns = patterns;
        MaximumPatternLength = patterns.Max(pattern => pattern.Bytes.Length);
    }

    public string Fingerprint { get; }

    public int MarkerLength { get; }

    public IReadOnlyList<MarkerPattern> Patterns { get; }

    public int MaximumPatternLength { get; }

    public static MarkerSet Create(string marker)
    {
        if (string.IsNullOrWhiteSpace(marker))
        {
            throw new ArgumentException("The test marker is empty.", nameof(marker));
        }

        if (!marker.StartsWith("AERO_TEST_", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "For safety, TestLab only accepts markers beginning with AERO_TEST_.",
                nameof(marker));
        }

        if (marker.Length is < 16 or > 128)
        {
            throw new ArgumentException(
                "The test marker must contain between 16 and 128 characters.",
                nameof(marker));
        }

        if (marker.Any(character => character < 0x21 || character > 0x7E))
        {
            throw new ArgumentException(
                "The test marker may contain printable ASCII characters only.",
                nameof(marker));
        }

        byte[] utf8 = Encoding.UTF8.GetBytes(marker);
        byte[] utf16 = Encoding.Unicode.GetBytes(marker);
        byte[] base64Utf8 = Encoding.ASCII.GetBytes(Convert.ToBase64String(utf8));
        string fingerprint = Convert.ToHexString(SHA256.HashData(utf8))[..16];

        return new MarkerSet(
            fingerprint,
            marker.Length,
            [
                new MarkerPattern("UTF-8", utf8),
                new MarkerPattern("UTF-16LE", utf16),
                new MarkerPattern("Base64(UTF-8)", base64Utf8)
            ]);
    }

    public static string Generate(string kind)
    {
        string normalized = string.IsNullOrWhiteSpace(kind)
            ? "PASSWORD"
            : new string(kind
                .ToUpperInvariant()
                .Where(character => char.IsAsciiLetterOrDigit(character) || character == '_')
                .Take(24)
                .ToArray());

        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "PASSWORD";
        }

        return $"AERO_TEST_{normalized}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(12))}";
    }
}
