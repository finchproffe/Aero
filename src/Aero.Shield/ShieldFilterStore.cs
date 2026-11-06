using System.Net.Http;
using System.Text.Json;

namespace Aero.Shield;

internal sealed class ShieldFilterStore
{
    private const string EasyListUrl = "https://easylist.to/easylist/easylist.txt";
    private const string EasyPrivacyUrl = "https://easylist.to/easylist/easyprivacy.txt";

    private static readonly HttpClient Client = CreateHttpClient();
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _folder = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Aero",
        "Shield");

    private string EasyListFile => System.IO.Path.Combine(_folder, "easylist.txt");

    private string EasyPrivacyFile => System.IO.Path.Combine(_folder, "easyprivacy.txt");

    private string MetadataFile => System.IO.Path.Combine(_folder, "metadata.json");

    public FilterListBundle LoadCached()
    {
        Directory.CreateDirectory(_folder);

        string easyList = ReadFile(EasyListFile);
        string easyPrivacy = ReadFile(EasyPrivacyFile);
        ShieldFilterMetadata metadata = ReadMetadata();

        return new FilterListBundle(easyList, easyPrivacy, metadata.UpdatedAtUtc);
    }

    public static bool ShouldRefresh(DateTimeOffset? updatedAtUtc)
    {
        if (updatedAtUtc is null)
        {
            return true;
        }

        return DateTimeOffset.UtcNow - updatedAtUtc.Value > TimeSpan.FromHours(24);
    }

    public async Task<FilterListBundle> RefreshAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_folder);

        Task<string> easyListTask = DownloadAsync(EasyListUrl, cancellationToken);
        Task<string> easyPrivacyTask = DownloadAsync(EasyPrivacyUrl, cancellationToken);
        await Task.WhenAll(easyListTask, easyPrivacyTask);

        string easyList = await easyListTask;
        string easyPrivacy = await easyPrivacyTask;

        if (easyList.Length < 10_000 || easyPrivacy.Length < 5_000)
        {
            throw new InvalidDataException("The downloaded filter lists were incomplete.");
        }

        WriteAtomic(EasyListFile, easyList);
        WriteAtomic(EasyPrivacyFile, easyPrivacy);

        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;
        string metadataJson = JsonSerializer.Serialize(
            new ShieldFilterMetadata { UpdatedAtUtc = updatedAt },
            MetadataJsonOptions);
        WriteAtomic(MetadataFile, metadataJson);

        return new FilterListBundle(easyList, easyPrivacy, updatedAt);
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aero-Shield/0.13.1");
        return client;
    }

    private static async Task<string> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Client.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private ShieldFilterMetadata ReadMetadata()
    {
        try
        {
            if (!File.Exists(MetadataFile))
            {
                return new ShieldFilterMetadata();
            }

            return JsonSerializer.Deserialize<ShieldFilterMetadata>(File.ReadAllText(MetadataFile)) ??
                new ShieldFilterMetadata();
        }
        catch
        {
            return new ShieldFilterMetadata();
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, content);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed class ShieldFilterMetadata
    {
        public DateTimeOffset? UpdatedAtUtc { get; set; }
    }
}

internal readonly record struct FilterListBundle(
    string EasyList,
    string EasyPrivacy,
    DateTimeOffset? UpdatedAtUtc)
{
    public IEnumerable<string> EnumerateLists()
    {
        if (!string.IsNullOrWhiteSpace(EasyList))
        {
            yield return EasyList;
        }

        if (!string.IsNullOrWhiteSpace(EasyPrivacy))
        {
            yield return EasyPrivacy;
        }
    }
}
