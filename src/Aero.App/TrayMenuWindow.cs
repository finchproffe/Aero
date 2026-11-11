using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;

namespace Aero.App;

/// <summary>
/// Windows 11 style tray menu: acrylic, rounded, with a slider header, rows that show
/// their current value, and submenus. Closes itself when it loses activation.
/// </summary>
internal sealed class TrayMenuWindow : Window
{
    private const double MenuWidth = 280;

    private readonly StackPanel _items = new();
    private readonly Border _root;
    private readonly Color _separatorColor;
    private PointInt32 _anchor;
    private bool _hasBeenActivated;
    private bool _isActive;
    private bool _isClosing;
    private int _openSubmenus;

    public TrayMenuWindow(ElementTheme theme)
    {
        bool dark = theme == ElementTheme.Dark;
        _separatorColor = dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0, 0, 0);
        Color hover = dark ? Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x09, 0, 0, 0);
        Color pressed = dark ? Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x06, 0, 0, 0);
        SolidColorBrush transparent = new(Microsoft.UI.Colors.Transparent);

        Title = "Aero";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        _root = new Border
        {
            RequestedTheme = theme,
            Width = MenuWidth,
            Padding = new Thickness(4),
            Child = _items
        };
        // Lightweight styling: rows are plain Buttons that look like menu items.
        _root.Resources["ButtonBackground"] = transparent;
        _root.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(hover);
        _root.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(pressed);
        _root.Resources["ButtonBorderBrush"] = transparent;
        _root.Resources["ButtonBorderBrushPointerOver"] = transparent;
        _root.Resources["ButtonBorderBrushPressed"] = transparent;
        // Slides up into place like Windows 11 menus; the system animation setting is honoured.
        _items.Transitions = new TransitionCollection { new EntranceThemeTransition { FromVerticalOffset = 12 } };
        _root.KeyDown += Root_KeyDown;
        _root.Loaded += Root_Loaded;
        Content = _root;

        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            try
            {
                presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            }
            catch
            {
                // Older Windows App SDK versions can keep the tiny tool-window border.
            }
        }

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        int round = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        // DWM draws a bright 1px frame by default; match the subtle stroke of system menus.
        int border = dark ? 0x003A3A3A : 0x00E0E0E0; // COLORREF 0x00BBGGRR
        _ = DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref border, sizeof(int));
        Activated += TrayMenuWindow_Activated;
    }

    public void AddSlider(string title, int minimum, int maximum, int value, Func<int, string> format, Action<int> changed)
    {
        TextBlock caption = new() { Text = format(value), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.75 };
        Slider slider = new()
        {
            Minimum = minimum,
            Maximum = maximum,
            StepFrequency = 1,
            Value = value,
            IsThumbToolTipEnabled = false,
            Margin = new Thickness(0, 2, 0, -6)
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, title);
        slider.ValueChanged += (_, args) =>
        {
            int index = (int)Math.Round(args.NewValue);
            caption.Text = format(index);
            changed(index);
        };

        _items.Children.Add(new StackPanel
        {
            Padding = new Thickness(12, 6, 12, 4),
            Children =
            {
                new TextBlock { Text = title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = 0.6 },
                slider,
                caption
            }
        });
    }

    public void AddSeparator()
    {
        _items.Children.Add(new Rectangle
        {
            Height = 1,
            Margin = new Thickness(-4, 4, -4, 4),
            Fill = new SolidColorBrush(_separatorColor)
        });
    }

    public void AddItem(string text, Action action)
    {
        Button row = CreateRow(text, value: null, submenu: false);
        row.Click += (_, _) =>
        {
            CloseSafely();
            action();
        };
        _items.Children.Add(row);
    }

    public void AddChoice<T>(string text, IReadOnlyList<(T Value, string Label)> options, T current, Action<T> select)
    {
        string currentLabel = options.FirstOrDefault(option => EqualityComparer<T>.Default.Equals(option.Value, current)).Label ?? string.Empty;
        Button row = CreateRow(text, currentLabel, submenu: true);
        MenuFlyout flyout = new()
        {
            Placement = FlyoutPlacementMode.RightEdgeAlignedTop,
            // Lets the submenu leave this small window, like a real context menu.
            ShouldConstrainToRootBounds = false
        };
        foreach ((T value, string label) in options)
        {
            RadioMenuFlyoutItem item = new()
            {
                Text = label,
                GroupName = text,
                IsChecked = EqualityComparer<T>.Default.Equals(value, current)
            };
            item.Click += (_, _) =>
            {
                CloseSafely();
                select(value);
            };
            flyout.Items.Add(item);
        }

        flyout.Opened += (_, _) => _openSubmenus++;
        flyout.Closed += (_, _) =>
        {
            _openSubmenus--;
            // Activation returns to this window after the submenu closes; check once it has.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!_isActive && _openSubmenus == 0)
                {
                    CloseSafely();
                }
            });
        };
        row.Flyout = flyout;
        _items.Children.Add(row);
    }

    public void ShowAt(PointInt32 anchor)
    {
        _anchor = anchor;
        // Real size is known only after layout; Root_Loaded moves and sizes the window.
        AppWindow.MoveAndResize(new RectInt32(anchor.X, anchor.Y, 1, 1));
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private static Button CreateRow(string text, string? value, bool submenu)
    {
        Grid grid = new()
        {
            ColumnDefinitions =
            {
                // The label keeps its full width; a long value is the one that gets trimmed.
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        grid.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(value))
        {
            TextBlock valueText = new()
            {
                Text = value,
                Opacity = 0.75,
                Margin = new Thickness(12, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(valueText, 1);
            grid.Children.Add(valueText);
        }

        if (submenu)
        {
            FontIcon chevron = new() { Glyph = "", FontSize = 10, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(chevron, 2);
            grid.Children.Add(chevron);
        }

        return new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 7, 12, 7),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4)
        };
    }

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        double scale = _root.XamlRoot?.RasterizationScale ?? 1.0;
        _root.Measure(new Windows.Foundation.Size(MenuWidth, double.PositiveInfinity));
        int width = (int)Math.Ceiling(MenuWidth * scale);
        int height = (int)Math.Ceiling(_root.DesiredSize.Height * scale);
        RectInt32 work = DisplayArea.GetFromPoint(_anchor, DisplayAreaFallback.Nearest).WorkArea;
        // Open up and to the left of the tray icon, kept inside the work area.
        int x = Math.Clamp(_anchor.X - width, work.X, Math.Max(work.X, work.X + work.Width - width));
        int y = Math.Clamp(_anchor.Y - height, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CloseSafely();
        }
    }

    private void TrayMenuWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        _isActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (_isActive)
        {
            _hasBeenActivated = true;
        }
        else if (_hasBeenActivated && _openSubmenus == 0)
        {
            CloseSafely();
        }
    }

    private void CloseSafely()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        Close();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);
}
