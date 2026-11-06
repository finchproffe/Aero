using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Aero.Security.Driver.TestClient;

internal static class Program
{
    private const string DevicePath = @"\\.\AeroSecurity";
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;

    private const uint AeroMagic = 0x4F524541;
    private const uint ProtocolVersion = 1;
    private const uint IoctlPing = 0x0022E000;
    private const uint IoctlGetVersion = 0x0022E004;

    public static int Main()
    {
        try
        {
            using SafeFileHandle device = CreateFileW(
                DevicePath,
                GenericRead | GenericWrite,
                0,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);

            if (device.IsInvalid)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Cannot open the Aero Security driver. Run this client as administrator and verify that the driver service is running.");
            }

            VersionResponse version = InvokeNoInput<VersionResponse>(device, IoctlGetVersion);
            ValidateCommonHeader(version.Magic, version.ProtocolVersion);

            Span<byte> nonceBytes = stackalloc byte[sizeof(ulong)];
            RandomNumberGenerator.Fill(nonceBytes);
            ulong nonce = BitConverter.ToUInt64(nonceBytes);
            PingRequest request = new()
            {
                Magic = AeroMagic,
                ProtocolVersion = ProtocolVersion,
                Nonce = nonce
            };

            PingResponse response = Invoke<PingRequest, PingResponse>(device, IoctlPing, request);
            ValidateCommonHeader(response.Magic, response.ProtocolVersion);

            if (response.Nonce != nonce)
            {
                throw new InvalidOperationException("The driver returned an unexpected ping nonce.");
            }

            Console.WriteLine("Aero Security Driver is responding correctly.");
            Console.WriteLine($"Version: {version.Major}.{version.Minor}.{version.Patch}.{version.Build}");
            Console.WriteLine($"Protocol: {version.ProtocolVersion}");
            Console.WriteLine($"Capabilities: 0x{version.Capabilities:X8}");
            Console.WriteLine($"Build type: {(version.IsDebugBuild != 0 ? "Debug" : "Release")}");
            Console.WriteLine($"Ping nonce: 0x{response.Nonce:X16}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Driver test failed: {exception.Message}");
            return 1;
        }
    }

    private static void ValidateCommonHeader(uint magic, uint protocolVersion)
    {
        if (magic != AeroMagic)
        {
            throw new InvalidOperationException($"Unexpected driver magic: 0x{magic:X8}.");
        }

        if (protocolVersion != ProtocolVersion)
        {
            throw new InvalidOperationException(
                $"Protocol mismatch. Client={ProtocolVersion}, driver={protocolVersion}.");
        }
    }

    private static TOutput InvokeNoInput<TOutput>(SafeFileHandle device, uint ioctl)
        where TOutput : struct
    {
        int outputSize = Marshal.SizeOf<TOutput>();
        IntPtr output = Marshal.AllocHGlobal(outputSize);

        try
        {
            ZeroMemory(output, outputSize);

            if (!DeviceIoControl(
                    device,
                    ioctl,
                    IntPtr.Zero,
                    0,
                    output,
                    checked((uint)outputSize),
                    out uint bytesReturned,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (bytesReturned != checked((uint)outputSize))
            {
                throw new InvalidOperationException(
                    $"Unexpected response size. Expected {outputSize}, received {bytesReturned}.");
            }

            return Marshal.PtrToStructure<TOutput>(output);
        }
        finally
        {
            Marshal.FreeHGlobal(output);
        }
    }

    private static TOutput Invoke<TInput, TOutput>(
        SafeFileHandle device,
        uint ioctl,
        TInput inputValue)
        where TInput : struct
        where TOutput : struct
    {
        int inputSize = Marshal.SizeOf<TInput>();
        int outputSize = Marshal.SizeOf<TOutput>();
        IntPtr input = Marshal.AllocHGlobal(inputSize);
        IntPtr output = Marshal.AllocHGlobal(outputSize);

        try
        {
            Marshal.StructureToPtr(inputValue, input, false);
            ZeroMemory(output, outputSize);

            if (!DeviceIoControl(
                    device,
                    ioctl,
                    input,
                    checked((uint)inputSize),
                    output,
                    checked((uint)outputSize),
                    out uint bytesReturned,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (bytesReturned != checked((uint)outputSize))
            {
                throw new InvalidOperationException(
                    $"Unexpected response size. Expected {outputSize}, received {bytesReturned}.");
            }

            return Marshal.PtrToStructure<TOutput>(output);
        }
        finally
        {
            Marshal.FreeHGlobal(input);
            Marshal.FreeHGlobal(output);
        }
    }

    private static void ZeroMemory(IntPtr buffer, int length)
    {
        byte[] zeros = new byte[length];
        Marshal.Copy(zeros, 0, buffer, length);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct PingRequest
    {
        public uint Magic;
        public uint ProtocolVersion;
        public ulong Nonce;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct PingResponse
    {
        public uint Magic;
        public uint ProtocolVersion;
        public ulong Nonce;
        public uint DriverVersionBuild;
        public uint Capabilities;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct VersionResponse
    {
        public uint Magic;
        public uint ProtocolVersion;
        public uint Major;
        public uint Minor;
        public uint Patch;
        public uint Build;
        public uint Capabilities;
        public uint IsDebugBuild;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        IntPtr inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
