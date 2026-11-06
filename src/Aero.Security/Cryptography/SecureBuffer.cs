using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Aero.App.Security;

/// <summary>
/// Owns a small secret in unmanaged memory. The allocation is locked in RAM when
/// Windows permits it, is never exposed as a managed array unless explicitly copied,
/// and is always zeroed before release.
/// </summary>
internal sealed unsafe partial class SecureBuffer : IDisposable
{
    private IntPtr _pointer;
    private readonly int _length;
    private readonly int _allocationLength;
    private bool _pageLocked;
    private bool _werExcluded;
    private int _disposed;

    private SecureBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _length = length;
        _allocationLength = Math.Max(length, 1);
        _pointer = Marshal.AllocHGlobal(_allocationLength);
        new Span<byte>((void*)_pointer, _allocationLength).Clear();
        _pageLocked = NativeMethods.VirtualLock(_pointer, (nuint)_allocationLength);
        _werExcluded = NativeMethods.WerRegisterExcludedMemoryBlock(
            _pointer,
            checked((uint)_allocationLength)) == 0;
    }

    public int Length
    {
        get
        {
            ThrowIfDisposed();
            return _length;
        }
    }

    public bool IsPageLocked
    {
        get
        {
            ThrowIfDisposed();
            return _pageLocked;
        }
    }

    public ReadOnlySpan<byte> ReadOnlySpan
    {
        get
        {
            ThrowIfDisposed();
            return new ReadOnlySpan<byte>((void*)_pointer, _length);
        }
    }

    internal Span<byte> Span
    {
        get
        {
            ThrowIfDisposed();
            return new Span<byte>((void*)_pointer, _length);
        }
    }

    public static SecureBuffer From(ReadOnlySpan<byte> source)
    {
        SecureBuffer buffer = new(source.Length);
        try
        {
            source.CopyTo(buffer.Span);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    public SecureBuffer Clone() => From(ReadOnlySpan);

    public byte[] CopyToArray()
    {
        byte[] copy = GC.AllocateUninitializedArray<byte>(_length);
        ReadOnlySpan<byte> source = ReadOnlySpan;
        source.CopyTo(copy);
        return copy;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0 || _pointer == IntPtr.Zero,
            this);
    }

    private void Release()
    {
        IntPtr pointer = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        try
        {
            CryptographicOperations.ZeroMemory(
                new Span<byte>((void*)pointer, _allocationLength));
        }
        finally
        {
            if (_werExcluded)
            {
                _ = NativeMethods.WerUnregisterExcludedMemoryBlock(pointer);
                _werExcluded = false;
            }

            if (_pageLocked)
            {
                _ = NativeMethods.VirtualUnlock(pointer, (nuint)_allocationLength);
                _pageLocked = false;
            }

            Marshal.FreeHGlobal(pointer);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Release();
        GC.SuppressFinalize(this);
    }

    ~SecureBuffer()
    {
        Release();
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool VirtualLock(IntPtr address, nuint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool VirtualUnlock(IntPtr address, nuint size);

        [LibraryImport("kernel32.dll")]
        internal static partial int WerRegisterExcludedMemoryBlock(IntPtr address, uint size);

        [LibraryImport("kernel32.dll")]
        internal static partial int WerUnregisterExcludedMemoryBlock(IntPtr address);
    }
}
