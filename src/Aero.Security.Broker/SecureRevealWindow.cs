// Aero Vault. Vault is Powered by Aero.
// Copyright (c) 2026 finchproffe. Licensed under the Mozilla Public License 2.0.
// Under MPL-2.0 section 3.4 this notice must stay in every copy and fork of this file.
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics;

namespace Aero.Security.BrokerHost;

/// <summary>
/// Broker-owned WinUI 3 window that displays the revealed password. Stealers read other apps'
/// windows through UI Automation, and every stock text control (even PasswordBox in visible
/// mode) hands its text to UIA, so the password sits in a <see cref="SecretText"/> whose
/// automation peer hides it. The text cannot be selected or copied, and the window is excluded
/// from screen capture.
/// </summary>
internal sealed class SecureRevealWindow : Window, IDisposable
{
    private const int LogicalWindowWidth = 520;
    private const int LogicalWindowHeight = 150;
    private const int GwlpOwner = -8;
    private const uint WdaMonitor = 0x00000001;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const uint MonitorDefaultToNearest = 2;

    private readonly TaskCompletionSource<bool> _closed = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SecretText _passwordField = new();
    private readonly IntPtr _aeroOwner;
    private readonly IntPtr _windowHandle;
    private int _disposed;

    private SecureRevealWindow(IntPtr aeroOwner, string normalizedOrigin)
    {
        _ = normalizedOrigin;
        _aeroOwner = aeroOwner;
        Title = "Aero Security Broker";

        Grid root = new()
        {
            Padding = new Thickness(16)
        };
        root.Children.Add(_passwordField);
        Content = root;

        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ConfigureNativeWindow();
        Closed += OnClosed;
        Activate();
        _ = SetForegroundWindow(_windowHandle);
        _ = BringWindowToTop(_windowHandle);
    }

    public static SecureRevealWindow Create(
        IntPtr aeroOwner,
        string normalizedOrigin)
    {
        if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is null)
        {
            throw new InvalidOperationException(
                "Aero Security Broker must create its reveal window on the WinUI dispatcher.");
        }

        return new SecureRevealWindow(aeroOwner, normalizedOrigin);
    }

    public IntPtr WindowHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                nameof(SecureRevealWindow));
            return _windowHandle;
        }
    }

    public void SetPassword(ReadOnlySpan<byte> passwordUtf8)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            nameof(SecureRevealWindow));
        if (_closed.Task.IsCompleted)
        {
            throw new OperationCanceledException(
                "The password window was closed before display.");
        }
        if (passwordUtf8.IsEmpty)
        {
            throw new ArgumentException("The password is empty.", nameof(passwordUtf8));
        }

        _passwordField.Text = Encoding.UTF8.GetString(passwordUtf8);
    }

    /// <summary>Smoke tests: what the user sees, plus whether screen capture is blocked.</summary>
    internal bool IsShowing(string password) =>
        _passwordField.Text == password &&
        GetWindowDisplayAffinity(_windowHandle, out uint affinity) &&
        affinity is WdaExcludeFromCapture or WdaMonitor;

    public async Task ShowForAsync(
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state =>
            {
                SecureRevealWindow window = (SecureRevealWindow)state!;
                _ = window.DispatcherQueue.TryEnqueue(window.RequestClose);
            },
            this);

        Task timeout = Task.Delay(lifetime, cancellationToken);
        Task completed = await Task.WhenAny(_closed.Task, timeout);
        if (ReferenceEquals(completed, timeout))
        {
            await timeout;
            RequestClose();
        }

        if (!_closed.Task.IsCompleted)
        {
            await _closed.Task.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void ConfigureNativeWindow()
    {
        if (_aeroOwner != IntPtr.Zero)
        {
            _ = SetWindowLongPtr(_windowHandle, GwlpOwner, _aeroOwner);
        }

        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
        uint dpi = GetDpiForWindow(_windowHandle);
        double scale = dpi == 0 ? 1.0 : dpi / 96.0;
        int width = Math.Max(1, (int)Math.Round(LogicalWindowWidth * scale));
        int height = Math.Max(1, (int)Math.Round(LogicalWindowHeight * scale));
        Rectangle workArea = GetWorkArea();
        Rectangle anchor = workArea;
        if (_aeroOwner != IntPtr.Zero && GetWindowRect(_aeroOwner, out Rectangle owner))
        {
            anchor = owner;
        }

        int x = anchor.Left + (anchor.Right - anchor.Left - width) / 2;
        int y = anchor.Top + (anchor.Bottom - anchor.Top - height) / 2;
        x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        appWindow.MoveAndResize(new RectInt32(x, y, width, height));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        if (!SetWindowDisplayAffinity(_windowHandle, WdaExcludeFromCapture))
        {
            _ = SetWindowDisplayAffinity(_windowHandle, WdaMonitor);
        }
    }

    private Rectangle GetWorkArea()
    {
        IntPtr monitor = MonitorFromWindow(
            _aeroOwner != IntPtr.Zero ? _aeroOwner : _windowHandle,
            MonitorDefaultToNearest);
        MonitorInfo info = new()
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>()
        };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)
            ? info.WorkArea
            : new Rectangle(0, 0, LogicalWindowWidth, LogicalWindowHeight);
    }

    private void RequestClose()
    {
        if (!_closed.Task.IsCompleted)
        {
            Close();
        }
    }

    private void ClearPassword()
    {
        _passwordField.Text = string.Empty;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _ = sender;
        _ = args;
        ClearPassword();
        _closed.TrySetResult(true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ClearPassword();
        if (!_closed.Task.IsCompleted)
        {
            Close();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public Rectangle(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rectangle Monitor;
        public Rectangle WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(
        IntPtr window,
        int index,
        IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr window,
        out Rectangle rectangle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(
        IntPtr window,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        IntPtr monitor,
        ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(
        IntPtr window,
        uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);
    /// <summary>The revealed password, visible on screen but not to UI Automation clients.</summary>
    private sealed partial class SecretText : ContentControl
    {
        private const uint SpiGetScreenReader = 0x0046;
        private readonly TextBlock _text = new()
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 18,
            IsTextSelectionEnabled = false,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        public SecretText()
        {
            Content = _text;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Center;
            IsTabStop = false;
        }

        public string Text
        {
            get => _text.Text;
            set => _text.Text = value;
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new SecretTextPeer(this);

        private sealed partial class SecretTextPeer(SecretText owner) : FrameworkElementAutomationPeer(owner)
        {
            // Hiding the TextBlock child keeps its Text (exposed as Name) out of the UIA tree.
            protected override IList<AutomationPeer>? GetChildrenCore() => null;

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

            protected override bool IsPasswordCore() => true;

            // Accessibility: a running screen reader still reads the password the user asked to see.
            protected override string GetNameCore() =>
                SystemParametersInfo(SpiGetScreenReader, 0, out int running, 0) && running != 0
                    ? owner.Text
                    : "Revealed password";
        }

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);
    }
}
