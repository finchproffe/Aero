using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Aero.App;

/// <summary>
/// Notification-area icon. Its hidden window is created on the UI thread, so the
/// callbacks run on the WinUI dispatcher like any other window message.
/// </summary>
internal sealed class AeroTrayIcon : IDisposable
{
    private const string WindowClassName = "AeroTrayIconWindow";
    private const uint CallbackMessage = 0x8000 + 0x41; // WM_APP range
    private const int WmContextMenu = 0x007B;
    private const int NinSelect = 0x0400;
    private const int NinKeySelect = 0x0401;
    private const int NimAdd = 0;
    private const int NimDelete = 2;
    private const int NimSetVersion = 4;
    private const int NifMessage = 0x1;
    private const int NifIcon = 0x2;
    private const int NifTip = 0x4;
    private const int NifShowTip = 0x80;
    private const int NotifyIconVersion4 = 4;

    private static readonly WndProc Procedure = WindowProc;
    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
    private static bool _classRegistered;
    private static AeroTrayIcon? _current;

    private readonly Action _select;
    private readonly Action<PointInt32> _openMenu;
    private readonly string _tooltip;
    private readonly nint _window;
    private readonly nint _icon;

    public AeroTrayIcon(string iconPath, string tooltip, Action select, Action<PointInt32> openMenu)
    {
        _select = select;
        _openMenu = openMenu;
        _tooltip = tooltip;

        nint instance = GetModuleHandleW(null);
        if (!_classRegistered)
        {
            WNDCLASSEXW windowClass = new()
            {
                cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Procedure),
                hInstance = instance,
                lpszClassName = WindowClassName
            };
            _classRegistered = RegisterClassExW(ref windowClass) != 0;
        }

        // Hidden top-level window, not HWND_MESSAGE: message-only windows miss the
        // TaskbarCreated broadcast that tells us Explorer restarted.
        _window = CreateWindowExW(0, WindowClassName, "Aero tray", 0, 0, 0, 0, 0, 0, 0, instance, 0);
        _icon = LoadImageW(0, iconPath, 1 /* IMAGE_ICON */, GetSystemMetrics(49), GetSystemMetrics(50), 0x10 /* LR_LOADFROMFILE */);
        _current = this;
        Add();
    }

    public void Dispose()
    {
        NOTIFYICONDATAW data = CreateData();
        Shell_NotifyIconW(NimDelete, ref data);
        if (_window != 0)
        {
            DestroyWindow(_window);
        }

        if (_icon != 0)
        {
            DestroyIcon(_icon);
        }

        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
    }

    private void Add()
    {
        NOTIFYICONDATAW data = CreateData();
        data.uFlags = NifMessage | NifIcon | NifTip | NifShowTip;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = _tooltip;
        Shell_NotifyIconW(NimAdd, ref data);
        data.uVersion = NotifyIconVersion4;
        Shell_NotifyIconW(NimSetVersion, ref data);
    }

    private NOTIFYICONDATAW CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window,
        uID = 1,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        AeroTrayIcon? icon = _current;
        if (icon is not null && hwnd == icon._window)
        {
            try
            {
                if (message == CallbackMessage)
                {
                    // NOTIFYICON_VERSION_4: event in LOWORD(lParam), anchor point in wParam.
                    int trayEvent = (int)(lParam & 0xFFFF);
                    if (trayEvent is NinSelect or NinKeySelect)
                    {
                        icon._select();
                    }
                    else if (trayEvent == WmContextMenu)
                    {
                        icon._openMenu(new PointInt32((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF)));
                    }

                    return 0;
                }

                if (message == TaskbarCreatedMessage)
                {
                    icon.Add();
                    return 0;
                }
            }
            catch (Exception exception)
            {
                // An exception cannot cross this native callback without killing every tab.
                AppHealth.RecordCrash(exception);
                return 0;
            }
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public int cbSize;
        public int style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImageW(nint instance, string name, int type, int width, int height, int flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);
}
