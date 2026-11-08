using Aero.App.Data;
using Aero.Extensions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aero.App;

public sealed partial class MainWindow
{
    private const long MaximumCrxBytes = 256L * 1024 * 1024;
    private const long MaximumCrxExtractedBytes = 768L * 1024 * 1024;
    private static readonly HttpClient ExtensionPackageClient =
        CreateExtensionPackageClient();

    // Chrome Web Store only: "Add to Chrome" goes to Aero instead of WebView2's own installer,
    // whose prompt is followed by a download that WebView2 itself interrupts.
    private const string ExtensionStoreBridgeScript = """
        (() => {
          if (location.hostname !== 'chromewebstore.google.com' ||
              !window.chrome || !chrome.webstorePrivate || !chrome.webview) {
            return;
          }
          chrome.webstorePrivate.beginInstallWithManifest3 = (details, callback) => {
            chrome.webview.postMessage({ type: 'aero-store-install', id: String((details && details.id) || '') });
            // 'user_cancelled' quietly resets the store button; Aero's own dialog takes over.
            if (typeof callback === 'function') {
              setTimeout(() => callback('user_cancelled'), 0);
              return undefined;
            }
            return Promise.resolve('user_cancelled');
          };
        })();
        """;

    private readonly HashSet<string> _storeInstallsInProgress = new(StringComparer.Ordinal);

