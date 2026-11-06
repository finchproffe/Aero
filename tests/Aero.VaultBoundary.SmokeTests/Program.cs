using System.Text.RegularExpressions;
using System.Xml.Linq;

string repositoryRoot = FindRepositoryRoot();

string Read(params string[] parts)
{
    string path = Path.Combine(new[] { repositoryRoot }.Concat(parts).ToArray());
    if (!File.Exists(path))
    {
        throw new InvalidOperationException($"Required Vault boundary file is missing: {path}");
    }
    return File.ReadAllText(path);
}

void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

void RequireMatch(string text, string pattern, string message) =>
    Require(Regex.IsMatch(
        text,
        pattern,
        RegexOptions.CultureInvariant | RegexOptions.Singleline), message);

string savedSignIns = Read("src", "Aero.App", "MainWindow.SavedSignIns.cs");
string vaultService = Read("src", "Aero.Security", "Vault", "PasswordVaultService.cs");
string brokerClient = Read("src", "Aero.Security", "Broker", "PasswordBrokerClient.cs");
string brokerProtocol = Read("src", "Aero.Security", "Broker", "BrokerProtocol.cs");
string brokerServer = Read("src", "Aero.Security.Broker", "BrokerServer.cs");
string brokerProgram = Read("src", "Aero.Security.Broker", "Program.cs");
string brokerAppXaml = Read("src", "Aero.Security.Broker", "App.xaml");
string brokerApplication = Read("src", "Aero.Security.Broker", "BrokerApplication.cs");
string brokerHardening = Read("src", "Aero.Security.Broker", "BrokerProcessHardening.cs");
string secureRevealWindow = Read("src", "Aero.Security.Broker", "SecureRevealWindow.cs");
string kernelProtection = Read("src", "Aero.Security.Broker", "AeroSecurityKernelProtection.cs");
string appProjectText = Read("src", "Aero.App", "Aero.App.csproj");
string brokerProjectText = Read("src", "Aero.Security.Broker", "Aero.Security.Broker.csproj");
string msixBuild = Read("scripts", "msix", "Build-Test-Msix.ps1");

// Aero.App receives only the signed completion acknowledgement for Reveal.
RequireMatch(
    savedSignIns,
    @"GetPasswordForRevealAsync\s*\(\s*origin\s*,\s*windowHandle",
    "Saved sign-ins do not use the Broker-only reveal boundary.");
Require(!savedSignIns.Contains("GetPasswordForRevealJitAsync", StringComparison.Ordinal),
    "Aero.App can invoke the plaintext JIT reveal API.");
Require(!savedSignIns.Contains("PasswordLease", StringComparison.Ordinal) &&
        !savedSignIns.Contains("lease.PasswordUtf8", StringComparison.Ordinal),
    "Aero.App receives a plaintext reveal lease.");
MatchCollection passwordAssignments = Regex.Matches(
    savedSignIns,
    @"passwordField\.Password\s*=\s*([^;]+);",
    RegexOptions.CultureInvariant);
Require(passwordAssignments.Cast<Match>().All(match =>
        match.Groups[1].Value.Trim().Equals("string.Empty", StringComparison.Ordinal)),
    "Aero.App assigns reveal plaintext to a PasswordBox.");

RequireMatch(
    vaultService,
    @"Task<bool>\s+GetPasswordForRevealAsync[\s\S]*?brokerClient\.RevealCredentialAsync",
    "PasswordVaultService does not delegate Reveal to the authenticated Broker.");
RequireMatch(
    brokerClient,
    @"RevealCredentialAsync[\s\S]*?BrokerOperationOutcome\.RevealCompleted",
    "Broker client does not require RevealCompleted.");
Require(brokerClient.Contains(
        "returned plaintext for a reveal request",
        StringComparison.Ordinal),
    "Broker client no longer rejects plaintext reveal responses.");

// Protocol and lifecycle properties: one reader, no blind retry, complete drain.
Require(brokerProtocol.Contains("public const ushort Version = 3;", StringComparison.Ordinal),
    "Unexpected authenticated Broker protocol version.");
Require(brokerProtocol.Contains("RevealCompleted = 7", StringComparison.Ordinal),
    "RevealCompleted frame type is missing.");
int serverFrameReaders = Regex.Count(
    brokerServer,
    @"BrokerProtocol\.ReadFrameAsync\s*\(",
    RegexOptions.CultureInvariant);
Require(serverFrameReaders == 2,
    $"Broker must read exactly ClientHello and CredentialRequest, found {serverFrameReaders} frame readers.");
Require(!brokerServer.Contains("MonitorClientDisconnectAsync", StringComparison.Ordinal) &&
        !Regex.IsMatch(brokerServer, @"\bpipe\.ReadAsync\s*\("),
    "Broker still performs a competing read from its duplex pipe.");
