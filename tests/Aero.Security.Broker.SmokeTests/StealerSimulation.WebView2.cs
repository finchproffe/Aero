using Aero.App.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.Text;

internal static partial class StealerSimulation
{
    /// <summary>
    /// A real WebView2 profile, the engine Aero runs on. Default stealer methods: scrape cookies
    /// and typed passwords out of the WebView2 processes, and copy the Cookies database. Runs
    /// unprotected first (the control: the canaries must be found), then with Aero's protections.
    /// </summary>
    private static async Task RunWebView2AttacksAsync()
    {
        string password = NewCanary("TYPED");
        string vaultedValue = NewCanary("VAULTED");
        string vaultedName = "aero_sim_vaulted_" + Convert.ToHexString(Guid.NewGuid().ToByteArray()[..6]);
        string controlName = "aero_sim_control_" + Convert.ToHexString(Guid.NewGuid().ToByteArray()[..6]);
        string folder = NewTemporaryFolder();
        Window window = new() { Title = "Aero stealer simulation" };
        WebView2 view = new();
        window.Content = view;
        window.Activate();
        try
        {
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                null, folder, new CoreWebView2EnvironmentOptions());
            await view.EnsureCoreWebView2Async(environment);
            CoreWebView2 core = view.CoreWebView2;
            List<string> failures = [];
            core.ProcessFailed += (_, args) => failures.Add($"{args.ProcessFailedKind}/{args.Reason}");

            await NavigateAsync(core, $"<html><body><input type=password id=p value='{password}'></body></html>");
            CoreWebView2CookieManager cookies = core.CookieManager;
            double nextYear = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
            AddCookie(cookies, vaultedName, vaultedValue, nextYear);
            AddCookie(cookies, controlName, "control", nextYear);
            // Aero's cookie vault sweep: persistent cookies become in-memory session copies.
            AddCookie(cookies, vaultedName, vaultedValue, -1);
            _ = await cookies.GetCookiesAsync(string.Empty);

            // Control: unprotected WebView2 processes hand out the session cookie and the typed password.
            int[] processIds = environment.GetProcessInfos().Select(info => info.ProcessId).ToArray();
            Require(AnyProcessContains(processIds, Encoding.UTF8.GetBytes(vaultedValue)),
                "the scanner found no cookie in unprotected WebView2 memory, so it proves nothing.");
            Report("scan unprotected WebView2 memory for a cookie", "cookie FOUND (control, expected)");

            // Aero's protection: every WebView2 process, including ones started later, is guarded.
            void Guard() => _ = ProcessAccessGuard.GuardWebViewProcesses(
                environment.GetProcessInfos().Select(info => info.ProcessId));
            Guard();
            environment.ProcessInfosChanged += (_, _) => Guard();

            // Includes msedgewebview2 children WebView2 does not report, such as the crashpad handler.
            int[] reported = environment.GetProcessInfos().Select(info => info.ProcessId).ToArray();
            int[] unreported = WebViewChildren(reported).ToArray();
            Require(unreported.Length > 0, "no unreported WebView2 child (crashpad handler) was found to check.");
            int[] readable = reported.Concat(unreported)
                .Where(id => !Denied(id, ProcessVmRead | ProcessQueryInformation)).ToArray();
            Require(readable.Length == 0,
                $"WebView2 processes {string.Join(", ", readable.Select(DescribeProcess))} are still readable by a same-user stealer.");
            Report($"OpenProcess(VM_READ) on {reported.Length} WebView2 + {unreported.Length} crashpad", "denied");
            Require(reported.Concat(unreported).All(id => Denied(id, ProcessInjection)),
                "a same-user process can still inject into WebView2.");
            Report("OpenProcess(VM_WRITE|CREATE_THREAD) on WebView2", "denied");
            Require(reported.Concat(unreported).All(id => Denied(id, ProcessDupHandle)),
                "a same-user process can open WebView2 for DUP_HANDLE and steal its full-access handles.");
            Report("OpenProcess(DUP_HANDLE) for handle theft on WebView2", "denied");

            // The browser keeps working: navigation, new renderers, scripts and cookies.
            for (int page = 0; page < 6; page++)
            {
                await NavigateAsync(core, $"<html><body>page {page}<script>document.title='p{page}'</script></body></html>");
            }

            Require(await core.ExecuteScriptAsync("1+1") == "2", "WebView2 scripts stopped working under the guard.");
            // Aero's password capture and autofill move data through shared buffers (handle duplication).
            TaskCompletionSource<string> bufferEcho = new(TaskCreationOptions.RunContinuationsAsynchronously);
            core.WebMessageReceived += (_, args) => bufferEcho.TrySetResult(args.TryGetWebMessageAsString());
            await core.ExecuteScriptAsync(
                "chrome.webview.addEventListener('sharedbufferreceived', e => { const b = e.getBuffer(); " +
                "chrome.webview.postMessage(String(new Uint8Array(b)[0])); chrome.webview.releaseBuffer(b); });");
            using (CoreWebView2SharedBuffer buffer = environment.CreateSharedBuffer(16))
            {
                using (Stream stream = buffer.OpenStream().AsStreamForWrite())
                {
                    stream.WriteByte(42);
                }

                core.PostSharedBufferToScript(buffer, CoreWebView2SharedBufferAccess.ReadOnly, null);
                Require(await bufferEcho.Task.WaitAsync(TimeSpan.FromSeconds(10)) == "42",
                    "WebView2 shared buffers stopped working under the guard.");
            }
            Require((await cookies.GetCookiesAsync(string.Empty)).Any(cookie => cookie.Name == controlName),
                "WebView2 cookies stopped working under the guard.");
            Require(failures.Count == 0, $"WebView2 processes failed under the guard: {string.Join(", ", failures)}.");
            Report("browse with every WebView2 process guarded", "stable");

            // Copy-the-Cookies-file attack, after WebView2 shut down and flushed its database.
            TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
            environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
            view.Close();
            window.Close();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(30));
            byte[][] files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(path => { try { return File.ReadAllBytes(path); } catch (IOException) { return []; } })
                .ToArray();
            Require(files.Any(content => content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(controlName)) >= 0),
                "the persistent control cookie is not in the WebView2 profile files, so the disk check proves nothing.");
            Report("grep the WebView2 Cookies DB for a persistent cookie", "cookie FOUND (control, expected)");
            Require(!files.Any(content => content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(vaultedName)) >= 0),
                "a vaulted cookie reached the WebView2 Cookies DB that stealers copy.");
            Report("grep the WebView2 Cookies DB for a vaulted cookie", "not on disk");
        }
        finally
        {
            window.Close();
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(500); // WebView2 releases its files shortly after exit
                }
            }
        }
    }

    private static void AddCookie(CoreWebView2CookieManager manager, string name, string value, double expires)
    {
        CoreWebView2Cookie cookie = manager.CreateCookie(name, value, ".stealer-sim.aero.test", "/");
        cookie.Expires = expires;
        manager.AddOrUpdateCookie(cookie);
    }

    private static async Task NavigateAsync(CoreWebView2 core, string html)
    {
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args) => done.TrySetResult();
        core.NavigationCompleted += Completed;
        try
        {
            core.NavigateToString(html);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            core.NavigationCompleted -= Completed;
        }
    }

    private static bool AnyProcessContains(IEnumerable<int> processIds, byte[] pattern)
    {
        foreach (int id in processIds)
        {
            using ProcessHandle handle = ProcessHandle.Open(ProcessVmRead | ProcessQueryInformation, id);
            if (!handle.IsInvalid && MemoryContains(handle, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<int> WebViewChildren(int[] parents) =>
        Process.GetProcessesByName("msedgewebview2")
            .Select(process => { using (process) { return (process.Id, Parent: ParentOf(process.Id)); } })
            .Where(entry => parents.Contains(entry.Parent) && !parents.Contains(entry.Id))
            .Select(entry => entry.Id)
            .ToArray();

    // The parent id is in the basic information, which only needs query-limited access.
    private static int ParentOf(int id)
    {
        using ProcessHandle handle = ProcessHandle.Open(ProcessQueryLimited, id);
        return !handle.IsInvalid && NtQueryInformationProcess(handle, 0, out ProcessBasicInformation info,
            System.Runtime.InteropServices.Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
            ? (int)info.InheritedFromUniqueProcessId
            : -1;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        ProcessHandle process, int informationClass, out ProcessBasicInformation information, int length, out int returned);

    private static string DescribeProcess(int id)
    {
        try
        {
            using Process process = Process.GetProcessById(id);
            return $"{id} ({process.ProcessName})";
        }
        catch (ArgumentException)
        {
            return $"{id} (exited)";
        }
    }
}
