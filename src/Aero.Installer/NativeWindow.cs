using System.Runtime.InteropServices;

namespace Aero.Installer;

internal static class NativeWindow
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;

    public static void ShowError(string message, string caption) =>
        _ = MessageBoxW(IntPtr.Zero, message, caption, MbOk | MbIconError);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(
        IntPtr windowHandle,
        string text,
        string caption,
        uint type);
}
