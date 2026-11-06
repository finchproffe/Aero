namespace Aero.Search;

public sealed class SearchDebouncer : IDisposable
{
    private readonly TimeSpan _delay;
    private CancellationTokenSource? _pending;
    private bool _disposed;

    public SearchDebouncer(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        _delay = delay;
    }

    public async Task<bool> WaitAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CancellationTokenSource current = new();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _pending, current);
        TryCancel(previous);

        try
        {
            await Task.Delay(_delay, current.Token).ConfigureAwait(true);
            return ReferenceEquals(
                Interlocked.CompareExchange(ref _pending, null, current),
                current);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            Interlocked.CompareExchange(ref _pending, null, current);
            current.Dispose();
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? pending = Interlocked.Exchange(ref _pending, null);
        TryCancel(pending);
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
            // The superseded waiter owns disposal and may finish concurrently.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cancel();
    }
}
