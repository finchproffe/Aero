using System.Diagnostics;
using System.Runtime.InteropServices;
using Aero.Security.TestLab.Native;

namespace Aero.Security.TestLab.Scanning;

internal static class ProcessDiscovery
{
    private static readonly HashSet<string> AllowedProcessNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Aero",
        "Aero.App",
        "Aero.Security.Broker",
        "msedgewebview2"
    };

    public static IReadOnlyList<Process> Resolve(
        IReadOnlyList<int> explicitPids,
        IReadOnlyList<string> requestedNames,
        bool includeWebView2,
        bool includeBroker,
        bool allowSelf = false)
    {
        Dictionary<int, Process> selected = [];
        int currentSession = Process.GetCurrentProcess().SessionId;

        foreach (int pid in explicitPids)
        {
            Process process;
            try
            {
                process = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                continue;
            }

            try
            {
                if (process.SessionId != currentSession ||
                    !IsAllowed(process.ProcessName, allowSelf))
                {
                    process.Dispose();
                    continue;
                }
            }
            catch (InvalidOperationException)
            {
                process.Dispose();
                continue;
            }

            AddOrDispose(selected, process);
        }

        foreach (string requestedName in requestedNames)
        {
            string normalized = Path.GetFileNameWithoutExtension(requestedName);
            if (normalized.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase))
            {
                // Scanning every WebView2 process on the machine would include unrelated apps.
                // WebView2 is selected only as an Aero descendant or by explicit PID.
                continue;
            }

            if (!IsAllowed(normalized, allowSelf))
            {
                continue;
            }

            foreach (Process process in Process.GetProcessesByName(normalized))
            {
                try
                {
                    if (process.SessionId == currentSession)
                    {
                        AddOrDispose(selected, process);
                    }
                    else
                    {
                        process.Dispose();
                    }
                }
                catch (InvalidOperationException)
                {
                    process.Dispose();
                }
            }
        }

        if (includeWebView2 || includeBroker)
        {
            Dictionary<int, ProcessSnapshotEntry> snapshot = CreateProcessSnapshot();
            HashSet<int> rootPids = selected.Keys
                .Where(pid =>
                    snapshot.TryGetValue(pid, out ProcessSnapshotEntry? root) &&
                    (root.Name.Equals("Aero.exe", StringComparison.OrdinalIgnoreCase) ||
                     root.Name.Equals("Aero.App.exe", StringComparison.OrdinalIgnoreCase)))
                .ToHashSet();

            foreach (ProcessSnapshotEntry entry in snapshot.Values)
            {
                bool isAeroBroker = entry.Name.Equals(
                    "Aero.Security.Broker.exe",
                    StringComparison.OrdinalIgnoreCase);
                bool isAeroWebView = entry.Name.Equals(
                    "msedgewebview2.exe",
                    StringComparison.OrdinalIgnoreCase);
                if ((isAeroBroker && !includeBroker) ||
                    (isAeroWebView && !includeWebView2) ||
                    (!isAeroBroker && !isAeroWebView) ||
                    !IsDescendantOf(entry.ProcessId, rootPids, snapshot))
                {
                    continue;
                }

                try
                {
                    Process process = Process.GetProcessById(entry.ProcessId);
                    if (process.SessionId == currentSession)
                    {
                        AddOrDispose(selected, process);
                    }
                    else
                    {
                        process.Dispose();
                    }
                }
                catch (Exception exception) when (
                    exception is ArgumentException or InvalidOperationException)
                {
                    // Process exited between snapshot and open.
                }
            }
        }

        return selected.Values.OrderBy(process => process.Id).ToList();
    }

    private static void AddOrDispose(Dictionary<int, Process> selected, Process process)
    {
        if (!selected.TryAdd(process.Id, process))
        {
            process.Dispose();
        }
    }

    private static bool IsAllowed(string name, bool allowSelf) =>
        AllowedProcessNames.Contains(name) ||
        (allowSelf && name.Equals("Aero.Security.TestLab", StringComparison.OrdinalIgnoreCase));

    private static Dictionary<int, ProcessSnapshotEntry> CreateProcessSnapshot()
    {
        Dictionary<int, ProcessSnapshotEntry> result = [];
        nint snapshot = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.Th32csSnapProcess,
            0);
        if (snapshot == new nint(-1))
        {
            return result;
        }

        try
        {
            ProcessEntry32 entry = new()
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
                ExeFile = string.Empty
            };

            if (!NativeMethods.Process32First(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                result[(int)entry.ProcessId] = new ProcessSnapshotEntry(
                    (int)entry.ProcessId,
                    (int)entry.ParentProcessId,
                    entry.ExeFile ?? string.Empty);
            }
            while (NativeMethods.Process32Next(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return result;
    }

    private static bool IsDescendantOf(
        int processId,
        HashSet<int> roots,
        IReadOnlyDictionary<int, ProcessSnapshotEntry> snapshot)
    {
        HashSet<int> visited = [];
        int current = processId;
        while (snapshot.TryGetValue(current, out ProcessSnapshotEntry? entry) &&
               visited.Add(current))
        {
            if (roots.Contains(entry.ParentProcessId))
            {
                return true;
            }

            current = entry.ParentProcessId;
        }

        return false;
    }

    private sealed record ProcessSnapshotEntry(int ProcessId, int ParentProcessId, string Name);
}
