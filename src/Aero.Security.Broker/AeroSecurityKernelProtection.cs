// Aero Vault. Vault is Powered by Aero.
// Copyright (c) 2026 finchproffe. Licensed under the Mozilla Public License 2.0.
// Under MPL-2.0 section 3.4 this notice must stay in every copy and fork of this file.
using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Aero.Security.BrokerHost;

/// <summary>
/// Activates the Aero Security kernel callback protection for the current
/// one-operation Broker before plaintext credential bytes are materialized.
/// </summary>
internal sealed class AeroSecurityKernelProtection : IDisposable
{
    private const string DevicePath = @"\\.\AeroSecurity";
    private const uint AeroMagic = 0x4F524541;
    private const uint ProtocolVersion = 1;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    private const uint IoctlPing = 0x0022E000;
    private const uint IoctlProtectCurrentProcess = 0x0022E008;
    private const uint IoctlUnprotectCurrentProcess = 0x0022E00C;
    private const uint IoctlGetProtectionStatusV2 = 0x0022E014;

    private const uint RequiredCapabilities = 0x0000003F;
    private const int PingRequestSize = 16;
    private const int PingResponseSize = 24;
    private const int ProtectionRequestSize = 16;
    private const int ProtectionStatusSize = 24;
    private const int ProtectionStatusV2Size = 48;

    private SafeFileHandle? _device;
    private bool _active;
    private int _disposed;

    private AeroSecurityKernelProtection(SafeFileHandle device)
    {
        _device = device;
    }

    public static bool TryActivate(
        out AeroSecurityKernelProtection? protection,
        out KernelProtectionStatus status)
    {
        protection = null;
        try
        {
            protection = Activate();
            status = new KernelProtectionStatus(
                KernelProtectionState.Active,
                ErrorCode: 0);
            return true;
        }
        catch (Win32Exception exception) when (
            exception.NativeErrorCode is 2 or 3 or 1060 or 1062)
        {
            status = new KernelProtectionStatus(
                KernelProtectionState.Unavailable,
                exception.HResult);
            return false;
        }
        catch (InvalidDataException exception)
        {
            status = new KernelProtectionStatus(
                KernelProtectionState.Incompatible,
                exception.HResult);
            return false;
        }
        catch (Exception exception)
        {
            status = new KernelProtectionStatus(
                KernelProtectionState.ActivationFailed,
                exception.HResult);
            return false;
        }
    }

    public static AeroSecurityKernelProtection Activate()
    {
        SafeFileHandle device = CreateFile(
            DevicePath,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

        if (device.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            device.Dispose();
            throw new Win32Exception(
                error,
                "Aero Security Broker could not open the Aero Security kernel driver.");
        }

        AeroSecurityKernelProtection protection = new(device);
        try
        {
            protection.VerifyDriver();
            protection.ProtectCurrentProcess();
            return protection;
        }
        catch
        {
            protection.Dispose();
            throw;
        }
    }

    private void VerifyDriver()
    {
        SafeFileHandle device = GetDevice();
        byte[] input = new byte[PingRequestSize];
        byte[] output = new byte[PingResponseSize];
        Span<byte> nonceBytes = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(nonceBytes);
        ulong nonce = BinaryPrimitives.ReadUInt64LittleEndian(nonceBytes);
        if (nonce == 0)
        {
            nonce = 1;
        }

        try
        {
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(0, 4), AeroMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4, 4), ProtocolVersion);
            BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(8, 8), nonce);

