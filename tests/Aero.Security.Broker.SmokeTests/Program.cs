using Aero.App.Security;
using Aero.Security.BrokerHost;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task WriteRevealCompletionAsync(
    NamedPipeServerStream server,
    ulong sequence,
    Guid requestId,
    byte[] authenticationKey,
    CancellationToken cancellationToken)
{
    await server.WaitForConnectionAsync(cancellationToken);
    byte[] completion = BrokerProtocol.BuildRevealCompletedPayload(
        sequence,
        requestId,
        "https://example.com"u8);
    try
    {
        await BrokerProtocol.WriteFrameAsync(
            server,
            BrokerMessageType.RevealCompleted,
            sequence,
            requestId,
            completion,
            authenticationKey,
            cancellationToken);
        server.WaitForPipeDrain();
    }
    finally
    {
        BrokerProtocol.Zero(completion);
    }
}

static void VerifyWinUiRuntimeStartup()
{
    Exception? startupException = null;
    Thread thread = new(() =>
    {
        try
        {
            Aero.Security.BrokerHost.Program.InitializeWinUiRuntime();
            BrokerApplication? application = null;
            Application.Start(_ =>
            {
                DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(dispatcher));
                application = BrokerApplication.CreateStartupProbe();
                if (!dispatcher.TryEnqueue(application.Exit))
                {
                    throw new InvalidOperationException(
                        "The WinUI startup probe could not enqueue shutdown.");
                }
            });
            GC.KeepAlive(application);
        }
        catch (Exception exception)
        {
            startupException = exception;
        }
    })
    {
        IsBackground = true,
        Name = "Aero Broker WinUI startup probe"
    };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Require(thread.Join(TimeSpan.FromSeconds(15)),
        "The WinUI runtime startup probe did not terminate.");
    if (startupException is not null)
    {
        throw new InvalidOperationException(
            "The Broker WinUI runtime could not start.",
            startupException);
    }
}

static void VerifyBrokerExecutableStartup()
{
    string brokerPath = Path.Combine(AppContext.BaseDirectory, "Aero.Security.Broker.exe");
    Require(File.Exists(brokerPath), "The Broker executable was not copied to the smoke-test output.");
    using Process process = Process.Start(new ProcessStartInfo
    {
        FileName = brokerPath,
        Arguments = "--startup-probe",
        UseShellExecute = false,
        CreateNoWindow = true
    }) ?? throw new InvalidOperationException("The Broker startup probe could not be started.");
    Require(process.WaitForExit((int)TimeSpan.FromSeconds(30).TotalMilliseconds),
        "The Broker executable startup probe timed out.");
    Require(process.ExitCode == 0,
        $"The Broker executable startup probe exited with code {process.ExitCode}.");
}

HashSet<string> generated = new(StringComparer.Ordinal);
for (int sample = 0; sample < 512; sample++)
{
    byte[] password = BrokerPasswordGenerator.GenerateUtf8();
    try
    {
        Require(password.Length == BrokerPasswordGenerator.DefaultLength, "Generated password length changed.");
        Require(password.Any(value => value is >= (byte)'A' and <= (byte)'Z'), "Uppercase category missing.");
        Require(password.Any(value => value is >= (byte)'a' and <= (byte)'z'), "Lowercase category missing.");
        Require(password.Any(value => value is >= (byte)'0' and <= (byte)'9'), "Digit category missing.");
        Require(password.Any(value => "!#$%&*+-=?@_"u8.Contains(value)), "Symbol category missing.");
        Require(!password.Contains((byte)'0') &&
                !password.Contains((byte)'1') &&
                !password.Contains((byte)'I') &&
                !password.Contains((byte)'O') &&
                !password.Contains((byte)'l'),
            "Ambiguous characters must remain excluded.");
        generated.Add(Convert.ToHexString(password));
    }
    finally
    {
        CryptographicOperations.ZeroMemory(password);
    }
}
Require(generated.Count == 512, "Generated password collision detected in smoke sample.");

