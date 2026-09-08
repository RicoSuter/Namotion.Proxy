namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Per-connection commit lease that keeps a final inbound commit from landing after the connection
/// it belongs to has been replaced: a commit is admitted only while the lease is live, and retirement
/// waits for every admitted commit to finish before the teardown may continue.
/// </summary>
/// <remarks>
/// Admission and release each take one short, allocation-free lock; the property write and its
/// interceptor or user code run outside that lock. Retirement closes admission and allocates a
/// completion source only when a commit was already admitted.
/// </remarks>
public sealed class ConnectorCommitLease
{
    private readonly Lock _lock = new();

    private TaskCompletionSource? _drained;
    private int _activeCommits;
    private bool _retired;

    /// <summary>
    /// Admits a commit. Returns <c>false</c> once the lease is retired; a <c>true</c> result must be
    /// paired with exactly one <see cref="ReleaseCommit"/>.
    /// </summary>
    public bool TryAcquireCommit()
    {
        lock (_lock)
        {
            if (_retired)
            {
                return false;
            }

            _activeCommits++;
            return true;
        }
    }

    /// <summary>
    /// Releases a commit admitted by <see cref="TryAcquireCommit"/>.
    /// </summary>
    public void ReleaseCommit()
    {
        TaskCompletionSource? drained = null;
        lock (_lock)
        {
            _activeCommits--;
            if (_retired && _activeCommits == 0)
            {
                drained = _drained;
            }
        }

        // Completed outside the lock so continuations cannot run while it is held.
        drained?.TrySetResult();
    }

    /// <summary>
    /// Closes admission and returns a task that completes once every admitted commit has been
    /// released. Completes synchronously when no commit is active.
    /// </summary>
    public Task RetireAsync()
    {
        lock (_lock)
        {
            _retired = true;
            if (_activeCommits == 0)
            {
                return Task.CompletedTask;
            }

            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }
}
