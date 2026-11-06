using System.Globalization;
using System.Text.Json;

namespace Aero.App.Localization;

internal static class AeroLocalizer
{
    private static readonly object Sync = new();
    private static IReadOnlyDictionary<string, string> _strings =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static string Language { get; private set; } = "en";

    public static void Configure(string? language)
    {
        string normalized = language?.Equals("ru", StringComparison.OrdinalIgnoreCase) == true
            ? "ru"
            : "en";
        lock (Sync)
        {
            Language = normalized;
            _strings = Load(normalized);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(
                normalized == "ru" ? "ru-RU" : "en-US");
        }
    }

    public static string Get(string key)
    {
        lock (Sync)
        {
            return _strings.TryGetValue(key, out string? value) ? value : key;
        }
    }

    public static string TranslateUi(string original)
    {
        lock (Sync)
        {
            return _strings.TryGetValue("Ui." + original, out string? value)
                ? value
                : original;
        }
    }

    public static string TranslateDocument(string document)
    {
        KeyValuePair<string, string>[] replacements;
        lock (Sync)
        {
            replacements = _strings
                .Where(pair => pair.Key.StartsWith("Html.", StringComparison.Ordinal))
                .OrderByDescending(pair => pair.Key.Length)
                .ToArray();
        }

        foreach (KeyValuePair<string, string> replacement in replacements)
        {
            document = document.Replace(
                replacement.Key[5..],
                replacement.Value,
                StringComparison.Ordinal);
        }
        return document;
    }

    public static string Format(string key, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), values);

    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        try
        {
            string path = Path.Combine(
                AppContext.BaseDirectory,
                "Localization",
                language + ".json");
            using FileStream stream = File.OpenRead(path);
            Dictionary<string, string>? strings =
                JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            return strings is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(strings, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
