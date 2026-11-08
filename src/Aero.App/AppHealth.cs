using System.Net.Http;
using System.Text.Json;

namespace Aero.App;

/// <summary>
/// Crash journal and update check. Both stay on this PC: a crash is only written to a local file
/// the user can attach to an issue, and the update check asks GitHub for the latest release tag
/// without sending anything about the user.
/// </summary>
internal static class AppHealth
{
    public const string ReleasesPage = "https://github.com/finchproffe/Aero/releases/latest";
    private const string LatestReleaseApi = "https://api.github.com/repos/finchproffe/Aero/releases/latest";
    private const long MaximumLogBytes = 1024 * 1024;

    public static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aero", "Logs", "crash.log");

    private static readonly string SeenMarkerPath = CrashLogPath + ".seen";

    public static Version CurrentVersion => typeof(AppHealth).Assembly.GetName().Version ?? new Version(0, 0);

    public static void RecordCrash(Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            if (File.Exists(CrashLogPath) && new FileInfo(CrashLogPath).Length > MaximumLogBytes)
            {
                File.Delete(CrashLogPath);
            }

            File.AppendAllText(CrashLogPath,
                $"[{DateTimeOffset.UtcNow:O}] Aero {CurrentVersion} on {Environment.OSVersion}{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // The process is already failing; a full disk must not turn into a second crash.
        }
    }

    /// <summary>True once for every crash recorded since the last call.</summary>
    public static bool TakeUnseenCrash()
    {
        try
        {
            if (!File.Exists(CrashLogPath) ||
                File.Exists(SeenMarkerPath) && File.GetLastWriteTimeUtc(SeenMarkerPath) >= File.GetLastWriteTimeUtc(CrashLogPath))
            {
                return false;
            }

            File.WriteAllText(SeenMarkerPath, string.Empty);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Returns the newer released version, or null when up to date or offline.</summary>
    public static async Task<Version?> FindNewerReleaseAsync()
    {
        try
        {
            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Aero-Browser"); // GitHub's API rejects requests without one.
            using JsonDocument release = JsonDocument.Parse(await http.GetStringAsync(LatestReleaseApi));
            string tag = release.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
            return Version.TryParse(tag.TrimStart('v', 'V'), out Version? latest) && Normalize(latest) > Normalize(CurrentVersion)
                ? latest
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
            JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    // "0.24" and "0.24.0.0" must compare equal: missing parts count as zero.
    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
}
