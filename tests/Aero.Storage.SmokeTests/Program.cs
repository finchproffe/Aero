using Aero.App.Data;
using System.Text.Json;

string root = Path.Combine(
    Path.GetTempPath(),
    $"Aero-Storage-Smoke-{Guid.NewGuid():N}");

try
{
    Directory.CreateDirectory(root);

    BrowserDataStore store = BrowserDataStore.Load(root);
    Uri firstUri = new("https://example.com/first");
    store.AddHistory(firstUri, "First page");
    bool bookmarked = store.ToggleBookmark(firstUri, "Example");
    Assert(bookmarked, "A bookmark should be created.");

    store.SaveSession(new BrowserSessionData
    {
        ActiveTabIndex = 0,
        Tabs =
        [
            new SessionTabEntry
            {
                Url = firstUri.AbsoluteUri,
                IsPinned = true
            }
        ]
    });
    store.FlushPendingWrites();

    string firstLibraryPath = Path.Combine(root, "library.json");
    string firstBackupPath = firstLibraryPath + ".bak";
    Assert(!File.Exists(firstBackupPath), "The first commit unexpectedly created a backup.");
    store.FlushPendingWrites();
    Assert(
        !File.Exists(firstBackupPath),
        "A clean shutdown flush rewrote unchanged library data and created a redundant backup.");

    BrowserDataStore reloaded = BrowserDataStore.Load(root);
    Assert(reloaded.Library.History.Count == 1, "History did not survive reload.");
    Assert(reloaded.Library.Bookmarks.Count == 1, "Bookmark did not survive reload.");
    Assert(reloaded.LoadSession().Tabs.Count == 1, "Session did not survive reload.");

    // A second atomic commit creates a .bak containing the previous valid file.
    reloaded.AddHistory(new Uri("https://example.com/second"), "Second page");
    reloaded.FlushPendingWrites();
    string libraryPath = Path.Combine(root, "library.json");
    string backupPath = libraryPath + ".bak";
    Assert(File.Exists(backupPath), "Atomic replacement did not retain a backup.");

    File.WriteAllText(libraryPath, "{this is intentionally invalid json");
    BrowserDataStore recovered = BrowserDataStore.Load(root);
    Assert(recovered.Library.History.Count >= 1, "Corrupt primary file was not recovered.");

    // A newer valid interrupted commit must win over an older backup.
    BrowserLibraryData newestRecovery = new()
    {
        History =
        [
            new HistoryEntry
            {
                Url = "https://newest-recovery.example/",
                Title = "Newest recovery",
                VisitedAt = DateTimeOffset.UtcNow
            }
        ],
        LegacyDefaultQuickLinksRemoved = true
    };
    string temporaryPath = libraryPath + ".tmp";
    File.WriteAllText(temporaryPath, JsonSerializer.Serialize(newestRecovery));
    File.SetLastWriteTimeUtc(temporaryPath, DateTime.UtcNow.AddSeconds(2));
    File.WriteAllText(libraryPath, "{broken again");

    BrowserDataStore recoveredNewest = BrowserDataStore.Load(root);
    Assert(
        recoveredNewest.Library.History.Count == 1 &&
        recoveredNewest.Library.History[0].Url.Contains("newest-recovery", StringComparison.Ordinal),
        "The newest valid recovery candidate was not selected.");
    Assert(!File.Exists(temporaryPath), "A recovered .tmp file should be cleaned up.");

    // The delayed writer must serialize the snapshot captured by SaveLibrary,
    // not a later unsaved mutation of the same mutable entry instance.
    BrowserDataStore snapshotStore = BrowserDataStore.Load(Path.Combine(root, "snapshot"));
    BookmarkEntry snapshotBookmark = snapshotStore.AddBookmark(
        "https://snapshot.example/",
        "Saved title");
    snapshotBookmark.Title = "Unsaved mutation";
    await Task.Delay(500);

    BrowserDataStore snapshotReloaded = BrowserDataStore.Load(Path.Combine(root, "snapshot"));
    Assert(
        snapshotReloaded.Library.Bookmarks.Single().Title == "Saved title",
        "Background persistence observed a mutable object after the snapshot was captured.");

    BrowserDataStore ephemeral = BrowserDataStore.CreateEphemeral();
    ephemeral.AddHistory(new Uri("https://example.net/"), "Ephemeral");
    ephemeral.FlushPendingWrites();
    Assert(ephemeral.Library.History.Count == 1, "Ephemeral in-memory storage failed.");

    Console.WriteLine("Aero.Storage smoke tests passed.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch
    {
        // The operating system may briefly retain a file handle after a failed test.
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