Require(brokerServer.Contains("pipe.WaitForPipeDrain();", StringComparison.Ordinal),
    "Broker disconnects before draining its signed response.");
Require(!brokerClient.Contains("ExecuteCredentialOperationWithRetryAsync", StringComparison.Ordinal) &&
        !brokerClient.Contains("ShouldRetryAbruptBrokerClose", StringComparison.Ordinal),
    "Broker client still performs a blind retry after Windows Hello.");
RequireMatch(
    brokerClient,
    @"AddDevelopmentCandidates\s*\(\s*candidates\s*,\s*runtimeCandidates\s*\)\s*;\s*candidates\.AddRange\s*\(\s*runtimeCandidates\s*\)",
    "Development builds do not prefer the Broker's complete source output over Aero.App's flattened output.");
RequireMatch(
    brokerClient,
    @"catch\s*\(EndOfStreamException[\s\S]*?PasswordBrokerFailure\.ProtocolError",
    "Authenticated EOF is not classified as protocol corruption.");

// Kernel protection is optional; the user-mode boundary remains fail-closed.
RequireMatch(
    kernelProtection,
    @"static\s+bool\s+TryActivate\s*\([\s\S]*?out\s+AeroSecurityKernelProtection\?[\s\S]*?out\s+KernelProtectionStatus",
    "Kernel protection has no non-throwing TryActivate API.");
RequireMatch(
    brokerServer,
    @"TryRestrictCurrentProcessDacl\s*\(\)[\s\S]*?AeroSecurityKernelProtection\.TryActivate",
    "Required process DACL is not applied before optional kernel activation.");
Require(brokerServer.Contains("UserModeProtectionUnavailable", StringComparison.Ordinal),
    "Required user-mode hardening has no precise failure category.");
Require(!brokerServer.Contains("PasswordBrokerFailure.SecurityDriverUnavailable", StringComparison.Ordinal),
    "Driver unavailability still stops a Broker operation.");
Require(brokerServer.Contains("standard-protection-", StringComparison.Ordinal) &&
        brokerServer.Contains("enhanced-protection-active", StringComparison.Ordinal),
    "Kernel fallback status is not recorded in the safe Broker audit.");
Require(brokerServer.Contains("exception.Stage", StringComparison.Ordinal) &&
        brokerServer.Contains("process-integrity-audit", StringComparison.Ordinal),
    "Broker hardening failures do not record their safe lifecycle stage.");
int trustedRootsFirst = brokerHardening.IndexOf(
    "foreach (string root in trustedReadOnlyRoots)",
    StringComparison.Ordinal);
int writableBrokerFallback = brokerHardening.IndexOf(
    "modulePath.StartsWith(brokerDirectory",
    StringComparison.Ordinal);
Require(trustedRootsFirst >= 0 && writableBrokerFallback > trustedRootsFirst,
    "Immutable package roots are not evaluated before the writable Broker filename whitelist.");
RequireMatch(
    brokerHardening,
    @"EnsureTrustedForSecretOperation\s*\(\s*\)[\s\S]*?StopModuleMonitoring\s*\(\s*\)",
    "The DLL notification watcher is not stopped at the pre-secret integrity gate.");
Require(!brokerHardening.Contains("TerminateProcess(", StringComparison.Ordinal) &&
        !brokerHardening.Contains("Environment.FailFast", StringComparison.Ordinal),
    "Broker hardening can still kill an authenticated pipe instead of returning a signed failure.");
Require(brokerHardening.Contains("AuditLoadedModulesWithRetry", StringComparison.Ordinal),
    "Transient loader snapshots are not retried before an integrity failure.");
Require(brokerServer.Contains("TimeSpan.FromSeconds(10)", StringComparison.Ordinal),
    "Broker reveal lifetime is not ten seconds.");

// The Broker reveal UI is deliberately one standard WinUI 3 text field.
RequireMatch(
    secureRevealWindow,
    @"sealed\s+class\s+SecureRevealWindow\s*:\s*Window",
    "SecureRevealWindow is not a Microsoft.UI.Xaml.Window.");
RequireMatch(
    brokerApplication,
    @"sealed\s+partial\s+class\s+BrokerApplication\s*:\s*Application",
    "Broker has no WinUI Application dispatcher host.");
Require(brokerApplication.Contains("InitializeComponent();", StringComparison.Ordinal) &&
        brokerAppXaml.Contains("XamlControlsResources", StringComparison.Ordinal),
    "Broker does not load WinUI control resources through App.xaml.");
Require(!brokerApplication.Contains("Resources.MergedDictionaries.Add", StringComparison.Ordinal),
    "Broker still initializes XamlControlsResources through the crashing programmatic path.");
