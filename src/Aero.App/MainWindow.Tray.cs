using Aero.App.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using static Aero.App.Localization.AeroLocalizer;

namespace Aero.App;

public sealed partial class MainWindow
{
    private static AeroTrayIcon? _trayIcon;
    private static MainWindow? _trayTarget;

    private static MainWindow? TrayTarget =>
        _trayTarget is not null && OpenWindows.Contains(_trayTarget)
            ? _trayTarget
            : OpenWindows.LastOrDefault();

    private void InitializeTrayIcon()
    {
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                _trayTarget = this;
            }
        };

        try
        {
            _trayIcon ??= new AeroTrayIcon(
                Path.Combine(AppContext.BaseDirectory, "Assets", "AeroIcon.ico"),
                "Aero",
                ShowFromTray,
                OpenTrayMenu);
        }
        catch (Exception exception)
        {
            // A missing notification area must never stop the browser from opening.
            AppHealth.RecordCrash(exception);
        }
    }

    private static void DisposeTrayIconAfterLastWindow()
    {
        if (OpenWindows.Count == 0)
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
        }
    }

    private static void ShowFromTray()
    {
        MainWindow? target = TrayTarget;
        foreach (MainWindow window in OpenWindows.Where(window => !ReferenceEquals(window, target)))
        {
            window.AppWindow.Show(activateWindow: false);
        }

        target?.BringToFront();
    }

    private static void HideToTray()
    {
        foreach (MainWindow window in OpenWindows)
        {
            window.AppWindow.Hide();
        }
    }

    private void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        AppWindow.Show();
        Activate();
    }

    private static void OpenTrayMenu(PointInt32 anchor)
    {
        if (TrayTarget is not MainWindow target)
        {
            return;
        }

        TrayMenuWindow menu = new(target.RootGrid.ActualTheme);
        if (target.ActiveTab is BrowserTabState tab)
        {
            menu.AddSlider(
                TranslateUi("Page zoom"),
                0,
                PageZoomSteps.Length - 1,
                FindNearestZoomStepIndex(tab.PageZoomFactor),
                index => $"{Math.Round(PageZoomSteps[index] * 100)}%",
                index =>
                {
                    if (target._tabs.Contains(tab))
                    {
                        target.SetPageZoom(tab, PageZoomSteps[index]);
                    }
                });
            menu.AddSeparator();
        }

        bool anyVisible = OpenWindows.Any(window => window.AppWindow.IsVisible);
        menu.AddItem(TranslateUi(anyVisible ? "Hide Aero" : "Show Aero"), anyVisible ? HideToTray : ShowFromTray);
        menu.AddItem(TranslateUi("New tab"), () =>
        {
            target.BringToFront();
            _ = target.CreateTabAsync(null, selectTab: true, showStartPage: true);
        });
        menu.AddItem(TranslateUi("New private window"), () =>
            new MainWindow(
                isPrivate: true,
                browserProfile: target._browserProfile,
                startupTransfer: null,
                sharedPrivateUserDataFolder: null).Activate());
        menu.AddSeparator();

        menu.AddChoice(
            TranslateUi("Shield mode"),
            [(ShieldMode.Standard, TranslateUi("Standard")), (ShieldMode.Strict, TranslateUi("Strict"))],
            target._settings.ShieldMode,
            mode => target.ChangeSettingFromTray(nameof(BrowserSettings.ShieldMode), settings => settings.ShieldMode = mode));
        menu.AddChoice(
            TranslateUi("Memory saver"),
            [
                (MemorySaverMode.Maximum, TranslateUi("Maximum")),
                (MemorySaverMode.Balanced, TranslateUi("Balanced")),
                (MemorySaverMode.Off, TranslateUi("Off"))
            ],
            target._settings.MemorySaverMode,
            mode => target.ChangeSettingFromTray(nameof(BrowserSettings.MemorySaverMode), settings => settings.MemorySaverMode = mode));
        menu.AddChoice(
            TranslateUi("Theme"),
            [
                (AeroThemeMode.Light, TranslateUi("Light")),
                (AeroThemeMode.Dark, TranslateUi("Dark")),
                (AeroThemeMode.System, TranslateUi("System"))
            ],
            target._settings.ThemeMode,
            mode => target.ChangeSettingFromTray(nameof(BrowserSettings.ThemeMode), settings => settings.ThemeMode = mode));
        menu.AddSeparator();

        menu.AddItem(TranslateUi("Settings"), () =>
        {
            target.BringToFront();
            target.OpenSettingsPane();
        });
        menu.AddItem(TranslateUi("Exit"), () =>
        {
            foreach (MainWindow window in OpenWindows.ToList())
            {
                window.Close();
            }
        });
        menu.ShowAt(anchor);
    }

    // Each window holds its own copy of the profile's settings; keep them in step.
    private void ChangeSettingFromTray(string name, Action<BrowserSettings> change)
    {
        foreach (MainWindow window in OpenWindows
            .Where(window => window._browserProfile.StorageRoot == _browserProfile.StorageRoot)
            .ToList())
        {
            change(window._settings);
            window._settings.Save();
            window.ApplyAllRuntimeSettings(name);
        }
    }
}
