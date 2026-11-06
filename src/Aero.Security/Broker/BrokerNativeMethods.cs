using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Aero.App.Security;

internal static partial class BrokerNativeMethods
{
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const int SecurityDescriptorRevision = 1;


    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        char* executablePath,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetKernelObjectSecurity(
        IntPtr handle,
        uint securityInformation,
        IntPtr securityDescriptor);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);


    public static int GetCurrentParentProcessId()
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue)
        {
            return -1;
        }

        try
        {
            ProcessEntry32 entry = new()
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
                ExecutableFile = string.Empty
            };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return -1;
            }

            do
            {
                if (entry.ProcessId == (uint)Environment.ProcessId)
                {
                    return entry.ParentProcessId <= int.MaxValue
                        ? (int)entry.ParentProcessId
                        : -1;
                }
            }
            while (Process32NextW(snapshot, ref entry));

            return -1;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    public static int GetCurrentSessionId()
    {
        if (!ProcessIdToSessionId((uint)Environment.ProcessId, out uint sessionId) ||
            sessionId > int.MaxValue)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not determine the current Windows session.");
        }

        return (int)sessionId;
    }

    public static int GetPipeServerProcessId(SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out uint processId) || processId > int.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not verify Broker pipe server.");
        }

        return (int)processId;
    }

    public static int GetPipeClientProcessId(SafePipeHandle pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe, out uint processId) || processId > int.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not verify Broker pipe client.");
        }

        return (int)processId;
    }

    public static void TryAllowForegroundWindow(int processId)
    {
        if (processId > 0)
        {
            // Best effort only. The Aero window remains the verified owner; this
            // merely lets the child Broker participate in foreground credential UI.
            _ = AllowSetForegroundWindow((uint)processId);
        }
    }

    public static void TryActivateOwnerWindow(IntPtr window)
    {
        if (window != IntPtr.Zero)
        {
            _ = SetForegroundWindow(window);
        }
    }

    public static bool WindowBelongsToProcess(IntPtr window, int expectedProcessId)
    {
        if (window == IntPtr.Zero || expectedProcessId <= 0)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(window, out uint processId);
        return processId == (uint)expectedProcessId;
    }

    public static bool TryRestrictCurrentProcessDacl()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string? userSid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            return false;
        }

        // SYSTEM keeps full access. The current user receives only terminate,
        // limited-query and synchronize rights, not PROCESS_VM_READ/WRITE.
        // As object owner the same SID can still replace the DACL, so this is
        // hardening rather than a kernel-level same-user security boundary.
        string sddl = $"O:{userSid}D:P(A;;GA;;;SY)(A;;0x00101001;;;{userSid})";
        IntPtr descriptor = IntPtr.Zero;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                    sddl,
                    SecurityDescriptorRevision,
                    out descriptor,
                    out _))
            {
                return false;
            }

            using SafeProcessHandle currentProcess = OpenProcess(
                ReadControl | WriteDac | ProcessQueryLimitedInformation,
                inheritHandle: false,
                (uint)Environment.ProcessId);
            if (currentProcess.IsInvalid)
            {
                return false;
            }

            return SetKernelObjectSecurity(
                currentProcess.DangerousGetHandle(),
                DaclSecurityInformation |
                ProtectedDaclSecurityInformation,
                descriptor);
        }
        finally
        {
            if (descriptor != IntPtr.Zero)
            {
                _ = LocalFree(descriptor);
            }
        }
    }

    public static bool TryGetProcessImagePath(int processId, out string imagePath)
    {
        imagePath = string.Empty;
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using SafeProcessHandle process = OpenProcess(
                ProcessQueryLimitedInformation,
                inheritHandle: false,
                (uint)processId);
            if (process.IsInvalid)
            {
                return false;
            }

            char[] pathBuffer = new char[32768];
            uint capacity = (uint)pathBuffer.Length;
            unsafe
            {
                fixed (char* pathPointer = pathBuffer)
                {
                    if (!QueryFullProcessImageName(
                            process,
                            0,
                            pathPointer,
                            ref capacity) ||
                        capacity == 0 ||
                        capacity > (uint)pathBuffer.Length)
                    {
                        return false;
                    }

                    imagePath = Path.GetFullPath(new string(
                        pathPointer,
                        0,
                        checked((int)capacity)));
                    return true;
                }
            }
        }
        catch
        {
            imagePath = string.Empty;
            return false;
        }
    }

    public static bool IsExpectedProcessImage(int processId, string expectedPath, int expectedSessionId)
    {
        if (processId <= 0 || expectedSessionId < 0)
        {
            return false;
        }

        try
        {
            if (!ProcessIdToSessionId((uint)processId, out uint sessionId) ||
                sessionId != (uint)expectedSessionId)
            {
                return false;
            }

            using SafeProcessHandle process = OpenProcess(
                ProcessQueryLimitedInformation,
                inheritHandle: false,
                (uint)processId);
            if (process.IsInvalid)
            {
                return false;
            }

            char[] actualPathBuffer = new char[32768];
            uint capacity = (uint)actualPathBuffer.Length;
            unsafe
            {
                fixed (char* actualPathPointer = actualPathBuffer)
                {
                    if (!QueryFullProcessImageName(
                            process,
                            0,
                            actualPathPointer,
                            ref capacity) ||
                        capacity == 0 ||
                        capacity > (uint)actualPathBuffer.Length)
                    {
                        return false;
                    }

                    string actualPath = new(
                        actualPathPointer,
                        0,
                        checked((int)capacity));
                    return Path.GetFullPath(actualPath).Equals(
                        Path.GetFullPath(expectedPath),
                        StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
            return false;
        }
    }
}
