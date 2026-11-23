using Aero.App.Data;
using System.Text.Json;

namespace Aero.App;

public sealed partial class MainWindow
{
    private const int HomeRecentLimit = 5; // one large card and four small ones
    private const int HomeListLimit = 8;

    /// <summary>
    /// What Aero Home shows below the search: recent pages, bookmarks, workspaces
    /// and the protection state. Only local data; "null" hides it (setting off, private or guest).
    /// Serialized with the default encoder, which escapes '<', so page titles cannot close the
    /// script tag they are embedded in.
    /// </summary>
    private string BuildHomeActivityJson()
    {
        if (!_settings.ShowHomeActivity || _isPrivateWindow || _browserProfile.IsGuest)
        {
            return "null";
        }

        DateTimeOffset dayStart = new(DateTime.Today);
        // History is newest first and holds one entry per address with its latest visit.
        HistoryEntry[] history = _dataStore.Library.History.Where(entry => IsWebAddress(entry.Url)).ToArray();
        var activity = new
        {
            todayCount = history.TakeWhile(entry => entry.VisitedAt >= dayStart).Count(),
            // One card per site: three inbox pages in a row would push everything else out.
            recent = history.DistinctBy(entry => new Uri(entry.Url).Host)
                .Take(HomeRecentLimit)
                .Select(entry => new { at = entry.VisitedAt.ToUnixTimeMilliseconds(), t = entry.Title, u = entry.Url }),
            bookmarks = _dataStore.Library.Bookmarks.Where(entry => IsWebAddress(entry.Url))
                .OrderByDescending(entry => entry.AddedAt)
                .Take(HomeListLimit)
                .Select(entry => new
                {
                    t = entry.Title,
                    u = entry.Url,
                    // Only inline images: a remote icon URL would be a request to a third party.
                    i = entry.Icon.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) ? entry.Icon : string.Empty
                }),
            workspaces = _dataStore.Library.Workspaces.Take(HomeListLimit)
                .Select(workspace => new { id = workspace.Id, name = workspace.Name, tabs = workspace.Tabs.Count }),
            guard = new
            {
                blocked = Interlocked.Read(ref _shieldBlockedSessionTotal),
                vault = !CookieVaultEnabled ? "off" : _settings.ProtectCookiesWithHello ? "hello" : "on",
                memory = App.ProcessGuarded
            }
        };
        return JsonSerializer.Serialize(activity);
    }

    private static bool IsWebAddress(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
