using Aero.App.Security;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Aero.Security.BrokerHost;

internal static class Program
{
    private const int ErrorModeNoGpFaultErrorBox = 0x0002;
    private const int ErrorModeNoOpenFileErrorBox = 0x8000;
    private static BrokerApplication? s_application;

    [DllImport("kernel32.dll")]
    private static extern int SetErrorMode(int mode);

    [STAThread]
    private static int Main(string[] args)
    {
        _ = SetErrorMode(ErrorModeNoGpFaultErrorBox | ErrorModeNoOpenFileErrorBox);

        if (args is ["--startup-probe"])
        {
            return RunWinUiStartupProbe();
        }

        if (!TryParseArguments(args, out BrokerLaunchArguments launch))
        {
            WriteStartupDiagnostic(2, "invalid-launch-arguments", null);
            return 2;
        }

        PreparedBrokerLaunch? prepared = null;
        try
        {
            PreparedBrokerLaunch launchContext =
                PrepareLaunchAsync(launch).GetAwaiter().GetResult();
            prepared = launchContext;
            int exitCode = 5;
            WriteStartupDiagnostic(0, "winui-runtime-initializing", null);
            InitializeWinUiRuntime();
            WriteStartupDiagnostic(0, "winui-runtime-initialized", null);
            Application.Start(_ =>
            {
                DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(dispatcher));
                s_application = new BrokerApplication(
                    launchContext,
                    completedExitCode => exitCode = completedExitCode);
            });
            s_application = null;
            return exitCode;
        }
        catch (BrokerStartupException exception)
        {
            WriteStartupDiagnostic(exception.ExitCode, exception.Stage, null);
            return exception.ExitCode;
        }
        catch (OperationCanceledException exception)
        {
            WriteStartupDiagnostic(4, "bootstrap-timeout", exception);
            return 4;
        }
        catch (Exception exception)
        {
            WriteStartupDiagnostic(5, "startup-exception", exception);
            return 5;
        }
        finally
        {
            prepared?.Dispose();
        }
    }

    internal static void InitializeWinUiRuntime() =>
        WinRT.ComWrappersSupport.InitializeComWrappers();

    private static int RunWinUiStartupProbe()
    {
        try
        {
            WriteStartupDiagnostic(0, "winui-runtime-probe-initializing", null);
            InitializeWinUiRuntime();
            Application.Start(_ =>
            {
                DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(dispatcher));
                s_application = BrokerApplication.CreateStartupProbe();
            });
            s_application = null;
            WriteStartupDiagnostic(0, "winui-runtime-probe-completed", null);
            return 0;
        }
        catch (Exception exception)
        {
            WriteStartupDiagnostic(5, "winui-runtime-probe-failed", exception);
            return 5;
        }
    }

    private static async Task<PreparedBrokerLaunch> PrepareLaunchAsync(
        BrokerLaunchArguments launch)
    {
        using CancellationTokenSource bootstrapLifetime = new(TimeSpan.FromMinutes(2));
        await using AnonymousPipeClientStream bootstrapPipe = new(
            PipeDirection.In,
            launch.BootstrapHandle);
        byte[] bootstrap = await BrokerProtocol.ReadBootstrapAsync(
            bootstrapPipe,
            bootstrapLifetime.Token).ConfigureAwait(false);
        byte[]? authenticationKey = null;
        try
        {
            BrokerProtocol.ParseBootstrap(
                bootstrap,
                launch.PipeName,
                out int clientProcessId,
                out int clientSessionId,
                out DateTimeOffset expiresUtc,
                out Guid clientInstanceId,
                out byte[] parsedAuthenticationKey,
                out string securityDirectory);
            authenticationKey = parsedAuthenticationKey;

            if (!TryResolveExpectedAeroClientPath(clientProcessId, out string expectedClientPath) ||
                clientProcessId != launch.ClientProcessId ||
                DateTimeOffset.UtcNow >= expiresUtc ||
                BrokerNativeMethods.GetCurrentParentProcessId() != clientProcessId ||
                !TryValidateAeroParent(clientProcessId, clientSessionId, expectedClientPath) ||
                !TryValidateSecurityDirectory(securityDirectory))
            {
                throw new BrokerStartupException(
                    3,
                    "client-or-bootstrap-validation");
            }

            PreparedBrokerLaunch result = new(
                launch.PipeName,
                clientProcessId,
                clientSessionId,
                clientInstanceId,
                expiresUtc,
                authenticationKey,
                securityDirectory,
                expectedClientPath);
            authenticationKey = null;
            return result;
        }
        finally
        {
            BrokerProtocol.Zero(authenticationKey);
            BrokerProtocol.Zero(bootstrap);
        }
    }

    internal static async Task<int> RunPreparedAsync(
        PreparedBrokerLaunch launch,
        CancellationToken cancellationToken)
    {
        using Process parent = Process.GetProcessById(launch.ClientProcessId);
        using CancellationTokenSource stopParentWatch = new();
        using CancellationTokenSource parentExited = new();
        Task parentExitTask = WatchParentExitAsync(
            parent,
            parentExited,
            stopParentWatch.Token);
        try
        {
            using CancellationTokenSource operationLifetime =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    parentExited.Token);
            using PasswordVaultService vault = new(launch.SecurityDirectory);
            using BrokerServer server = new(
                launch.PipeName,
                launch.ClientProcessId,
                launch.ClientSessionId,
                launch.ClientInstanceId,
                launch.ExpiresUtc,
                launch.AuthenticationKey,
                vault,
                launch.SecurityDirectory,
                launch.ExpectedClientPath);
            return await server.RunAsync(operationLifetime.Token);
        }
        finally
        {
            stopParentWatch.Cancel();
            try
            {
                await parentExitTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task WatchParentExitAsync(
        Process parent,
        CancellationTokenSource parentExited,
        CancellationToken cancellationToken)
    {
        await parent.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        parentExited.Cancel();
    }

    private static bool TryValidateSecurityDirectory(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Path.GetFileName(fullPath).Equals("Security", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string localAeroRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Aero");
            string guestRoot = Path.Combine(Path.GetTempPath(), "Aero", "Guest");
            return IsPathWithin(fullPath, localAeroRoot) || IsPathWithin(fullPath, guestRoot);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPathWithin(string path, string root)
    {
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveExpectedAeroClientPath(
        int clientProcessId,
        out string expectedPath)
    {
        expectedPath = string.Empty;
        try
        {
            if (!BrokerNativeMethods.TryGetProcessImagePath(
                    clientProcessId,
                    out string clientPath) ||
                !Path.GetFileName(clientPath).Equals(
                    "Aero.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string brokerPath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(brokerPath))
            {
                return false;
            }

            brokerPath = Path.GetFullPath(brokerPath);
            clientPath = Path.GetFullPath(clientPath);
            if (!AreTrustedSiblingPayloads(clientPath, brokerPath))
            {
                return false;
            }

            expectedPath = clientPath;
            return true;
        }
        catch
        {
            expectedPath = string.Empty;
            return false;
        }
    }

    private static bool AreTrustedSiblingPayloads(
        string clientPath,
        string brokerPath)
    {
        string clientDirectory = Path.GetDirectoryName(clientPath) ?? string.Empty;
        string brokerDirectory = Path.GetDirectoryName(brokerPath) ?? string.Empty;
        if (clientDirectory.Equals(
                brokerDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? clientPackageRoot = TryGetWindowsAppsPackageRoot(clientPath);
        string? brokerPackageRoot = TryGetWindowsAppsPackageRoot(brokerPath);
        if (clientPackageRoot is not null &&
            brokerPackageRoot is not null &&
            clientPackageRoot.Equals(
                brokerPackageRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        DirectoryInfo? clientProject = FindAncestor(
            clientDirectory,
            "Aero.App");
        DirectoryInfo? brokerProject = FindAncestor(
            brokerDirectory,
            "Aero.Security.Broker");
        return clientProject?.Parent is DirectoryInfo clientSource &&
               brokerProject?.Parent is DirectoryInfo brokerSource &&
               clientSource.FullName.Equals(
                   brokerSource.FullName,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetWindowsAppsPackageRoot(string path)
    {
        string windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps");
        string normalizedRoot = Path.GetFullPath(windowsApps)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string relative = normalizedPath[normalizedRoot.Length..];
        int separator = relative.IndexOf(Path.DirectorySeparatorChar);
        if (separator <= 0)
        {
            return null;
        }

        return Path.Combine(normalizedRoot, relative[..separator]);
    }

    private static DirectoryInfo? FindAncestor(string startPath, string name)
    {
        DirectoryInfo? current = new(startPath);
        while (current is not null)
        {
            if (current.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }
            current = current.Parent;
        }
        return null;
    }

    internal static void WriteStartupDiagnostic(
        int exitCode,
        string stage,
        Exception? exception)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Aero",
                "Security");
            Directory.CreateDirectory(directory);
            string message = string.Join(
                " | ",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                $"code={exitCode}",
                $"stage={stage}",
                $"type={exception?.GetType().FullName ?? "none"}",
                $"hresult=0x{exception?.HResult ?? 0:X8}",
                $"message={SanitizeDiagnostic(exception?.Message)}");
            File.AppendAllText(
                Path.Combine(directory, "broker-startup.log"),
                message + Environment.NewLine);
        }
        catch
        {
            // Diagnostics must never change the fail-closed result.
        }
    }

    private static string SanitizeDiagnostic(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "none" : "details-redacted";

    private static bool TryValidateAeroParent(
        int processId,
        int expectedSessionId,
        string expectedPath) =>
        BrokerNativeMethods.IsExpectedProcessImage(
            processId,
            expectedPath,
            expectedSessionId);

    private static bool TryParseArguments(string[] args, out BrokerLaunchArguments launch)
    {
        launch = default;
        string? pipeName = null;
        string? bootstrapHandle = null;
        int clientProcessId = 0;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--pipe-name" when index + 1 < args.Length:
                    pipeName = args[++index];
                    break;
                case "--bootstrap-handle" when index + 1 < args.Length:
                    bootstrapHandle = args[++index];
                    break;
                case "--client-pid" when index + 1 < args.Length &&
                    int.TryParse(
                        args[++index],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int parsedPid):
                    clientProcessId = parsedPid;
                    break;
                default:
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(pipeName) ||
            pipeName.Length > 180 ||
            !pipeName.StartsWith("Aero.Security.Broker.", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(bootstrapHandle) ||
            clientProcessId <= 0)
        {
            return false;
        }

        launch = new BrokerLaunchArguments(pipeName, bootstrapHandle, clientProcessId);
        return true;
    }

    private readonly record struct BrokerLaunchArguments(
        string PipeName,
        string BootstrapHandle,
        int ClientProcessId);
}

internal sealed class PreparedBrokerLaunch : IDisposable
{
    private byte[]? _authenticationKey;

    public PreparedBrokerLaunch(
        string pipeName,
        int clientProcessId,
        int clientSessionId,
        Guid clientInstanceId,
        DateTimeOffset expiresUtc,
        byte[] authenticationKey,
        string securityDirectory,
        string expectedClientPath)
    {
        PipeName = pipeName;
        ClientProcessId = clientProcessId;
        ClientSessionId = clientSessionId;
        ClientInstanceId = clientInstanceId;
        ExpiresUtc = expiresUtc;
        _authenticationKey = authenticationKey;
        SecurityDirectory = securityDirectory;
        ExpectedClientPath = expectedClientPath;
    }

    public string PipeName { get; }
    public int ClientProcessId { get; }
    public int ClientSessionId { get; }
    public Guid ClientInstanceId { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public byte[] AuthenticationKey => _authenticationKey
        ?? throw new ObjectDisposedException(nameof(PreparedBrokerLaunch));
    public string SecurityDirectory { get; }
    public string ExpectedClientPath { get; }

    public void Dispose()
    {
        byte[]? authenticationKey = Interlocked.Exchange(
            ref _authenticationKey,
            null);
        BrokerProtocol.Zero(authenticationKey);
    }
}

internal sealed class BrokerStartupException : Exception
{
    public BrokerStartupException(int exitCode, string stage)
        : base(stage)
    {
        ExitCode = exitCode;
        Stage = stage;
    }

    public int ExitCode { get; }
    public string Stage { get; }
}