byte[] origin = Encoding.UTF8.GetBytes("https://example.com");
byte[] username = Encoding.UTF8.GetBytes("user@example.com");
byte[] challenge1 = Enumerable.Repeat((byte)0x31, BrokerProtocol.ChallengeLength).ToArray();
byte[] challenge2 = Enumerable.Repeat((byte)0x52, BrokerProtocol.ChallengeLength).ToArray();
byte[] payload = BrokerProtocol.BuildCredentialRequestPayload(
    PasswordBrokerOperation.GenerateAndSave,
    Guid.NewGuid(),
    challenge1,
    challenge2,
    navigationId: 17,
    documentIdentity: Guid.NewGuid(),
    ownerWindow: new IntPtr(123),
    originUtf8: origin,
    usernameUtf8: username);
try
{
    Require(BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(113, 4)) == origin.Length,
        "Origin length was not encoded.");
    Require(BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(117, 4)) == username.Length,
        "Username length was not encoded.");
    Require(payload.AsSpan(121, origin.Length).SequenceEqual(origin),
        "Origin bytes changed in Broker request.");
    Require(payload.AsSpan(121 + origin.Length, username.Length).SequenceEqual(username),
        "Username bytes changed in Broker request.");
}
finally
{
    BrokerProtocol.Zero(payload);
    BrokerProtocol.Zero(origin);
    BrokerProtocol.Zero(username);
    BrokerProtocol.Zero(challenge1);
    BrokerProtocol.Zero(challenge2);
}

bool rejectedUsernameOnFill = false;
try
{
    _ = BrokerProtocol.BuildCredentialRequestPayload(
        PasswordBrokerOperation.Fill,
        Guid.NewGuid(),
        new byte[BrokerProtocol.ChallengeLength],
        new byte[BrokerProtocol.ChallengeLength],
        navigationId: 1,
        documentIdentity: Guid.NewGuid(),
        ownerWindow: new IntPtr(1),
        originUtf8: "https://example.com"u8,
        usernameUtf8: "unexpected"u8);
}
catch (ArgumentException)
{
    rejectedUsernameOnFill = true;
}
Require(rejectedUsernameOnFill, "Fill request accepted unexpected username payload.");

// Verify ten independent one-shot Broker responses over real Windows named
// pipes. The client must consume each signed RevealCompleted frame completely
// before the server closes, exercising the transport path that previously
// surfaced as EndOfStreamException after Windows Hello.
byte[] authenticationKey = RandomNumberGenerator.GetBytes(BrokerProtocol.AuthenticationKeyLength);
try
{
    for (ulong sequence = 1; sequence <= 10; sequence++)
    {
        string pipeName = $"Aero-Security-Broker-Smoke-{Guid.NewGuid():N}";
        Guid requestId = Guid.NewGuid();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        await using NamedPipeClientStream client = new(
            ".",
            pipeName,
            PipeDirection.In,
            PipeOptions.Asynchronous);
        Task serverTask = WriteRevealCompletionAsync(
            server,
            sequence,
            requestId,
            authenticationKey,
            timeout.Token);
        await client.ConnectAsync(timeout.Token);
        using BrokerFrame frame = await BrokerProtocol.ReadFrameAsync(
            client,
            sequence,
            authenticationKey,
            timeout.Token);
        Require(frame.Type == BrokerMessageType.RevealCompleted,
            "Sequential reveal frame type changed.");
        Require(frame.RequestId == requestId,
            "Sequential reveal frame binding changed.");
        Require(frame.Payload.Length == 56,
            "Sequential reveal completion payload was truncated.");
        await serverTask;
    }
}
finally
{
    CryptographicOperations.ZeroMemory(authenticationKey);
}

Require(typeof(SecureRevealWindow).IsSubclassOf(typeof(Window)),
    "SecureRevealWindow is not a real Microsoft.UI.Xaml.Window.");
