namespace Aero.App;

public sealed partial class MainWindow
{
    private readonly Queue<Uri> _pendingExternalUris = new();
    private readonly object _pendingExternalUrisGate = new();
    private readonly SemaphoreSlim _externalUriOpenGate = new(1, 1);

    internal async Task OpenExternalUriAsync(Uri uri)
    {
        if (!IsSupportedExternalUri(uri))
        {
            return;
        }

        // Uri is immutable, but creating a normalized copy makes sure the queued value
        // no longer depends on an activation object owned by the secondary process.
        Uri safeUri = new(uri.AbsoluteUri, UriKind.Absolute);

        if (_environment is null || !_isInitialized)
        {
            EnqueuePendingExternalUri(safeUri);
            return;
        }

        await _externalUriOpenGate.WaitAsync();
        try
        {
            if (_environment is null || !_isInitialized)
            {
                EnqueuePendingExternalUri(safeUri);
                return;
            }

            // Persist the current session before touching WebView2. Even if the runtime
            // itself fails while creating the externally requested tab, existing tabs
            // remain recoverable on the next launch.
            SaveSessionSnapshot();
            await CreateTabAsync(
                safeUri,
                selectTab: true,
                openedFromExternalApplication: true);
            SaveSessionSnapshot();
            Activate();
        }
        catch
        {
            // Never let a protocol activation exception escape through AppInstance's
            // Activated callback. The current Aero window and all tabs stay alive.
        }
        finally
        {
            _externalUriOpenGate.Release();
        }
    }

    private void EnqueuePendingExternalUri(Uri uri)
    {
        lock (_pendingExternalUrisGate)
        {
            // Some applications submit the same protocol activation more than once.
            // Avoid opening a duplicate tab while Aero is still initializing.
            if (_pendingExternalUris.Any(pending =>
                pending.AbsoluteUri.Equals(uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _pendingExternalUris.Enqueue(uri);
        }
    }

    private Uri? DequeuePendingExternalUri()
    {
        lock (_pendingExternalUrisGate)
        {
            return _pendingExternalUris.Count > 0
                ? _pendingExternalUris.Dequeue()
                : null;
        }
    }

    private static bool IsSupportedExternalUri(Uri uri)
    {
        return uri.IsAbsoluteUri &&
            (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }
}
