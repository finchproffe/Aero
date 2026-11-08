using System.Diagnostics;
using System.Globalization;
using System.Text;
using Aero.Security.TestLab.Models;
using Aero.Security.TestLab.Reporting;
using Aero.Security.TestLab.Scanning;
using Aero.Security.TestLab.Server;

namespace Aero.Security.TestLab;

internal static class Program
{
    private const string Version = "0.25.0.0";

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Aero.Security.TestLab runs on Windows only.");
            return 2;
        }

        string command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
        CommandLine options = CommandLine.Parse(args.Skip(1));

        try
        {
            return command switch
            {
                "marker" => RunMarker(options),
                "node-lock" => RunNodeLockInspection(options),
                "scan" => await RunScanAsync(options, allowSelf: false),
                "self-test" => await RunSelfTestAsync(options),
                "serve" => await RunServerAsync(options),
                "help" or "--help" or "-h" => ShowHelp(),
                _ => UnknownCommand(command)
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"TestLab failed: {exception.Message}");
            return 3;
        }
    }

    private static int RunMarker(CommandLine options)
    {
        string kind = options.GetSingle("kind") ?? "PASSWORD";
        Console.WriteLine(MarkerSet.Generate(kind));
        return 0;
    }

    private static int RunNodeLockInspection(CommandLine options)
    {
        string securityDirectory = Path.GetFullPath(
            options.GetSingle("security") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Aero",
                "Security"));
        NodeLockInspection inspection = NodeLockInspector.Inspect(securityDirectory);

        Console.WriteLine("Aero Node Lock inspection");
        Console.WriteLine($"Security directory: {securityDirectory}");
        Console.WriteLine($"Envelope present: {inspection.Exists}");
        Console.WriteLine($"Structurally valid: {inspection.StructurallyValid}");
        Console.WriteLine($"Legacy Hello-only envelope present: {inspection.LegacyHelloOnlyEnvelopePresent}");
        Console.WriteLine($"Envelope length: {inspection.FileLength}");
        Console.WriteLine(inspection.Message);
        Console.WriteLine("No node identifier, key material, or ciphertext bytes were printed.");

        if (!inspection.Exists)
        {
            return 4;
        }
        if (!inspection.StructurallyValid)
        {
            return 10;
        }
        return inspection.LegacyHelloOnlyEnvelopePresent ? 4 : 0;
    }

    private static async Task<int> RunServerAsync(CommandLine options)
    {
        int port = options.GetInt32("port", 8443, 1024, 65535);
        string certificateDirectory = Path.GetFullPath(
            options.GetSingle("cert-dir") ?? Path.Combine(Environment.CurrentDirectory, "TestLabCert"));
        using CancellationTokenSource cancellation = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        return await TestSiteServer.RunAsync(port, certificateDirectory, cancellation.Token);
    }

    private static async Task<int> RunSelfTestAsync(CommandLine options)
    {
        string marker = MarkerSet.Generate("SELFTEST");
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "Aero.Security.TestLab",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            File.WriteAllText(Path.Combine(temporaryRoot, "utf8.txt"), marker, Encoding.UTF8);
            File.WriteAllText(Path.Combine(temporaryRoot, "utf16.txt"), marker, Encoding.Unicode);
            File.WriteAllText(
                Path.Combine(temporaryRoot, "base64.txt"),
                Convert.ToBase64String(Encoding.UTF8.GetBytes(marker)),
                Encoding.ASCII);

            List<string> forwarded =
            [
                "--marker", marker,
                "--profile", temporaryRoot,
                "--pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                "--deep-files",
                "--output", options.GetSingle("output") ?? Path.Combine(Environment.CurrentDirectory, "TestLabReports")
            ];

            int result = await RunScanAsync(CommandLine.Parse(forwarded), allowSelf: true);
            if (result == 10)
            {
                Console.WriteLine("Self-test passed: expected marker exposures were detected.");
                return 0;
            }

            Console.Error.WriteLine("Self-test failed: expected marker exposures were not detected.");
            return 5;
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
            catch
            {
                // Temporary test fixtures contain only generated markers.
            }
        }
    }

    private static async Task<int> RunScanAsync(CommandLine options, bool allowSelf)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string marker = options.GetRequiredSingle("marker");
        MarkerSet markerSet = MarkerSet.Create(marker);
        long maximumFileBytes = options.GetInt64("max-file-mb", 256, 1, 2048) * 1024L * 1024L;
        long maximumProcessBytes = options.GetInt64("max-process-mb", 1024, 16, 8192) * 1024L * 1024L;
        bool deepFiles = options.HasFlag("deep-files");
        bool scanFiles = !options.HasFlag("no-files");
        bool scanMemory = !options.HasFlag("no-memory");
        bool includeWebView2 = options.HasFlag("include-webview2");
        bool includeBroker = options.HasFlag("include-broker") || includeWebView2;
        int memorySamples = options.GetInt32("samples", 1, 1, 100);
        int sampleDelayMs = options.GetInt32("sample-delay-ms", 250, 50, 5000);
        string scenarioLabel = options.GetSingle("label") ?? "unspecified";
        string output = Path.GetFullPath(
            options.GetSingle("output") ?? Path.Combine(Environment.CurrentDirectory, "TestLabReports"));

        List<string> profileRoots = options.GetMany("profile")
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (scanFiles && profileRoots.Count == 0)
        {
            string defaultRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Aero");
            if (Directory.Exists(defaultRoot))
            {
                profileRoots.Add(defaultRoot);
            }
        }

        List<string> backupPaths = options.GetMany("backup")
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (string root in profileRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            backupPaths.AddRange(EnumerateBackupsSafely(root));
        }
        backupPaths = backupPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        List<int> explicitPids = options.GetMany("pid")
            .Select(value => int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int pid) ? pid : -1)
            .Where(pid => pid > 0)
            .Distinct()
            .ToList();
        List<string> requestedNames = options.GetMany("process");
        if (scanMemory && explicitPids.Count == 0 && requestedNames.Count == 0)
        {
            requestedNames.Add(allowSelf ? "Aero.Security.TestLab" : "Aero");
        }

        List<ScanFinding> findings = [];
        List<string> notes =
        [
            "The report stores only the marker fingerprint, never the raw marker.",
            "A clean result is evidence for the tested state only, not proof against every malware technique.",
            "WebView2 renderer memory can legitimately contain the marker briefly while a password field is populated.",
            $"Memory discovery: {memorySamples} sample(s), {sampleDelayMs} ms apart; each PID is scanned once."
        ];

        FileExposureScanner fileScanner = new(markerSet, maximumFileBytes, deepFiles);
        BackupExposureScanner backupScanner = new(markerSet, maximumFileBytes);
        if (scanFiles)
        {
            foreach (string root in profileRoots)
            {
                findings.AddRange(fileScanner.ScanRoot(root));
            }

            foreach (string backup in backupPaths)
            {
                findings.AddRange(backupScanner.Scan(backup));
            }
        }

        ProcessMemoryScanner memoryScanner = new(markerSet, maximumProcessBytes);
        HashSet<int> requestedProcessIds = [];
        HashSet<int> scannedProcessIds = [];
        bool matchedAnyProcess = false;
        if (scanMemory)
        {
            for (int sample = 0; sample < memorySamples; sample++)
            {
                IReadOnlyList<Process> processes = ProcessDiscovery.Resolve(
                    explicitPids,
                    requestedNames,
                    includeWebView2,
                    includeBroker,
                    allowSelf);
                try
                {
                    matchedAnyProcess |= processes.Count > 0;
                    foreach (Process process in processes)
                    {
                        requestedProcessIds.Add(process.Id);
                        if (scannedProcessIds.Add(process.Id))
                        {
                            findings.AddRange(memoryScanner.Scan(process));
                        }
                    }
                }
                finally
                {
                    foreach (Process process in processes)
                    {
                        process.Dispose();
                    }
                }

                if (sample + 1 < memorySamples)
                {
                    await Task.Delay(sampleDelayMs).ConfigureAwait(false);
                }
            }

            if (!matchedAnyProcess)
            {
                findings.Add(new ScanFinding(
                    FindingSeverity.Warning,
                    "memory",
                    "no-allowed-processes",
                    string.Join(", ", requestedNames),
                    "No running allowlisted Aero process matched any memory sample."));
            }
        }

        DateTimeOffset finished = DateTimeOffset.UtcNow;
        ScanStatistics statistics = new(
            fileScanner.FilesVisited + backupScanner.FilesVisited,
            fileScanner.BytesRead + backupScanner.BytesRead,
            memoryScanner.ProcessesVisited,
            memoryScanner.BytesRead,
            memoryScanner.AccessDeniedCount,
            fileScanner.ErrorCount + backupScanner.ErrorCount + memoryScanner.ErrorCount);
        TestLabReport report = new(
            SchemaVersion: 1,
            TestLabVersion: Version,
            StartedAtUtc: started,
            FinishedAtUtc: finished,
            MachineName: Environment.MachineName,
            WindowsUser: Environment.UserName,
            MarkerFingerprint: markerSet.Fingerprint,
            MarkerLength: markerSet.MarkerLength,
            ScenarioLabel: scenarioLabel,
            DeepFileScan: deepFiles,
            MaximumFileBytes: maximumFileBytes,
            MaximumProcessBytes: maximumProcessBytes,
            ProfileRoots: profileRoots,
            BackupPaths: backupPaths,
            RequestedProcessIds: requestedProcessIds.Order().ToList(),
            Statistics: statistics,
            Findings: findings,
            Notes: notes);

        (string jsonPath, string textPath) = ReportWriter.Write(report, output);
        PrintSummary(report, jsonPath, textPath);

        int severe = findings.Count(finding =>
            finding.Severity is FindingSeverity.Critical or FindingSeverity.High);
        return severe > 0 ? 10 : statistics.ErrorCount > 0 ? 4 : 0;
    }

    private static void PrintSummary(TestLabReport report, string jsonPath, string textPath)
    {
        Console.WriteLine();
        Console.WriteLine("Aero.Security.TestLab completed");
        Console.WriteLine($"Marker fingerprint: {report.MarkerFingerprint}");
        Console.WriteLine($"Critical: {report.Findings.Count(item => item.Severity == FindingSeverity.Critical)}");
        Console.WriteLine($"High: {report.Findings.Count(item => item.Severity == FindingSeverity.High)}");
        Console.WriteLine($"Warnings: {report.Findings.Count(item => item.Severity == FindingSeverity.Warning)}");
        Console.WriteLine($"Files scanned: {report.Statistics.FilesVisited}");
        Console.WriteLine($"Processes scanned: {report.Statistics.ProcessesVisited}");
        Console.WriteLine($"JSON: {jsonPath}");
        Console.WriteLine($"Text: {textPath}");
        Console.WriteLine();

        foreach (ScanFinding finding in report.Findings
                     .Where(item => item.Severity is FindingSeverity.Critical or FindingSeverity.High)
                     .Take(20))
        {
            Console.WriteLine($"[{finding.Severity}] {finding.Rule}: {finding.Target} {finding.Location}");
        }
    }


    private static IEnumerable<string> EnumerateBackupsSafely(string root)
    {
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.aerobackup", SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                yield return Path.GetFullPath(file);
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string child in children)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(child);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static int ShowHelp()
    {
        Console.WriteLine("""
Aero.Security.TestLab 0.25.0.0

Commands:
  marker [--kind PASSWORD]
      Generate a random fake marker beginning with AERO_TEST_.

  node-lock [--security PATH]
      Validate only the Node Lock envelope structure. No node ID or key bytes are printed.

  serve [--port 8443] [--cert-dir PATH]
      Start a loopback-only HTTPS login test page. It never logs or stores form values.

  scan --marker AERO_TEST_... [options]
      Scan selected Aero profile files, backups, and allowlisted process memory.

  self-test [--output PATH]
      Verify file-pattern and process-memory scanning using generated test data.

Scan options:
  --profile PATH             Profile/root directory. May be repeated.
  --backup PATH              .aerobackup file. May be repeated.
  --pid NUMBER               Explicit allowlisted process PID. May be repeated.
  --process NAME             Aero or Aero.Security.Broker. May be repeated.
  --include-broker           Scan the on-demand Broker descendant of selected Aero processes.
  --include-webview2         Scan Broker and WebView2 descendants of selected Aero processes.
  --samples NUMBER           Repeat memory discovery/scanning to catch the one-shot Broker, default 1.
  --sample-delay-ms NUMBER   Delay between samples, default 250 ms.
  --label NAME               Scenario name, for example idle, filled, hidden, after-navigation.
  --deep-files               Include cache-like directories and all file types.
  --max-file-mb NUMBER       Per-file/backup-entry limit, default 256 MB.
  --max-process-mb NUMBER    Per-process readable-memory limit, default 1024 MB.
  --no-files                 Skip profile and backup scanning.
  --no-memory                Skip process-memory scanning.
  --output PATH              Report directory.

Safety limits:
  * Only markers beginning with AERO_TEST_ are accepted.
  * Only Aero, Aero.Security.Broker and Aero-owned WebView2 processes are scanned.
  * No raw memory, cookies, passwords, or surrounding bytes are written to reports.
  * TestLab has no exfiltration or persistence functionality.

Exit codes:
  0  Scan is clean, or Node Lock is valid without a legacy bypass envelope.
  4  Scan has read warnings, or Node Lock is absent/awaiting legacy cleanup.
  10 High/critical marker exposure or an invalid Node Lock envelope was found.
""");
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Run with 'help' to see available commands.");
        return 2;
    }

    private sealed class CommandLine
    {
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

        public static CommandLine Parse(IEnumerable<string> arguments)
        {
            CommandLine result = new();
            string[] items = arguments.ToArray();
            for (int index = 0; index < items.Length; index++)
            {
                string item = items[index];
                if (!item.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                string key = item[2..];
                if (index + 1 >= items.Length || items[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    result._flags.Add(key);
                    continue;
                }

                string value = items[++index];
                if (!result._values.TryGetValue(key, out List<string>? values))
                {
                    values = [];
                    result._values[key] = values;
                }

                values.Add(value);
            }

            return result;
        }

        public bool HasFlag(string key) => _flags.Contains(key);

        public string? GetSingle(string key) =>
            _values.TryGetValue(key, out List<string>? values) ? values.LastOrDefault() : null;

        public string GetRequiredSingle(string key) =>
            GetSingle(key) ?? throw new ArgumentException($"Missing required option --{key}.");

        public List<string> GetMany(string key) =>
            _values.TryGetValue(key, out List<string>? values) ? [.. values] : [];

        public int GetInt32(string key, int defaultValue, int minimum, int maximum)
        {
            string? text = GetSingle(key);
            if (text is null)
            {
                return defaultValue;
            }

            if (!int.TryParse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int value)
                || value < minimum
                || value > maximum)
            {
                throw new ArgumentException($"--{key} must be between {minimum} and {maximum}.");
            }

            return value;
        }

        public long GetInt64(string key, long defaultValue, long minimum, long maximum)
        {
            string? text = GetSingle(key);
            if (text is null)
            {
                return defaultValue;
            }

            if (!long.TryParse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long value)
                || value < minimum
                || value > maximum)
            {
                throw new ArgumentException($"--{key} must be between {minimum} and {maximum}.");
            }

            return value;
        }
    }
}
