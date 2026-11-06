using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using WinRT.Interop;

namespace Aero.Installer;

public sealed partial class MainWindow : Window
{
    private readonly InstallerEngine _engine;
    private readonly InstallerStrings _strings;
    private readonly bool _autoInstall;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly StringBuilder _details = new();
    private AppWindow? _appWindow;
    private bool _busy;
    private bool _readyToInstall;
    private bool _completed;
    private bool _initializationFailed;
    private bool _installationStarted;
    private bool _loaded;
    private bool _browserNeedsInstall;
    private bool _securityDriverPanelAvailable;
    private int _installedVersionComparison;

    internal MainWindow(
        InstallerEngine engine,
        InstallerStrings strings,
        bool autoInstall,
        InstallerOptions requestedOptions)
    {
        _engine = engine;
        _strings = strings;
        _autoInstall = autoInstall;

        InitializeComponent();
        ConfigureRuntimeVisuals();
        Title = strings.WindowTitle;
        WindowTitleText.Text = strings.WindowTitle;
        ApplyStrings(requestedOptions);
        ConfigureWindow();

        RootGrid.Loaded += RootGrid_Loaded;
        Closed += MainWindow_Closed;
    }


    private void ConfigureRuntimeVisuals()
    {
        // Keep file-backed images and optional theme styles out of compiled XAML.
        // This avoids startup XAML failures in unpackaged single-file builds.
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AeroIcon.png");
            if (File.Exists(iconPath))
            {
                BitmapImage icon = new(new Uri(iconPath, UriKind.Absolute));
                TitleBarIconImage.Source = icon;
                HeroIconImage.Source = icon;
            }
        }
        catch
        {
            // The installer remains fully usable if the decorative icon cannot be loaded.
        }

