using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace Aero.Security.Driver.Setup;

[SupportedOSPlatform("windows")]
internal static class DriverInstaller
{
    private const string ServiceName = "AeroSecurityDriver";
    private const string DisplayName = "Aero Security Driver";
    private const string DriverFileName = "Aero.Security.Driver.sys";
    private const string ServiceBinaryPath = @"\SystemRoot\System32\drivers\Aero.Security.Driver.sys";

    public static DriverStatusResult InstallOrRepair(
        string packageDirectory,
        bool installBootSplash,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        log("Checking VM-only installation policy...");
        string vm = VmGuard.RequireSupportedVirtualMachine();
        log($"Virtual machine detected: {vm}");

        string driverSource = FindSingleFile(packageDirectory, DriverFileName);
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string driverDestination = Path.Combine(windowsDirectory, "System32", "drivers", DriverFileName);
        string backupPath = driverDestination + ".aero-backup";
        bool serviceExisted = ServiceExists();
        bool destinationExisted = File.Exists(driverDestination);

        log($"Driver payload: {driverSource}");
        log($"Destination: {driverDestination}");

        InstallCertificates(packageDirectory, log);
        VerifyAuthenticodeSignature(driverSource);
        log("Authenticode signature verification: PASS");

        cancellationToken.ThrowIfCancellationRequested();
        StopServiceIfPresent(log);

        if (destinationExisted)
        {
            File.Copy(driverDestination, backupPath, overwrite: true);
            log("Existing driver was backed up for rollback.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(driverDestination)!);
            File.Copy(driverSource, driverDestination, overwrite: true);
            log("Signed driver copied into System32\\drivers.");

            EnsureServiceInstalled(log);
            StartAndWaitForService(log, cancellationToken);

            DriverStatusResult status = DriverProtocol.PingAndGetVersion();
            log($"PING nonce: 0x{status.PingNonce:X16}");
            log($"Driver version: {status.Version}");
            log($"Capabilities: 0x{status.Capabilities:X8}");
            log($"Build type: {(status.DebugBuild ? "Debug" : "Release")}");
            LogProtectionStatus(status, log);

            if (installBootSplash)
            {
                try
                {
                    InstallBootSplashIfPresent(packageDirectory, log, cancellationToken);
                }
                catch (Exception bootSplashException)
                {
                    log($"BootSplash warning: {bootSplashException.Message}");
                    log("The kernel driver remains installed and verified.");
                }
            }

            TryDelete(backupPath, log);
            log("Aero Security Driver installation completed successfully.");
            return status;
        }
        catch
        {
            log("Installation failed. Starting rollback...");
            try
            {
                StopServiceIfPresent(log);

                if (destinationExisted && File.Exists(backupPath))
                {
                    File.Copy(backupPath, driverDestination, overwrite: true);
                    log("Previous driver file restored.");
                }
                else
                {
                    TryDelete(driverDestination, log);
                }

                if (!serviceExisted)
                {
                    DeleteServiceIfPresent(log);
                }
                else if (destinationExisted)
                {
                    StartAndWaitForService(log, cancellationToken);
                    log("Previous driver service restarted.");
                }
            }
            catch (Exception rollbackException)
            {
                log($"ROLLBACK ERROR: {rollbackException.Message}");
            }
            finally
            {
                TryDelete(backupPath, log);
            }

            throw;
        }
    }

    public static DriverStatusResult CheckStatus(Action<string> log, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string state = GetServiceStateText(out uint currentState);
        log($"Service state: {state}");

        if (currentState != NativeMethods.ServiceRunning)
        {
            throw new InvalidOperationException("AeroSecurityDriver is not running.");
        }

        DriverStatusResult status = DriverProtocol.PingAndGetVersion();
        log("Device open: PASS");
        log("PING verification: PASS");
        log($"Version: {status.Version}");
        log($"Protocol: 1");
        log($"Capabilities: 0x{status.Capabilities:X8}");
        log($"Build type: {(status.DebugBuild ? "Debug" : "Release")}");
        LogProtectionStatus(status, log);
        log($"Ping nonce: 0x{status.PingNonce:X16}");
        return status;
    }

    private static void LogProtectionStatus(
        DriverStatusResult status,
        Action<string> log)
    {
        log(status.ProtectionActive
            ? $"Broker protection: ACTIVE (PID {status.ProtectedProcessId})"
            : "Broker protection: waiting for an on-demand Broker operation.");
        log($"Blocked process access: 0x{status.BlockedProcessAccessMask:X8}");
        log($"Blocked thread access: 0x{status.BlockedThreadAccessMask:X8}");
        log($"Handle downgrades: process={status.ProcessHandleDowngrades}, thread={status.ThreadHandleDowngrades}");
        log($"Protection generation: {status.ProtectionGeneration}");
    }

    public static void Uninstall(Action<string> log, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VmGuard.RequireSupportedVirtualMachine();
        StopServiceIfPresent(log);
        DeleteServiceIfPresent(log);

        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string driverPath = Path.Combine(windowsDirectory, "System32", "drivers", DriverFileName);
        TryDelete(driverPath, log);
        TryDelete(driverPath + ".aero-backup", log);
        log("The lab certificate was intentionally left installed for safe rollback and future signed builds.");
    }

    private static void InstallCertificates(string packageDirectory, Action<string> log)
    {
        string[] certificates = Directory
            .EnumerateFiles(packageDirectory, "*.cer", SearchOption.AllDirectories)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (certificates.Length == 0)
        {
            log("No .cer payload found. Continuing with the certificates already trusted by the VM.");
            return;
        }

        foreach (string certificatePath in certificates)
        {
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(certificatePath);
#pragma warning restore SYSLIB0057
            AddCertificateIfMissing(StoreName.Root, certificate, log);
            AddCertificateIfMissing(StoreName.TrustedPublisher, certificate, log);
        }
    }

    private static void AddCertificateIfMissing(
        StoreName storeName,
        X509Certificate2 certificate,
        Action<string> log)
    {
        using var store = new X509Store(storeName, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);

        X509Certificate2Collection matches = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            certificate.Thumbprint,
            validOnly: false);

        if (matches.Count == 0)
        {
            store.Add(certificate);
            log($"Certificate added to LocalMachine\\{storeName}: {certificate.Subject}");
        }
        else
        {
            log($"Certificate already trusted in LocalMachine\\{storeName}: {certificate.Subject}");
        }
    }

