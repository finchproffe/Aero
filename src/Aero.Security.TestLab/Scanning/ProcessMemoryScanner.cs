using System.Diagnostics;
using Aero.Security.TestLab.Models;
using Aero.Security.TestLab.Native;
using Microsoft.Win32.SafeHandles;

namespace Aero.Security.TestLab.Scanning;

internal sealed class ProcessMemoryScanner
{
    private const int ChunkSize = 1024 * 1024;
    private const int MaximumHitsPerPattern = 12;

    public ProcessMemoryScanner(MarkerSet markerSet, long maximumBytesPerProcess)
    {
        _markerSet = markerSet;
        _maximumBytesPerProcess = maximumBytesPerProcess;
    }

    private readonly MarkerSet _markerSet;
    private readonly long _maximumBytesPerProcess;

    public int ProcessesVisited { get; private set; }

    public long BytesRead { get; private set; }

    public int AccessDeniedCount { get; private set; }

    public int ErrorCount { get; private set; }

    public IReadOnlyList<ScanFinding> Scan(
        Process process,
        CancellationToken cancellationToken = default)
    {
        List<ScanFinding> findings = [];
        ProcessesVisited++;
        string target;
        try
        {
            target = $"{process.ProcessName}.exe (PID {process.Id})";
        }
        catch (InvalidOperationException)
        {
            ErrorCount++;
            findings.Add(new ScanFinding(
                FindingSeverity.Warning,
                "memory",
                "process-exited",
                $"PID {process.Id}",
                "The process exited before the memory scan started."));
            return findings;
        }

        using SafeProcessHandle handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryInformation | NativeMethods.ProcessVmRead,
            inheritHandle: false,
            process.Id);

        if (handle.IsInvalid)
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (error == 5)
            {
                AccessDeniedCount++;
            }
            else
            {
                ErrorCount++;
            }

            findings.Add(new ScanFinding(
                FindingSeverity.Warning,
                "memory",
                "process-open-failed",
                target,
                $"OpenProcess failed with Win32 error {error}."));
            return findings;
        }

        NativeMethods.GetSystemInfo(out SystemInfo systemInfo);
        nuint address = (nuint)systemInfo.MinimumApplicationAddress;
        nuint maximumAddress = (nuint)systemInfo.MaximumApplicationAddress;
        long processBytesRead = 0;
        Dictionary<string, int> hitCounts = _markerSet.Patterns
            .ToDictionary(pattern => pattern.Name, _ => 0, StringComparer.Ordinal);

        int overlapCapacity = Math.Max(0, _markerSet.MaximumPatternLength - 1);
        byte[] readBuffer = new byte[ChunkSize];
        byte[] window = new byte[ChunkSize + overlapCapacity];
        byte[] tail = new byte[overlapCapacity];

        try
        {
        while (address < maximumAddress && processBytesRead < _maximumBytesPerProcess)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nuint queried = NativeMethods.VirtualQueryEx(
                handle,
                (nint)address,
                out MemoryBasicInformation memory,
                (nuint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryBasicInformation>());
            if (queried == 0)
            {
                address += systemInfo.PageSize;
                continue;
            }

            nuint regionBase = (nuint)memory.BaseAddress;
            nuint regionSize = memory.RegionSize;
            if (regionSize == 0)
            {
                address += systemInfo.PageSize;
                continue;
            }

            if (memory.State == NativeMethods.MemCommit &&
                (memory.Protect & NativeMethods.PageNoAccess) == 0 &&
                (memory.Protect & NativeMethods.PageGuard) == 0)
            {
                long remainingBudget = _maximumBytesPerProcess - processBytesRead;
                nuint budget = (nuint)Math.Max(0L, remainingBudget);
                long bytesToScan = (long)(regionSize < budget ? regionSize : budget);
                ScanRegion(
                    handle,
                    target,
                    regionBase,
                    bytesToScan,
                    hitCounts,
                    findings,
                    ref processBytesRead,
                    readBuffer,
                    window,
                    tail,
                    cancellationToken);
            }

            nuint next = regionBase + regionSize;
            address = next > address ? next : address + systemInfo.PageSize;
        }
        }
        finally
        {
            Array.Clear(readBuffer);
            Array.Clear(window);
            Array.Clear(tail);
        }

        BytesRead += processBytesRead;
        if (processBytesRead >= _maximumBytesPerProcess)
        {
            findings.Add(new ScanFinding(
                FindingSeverity.Info,
                "memory",
                "process-scan-truncated",
                target,
                $"The scan reached the {_maximumBytesPerProcess / (1024 * 1024)} MB per-process limit."));
        }

        return findings;
    }

    private void ScanRegion(
        SafeProcessHandle handle,
        string target,
        nuint regionBase,
        long regionBytes,
        Dictionary<string, int> hitCounts,
        List<ScanFinding> findings,
        ref long processBytesRead,
        byte[] readBuffer,
        byte[] window,
        byte[] tail,
        CancellationToken cancellationToken)
    {
        int overlapCapacity = tail.Length;
        int tailLength = 0;
        long regionOffset = 0;

        while (regionOffset < regionBytes)
        {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = (int)Math.Min(readBuffer.Length, regionBytes - regionOffset);
                _ = NativeMethods.ReadProcessMemory(
                    handle,
                    (nint)(regionBase + (nuint)regionOffset),
                    readBuffer,
                    (nuint)requested,
                    out nuint nativeRead);
                int read = nativeRead > int.MaxValue ? int.MaxValue : (int)nativeRead;
                if (read <= 0)
                {
                    regionOffset += requested;
                    tailLength = 0;
                    continue;
                }

                if (tailLength > 0)
                {
                    Buffer.BlockCopy(tail, 0, window, 0, tailLength);
                }

                Buffer.BlockCopy(readBuffer, 0, window, tailLength, read);
                int windowLength = tailLength + read;
                long windowBaseOffset = regionOffset - tailLength;
                ReadOnlySpan<byte> span = window.AsSpan(0, windowLength);

                foreach (MarkerPattern pattern in _markerSet.Patterns)
                {
                    if (hitCounts[pattern.Name] >= MaximumHitsPerPattern)
                    {
                        continue;
                    }

                    int cursor = 0;
                    while (cursor <= span.Length - pattern.Bytes.Length &&
                           hitCounts[pattern.Name] < MaximumHitsPerPattern)
                    {
                        int relative = span[cursor..].IndexOf(pattern.Bytes);
                        if (relative < 0)
                        {
                            break;
                        }

                        int index = cursor + relative;
                        nuint absolute = regionBase + (nuint)Math.Max(0L, windowBaseOffset + index);
                        findings.Add(new ScanFinding(
                            FindingSeverity.Critical,
                            "memory",
                            "plaintext-marker-in-process",
                            target,
                            "The test secret marker is present in readable process memory.",
                            pattern.Name,
                            $"0x{absolute:X}"));
                        hitCounts[pattern.Name]++;
                        cursor = index + 1;
                    }
                }

                processBytesRead += read;
                regionOffset += read;
                tailLength = Math.Min(overlapCapacity, windowLength);
                if (tailLength > 0)
                {
                    Buffer.BlockCopy(window, windowLength - tailLength, tail, 0, tailLength);
                }
        }
    }
}
