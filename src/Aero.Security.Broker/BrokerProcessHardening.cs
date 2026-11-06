using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aero.Security.BrokerHost;

/// <summary>
/// Applies Broker-only process mitigations and watches loader notifications.
/// Normal browsing never touches this code: the Broker exists only for a single
/// protected Vault operation.
/// </summary>
internal sealed class BrokerProcessHardening : IDisposable
{
    private const uint DllNotificationReasonLoaded = 1;
    private const uint WaitObject0 = 0;
    private const uint Infinite = 0xFFFFFFFF;
    private const int ModuleAuditRetryCount = 3;
    private const int ModuleAuditRetryDelayMilliseconds = 10;

    private static BrokerProcessHardening? s_active;
    private static readonly BrokerHardeningNative.DllNotificationCallback DllNotificationCallback = OnDllNotification;

    private readonly object _moduleGate = new();
    private readonly HashSet<string> _knownModulePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _brokerDirectory;
    private readonly string[] _trustedReadOnlyRoots;
    private readonly IntPtr _notificationEvent;
    private readonly Thread _moduleMonitor;

    private IntPtr _notificationCookie;
    private int _stopRequested;
    private int _integrityViolation;
    private int _disposed;

    private BrokerProcessHardening()
    {
        string processPath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Broker executable path is unavailable.");
        _brokerDirectory = AppendDirectorySeparator(
            Path.GetDirectoryName(Path.GetFullPath(processPath))
                ?? throw new InvalidOperationException("Broker executable directory is unavailable."));
        _trustedReadOnlyRoots = BuildTrustedReadOnlyRoots();

        _notificationEvent = BrokerHardeningNative.CreateEvent(
            IntPtr.Zero,
            manualReset: false,
            initialState: false,
            name: null);
        if (_notificationEvent == IntPtr.Zero)
        {
            throw new InvalidOperationException("Broker module notification event could not be created.");
        }

        _moduleMonitor = new Thread(MonitorModuleLoads)
        {
            IsBackground = true,
            Name = "Aero Broker integrity monitor"
        };
    }

    public static bool TryCreate(out BrokerProcessHardening? hardening)
    {
        hardening = null;
        BrokerProcessHardening? created = null;
        try
        {
            if (!ApplyEarlyMitigations())
            {
                return false;
            }

            created = new BrokerProcessHardening();
            if (!created.Start())
            {
                created.Dispose();
                return false;
            }

            hardening = created;
            return true;
        }
        catch
        {
            created?.Dispose();
            hardening = null;
            return false;
        }
    }

    /// <summary>
    /// Performs the heavier checks only immediately before Broker touches Vault
    /// key material. This does not run during page loading, typing or navigation.
    /// </summary>
    public void EnsureTrustedForSecretOperation()
    {
        // The notification thread exists only for the pre-secret audit window.
        // WinUI legitimately lazy-loads native text/input modules when the standard
        // TextBox receives its first value. Keeping the watcher alive after this
        // gate introduced a race that could terminate a valid one-shot Broker.
        // Process mitigations and the restricted DACL remain active for the full
        // process lifetime; only the redundant notification watcher is stopped.
        StopModuleMonitoring();

        if (Volatile.Read(ref _integrityViolation) != 0 ||
            !VerifyRequiredMitigations() ||
            !AuditLoadedModulesWithRetry(allowNewTrustedModules: true) ||
            !AuditThreadStartAddresses() ||
            !AuditNativeAotExecutableMemory())
        {
            // This check runs before password bytes are materialized. Throwing keeps
            // the operation fail-closed while allowing the Broker to return a signed,
            // visible diagnostic instead of disappearing with no explanation.
            throw new InvalidOperationException(
                "Aero Security Broker integrity verification failed before secret access.");
        }
    }

    private bool Start()
    {
        if (Interlocked.CompareExchange(ref s_active, this, null) is not null)
        {
            return false;
        }

        int status = BrokerHardeningNative.LdrRegisterDllNotification(
            flags: 0,
            DllNotificationCallback,
            IntPtr.Zero,
            out _notificationCookie);
        if (status < 0 || _notificationCookie == IntPtr.Zero)
        {
            Interlocked.CompareExchange(ref s_active, null, this);
            return false;
        }

        if (!AuditLoadedModulesWithRetry(allowNewTrustedModules: true))
        {
            return false;
        }

        _moduleMonitor.Start();
        return true;
    }

