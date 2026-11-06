namespace Aero.WebView;

/// <summary>
/// Owns one event-binding set for one WebView/controller pair.
/// Binding is transactional: if an attachment fails, already attached handlers
/// are removed in reverse order before the exception leaves the method.
/// </summary>
public sealed class WebViewEventBinder : IDisposable
{
    private List<Action>? _detachActions;
    private object? _browserIdentity;
    private object? _controllerIdentity;
    private bool _disposed;

    public bool IsBound => _detachActions is not null;

    public void Bind(
        object browserIdentity,
        object controllerIdentity,
        Action<WebViewEventBindingBuilder> configure)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(browserIdentity);
        ArgumentNullException.ThrowIfNull(controllerIdentity);
        ArgumentNullException.ThrowIfNull(configure);

        if (_detachActions is not null)
        {
            string target = ReferenceEquals(_browserIdentity, browserIdentity) &&
                ReferenceEquals(_controllerIdentity, controllerIdentity)
                ? "The WebView event set is already bound to this controller."
                : "Detach the previous WebView event set before binding another controller.";
            throw new InvalidOperationException(target);
        }

        WebViewEventBindingBuilder builder = new();
        try
        {
            configure(builder);
            _detachActions = builder.Commit();
            _browserIdentity = browserIdentity;
            _controllerIdentity = controllerIdentity;
        }
        catch
        {
            builder.Rollback();
            throw;
        }
    }

    public void Detach()
    {
        List<Action>? detachActions = _detachActions;
        _detachActions = null;
        _browserIdentity = null;
        _controllerIdentity = null;

        if (detachActions is null)
        {
            return;
        }

        for (int index = detachActions.Count - 1; index >= 0; index--)
        {
            try
            {
                detachActions[index]();
            }
            catch
            {
                // A crashed or already closed WebView2 controller can release its
                // event sources before Aero gets a chance to detach them.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Detach();
        GC.SuppressFinalize(this);
    }
}

public sealed class WebViewEventBindingBuilder
{
    private readonly List<Action> _detachActions = new(18);
    private bool _completed;

    public void Add(Action attach, Action detach)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        ArgumentNullException.ThrowIfNull(attach);
        ArgumentNullException.ThrowIfNull(detach);

        attach();
        _detachActions.Add(detach);
    }

    internal List<Action> Commit()
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        _completed = true;
        return _detachActions;
    }

    internal void Rollback()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        for (int index = _detachActions.Count - 1; index >= 0; index--)
        {
            try
            {
                _detachActions[index]();
            }
            catch
            {
                // Rollback is best effort. The original attachment exception is
                // the one that must remain visible to the caller.
            }
        }

        _detachActions.Clear();
    }
}
