using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Aero.App.Security;

/// <summary>
/// App-bound encryption, the Chrome 127 model: the AeroAppBound service (LocalSystem, shipped in
/// the MSIX) wraps keys with SYSTEM's DPAPI and unwraps them only for Aero.exe from its own
/// package folder. A stealer running as the user can copy the wrapped key file but cannot open
/// it; it has to inject into a running Aero instead, which is far louder.
/// </summary>
public static partial class AppBoundKey
{
    public const string ServiceName = "AeroAppBound";
    public const string PipeName = "Aero.AppBound";
    public const string ClientExecutableName = "Aero.exe";
    public const byte OpWrap = 1;
    public const byte OpUnwrap = 2;
    public const byte StatusOk = 0;
    public const byte StatusDenied = 1;
    public const byte StatusFailed = 2;
    public const int MaxPayload = 4096;

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ConnectTimeoutMilliseconds = 3000;

    private static readonly Lazy<bool> Installed = new(() =>
    {
        using ServiceHandle service = OpenAeroService(ServiceQueryStatus);
        return !service.IsInvalid;
    });

    /// <summary>False on dev builds and unpackaged installs: there is no service to bind to.</summary>
    public static bool IsInstalled => Installed.Value;

    public static byte[] Wrap(byte[] key) => Call(OpWrap, key);

    public static byte[] Unwrap(byte[] wrapped) => Call(OpUnwrap, wrapped);

    private static byte[] Call(byte operation, byte[] payload)
    {
        // The service exits after a minute idle; a second attempt covers the race where it
        // was shutting down exactly while we connected.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return CallOnce(operation, payload);
            }
            catch (Exception exception) when (attempt == 0 && exception is TimeoutException or IOException)
            {
            }
        }
    }

    private static byte[] CallOnce(byte operation, byte[] payload)
    {
        EnsureServiceStarted();
        using NamedPipeClientStream pipe = new(
            ".", PipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
        pipe.Connect(ConnectTimeoutMilliseconds);

        // User processes never run in session 0, so a pipe squatted by malware fails here
        // before the key is sent to it.
        int serverProcessId = BrokerNativeMethods.GetPipeServerProcessId(pipe.SafePipeHandle);
        if (!BrokerNativeMethods.TryGetProcessSessionId(serverProcessId, out int session) || session != 0)
        {
            throw new CryptographicException("The Aero key service pipe is not owned by a Windows service.");
        }

        WriteFrame(pipe, operation, payload);
        (byte status, byte[] result) = ReadFrame(pipe);
        return status == StatusOk
            ? result
            : throw new CryptographicException(status == StatusDenied
                ? "The Aero key service refused this process."
                : "The Aero key service could not process the key.");
    }

    private static void EnsureServiceStarted()
    {
        using ServiceHandle service = OpenAeroService(ServiceStart | ServiceQueryStatus);
        if (service.IsInvalid)
        {
            throw new CryptographicException("The Aero key service is not installed.");
        }

        if (!StartService(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != ErrorServiceAlreadyRunning)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The Aero key service could not start.");
        }
    }

    /// <summary>Frame: head (op or status) | length (UInt16 LE) | payload.</summary>
    public static void WriteFrame(Stream stream, byte head, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        byte[] frame = new byte[3 + payload.Length];
        frame[0] = head;
        frame[1] = (byte)payload.Length;
        frame[2] = (byte)(payload.Length >> 8);
        payload.CopyTo(frame.AsSpan(3));
        stream.Write(frame);
        stream.Flush();
        CryptographicOperations.ZeroMemory(frame);
    }

    public static (byte Head, byte[] Payload) ReadFrame(Stream stream)
    {
        Span<byte> header = stackalloc byte[3];
        stream.ReadExactly(header);
        int length = header[1] | header[2] << 8;
        if (length > MaxPayload)
        {
            throw new InvalidDataException("Oversized Aero key service frame.");
        }

        byte[] payload = new byte[length];
        stream.ReadExactly(payload);
        return (header[0], payload);
    }

    private static ServiceHandle OpenAeroService(uint access)
    {
        using ServiceHandle manager = OpenSCManager(null, null, ScManagerConnect);
        return OpenService(manager, ServiceName, access);
    }

    private sealed class ServiceHandle() : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial ServiceHandle OpenSCManager(string? machineName, string? databaseName, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial ServiceHandle OpenService(ServiceHandle manager, string serviceName, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartService(ServiceHandle service, uint argumentCount, IntPtr arguments);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);
}
