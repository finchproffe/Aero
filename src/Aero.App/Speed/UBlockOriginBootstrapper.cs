using Aero.App.Data;
using Aero.Extensions;
using Microsoft.Web.WebView2.Core;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aero.App.Speed;

internal enum UBlockOriginState
{
    Idle,
    Checking,
    Downloading,
    Installing,
    Ready,
    Failed
}

/// <summary>
/// Installs the official Chromium build of uBlock Origin into Aero's WebView2
/// profile. The extension remains a separate GPL-3.0 component; Aero only
/// manages its download, local folder and browser-profile registration.
/// </summary>
internal sealed class UBlockOriginBootstrapper
{
    public const string Version = "1.72.2";
    public const string RepositoryUrl = "https://github.com/gorhill/uBlock";
    public const string ReleaseDownloadUrl =
        "https://github.com/gorhill/uBlock/releases/download/1.72.2/uBlock0_1.72.2.chromium.zip";
    public const string ReleaseSha256 =
        "d104cac4e1f48d76b1c3ff88aede2eaa8269814ae06cc9c850d27afb5274e533";

    private const string ExpectedNameToken = "ublock origin";
    private const string ExpectedOfficialId = "cjpalhdlnbpafiamejdnhcphjbkeiagm";

    private static readonly HttpClient Client = CreateClient();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly string _rootFolder = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Aero",
        "ThirdParty",
        "uBlockOrigin");

    private UBlockOriginState _state = UBlockOriginState.Idle;
    private string? _errorMessage;
    private CoreWebView2BrowserExtension? _extension;

    public event EventHandler? StateChanged;

    public UBlockOriginState State => _state;

    public string? ErrorMessage => _errorMessage;

    public CoreWebView2BrowserExtension? Extension => _extension;

    public bool IsReady => _state == UBlockOriginState.Ready && _extension?.IsEnabled == true;

    public string StatusText => _state switch
    {
        UBlockOriginState.Idle => "uBlock Origin engine is waiting for WebView2",
        UBlockOriginState.Checking => "Checking the built-in uBlock Origin engine…",
        UBlockOriginState.Downloading => $"Downloading uBlock Origin {Version} from GitHub…",
        UBlockOriginState.Installing => "Installing uBlock Origin into the Aero profile…",
        UBlockOriginState.Ready => $"uBlock Origin {Version} engine is active",
        UBlockOriginState.Failed => string.IsNullOrWhiteSpace(_errorMessage)
            ? "uBlock Origin could not be installed; Aero native fallback remains active"
            : $"uBlock Origin unavailable: {_errorMessage}",
        _ => "uBlock Origin engine status is unknown"
    };

    public async Task EnsureInstalledAsync(
        CoreWebView2Profile profile,
        ExtensionRegistry registry,
        CancellationToken cancellationToken = default,
        bool forceRedownload = false)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsReady && !forceRedownload)
            {
                return;
            }

            SetState(UBlockOriginState.Checking);
            _errorMessage = null;

            IReadOnlyList<CoreWebView2BrowserExtension> installed =
                await profile.GetBrowserExtensionsAsync();

            CoreWebView2BrowserExtension? existing = installed.FirstOrDefault(IsUBlockOrigin);
            if (existing is not null)
            {
                if (!existing.IsEnabled)
                {
                    await existing.EnableAsync(true);
                }

                _extension = existing;
                registry.SetPinned(existing.Id, true);
                SetState(UBlockOriginState.Ready);
                return;
            }

            string extensionFolder = System.IO.Path.Combine(_rootFolder, Version, "extension");
            if (forceRedownload || !IsValidPackage(extensionFolder))
            {
                await DownloadAndExtractAsync(extensionFolder, cancellationToken);
            }

            SetState(UBlockOriginState.Installing);
            CoreWebView2BrowserExtension extension =
                await profile.AddBrowserExtensionAsync(extensionFolder);

            if (!IsUBlockOrigin(extension))
            {
                try
                {
                    await extension.RemoveAsync();
                }
                catch
                {
                    // Best effort cleanup only.
                }

                throw new InvalidDataException(
                    "The downloaded extension did not identify itself as uBlock Origin.");
            }

            if (!extension.IsEnabled)
            {
                await extension.EnableAsync(true);
            }

            _extension = extension;
            registry.SetPath(extension.Id, extensionFolder);
            registry.SetPinned(extension.Id, true);
            SetState(UBlockOriginState.Ready);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _extension = null;
            _errorMessage = NormalizeError(exception);
            SetState(UBlockOriginState.Failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        if (_extension is null)
        {
            return;
        }

        await _extension.EnableAsync(enabled);
        SetState(enabled ? UBlockOriginState.Ready : UBlockOriginState.Idle);
    }

    private async Task DownloadAndExtractAsync(
        string extensionFolder,
        CancellationToken cancellationToken)
    {
        SetState(UBlockOriginState.Downloading);

        string versionFolder = Directory.GetParent(extensionFolder)?.FullName ?? _rootFolder;
        string stagingFolder = System.IO.Path.Combine(
            _rootFolder,
            ".staging-" + Guid.NewGuid().ToString("N"));
        string zipPath = System.IO.Path.Combine(stagingFolder, "uBlockOrigin.chromium.zip");
        string extractedFolder = System.IO.Path.Combine(stagingFolder, "extracted");

        Directory.CreateDirectory(stagingFolder);
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(
                ReleaseDownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (FileStream output = new(
                zipPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await response.Content.CopyToAsync(output, cancellationToken);
            }

            FileInfo archive = new(zipPath);
            if (archive.Length < 1_000_000)
            {
                throw new InvalidDataException("The uBlock Origin archive was incomplete.");
            }

            string actualDigest = await ComputeSha256Async(zipPath, cancellationToken);
            if (!actualDigest.Equals(ReleaseSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The uBlock Origin archive failed its SHA-256 integrity check.");
            }

            Directory.CreateDirectory(extractedFolder);
            ExtractArchiveSafely(zipPath, extractedFolder);

            string? manifestPath = Directory
                .EnumerateFiles(extractedFolder, "manifest.json", SearchOption.AllDirectories)
                .OrderBy(path => path.Count(ch => ch == System.IO.Path.DirectorySeparatorChar))
                .FirstOrDefault();

            if (manifestPath is null)
            {
                throw new InvalidDataException("manifest.json was not found in the uBlock Origin archive.");
            }

            string sourceFolder = Directory.GetParent(manifestPath)!.FullName;
            ValidateManifest(manifestPath);

            string replacementFolder = System.IO.Path.Combine(
                stagingFolder,
                "validated-extension");
            CopyDirectory(sourceFolder, replacementFolder);

            if (Directory.Exists(versionFolder))
            {
                Directory.Delete(versionFolder, recursive: true);
            }

            Directory.CreateDirectory(versionFolder);
            Directory.Move(replacementFolder, extensionFolder);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingFolder))
                {
                    Directory.Delete(stagingFolder, recursive: true);
                }
            }
            catch
            {
                // A locked temporary file can be removed on the next attempt.
            }
        }
    }


    private static async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static void ExtractArchiveSafely(string zipPath, string destinationFolder)
    {
        string destinationRoot = System.IO.Path.GetFullPath(destinationFolder) +
            System.IO.Path.DirectorySeparatorChar;

        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string destinationPath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(destinationFolder, entry.FullName));

            if (!destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The extension archive contained an unsafe path.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static void ValidateManifest(string manifestPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = document.RootElement;

        string name = root.TryGetProperty("name", out JsonElement nameElement)
            ? nameElement.GetString() ?? string.Empty
            : string.Empty;
        string version = root.TryGetProperty("version", out JsonElement versionElement)
            ? versionElement.GetString() ?? string.Empty
            : string.Empty;

        if (!name.Contains("uBlock", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded manifest was not uBlock Origin.");
        }

        if (!version.Equals(Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Expected uBlock Origin {Version}, but the archive contained {version}.");
        }
    }

    private static bool IsValidPackage(string extensionFolder)
    {
        string manifestPath = System.IO.Path.Combine(extensionFolder, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            ValidateManifest(manifestPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUBlockOrigin(CoreWebView2BrowserExtension extension) =>
        extension.Id.Equals(ExpectedOfficialId, StringComparison.OrdinalIgnoreCase) ||
        extension.Name.Contains(ExpectedNameToken, StringComparison.OrdinalIgnoreCase) ||
        extension.Name.Equals("uBlock Origin", StringComparison.OrdinalIgnoreCase);

    private static void CopyDirectory(string sourceFolder, string destinationFolder)
    {
        Directory.CreateDirectory(destinationFolder);

        foreach (string directory in Directory.EnumerateDirectories(
                     sourceFolder,
                     "*",
                     SearchOption.AllDirectories))
        {
            string relative = System.IO.Path.GetRelativePath(sourceFolder, directory);
            Directory.CreateDirectory(System.IO.Path.Combine(destinationFolder, relative));
        }

        foreach (string file in Directory.EnumerateFiles(
                     sourceFolder,
                     "*",
                     SearchOption.AllDirectories))
        {
            string relative = System.IO.Path.GetRelativePath(sourceFolder, file);
            string destination = System.IO.Path.Combine(destinationFolder, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromMinutes(3)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aero-Browser/0.12");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
        return client;
    }

    private static string NormalizeError(Exception exception)
    {
        string message = exception.Message.Trim();
        return message.Length <= 180 ? message : message[..177] + "…";
    }

    private void SetState(UBlockOriginState state)
    {
        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
