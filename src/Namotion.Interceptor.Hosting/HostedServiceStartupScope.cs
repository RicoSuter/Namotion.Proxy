namespace Namotion.Interceptor.Hosting;

/// <summary>
/// Defers hosted-service starts captured in this scope until it and its enclosing scopes are disposed.
/// </summary>
/// <remarks>
/// Disposal releases captured starts even when configuration throws.
/// Do not await a captured service's startup before disposing the scope.
/// Scopes must be disposed in reverse creation order in the creating execution flow.
/// </remarks>
public sealed class HostedServiceStartupScope : IDisposable
{
    private readonly AsyncLocal<HostedServiceStartupScope?> _current;
    private readonly HostedServiceStartupScope? _parent;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    internal HostedServiceStartupScope(AsyncLocal<HostedServiceStartupScope?> current)
    {
        _current = current;
        _parent = current.Value;
        current.Value = this;
    }

    /// <summary>
    /// Releases the starts captured in this scope, once its enclosing scopes are released too.
    /// </summary>
    public void Dispose()
    {
        // Restore this flow's parent even if another flow already disposed the scope.
        if (ReferenceEquals(_current.Value, this))
        {
            _current.Value = _parent;
        }

        if (_disposed) return;
        _disposed = true;
        _completion.TrySetResult();
    }

    internal bool IsReady => _completion.Task.IsCompleted && (_parent is null || _parent.IsReady);

    internal async Task WaitAsync(CancellationToken cancellationToken)
    {
        await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (_parent is not null)
        {
            await _parent.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