    private static bool ApplyEarlyMitigations()
    {
        // Keep the Broker's normal native DLL search limited to its own payload
        // and Windows System32. This removes the current directory and PATH from
        // the default search order without affecting the main Aero process.
        if (!BrokerHardeningNative.SetDefaultDllDirectories(
                BrokerHardeningNative.LoadLibrarySearchApplicationDir |
                BrokerHardeningNative.LoadLibrarySearchSystem32))
        {
            return false;
        }

        return BrokerHardeningNative.TrySetMitigation(
                   BrokerHardeningNative.ProcessMitigationPolicy.ExtensionPointDisable,
                   BrokerHardeningNative.ExtensionPointsDisabled) &&
               BrokerHardeningNative.TrySetMitigation(
                   BrokerHardeningNative.ProcessMitigationPolicy.StrictHandleCheck,
                   BrokerHardeningNative.StrictHandleChecksEnabled) &&
               BrokerHardeningNative.TrySetMitigation(
                   BrokerHardeningNative.ProcessMitigationPolicy.ImageLoad,
                   BrokerHardeningNative.ImageLoadRestrictions) &&
               BrokerHardeningNative.TrySetMitigation(
                   BrokerHardeningNative.ProcessMitigationPolicy.ChildProcess,
                   BrokerHardeningNative.ChildProcessCreationDisabled) &&
               ApplyDynamicCodePolicyWhenSupported();
    }

    private static bool ApplyDynamicCodePolicyWhenSupported()
    {
        // A normal managed Broker still needs the CLR JIT. When the same project
        // is published as Native AOT, the JIT is absent and Arbitrary Code Guard
        // can be enabled safely without adding runtime work to each request.
        return RuntimeFeature.IsDynamicCodeSupported ||
               BrokerHardeningNative.TrySetMitigation(
                   BrokerHardeningNative.ProcessMitigationPolicy.DynamicCode,
                   BrokerHardeningNative.DynamicCodeProhibited);
    }

    private static bool VerifyRequiredMitigations()
    {
        return BrokerHardeningNative.HasMitigationFlags(
                   BrokerHardeningNative.ProcessMitigationPolicy.ExtensionPointDisable,
                   BrokerHardeningNative.ExtensionPointsDisabled) &&
               BrokerHardeningNative.HasMitigationFlags(
                   BrokerHardeningNative.ProcessMitigationPolicy.StrictHandleCheck,
                   BrokerHardeningNative.StrictHandleChecksEnabled) &&
               BrokerHardeningNative.HasMitigationFlags(
                   BrokerHardeningNative.ProcessMitigationPolicy.ImageLoad,
                   BrokerHardeningNative.ImageLoadRestrictions) &&
               BrokerHardeningNative.HasMitigationFlags(
                   BrokerHardeningNative.ProcessMitigationPolicy.ChildProcess,
                   BrokerHardeningNative.ChildProcessCreationDisabled) &&
               (RuntimeFeature.IsDynamicCodeSupported ||
                BrokerHardeningNative.HasMitigationFlags(
                    BrokerHardeningNative.ProcessMitigationPolicy.DynamicCode,
                    BrokerHardeningNative.DynamicCodeProhibited));
    }

    private void MonitorModuleLoads()
    {
        while (Volatile.Read(ref _stopRequested) == 0)
        {
            uint wait = BrokerHardeningNative.WaitForSingleObject(_notificationEvent, Infinite);
            if (Volatile.Read(ref _stopRequested) != 0)
            {
                return;
            }
            if (wait != WaitObject0)
            {
                Volatile.Write(ref _integrityViolation, 1);
                return;
            }

            // The loader notification only signals. Module enumeration and path
            // validation happen outside the loader lock on this dedicated thread.
            // Let the loader finish publishing its bookkeeping first.
            Thread.Sleep(1);
            if (Volatile.Read(ref _stopRequested) != 0)
            {
                return;
            }
            if (!AuditLoadedModulesWithRetry(allowNewTrustedModules: true))
            {
                // No secret exists while this watcher is active. Record the
                // violation and let the synchronous pre-secret gate return a
                // signed failure instead of abruptly killing the pipe.
                Volatile.Write(ref _integrityViolation, 1);
                return;
            }
        }
    }

    private bool AuditLoadedModulesWithRetry(bool allowNewTrustedModules)
    {
        for (int attempt = 0; attempt < ModuleAuditRetryCount; attempt++)
        {
            if (AuditLoadedModules(allowNewTrustedModules))
            {
                return true;
            }

            if (attempt + 1 < ModuleAuditRetryCount)
            {
                Thread.Sleep(ModuleAuditRetryDelayMilliseconds);
            }
        }

        return false;
    }

    private bool AuditLoadedModules(bool allowNewTrustedModules)
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                string modulePath = NormalizePath(module.FileName);
                if (modulePath.Length == 0)
                {
                    return false;
                }

                lock (_moduleGate)
                {
                    if (_knownModulePaths.Contains(modulePath))
                    {
                        continue;
                    }
                }

                if (!allowNewTrustedModules || !IsTrustedModulePath(modulePath))
                {
                    return false;
                }