    private static void VerifyAuthenticodeSignature(string filePath)
    {
        Guid action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        var fileInfo = new NativeMethods.WinTrustFileInfo
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeMethods.WinTrustFileInfo>()),
            FilePath = filePath,
            FileHandle = IntPtr.Zero,
            KnownSubject = IntPtr.Zero
        };

        IntPtr fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.WinTrustFileInfo>());
        IntPtr trustDataPointer = IntPtr.Zero;

        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trustData = new NativeMethods.WinTrustData
            {
                StructSize = checked((uint)Marshal.SizeOf<NativeMethods.WinTrustData>()),
                PolicyCallbackData = IntPtr.Zero,
                SipClientData = IntPtr.Zero,
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfo = fileInfoPointer,
                StateAction = 0,
                StateData = IntPtr.Zero,
                UrlReference = null,
                ProviderFlags = 0x00001000,
                UiContext = 0
            };

            trustDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.WinTrustData>());
            Marshal.StructureToPtr(trustData, trustDataPointer, false);

            uint result = NativeMethods.WinVerifyTrust(IntPtr.Zero, action, trustDataPointer);
            if (result != 0)
            {
                throw new InvalidOperationException(
                    $"Windows rejected the driver Authenticode signature. WinVerifyTrust=0x{result:X8}.");
            }
        }
        finally
        {
            if (trustDataPointer != IntPtr.Zero)
            {
                Marshal.DestroyStructure<NativeMethods.WinTrustData>(trustDataPointer);
                Marshal.FreeHGlobal(trustDataPointer);
            }

            Marshal.DestroyStructure<NativeMethods.WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    private static void EnsureServiceInstalled(Action<string> log)
    {
        using SafeServiceHandle manager = OpenServiceManager();
        using SafeServiceHandle existing = NativeMethods.OpenService(
            manager,
            ServiceName,
            NativeMethods.ServiceAllRequiredAccess);

        if (!existing.IsInvalid)
        {
            if (!NativeMethods.ChangeServiceConfig(
                    existing,
                    NativeMethods.ServiceKernelDriver,
                    NativeMethods.ServiceSystemStart,
                    NativeMethods.ServiceErrorNormal,
                    ServiceBinaryPath,
                    null,
                    IntPtr.Zero,
                    null,
                    null,
                    null,
                    DisplayName))
            {
                throw NativeMethods.CreateWin32Exception("Cannot update the AeroSecurityDriver service");
            }

            log("Existing driver service updated: SYSTEM_START, ERROR_NORMAL.");
            return;
        }

        int openError = Marshal.GetLastWin32Error();
        if (openError != NativeMethods.ErrorServiceDoesNotExist)
        {
            throw new Win32Exception(openError, "Cannot inspect the AeroSecurityDriver service.");
        }

        using SafeServiceHandle created = NativeMethods.CreateService(
            manager,
            ServiceName,
            DisplayName,
            NativeMethods.ServiceAllRequiredAccess,
            NativeMethods.ServiceKernelDriver,
            NativeMethods.ServiceSystemStart,
            NativeMethods.ServiceErrorNormal,
            ServiceBinaryPath,
            null,
            IntPtr.Zero,
            null,
            null,
            null);

        if (created.IsInvalid)
        {
            throw NativeMethods.CreateWin32Exception("Cannot create the AeroSecurityDriver service");
        }

        log("Driver service created: SYSTEM_START, ERROR_NORMAL.");
    }

    private static void StartAndWaitForService(Action<string> log, CancellationToken cancellationToken)
    {
        using SafeServiceHandle manager = OpenServiceManager();
        using SafeServiceHandle service = NativeMethods.OpenService(
            manager,
            ServiceName,
            NativeMethods.ServiceAllRequiredAccess);

        if (service.IsInvalid)
        {
            throw NativeMethods.CreateWin32Exception("Cannot open the AeroSecurityDriver service");
        }

        uint state = QueryServiceState(service);
        if (state != NativeMethods.ServiceRunning)
        {
            if (!NativeMethods.StartService(service, 0, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != NativeMethods.ErrorServiceAlreadyRunning)
                {
                    throw new Win32Exception(error, "Cannot start the AeroSecurityDriver service.");
                }
            }

            WaitForServiceState(service, NativeMethods.ServiceRunning, TimeSpan.FromSeconds(15), cancellationToken);
        }

        log("Driver service state: RUNNING.");
    }

    private static void StopServiceIfPresent(Action<string> log)
    {
        using SafeServiceHandle manager = OpenServiceManager();
        using SafeServiceHandle service = NativeMethods.OpenService(
            manager,
            ServiceName,
            NativeMethods.ServiceAllRequiredAccess);

        if (service.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NativeMethods.ErrorServiceDoesNotExist)
            {
                return;
            }

            throw new Win32Exception(error, "Cannot open the AeroSecurityDriver service.");
        }

        uint state = QueryServiceState(service);
        if (state == NativeMethods.ServiceStopped)
        {
            return;
        }

        if (!NativeMethods.ControlService(service, NativeMethods.ServiceControlStop, out _))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ErrorServiceNotActive)
            {
                throw new Win32Exception(error, "Cannot stop the AeroSecurityDriver service.");
            }
        }

        WaitForServiceState(service, NativeMethods.ServiceStopped, TimeSpan.FromSeconds(15), CancellationToken.None);
        log("Previous driver service stopped.");
    }

    private static void DeleteServiceIfPresent(Action<string> log)
    {
        using SafeServiceHandle manager = OpenServiceManager();
        using SafeServiceHandle service = NativeMethods.OpenService(
            manager,
            ServiceName,
            NativeMethods.ServiceAllRequiredAccess);

        if (service.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NativeMethods.ErrorServiceDoesNotExist)
            {
                return;
            }

            throw new Win32Exception(error, "Cannot open the AeroSecurityDriver service.");
        }

        if (!NativeMethods.DeleteService(service))
        {
            throw NativeMethods.CreateWin32Exception("Cannot delete the AeroSecurityDriver service");
        }

        log("Driver service removed.");
    }

    private static bool ServiceExists()
    {
        using SafeServiceHandle manager = OpenServiceManager();
        using SafeServiceHandle service = NativeMethods.OpenService(
            manager,
            ServiceName,
            NativeMethods.ServiceQueryStatus);
        return !service.IsInvalid;
    }

    private static string GetServiceStateText(out uint state)
    {
        using SafeServiceHandle manager = OpenServiceManager();
        using SafeServiceHandle service = NativeMethods.OpenService(
            manager,
            ServiceName,
            NativeMethods.ServiceQueryStatus);

        if (service.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NativeMethods.ErrorServiceDoesNotExist)
            {
                state = 0;
                return "NOT INSTALLED";
            }

            throw new Win32Exception(error, "Cannot query the AeroSecurityDriver service.");
        }

        state = QueryServiceState(service);
        return state switch
        {
            NativeMethods.ServiceStopped => "STOPPED",
            NativeMethods.ServiceStartPending => "START_PENDING",
            NativeMethods.ServiceStopPending => "STOP_PENDING",
            NativeMethods.ServiceRunning => "RUNNING",
            _ => $"STATE {state}"
        };
    }

    private static SafeServiceHandle OpenServiceManager()
    {
        SafeServiceHandle manager = NativeMethods.OpenSCManager(
            null,
            null,
            NativeMethods.ScManagerConnect | NativeMethods.ScManagerCreateService);

        if (manager.IsInvalid)
        {
            throw NativeMethods.CreateWin32Exception("Cannot open the Service Control Manager");
        }

        return manager;
    }

    private static uint QueryServiceState(SafeServiceHandle service)
    {
        int size = Marshal.SizeOf<NativeMethods.ServiceStatusProcess>();
        IntPtr buffer = Marshal.AllocHGlobal(size);

        try
        {
            if (!NativeMethods.QueryServiceStatusEx(
                    service,
                    NativeMethods.ScStatusProcessInfo,
                    buffer,
                    size,
                    out _))
            {
                throw NativeMethods.CreateWin32Exception("Cannot query driver service status");
            }

            return Marshal.PtrToStructure<NativeMethods.ServiceStatusProcess>(buffer).CurrentState;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void WaitForServiceState(
        SafeServiceHandle service,
        uint expectedState,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint state = QueryServiceState(service);
            if (state == expectedState)
            {
                return;
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException($"Driver service did not reach state {expectedState} within {timeout.TotalSeconds:F0} seconds.");
    }

    private static string FindSingleFile(string packageDirectory, string fileName)
    {
        string[] files = Directory
            .EnumerateFiles(packageDirectory, fileName, SearchOption.AllDirectories)
            .Where(path => !path.Contains("System32", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return files.Length switch
        {
            0 => throw new FileNotFoundException($"The package does not contain {fileName}."),
            1 => files[0],
            _ => files
                .OrderBy(path => path.Contains("driver", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(path => path.Length)
                .First()
        };
    }

    private static void InstallBootSplashIfPresent(
        string packageDirectory,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        string? script = Directory
            .EnumerateFiles(packageDirectory, "Install-AeroSecurityBootSplashVm.ps1", SearchOption.AllDirectories)
            .FirstOrDefault();

        string? efi = Directory
            .EnumerateFiles(packageDirectory, "Aero.Security.BootSplash*.efi", SearchOption.AllDirectories)
            .FirstOrDefault();

        if (script is null || efi is null)
        {
            log("BootSplash was selected, but its optional payload is not present. Driver installation remains successful.");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        log("Installing or updating the VM UEFI BootSplash...");

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -VmLabOnly",
            WorkingDirectory = Path.GetDirectoryName(script)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Cannot start the BootSplash installer.");

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        foreach (string line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            log($"BootSplash: {line}");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"BootSplash installer failed with exit code {process.ExitCode}: {error.Trim()}");
        }

        log("BootSplash installation/update completed.");
    }

    private static void TryDelete(string path, Action<string> log)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            log($"Could not delete {path}: {exception.Message}");
        }
    }
}