VerifyWinUiRuntimeStartup();
VerifyBrokerExecutableStartup();

string immutablePackageRoot = @"C:\Program Files\WindowsApps\";
string immutableBrokerDirectory = immutablePackageRoot + @"Aero.Browser_0.24.0.0_x64__test\";
Require(BrokerProcessHardening.IsTrustedModulePath(
        immutableBrokerDirectory + "WinUIEdit.dll",
        immutableBrokerDirectory,
        [immutablePackageRoot]),
    "A signed read-only MSIX module was rejected by Broker hardening.");
string writableBrokerDirectory = @"D:\Zodiac\Aero\src\Aero.Security.Broker\bin\x64\Debug\";
Require(!BrokerProcessHardening.IsTrustedModulePath(
        writableBrokerDirectory + "WinUIEdit.dll",
        writableBrokerDirectory,
        [immutablePackageRoot]),
    "A non-whitelisted module in writable Broker output was trusted.");
Require(BrokerProcessHardening.IsTrustedModulePath(
        writableBrokerDirectory + "Microsoft.UI.dll",
        writableBrokerDirectory,
        [immutablePackageRoot]),
    "A whitelisted development Broker module was rejected.");

MethodInfo? tryActivate = typeof(AeroSecurityKernelProtection).GetMethod(
    "TryActivate",
    BindingFlags.Public | BindingFlags.Static);
Require(tryActivate is not null && tryActivate.ReturnType == typeof(bool),
    "Kernel protection has no non-throwing activation boundary.");
ParameterInfo[] tryActivateParameters = tryActivate!.GetParameters();
Require(tryActivateParameters.Length == 2 &&
        tryActivateParameters.All(parameter => parameter.IsOut),
    "Kernel protection activation does not return protection and status via out parameters.");

Require(typeof(BrokerServer).GetMethod(
        "MonitorClientDisconnectAsync",
        BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance) is null,
    "Broker still has a competing pipe disconnect reader.");
Require(typeof(PasswordBrokerClient).GetMethod(
        "ExecuteCredentialOperationWithRetryAsync",
        BindingFlags.NonPublic | BindingFlags.Instance) is null,
    "Password Broker still retries a completed Windows Hello interaction.");

// Cookie vault: persistent cookies are adopted and hidden, sign-outs and real session cookies drop out.
DateTimeOffset vaultNow = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
VaultCookie sid = new("SID", "a", ".youtube.com", "/", 1_900_000_000, true, true, 1);
VaultCookie pref = new("PREF", "x", "www.youtube.com", "/", 1_900_000_000, true, false, 1);
CookieVaultLedger ledger = new([]);
CookieVaultReconcileResult first = ledger.Reconcile(
    [new ObservedCookie(sid, IsSession: false), new ObservedCookie(pref, IsSession: false)], vaultNow);
Require(first.Changed && first.ToMakeSession.Count == 2 && ledger.Entries.Count == 2,
    "Persistent cookies were not adopted into the cookie vault.");
CookieVaultReconcileResult steady = ledger.Reconcile(
    [new ObservedCookie(sid, IsSession: true), new ObservedCookie(pref, IsSession: true)], vaultNow);
Require(!steady.Changed && steady.ToMakeSession.Count == 0,
    "The vault rewrote cookies that WebView2 already holds only in memory.");
CookieVaultReconcileResult signOut = ledger.Reconcile(
    [new ObservedCookie(pref with { Value = "session-only" }, IsSession: true)], vaultNow);
Require(signOut.Changed && ledger.Entries.Count == 0,
    "A signed-out cookie or a site-replaced session cookie stayed in the vault.");
CookieVaultLedger expiring = new([sid with { ExpiresUnixSeconds = 1_700_000_000 }]);
CookieVaultReconcileResult expiry = expiring.Reconcile([new ObservedCookie(sid, IsSession: true)], vaultNow);
Require(expiry.ToDelete.Count == 1 && expiring.Entries.Count == 0,
    "An expired vault cookie kept living as a session cookie.");

