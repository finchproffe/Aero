using Aero.App.Security;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Aero.Security.AppBound;

/// <summary>
/// LocalSystem service behind <see cref="AppBoundKey"/>. Started on demand by Aero, it serves the
/// key requests of one launch and exits after a minute idle, so it costs nothing between launches.
/// </summary>
internal static partial class Program
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    // Only Aero.exe from this very folder: under WindowsApps it is writable by TrustedInstaller alone.
    private static readonly string ExpectedClientPath =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, AppBoundKey.ClientExecutableName));

    private static readonly CancellationTokenSource Stop = new();
    private static IntPtr _statusHandle;
    private static ServiceMainCallback? _serviceMain;
    private static HandlerCallback? _handler;

    private static int Main(string[] args)
    {
        if (args is ["--console"])
        {
            // Dev only: same protocol as the user. DPAPI then binds to the user, not to SYSTEM.
            Serve(Stop.Token);
            return 0;
        }

        _serviceMain = ServiceMain;
        _handler = Handler;
        IntPtr name = Marshal.StringToHGlobalUni(AppBoundKey.ServiceName);
        ServiceTableEntry[] table =
        [
            new() { Name = name, Proc = Marshal.GetFunctionPointerForDelegate(_serviceMain) },
            default
        ];
        return StartServiceCtrlDispatcher(table) ? 0 : Marshal.GetLastWin32Error();
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _statusHandle = RegisterServiceCtrlHandlerEx(AppBoundKey.ServiceName, _handler!, IntPtr.Zero);
        SetState(ServiceRunning);
        AllowUsersToStart();
        try
        {
            Serve(Stop.Token);
        }
        finally
        {
            SetState(ServiceStopped);
        }
    }

    private static int Handler(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        if (control is ServiceControlStop or ServiceControlShutdown)
        {
            SetState(ServiceStopPending);
            Stop.Cancel();
        }

        return 0;
    }

    private static void Serve(CancellationToken stop)
    {
        PipeSecurity security = new();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));

        // FirstPipeInstance: fail instead of serving next to a pipe someone created first.
        using NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(
            AppBoundKey.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
        while (true)
        {
            using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(stop);
            idle.CancelAfter(IdleTimeout);
            try
            {
                pipe.WaitForConnectionAsync(idle.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                HandleClient(pipe);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                // A client that hung up or sent garbage; serve the next one.
            }
            finally
            {
                pipe.Disconnect();
            }
        }
    }

    private static void HandleClient(NamedPipeServerStream pipe)
    {
        (byte operation, byte[] payload) = AppBoundKey.ReadFrame(pipe);
        byte[] result = [];
        byte status = AppBoundKey.StatusDenied;
        try
        {
            if (IsAeroClient(pipe, out byte[] userEntropy))
            {
                status = AppBoundKey.StatusFailed;
                // SYSTEM's user-scope DPAPI: only SYSTEM can unprotect. The caller's SID as entropy
                // keeps another account on this PC from opening this user's wrapped key.
                result = operation switch
                {
                    AppBoundKey.OpWrap => ProtectedData.Protect(payload, userEntropy, DataProtectionScope.CurrentUser),
                    AppBoundKey.OpUnwrap => ProtectedData.Unprotect(payload, userEntropy, DataProtectionScope.CurrentUser),
                    _ => throw new CryptographicException("Unknown operation.")
                };
                status = AppBoundKey.StatusOk;
            }
        }
        catch (CryptographicException)
        {
            result = [];
        }

        AppBoundKey.WriteFrame(pipe, status, result);
        CryptographicOperations.ZeroMemory(payload);
        CryptographicOperations.ZeroMemory(result);
    }

    private static bool IsAeroClient(NamedPipeServerStream pipe, out byte[] userEntropy)
    {
        userEntropy = [];
        int processId = BrokerNativeMethods.GetPipeClientProcessId(pipe.SafePipeHandle);
        if (!BrokerNativeMethods.TryGetProcessImagePath(processId, out string imagePath) ||
            !imagePath.Equals(ExpectedClientPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The SID comes from the token of the very process whose image was checked. (Pipe
        // impersonation is avoided: .NET cannot load assemblies under an identification token.)
        using Process client = Process.GetProcessById(processId);
        if (!OpenProcessToken(client.SafeHandle, TokenQuery, out SafeAccessTokenHandle token))
        {
            return false;
        }

        using (token)
        using (WindowsIdentity identity = new(token.DangerousGetHandle()))
        {
            string? sid = identity.User?.Value;
            userEntropy = sid is null ? [] : Encoding.UTF8.GetBytes(sid);
            return sid is not null;
        }
    }

    /// <summary>
    /// Packaged services cannot declare a security descriptor, so the service grants interactive
    /// users SERVICE_START (not stop or configure) itself on every start.
    /// </summary>
    private static void AllowUsersToStart()
    {
        const string sddl =
            "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)" +
            "(A;;CCLCSWRPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";
        IntPtr manager = OpenSCManager(null, null, ScManagerConnect);
        IntPtr service = manager == IntPtr.Zero ? IntPtr.Zero : OpenService(manager, AppBoundKey.ServiceName, WriteDac);
        if (service != IntPtr.Zero &&
            ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out IntPtr descriptor, out _))
        {
            _ = SetServiceObjectSecurity(service, DaclSecurityInformation, descriptor);
            _ = LocalFree(descriptor);
        }

        if (service != IntPtr.Zero)
        {
            _ = CloseServiceHandle(service);
        }

        if (manager != IntPtr.Zero)
        {
            _ = CloseServiceHandle(manager);
        }
    }

    private static void SetState(int state)
    {
        ServiceStatus status = new()
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = state == ServiceRunning ? ServiceAcceptStop | ServiceAcceptShutdown : 0
        };
        _ = SetServiceStatus(_statusHandle, ref status);
    }

    private const int ServiceWin32OwnProcess = 0x10;
    private const int ServiceStopped = 1;
    private const int ServiceStopPending = 3;
    private const int ServiceRunning = 4;
    private const int ServiceAcceptStop = 0x1;
    private const int ServiceAcceptShutdown = 0x4;
    private const int ServiceControlStop = 0x1;
    private const int ServiceControlShutdown = 0x5;
    private const uint ScManagerConnect = 0x0001;
    private const uint WriteDac = 0x00040000;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint TokenQuery = 0x0008;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainCallback(int argc, IntPtr argv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int HandlerCallback(int control, int eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        public IntPtr Name;
        public IntPtr Proc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [DllImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] table);

    [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string serviceName, HandlerCallback handler, IntPtr context);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetServiceStatus(IntPtr statusHandle, ref ServiceStatus status);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenService(IntPtr manager, string serviceName, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetServiceObjectSecurity(IntPtr service, uint securityInformation, IntPtr descriptor);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor, uint revision, out IntPtr securityDescriptor, out uint size);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
