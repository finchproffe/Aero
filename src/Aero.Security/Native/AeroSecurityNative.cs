using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

// Native code loads only from Aero's own folder or System32, never the current directory or PATH.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]

namespace Aero.App.Security;

/// <summary>
/// The Rust core (src/Aero.Security.Native, aero_security_native.dll): protected secret memory,
/// cookie vault sealing and the process guard.
/// </summary>
internal static unsafe partial class AeroSecurityNative
{
    private const string Library = "aero_security_native";

    internal const int Ok = 0;
    internal const int InvalidArgument = 1;
    internal const int Rejected = 2;
    internal const int OsFailure = 3;

    [LibraryImport(Library, EntryPoint = "aero_secret_new")]
    internal static partial int SecretNew(nuint length, int random, out SecretHandle secret);

    [LibraryImport(Library, EntryPoint = "aero_secret_free")]
    internal static partial void SecretFree(IntPtr secret);

    [LibraryImport(Library, EntryPoint = "aero_secret_data")]
    internal static partial byte* SecretData(SecretHandle secret);

    [LibraryImport(Library, EntryPoint = "aero_secret_len")]
    internal static partial nuint SecretLength(SecretHandle secret);

    [LibraryImport(Library, EntryPoint = "aero_secret_is_locked")]
    internal static partial int SecretIsLocked(SecretHandle secret);

    [LibraryImport(Library, EntryPoint = "aero_secret_conceal")]
    internal static partial int SecretConceal(SecretHandle secret);

    [LibraryImport(Library, EntryPoint = "aero_secret_reveal")]
    internal static partial int SecretReveal(SecretHandle secret, byte* destination, nuint length);

    [LibraryImport(Library, EntryPoint = "aero_vault_seal")]
    internal static partial int VaultSeal(
        SecretHandle key, byte* plain, nuint plainLength, byte* context, nuint contextLength, byte* output, nuint outputLength);

    [LibraryImport(Library, EntryPoint = "aero_vault_open")]
    internal static partial int VaultOpen(
        SecretHandle key, byte* sealedData, nuint sealedLength, byte* context, nuint contextLength, out SecretHandle plain);

    [LibraryImport(Library, EntryPoint = "aero_guard_harden_current_process")]
    internal static partial int GuardHardenCurrentProcess();

    [LibraryImport(Library, EntryPoint = "aero_guard_restrict")]
    internal static partial int GuardRestrict(uint processId, uint deniedAccess);

    [LibraryImport(Library, EntryPoint = "aero_guard_restrict_webview_tree")]
    internal static partial uint GuardRestrictWebViewTree(uint processId, uint deniedAccess);
}

internal sealed class SecretHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SecretHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle()
    {
        AeroSecurityNative.SecretFree(handle); // wipes, unlocks and frees in Rust
        return true;
    }
}