byte[] vaultKey = RandomNumberGenerator.GetBytes(32);
byte[] sealedVault = CookieVaultFile.Seal(vaultKey, [sid, pref], "profile-a");
Require(CookieVaultFile.Open(vaultKey, sealedVault, "profile-a").SequenceEqual([sid, pref]),
    "The cookie vault did not round-trip.");
Require(sealedVault.AsSpan().IndexOf("youtube"u8) < 0,
    "Cookie data is visible in the sealed vault file.");
bool rejected(Action open)
{
    try { open(); return false; }
    catch (CryptographicException) { return true; }
}
Require(rejected(() => CookieVaultFile.Open(vaultKey, sealedVault, "profile-b")),
    "A vault from another profile was accepted.");
sealedVault[^1] ^= 1;
Require(rejected(() => CookieVaultFile.Open(vaultKey, sealedVault, "profile-a")),
    "A tampered cookie vault was accepted.");

// App-bound key: frames round-trip, oversized ones are refused before allocation, and a key
// file in an unknown format fails closed instead of being guessed at.
using (MemoryStream frame = new())
{
    AppBoundKey.WriteFrame(frame, AppBoundKey.OpUnwrap, vaultKey);
    frame.Position = 0;
    (byte head, byte[] body) = AppBoundKey.ReadFrame(frame);
    Require(head == AppBoundKey.OpUnwrap && body.SequenceEqual(vaultKey), "An app-bound frame did not round-trip.");
}
bool oversizedRejected = false;
try { _ = AppBoundKey.ReadFrame(new MemoryStream([AppBoundKey.OpUnwrap, 0xFF, 0xFF])); }
catch (InvalidDataException) { oversizedRejected = true; }
Require(oversizedRejected, "An oversized app-bound frame was accepted.");
Require(rejected(() => CookieVaultKey.Unwrap("unused", [(byte)'X', 1, 2], IntPtr.Zero)) &&
        rejected(() => CookieVaultKey.Unwrap("unused", [], IntPtr.Zero)),
    "A cookie vault key in an unknown format was not refused.");
Require(CookieVaultKey.IsHelloWrapped([(byte)'H', 1]) && !CookieVaultKey.IsHelloWrapped([(byte)'A', 1]),
    "The cookie vault key mode byte is misread.");

