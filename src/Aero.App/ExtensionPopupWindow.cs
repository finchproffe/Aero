using Aero.WebView;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.UI;

namespace Aero.App;

/// <summary>
/// Compact browser-action surface for Chromium extension popup pages.
/// It shares Aero's WebView2 environment/profile, but behaves like a toolbar popup
/// instead of creating a normal browser tab.
/// </summary>
internal sealed class ExtensionPopupWindow : Window
{
    private readonly CoreWebView2Environment _environment;
    private readonly Func<Uri, Task> _openInBrowser;
    private readonly WebView2 _browser;
    private readonly WebViewEventBinder _eventBinder = new();
    private bool _hasBeenActivated;
    private bool _isClosing;

    public ExtensionPopupWindow(
        CoreWebView2Environment environment,
        ElementTheme theme,
        Func<Uri, Task> openInBrowser)
    {
        _environment = environment;
        _openInBrowser = openInBrowser;

        Title = "Aero extension";
        ExtendsContentIntoTitleBar = true;

        _browser = WebViewControlFactory.Create(
            allowDrop: false,
            initiallyVisible: true);
        _browser.DefaultBackgroundColor = theme == ElementTheme.Light
            ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(255, 31, 35, 39);

        Border surface = new()
        {
            RequestedTheme = theme,
            Background = new SolidColorBrush(
                theme == ElementTheme.Light
                    ? Color.FromArgb(255, 255, 255, 255)
                    : Color.FromArgb(255, 31, 35, 39)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            Child = _browser
        };

        Content = surface;
        Activated += ExtensionPopupWindow_Activated;
        Closed += ExtensionPopupWindow_Closed;
    }

    public async Task ShowAsync(Uri extensionUri, PointInt32 position, SizeInt32 size)
    {
        ConfigureWindow(position, size);
        Activate();

        await WebViewControllerInitializer.EnsureInitializedAsync(
            _browser,
            _environment);

        CoreWebView2 core = _browser.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        _eventBinder.Bind(_browser, core, bindings =>
            bindings.Add(
                () => core.NewWindowRequested += CoreWebView2_NewWindowRequested,
                () => core.NewWindowRequested -= CoreWebView2_NewWindowRequested));

        _browser.Source = extensionUri;
        _browser.Focus(FocusState.Programmatic);
    }

    private void ConfigureWindow(PointInt32 position, SizeInt32 size)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;

            try
            {
                presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            }
            catch
            {
                // Older Windows App SDK versions can keep the tiny tool-window border.
            }
        }

        AppWindow.MoveAndResize(new RectInt32(position.X, position.Y, size.Width, size.Height));
    }

    private async void CoreWebView2_NewWindowRequested(
        CoreWebView2 sender,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;

        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? uri))
        {
            await _openInBrowser(uri);
        }

        CloseSafely();
    }

    private void ExtensionPopupWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (_hasBeenActivated)
            {
                CloseSafely();
            }

            return;
        }

        _hasBeenActivated = true;
    }

    private void ExtensionPopupWindow_Closed(object sender, WindowEventArgs args)
    {
        _eventBinder.Dispose();
        WebViewControllerLifecycle.TryStop(_browser);
        WebViewControllerLifecycle.TryClose(_browser);
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
}
