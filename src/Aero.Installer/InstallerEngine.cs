using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace Aero.Installer;

internal sealed class InstallerEngine : IDisposable
{
    private const string PackageResourceName = "Aero.Installer.Payload.Aero.msix";
    private const string CertificateResourceName = "Aero.Installer.Payload.Aero.cer";
    private const string SecurityDriverPanelResourceName =
        "Aero.Installer.Payload.AeroSecurityDriverPanel.zip";
    private const string SecurityDriverPanelExecutableName =
        "Aero.Security.Driver.Setup.exe";
    private const string PackageName = "Aero.Browser";
    private const string LegacyPackageName = "Aero.Browser.Test";
    private readonly InstallerStrings _strings;
    private string? _workingDirectory;
    private string? _packagePath;
    private string? _certificatePath;
    private X509Certificate2? _certificate;
    private bool _initialized;
    private bool _disposed;

    public InstallerEngine(InstallerStrings strings)
    {
        _strings = strings;
    }

    public InstallerPackageInfo? Package { get; private set; }
    public InstalledPackageInfo? InstalledPackage { get; private set; }
    public bool SecurityDriverPanelAvailable =>
        Assembly.GetExecutingAssembly().GetManifestResourceInfo(
            SecurityDriverPanelResourceName) is not null;

    public static bool IsAdministrator
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public async Task<InstallerInitializationResult> InitializeAsync(
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CleanupWorkingDirectory();

        progress.Report(new InstallerProgress(6, _strings.Extracting));
        _workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "AeroInstaller",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workingDirectory);
        _packagePath = Path.Combine(_workingDirectory, "Aero.msix");
        _certificatePath = Path.Combine(_workingDirectory, "Aero.cer");

        await ExtractResourceAsync(
            PackageResourceName,
            _packagePath,
            cancellationToken);
        await ExtractResourceAsync(
            CertificateResourceName,
            _certificatePath,
            cancellationToken);

        progress.Report(new InstallerProgress(35, _strings.ReadingPackage));
        Package = await Task.Run(
            () => ReadPackageInfo(_packagePath),
            cancellationToken);

        progress.Report(new InstallerProgress(58, _strings.CheckingCertificate));
        _certificate?.Dispose();
        _certificate = X509CertificateLoader.LoadCertificateFromFile(_certificatePath);
        ValidateCertificateAndSigner(Package, _certificate, _packagePath);

        progress.Report(new InstallerProgress(78, _strings.ReadingInstalledVersion));
        InstalledPackage = await GetInstalledPackageAsync(cancellationToken);
        _initialized = true;

