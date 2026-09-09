namespace Namotion.Interceptor.Hosting;

/// <summary>
/// Defers hosted-service starts captured in this scope until it and its enclosing scopes are disposed.
/// </summary>
/// <remarks>
/// Disposal is the only release, so a <c>using</c> is the whole contract and an exception unwinding
/// through it releases the captured starts like any other exit.
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
        // Ahead of the disposed check, so a scope already released from another execution flow is
        // still cleared from the flow that created it. Skipping it there would pin that flow's
        // current scope to a disposed one for good, and every later attach on it would take the
        // deferred path for a scope nobody can release again.
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
