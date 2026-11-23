namespace Aero.App.Security;

/// <summary>
/// Keeps same-user info-stealers from opening Aero for memory reads or injection: a deny ACE in
/// front of the process DACL plus loader mitigations. Implemented in the Rust core
/// (src/Aero.Security.Native/src/guard.rs), which documents the exact rights and trade-offs.
/// </summary>
public static class ProcessAccessGuard
{
    public const uint DeniedUserAccess = 0x000C2BFA;

    /// <summary>
    /// For WebView2 (Chromium) processes, which hold the live cookies and typed passwords:
    /// CREATE_THREAD | VM_OPERATION | VM_READ | VM_WRITE | DUP_HANDLE (handle theft equals full
    /// access) | SUSPEND_RESUME | WRITE_DAC | WRITE_OWNER. SET_INFORMATION stays for Chromium.
    /// </summary>
    public const uint WebViewDeniedAccess = 0x000C087A;

    /// <summary>Applies loader mitigations and the access guard to the current process.</summary>
    public static bool HardenCurrentProcess() => AeroSecurityNative.GuardHardenCurrentProcess() != 0;

    /// <summary>
    /// Guards a same-user process. False when the process is gone or already guarded: the guard
    /// itself removes our WRITE_DAC. For WebView2 processes use <see cref="GuardWebViewProcesses"/>.
    /// </summary>
    public static bool TryRestrict(int processId, uint deniedAccess = DeniedUserAccess) =>
        processId > 0 && AeroSecurityNative.GuardRestrict((uint)processId, deniedAccess) != 0;

    /// <summary>
    /// Guards WebView2 processes, plus their unreported msedgewebview2.exe children such as the
    /// crashpad handler, against same-user memory scraping and injection. Call again whenever
    /// WebView2 starts new processes; already guarded ones are skipped. Returns how many this
    /// call guarded.
    /// </summary>
    public static int GuardWebViewProcesses(IEnumerable<int> processIds) =>
        processIds.Where(id => id > 0)
            .Sum(id => (int)AeroSecurityNative.GuardRestrictWebViewTree((uint)id, WebViewDeniedAccess));
}