                lock (_moduleGate)
                {
                    _knownModulePaths.Add(modulePath);
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool AuditThreadStartAddresses()
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            // CLR/JIT worker threads are created dynamically and their reported start
            // addresses are not a stable integrity boundary across Windows/.NET builds.
            // The managed Broker remains covered by loader auditing, process mitigations,
            // the restricted DACL, and the kernel process/thread handle filter.
            return true;
        }

        try
        {
            using Process process = Process.GetCurrentProcess();
            List<ModuleRange> moduleRanges = new(process.Modules.Count);
            foreach (ProcessModule module in process.Modules)
            {
                long start = module.BaseAddress.ToInt64();
                long size = Math.Max(module.ModuleMemorySize, 1);
                moduleRanges.Add(new ModuleRange(start, checked(start + size)));
            }

            foreach (ProcessThread thread in process.Threads)
            {
                using (thread)
                {
                    long startAddress;
                    try
                    {
                        startAddress = thread.StartAddress.ToInt64();
                    }
                    catch (InvalidOperationException)
                    {
                        // A short-lived CLR worker may exit between snapshot and query.
                        continue;
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        continue;
                    }

                    if (startAddress == 0)
                    {
                        continue;
                    }

                    bool belongsToLoadedImage = false;
                    foreach (ModuleRange range in moduleRanges)
                    {
                        if (startAddress >= range.Start && startAddress < range.End)
                        {
                            belongsToLoadedImage = true;
                            break;
                        }
                    }

                    if (!belongsToLoadedImage)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool AuditNativeAotExecutableMemory()
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            // Managed builds legitimately contain CLR JIT code pages. Loader
            // notifications remain active in this mode.
            return true;
        }

        BrokerHardeningNative.GetSystemInfo(out BrokerHardeningNative.SystemInfo systemInfo);
        nuint address = (nuint)systemInfo.MinimumApplicationAddress;
        nuint maximumAddress = (nuint)systemInfo.MaximumApplicationAddress;
        nuint informationSize = (nuint)Marshal.SizeOf<BrokerHardeningNative.MemoryBasicInformation>();

        while (address < maximumAddress)
        {
            nuint read = BrokerHardeningNative.VirtualQuery(
                (IntPtr)address,
                out BrokerHardeningNative.MemoryBasicInformation information,
                informationSize);
            if (read == 0 || information.RegionSize == 0)
            {
                break;
            }

            if (information.State == BrokerHardeningNative.MemCommit &&
                information.Type == BrokerHardeningNative.MemPrivate &&
                BrokerHardeningNative.IsExecutableProtection(information.Protect))
            {
                return false;
            }

            nuint next = address + information.RegionSize;
            if (next <= address)
            {
                break;
            }
            address = next;
        }

        return true;
    }

    private bool IsTrustedModulePath(string modulePath)
    {
        return IsTrustedModulePath(modulePath, _brokerDirectory, _trustedReadOnlyRoots);
    }

    internal static bool IsTrustedModulePath(
        string modulePath,
        string brokerDirectory,
        IReadOnlyList<string> trustedReadOnlyRoots)
    {
        // A signed MSIX is installed beneath the ACL-protected WindowsApps root.
        // Check immutable roots first: the package contains legitimate native WinUI
        // modules (for example WinUIEdit.dll) whose names intentionally do not use
        // the Microsoft.* prefix. Writable development output still falls through
        // to the narrow Broker payload whitelist below.
        foreach (string root in trustedReadOnlyRoots)
        {
            if (modulePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return modulePath.StartsWith(brokerDirectory, StringComparison.OrdinalIgnoreCase) &&
               IsTrustedBrokerPayloadName(Path.GetFileName(modulePath));
    }

    private static bool IsTrustedBrokerPayloadName(string fileName)
    {
        if (fileName.Equals("Aero.Security.Broker.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("Aero.Security.Broker.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("Aero.Security.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("hostfxr.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("hostpolicy.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("clrjit.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("WinRT.Runtime.dll", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
               (fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] BuildTrustedReadOnlyRoots()
    {
        List<string> roots = [];
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        AddRoot(roots, Environment.SystemDirectory);
        AddRoot(roots, Path.Combine(windowsDirectory, "SysWOW64"));
        AddRoot(roots, Path.Combine(windowsDirectory, "WinSxS"));
        AddRoot(roots, Path.Combine(windowsDirectory, "Microsoft.NET"));
        AddRoot(roots, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet"));
        AddRoot(roots, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps"));
        AddRoot(roots, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "dotnet"));
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddRoot(List<string> roots, string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            roots.Add(AppendDirectorySeparator(Path.GetFullPath(path)));
        }
    }

    private static string NormalizePath(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : Path.GetFullPath(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string AppendDirectorySeparator(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
        Path.DirectorySeparatorChar;

    private static void OnDllNotification(uint reason, IntPtr data, IntPtr context)
    {
        _ = data;
        _ = context;
        if (reason != DllNotificationReasonLoaded)
        {
            return;
        }

        BrokerProcessHardening? active = Volatile.Read(ref s_active);
        if (active is not null && active._notificationEvent != IntPtr.Zero &&
            !BrokerHardeningNative.SetEvent(active._notificationEvent))
        {
            Volatile.Write(ref active._integrityViolation, 1);
        }
    }

    private void StopModuleMonitoring()
    {
        Interlocked.Exchange(ref _stopRequested, 1);
        if (_notificationCookie != IntPtr.Zero)
        {
            _ = BrokerHardeningNative.LdrUnregisterDllNotification(_notificationCookie);
            _notificationCookie = IntPtr.Zero;
        }
        Interlocked.CompareExchange(ref s_active, null, this);

        if (_notificationEvent != IntPtr.Zero)
        {
            _ = BrokerHardeningNative.SetEvent(_notificationEvent);
        }
        if (_moduleMonitor.IsAlive && Thread.CurrentThread != _moduleMonitor)
        {
            _ = _moduleMonitor.Join(millisecondsTimeout: 1000);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopModuleMonitoring();
        if (_notificationEvent != IntPtr.Zero)
        {
            _ = BrokerHardeningNative.CloseHandle(_notificationEvent);
        }
    }

    private readonly record struct ModuleRange(long Start, long End);
}

internal static partial class BrokerHardeningNative
{
    public const uint LoadLibrarySearchApplicationDir = 0x00000200;
    public const uint LoadLibrarySearchSystem32 = 0x00000800;

    public const uint ExtensionPointsDisabled = 0x00000001;
    public const uint StrictHandleChecksEnabled = 0x00000003;
    public const uint ImageLoadRestrictions = 0x00000007;
    public const uint ChildProcessCreationDisabled = 0x00000001;
    public const uint DynamicCodeProhibited = 0x00000001;

    public const uint MemCommit = 0x00001000;
    public const uint MemPrivate = 0x00020000;

    private const uint PageExecute = 0x00000010;
    private const uint PageExecuteRead = 0x00000020;
    private const uint PageExecuteReadWrite = 0x00000040;
    private const uint PageExecuteWriteCopy = 0x00000080;
    private const uint PageGuard = 0x00000100;
    private const uint PageNoAccess = 0x00000001;

    internal enum ProcessMitigationPolicy
    {
        DynamicCode = 2,
        StrictHandleCheck = 3,
        ExtensionPointDisable = 6,
        ImageLoad = 10,
        ChildProcess = 13
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemInfo
    {
        public ushort ProcessorArchitecture;
        public ushort Reserved;
        public uint PageSize;
        public IntPtr MinimumApplicationAddress;
        public IntPtr MaximumApplicationAddress;
        public nuint ActiveProcessorMask;
        public uint NumberOfProcessors;
        public uint ProcessorType;
        public uint AllocationGranularity;
        public ushort ProcessorLevel;
        public ushort ProcessorRevision;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void DllNotificationCallback(uint reason, IntPtr data, IntPtr context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetDefaultDllDirectories(uint directoryFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessMitigationPolicy(
        ProcessMitigationPolicy mitigationPolicy,
        ref uint buffer,
        nuint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMitigationPolicy(
        IntPtr process,
        ProcessMitigationPolicy mitigationPolicy,
        out uint buffer,
        nuint length);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateEvent(
        IntPtr eventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState,
        string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetEvent(IntPtr eventHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    internal static partial void GetSystemInfo(out SystemInfo systemInfo);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nuint VirtualQuery(
        IntPtr address,
        out MemoryBasicInformation information,
        nuint informationLength);

    [DllImport("ntdll.dll")]
    internal static extern int LdrRegisterDllNotification(
        uint flags,
        DllNotificationCallback notificationFunction,
        IntPtr context,
        out IntPtr cookie);

    [DllImport("ntdll.dll")]
    internal static extern int LdrUnregisterDllNotification(IntPtr cookie);

    internal static bool TrySetMitigation(ProcessMitigationPolicy policy, uint flags) =>
        SetProcessMitigationPolicy(policy, ref flags, (nuint)sizeof(uint));

    internal static bool HasMitigationFlags(ProcessMitigationPolicy policy, uint requiredFlags)
    {
        return GetProcessMitigationPolicy(
                   GetCurrentProcess(),
                   policy,
                   out uint actualFlags,
                   (nuint)sizeof(uint)) &&
               (actualFlags & requiredFlags) == requiredFlags;
    }

    internal static bool IsExecutableProtection(uint protection)
    {
        if ((protection & (PageGuard | PageNoAccess)) != 0)
        {
            return false;
        }

        uint baseProtection = protection & 0xFF;
        return baseProtection is PageExecute or
            PageExecuteRead or
            PageExecuteReadWrite or
            PageExecuteWriteCopy;
    }
}