foreach (string legacyWindowToken in new[]
         {
             "CreateWindowEx",
             "RegisterClassEx",
             "WmPaint",
             "GetMessage(",
             "DispatchMessage("
         })
{
    Require(!secureRevealWindow.Contains(legacyWindowToken, StringComparison.Ordinal),
        $"Legacy reveal-window implementation remains: {legacyWindowToken}");
}
Require(secureRevealWindow.Contains("new TextBox", StringComparison.Ordinal) &&
        secureRevealWindow.Contains("IsReadOnly = true", StringComparison.Ordinal) &&
        secureRevealWindow.Contains("_passwordField.Text = Encoding.UTF8.GetString", StringComparison.Ordinal),
    "Broker reveal UI is not the required standard read-only WinUI TextBox.");
Require(!secureRevealWindow.Contains("SecurePasswordPresenter", StringComparison.Ordinal) &&
        !secureRevealWindow.Contains("WriteableBitmap", StringComparison.Ordinal) &&
        !secureRevealWindow.Contains("Border", StringComparison.Ordinal) &&
        !secureRevealWindow.Contains("TextBlock", StringComparison.Ordinal),
    "Custom password presentation UI remains in the Broker window.");
Require(secureRevealWindow.Contains("SetWindowDisplayAffinity", StringComparison.Ordinal),
    "WinUI reveal window is not excluded from screen capture when supported.");

XDocument brokerProject = XDocument.Parse(brokerProjectText);
XDocument appProject = XDocument.Parse(appProjectText);
string? Property(XDocument project, string name) =>
    project.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value;
string? PackageVersion(XDocument project, string package) =>
    project.Descendants()
        .FirstOrDefault(element =>
            element.Name.LocalName == "PackageReference" &&
            string.Equals((string?)element.Attribute("Include"), package, StringComparison.OrdinalIgnoreCase))
        ?.Attribute("Version")?.Value;

Require(string.Equals(Property(brokerProject, "OutputType"), "WinExe", StringComparison.OrdinalIgnoreCase),
    "Broker build creates a console subsystem executable.");
Require(string.Equals(Property(brokerProject, "UseWinUI"), "true", StringComparison.OrdinalIgnoreCase),
    "Broker does not enable WinUI 3.");
Require(string.Equals(Property(brokerProject, "WindowsAppSDKSelfContained"), "true", StringComparison.OrdinalIgnoreCase),
    "Broker does not deploy its Windows App SDK runtime.");
Require((Property(brokerProject, "DefineConstants") ?? string.Empty).Contains(
        "DISABLE_XAML_GENERATED_MAIN",
        StringComparison.Ordinal),
    "Broker XAML application does not preserve the authenticated custom entry point.");
Require(brokerProgram.Contains("ComWrappersSupport.InitializeComWrappers", StringComparison.Ordinal),
    "Broker does not initialize WinRT COM wrappers before starting WinUI.");
Require(brokerProgram.Contains("--startup-probe", StringComparison.Ordinal) &&
        brokerProgram.Contains("RunWinUiStartupProbe", StringComparison.Ordinal),
    "Broker has no executable-level WinUI startup probe.");
Require(string.Equals(
        PackageVersion(brokerProject, "Microsoft.WindowsAppSDK"),
        PackageVersion(appProject, "Microsoft.WindowsAppSDK"),
        StringComparison.OrdinalIgnoreCase),
    "Broker and Aero.App use different Windows App SDK versions.");
Require(appProjectText.Contains("Aero.Security.Broker.pri", StringComparison.Ordinal) &&
        msixBuild.Contains("Aero.Security.Broker.pri", StringComparison.Ordinal),
    "Broker WinUI resource index is missing from desktop/MSIX payload rules.");
Require(brokerProjectText.Contains("CreateIndependentMsixPriPayload", StringComparison.Ordinal) &&
        brokerProjectText.Contains("Aero.Security.Broker.pri.payload", StringComparison.Ordinal) &&
        appProjectText.Contains("Aero.Security.Broker.pri.payload", StringComparison.Ordinal) &&
        appProjectText.Contains("Link=\"Aero.Security.Broker.pri\"", StringComparison.Ordinal),
    "Broker PRI is not isolated from Aero.App's MSIX resource merge.");
Require(msixBuild.Contains("/p:SelfContained=true", StringComparison.Ordinal) &&
        msixBuild.Contains("/p:RuntimeIdentifier=win-$Platform", StringComparison.Ordinal) &&
        msixBuild.Contains("net10.0-windows10.0.19041.0\\win-$Platform", StringComparison.Ordinal),
    "The packaged Broker is not built as an explicit self-contained runtime target.");

Console.WriteLine("Aero Vault boundary smoke tests passed.");

static string FindRepositoryRoot()
{
    DirectoryInfo? current = new(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, "src", "Aero.App")) &&
            Directory.Exists(Path.Combine(current.FullName, "src", "Aero.Security.Broker")))
        {
            return current.FullName;
        }
        current = current.Parent;
    }
    throw new InvalidOperationException("Could not locate the Aero repository root.");
}
