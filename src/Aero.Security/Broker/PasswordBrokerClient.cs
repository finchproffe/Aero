using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Aero.App.Security;

/// <summary>
/// Launches an on-demand, one-operation Aero.Security.Broker process. The broker
/// receives its per-launch authentication key through an inherited anonymous pipe;
/// no secret is placed in command-line arguments, environment variables or files.
/// </summary>
public sealed class PasswordBrokerClient : IDisposable
{
    private static readonly TimeSpan BootstrapLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MinimumOperationInterval = TimeSpan.FromSeconds(2);
    private static readonly SemaphoreSlim ProcessOperationGate = new(1, 1);
    private static long _lastProcessOperationTimestamp;

    private readonly Guid _clientInstanceId = Guid.NewGuid();
    private readonly string _securityDirectory;
    private readonly int _clientProcessId = Environment.ProcessId;
    private readonly int _clientSessionId = BrokerNativeMethods.GetCurrentSessionId();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private int _disposed;

    public PasswordBrokerClient(string securityDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(securityDirectory);
        _securityDirectory = Path.GetFullPath(securityDirectory);
        Directory.CreateDirectory(_securityDirectory);
    }

    public Guid ClientInstanceId => _clientInstanceId;

    /// <summary>
    /// Requests a one-time password reveal in a Broker-owned native window.
    /// No password bytes are returned to Aero.App; only an authenticated
    /// completion acknowledgement crosses the IPC boundary.
    /// </summary>
    public async Task<bool> RevealCredentialAsync(
        string origin,
        IntPtr ownerWindow,
        CancellationToken cancellationToken = default)
    {
        BrokerOperationResult result = await RunOperationAsync(
            PasswordBrokerOperation.Reveal,
            origin,
            ownerWindow,
            navigationId: 0,
            documentIdentity: Guid.NewGuid(),
            usernameUtf8: ReadOnlyMemory<byte>.Empty,
            cancellationToken).ConfigureAwait(false);

        result.Credential?.Dispose();
        if (result.Outcome == BrokerOperationOutcome.Credential)
        {
            throw new PasswordBrokerException(
                PasswordBrokerFailure.ProtocolError,
                "Aero Security Broker returned plaintext for a reveal request.");
        }

        return result.Outcome == BrokerOperationOutcome.RevealCompleted;
    }

    public async Task<BrokerCredentialLease?> GetCredentialAsync(
        PasswordBrokerOperation operation,
        string origin,
        IntPtr ownerWindow,
        ulong navigationId,
        Guid documentIdentity,
        ReadOnlyMemory<byte> usernameUtf8 = default,
        CancellationToken cancellationToken = default)
    {
        if (operation == PasswordBrokerOperation.Reveal)
        {
            throw new ArgumentException(
                "Reveal must use RevealCredentialAsync so plaintext never enters Aero.App.",
                nameof(operation));
        }

        BrokerOperationResult result = await RunOperationAsync(
            operation,
            origin,
            ownerWindow,
            navigationId,
            documentIdentity,
            usernameUtf8,
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome == BrokerOperationOutcome.RevealCompleted)
        {
            result.Credential?.Dispose();
            throw new PasswordBrokerException(
                PasswordBrokerFailure.ProtocolError,
                "Aero Security Broker returned reveal completion for a credential request.");
        }

        return result.Credential;
    }

    private async Task<BrokerOperationResult> RunOperationAsync(
        PasswordBrokerOperation operation,
        string origin,
        IntPtr ownerWindow,
        ulong navigationId,
        Guid documentIdentity,
        ReadOnlyMemory<byte> usernameUtf8,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
        CancellationToken effectiveCancellation = linkedCancellation.Token;
        string normalizedOrigin = PasswordVaultService.NormalizeHttpsOrigin(origin);
        if (!BrokerNativeMethods.WindowBelongsToProcess(ownerWindow, _clientProcessId))
        {
            throw new PasswordBrokerException(
                PasswordBrokerFailure.WindowRejected,
                "Aero Security Broker rejected the owner window.");
        }

        await ProcessOperationGate.WaitAsync(effectiveCancellation).ConfigureAwait(false);
        try
        {
            long previousTimestamp = _lastProcessOperationTimestamp;
            if (previousTimestamp != 0)
            {
                TimeSpan sinceLastOperation = Stopwatch.GetElapsedTime(previousTimestamp);
                if (sinceLastOperation < MinimumOperationInterval)
                {
                    await Task.Delay(
                        MinimumOperationInterval - sinceLastOperation,
                        effectiveCancellation).ConfigureAwait(false);
                }
            }
            _lastProcessOperationTimestamp = Stopwatch.GetTimestamp();

            return await ExecuteCredentialOperationAsync(
                operation,
                normalizedOrigin,
                ownerWindow,
                navigationId,
                documentIdentity,
                usernameUtf8,
                effectiveCancellation).ConfigureAwait(false);
        }
        finally
        {
            ProcessOperationGate.Release();
        }
    }