        try
        {
            if (Application.Current.Resources["AccentButtonStyle"] is Style accentButtonStyle)
            {
                PrimaryButton.Style = accentButtonStyle;
            }
        }
        catch
        {
            // Fall back to the default button style if a theme resource is unavailable.
        }
    }

    private void ApplyStrings(InstallerOptions requestedOptions)
    {
        HeroTitleText.Text = _strings.HeroTitle;
        HeroSubtitleText.Text = _strings.HeroSubtitle;
        InstalledCaptionText.Text = _strings.InstalledLabel.ToUpperInvariant();
        PackageCaptionText.Text = _strings.PackageLabel.ToUpperInvariant();
        DataNoticeText.Text = "✓  " + _strings.DataNotice;
        ElevationNoticeText.Text = _strings.ElevationNotice;
        StatusText.Text = _strings.Checking;
        LaunchAfterCheckBox.Content = _strings.LaunchAfter;
        LaunchAfterCheckBox.IsChecked = requestedOptions.LaunchAfterInstall;
        OpenDefaultsCheckBox.Content = _strings.OpenDefaultsAfter;
        OpenDefaultsCheckBox.IsChecked = requestedOptions.OpenDefaultApps;
        SecurityDriverPanelCheckBox.Content = _strings.InstallSecurityDriverPanel;
        SecurityDriverPanelCheckBox.IsChecked = requestedOptions.InstallSecurityDriverPanel;
        DetailsButton.Content = _strings.Details;
        CancelButton.Content = _strings.Cancel;
        PrimaryButton.Content = _strings.Install;
        PrimaryButton.IsEnabled = false;
        DetailsExpander.Header = _strings.Details;
    }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        IntPtr windowHandle = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Title = _strings.WindowTitle;
        _appWindow.Resize(new SizeInt32(760, 720));
        _appWindow.Closing += AppWindow_Closing;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
            presenter.IsResizable = true;
        }

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AeroIcon.ico");
        if (File.Exists(iconPath))
        {
            _appWindow.SetIcon(iconPath);
        }

        DisplayArea displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        RectInt32 workArea = displayArea.WorkArea;
        int x = workArea.X + Math.Max(0, (workArea.Width - 760) / 2);
        int y = workArea.Y + Math.Max(0, (workArea.Height - 720) / 2);
        _appWindow.Move(new PointInt32(x, y));
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await InitializeInstallerAsync();
    }

    private async Task InitializeInstallerAsync()
    {
        SetBusy(true);
        _initializationFailed = false;
        _completed = false;
        _readyToInstall = false;
        _browserNeedsInstall = false;
        _details.Clear();
        UpdateDetailsText();
        InstallProgressBar.IsIndeterminate = false;
        InstallProgressBar.Value = 2;

        try
        {
            Progress<InstallerProgress> progress = new(ApplyProgress);
            InstallerInitializationResult result = await _engine.InitializeAsync(
                progress,
                _lifetimeCancellation.Token);

            PackageValueText.Text = $"{result.Package.Version}  ·  {result.Package.Architecture}";
            InstalledValueText.Text = result.InstalledPackage is null
                ? _strings.NotInstalled
                : result.InstalledPackage.Version.ToString();

            _securityDriverPanelAvailable = result.SecurityDriverPanelAvailable;
            SecurityDriverPanelCheckBox.Visibility = _securityDriverPanelAvailable
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (!_securityDriverPanelAvailable)
            {
                SecurityDriverPanelCheckBox.IsChecked = false;
            }

            _installedVersionComparison =
                result.InstalledPackage?.Version.CompareTo(result.Package.Version) ?? -1;
            _browserNeedsInstall = result.InstalledPackage is null || _installedVersionComparison < 0;
            UpdatePrimaryActionState();

            InstallProgressBar.Value = 100;
            SetBusy(false);

            if (_autoInstall && _readyToInstall)
            {
                await BeginInstallAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The window is closing; no error UI is required.
        }
        catch (Exception exception)
        {
            _initializationFailed = true;
            _readyToInstall = false;
            PrimaryButton.Content = _strings.Retry;
            ShowFailure(exception);
            SetBusy(false);
        }
    }

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_completed)
        {
            Close();
            return;
        }

        if (_initializationFailed)
        {
            await InitializeInstallerAsync();
            return;
        }

        if (!_readyToInstall || _busy)
        {
            return;
        }

        InstallerOptions options = ReadOptions();
        if (!InstallerEngine.IsAdministrator)
        {
            if (InstallerEngine.TryRelaunchElevated(options, out string? error))
            {
                Close();
                return;
            }

            SetStatus(
                string.IsNullOrWhiteSpace(error) ? _strings.ElevationCancelled : error,
                InfoBarSeverity.Error);
            return;
        }

        await BeginInstallAsync();
    }

    private async Task BeginInstallAsync()
    {
        _installationStarted = true;
        SetBusy(true);
        _readyToInstall = false;
        _details.Clear();
        UpdateDetailsText();

        try
        {
            Progress<InstallerProgress> progress = new(ApplyProgress);
            InstalledPackageInfo installed = await _engine.InstallAsync(
                ReadOptions(),
                progress,
                _lifetimeCancellation.Token);
            InstalledValueText.Text = installed.Version.ToString();
            _completed = true;
            PrimaryButton.Content = _strings.Close;
            CancelButton.Visibility = Visibility.Collapsed;
            SetStatus(_strings.SuccessBody, InfoBarSeverity.Success);
            InstallProgressBar.Value = 100;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Shutdown requested outside the deployment stage.
        }
        catch (Exception exception)
        {
            _readyToInstall = true;
            bool panelSelected =
                _securityDriverPanelAvailable && SecurityDriverPanelCheckBox.IsChecked == true;
            PrimaryButton.Content = !_browserNeedsInstall && panelSelected
                ? _strings.InstallComponents
                : _engine.InstalledPackage is null
                    ? _strings.Install
                    : _strings.Update;
            ShowFailure(exception);
        }
        finally
        {
            _installationStarted = false;
            SetBusy(false);
        }
    }

    private InstallerOptions ReadOptions() => new(
        LaunchAfterInstall: LaunchAfterCheckBox.IsChecked == true,
        OpenDefaultApps: OpenDefaultsCheckBox.IsChecked == true,
        InstallSecurityDriverPanel:
            _securityDriverPanelAvailable && SecurityDriverPanelCheckBox.IsChecked == true);

    private void SecurityDriverPanelCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loaded && !_busy && !_initializationFailed && !_installationStarted)
        {
            UpdatePrimaryActionState();
        }
    }

    private void UpdatePrimaryActionState()
    {
        bool panelSelected =
            _securityDriverPanelAvailable && SecurityDriverPanelCheckBox.IsChecked == true;
        bool hasWork = _browserNeedsInstall || panelSelected;

        _completed = !hasWork;
        _readyToInstall = hasWork;
        CancelButton.Visibility = hasWork ? Visibility.Visible : Visibility.Collapsed;

        if (_browserNeedsInstall)
        {
            PrimaryButton.Content = _engine.InstalledPackage is null
                ? _strings.Install
                : _strings.Update;
            SetStatus(_strings.Ready, InfoBarSeverity.Success);
        }
        else if (panelSelected)
        {
            PrimaryButton.Content = _strings.InstallComponents;
            SetStatus(_strings.Ready, InfoBarSeverity.Success);
        }
        else
        {
            PrimaryButton.Content = _strings.Close;
            SetStatus(
                _installedVersionComparison == 0
                    ? _strings.AlreadyCurrent
                    : _strings.NewerInstalled,
                _installedVersionComparison == 0
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Informational);
        }

        PrimaryButton.IsEnabled = !_busy && (_readyToInstall || _completed || _initializationFailed);
    }

    private void ApplyProgress(InstallerProgress progress)
    {
        InstallProgressBar.IsIndeterminate = false;
        InstallProgressBar.Value = Math.Clamp(progress.Percent, 0, 100);
        SetStatus(progress.Message, InfoBarSeverity.Informational);
        AppendDetail($"{DateTime.Now:HH:mm:ss}  {progress.Message}");
        if (!string.IsNullOrWhiteSpace(progress.Detail))
        {
            AppendDetail(progress.Detail);
        }
    }

    private void ShowFailure(Exception exception)
    {
        SetStatus(exception.Message, InfoBarSeverity.Error);
        AppendDetail(exception.ToString());
        DetailsExpander.IsExpanded = true;
        DetailsButton.Content = _strings.HideDetails;
    }

    private void SetStatus(string? text, InfoBarSeverity severity)
    {
        StatusText.Text = string.IsNullOrWhiteSpace(text) ? _strings.ErrorTitle : text;
        StatusGlyphText.Text = severity switch
        {
            InfoBarSeverity.Success => "✓",
            InfoBarSeverity.Warning => "!",
            InfoBarSeverity.Error => "×",
            _ => "ℹ"
        };
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        PrimaryButton.IsEnabled = !busy && (_readyToInstall || _completed || _initializationFailed);
        CancelButton.IsEnabled = !busy;
        LaunchAfterCheckBox.IsEnabled = !busy;
        OpenDefaultsCheckBox.IsEnabled = !busy;
        SecurityDriverPanelCheckBox.IsEnabled = !busy && _securityDriverPanelAvailable;
    }

    private void AppendDetail(string value)
    {
        if (_details.Length > 96_000)
        {
            _details.Remove(0, Math.Min(32_000, _details.Length));
        }

        _details.AppendLine(value);
        UpdateDetailsText();
    }

    private void UpdateDetailsText()
    {
        DetailsTextBox.Text = _details.ToString();
        DetailsTextBox.SelectionStart = DetailsTextBox.Text.Length;
    }

    private void DetailsButton_Click(object sender, RoutedEventArgs e)
    {
        DetailsExpander.IsExpanded = !DetailsExpander.IsExpanded;
        DetailsButton.Content = DetailsExpander.IsExpanded
            ? _strings.HideDetails
            : _strings.Details;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_busy && _installationStarted)
        {
            args.Cancel = true;
            SetStatus(_strings.InstallingPackage, InfoBarSeverity.Warning);
            return;
        }

        _lifetimeCancellation.Cancel();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_appWindow is not null)
        {
            _appWindow.Closing -= AppWindow_Closing;
        }

        _lifetimeCancellation.Cancel();
        _engine.Dispose();
        _lifetimeCancellation.Dispose();
    }
}
