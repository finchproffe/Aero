using Aero.App.Data;
using System.Text;
using System.Text.Json;

CheckBrowserSessionReader();

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

// Importing tabs must bring only what is open now: not back/forward pages, closed tabs or closed windows.
static void CheckBrowserSessionReader()
{
    List<byte> snss = [.. "SNSS"u8, 1, 0, 0, 0];
    void Command(byte id, byte[] payload)
    {
        snss.AddRange(BitConverter.GetBytes((ushort)(payload.Length + 1)));
        snss.Add(id);
        snss.AddRange(payload);
    }
    byte[] Ints(params int[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();
    void Navigation(int tab, int index, string url)
    {
        byte[] text = Encoding.UTF8.GetBytes(url);
        byte[] body = [.. Ints(tab, index, text.Length), .. text, .. new byte[(4 - text.Length % 4) % 4], .. Ints(0)];
        Command(6, [.. Ints(body.Length), .. body]);
    }

    Command(0, Ints(10, 1));
    Navigation(1, 0, "https://a.example/old");
    Navigation(1, 1, "https://a.example/now");
    Command(7, Ints(1, 0)); // went back
    Navigation(2, 0, "https://b.example/closed");
    Command(16, [.. Ints(2), .. new byte[8]]);
    Command(0, Ints(20, 3));
    Navigation(3, 0, "https://c.example/closed-window");
    Command(17, [.. Ints(20), .. new byte[8]]);
    Navigation(4, 0, "https://d.example/x");
    Navigation(4, 1, "https://d.example/y");
    snss.AddRange([40, 0, 6]); // torn final command
    List<string> chromium = BrowserSessionReader.ReadChromiumSession(snss.ToArray());
    Assert(
        chromium.SequenceEqual(["https://a.example/old", "https://d.example/y"]),
        "Chromium session import returned " + string.Join(", ", chromium));
    Assert(BrowserSessionReader.ReadChromiumSession([.. "SNSS"u8, 2, 0, 0, 0]).Count == 0, "An encrypted session should be skipped.");

    // An LZ4 block with an overlapping match, like Firefox writes.
    string head = """{"windows":[{"tabs":[{"entries":[{"url":"https://e.example/1"},{"url":"https://e.example/2"}],"index":1}],"_closedTabs":[{"state":{"entries":[{"url":"https://closed.example/"}]}}]}],"_closedWindows":[{"tabs":[{"entries":[{"url":"https://gone.example/"}]}]}],"x":"a""";
    byte[] headBytes = Encoding.UTF8.GetBytes(head);
    List<byte> block = [0xF2];
    for (int left = headBytes.Length - 15; ; left -= 255)
    {
        block.Add((byte)Math.Min(left, 255));
        if (left < 255) break;
    }
    block.AddRange(headBytes);
    block.AddRange([1, 0]); // offset 1, match length 2 + 4: "aaaaaa"
    block.AddRange([0x20, (byte)'"', (byte)'}']);
    int total = headBytes.Length + 6 + 2;
    byte[] mozlz4 = [.. "mozLz40\0"u8, .. BitConverter.GetBytes(total), .. block];
    List<string> firefox = BrowserSessionReader.ReadFirefoxSession(mozlz4);
    Assert(firefox.SequenceEqual(["https://e.example/1"]), "Firefox session import returned " + string.Join(", ", firefox));
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
