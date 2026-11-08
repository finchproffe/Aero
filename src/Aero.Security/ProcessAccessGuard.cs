using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Aero.App.Security;

/// <summary>
/// Info-stealers running as the same user open the browser with PROCESS_VM_READ to scrape
/// cookies, tokens and typed passwords from memory, or with VM_WRITE/CREATE_THREAD to inject.
/// The guard puts a deny ACE for the user in front of the process DACL and an OWNER RIGHTS
/// ACE that removes the owner's implicit WRITE_DAC, so a non-admin stealer cannot simply
/// rewrite the DACL back. Existing grants stay: Chromium sandbox processes die without their
/// AppContainer/logon SID entries. Admins with SeDebugPrivilege still bypass this.
/// </summary>
public static partial class ProcessAccessGuard
{
    // CREATE_THREAD | VM_OPERATION | VM_READ | VM_WRITE | DUP_HANDLE (equals full access) |
    // CREATE_PROCESS (parent spoofing) | SET_QUOTA | SET_INFORMATION | SUSPEND_RESUME |
    // SET_LIMITED_INFORMATION | WRITE_DAC | WRITE_OWNER. Terminate, query and wait stay allowed.
    public const uint DeniedUserAccess = 0x000C2BFA;
    private const uint ProcessDupHandle = 0x00000040;

    private const uint SecurityDescriptorRevision = 1;
    private const uint DaclSecurityInformation = 0x00000004;
    private const int SeKernelObject = 6;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint ProcessQueryLimitedInformation = 0x00001000;

    private const int ProcessExtensionPointDisablePolicy = 6;
    private const int ProcessImageLoadPolicy = 10;
    // NoRemoteImages | NoLowMandatoryLabelImages | PreferSystem32Images: blocks DLL planting
    // from network shares and low-integrity (browser download / temp) locations.
    private const uint ImageLoadRestrictions = 0x00000007;
    private const uint ExtensionPointsDisabled = 0x00000001;
    private const uint SpiGetScreenReader = 0x0046;

    /// <summary>Applies loader mitigations and the access guard to the current process.</summary>
    public static bool HardenCurrentProcess()
    {
        bool hardened = TrySetMitigation(ProcessImageLoadPolicy, ImageLoadRestrictions);

        // Blocks AppInit_DLLs, Winsock LSPs and SetWindowsHookEx DLL injection. Some screen
        // readers inject the same way, so the mitigation is skipped while one is running.
        if (!IsScreenReaderRunning())
        {
            hardened &= TrySetMitigation(ProcessExtensionPointDisablePolicy, ExtensionPointsDisabled);
        }

        // The WebView2 browser process duplicates shared-buffer handles (password capture and
        // autofill) into Aero, so Aero cannot deny DUP_HANDLE. That right lets a stealer that
        // knows the pseudo-handle trick regain full access; plain OpenProcess dumpers stay blocked.
        return TryRestrict(Environment.ProcessId, DeniedUserAccess & ~ProcessDupHandle) && hardened;
    }

    /// <summary>
    /// Guards a same-user process. Returns false when the process is gone or already guarded:
    /// the guard itself removes our WRITE_DAC. WebView2 processes cannot be guarded: Chromium
    /// opens its own processes for VM access and they crash when that is denied.
    /// </summary>
    public static bool TryRestrict(int processId, uint deniedAccess = DeniedUserAccess)
    {
        string? userSid = WindowsIdentity.GetCurrent().User?.Value;
        if (processId <= 0 || string.IsNullOrWhiteSpace(userSid))
        {
            return false;
        }

        using SafeProcessHandle process = OpenProcess(
            ReadControl | WriteDac | ProcessQueryLimitedInformation,
            inheritHandle: false,
            (uint)processId);
        if (process.IsInvalid ||
            GetSecurityInfo(process, SeKernelObject, DaclSecurityInformation,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out IntPtr current) != 0)
        {
            return false;
        }

        string? dacl;
        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptor(
                    current, SecurityDescriptorRevision, DaclSecurityInformation, out IntPtr text, out _))
            {
                return false;
            }

            dacl = Marshal.PtrToStringUni(text);
            _ = LocalFree(text);
        }
        finally
        {
            _ = LocalFree(current);
        }

        string? guarded = AddGuardAces(dacl, userSid, deniedAccess);
        if (guarded is null ||
            !ConvertStringSecurityDescriptorToSecurityDescriptor(
                guarded, SecurityDescriptorRevision, out IntPtr descriptor, out _))
        {
            return false;
        }

        try
        {
            return SetKernelObjectSecurity(process.DangerousGetHandle(), DaclSecurityInformation, descriptor);
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    /// <summary>Puts the deny and OWNER RIGHTS ACEs in front of the existing ACEs of a "D:..." SDDL.</summary>
    public static string? AddGuardAces(string? dacl, string userSid, uint deniedAccess = DeniedUserAccess)
    {
        // A NULL DACL ("D:NO_ACCESS_CONTROL") or a missing one cannot be extended safely.
        if (dacl is null || !dacl.StartsWith("D:", StringComparison.Ordinal) ||
            dacl.Contains("NO_ACCESS_CONTROL", StringComparison.Ordinal))
        {
            return null;
        }

        string aces = $"(D;;0x{deniedAccess:x};;;{userSid})(A;;RC;;;OW)";
        int firstAce = dacl.IndexOf('(');
        return firstAce < 0 ? dacl + aces : dacl.Insert(firstAce, aces);
    }

    private static bool TrySetMitigation(int policy, uint flags) =>
        SetProcessMitigationPolicy(policy, ref flags, sizeof(uint));

    private static bool IsScreenReaderRunning() =>
        SystemParametersInfo(SpiGetScreenReader, 0, out int running, 0) && running != 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityInfo(
        SafeProcessHandle handle,
        int objectType,
        uint securityInfo,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl,
        out IntPtr securityDescriptor);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSecurityDescriptorToStringSecurityDescriptorW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertSecurityDescriptorToStringSecurityDescriptor(
        IntPtr securityDescriptor,
        uint revision,
        uint securityInformation,
        out IntPtr stringSecurityDescriptor,
        out uint length);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetKernelObjectSecurity(
        IntPtr handle,
        uint securityInformation,
        IntPtr securityDescriptor);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessMitigationPolicy(int policy, ref uint buffer, nuint length);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);
}