    private async Task<BrokerOperationResult> ExecuteCredentialOperationAsync(
        PasswordBrokerOperation operation,
        string normalizedOrigin,
        IntPtr ownerWindow,
        ulong navigationId,
        Guid documentIdentity,
        ReadOnlyMemory<byte> usernameUtf8,
        CancellationToken cancellationToken)
    {
        string brokerPath = ResolveBrokerPath();
        string pipeName = CreatePipeName();
        byte[] authenticationKey = RandomNumberGenerator.GetBytes(BrokerProtocol.AuthenticationKeyLength);
        byte[] clientChallenge = RandomNumberGenerator.GetBytes(BrokerProtocol.ChallengeLength);
        byte[]? bootstrap = null;
        byte[]? helloPayload = null;
        byte[]? requestPayload = null;
        byte[]? originUtf8 = null;
        byte[]? expectedOriginHash = null;
        Process? brokerProcess = null;
        bool authenticatedChannel = false;
        bool credentialRequestSent = false;

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        CancellationToken operationToken = timeout.Token;

        try
        {
            DateTimeOffset expiresUtc = DateTimeOffset.UtcNow.Add(BootstrapLifetime);
            bootstrap = BrokerProtocol.CreateBootstrap(
                _clientProcessId,
                _clientSessionId,
                expiresUtc,
                _clientInstanceId,
                authenticationKey,
                pipeName,
                _securityDirectory);

            using AnonymousPipeServerStream bootstrapPipe = new(
                PipeDirection.Out,
                HandleInheritability.Inheritable);
            ProcessStartInfo startInfo = new(brokerPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(brokerPath) ?? AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("--pipe-name");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--bootstrap-handle");
            startInfo.ArgumentList.Add(bootstrapPipe.GetClientHandleAsString());
            startInfo.ArgumentList.Add("--client-pid");
            startInfo.ArgumentList.Add(_clientProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

            try
            {
                brokerProcess = Process.Start(startInfo) ??
                    throw new PasswordBrokerException(
                        PasswordBrokerFailure.BrokerUnavailable,
                        $"Aero Security Broker returned no process handle. Path: {brokerPath}");
            }
            catch (PasswordBrokerException)
            {
                throw;
            }
            catch (Win32Exception exception)
            {
                throw new PasswordBrokerException(
                    PasswordBrokerFailure.BrokerUnavailable,
                    $"Aero Security Broker could not start (Win32 {exception.NativeErrorCode}): " +
                    $"{exception.Message}. Path: {brokerPath}",
                    exception);
            }
            catch (Exception exception)
            {
                throw new PasswordBrokerException(
                    PasswordBrokerFailure.BrokerUnavailable,
                    $"Aero Security Broker could not start: {exception.Message}. Path: {brokerPath}",
                    exception);
            }
            BrokerNativeMethods.TryAllowForegroundWindow(brokerProcess.Id);
            bootstrapPipe.DisposeLocalCopyOfClientHandle();

            await bootstrapPipe.WriteAsync(bootstrap, operationToken).ConfigureAwait(false);
            await bootstrapPipe.FlushAsync(operationToken).ConfigureAwait(false);

            using NamedPipeClientStream pipe = new(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using CancellationTokenSource connectTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(operationToken);
            connectTimeout.CancelAfter(ConnectTimeout);

            Task connectTask = pipe.ConnectAsync(connectTimeout.Token);
            Task brokerExitTask = brokerProcess.WaitForExitAsync(CancellationToken.None);
            Task completedTask = await Task.WhenAny(
                connectTask,
                brokerExitTask).ConfigureAwait(false);

            if (ReferenceEquals(completedTask, brokerExitTask) && !pipe.IsConnected)
            {
                int exitCode = brokerProcess.ExitCode;
                throw new PasswordBrokerException(
                    PasswordBrokerFailure.BrokerUnavailable,
                    DescribeBrokerEarlyExit(exitCode, brokerPath));
            }

            await connectTask.ConfigureAwait(false);

            int pipeServerProcessId = BrokerNativeMethods.GetPipeServerProcessId(pipe.SafePipeHandle);
            if (pipeServerProcessId != brokerProcess.Id ||
                !BrokerNativeMethods.IsExpectedProcessImage(
                    pipeServerProcessId,
                    brokerPath,
                    _clientSessionId))
            {
                throw new PasswordBrokerException(
                    PasswordBrokerFailure.ClientRejected,
                    "Aero rejected an unexpected Security Broker process.");
            }

            Guid helloRequestId = Guid.NewGuid();
            helloPayload = BrokerProtocol.BuildClientHelloPayload(
                _clientProcessId,
                _clientSessionId,
                _clientInstanceId,
                clientChallenge,
                expiresUtc);
            await BrokerProtocol.WriteFrameAsync(
                pipe,
                BrokerMessageType.ClientHello,
                sequence: 1,
                helloRequestId,
                helloPayload,
                authenticationKey,
                operationToken).ConfigureAwait(false);

            byte[] serverChallenge = new byte[BrokerProtocol.ChallengeLength];
            try
            {
                using BrokerFrame serverHello = await BrokerProtocol.ReadFrameAsync(
                    pipe,
                    expectedSequence: 1,
                    authenticationKey,
                    operationToken).ConfigureAwait(false);
                ValidateServerHello(
                    serverHello,
                    helloRequestId,
                    brokerProcess.Id,
                    clientChallenge,
                    serverChallenge);
                authenticatedChannel = true;

                originUtf8 = Encoding.UTF8.GetBytes(normalizedOrigin);
                expectedOriginHash = BrokerProtocol.HashOrigin(originUtf8);
                Guid operationRequestId = Guid.NewGuid();
                requestPayload = BrokerProtocol.BuildCredentialRequestPayload(
                    operation,
                    _clientInstanceId,
                    clientChallenge,
                    serverChallenge,
                    navigationId,
                    documentIdentity,
                    ownerWindow,
                    originUtf8,
                    usernameUtf8.Span);
                await BrokerProtocol.WriteFrameAsync(
                    pipe,
                    BrokerMessageType.CredentialRequest,
                    sequence: 2,
                    operationRequestId,
                    requestPayload,
                    authenticationKey,
                    operationToken).ConfigureAwait(false);
                credentialRequestSent = true;

                using BrokerFrame response = await BrokerProtocol.ReadFrameAsync(
                    pipe,
                    expectedSequence: 2,
                    authenticationKey,
                    operationToken).ConfigureAwait(false);
                if (response.RequestId != operationRequestId)
                {
                    throw new PasswordBrokerException(
                        PasswordBrokerFailure.ProtocolError,
                        "Aero Security Broker response binding failed.");
                }

                return response.Type switch
                {
                    BrokerMessageType.CredentialResponse => new BrokerOperationResult(
                        BrokerOperationOutcome.Credential,
                        ParseCredentialResponse(
                            response.Payload,
                            navigationId,
                            documentIdentity,
                            expectedOriginHash)),
                    BrokerMessageType.RevealCompleted => ValidateRevealCompleted(
                        response.Payload,
                        navigationId,
                        documentIdentity,
                        expectedOriginHash),
                    BrokerMessageType.NotFound => new BrokerOperationResult(
                        BrokerOperationOutcome.NotFound,
                        null),
                    BrokerMessageType.Failure => throw CreateBrokerFailure(
                        BrokerProtocol.ParseFailure(response.Payload)),
                    _ => throw new PasswordBrokerException(
                        PasswordBrokerFailure.ProtocolError,
                        "Aero Security Broker returned an invalid response.")
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(serverChallenge);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PasswordBrokerException(
                PasswordBrokerFailure.Timeout,
                "Aero Security Broker operation timed out.",
                exception);
        }
        catch (PasswordBrokerException)
        {
            throw;
        }
        catch (EndOfStreamException exception)
        {
            int? exitCode = null;
            try
            {
                if (brokerProcess?.HasExited == true)
                {
                    exitCode = brokerProcess.ExitCode;
                }
            }
            catch
            {
                // The channel state remains authoritative when process status races exit.
            }

            PasswordBrokerFailure failure = authenticatedChannel && credentialRequestSent
                ? PasswordBrokerFailure.ProtocolError
                : PasswordBrokerFailure.BrokerUnavailable;
            string stage = authenticatedChannel && credentialRequestSent
                ? "after the authenticated credential request"
                : "during authenticated channel bootstrap";
            string processStatus = exitCode.HasValue
                ? $" Broker exit code: {exitCode.Value}."
                : string.Empty;
            throw new PasswordBrokerException(
                failure,
                $"Aero Security Broker closed the pipe {stage} before a complete signed frame was received.{processStatus}",
                exception);
        }
        catch (Exception exception)
        {
            throw new PasswordBrokerException(
                PasswordBrokerFailure.BrokerUnavailable,
                $"Aero Security Broker operation failed closed: {exception.Message}",
                exception);
        }
        finally
        {
            BrokerProtocol.Zero(authenticationKey);
            BrokerProtocol.Zero(clientChallenge);
            BrokerProtocol.Zero(bootstrap);
            BrokerProtocol.Zero(helloPayload);
            BrokerProtocol.Zero(requestPayload);
            BrokerProtocol.Zero(originUtf8);
            BrokerProtocol.Zero(expectedOriginHash);

            if (brokerProcess is not null)
            {
                try
                {
                    if (!brokerProcess.HasExited)
                    {
                        using CancellationTokenSource exitTimeout = new(TimeSpan.FromSeconds(3));
                        await brokerProcess.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
                    }
                }
                catch
                {
                    try
                    {
                        if (!brokerProcess.HasExited)
                        {
                            brokerProcess.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                        // Fail closed. The broker also monitors its parent and has a short idle timeout.
                    }
                }
                finally
                {
                    brokerProcess.Dispose();
                }
            }
        }
    }

    private void ValidateServerHello(
        BrokerFrame frame,
        Guid expectedRequestId,
        int expectedBrokerProcessId,
        ReadOnlySpan<byte> clientChallenge,
        Span<byte> serverChallenge)
    {
        ReadOnlySpan<byte> payload = frame.Payload;
        if (frame.Type != BrokerMessageType.ServerHello ||
            frame.RequestId != expectedRequestId ||
            payload.Length != 88 ||
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(0, 4)) != expectedBrokerProcessId ||
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4)) != _clientSessionId ||
            new Guid(payload.Slice(8, 16)) != _clientInstanceId ||
            !CryptographicOperations.FixedTimeEquals(payload.Slice(24, 32), clientChallenge))
        {
            throw new PasswordBrokerException(
                PasswordBrokerFailure.ProtocolError,
                "Aero Security Broker challenge validation failed.");
        }

        payload.Slice(56, 32).CopyTo(serverChallenge);
        if (BrokerProtocol.IsAllZero(serverChallenge))
        {
            throw new PasswordBrokerException(
                PasswordBrokerFailure.ProtocolError,
                "Aero Security Broker returned an invalid challenge.");
        }
    }

    private static BrokerOperationResult ValidateRevealCompleted(
        ReadOnlySpan<byte> payload,
        ulong expectedNavigationId,
        Guid expectedDocumentIdentity,
        ReadOnlySpan<byte> expectedOriginHash)
    {
        if (payload.Length != 56 ||
            BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(0, 8)) != expectedNavigationId ||
            new Guid(payload.Slice(8, 16)) != expectedDocumentIdentity ||
            !CryptographicOperations.FixedTimeEquals(payload.Slice(24, 32), expectedOriginHash))
        {
            throw new PasswordBrokerException(
                PasswordBrokerFailure.ProtocolError,
                "Aero Security Broker reveal completion binding failed.");
        }

        return new BrokerOperationResult(BrokerOperationOutcome.RevealCompleted, null);
    }

    private static BrokerCredentialLease ParseCredentialResponse(
        ReadOnlySpan<byte> payload,
        ulong expectedNavigationId,
        Guid expectedDocumentIdentity,
        ReadOnlySpan<byte> expectedOriginHash)
    {
        if (payload.Length < 64 ||
            BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(0, 8)) != expectedNavigationId ||
            new Guid(payload.Slice(8, 16)) != expectedDocumentIdentity ||
            !CryptographicOperations.FixedTimeEquals(payload.Slice(24, 32), expectedOriginHash))
        {
            throw new PasswordBrokerException(
                PasswordBrokerFailure.ProtocolError,
                "Aero Security Broker credential binding failed.");
        }

        int usernameLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(56, 4));
        int passwordLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(60, 4));
        int expectedLength;
        try
        {
            expectedLength = checked(64 + usernameLength + passwordLength);
        }
        catch (OverflowException exception)
        {
            throw new PasswordBrokerException(
                PasswordBrokerFailure.ProtocolError,
                "Aero Security Broker returned invalid credential lengths.",
                exception);
        }

