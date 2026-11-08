using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace Aero.App;

public partial class App : Application
{
    private const string MainInstanceKey = "Aero.Browser.Main";

    private readonly SemaphoreSlim _activationGate = new(1, 1);
    private Window? _window;
    private AppInstance? _mainInstance;

    public App()
    {
        // Before any WebView2, vault or broker state exists in this process.
        Aero.App.Security.ProcessAccessGuard.HardenCurrentProcess();
        // Logged only: WinUI still decides whether the process survives.
        UnhandledException += (_, args) => AppHealth.RecordCrash(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppHealth.RecordCrash(args.ExceptionObject as Exception);
        InitializeComponent();
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        bool ownsMainInstance = false;
        try
        {
            AppInstance currentInstance = AppInstance.GetCurrent();
            AppActivationArguments activation = currentInstance.GetActivatedEventArgs();
            AppInstance mainInstance = AppInstance.FindOrRegisterForKey(MainInstanceKey);

            if (!mainInstance.IsCurrent)
            {
                // Redirect only the activation payload. The secondary process owns no
                // browser window, so a normal WinUI shutdown is safer than a hard
                // Environment.Exit while the primary process is handling WebView2.
                await mainInstance.RedirectActivationToAsync(activation);
                Exit();
                return;
            }

            ownsMainInstance = true;
            _mainInstance = mainInstance;
            _mainInstance.Activated += MainInstance_Activated;

            MainWindow window = new();
            _window = window;
            window.Activate();

            await ProcessActivationSafelyAsync(activation);
        }
        catch
        {
            // A malformed protocol activation must never take down an already-running
            // Aero session. If this is the primary startup, still open a normal window.
            if (ownsMainInstance && _window is null)
            {
                MainWindow window = new();
                _window = window;
                window.Activate();
            }
        }
    }

    private void MainInstance_Activated(object? sender, AppActivationArguments args)
    {
        if (_window is not MainWindow window)
        {
            return;
        }

        window.DispatcherQueue.TryEnqueue(() =>
        {
            // DispatcherQueue callbacks are effectively async-void once an await is
            // introduced. Keep the exception inside a guarded Task so a failed tab
            // creation cannot terminate the primary Aero process and all open tabs.
            _ = ProcessActivationSafelyAsync(args);
        });
    }

    private async Task ProcessActivationSafelyAsync(AppActivationArguments args)
    {
        await _activationGate.WaitAsync();
        try
        {
            if (_window is not MainWindow window)
            {
                return;
            }

            window.Activate();
            await HandleActivationAsync(window, args);
        }
        catch
        {
            // External applications can send duplicate, stale, or malformed activation
            // payloads. Ignore the bad activation and preserve the current browser state.
        }
        finally
        {
            _activationGate.Release();
        }
    }

    private static async Task HandleActivationAsync(
        MainWindow window,
        AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.Protocol &&
            args.Data is ProtocolActivatedEventArgs protocolArgs)
        {
            await window.OpenExternalUriAsync(protocolArgs.Uri);
        }
        else if (args.Kind == ExtendedActivationKind.File &&
                 args.Data is FileActivatedEventArgs fileArgs)
        {
            foreach (Windows.Storage.IStorageItem item in fileArgs.Files)
            {
                if (item is Windows.Storage.StorageFile)
                {
                    await window.OpenExternalUriAsync(new Uri(item.Path));
                }
            }
        }
    }
}