        return new InstallerInitializationResult(
            Package,
            InstalledPackage,
            SecurityDriverPanelAvailable);
    }

    public async Task<InstalledPackageInfo> InstallAsync(
        InstallerOptions options,
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_initialized ||
            Package is null ||
            _certificate is null ||
            string.IsNullOrWhiteSpace(_packagePath))
        {
            throw new InvalidOperationException("The Aero installer payload is not initialized.");
        }
        if (!IsAdministrator)
        {
            throw new UnauthorizedAccessException(
                "Administrator permission is required to install Aero.");
        }

        bool installBrowser =
            InstalledPackage is null ||
            InstalledPackage.Version.CompareTo(Package.Version) < 0;
        InstalledPackageInfo? installed = InstalledPackage;

        if (installBrowser)
        {
            progress.Report(new InstallerProgress(8, _strings.TrustingCertificate));
            TrustCertificate(_certificate, StoreLocation.LocalMachine);
            TrustCertificate(_certificate, StoreLocation.CurrentUser);

            progress.Report(new InstallerProgress(20, _strings.VerifyingSignature));
            int trustStatus = NativeSignatureVerifier.Verify(_packagePath);
            if (trustStatus != 0)
            {
                throw new InvalidDataException(
                    $"Windows rejected the Aero MSIX signature (0x{trustStatus:X8}).");
            }

            progress.Report(new InstallerProgress(32, _strings.ClosingAero));
            await StopRunningAeroAsync(cancellationToken);

            progress.Report(new InstallerProgress(48, _strings.InstallingPackage));
            await InstallPackageAsync(_packagePath, cancellationToken);

            progress.Report(new InstallerProgress(72, _strings.VerifyingInstall));
            installed = await GetInstalledPackageAsync(cancellationToken);
            if (installed is null || installed.Version != Package.Version)
            {
                throw new InvalidDataException(
                    $"Windows did not register the expected Aero version {Package.Version}.");
            }
            InstalledPackage = installed;
        }

        if (options.InstallSecurityDriverPanel)
        {
            progress.Report(new InstallerProgress(84, _strings.InstallingSecurityDriverPanel));
            await InstallSecurityDriverPanelAsync(cancellationToken);
        }

        installed ??= await GetInstalledPackageAsync(cancellationToken);
        if (installed is null)
        {
            throw new InvalidDataException("Aero is not installed after setup completed.");
        }
        InstalledPackage = installed;

        if (options.LaunchAfterInstall)
        {
            progress.Report(new InstallerProgress(94, _strings.StartingAero));
            LaunchPackage(installed.PackageFamilyName);
        }
        if (options.OpenDefaultApps)
        {
            OpenDefaultAppsSettings();
        }

        progress.Report(new InstallerProgress(100, _strings.Complete));
        return installed;
    }

    public static bool TryRelaunchElevated(
        InstallerOptions options,
        out string? error)
    {
        error = null;
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            error = "The installer executable path is unavailable.";
            return false;
        }

        try
        {
            string arguments = "--install";
            if (!options.LaunchAfterInstall)
            {
                arguments += " --no-launch";
            }
            if (options.OpenDefaultApps)
            {
                arguments += " --open-defaults";
            }
            if (options.InstallSecurityDriverPanel)
            {
                arguments += " --install-security-panel";
            }

            Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
            });
            return process is not null;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return false;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static async Task ExtractResourceAsync(
        string resourceName,
        string destination,
        CancellationToken cancellationToken)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        await using Stream source = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException(
                $"The embedded installer resource {resourceName} is missing.");
        await using FileStream target = new(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(target, 1024 * 128, cancellationToken);
        await target.FlushAsync(cancellationToken);
    }

    private static InstallerPackageInfo ReadPackageInfo(string packagePath)
    {
        using FileStream stream = File.OpenRead(packagePath);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        ZipArchiveEntry manifestEntry = archive.GetEntry("AppxManifest.xml")
            ?? throw new InvalidDataException("The Aero MSIX has no AppxManifest.xml.");
        using Stream manifestStream = manifestEntry.Open();
        XDocument manifest = XDocument.Load(manifestStream, LoadOptions.None);
        XElement identity = manifest.Root?.Elements().FirstOrDefault(
            element => element.Name.LocalName == "Identity")
            ?? throw new InvalidDataException("The Aero package identity is missing.");

        string name = identity.Attribute("Name")?.Value ?? string.Empty;
        string publisher = identity.Attribute("Publisher")?.Value ?? string.Empty;
        string architecture = identity.Attribute("ProcessorArchitecture")?.Value ?? string.Empty;
        string versionText = identity.Attribute("Version")?.Value ?? string.Empty;
        if (!string.Equals(name, PackageName, StringComparison.Ordinal) ||
            !Version.TryParse(versionText, out Version? version) ||
            !string.Equals(architecture, "x64", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The embedded MSIX is not the expected x64 Aero package.");
        }

        return new InstallerPackageInfo(name, publisher, version, architecture);
    }

    private static void ValidateCertificateAndSigner(
        InstallerPackageInfo package,
        X509Certificate2 certificate,
        string packagePath)
    {
        DateTime now = DateTime.Now;
        if (now < certificate.NotBefore || now > certificate.NotAfter)
        {
            throw new InvalidDataException(
                $"The Aero certificate is not valid at the current computer time. " +
                $"Valid from {certificate.NotBefore:u} until {certificate.NotAfter:u}.");
        }
        if (!string.Equals(
                package.Publisher,
                certificate.Subject,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The MSIX publisher does not match the embedded Aero certificate.");
        }

        bool codeSigningAllowed = certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .SelectMany(extension => extension.EnhancedKeyUsages.Cast<Oid>())
            .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.3");
        if (!codeSigningAllowed)
        {
            throw new InvalidDataException("The Aero certificate is not valid for code signing.");
        }

#pragma warning disable SYSLIB0057 // The loader has no signed-file equivalent; WinVerifyTrust validates the digest after trust is installed.
        using X509Certificate signedFileCertificate = X509Certificate.CreateFromSignedFile(packagePath);
#pragma warning restore SYSLIB0057
        using X509Certificate2 signer = X509CertificateLoader.LoadCertificate(
            signedFileCertificate.GetRawCertData());
        if (!string.Equals(
                signer.Thumbprint,
                certificate.Thumbprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The embedded certificate does not match the MSIX signer.");
        }
    }

    private static void TrustCertificate(
        X509Certificate2 certificate,
        StoreLocation location)
    {
        using X509Store store = new(StoreName.TrustedPeople, location);
        store.Open(OpenFlags.ReadWrite);
        X509Certificate2Collection existing = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            certificate.Thumbprint,
            validOnly: false);
        try
        {
            if (existing.Count == 0)
            {
                store.Add(certificate);
            }
        }
        finally
        {
            foreach (X509Certificate2 item in existing)
            {
                item.Dispose();
            }
        }
    }

    private async Task InstallSecurityDriverPanelAsync(
        CancellationToken cancellationToken)
    {
        if (!SecurityDriverPanelAvailable || string.IsNullOrWhiteSpace(_workingDirectory))
        {
            throw new InvalidDataException(
                "The Aero Security Driver Panel payload is not embedded in this installer.");
        }

        string archivePath = Path.Combine(
            _workingDirectory,
            "AeroSecurityDriverPanel.zip");
        string stagingDirectory = Path.Combine(
            _workingDirectory,
            "AeroSecurityDriverPanel");

        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }

        await ExtractResourceAsync(
            SecurityDriverPanelResourceName,
            archivePath,
            cancellationToken);
        Directory.CreateDirectory(stagingDirectory);
        await ExtractZipSafelyAsync(
            archivePath,
            stagingDirectory,
            cancellationToken);

        string? setupExecutable = Directory
            .EnumerateFiles(
                stagingDirectory,
                SecurityDriverPanelExecutableName,
                SearchOption.AllDirectories)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(setupExecutable))
        {
            throw new InvalidDataException(
                $"{SecurityDriverPanelExecutableName} is missing from the embedded panel package.");
        }

        string sourceDirectory = Path.GetDirectoryName(setupExecutable)
            ?? throw new InvalidDataException(
                "The Aero Security Driver Panel package root could not be determined.");
        string installDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Aero",
            "Security Driver Panel");

        await StopSecurityDriverPanelAsync(cancellationToken);
        await Task.Run(
            () => ReplaceDirectoryTransactional(sourceDirectory, installDirectory),
            cancellationToken);

        string installedExecutable = Path.Combine(
            installDirectory,
            SecurityDriverPanelExecutableName);
        await CreateSecurityDriverPanelShortcutAsync(
            installedExecutable,
            cancellationToken);

        _ = Process.Start(new ProcessStartInfo
        {
            FileName = installedExecutable,
            WorkingDirectory = installDirectory,
            UseShellExecute = true
        });
    }

    private static async Task ExtractZipSafelyAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        string destinationRoot =
            Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;
        using FileStream archiveStream = File.OpenRead(archivePath);
        using ZipArchive archive = new(archiveStream, ZipArchiveMode.Read);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = entry.FullName.Replace(
                '/',
                Path.DirectorySeparatorChar);
            string destinationPath = Path.GetFullPath(
                Path.Combine(destinationDirectory, relativePath));
            if (!destinationPath.StartsWith(
                    destinationRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The Aero Security Driver Panel archive contains an unsafe path.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(
                Path.GetDirectoryName(destinationPath)
                ?? destinationDirectory);
            await using Stream source = entry.Open();
            await using FileStream target = new(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 128,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, 1024 * 128, cancellationToken);
            await target.FlushAsync(cancellationToken);
        }
    }

    private static void ReplaceDirectoryTransactional(
        string sourceDirectory,
        string destinationDirectory)
    {
        string? destinationParent = Path.GetDirectoryName(destinationDirectory);
        if (string.IsNullOrWhiteSpace(destinationParent))
        {
            throw new InvalidDataException(
                "The Aero Security Driver Panel install path is invalid.");
        }

        Directory.CreateDirectory(destinationParent);
        string backupDirectory = destinationDirectory +
            ".backup-" + Guid.NewGuid().ToString("N");
        bool backupCreated = false;
        try
        {
            if (Directory.Exists(destinationDirectory))
            {
                Directory.Move(destinationDirectory, backupDirectory);
                backupCreated = true;
            }

            CopyDirectory(sourceDirectory, destinationDirectory);
            if (backupCreated && Directory.Exists(backupDirectory))
            {
                Directory.Delete(backupDirectory, recursive: true);
            }
        }
        catch
        {
            if (Directory.Exists(destinationDirectory))
            {
                Directory.Delete(destinationDirectory, recursive: true);
            }
            if (backupCreated && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, destinationDirectory);
            }
            throw;
        }
    }

    private static void CopyDirectory(
        string sourceDirectory,
        string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (string directory in Directory.EnumerateDirectories(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
        }
        foreach (string file in Directory.EnumerateFiles(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDirectory, file);
            string destination = Path.Combine(destinationDirectory, relative);
            Directory.CreateDirectory(
                Path.GetDirectoryName(destination) ?? destinationDirectory);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static async Task StopSecurityDriverPanelAsync(
        CancellationToken cancellationToken)
    {
        foreach (Process process in Process.GetProcessesByName(
                     "Aero.Security.Driver.Setup"))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }
                    _ = process.CloseMainWindow();
                    using CancellationTokenSource timeout =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                        when (!cancellationToken.IsCancellationRequested)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(cancellationToken);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The panel closed between enumeration and shutdown.
                }
            }
        }
    }

    private static async Task CreateSecurityDriverPanelShortcutAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        string shortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            "Programs",
            "Aero",
            "Aero Security Driver Panel.lnk");
        string workingDirectory = Path.GetDirectoryName(executablePath)
            ?? Environment.CurrentDirectory;
        string script = $$"""
$ErrorActionPreference = 'Stop'
$shortcutPath = '{{EscapePowerShellLiteral(shortcutPath)}}'
$targetPath = '{{EscapePowerShellLiteral(executablePath)}}'
$workingDirectory = '{{EscapePowerShellLiteral(workingDirectory)}}'
$shortcutDirectory = Split-Path -Parent $shortcutPath
New-Item -ItemType Directory -Path $shortcutDirectory -Force | Out-Null
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $targetPath
$shortcut.WorkingDirectory = $workingDirectory
$shortcut.IconLocation = $targetPath + ',0'
$shortcut.Description = 'Aero Security Driver Panel'
$shortcut.Save()
""";
        PowerShellResult result = await RunPowerShellAsync(script, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.StandardError)
                    ? "The Aero Security Driver Panel shortcut could not be created."
                    : result.StandardError.Trim());
        }
    }

    private static async Task StopRunningAeroAsync(CancellationToken cancellationToken)
    {
        foreach (Process process in Process.GetProcessesByName("Aero"))
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.HasExited)
                    {
                        continue;
                    }

                    _ = process.CloseMainWindow();
                    using CancellationTokenSource gracefulTimeout =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    gracefulTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        await process.WaitForExitAsync(gracefulTimeout.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(cancellationToken);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited between enumeration and shutdown.
                }
            }
        }

        await Task.Delay(250, cancellationToken);
    }

    private static async Task InstallPackageAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        string escapedPackage = EscapePowerShellLiteral(packagePath);
        string script = $$"""
$ErrorActionPreference = 'Stop'
Get-AppxPackage -Name '{{LegacyPackageName}}' -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction SilentlyContinue }
Add-AppxPackage -Path '{{escapedPackage}}' -ForceApplicationShutdown -ForceUpdateFromAnyVersion
""";
        PowerShellResult result = await RunPowerShellAsync(script, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.StandardError)
                    ? "Windows package deployment failed."
                    : result.StandardError.Trim());
        }
    }

    private static async Task<InstalledPackageInfo?> GetInstalledPackageAsync(
        CancellationToken cancellationToken)
    {
        string script = $$"""
$package = Get-AppxPackage -Name '{{PackageName}}' -ErrorAction SilentlyContinue |
    Sort-Object Version -Descending |
    Select-Object -First 1
if ($null -ne $package) {
    [Console]::Out.Write($package.Version.ToString() + '|' + $package.PackageFamilyName)
}
""";
        PowerShellResult result = await RunPowerShellAsync(script, cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        string[] fields = result.StandardOutput.Trim().Split('|', 2);
        return fields.Length == 2 && Version.TryParse(fields[0], out Version? version)
            ? new InstalledPackageInfo(version, fields[1])
            : null;
    }

    private static async Task<PowerShellResult> RunPowerShellAsync(
        string script,
        CancellationToken cancellationToken)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        string powerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = powerShellPath,
                Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("Windows PowerShell could not be started.");
        }

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new PowerShellResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }

    private static string EscapePowerShellLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static void LaunchPackage(string packageFamilyName)
    {
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"shell:AppsFolder\\{packageFamilyName}!App",
            UseShellExecute = true
        });
    }

    private static void OpenDefaultAppsSettings()
    {
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = "ms-settings:defaultapps",
            UseShellExecute = true
        });
    }

    private void CleanupWorkingDirectory()
    {
        _certificate?.Dispose();
        _certificate = null;
        if (string.IsNullOrWhiteSpace(_workingDirectory))
        {
            return;
        }

        try
        {
            string fullPath = Path.GetFullPath(_workingDirectory);
            string expectedRoot = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "AeroInstaller")) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch
        {
            // Windows may still hold a transient package handle; temp cleanup is best effort.
        }

        _workingDirectory = null;
        _packagePath = null;
        _certificatePath = null;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CleanupWorkingDirectory();
        _disposed = true;
    }

    private sealed record PowerShellResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}

internal sealed record InstallerPackageInfo(
    string Name,
    string Publisher,
    Version Version,
    string Architecture);

internal sealed record InstalledPackageInfo(
    Version Version,
    string PackageFamilyName);

internal sealed record InstallerInitializationResult(
    InstallerPackageInfo Package,
    InstalledPackageInfo? InstalledPackage,
    bool SecurityDriverPanelAvailable);

internal sealed record InstallerOptions(
    bool LaunchAfterInstall,
    bool OpenDefaultApps,
    bool InstallSecurityDriverPanel);

internal sealed record InstallerProgress(
    int Percent,
    string Message,
    string? Detail = null);