    private async Task RegisterExtensionStoreBridgeAsync(BrowserTabState state)
    {
        if (state.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ExtensionStoreBridgeScript);
        }
        catch
        {
            // The controller may be replaced while a discarded tab is restored.
        }
    }

    private static bool IsStoreInstallRequest(string? source, string extensionId) =>
        Uri.TryCreate(source, UriKind.Absolute, out Uri? page) &&
        page.Scheme == Uri.UriSchemeHttps &&
        page.Host.Equals("chromewebstore.google.com", StringComparison.OrdinalIgnoreCase) &&
        extensionId.Length == 32 &&
        extensionId.All(character => character is >= 'a' and <= 'p');

    /// <summary>Downloads the package from Google's delivery server and asks with Aero's dialog.</summary>
    private async Task InstallStoreExtensionAsync(BrowserTabState state, string extensionId)
    {
        if (_isPrivateWindow)
        {
            await ShowErrorDialogAsync(
                "Extension installation is unavailable",
                "Install extensions from a regular Aero window. Private windows use a temporary WebView2 profile.");
            return;
        }

        // A double click on "Add to Chrome" must not open two dialogs.
        if (!_storeInstallsInProgress.Add(extensionId))
        {
            return;
        }

        try
        {
            string downloadFolder = Path.Combine(_browserProfile.StorageRoot, "ExtensionDownloads");
            Directory.CreateDirectory(downloadFolder);
            string packagePath = Path.Combine(downloadFolder, $"store-{Guid.NewGuid():N}.crx");
            ExtensionsStatusText.Text = "Downloading extension package from the official store…";
            if (!await TryDownloadExtensionPackageAsync(ChromeStoreCrxUri(extensionId), packagePath))
            {
                TryDeleteFile(packagePath);
                ExtensionsStatusText.Text = "Extension download failed.";
                await ShowErrorDialogAsync(
                    "Could not download extension",
                    "The official Chrome Web Store delivery server did not return the extension package.");
                return;
            }

            await InstallDownloadedStorePackageAsync(packagePath, state, extensionId);
        }
        finally
        {
            _storeInstallsInProgress.Remove(extensionId);
        }
    }

    private void CleanupPendingExtensionDownloads()
    {
        string folder = Path.Combine(
            _browserProfile.StorageRoot,
            "ExtensionDownloads");
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(folder, "*.crx"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-6))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // An active antivirus scan can keep a stale package briefly locked.
            }
        }
    }

    private async Task<bool> TryHandleExtensionStoreDownloadAsync(
        BrowserTabState state,
        CoreWebView2DownloadStartingEventArgs args)
    {
        if (!IsOfficialExtensionStoreDownload(state, args))
        {
            return false;
        }

        if (_isPrivateWindow)
        {
            args.Cancel = true;
            await ShowErrorDialogAsync(
                "Extension installation is unavailable",
                "Install extensions from a regular Aero window. Private windows use a temporary WebView2 profile.");
            return true;
        }

        string downloadFolder = Path.Combine(
            _browserProfile.StorageRoot,
            "ExtensionDownloads");
        Directory.CreateDirectory(downloadFolder);
        string packagePath = Path.Combine(
            downloadFolder,
            $"store-{Guid.NewGuid():N}.crx");

        args.ResultFilePath = packagePath;
        RegisterExtensionStoreDownload(args.DownloadOperation, packagePath, state);
        ExtensionsStatusText.Text = "Downloading extension package from the official store…";
        return true;
    }

    private void RegisterExtensionStoreDownload(
        CoreWebView2DownloadOperation operation,
        string packagePath,
        BrowserTabState sourceTab)
    {
        bool completed = false;

        void StateChanged(object? sender, object args)
        {
            if (completed ||
                operation.State == CoreWebView2DownloadState.InProgress)
            {
                return;
            }

            completed = true;
            operation.StateChanged -= StateChanged;
            ReleaseDownloadOperationClaim(operation);
            RootGrid.DispatcherQueue.TryEnqueue(() =>
            {
                _ = CompleteExtensionStoreDownloadAsync(operation, packagePath, sourceTab);
            });
        }

        operation.StateChanged += StateChanged;
        StateChanged(null, EventArgs.Empty);
    }

    private async Task CompleteExtensionStoreDownloadAsync(
        CoreWebView2DownloadOperation operation,
        string packagePath,
        BrowserTabState sourceTab)
    {
        if (operation.State != CoreWebView2DownloadState.Completed)
        {
            ExtensionsStatusText.Text =
                "WebView2 interrupted the download. Retrying from the official extension server…";
            if (!await TryRecoverInterruptedExtensionDownloadAsync(
                    operation.Uri,
                    packagePath,
                    sourceTab))
            {
                TryDeleteFile(packagePath);
                ExtensionsStatusText.Text =
                    $"Extension download stopped: {operation.InterruptReason}.";
                await ShowErrorDialogAsync(
                    "Could not download extension",
                    "The Web Store interrupted the download and the secure retry from its official delivery server also failed.");
                return;
            }
        }

        await InstallDownloadedStorePackageAsync(packagePath, sourceTab, GetExpectedStoreExtensionId(sourceTab));
    }

    private async Task InstallDownloadedStorePackageAsync(
        string packagePath,
        BrowserTabState sourceTab,
        string? expectedExtensionId)
    {
        string stagingRoot = Path.Combine(
            Path.GetTempPath(),
            "Aero",
            "ExtensionInstall",
            Guid.NewGuid().ToString("N"));
        string managedPath = string.Empty;

        try
        {
            FileInfo packageFile = new(packagePath);
            if (!packageFile.Exists || packageFile.Length is <= 0 or > MaximumCrxBytes)
            {
                throw new InvalidDataException("The extension package has an invalid size.");
            }

            Directory.CreateDirectory(stagingRoot);
            byte[]? packagePublicKey = ExtractCrxPackage(packagePath, stagingRoot);
            string extensionRoot = FindExtensionRoot(stagingRoot);
            EnsureManifestExtensionKey(extensionRoot, packagePublicKey);
            ExtensionPackage package = ExtensionCatalog.ReadPackage(extensionRoot) ??
                throw new InvalidDataException(
                    "The downloaded package does not contain a valid Chromium manifest.");

            if (!await ConfirmStoreExtensionInstallAsync(package))
            {
                ExtensionsStatusText.Text = "Extension installation cancelled.";
                return;
            }

            CoreWebView2Profile? profile = _tabs.Contains(sourceTab)
                ? sourceTab.Browser.CoreWebView2?.Profile
                : null;
            profile ??= GetActiveProfile();
            if (profile is null)
            {
                throw new InvalidOperationException(
                    "The browser profile is not ready for extension installation.");
            }

            managedPath = CreateManagedExtensionPath(package.Name);
            CopyDirectory(extensionRoot, managedPath);
            CoreWebView2BrowserExtension extension =
                await profile.AddBrowserExtensionAsync(managedPath);

            if (!string.IsNullOrWhiteSpace(expectedExtensionId) &&
                !extension.Id.Equals(expectedExtensionId, StringComparison.OrdinalIgnoreCase))
            {
                await extension.RemoveAsync();
                throw new InvalidDataException(
                    $"The extension was installed with ID {extension.Id}, but the Web Store ID is {expectedExtensionId}. " +
                    "Aero could not preserve the package signing identity.");
            }

            string? previousPath = _extensionRegistry.GetPath(extension.Id);
            _extensionRegistry.SetPath(extension.Id, managedPath);
            if (!string.IsNullOrWhiteSpace(previousPath) &&
                !previousPath.Equals(managedPath, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteManagedExtensionFolder(previousPath);
            }

            await OpenExtensionsPaneAsync();
            ExtensionsStatusText.Text = $"{extension.Name} installed from the Web Store";
        }
        catch (Exception exception)
        {
            TryDeleteManagedExtensionFolder(managedPath);
            ExtensionsStatusText.Text = "Extension installation failed.";
            await ShowErrorDialogAsync(
                "Could not install extension",
                exception.Message);
        }
        finally
        {
            TryDeleteFile(packagePath);
            TryDeleteDirectory(stagingRoot);
        }
    }

    private async Task<bool> ConfirmStoreExtensionInstallAsync(ExtensionPackage package)
    {
        string permissionText = package.Permissions.Count == 0
            ? "The manifest does not request named permissions."
            : "Requested permissions:\n" +
                string.Join(", ", package.Permissions.Take(16));

        ContentDialog dialog = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"Install {package.Name}?",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Version {package.Version}",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    },
                    new TextBlock
                    {
                        Text = permissionText,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text =
                            "Extensions can read or change browser data allowed by their manifest. " +
                            "Install only extensions you trust.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.72
                    }
                }
            },
            PrimaryButtonText = "Install",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<bool> TryRecoverInterruptedExtensionDownloadAsync(
        string source,
        string packagePath,
        BrowserTabState sourceTab)
    {
        List<Uri> candidates = [];
        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? sourceUri) &&
            IsTrustedExtensionDeliveryUri(sourceUri))
        {
            candidates.Add(sourceUri);
        }

        AddOfficialStoreRecoveryUri(sourceTab.Browser.Source, candidates);
        AddOfficialStoreRecoveryUri(sourceTab.CurrentUri, candidates);
        AddOfficialStoreRecoveryUri(sourceTab.PendingUri, candidates);

        foreach (Uri candidate in candidates.DistinctBy(uri => uri.AbsoluteUri))
        {
            if (await TryDownloadExtensionPackageAsync(candidate, packagePath))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> TryDownloadExtensionPackageAsync(
        Uri sourceUri,
        string packagePath)
    {
        try
        {
            using HttpResponseMessage response = await ExtensionPackageClient.GetAsync(
                sourceUri,
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            Uri? finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is null || !IsTrustedExtensionDeliveryUri(finalUri))
            {
                return false;
            }

            long? expectedBytes = response.Content.Headers.ContentLength;
            if (expectedBytes > MaximumCrxBytes)
            {
                return false;
            }

            await using Stream input = await response.Content.ReadAsStreamAsync();
            await using FileStream output = new(
                packagePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);

            byte[] buffer = new byte[81920];
            long written = 0;
            long nextProgressUpdate = 512 * 1024;
            while (true)
            {
                int read = await input.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                written = checked(written + read);
                if (written > MaximumCrxBytes)
                {
                    throw new InvalidDataException(
                        "The extension package exceeds Aero's download limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read));
                if (written >= nextProgressUpdate)
                {
                    ExtensionsStatusText.Text =
                        $"Retrying extension download… {FormatBytes(written)}";
                    nextProgressUpdate = written + 512 * 1024;
                }
            }

            await output.FlushAsync();
            return written > 0;
        }
        catch
        {
            TryDeleteFile(packagePath);
            return false;
        }
    }

    private static void AddOfficialStoreRecoveryUri(
        Uri? storePage,
        ICollection<Uri> candidates)
    {
        if (!TryGetStoreExtensionId(storePage, out string extensionId))
        {
            return;
        }

        if (storePage!.Host.Equals(
                "chromewebstore.google.com",
                StringComparison.OrdinalIgnoreCase) ||
            storePage.Host.Equals(
                "chrome.google.com",
                StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(ChromeStoreCrxUri(extensionId));
        }
    }

    private static Uri ChromeStoreCrxUri(string extensionId) => new(
        "https://clients2.google.com/service/update2/crx" +
        "?response=redirect" +
        "&prodversion=150.0.0.0" +
        "&acceptformat=crx2,crx3" +
        $"&x={Uri.EscapeDataString($"id={extensionId}&installsource=ondemand&uc")}");

    private static bool TryGetStoreExtensionId(Uri? storePage, out string extensionId)
    {
        extensionId = string.Empty;
        if (!IsOfficialExtensionStorePage(storePage))
        {
            return false;
        }

        string[] segments = storePage!.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        for (int index = segments.Length - 1; index >= 0; index--)
        {
            string candidate = Uri.UnescapeDataString(segments[index]).Trim();
            if (candidate.Length == 32 &&
                candidate.All(character => character is >= 'a' and <= 'p'))
            {
                extensionId = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsOfficialExtensionStoreDownload(
        BrowserTabState state,
        CoreWebView2DownloadStartingEventArgs args)
    {
        // Do not trust only CurrentUri here. During store redirects it can lag
        // behind the live WebView source, which previously made Aero treat a
        // CRX package as a normal download. Check every known navigation value.
        bool isStorePage =
            IsOfficialExtensionStorePage(state.Browser.Source) ||
            IsOfficialExtensionStorePage(state.CurrentUri) ||
            IsOfficialExtensionStorePage(state.PendingUri);
        if (!isStorePage)
        {
            return false;
        }

        string suggestedPath = args.ResultFilePath ?? string.Empty;
        if (Path.GetExtension(suggestedPath)
            .Equals(".crx", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Uri.TryCreate(
                args.DownloadOperation.Uri,
                UriKind.Absolute,
                out Uri? downloadUri))
        {
            return false;
        }

        // Chrome Web Store can expose the package either through its delivery
        // hosts or as a blob: URL owned by the official store page. Both are
        // extension installs, not user downloads.
        return IsTrustedExtensionDeliveryUri(downloadUri) ||
            IsOfficialExtensionStoreBlobUri(downloadUri);
    }

    private static bool IsOfficialExtensionStoreBlobUri(Uri uri)
    {
        if (!uri.Scheme.Equals("blob", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        const string BlobPrefix = "blob:";
        string original = uri.OriginalString;
        if (!original.StartsWith(BlobPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Uri.TryCreate(
                original[BlobPrefix.Length..],
                UriKind.Absolute,
                out Uri? ownerUri) &&
            IsOfficialExtensionStorePage(ownerUri);
    }

    private static bool IsTrustedExtensionDeliveryUri(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string host = uri.Host;
        return host.Equals("clients2.google.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".gvt1.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("chromewebstore.google.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("chrome.google.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("edge.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".edge.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("microsoftedge.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".microsoftedge.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".msedge.net", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateExtensionPackageClient()
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/150.0.0.0 Safari/537.36 Aero/0.22");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
        return client;
    }

    private static bool IsOfficialExtensionStorePage(Uri? uri)
    {
        if (uri is null ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.Host.Equals("chromewebstore.google.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("chrome.google.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith("/webstore", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("microsoftedge.microsoft.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith("/addons", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[]? ExtractCrxPackage(
        string packagePath,
        string destinationRoot)
    {
        using FileStream package = new(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using BinaryReader reader = new(package, Encoding.UTF8, leaveOpen: true);

        byte[] signature = reader.ReadBytes(4);
        long zipOffset;
        byte[]? publicKey = null;
        if (signature.SequenceEqual("Cr24"u8.ToArray()))
        {
            uint version = reader.ReadUInt32();
            switch (version)
            {
                case 2:
                {
                    uint publicKeyLength = reader.ReadUInt32();
                    uint signatureLength = reader.ReadUInt32();
                    if (publicKeyLength is 0 or > 1024 * 1024 ||
                        signatureLength > MaximumCrxBytes)
                    {
                        throw new InvalidDataException("The CRX2 header is invalid.");
                    }

                    publicKey = reader.ReadBytes(checked((int)publicKeyLength));
                    if (publicKey.Length != publicKeyLength)
                    {
                        throw new EndOfStreamException("The CRX2 public key is truncated.");
                    }

                    zipOffset = checked(16L + publicKeyLength + signatureLength);
                    break;
                }
                case 3:
                {
                    uint headerLength = reader.ReadUInt32();
                    if (headerLength is 0 or > 16 * 1024 * 1024)
                    {
                        throw new InvalidDataException("The CRX3 header is invalid.");
                    }

                    byte[] header = reader.ReadBytes(checked((int)headerLength));
                    if (header.Length != headerLength)
                    {
                        throw new EndOfStreamException("The CRX3 header is truncated.");
                    }

                    publicKey = TryReadCrx3PublicKey(header) ??
                        throw new InvalidDataException(
                            "The CRX3 package does not contain a public key matching its signed extension ID.");
                    zipOffset = checked(12L + headerLength);
                    break;
                }
                default:
                    throw new InvalidDataException(
                        $"Unsupported CRX format version {version}.");
            }
        }
        else if (signature.Length >= 2 &&
                 signature[0] == (byte)'P' &&
                 signature[1] == (byte)'K')
        {
            zipOffset = 0;
        }
        else
        {
            throw new InvalidDataException("The downloaded file is not a CRX package.");
        }

        if (zipOffset < 0 || zipOffset >= package.Length)
        {
            throw new InvalidDataException("The CRX header is invalid.");
        }

        package.Position = zipOffset;
        using MemoryStream zipBytes = new();
        package.CopyTo(zipBytes);
        if (zipBytes.Length <= 0 || zipBytes.Length > MaximumCrxBytes)
        {
            throw new InvalidDataException("The CRX archive is too large.");
        }

        zipBytes.Position = 0;
        using ZipArchive archive = new(zipBytes, ZipArchiveMode.Read);
        long extractedBytes = 0;
        string rootFull = Path.GetFullPath(destinationRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.Length == 0)
            {
                continue;
            }

            string normalizedEntry = entry.FullName.Replace('\\', '/');
            string[] segments = normalizedEntry.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries);
            if (normalizedEntry.StartsWith("/", StringComparison.Ordinal) ||
                normalizedEntry.Length > 1024 ||
                segments.Any(segment =>
                    segment is "." or ".." ||
                    segment.Contains(':') ||
                    segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                throw new InvalidDataException(
                    "The extension contains an unsafe archive entry.");
            }

            extractedBytes = checked(extractedBytes + entry.Length);
            if (entry.Length > MaximumCrxBytes ||
                extractedBytes > MaximumCrxExtractedBytes)
            {
                throw new InvalidDataException(
                    "The extension expands beyond Aero's safety limit.");
            }

            string destination = Path.GetFullPath(Path.Combine(
                rootFull,
                normalizedEntry.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The extension contains an unsafe file path.");
            }

            if (normalizedEntry.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            string? directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using Stream input = entry.Open();
            using FileStream output = new(
                destination,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);
            input.CopyTo(output);
        }

        return publicKey;
    }

    private static byte[]? TryReadCrx3PublicKey(ReadOnlySpan<byte> header)
    {
        int offset = 0;
        List<byte[]> proofKeys = [];
        byte[]? declaredCrxId = null;

        while (offset < header.Length)
        {
            if (!TryReadProtobufVarint(header, ref offset, out ulong tag))
            {
                return null;
            }

            int fieldNumber = checked((int)(tag >> 3));
            int wireType = (int)(tag & 7);
            if ((fieldNumber == 2 || fieldNumber == 3) && wireType == 2)
            {
                if (!TryReadProtobufBytes(header, ref offset, out ReadOnlySpan<byte> proof))
                {
                    return null;
                }

                byte[]? key = TryReadAsymmetricKeyProofPublicKey(proof);
                if (key is { Length: > 0 })
                {
                    proofKeys.Add(key);
                }

                continue;
            }

            if (fieldNumber == 10000 && wireType == 2)
            {
                if (!TryReadProtobufBytes(
                        header,
                        ref offset,
                        out ReadOnlySpan<byte> signedHeaderData))
                {
                    return null;
                }

                declaredCrxId = TryReadCrx3Id(signedHeaderData);
                continue;
            }

            if (!TrySkipProtobufField(header, ref offset, wireType))
            {
                return null;
            }
        }

        if (declaredCrxId is { Length: 16 })
        {
            foreach (byte[] key in proofKeys)
            {
                byte[] keyHash = SHA256.HashData(key);
                if (CryptographicOperations.FixedTimeEquals(
                        keyHash.AsSpan(0, declaredCrxId.Length),
                        declaredCrxId))
                {
                    return key;
                }
            }

            // A CRX3 can contain both the developer proof and a Web Store proof.
            // Never use the first proof blindly: that gives an unrelated unpacked
            // extension ID and breaks the identity check after installation.
            return null;
        }

        // Legacy/non-standard CRX3 producers sometimes omit SignedData. Only a
        // single proof is unambiguous enough to use in that case.
        return proofKeys.Count == 1 ? proofKeys[0] : null;
    }

    private static byte[]? TryReadCrx3Id(ReadOnlySpan<byte> signedHeaderData)
    {
        int offset = 0;
        while (offset < signedHeaderData.Length)
        {
            if (!TryReadProtobufVarint(
                    signedHeaderData,
                    ref offset,
                    out ulong tag))
            {
                return null;
            }

            int fieldNumber = checked((int)(tag >> 3));
            int wireType = (int)(tag & 7);
            if (fieldNumber == 1 && wireType == 2)
            {
                if (!TryReadProtobufBytes(
                        signedHeaderData,
                        ref offset,
                        out ReadOnlySpan<byte> crxId) ||
                    crxId.Length != 16)
                {
                    return null;
                }

                return crxId.ToArray();
            }

            if (!TrySkipProtobufField(signedHeaderData, ref offset, wireType))
            {
                return null;
            }
        }

        return null;
    }

    private static byte[]? TryReadAsymmetricKeyProofPublicKey(
        ReadOnlySpan<byte> proof)
    {
        int offset = 0;
        while (offset < proof.Length)
        {
            if (!TryReadProtobufVarint(proof, ref offset, out ulong tag))
            {
                return null;
            }

            int fieldNumber = checked((int)(tag >> 3));
            int wireType = (int)(tag & 7);
            if (fieldNumber == 1 && wireType == 2)
            {
                return TryReadProtobufBytes(
                    proof,
                    ref offset,
                    out ReadOnlySpan<byte> key)
                    ? key.ToArray()
                    : null;
            }

            if (!TrySkipProtobufField(proof, ref offset, wireType))
            {
                return null;
            }
        }

        return null;
    }

    private static bool TryReadProtobufBytes(
        ReadOnlySpan<byte> data,
        ref int offset,
        out ReadOnlySpan<byte> value)
    {
        value = default;
        if (!TryReadProtobufVarint(data, ref offset, out ulong rawLength) ||
            rawLength > int.MaxValue)
        {
            return false;
        }

        int length = (int)rawLength;
        if (length < 0 || offset > data.Length - length)
        {
            return false;
        }

        value = data.Slice(offset, length);
        offset += length;
        return true;
    }

    private static bool TryReadProtobufVarint(
        ReadOnlySpan<byte> data,
        ref int offset,
        out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64 && offset < data.Length; shift += 7)
        {
            byte current = data[offset++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TrySkipProtobufField(
        ReadOnlySpan<byte> data,
        ref int offset,
        int wireType)
    {
        switch (wireType)
        {
            case 0:
                return TryReadProtobufVarint(data, ref offset, out _);
            case 1:
                if (offset > data.Length - 8)
                {
                    return false;
                }

                offset += 8;
                return true;
            case 2:
                return TryReadProtobufBytes(data, ref offset, out _);
            case 5:
                if (offset > data.Length - 4)
                {
                    return false;
                }

                offset += 4;
                return true;
            default:
                return false;
        }
    }

    private static void EnsureManifestExtensionKey(
        string extensionRoot,
        byte[]? publicKey)
    {
        if (publicKey is not { Length: > 0 })
        {
            return;
        }

        string manifestPath = Path.Combine(extensionRoot, "manifest.json");
        JsonNode? root = JsonNode.Parse(
            File.ReadAllText(manifestPath),
            documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        if (root is not JsonObject manifest)
        {
            throw new InvalidDataException("The extension manifest is invalid.");
        }

        string packageIdentity = GetExtensionIdFromPublicKey(publicKey);
        if (manifest["key"] is JsonValue existingKey &&
            existingKey.TryGetValue(out string? existingText) &&
            !string.IsNullOrWhiteSpace(existingText))
        {
            try
            {
                byte[] existingPublicKey = Convert.FromBase64String(existingText);
                if (GetExtensionIdFromPublicKey(existingPublicKey).Equals(
                        packageIdentity,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch (FormatException)
            {
                // Replace an invalid or unrelated manifest key with the key that
                // is cryptographically tied to the CRX3 signed extension ID.
            }
        }

        manifest["key"] = Convert.ToBase64String(publicKey);
        File.WriteAllText(
            manifestPath,
            manifest.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string GetExtensionIdFromPublicKey(ReadOnlySpan<byte> publicKey)
    {
        byte[] hash = SHA256.HashData(publicKey);
        char[] extensionId = new char[32];
        for (int index = 0; index < 16; index++)
        {
            extensionId[index * 2] = (char)('a' + (hash[index] >> 4));
            extensionId[index * 2 + 1] = (char)('a' + (hash[index] & 0x0F));
        }

        return new string(extensionId);
    }

    private static string? GetExpectedStoreExtensionId(BrowserTabState state)
    {
        Uri?[] candidates =
        [
            state.Browser.Source,
            state.CurrentUri,
            state.PendingUri
        ];
        foreach (Uri? candidate in candidates)
        {
            if (TryGetStoreExtensionId(candidate, out string extensionId))
            {
                return extensionId;
            }
        }

        return null;
    }

    private static string FindExtensionRoot(string stagingRoot)
    {
        string directManifest = Path.Combine(stagingRoot, "manifest.json");
        if (File.Exists(directManifest))
        {
            return stagingRoot;
        }

        string? manifest = Directory.EnumerateFiles(
                stagingRoot,
                "manifest.json",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}__MACOSX{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path.Count(character =>
                character == Path.DirectorySeparatorChar))
            .FirstOrDefault();
        return manifest is null
            ? throw new InvalidDataException(
                "The extension archive does not contain manifest.json.")
            : Path.GetDirectoryName(manifest)!;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A failed temporary cleanup can be retried on the next launch.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Windows can hold an antivirus scanning handle briefly.
        }
    }
}
