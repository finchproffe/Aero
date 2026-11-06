using System.Globalization;
using Microsoft.UI.Xaml;

namespace Aero.Installer;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
        InstallerStrings strings = InstallerStrings.ForCulture(CultureInfo.CurrentUICulture);
        bool autoInstall = commandLine.Any(
            argument => string.Equals(argument, "--install", StringComparison.OrdinalIgnoreCase));
        InstallerOptions requestedOptions = new(
            LaunchAfterInstall: !commandLine.Any(
                argument => string.Equals(argument, "--no-launch", StringComparison.OrdinalIgnoreCase)),
            OpenDefaultApps: commandLine.Any(
                argument => string.Equals(argument, "--open-defaults", StringComparison.OrdinalIgnoreCase)),
            InstallSecurityDriverPanel: commandLine.Any(
                argument => string.Equals(argument, "--install-security-panel", StringComparison.OrdinalIgnoreCase)));

        InstallerEngine engine = new(strings);
        _window = new MainWindow(engine, strings, autoInstall, requestedOptions);
        _window.Activate();
    }

    private static void App_UnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        NativeWindow.ShowError(BuildStartupError(eventArgs.Exception), "Aero Installer");
    }

    private static string BuildStartupError(Exception exception)
    {
        List<string> parts = new();
        Exception? current = exception;

        while (current is not null && parts.Count < 4)
        {
            parts.Add(
                $"{current.GetType().Name} (0x{current.HResult:X8}){Environment.NewLine}{current.Message}");
            current = current.InnerException;
        }

        return string.Join(
            $"{Environment.NewLine}{Environment.NewLine}Caused by:{Environment.NewLine}",
            parts);
    }
}
