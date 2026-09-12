namespace Namotion.Interceptor.Hosting;

internal enum HostedServiceGateState
{
    NotStarted,
    Running,
    Draining,
    Drained
}

/// <summary>
/// Startup and shutdown gate for hosted service transitions. The state only ever moves forward:
/// NotStarted to Running to Draining to Drained, or NotStarted straight to Draining when a host is
/// stopped without having started.
/// </summary>
internal sealed class HostedServiceGate
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _draining = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HostedServiceGateState _state = HostedServiceGateState.NotStarted;

    public HostedServiceGateState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Advances NotStarted to Running. A one way ratchet: calling this during shutdown must not
    /// reopen the gate, or a detach arriving mid drain would let queued starts run again.
    /// </summary>
    public void EnsureStarted()
    {
        var opened = false;
        lock (_sync)
        {
            if (_state == HostedServiceGateState.NotStarted)
            {
                _state = HostedServiceGateState.Running;
                opened = true;
            }
        }

        if (opened)
        {
            _opened.TrySetResult();
        }
    }

    public void BeginDraining()
    {
        lock (_sync)
        {
            if (_state is HostedServiceGateState.NotStarted or HostedServiceGateState.Running)
            {
                _state = HostedServiceGateState.Draining;
            }
        }

        // Releases anything parked on a gate that was never opened, so a host that aborts startup
        // does not leave transitions and their awaiters hanging forever.
        _opened.TrySetResult();
        _draining.TrySetResult();
    }

    public void CompleteDraining()
    {
        lock (_sync)
        {
            _state = HostedServiceGateState.Drained;
        }

        _opened.TrySetResult();
    }

    /// <summary>
    /// Completes once the gate has left <see cref="HostedServiceGateState.NotStarted"/>. Callers must
    /// then read <see cref="State"/> and decide what to do; the wait itself carries no verdict.
    /// </summary>
    public Task WaitForOpenAsync() => _opened.Task;

    /// <summary>
    /// Completes once the drain has begun. Lets a transition parked on something a caller controls,
    /// which is a startup scope and nothing else, be released by shutdown instead of holding the
    /// drain's barrier for the whole shutdown deadline. Continuations run asynchronously, so what was
    /// parked cannot resume on the thread that begins the drain.
    /// </summary>
    public Task WaitForDrainingAsync() => _draining.Task;
}