        if (usernameLength < 0 || usernameLength > BrokerProtocol.MaximumUsernameLength ||
            passwordLength <= 0 || passwordLength > BrokerProtocol.MaximumPasswordLength ||
            expectedLength != payload.Length)
        {
            throw new PasswordBrokerException(
                PasswordBrokerFailure.ProtocolError,
                "Aero Security Broker returned invalid credential lengths.");
        }

        return new BrokerCredentialLease(
            payload.Slice(64, usernameLength),
            payload.Slice(64 + usernameLength, passwordLength));
    }

    private static PasswordBrokerException CreateBrokerFailure(PasswordBrokerFailure failure) =>
        failure switch
        {
            PasswordBrokerFailure.VerificationCancelled => new(
                failure,
                "Windows Hello verification was not completed."),
            PasswordBrokerFailure.OriginRejected => new(
                failure,
                "Aero Security Broker rejected the requested origin."),
            PasswordBrokerFailure.WindowRejected => new(
                failure,
                "Aero Security Broker rejected the owner window."),
            PasswordBrokerFailure.Timeout => new(
                failure,
                "Aero Security Broker operation timed out."),
            PasswordBrokerFailure.NotFound => new(
                failure,
                "No credential is saved for this site."),
            PasswordBrokerFailure.SecurityDriverUnavailable => new(
                failure,
                "Aero Security Broker received a legacy driver-required failure."),
            PasswordBrokerFailure.UserModeProtectionUnavailable => new(
                failure,
                "Aero Security Broker could not activate its required user-mode protection."),
            _ => new(
                failure,
                "Aero Security Broker failed closed.")
        };

    private static string CreatePipeName()
    {
        Span<byte> random = stackalloc byte[16];
        RandomNumberGenerator.Fill(random);
        return $"Aero.Security.Broker.{Environment.ProcessId}.{Convert.ToHexString(random)}";
    }

    private static string ResolveBrokerPath()
    {
        List<string> runtimeCandidates = [];
        AddCandidateDirectory(runtimeCandidates, AppContext.BaseDirectory);
        AddCandidateDirectory(runtimeCandidates, Path.GetDirectoryName(Environment.ProcessPath));
        AddCandidateDirectory(
            runtimeCandidates,
            Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location));

        try
        {
            using Process process = Process.GetCurrentProcess();
            AddCandidateDirectory(
                runtimeCandidates,
                Path.GetDirectoryName(process.MainModule?.FileName));
        }
        catch
        {
            // AppContext and Environment.ProcessPath remain available.
        }

        string? packageRoot = TryGetPackageInstallDirectory();
        if (!string.IsNullOrWhiteSpace(packageRoot))
        {
            AddCandidateDirectory(runtimeCandidates, packageRoot);
            try
            {
                foreach (string packagedBroker in Directory.EnumerateFiles(
                             packageRoot,
                             "Aero.Security.Broker.exe",
                             SearchOption.AllDirectories))
                {
                    AddCandidatePath(runtimeCandidates, packagedBroker);
                }
            }
            catch
            {
                // Package root is read-only; failure to enumerate is non-fatal.
            }
        }

        List<string> candidates = [];
        AddDevelopmentCandidates(candidates, runtimeCandidates);
        candidates.AddRange(runtimeCandidates);

        List<string> incompletePayloads = [];
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            string[] missing = GetMissingBrokerPayloadFiles(candidate);
            if (missing.Length == 0)
            {
                return Path.GetFullPath(candidate);
            }

            incompletePayloads.Add(
                $"{candidate} (missing: {string.Join(", ", missing)})");
        }

        if (incompletePayloads.Count > 0)
        {
            throw new PasswordBrokerException(
                PasswordBrokerFailure.BrokerUnavailable,
                "Aero Security Broker executable exists, but its payload is incomplete: " +
                string.Join("; ", incompletePayloads));
        }

        throw new PasswordBrokerException(
            PasswordBrokerFailure.BrokerUnavailable,
            "Aero.Security.Broker.exe was not found in the Aero executable directory " +
            "or the installed MSIX package. Checked: " +
            string.Join("; ", candidates.Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    private static void AddCandidateDirectory(
        List<string> candidates,
        string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            AddCandidatePath(
                candidates,
                Path.Combine(directory, "Aero.Security.Broker.exe"));
        }
    }

    private static void AddCandidatePath(
        List<string> candidates,
        string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            candidates.Add(Path.GetFullPath(path));
        }
        catch
        {
            // Ignore malformed runtime paths and continue with trusted candidates.
        }
    }

    private static void AddDevelopmentCandidates(
        List<string> candidates,
        IReadOnlyList<string> runtimeCandidates)
    {
        foreach (string baseDirectory in runtimeCandidates
                     .Select(Path.GetDirectoryName)
                     .Where(static path => !string.IsNullOrWhiteSpace(path))
                     .Cast<string>()
                     .ToArray())
        {
            DirectoryInfo? current = new(baseDirectory);
            while (current is not null &&
                   !current.Name.Equals(
                       "Aero.App",
                       StringComparison.OrdinalIgnoreCase))
            {
                current = current.Parent;
            }

            if (current?.Parent is not DirectoryInfo srcDirectory)
            {
                continue;
            }

            string appBin = Path.Combine(current.FullName, "bin");
            string relativeOutput;
            try
            {
                relativeOutput = Path.GetRelativePath(appBin, baseDirectory);
            }
            catch
            {
                continue;
            }

            AddCandidatePath(
                candidates,
                Path.Combine(
                    srcDirectory.FullName,
                    "Aero.Security.Broker",
                    "bin",
                    relativeOutput,
                    "Aero.Security.Broker.exe"));
        }
    }

    private static string? TryGetPackageInstallDirectory()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current.InstalledLocation.Path;
        }
        catch
        {
            return null;
        }
    }

    private static string[] GetMissingBrokerPayloadFiles(string brokerExecutable)
    {
        string directory = Path.GetDirectoryName(brokerExecutable)
            ?? string.Empty;
        string[] required =
        [
            "Aero.Security.Broker.dll",
            "Aero.Security.Broker.deps.json",
            "Aero.Security.Broker.runtimeconfig.json",
            "Aero.Security.Broker.pri",
            "Aero.Security.dll"
        ];
        return required
            .Where(file => !File.Exists(Path.Combine(directory, file)))
            .ToArray();
    }

    private static string DescribeBrokerEarlyExit(int exitCode, string brokerPath)
    {
        string reason = exitCode switch
        {
            2 => "launch arguments were rejected",
            3 => "the Broker rejected the Aero parent process, package path, bootstrap, or Security directory",
            4 => "the bootstrap channel timed out",
            5 => "an exception occurred during Broker startup",
            7 => "Broker process hardening could not be activated on this system",
            10 => "the Broker rejected the pipe client",
            11 => "the authenticated handshake was rejected",
            12 => "the Broker operation timed out",
            13 => "the Broker protocol frame was invalid",
            _ => "the Broker failed during startup"
        };

        string logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Aero",
            "Security",
            "broker-startup.log");
        return $"Aero Security Broker exited before opening its protected channel " +
               $"(code {exitCode}: {reason}). Path: {brokerPath}. " +
               $"Startup log: {logPath}";
    }

    private enum BrokerOperationOutcome
    {
        NotFound,
        Credential,
        RevealCompleted
    }

    private readonly record struct BrokerOperationResult(
        BrokerOperationOutcome Outcome,
        BrokerCredentialLease? Credential);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            nameof(PasswordBrokerClient));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _lifetimeCancellation.Cancel();
            }
            catch
            {
                // Closing Aero must still fail closed even if a cancellation callback fails.
            }
            // Do not dispose the CTS while an in-flight operation may still be
            // observing its token. The short-lived object is reclaimed with the window.
        }
    }
}
