using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Runtime.Versioning;

namespace Aero.Security.Driver.Setup;

internal readonly record struct DriverStatusResult(
    bool Success,
    Version Version,
    uint Capabilities,
    bool DebugBuild,
    ulong PingNonce,
    bool ProtectionActive,
    ulong ProtectedProcessId,
    uint ProtectionGeneration,
    uint BlockedProcessAccessMask,
    uint BlockedThreadAccessMask,
    ulong ProcessHandleDowngrades,
    ulong ThreadHandleDowngrades);

[SupportedOSPlatform("windows")]
internal static class DriverProtocol
{
    private const string DevicePath = @"\\.\AeroSecurity";
    private const uint AeroMagic = 0x4F524541;
    private const uint ProtocolVersion = 1;
    private const uint IoctlPing = 0x0022E000;
    private const uint IoctlGetVersion = 0x0022E004;
    private const uint IoctlGetProtectionStatusV2 = 0x0022E014;

    public static DriverStatusResult PingAndGetVersion()
    {
        using SafeFileHandle handle = NativeMethods.CreateFile(
            DevicePath,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            0,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileAttributeNormal,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw NativeMethods.CreateWin32Exception("Cannot open \\.\\AeroSecurity");
        }

        ulong nonce = CreateNonce();
        byte[] pingInput = new byte[16];
        byte[] pingOutput = new byte[24];

        BinaryPrimitives.WriteUInt32LittleEndian(pingInput.AsSpan(0, 4), AeroMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(pingInput.AsSpan(4, 4), ProtocolVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(pingInput.AsSpan(8, 8), nonce);

        if (!NativeMethods.DeviceIoControl(
                handle,
                IoctlPing,
                pingInput,
                pingInput.Length,
                pingOutput,
                pingOutput.Length,
                out uint pingBytes,
                IntPtr.Zero))
        {
            throw NativeMethods.CreateWin32Exception("Aero Security PING failed");
        }

        if (pingBytes < pingOutput.Length)
        {
            throw new InvalidDataException($"PING response is too small: {pingBytes} bytes.");
        }

        uint pingMagic = BinaryPrimitives.ReadUInt32LittleEndian(pingOutput.AsSpan(0, 4));
        uint pingProtocol = BinaryPrimitives.ReadUInt32LittleEndian(pingOutput.AsSpan(4, 4));
        ulong returnedNonce = BinaryPrimitives.ReadUInt64LittleEndian(pingOutput.AsSpan(8, 8));
        uint capabilities = BinaryPrimitives.ReadUInt32LittleEndian(pingOutput.AsSpan(20, 4));

        if (pingMagic != AeroMagic || pingProtocol != ProtocolVersion || returnedNonce != nonce)
        {
            throw new InvalidDataException("The driver returned an invalid PING response.");
        }

        byte[] versionOutput = new byte[32];
        if (!NativeMethods.DeviceIoControl(
                handle,
                IoctlGetVersion,
                null,
                0,
                versionOutput,
                versionOutput.Length,
                out uint versionBytes,
                IntPtr.Zero))
        {
            throw NativeMethods.CreateWin32Exception("Cannot read the Aero Security Driver version");
        }

        if (versionBytes < versionOutput.Length)
        {
            throw new InvalidDataException($"Version response is too small: {versionBytes} bytes.");
        }

        uint versionMagic = BinaryPrimitives.ReadUInt32LittleEndian(versionOutput.AsSpan(0, 4));
        uint versionProtocol = BinaryPrimitives.ReadUInt32LittleEndian(versionOutput.AsSpan(4, 4));
        uint major = BinaryPrimitives.ReadUInt32LittleEndian(versionOutput.AsSpan(8, 4));
        uint minor = BinaryPrimitives.ReadUInt32LittleEndian(versionOutput.AsSpan(12, 4));
        uint patch = BinaryPrimitives.ReadUInt32LittleEndian(versionOutput.AsSpan(16, 4));
        uint build = BinaryPrimitives.ReadUInt32LittleEndian(versionOutput.AsSpan(20, 4));
        uint versionCapabilities = BinaryPrimitives.ReadUInt32LittleEndian(versionOutput.AsSpan(24, 4));
        bool isDebugBuild = BinaryPrimitives.ReadUInt32LittleEndian(versionOutput.AsSpan(28, 4)) != 0;

        if (versionMagic != AeroMagic || versionProtocol != ProtocolVersion)
        {
            throw new InvalidDataException("The driver returned an incompatible version response.");
        }

        if (versionCapabilities != capabilities)
        {
            throw new InvalidDataException("PING and version capability masks do not match.");
        }

        byte[] protectionOutput = new byte[48];
        if (!NativeMethods.DeviceIoControl(
                handle,
                IoctlGetProtectionStatusV2,
                null,
                0,
                protectionOutput,
                protectionOutput.Length,
                out uint protectionBytes,
                IntPtr.Zero))
        {
            throw NativeMethods.CreateWin32Exception(
                "Cannot read Aero Security Broker protection status");
        }

        if (protectionBytes < protectionOutput.Length)
        {
            throw new InvalidDataException(
                $"Protection status response is too small: {protectionBytes} bytes.");
        }

        uint protectionMagic =
            BinaryPrimitives.ReadUInt32LittleEndian(protectionOutput.AsSpan(0, 4));
        uint protectionProtocol =
            BinaryPrimitives.ReadUInt32LittleEndian(protectionOutput.AsSpan(4, 4));
        ulong protectedProcessId =
            BinaryPrimitives.ReadUInt64LittleEndian(protectionOutput.AsSpan(8, 8));
        bool protectionActive =
            BinaryPrimitives.ReadUInt32LittleEndian(protectionOutput.AsSpan(16, 4)) != 0;
        uint protectionGeneration =
            BinaryPrimitives.ReadUInt32LittleEndian(protectionOutput.AsSpan(20, 4));
        uint blockedProcessAccess =
            BinaryPrimitives.ReadUInt32LittleEndian(protectionOutput.AsSpan(24, 4));
        uint blockedThreadAccess =
            BinaryPrimitives.ReadUInt32LittleEndian(protectionOutput.AsSpan(28, 4));
        ulong processHandleDowngrades =
            BinaryPrimitives.ReadUInt64LittleEndian(protectionOutput.AsSpan(32, 8));
        ulong threadHandleDowngrades =
            BinaryPrimitives.ReadUInt64LittleEndian(protectionOutput.AsSpan(40, 8));

        if (protectionMagic != AeroMagic ||
            protectionProtocol != ProtocolVersion ||
            (protectionActive && protectedProcessId == 0) ||
            (!protectionActive && protectedProcessId != 0))
        {
            throw new InvalidDataException(
                "The driver returned an incompatible protection status response.");
        }

        return new DriverStatusResult(
            true,
            new Version(checked((int)major), checked((int)minor), checked((int)patch), checked((int)build)),
            capabilities,
            isDebugBuild,
            returnedNonce,
            protectionActive,
            protectedProcessId,
            protectionGeneration,
            blockedProcessAccess,
            blockedThreadAccess,
            processHandleDowngrades,
            threadHandleDowngrades);
    }

    private static ulong CreateNonce()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        ulong nonce = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return nonce == 0 ? 1UL : nonce;
    }
}
