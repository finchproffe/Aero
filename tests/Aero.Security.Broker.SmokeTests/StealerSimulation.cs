using Aero.App.Security;
using Aero.Security.BrokerHost;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;

/// <summary>
/// Plays the part of an info-stealer running as the same Windows user, using the default methods
/// real stealers use against browsers: read the profile files, unwrap them with the user's DPAPI,
/// open the browser processes for memory reads, dumps or injection, read windows through UI
/// Automation, and screenshot. It only ever targets Aero components with canaries this test
/// planted, in temporary profiles and processes it started itself. Nothing is decrypted for real
/// or sent anywhere; every probe is an access check or a canary search.
/// Each attack also runs once against an unprotected control, so a pass cannot come from a
/// probe that simply does not work.
/// </summary>
internal static partial class StealerSimulation
{
    private const string VictimSwitch = "--stealer-sim-victim";
    private const string RevealOrigin = "https://stealer-sim.aero.test";

    // CREATE_THREAD | VM_OPERATION | VM_READ | VM_WRITE | SUSPEND_RESUME | WRITE_DAC | WRITE_OWNER.
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessInjection = 0x0002 | 0x0008 | 0x0020;
    private const uint ProcessAllAccess = 0x001FFFFF;
    private const uint ProcessSuspendResume = 0x0800;
    private const uint ProcessDupHandle = 0x0040;
    private const uint ProcessQueryLimited = 0x1000;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint Synchronize = 0x00100000;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Stealer simulation: " + message);
        }
    }

    private static void Report(string attack, string outcome) =>
        Console.WriteLine($"  [stealer-sim] {attack,-58} {outcome}");

    private static string NewCanary(string kind) =>
        $"AERO-{kind}-CANARY-{Convert.ToHexString(RandomNumberGenerator.GetBytes(10))}";

    // ---------------------------------------------------------------- victim process

    public static bool IsVictimLaunch(string[] args) => args.Length == 2 && args[0] == VictimSwitch;

    /// <summary>
    /// Child process standing in for Aero.exe: the same production hardening call, then it holds
    /// a canary in ordinary managed memory (as a typed password or cookie would be).
    /// </summary>
    public static int RunVictim(string[] args)
    {
        bool guarded = args[1] == "guarded";
        if (guarded && !ProcessAccessGuard.HardenCurrentProcess())
        {
            Console.WriteLine("harden-failed");
        }

        string canary = Console.ReadLine() ?? string.Empty;
        byte[] utf8 = Encoding.UTF8.GetBytes(canary);
        Console.WriteLine("ready");
        _ = Console.ReadLine(); // the test closes stdin when done
        GC.KeepAlive(canary);
        GC.KeepAlive(utf8);
        return 0;
    }

    private static Process StartVictim(bool guarded, string canary)
    {
        Process victim = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{VictimSwitch} {(guarded ? "guarded" : "control")}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        })!;
        // The canary goes over stdin: a command line is readable by any same-user process.
        victim.StandardInput.WriteLine(canary);
        string? line = victim.StandardOutput.ReadLine();
        Require(line == "ready", $"victim did not start ({line}).");
        return victim;
    }

    // ---------------------------------------------------------------- attacks

    public static void RunProcessAttacks()
    {
        string canary = NewCanary("MEMORY");
        using Process control = StartVictim(guarded: false, canary);
        using Process guarded = StartVictim(guarded: true, canary);
        try
        {
            // Control: an unguarded process gives the canary up, so the scanner itself works.
            using (ProcessHandle handle = ProcessHandle.Open(ProcessVmRead | ProcessQueryInformation, control.Id))
            {
                Require(!handle.IsInvalid && MemoryContains(handle, Encoding.Unicode.GetBytes(canary)),
                    "the memory scanner could not find the canary in an unprotected control process.");
            }
            Report("ReadProcessMemory on an unprotected control", "canary FOUND (control, expected)");

            Require(Denied(guarded.Id, ProcessVmRead | ProcessQueryInformation),
                "a same-user process can open Aero for memory reads (cookie / password scraping).");
            Report("OpenProcess(VM_READ) + scan", "denied");
            Require(Denied(guarded.Id, ProcessAllAccess),
                "a same-user process can open Aero with full access (MiniDumpWriteDump).");
            Report("OpenProcess(ALL_ACCESS) for MiniDumpWriteDump", "denied");
            Require(Denied(guarded.Id, ProcessInjection),
                "a same-user process can inject into Aero (VirtualAllocEx + CreateRemoteThread).");
            Report("OpenProcess(VM_WRITE|CREATE_THREAD) for injection", "denied");
            Require(Denied(guarded.Id, ProcessSuspendResume),
                "a same-user process can suspend Aero.");
            Report("OpenProcess(SUSPEND_RESUME)", "denied");
            Require(!DebugActiveProcess((uint)guarded.Id),
                "a same-user process can attach a debugger to Aero.");
            Report("DebugActiveProcess", "denied");
            Require(!Denied(guarded.Id, Synchronize | ProcessQueryLimited),
                "the guard also blocks waiting and basic queries that Windows and WebView2 need.");
        }
        finally
        {
            foreach (Process victim in new[] { control, guarded })
            {
                try { victim.Kill(); } catch (InvalidOperationException) { }
            }
        }
    }

    /// <summary>
    /// A stealer that does get read access (admin, or the DUP_HANDLE pseudo-handle trick) scans
    /// memory for the key that opens the cookie vault. The key is concealed, so the scan of this
    /// whole process must come back empty, while an unconcealed control key is found.
    /// </summary>
    public static void RunConcealedKeyScan()
    {
        using SecureBuffer concealed = CookieVaultKey.CreateDataKey();
        using SecureBuffer control = SecureBuffer.Random(32);
        byte[] concealedPattern = MaskedCopy(concealed);
        byte[] controlPattern = MaskedCopy(control);

        using ProcessHandle self = ProcessHandle.Open(ProcessVmRead | ProcessQueryInformation, Environment.ProcessId);
        Require(MemoryContains(self, controlPattern, Mask),
            "the memory scanner could not find an unconcealed control key in this process.");
        Report("scan memory for an unconcealed control key", "key FOUND (control, expected)");
        Require(!MemoryContains(self, concealedPattern, Mask),
            "the cookie vault key is readable in plaintext in process memory.");
        Report("scan memory for the cookie vault key", "not found (concealed)");
    }

    public static void RunCookieVaultAtRest()
    {
        string canary = NewCanary("COOKIE");
        string folder = NewTemporaryFolder();
        try
        {
            using SecureBuffer key = CookieVaultKey.CreateDataKey();
            VaultCookie cookie = new("aero_stealer_sim", canary, ".stealer-sim.aero.test", "/",
                DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds(), true, true, 1);
            File.WriteAllBytes(Path.Combine(folder, "cookie-vault.bin"),
                CookieVaultFile.Seal(key, [cookie], "aero-cookie-vault-v1:stealer-sim"));

            if (AppBoundKey.IsInstalled)
            {
                // A stealer copies cookie-vault.key and asks the AeroAppBound service to unwrap it.
                // The service must refuse by caller identity (not Aero.exe), not merely fail on bad data.
                string? refusal = null;
                try { using SecureBuffer stolen = CookieVaultKey.Unwrap("unused", [(byte)'A', .. RandomNumberGenerator.GetBytes(64)], IntPtr.Zero); }
                catch (CryptographicException exception) { refusal = exception.Message; }
                Require(refusal?.Contains("refused this process", StringComparison.Ordinal) == true,
                    $"the AeroAppBound service did not refuse a process that is not Aero.exe ({refusal ?? "it answered"}).");
                Report("ask AeroAppBound to unwrap a stolen cookie key", "refused (not Aero.exe)");
            }
            else
            {
                Report("ask AeroAppBound to unwrap a stolen cookie key", "skipped (service not installed)");
            }

            AssertFilesHideCanary(folder, canary, "cookie vault");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Profile-file attack: grep for the canary, then try the user's DPAPI on every file.</summary>
    private static void AssertFilesHideCanary(string folder, string canary, string what)
    {
        byte[][] encodings =
        [
            Encoding.UTF8.GetBytes(canary),
            Encoding.Unicode.GetBytes(canary),
            Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(canary)))
        ];
        foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            byte[] content = File.ReadAllBytes(path);
            Require(!encodings.Any(pattern => content.AsSpan().IndexOf(pattern) >= 0),
                $"{what}: {Path.GetFileName(path)} holds the secret in readable form.");
            foreach (DataProtectionScope scope in new[] { DataProtectionScope.CurrentUser, DataProtectionScope.LocalMachine })
            {
                bool opened;
                try { opened = ProtectedData.Unprotect(content, null, scope).Length >= 0; }
                catch (CryptographicException) { opened = false; }
                Require(!opened, $"{what}: {Path.GetFileName(path)} opens with {scope} DPAPI, the stealer default.");
            }
        }

        Report($"grep + DPAPI-unprotect the {what} files", "nothing readable");
    }

    // ---------------------------------------------------------------- WinUI: broker reveal + WebView2

    public static void RunWinUiAttacks()
    {
        Exception? failure = null;
        BrokerApplication? application = null;
        bool finished = false;
        Thread thread = new(() =>
        {
            try
            {
                Aero.Security.BrokerHost.Program.InitializeWinUiRuntime();
                Application.Start(_ =>
                {
                    DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
                    SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
                    application = BrokerApplication.CreateStartupProbe(async () =>
                    {
                        try
                        {
                            await RunBrokerRevealAsync();
                            await RunWebView2AttacksAsync();
                            finished = true;
                        }
                        catch (Exception exception)
                        {
                            failure = exception;
                        }
                    });
                    // Test windows open and close one after another; WinUI must not quit in between.
                    application.DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
                });
                GC.KeepAlive(application);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        })
        {
            IsBackground = true,
            Name = "Aero stealer simulation (WinUI)"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromMinutes(4)), "the WinUI part did not finish.");
        if (failure is not null)
        {
            throw new InvalidOperationException("Stealer simulation (WinUI) failed.", failure);
        }

        Require(finished, "the WinUI part stopped before every attack ran.");
    }

    /// <summary>
    /// The real Broker reveal path with a simulated Windows Hello: the vault stays shut while the
    /// user cancels, opens on approval, and the Broker window then shows the password to the user
    /// while UI Automation and screen capture cannot read it.
    /// </summary>
    private static async Task RunBrokerRevealAsync()
    {
        string password = NewCanary("PASSWORD");
        string folder = NewTemporaryFolder();
        try
        {
            using SimulatedWindowsHello hello = new();
            using PasswordVaultService vault = new(folder, hello);

            using (SecureRevealWindow saveWindow = SecureRevealWindow.Create(IntPtr.Zero, RevealOrigin))
            {
                await vault.SavePasswordAsync(RevealOrigin, "stealer-sim@aero.test"u8.ToArray(),
                    Encoding.UTF8.GetBytes(password), saveWindow.WindowHandle);
            }

            AssertFilesHideCanary(folder, password, "password vault");

            using (SecureRevealWindow window = SecureRevealWindow.Create(IntPtr.Zero, RevealOrigin))
            {
                int prompts = hello.Prompts;
                bool protectionArmed = false;
                PasswordLease? lease = await vault.GetPasswordForRevealJitAsync(
                    RevealOrigin, window.WindowHandle, () => protectionArmed = true);
                Require(lease is not null, "the saved password was not found after a successful unlock.");
                Require(hello.Prompts > prompts, "the password was revealed without asking Windows Hello.");
                Require(protectionArmed, "the Broker protection hook did not run before the password was decrypted.");
                using (lease)
                {
                    window.SetPassword(lease!.PasswordUtf8);
                }

                Require(window.IsShowing(password),
                    "the Aero Security Broker window does not show the unlocked password, or is not capture-protected.");
                Report("Broker reveal after simulated Windows Hello", "password SHOWN to the user");

                string exposed = await ReadWindowTextAsync(window.WindowHandle) ?? string.Empty;
                Require(!exposed.Contains(password, StringComparison.Ordinal),
                    "UI Automation can read the revealed password out of the Broker window: " + string.Join(" | ",
                        exposed.Split('\n').Where(line => line.Contains(password, StringComparison.Ordinal))
                            .Select(line => line.Replace(password, "<password>", StringComparison.Ordinal))));
                Report("read the Broker window through UI Automation", "password not exposed");
                await window.ShowForAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None);
            }

            await Task.Delay(TimeSpan.FromSeconds(2.5)); // the vault rate-limits right after a failure
            hello.UserApproves = false;
            using (SecureRevealWindow window = SecureRevealWindow.Create(IntPtr.Zero, RevealOrigin))
            {
                bool refused = false;
                try { using PasswordLease? lease = await vault.GetPasswordForRevealJitAsync(RevealOrigin, window.WindowHandle, () => { }); }
                catch (PasswordVaultVerificationException) { refused = true; }
                Require(refused && !window.IsShowing(password), "the password was revealed although Windows Hello was cancelled.");
                Report("reveal with Windows Hello cancelled", "refused");
                await window.ShowForAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);
            }

            // Control: the same UI Automation probe does read an ordinary text field.
            Window plain = new() { Content = new TextBox { Text = password, IsReadOnly = true } };
            plain.Activate();
            await Task.Delay(500);
            Require((await ReadWindowTextAsync(WinRT.Interop.WindowNative.GetWindowHandle(plain)))?.Contains(password, StringComparison.Ordinal) == true,
                "the UI Automation probe cannot read an ordinary text box, so it proves nothing.");
            Report("read an ordinary TextBox through UI Automation", "password FOUND (control, expected)");
            plain.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>What a UI Automation stealer gets from a window: every name, value and text.</summary>
    private static Task<string?> ReadWindowTextAsync(IntPtr window) => Task.Run<string?>(() =>
    {
        StringBuilder text = new();
        AutomationElement root = AutomationElement.FromHandle(window);
        foreach (AutomationElement element in root.FindAll(TreeScope.Subtree, Condition.TrueCondition))
        {
            try
            {
                string where = $"{element.Current.ControlType.ProgrammaticName}/{element.Current.ClassName}";
                text.Append(where).Append(" name: ").Append(element.Current.Name).Append('\n');
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object value))
                {
                    text.Append(where).Append(" value: ").Append(((ValuePattern)value).Current.Value).Append('\n');
                }

                if (element.TryGetCurrentPattern(TextPattern.Pattern, out object range))
                {
                    text.Append(where).Append(" text: ").Append(((TextPattern)range).DocumentRange.GetText(-1)).Append('\n');
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or ElementNotAvailableException or COMException)
            {
                // A password control refuses its value: that is the protection, not a probe failure.
            }
        }

        return text.ToString();
    });

    // ---------------------------------------------------------------- helpers

    private const byte Mask = 0x5A;

    // Pinned and XOR-masked, so the pattern this test searches for never exists in plaintext.
    private static byte[] MaskedCopy(SecureBuffer secret)
    {
        byte[] plain = GC.AllocateArray<byte>(secret.Length, pinned: true);
        byte[] reveal = secret.CopyToArray();
        try
        {
            for (int i = 0; i < reveal.Length; i++)
            {
                plain[i] = (byte)(reveal[i] ^ Mask);
            }

            return plain;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(reveal);
        }
    }

    private static bool Denied(int processId, uint access)
    {
        using ProcessHandle handle = ProcessHandle.Open(access, processId);
        return handle.IsInvalid;
    }

    private static string NewTemporaryFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "aero-stealer-sim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Searches every readable committed page of a process for a (masked) pattern.</summary>
    private static bool MemoryContains(ProcessHandle process, byte[] pattern, byte mask = 0)
    {
        const int Chunk = 1 << 20;
        byte[] buffer = new byte[Chunk + pattern.Length];
        byte first = (byte)(pattern[0] ^ mask);
        long address = 0;
        while (VirtualQueryEx(process, (IntPtr)address, out MemoryBasicInformation info, Marshal.SizeOf<MemoryBasicInformation>()) != 0)
        {
            long regionEnd = (long)info.BaseAddress + (long)info.RegionSize;
            bool readable = info.State == 0x1000 && (info.Protect & 0x101) == 0 && info.Protect != 0;
            for (long offset = (long)info.BaseAddress; readable && offset < regionEnd; offset += Chunk)
            {
                int size = (int)Math.Min(Chunk + pattern.Length - 1, regionEnd - offset);
                if (!ReadProcessMemory(process, (IntPtr)offset, buffer, size, out nint read) || read == 0)
                {
                    break;
                }

                ReadOnlySpan<byte> span = buffer.AsSpan(0, (int)read);
                for (int at = span.IndexOf(first); at >= 0 && at <= span.Length - pattern.Length;)
                {
                    int i = 1;
                    while (i < pattern.Length && (byte)(span[at + i] ^ mask) == pattern[i])
                    {
                        i++;
                    }

                    if (i == pattern.Length)
                    {
                        CryptographicOperations.ZeroMemory(buffer);
                        return true;
                    }

                    int next = span[(at + 1)..].IndexOf(first);
                    at = next < 0 ? -1 : at + 1 + next;
                }
            }

            address = regionEnd;
        }

        CryptographicOperations.ZeroMemory(buffer);
        return false;
    }

    private sealed class ProcessHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public ProcessHandle()
            : base(true)
        {
        }

        public static ProcessHandle Open(uint access, int processId) => OpenProcess(access, false, (uint)processId);

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ProcessHandle OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQueryEx(ProcessHandle process, IntPtr address, out MemoryBasicInformation info, int length);

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(ProcessHandle process, IntPtr address, byte[] buffer, nint size, out nint read);

    [DllImport("kernel32.dll")]
    private static extern bool DebugActiveProcess(uint processId);
}
