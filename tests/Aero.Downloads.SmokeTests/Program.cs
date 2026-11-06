using Aero.Downloads;

DownloadRequestCoordinator coordinator = new();
object operation = new();
Assert(coordinator.TryClaimOperation(operation), "The first operation claim must succeed.");
Assert(!coordinator.TryClaimOperation(operation), "The same WebView operation was claimed twice.");
coordinator.ReleaseOperationClaim(operation);
Assert(coordinator.TryClaimOperation(operation), "A released operation could not be claimed again.");

DateTimeOffset now = DateTimeOffset.UtcNow;
Assert(
    coordinator.TryClaimRequest(
        "https://example.com/file.zip#fragment",
        "file.zip",
        now,
        out string fingerprint),
    "The first request fingerprint must succeed.");
Assert(
    !coordinator.TryClaimRequest(
        "https://example.com/file.zip",
        "file (2).zip",
        now.AddSeconds(1),
        out _),
    "A duplicate request with a browser-added numeric suffix was not suppressed.");
coordinator.ReleaseRequest(fingerprint);
Assert(
    coordinator.TryClaimRequest(
        "https://example.com/file.zip",
        "file.zip",
        now.AddSeconds(2),
        out _),
    "A cancelled request fingerprint was not released.");

Assert(
    DownloadPathPolicy.IsPotentiallyDangerous(null, "https://example.com/setup.exe"),
    "Executable downloads must be classified as potentially dangerous.");
Assert(
    DownloadPathPolicy.IsPotentiallyDangerous("setup.exe. ", null),
    "Trailing-dot executable names must not bypass the dangerous-file warning.");
Assert(
    !DownloadPathPolicy.IsPotentiallyDangerous("archive.zip", "https://example.com/archive.zip"),
    "A normal archive was incorrectly classified as executable content.");
Assert(
    DownloadPathPolicy.ResolveSafeFileName("CON.txt", null) == "_CON.txt",
    "Reserved Windows device names must be neutralized.");
Assert(
    DownloadPathPolicy.ResolveSafeFileName("..", null) == "download",
    "Traversal-like empty file names must fall back to download.");

string downloadRoot = Path.Combine(Path.GetTempPath(), $"Aero-Downloads-Smoke-{Guid.NewGuid():N}");
try
{
    Directory.CreateDirectory(downloadRoot);
    string firstPath = DownloadPathPolicy.CreateAutomaticPath(
        downloadRoot,
        "archive.zip",
        "https://example.com/archive.zip");
    File.WriteAllText(firstPath, "occupied");
    string secondPath = DownloadPathPolicy.CreateAutomaticPath(
        downloadRoot,
        "archive.zip",
        "https://example.com/archive.zip");
    Assert(
        Path.GetFileName(secondPath) == "archive (1).zip",
        "Automatic downloads must not overwrite an existing file.");
}
finally
{
    try
    {
        Directory.Delete(downloadRoot, recursive: true);
    }
    catch
    {
        // Antivirus software can briefly retain a just-created smoke-test file.
    }
}

DownloadTransferRateEstimator estimator = new(0, now);
Assert(estimator.Update(1024, now.AddSeconds(1)), "A valid transfer sample was ignored.");
Assert(estimator.BytesPerSecond >= 1023, "The transfer rate estimate is unexpectedly low.");

string cookieFile = NetscapeCookieFile.Format(
[
    new ExportedCookie(".youtube.com", "/", true, true, 1_900_000_000.9, "SID", "abc"),
    new ExportedCookie("www.youtube.com", "", false, false, null, "PREF", "f6=40000000"),
    new ExportedCookie(".youtube.com", "/", true, false, null, "EVIL", "x\n.example.com\tTRUE\t/\tFALSE\t0\tinjected\t1"),
]);
string[] cookieLines = cookieFile.Split('\n', StringSplitOptions.RemoveEmptyEntries);
Assert(cookieLines[0] == "# Netscape HTTP Cookie File", "The cookies.txt header is missing.");
Assert(
    cookieLines[1] == "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1900000000\tSID\tabc",
    "An HttpOnly domain cookie was not written in Netscape format.");
Assert(
    cookieLines[2] == "www.youtube.com\tFALSE\t/\tFALSE\t0\tPREF\tf6=40000000",
    "A host-only session cookie was not written in Netscape format.");
Assert(cookieLines.Length == 3, "A cookie value containing a newline injected an extra cookie line.");

Console.WriteLine("Aero.Downloads smoke tests passed.");
return 0;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