            DeviceIoControlChecked(
                device,
                IoctlPing,
                input,
                output,
                "Aero Security Broker kernel PING failed.");

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(0, 4));
            uint protocol = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(4, 4));
            ulong returnedNonce = BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(8, 8));
            uint capabilities = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(20, 4));

            if (magic != AeroMagic ||
                protocol != ProtocolVersion ||
                returnedNonce != nonce ||
                (capabilities & RequiredCapabilities) != RequiredCapabilities)
            {
                throw new InvalidDataException(
                    "Aero Security Broker rejected an incompatible kernel driver.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(output);
            CryptographicOperations.ZeroMemory(nonceBytes);
        }
    }

    private void ProtectCurrentProcess()
    {
        SafeFileHandle device = GetDevice();
        byte[] input = CreateProtectionRequest();
        byte[] output = new byte[ProtectionStatusSize];

        try
        {
            DeviceIoControlChecked(
                device,
                IoctlProtectCurrentProcess,
                input,
                output,
                "Aero Security Broker could not activate kernel process protection.");

            ValidateProtectionStatus(output);
            ValidateProtectionStatusV2();
            _active = true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(output);
        }
    }

    private void ValidateProtectionStatusV2()
    {
        byte[] output = new byte[ProtectionStatusV2Size];
        try
        {
            DeviceIoControlChecked(
                GetDevice(),
                IoctlGetProtectionStatusV2,
                null,
                output,
                "Aero Security Broker could not verify kernel protection status.");

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(0, 4));
            uint protocol = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(4, 4));
            ulong processId = BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(8, 8));
            uint active = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(16, 4));
            uint blockedProcessAccess = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(24, 4));
            uint blockedThreadAccess = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(28, 4));

            if (magic != AeroMagic ||
                protocol != ProtocolVersion ||
                processId != (ulong)Environment.ProcessId ||
                active != 1 ||
                blockedProcessAccess == 0 ||
                blockedThreadAccess == 0)
            {
                throw new InvalidDataException(
                    "Aero Security Broker kernel protection did not become active.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(output);
        }
    }

    private static void ValidateProtectionStatus(ReadOnlySpan<byte> output)
    {
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(output.Slice(0, 4));
        uint protocol = BinaryPrimitives.ReadUInt32LittleEndian(output.Slice(4, 4));
        ulong processId = BinaryPrimitives.ReadUInt64LittleEndian(output.Slice(8, 8));
        uint active = BinaryPrimitives.ReadUInt32LittleEndian(output.Slice(16, 4));
        uint blockedAccess = BinaryPrimitives.ReadUInt32LittleEndian(output.Slice(20, 4));

        if (magic != AeroMagic ||
            protocol != ProtocolVersion ||
            processId != (ulong)Environment.ProcessId ||
            active != 1 ||
            blockedAccess == 0)
        {
            throw new InvalidDataException(
                "Aero Security Broker received an invalid kernel protection response.");
        }
    }

    private static byte[] CreateProtectionRequest()
    {
        byte[] input = new byte[ProtectionRequestSize];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(0, 4), AeroMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4, 4), ProtocolVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(
            input.AsSpan(8, 8),
            checked((ulong)Environment.ProcessId));
        return input;
    }

    private SafeFileHandle GetDevice()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0 ||
            _device is null ||
            _device.IsInvalid ||
            _device.IsClosed,
            nameof(AeroSecurityKernelProtection));

        return _device;
    }

    private static void DeviceIoControlChecked(
        SafeFileHandle device,
        uint ioControlCode,
        byte[]? input,
        byte[] output,
        string errorMessage)
    {
        if (!DeviceIoControl(
                device,
                ioControlCode,
                input,
                input?.Length ?? 0,
                output,
                output.Length,
                out uint bytesReturned,
                IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), errorMessage);
        }

        if (bytesReturned != output.Length)
        {
            throw new InvalidDataException(
                $"{errorMessage} Expected {output.Length} bytes but received {bytesReturned}.");
        }
    }

    private void TryUnprotect()
    {
        if (!_active || _device is null || _device.IsInvalid || _device.IsClosed)
        {
            return;
        }

        byte[] input = CreateProtectionRequest();
        byte[] output = new byte[ProtectionStatusSize];
        try
        {
            _ = DeviceIoControl(
                _device,
                IoctlUnprotectCurrentProcess,
                input,
                input.Length,
                output,
                output.Length,
                out _,
                IntPtr.Zero);
        }
        catch
        {
            // The driver process-exit callback also clears stale protection.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(output);
            _active = false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        TryUnprotect();
        _device?.Dispose();
        _device = null;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
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
        byte[]? inputBuffer,
        int inputBufferSize,
        byte[]? outputBuffer,
        int outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}

internal enum KernelProtectionState
{
    Active,
    Unavailable,
    Incompatible,
    ActivationFailed
}

internal readonly record struct KernelProtectionStatus(
    KernelProtectionState State,
    int ErrorCode)
{
    public bool IsActive => State == KernelProtectionState.Active;
}
