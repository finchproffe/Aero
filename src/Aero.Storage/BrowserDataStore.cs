namespace Aero.App.Data;

public sealed class BrowserDataStore : IDisposable
{
    private const int MaximumHistoryEntries = 750;

    private static readonly TimeSpan LibraryWriteDelay = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan SessionWriteDelay = TimeSpan.FromMilliseconds(320);

    private readonly AtomicJsonFileStore _jsonStore;
    private readonly object _persistenceGate = new();
    private readonly SemaphoreSlim _libraryWriteGate = new(1, 1);
    private readonly SemaphoreSlim _sessionWriteGate = new(1, 1);
    private readonly HashSet<Task> _activePersistenceTasks = [];
    private readonly bool _persistToDisk;
    private readonly string? _libraryFile;
    private readonly string? _sessionFile;
    private CancellationTokenSource? _libraryWriteCancellation;
    private CancellationTokenSource? _sessionWriteCancellation;
    private BrowserSessionData? _pendingSessionSnapshot;
    private long _libraryWriteVersion;
    private long _sessionWriteVersion;
    private long _libraryPersistedVersion;
    private long _sessionPersistedVersion;
    private long _libraryRevision = 1;
    private int _disposeState;
    private int _disposeFlushCompleted;
    private int _persistenceGatesDisposed;

    private BrowserDataStore(bool persistToDisk, string? dataFolder)
    {
        _persistToDisk = persistToDisk;
        _jsonStore = new AtomicJsonFileStore(dataFolder);
        _libraryFile = dataFolder is null ? null : Path.Combine(dataFolder, "library.json");
        _sessionFile = dataFolder is null ? null : Path.Combine(dataFolder, "session.json");
    }

    public BrowserLibraryData Library { get; private set; } = new();

    public long LibraryRevision => Interlocked.Read(ref _libraryRevision);

    public static BrowserDataStore Load(string? storageRoot = null)
    {
        string root = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Aero");
        BrowserDataStore store = new(persistToDisk: true, dataFolder: root);
        store.Library = store._jsonStore.Read(store._libraryFile!, new BrowserLibraryData());
        store.NormalizeLibrary();
        if (store.RemoveLegacyDefaultQuickLinksOnce())
        {
            store.SaveLibrary();
        }
        return store;
    }

    public static BrowserDataStore CreateEphemeral() => new(
        persistToDisk: false,
        dataFolder: null);

    public BrowserSessionData LoadSession() => _persistToDisk && _sessionFile is not null
        ? _jsonStore.Read(_sessionFile, new BrowserSessionData())
        : new BrowserSessionData();

    public void SaveSession(BrowserSessionData session)
    {
        if (!_persistToDisk || _sessionFile is null ||
            Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        BrowserSessionData snapshot = CloneSession(session);
        long version = Interlocked.Increment(ref _sessionWriteVersion);
        CancellationTokenSource cancellation = new();
        CancellationTokenSource? previous;
        Task persistenceTask;

        lock (_persistenceGate)
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                cancellation.Dispose();
                return;
            }

            _pendingSessionSnapshot = snapshot;
            previous = _sessionWriteCancellation;
            _sessionWriteCancellation = cancellation;
            persistenceTask = PersistSessionAfterDelayAsync(snapshot, version, cancellation);
            _activePersistenceTasks.Add(persistenceTask);
        }

