using System.Diagnostics;
using System.Runtime.Versioning;

namespace Aero.Security.Driver.Setup;

[SupportedOSPlatform("windows")]
internal sealed class MainForm : Form
{
    private readonly Label _titleLabel;
    private readonly Label _statusLabel;
    private readonly ProgressBar _progressBar;
    private readonly TextBox _logBox;
    private readonly Button _installButton;
    private readonly Button _statusButton;
    private readonly Button _uninstallButton;
    private readonly CheckBox _bootSplashCheckBox;

    public MainForm()
    {
        Text = "Aero Security Driver Setup";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(760, 520);
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        _titleLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 19F, FontStyle.Bold, GraphicsUnit.Point),
            Location = new Point(24, 20),
            Text = "Aero Security Driver"
        };

        var subtitleLabel = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(27, 62),
            Text = "VM laboratory installer · driver, service, signature and PING verification"
        };

        _statusLabel = new Label
        {
            AutoEllipsis = true,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(28, 94),
            Padding = new Padding(10, 8, 10, 8),
            Size = new Size(704, 42),
            Text = "Ready. The installer only runs inside a supported virtual machine.",
            TextAlign = ContentAlignment.MiddleLeft
        };

        _progressBar = new ProgressBar
        {
            Location = new Point(28, 148),
            Size = new Size(704, 8),
            Style = ProgressBarStyle.Continuous,
            Minimum = 0,
            Maximum = 100
        };

        _bootSplashCheckBox = new CheckBox
        {
            AutoSize = true,
            Location = new Point(29, 174),
            Text = "Install or update Aero UEFI BootSplash from this package",
            Checked = true
        };

        _logBox = new TextBox
        {
            BackColor = SystemColors.Window,
            Font = new Font("Cascadia Mono", 9F, FontStyle.Regular, GraphicsUnit.Point),
            Location = new Point(28, 207),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Size = new Size(704, 235),
            WordWrap = false
        };

        _installButton = new Button
        {
            Location = new Point(28, 462),
            Size = new Size(190, 38),
            Text = "Install / Repair"
        };
        _installButton.Click += async (_, _) => await RunOperationAsync(InstallOrRepairAsync);

        _statusButton = new Button
        {
            Location = new Point(230, 462),
            Size = new Size(150, 38),
            Text = "Check status"
        };
        _statusButton.Click += async (_, _) => await RunOperationAsync(CheckStatusAsync);

        _uninstallButton = new Button
        {
            Location = new Point(392, 462),
            Size = new Size(150, 38),
            Text = "Uninstall"
        };
        _uninstallButton.Click += async (_, _) => await RunOperationAsync(UninstallAsync);

        var closeButton = new Button
        {
            DialogResult = DialogResult.Cancel,
            Location = new Point(554, 462),
            Size = new Size(178, 38),
            Text = "Close"
        };
        closeButton.Click += (_, _) => Close();

        Controls.AddRange([
            _titleLabel,
            subtitleLabel,
            _statusLabel,
            _progressBar,
            _bootSplashCheckBox,
            _logBox,
            _installButton,
            _statusButton,
            _uninstallButton,
            closeButton
        ]);

        CancelButton = closeButton;
        Shown += async (_, _) => await RunOperationAsync(CheckStatusAsync, clearLog: false);
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation, bool clearLog = true)
    {
        ToggleUi(false);
        _progressBar.Style = ProgressBarStyle.Marquee;
        _progressBar.MarqueeAnimationSpeed = 25;

        if (clearLog)
        {
            _logBox.Clear();
        }

        using var cancellation = new CancellationTokenSource();

        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Operation cancelled.");
            Log("Operation cancelled.");
        }
        catch (Exception exception)
        {
            SetStatus("Operation failed. See the log below.");
            Log($"ERROR: {exception.Message}");
            Log(exception.ToString());
        }
        finally
        {
            _progressBar.MarqueeAnimationSpeed = 0;
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = 0;
            ToggleUi(true);
        }
    }

    private async Task InstallOrRepairAsync(CancellationToken cancellationToken)
    {
        SetStatus("Checking the virtual machine and locating the driver payload...");

        bool installBootSplash = _bootSplashCheckBox.Checked;
        var result = await Task.Run(
            () => DriverInstaller.InstallOrRepair(
                AppContext.BaseDirectory,
                installBootSplash,
                LogThreadSafe,
                cancellationToken),
            cancellationToken);

        SetStatus(result.Success
            ? FormatDriverStatus(
                "Protection driver is active",
                result)
            : "Driver installation did not complete successfully.");
    }

    private async Task CheckStatusAsync(CancellationToken cancellationToken)
    {
        SetStatus("Checking Aero Security Driver...");

        var result = await Task.Run(
            () => DriverInstaller.CheckStatus(LogThreadSafe, cancellationToken),
            cancellationToken);

        SetStatus(result.Success
            ? FormatDriverStatus(
                "Driver is responding",
                result)
            : "Driver is not active or failed verification.");
    }

    private static string FormatDriverStatus(
        string prefix,
        DriverStatusResult result)
    {
        string brokerStatus = result.ProtectionActive
            ? $"Broker PID {result.ProtectedProcessId} is protected"
            : "waiting for a password operation";

        return $"{prefix}. Version {result.Version}, capabilities 0x{result.Capabilities:X8}; {brokerStatus}.";
    }

    private async Task UninstallAsync(CancellationToken cancellationToken)
    {
        var confirmation = MessageBox.Show(
            this,
            "Stop and remove the Aero Security Driver service and driver file from this VM?",
            "Aero Security Driver",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (confirmation != DialogResult.Yes)
        {
            SetStatus("Uninstall cancelled.");
            return;
        }

        SetStatus("Removing Aero Security Driver...");
        await Task.Run(
            () => DriverInstaller.Uninstall(LogThreadSafe, cancellationToken),
            cancellationToken);
        SetStatus("Aero Security Driver was removed from the VM.");
    }

    private void ToggleUi(bool enabled)
    {
        _installButton.Enabled = enabled;
        _statusButton.Enabled = enabled;
        _uninstallButton.Enabled = enabled;
        _bootSplashCheckBox.Enabled = enabled;
    }

    private void SetStatus(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => SetStatus(text)));
            return;
        }

        _statusLabel.Text = text;
    }

    private void LogThreadSafe(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => Log(message)));
            return;
        }

        Log(message);
    }

    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        _logBox.AppendText($"[{timestamp}] {message}{Environment.NewLine}");
    }
}
