// Aero Vault. Vault is Powered by Aero.
// Copyright (c) 2026 finchproffe. Licensed under the Mozilla Public License 2.0.
// Under MPL-2.0 section 3.4 this notice must stay in every copy and fork of this file.
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics;

namespace Aero.Security.BrokerHost;

/// <summary>
/// Broker-owned WinUI 3 window that displays the revealed password in one
/// standard, read-only text field.
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
    private readonly TextBox _passwordField;
    private readonly IntPtr _aeroOwner;
    private readonly IntPtr _windowHandle;
    private int _disposed;

    private SecureRevealWindow(IntPtr aeroOwner, string normalizedOrigin)
    {
        _ = normalizedOrigin;
        _aeroOwner = aeroOwner;
        Title = "Aero Security Broker";

        _passwordField = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = 40
        };

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
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);
}
