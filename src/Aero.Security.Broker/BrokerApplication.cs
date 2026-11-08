// Aero Vault. Vault is Powered by Aero.
// Copyright (c) 2026 finchproffe. Licensed under the Mozilla Public License 2.0.
// Under MPL-2.0 section 3.4 this notice must stay in every copy and fork of this file.
using Microsoft.UI.Xaml;

namespace Aero.Security.BrokerHost;

/// <summary>
/// Owns the WinUI 3 dispatcher for the one-shot Broker. The authenticated server
/// starts from this UI context so every reveal window and control is created by
/// the Broker process on the application STA.
/// </summary>
public sealed partial class BrokerApplication : Application
{
    private readonly PreparedBrokerLaunch? _launch;
    private readonly Action<int>? _completed;
    private readonly bool _startupProbe;
    private int _completionStarted;

    internal BrokerApplication(
        PreparedBrokerLaunch launch,
        Action<int> completed)
    {
        _launch = launch;
        _completed = completed;
        InitializeApplication();
    }

    internal BrokerApplication()
        : this(startupProbe: true)
    {
    }

    private BrokerApplication(bool startupProbe)
    {
        _startupProbe = startupProbe;
        InitializeApplication();
    }

    internal static BrokerApplication CreateStartupProbe() => new(true);

    private void InitializeApplication()
    {
        InitializeComponent();
        RequestedTheme = ApplicationTheme.Dark;
        UnhandledException += OnUnhandledException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ = args;
        if (_startupProbe)
        {
            Exit();
            return;
        }

        PreparedBrokerLaunch launch = _launch
            ?? throw new InvalidOperationException("The Broker launch context is missing.");
        using CancellationTokenSource lifetime = new(TimeSpan.FromMinutes(2));
        try
        {
            int exitCode = await Program.RunPreparedAsync(launch, lifetime.Token);
            Complete(exitCode);
        }
        catch (OperationCanceledException exception)
        {
            Program.WriteStartupDiagnostic(4, "operation-timeout", exception);
            Complete(4);
        }
        catch (Exception exception)
        {
            Program.WriteStartupDiagnostic(5, "broker-application", exception);
            Complete(5);
        }
    }

    private void OnUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        _ = sender;
        args.Handled = true;
        Program.WriteStartupDiagnostic(5, "winui-unhandled", args.Exception);
        Complete(5);
    }

    private void Complete(int exitCode)
    {
        if (Interlocked.Exchange(ref _completionStarted, 1) != 0)
        {
            return;
        }

        _completed?.Invoke(exitCode);
        Exit();
    }
}
