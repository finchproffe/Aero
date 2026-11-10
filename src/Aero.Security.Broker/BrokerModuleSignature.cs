using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Aero.Security.BrokerHost;

/// <summary>
/// Trust for native modules that sit next to a development (writable) Broker build, such as the
/// Windows App SDK's WinUIEdit.dll: the file must carry a valid Authenticode signature issued
/// to and by Microsoft. A planted DLL with a borrowed name fails this check.
/// </summary>
internal static partial class BrokerModuleSignature
{
    private static Guid s_genericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static bool IsMicrosoftSigned(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // Only the embedded signer is read; WinVerifyTrust validates the file below.
            using X509Certificate signer = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return IsMicrosoft(signer.Subject) && IsMicrosoft(signer.Issuer) && VerifyTrust(path) == 0;
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            return false; // unsigned or unreadable
        }
    }

    /// <summary>Any publisher, as long as the signature chains to a trusted root.</summary>
    public static bool IsValidlySigned(string path) => VerifyTrust(path) == 0;

    private static bool IsMicrosoft(string distinguishedName) =>
        distinguishedName.Contains("O=Microsoft Corporation", StringComparison.Ordinal);

    private static unsafe int VerifyTrust(string path)
    {
        fixed (char* pathPointer = path)
        {
            WinTrustFileInfo fileInfo = new()
            {
                StructSize = (uint)sizeof(WinTrustFileInfo),
                FilePath = (IntPtr)pathPointer
            };
            WinTrustData trustData = new()
            {
                StructSize = (uint)sizeof(WinTrustData),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 0,
                UnionChoice = 1, // WTD_CHOICE_FILE
                FileInfo = (IntPtr)(&fileInfo),
                ProviderFlags = 0x00001000 // WTD_CACHE_ONLY_URL_RETRIEVAL: no network
            };
            return WinVerifyTrust(IntPtr.Zero, ref s_genericVerifyV2, (IntPtr)(&trustData));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(IntPtr windowHandle, ref Guid actionId, IntPtr trustData);
}