// Folder sync: edits and deletions travel by timestamp, an emptied store never wipes the
// other devices, the binary format round-trips, and a wrong passphrase is refused.
{
    byte[] P(string text) => Encoding.UTF8.GetBytes(text);
    List<SyncItem> published = [new("a", 100, false, P("1")), new("b", 100, false, P("2"))];
    List<SyncItem> stamped = FolderSync.Stamp(
        new Dictionary<string, byte[]> { ["a"] = P("1"), ["c"] = P("3") }, published, 200);
    Require(stamped.Single(i => i.Key == "a").ModifiedAt == 100 &&
            stamped.Single(i => i.Key == "c").ModifiedAt == 200 &&
            stamped.Single(i => i.Key == "b") is { Deleted: true, ModifiedAt: 200 },
        "Sync stamping lost an unchanged time, a new item or a deletion.");
    Require(FolderSync.Stamp(new Dictionary<string, byte[]>(), published, 200).Count == 0,
        "An emptied local store turned into deletions that would wipe the other devices.");

    Dictionary<string, SyncItem> merged = FolderSync.Merge(
    [
        stamped,
        [new("a", 300, false, P("edited")), new("b", 150, false, P("stale"))]
    ]);
    Require(merged["a"].Payload.SequenceEqual(P("edited")) && merged["b"].Deleted && merged["c"].ModifiedAt == 200,
        "Sync merge did not pick the newest change per key.");
    Require(FolderSync.Merge([[new("x", 5, false, P("v"))], [new("x", 5, true, [])]])["x"].Deleted,
        "A deletion tied with an edit must win so it cannot resurrect.");

    List<SyncItem> roundTrip = FolderSync.Deserialize(FolderSync.Serialize(stamped));
    Require(roundTrip.Count == stamped.Count &&
            roundTrip.Zip(stamped).All(p => p.First.Key == p.Second.Key && p.First.ModifiedAt == p.Second.ModifiedAt &&
                                            p.First.Deleted == p.Second.Deleted && p.First.Payload.SequenceEqual(p.Second.Payload)),
        "The sync format did not round-trip.");
    bool truncatedRejected = false;
    try { FolderSync.Deserialize(FolderSync.Serialize(stamped).AsSpan(0, 7)); }
    catch (InvalidDataException) { truncatedRejected = true; }
    Require(truncatedRejected, "A truncated sync file was accepted.");

    string syncFolder = Path.Combine(Path.GetTempPath(), "aero-sync-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(syncFolder);
    try
    {
        using (FolderSync.Keys firstKeys = FolderSync.OpenOrCreate(syncFolder, "correct horse battery"))
        {
            FolderSync.WriteFile(FolderSync.FilePath(syncFolder, "dev1", FolderSync.LibraryExtension),
                FolderSync.Context(FolderSync.LibraryExtension, "dev1"), firstKeys.Library, stamped);
        }

        using (FolderSync.Keys secondKeys = FolderSync.OpenOrCreate(syncFolder, "correct horse battery"))
        {
            Require(FolderSync.ReadOthers(syncFolder, "dev2", FolderSync.LibraryExtension, secondKeys.Library).Single().Count == stamped.Count,
                "A second device could not read the first device's sync file.");
            Require(FolderSync.ReadOthers(syncFolder, "dev2", FolderSync.LibraryExtension, secondKeys.Passwords).Count == 0,
                "The password key opened a library file: the two keys are not independent.");
        }

        Require(rejected(() => FolderSync.OpenOrCreate(syncFolder, "wrong passphrase!!").Dispose()),
            "A wrong sync passphrase was accepted.");
    }
    finally
    {
        Directory.Delete(syncFolder, recursive: true);
    }
}

// Process guard: after the DACL write a same-user process can no longer open the target
// for memory access, but can still wait on it (WebView2 and the broker rely on that).
using (Process guarded = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
       {
           CreateNoWindow = true,
           UseShellExecute = false
       })!)
{
    try
    {
        Require(ProcessAccessGuard.TryRestrict(guarded.Id), "The process DACL could not be restricted.");
        using Process reopened = Process.GetProcessById(guarded.Id);
        bool memoryOpenDenied = false;
        try { _ = reopened.Handle; }
        catch (System.ComponentModel.Win32Exception) { memoryOpenDenied = true; }
        Require(memoryOpenDenied, "A guarded process can still be opened with full access.");
        Require(!reopened.HasExited, "A guarded process can no longer be waited on.");
        Require(!ProcessAccessGuard.TryRestrict(guarded.Id),
            "The owner can still rewrite a guarded process DACL.");
    }
    finally
    {
        guarded.Kill();
    }
}

Require(ProcessAccessGuard.AddGuardAces("D:(A;;GA;;;SY)", "S-1-5-21-1") ==
        "D:(D;;0xc2bfa;;;S-1-5-21-1)(A;;RC;;;OW)(A;;GA;;;SY)",
    "The deny ACE must precede every existing grant.");
Require(ProcessAccessGuard.AddGuardAces("D:NO_ACCESS_CONTROL", "S-1-5-21-1") is null &&
        ProcessAccessGuard.AddGuardAces(null, "S-1-5-21-1") is null,
    "A NULL or missing DACL must not be guarded blindly.");

Console.WriteLine("Aero Security Broker smoke tests passed.");
