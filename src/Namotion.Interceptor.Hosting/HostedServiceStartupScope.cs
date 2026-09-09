namespace Namotion.Interceptor.Hosting;

/// <summary>
/// Defers hosted-service starts captured in this scope until it and its enclosing scopes complete.
/// </summary>
/// <remarks>
/// Call <see cref="Complete"/> before disposal to allow startup. Otherwise captured starts are canceled.
/// Do not await a captured service's startup before completing and disposing the scope.
/// Scopes must be disposed in reverse creation order in the creating execution flow.
/// Canceling startup does not detach subjects; normal detach and host shutdown still stop attached services.
/// </remarks>
public sealed class HostedServiceStartupScope : IDisposable
{
    private readonly AsyncLocal<HostedServiceStartupScope?> _current;
    private readonly HostedServiceStartupScope? _parent;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;
    private bool _disposed;

    internal HostedServiceStartupScope(AsyncLocal<HostedServiceStartupScope?> current)
    {
        _current = current;
        _parent = current.Value;
        current.Value = this;
    }

    /// <summary>
    /// Allows captured starts after this scope and all enclosing scopes are successfully disposed.
    /// </summary>
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _completed = true;
    }

    /// <summary>
    /// Releases successful startup or cancels captured starts when <see cref="Complete"/> was not called.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        if (!ReferenceEquals(_current.Value, this))
        {
            throw new InvalidOperationException("Hosted-service startup scopes must be disposed in reverse creation order.");
        }

        _disposed = true;
        _current.Value = _parent;
        _completion.TrySetResult(_completed);
    }

    internal bool IsReady => _completion.Task.IsCompleted &&
        (!_completion.Task.Result || _parent is null || _parent.IsReady);

    internal async Task WaitAsync(CancellationToken cancellationToken)
    {
        if (!await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new OperationCanceledException("Hosted-service initialization did not complete.", cancellationToken);
        }

        if (_parent is not null)
        {
            await _parent.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