        TrackPersistenceCompletion(persistenceTask);
        TryCancel(previous);
    }

    public void FlushPendingWrites()
    {
        if (!_persistToDisk)
        {
            return;
        }

        BrowserSessionData? sessionSnapshot;
        CancellationTokenSource? libraryCancellation;
        CancellationTokenSource? sessionCancellation;
        long libraryVersion = Interlocked.Read(ref _libraryWriteVersion);
        long sessionVersion = Interlocked.Read(ref _sessionWriteVersion);

        lock (_persistenceGate)
        {
            libraryCancellation = _libraryWriteCancellation;
            sessionCancellation = _sessionWriteCancellation;
            _libraryWriteCancellation = null;
            _sessionWriteCancellation = null;
            sessionSnapshot = _pendingSessionSnapshot;
        }

        TryCancel(libraryCancellation);
        TryCancel(sessionCancellation);

        if (_libraryFile is not null &&
            Interlocked.Read(ref _libraryPersistedVersion) < libraryVersion)
        {
            BrowserLibraryData librarySnapshot = CreateLibrarySnapshot();
            _libraryWriteGate.Wait();
            try
            {
                if (Interlocked.Read(ref _libraryPersistedVersion) < libraryVersion &&
                    WriteFileSafely(_libraryFile, librarySnapshot))
                {
                    Interlocked.Exchange(ref _libraryPersistedVersion, libraryVersion);
                }
            }
            finally
            {
                _libraryWriteGate.Release();
            }
        }

        if (_sessionFile is not null &&
            sessionSnapshot is not null &&
            Interlocked.Read(ref _sessionPersistedVersion) < sessionVersion)
        {
            _sessionWriteGate.Wait();
            try
            {
                if (Interlocked.Read(ref _sessionPersistedVersion) < sessionVersion &&
                    WriteFileSafely(_sessionFile, sessionSnapshot))
                {
                    Interlocked.Exchange(ref _sessionPersistedVersion, sessionVersion);
                    lock (_persistenceGate)
                    {
                        if (sessionVersion == Interlocked.Read(ref _sessionWriteVersion) &&
                            ReferenceEquals(_pendingSessionSnapshot, sessionSnapshot))
                        {
                            _pendingSessionSnapshot = null;
                        }
                    }
                }
            }
            finally
            {
                _sessionWriteGate.Release();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            return;
        }

        FlushPendingWrites();
        Volatile.Write(ref _disposeFlushCompleted, 1);

        Task[] activeTasks;
        lock (_persistenceGate)
        {
            activeTasks = _activePersistenceTasks.ToArray();
        }

        if (activeTasks.Length == 0)
        {
            DisposePersistenceGates();
        }
        else
        {
            _ = Task.WhenAll(activeTasks).ContinueWith(
                static (_, state) => ((BrowserDataStore)state!).DisposePersistenceGates(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        GC.SuppressFinalize(this);
    }

    public void AddHistory(Uri? uri, string? title, bool refreshVisitTimestamp = true)
    {
        if (!IsBrowsableUri(uri))
        {
            return;
        }

        string url = uri!.AbsoluteUri;
        string resolvedTitle = string.IsNullOrWhiteSpace(title) ? uri.Host : title.Trim();

        DateTimeOffset visitedAt = DateTimeOffset.UtcNow;
        if (Library.History.Count > 0 &&
            Library.History[0].Url.Equals(url, StringComparison.OrdinalIgnoreCase))
        {
            HistoryEntry current = Library.History[0];
            bool titleChanged = !current.Title.Equals(resolvedTitle, StringComparison.Ordinal);
            if (!titleChanged && !refreshVisitTimestamp)
            {
                // Duplicate title notifications are common while a page commits.
                return;
            }

            if (titleChanged)
            {
                current.Title = resolvedTitle;
            }
            if (refreshVisitTimestamp)
            {
                current.VisitedAt = visitedAt;
            }
            SaveLibrary();
            return;
        }

        Library.History.RemoveAll(entry =>
            entry.Url.Equals(url, StringComparison.OrdinalIgnoreCase));

        Library.History.Insert(0, new HistoryEntry
        {
            Url = url,
            Title = resolvedTitle,
            VisitedAt = visitedAt
        });

        if (Library.History.Count > MaximumHistoryEntries)
        {
            Library.History.RemoveRange(
                MaximumHistoryEntries,
                Library.History.Count - MaximumHistoryEntries);
        }

        SaveLibrary();
    }

    public bool IsBookmarked(Uri? uri)
    {
        if (!IsBrowsableUri(uri))
        {
            return false;
        }

        string url = uri!.AbsoluteUri;
        return Library.Bookmarks.Any(entry =>
            entry.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
    }

    public bool ToggleBookmark(Uri? uri, string? title)
    {
        if (!IsBrowsableUri(uri))
        {
            return false;
        }

        string url = uri!.AbsoluteUri;
        BookmarkEntry? existing = Library.Bookmarks.FirstOrDefault(entry =>
            entry.Url.Equals(url, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            Library.Bookmarks.Remove(existing);
            SaveLibrary();
            return false;
        }

        Library.Bookmarks.Insert(0, new BookmarkEntry
        {
            Url = url,
            Title = string.IsNullOrWhiteSpace(title) ? uri.Host : title.Trim(),
            AddedAt = DateTimeOffset.UtcNow
        });

        SaveLibrary();
        return true;
    }

    public BookmarkEntry AddBookmark(string url, string? title, string? icon = null)
    {
        Uri uri = NormalizeWebUri(url);
        BookmarkEntry bookmark = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Url = uri.AbsoluteUri,
            Title = NormalizeTitle(title, uri.Host),
            Icon = NormalizeIcon(icon),
            AddedAt = DateTimeOffset.UtcNow
        };

        Library.Bookmarks.Add(bookmark);
        SaveLibrary();
        return bookmark;
    }

    public bool UpdateBookmark(string id, string url, string? title, string? icon)
    {
        BookmarkEntry? bookmark = Library.Bookmarks.FirstOrDefault(entry =>
            entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (bookmark is null)
        {
            return false;
        }

        Uri uri = NormalizeWebUri(url);
        bookmark.Url = uri.AbsoluteUri;
        bookmark.Title = NormalizeTitle(title, uri.Host);
        bookmark.Icon = NormalizeIcon(icon);
        SaveLibrary();
        return true;
    }

    public void RemoveBookmark(string url)
    {
        Library.Bookmarks.RemoveAll(entry =>
            entry.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
        SaveLibrary();
    }

    public bool RemoveBookmarkById(string id)
    {
        int removed = Library.Bookmarks.RemoveAll(entry =>
            entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            SaveLibrary();
            return true;
        }

        return false;
    }

    public bool MoveBookmark(string id, string? beforeId)
    {
        BookmarkEntry? bookmark = Library.Bookmarks.FirstOrDefault(entry =>
            entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (bookmark is null)
        {
            return false;
        }

        Library.Bookmarks.Remove(bookmark);
        int destination = string.IsNullOrWhiteSpace(beforeId)
            ? Library.Bookmarks.Count
            : Library.Bookmarks.FindIndex(entry =>
                entry.Id.Equals(beforeId, StringComparison.OrdinalIgnoreCase));
        if (destination < 0)
        {
            destination = Library.Bookmarks.Count;
        }

        Library.Bookmarks.Insert(destination, bookmark);
        SaveLibrary();
        return true;
    }

    public QuickLinkEntry UpsertQuickLink(
        string? id,
        string url,
        string? title,
        string? icon)
    {
        Uri uri = NormalizeWebUri(url);
        QuickLinkEntry? link = string.IsNullOrWhiteSpace(id)
            ? null
            : Library.QuickLinks.FirstOrDefault(entry =>
                entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        if (link is null)
        {
            link = new QuickLinkEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                AddedAt = DateTimeOffset.UtcNow
            };
            Library.QuickLinks.Add(link);
        }

        link.Url = uri.AbsoluteUri;
        link.Title = NormalizeTitle(title, uri.Host);
        link.Icon = NormalizeIcon(icon);

        if (Library.QuickLinks.Count > 24)
        {
            Library.QuickLinks.RemoveRange(24, Library.QuickLinks.Count - 24);
        }

        SaveLibrary();
        return link;
    }

    public bool RemoveQuickLink(string id)
    {
        int removed = Library.QuickLinks.RemoveAll(entry =>
            entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            SaveLibrary();
            return true;
        }

        return false;
    }

    public void ReorderQuickLinks(IEnumerable<string> orderedIds)
    {
        Dictionary<string, QuickLinkEntry> byId = Library.QuickLinks
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        List<QuickLinkEntry> reordered = [];
        foreach (string id in orderedIds.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            if (byId.Remove(id, out QuickLinkEntry? entry))
            {
                reordered.Add(entry);
            }
        }

        reordered.AddRange(Library.QuickLinks.Where(entry => byId.Remove(entry.Id)));
        Library.QuickLinks = reordered;
        SaveLibrary();
    }

    public void RemoveHistoryEntry(string url)
    {
        Library.History.RemoveAll(entry =>
            entry.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
        SaveLibrary();
    }

    public void ClearHistory()
    {
        Library.History.Clear();
        SaveLibrary();
    }

    public void ClearHistorySince(DateTimeOffset start)
    {
        Library.History.RemoveAll(entry => entry.VisitedAt >= start);
        SaveLibrary();
    }

    public int ImportBookmarks(IEnumerable<BookmarkEntry> entries)
    {
        int imported = 0;
        HashSet<string> existing = Library.Bookmarks
            .Select(entry => entry.Url)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (BookmarkEntry entry in entries)
        {
            if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out Uri? uri) ||
                !IsBrowsableUri(uri) ||
                !existing.Add(uri.AbsoluteUri))
            {
                continue;
            }

            Library.Bookmarks.Add(new BookmarkEntry
            {
                Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id,
                Url = uri.AbsoluteUri,
                Title = NormalizeTitle(entry.Title, uri.Host),
                Icon = NormalizeIcon(entry.Icon),
                AddedAt = entry.AddedAt == default ? DateTimeOffset.UtcNow : entry.AddedAt
            });
            imported++;
        }

        if (imported > 0)
        {
            SaveLibrary();
        }

        return imported;
    }

    public int ImportHistory(IEnumerable<HistoryEntry> entries)
    {
        int before = Library.History.Count;
        Dictionary<string, HistoryEntry> merged = Library.History
            .Where(entry => Uri.TryCreate(entry.Url, UriKind.Absolute, out Uri? uri) && IsBrowsableUri(uri))
            .GroupBy(entry => entry.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(entry => entry.VisitedAt).First(),
                StringComparer.OrdinalIgnoreCase);

        foreach (HistoryEntry entry in entries)
        {
            if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out Uri? uri) || !IsBrowsableUri(uri))
            {
                continue;
            }

            HistoryEntry normalized = new()
            {
                Url = uri.AbsoluteUri,
                Title = string.IsNullOrWhiteSpace(entry.Title) ? uri.Host : entry.Title.Trim(),
                VisitedAt = entry.VisitedAt == default ? DateTimeOffset.UtcNow : entry.VisitedAt
            };

            if (!merged.TryGetValue(normalized.Url, out HistoryEntry? existing) ||
                existing.VisitedAt < normalized.VisitedAt)
            {
                merged[normalized.Url] = normalized;
            }
        }

        Library.History = merged.Values
            .OrderByDescending(entry => entry.VisitedAt)
            .Take(MaximumHistoryEntries)
            .ToList();
        SaveLibrary();
        return Math.Max(0, Library.History.Count - before);
    }

    public void SaveLibraryData() => SaveLibrary();

    public WorkspaceEntry SaveWorkspace(string name, IEnumerable<WorkspaceTabEntry> tabs)
    {
        string resolvedName = string.IsNullOrWhiteSpace(name)
            ? $"Workspace {Library.Workspaces.Count + 1}"
            : name.Trim();

        WorkspaceEntry workspace = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = resolvedName.Length > 60 ? resolvedName[..60] : resolvedName,
            CreatedAt = DateTimeOffset.UtcNow,
            Tabs = tabs.ToList()
        };

        Library.Workspaces.Insert(0, workspace);
        if (Library.Workspaces.Count > 30)
        {
            Library.Workspaces.RemoveRange(30, Library.Workspaces.Count - 30);
        }

        SaveLibrary();
        return workspace;
    }

    public void RemoveWorkspace(string id)
    {
        Library.Workspaces.RemoveAll(workspace =>
            workspace.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        SaveLibrary();
    }

    private bool RemoveLegacyDefaultQuickLinksOnce()
    {
        if (Library.LegacyDefaultQuickLinksRemoved)
        {
            return false;
        }

        Library.LegacyDefaultQuickLinksRemoved = true;
        Library.QuickLinks.RemoveAll(link =>
        {
            string title = link.Title?.Trim() ?? string.Empty;
            if (!Uri.TryCreate(link.Url, UriKind.Absolute, out Uri? uri))
            {
                return false;
            }

            string host = uri.Host.TrimStart().TrimEnd('.');
            return
                host.Equals("opstan.org", StringComparison.OrdinalIgnoreCase) &&
                    title.Equals("Opstan", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase) &&
                    title.Equals("YouTube", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) &&
                    title.Equals("YouTube", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                    title.Equals("GitHub", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("mail.google.com", StringComparison.OrdinalIgnoreCase) &&
                    (title.Equals("Gmail", StringComparison.OrdinalIgnoreCase) ||
                     title.Equals("Google", StringComparison.OrdinalIgnoreCase));
        });
        return true;
    }

    private void NormalizeLibrary()
    {
        Library.History ??= [];
        Library.Bookmarks ??= [];
        Library.Workspaces ??= [];
        Library.QuickLinks ??= [];

        HashSet<string> bookmarkIds = new(StringComparer.OrdinalIgnoreCase);
        Library.Bookmarks = Library.Bookmarks
            .Where(bookmark => Uri.TryCreate(bookmark.Url, UriKind.Absolute, out Uri? uri) && IsBrowsableUri(uri))
            .Select(bookmark =>
            {
                Uri uri = new(bookmark.Url);
                string id = bookmark.Id?.Trim() ?? string.Empty;
                if (id.Length == 0 || !bookmarkIds.Add(id))
                {
                    do
                    {
                        id = Guid.NewGuid().ToString("N");
                    }
                    while (!bookmarkIds.Add(id));
                }

                bookmark.Id = id;
                bookmark.Url = uri.AbsoluteUri;
                bookmark.Title = NormalizeTitle(bookmark.Title, uri.Host);
                bookmark.Icon = NormalizeIcon(bookmark.Icon);
                bookmark.AddedAt = bookmark.AddedAt == default
                    ? DateTimeOffset.UtcNow
                    : bookmark.AddedAt;
                return bookmark;
            })
            .ToList();

        HashSet<string> quickLinkIds = new(StringComparer.OrdinalIgnoreCase);
        Library.QuickLinks = Library.QuickLinks
            .Where(link => Uri.TryCreate(link.Url, UriKind.Absolute, out Uri? uri) && IsBrowsableUri(uri))
            .Select(link =>
            {
                Uri uri = new(link.Url);
                string id = link.Id?.Trim() ?? string.Empty;
                if (id.Length == 0 || !quickLinkIds.Add(id))
                {
                    do
                    {
                        id = Guid.NewGuid().ToString("N");
                    }
                    while (!quickLinkIds.Add(id));
                }

                link.Id = id;
                link.Url = uri.AbsoluteUri;
                link.Title = NormalizeTitle(link.Title, uri.Host);
                link.Icon = NormalizeIcon(link.Icon);
                link.AddedAt = link.AddedAt == default
                    ? DateTimeOffset.UtcNow
                    : link.AddedAt;
                return link;
            })
            .Take(24)
            .ToList();

        SaveLibrary();
    }

    private void SaveLibrary()
    {
        // Consumers such as the omnibox can invalidate their compact indexes in
        // O(1) instead of hashing or re-reading the full library on every key.
        Interlocked.Increment(ref _libraryRevision);
        if (!_persistToDisk || _libraryFile is null ||
            Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        // Copy only the small in-memory model on the caller thread. JSON encoding,
        // temporary-file I/O and atomic replacement happen later on a worker thread.
        BrowserLibraryData snapshot = CreateLibrarySnapshot();
        long version = Interlocked.Increment(ref _libraryWriteVersion);
        CancellationTokenSource cancellation = new();
        CancellationTokenSource? previous;
        Task persistenceTask;

        lock (_persistenceGate)
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                cancellation.Dispose();
                return;
            }

            previous = _libraryWriteCancellation;
            _libraryWriteCancellation = cancellation;
            persistenceTask = PersistLibraryAfterDelayAsync(snapshot, version, cancellation);
            _activePersistenceTasks.Add(persistenceTask);
        }

        TrackPersistenceCompletion(persistenceTask);
        TryCancel(previous);
    }

    private void TrackPersistenceCompletion(Task persistenceTask)
    {
        _ = persistenceTask.ContinueWith(
            static (completedTask, state) =>
                ((BrowserDataStore)state!).OnPersistenceTaskCompleted(completedTask),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnPersistenceTaskCompleted(Task persistenceTask)
    {
        bool disposeGates;
        lock (_persistenceGate)
        {
            _activePersistenceTasks.Remove(persistenceTask);
            disposeGates = Volatile.Read(ref _disposeState) != 0 &&
                Volatile.Read(ref _disposeFlushCompleted) != 0 &&
                _activePersistenceTasks.Count == 0;
        }

        if (disposeGates)
        {
            DisposePersistenceGates();
        }
    }

    private void DisposePersistenceGates()
    {
        if (Interlocked.Exchange(ref _persistenceGatesDisposed, 1) != 0)
        {
            return;
        }

        _libraryWriteGate.Dispose();
        _sessionWriteGate.Dispose();
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The superseded writer completed between snapshot replacement and cancellation.
        }
    }

    private async Task PersistLibraryAfterDelayAsync(
        BrowserLibraryData snapshot,
        long version,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(LibraryWriteDelay, cancellation.Token).ConfigureAwait(false);
            if (version != Interlocked.Read(ref _libraryWriteVersion) || _libraryFile is null)
            {
                return;
            }

            await _libraryWriteGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                if (version == Interlocked.Read(ref _libraryWriteVersion))
                {
                    await _jsonStore.WriteAsync(_libraryFile, snapshot, cancellation.Token).ConfigureAwait(false);
                    Interlocked.Exchange(ref _libraryPersistedVersion, version);
                }
            }
            finally
            {
                _libraryWriteGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer library mutation superseded this write.
        }
        catch
        {
            // Browser data persistence must never interrupt browsing.
        }
        finally
        {
            lock (_persistenceGate)
            {
                if (ReferenceEquals(_libraryWriteCancellation, cancellation))
                {
                    _libraryWriteCancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private async Task PersistSessionAfterDelayAsync(
        BrowserSessionData snapshot,
        long version,
        CancellationTokenSource cancellation)
    {
        bool writeSucceeded = false;
        try
        {
            await Task.Delay(SessionWriteDelay, cancellation.Token).ConfigureAwait(false);
            if (version != Interlocked.Read(ref _sessionWriteVersion) || _sessionFile is null)
            {
                return;
            }

            await _sessionWriteGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                if (version == Interlocked.Read(ref _sessionWriteVersion))
                {
                    await _jsonStore.WriteAsync(_sessionFile, snapshot, cancellation.Token).ConfigureAwait(false);
                    Interlocked.Exchange(ref _sessionPersistedVersion, version);
                    writeSucceeded = true;
                }
            }
            finally
            {
                _sessionWriteGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer tab/session state superseded this write.
        }
        catch
        {
            // Session persistence must never interrupt browsing.
        }
        finally
        {
            lock (_persistenceGate)
            {
                if (ReferenceEquals(_sessionWriteCancellation, cancellation))
                {
                    _sessionWriteCancellation = null;
                    if (writeSucceeded &&
                        version == Interlocked.Read(ref _sessionWriteVersion))
                    {
                        _pendingSessionSnapshot = null;
                    }
                }
            }

            cancellation.Dispose();
        }
    }

    private BrowserLibraryData CreateLibrarySnapshot() => new()
    {
        // Snapshot every entry. Background JSON serialization must never observe
        // mutable objects that the UI can edit after SaveLibrary returns.
        History = Library.History.Select(static entry => new HistoryEntry
        {
            Url = entry.Url,
            Title = entry.Title,
            VisitedAt = entry.VisitedAt
        }).ToList(),
        Bookmarks = Library.Bookmarks.Select(static entry => new BookmarkEntry
        {
            Id = entry.Id,
            Url = entry.Url,
            Title = entry.Title,
            Icon = entry.Icon,
            AddedAt = entry.AddedAt
        }).ToList(),
        Workspaces = Library.Workspaces.Select(static workspace => new WorkspaceEntry
        {
            Id = workspace.Id,
            Name = workspace.Name,
            CreatedAt = workspace.CreatedAt,
            Tabs = workspace.Tabs.Select(static tab => new WorkspaceTabEntry
            {
                Url = tab.Url,
                IsStartPage = tab.IsStartPage,
                IsPinned = tab.IsPinned
            }).ToList()
        }).ToList(),
        QuickLinks = Library.QuickLinks.Select(static entry => new QuickLinkEntry
        {
            Id = entry.Id,
            Url = entry.Url,
            Title = entry.Title,
            Icon = entry.Icon,
            AddedAt = entry.AddedAt
        }).ToList(),
        LegacyDefaultQuickLinksRemoved = Library.LegacyDefaultQuickLinksRemoved
    };

    private static BrowserSessionData CloneSession(BrowserSessionData session) => new()
    {
        ActiveTabIndex = session.ActiveTabIndex,
        SplitLeftTabIndex = session.SplitLeftTabIndex,
        SplitRightTabIndex = session.SplitRightTabIndex,
        SplitRatio = session.SplitRatio,
        Tabs = session.Tabs.Select(static tab => new SessionTabEntry
        {
            Url = tab.Url,
            IsStartPage = tab.IsStartPage,
            IsPinned = tab.IsPinned,
            GroupId = tab.GroupId
        }).ToList(),
        TabGroups = session.TabGroups.Select(static group => new SessionTabGroupEntry
        {
            Id = group.Id,
            Name = group.Name,
            Color = group.Color,
            IsCollapsed = group.IsCollapsed,
            IsPinned = group.IsPinned
        }).ToList()
    };

    private bool WriteFileSafely<T>(string path, T value)
    {
        try
        {
            _jsonStore.Write(path, value);
            return true;
        }
        catch
        {
            // Browser data persistence must never prevent Aero from closing.
            return false;
        }
    }

    private static Uri NormalizeWebUri(string rawUrl)
    {
        string candidate = rawUrl?.Trim() ?? string.Empty;
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "https://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) || !IsBrowsableUri(uri))
        {
            throw new ArgumentException("Enter a valid HTTP or HTTPS address.", nameof(rawUrl));
        }

        return uri;
    }

    private static string NormalizeTitle(string? title, string fallback)
    {
        string value = string.IsNullOrWhiteSpace(title) ? fallback : title.Trim();
        return value.Length > 80 ? value[..80] : value;
    }

    private static string NormalizeIcon(string? icon)
    {
        string value = icon?.Trim() ?? string.Empty;
        return value.Length > 500 ? value[..500] : value;
    }

    private static bool IsBrowsableUri(Uri? uri) =>
        uri is not null &&
        uri.IsAbsoluteUri &&
        (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
}

public sealed class BrowserLibraryData
{
    public List<HistoryEntry> History { get; set; } = [];

    public List<BookmarkEntry> Bookmarks { get; set; } = [];

    public List<WorkspaceEntry> Workspaces { get; set; } = [];

    public List<QuickLinkEntry> QuickLinks { get; set; } = [];

    public bool LegacyDefaultQuickLinksRemoved { get; set; }
}

public sealed class WorkspaceEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Workspace";

    public DateTimeOffset CreatedAt { get; set; }

    public List<WorkspaceTabEntry> Tabs { get; set; } = [];
}

public sealed class WorkspaceTabEntry
{
    public string? Url { get; set; }

    public bool IsStartPage { get; set; }

    public bool IsPinned { get; set; }
}

public sealed class HistoryEntry
{
    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTimeOffset VisitedAt { get; set; }
}

public sealed class BookmarkEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Icon { get; set; } = string.Empty;

    public DateTimeOffset AddedAt { get; set; }
}

public sealed class QuickLinkEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Icon { get; set; } = string.Empty;

    public DateTimeOffset AddedAt { get; set; }
}

public sealed class BrowserSessionData
{
    public List<SessionTabEntry> Tabs { get; set; } = [];

    public List<SessionTabGroupEntry> TabGroups { get; set; } = [];

    public int ActiveTabIndex { get; set; }

    public int? SplitLeftTabIndex { get; set; }

    public int? SplitRightTabIndex { get; set; }

    public double SplitRatio { get; set; } = 0.5;
}

public sealed class SessionTabEntry
{
    public string? Url { get; set; }

    public bool IsStartPage { get; set; }

    public bool IsPinned { get; set; }

    public string? GroupId { get; set; }

    public string? ContainerId { get; set; }
}

public sealed class SessionTabGroupEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New group";

    public string Color { get; set; } = "Blue";

    public bool IsCollapsed { get; set; }

    public bool IsPinned { get; set; }
}
