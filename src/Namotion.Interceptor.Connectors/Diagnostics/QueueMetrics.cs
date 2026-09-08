namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// Write side of one buffer's diagnostics. Owned by the connector for its whole lifetime, while the
/// buffer it describes may be created and destroyed many times.
/// </summary>
/// <remarks>
/// All state lives in a single immutable snapshot replaced under a writer lock, so registration
/// handover, resets and drop reports cannot overwrite one another while reads stay lock-free.
/// </remarks>
public sealed class QueueMetrics
{
    private sealed record Snapshot(long Accumulated, long Epoch, Registration? Active, int? Capacity);

    private readonly string _name;

    // Writers serialize on this lock; readers take the immutable snapshot without locking, so no
    // getter can throw or block. Mutations are rare (per registration or drop batch, not per item).
    private readonly Lock _snapshotLock = new();

    private Snapshot _snapshot = new(0, 0, null, null);

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueMetrics"/> class.
    /// </summary>
    /// <param name="name">
    /// Which buffer this instance describes, for example <c>OutboundChanges</c>, used in the
    /// registration failure message. The instances exposed by <see cref="ConnectorMetrics"/> and
    /// <see cref="SourceMetrics"/> are already named; pass one only when constructing directly.
    /// </param>
    public QueueMetrics(string name = "queue")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _name = name;
    }

    /// <summary>
    /// Points this instance at a newly created buffer and returns the handle that owns the
    /// registration. Only one registration may be live at a time. Disposing the handle releases only
    /// that registration, and disposing it more than once has no further effect.
    /// </summary>
    /// <remarks>
    /// The depth delegate may not throw (a throwing delegate is treated as reporting zero) and may not
    /// take a lock owned by this library, because a diagnostics read can happen while a monitor holds
    /// its own lock. A buffer must report every drop through <see cref="AddDropped(long)"/>. The metrics own
    /// that count so a drop racing registration release cannot be lost or make the total decrease.
    /// Lifetime-long providers may intentionally leave the returned handle undisposed when their
    /// lifetime matches this instance.
    /// </remarks>
    /// <param name="depth">Reads the buffer's current item count.</param>
    /// <param name="capacity">The buffer's bound, or <c>null</c> if it is unbounded.</param>
    /// <exception cref="InvalidOperationException">
    /// A registration is already live. Dispose its registration handle first.
    /// </exception>
    public IDisposable Register(Func<int> depth, int? capacity)
    {
        ArgumentNullException.ThrowIfNull(depth);

        var registration = new Registration(this, depth, capacity);
        lock (_snapshotLock)
        {
            var current = _snapshot;
            if (current.Active is not null)
            {
                throw new InvalidOperationException(
                    $"A registration is already live on the '{_name}' queue metrics. Dispose its registration handle before registering again.");
            }

            Volatile.Write(ref _snapshot, current with { Active = registration, Capacity = registration.Capacity });
        }

        return registration;
    }

    /// <summary>
    /// Records drops for a buffer that has no counter of its own.
    /// </summary>
    public void AddDropped(long count)
        => AddDropped(count, epoch: null);

    /// <summary>
    /// Creates a drop reporter bound to the current metrics epoch.
    /// </summary>
    /// <remarks>
    /// Reports arriving after the metrics are reset are ignored, preventing asynchronous work from a
    /// previous connector execution from contributing to the new execution's totals.
    /// </remarks>
    /// <returns>An epoch-bound callback that records a positive number of dropped items.</returns>
    public Action<long> CreateDropReporter()
    {
        var epoch = Volatile.Read(ref _snapshot).Epoch;
        return count => AddDropped(count, epoch);
    }

    private void AddDropped(long count, long? epoch)
    {
        if (count <= 0)
        {
            return;
        }

        lock (_snapshotLock)
        {
            var current = _snapshot;
            if (epoch is not null && current.Epoch != epoch)
            {
                return;
            }

            Volatile.Write(ref _snapshot, current with { Accumulated = current.Accumulated + count });
        }
    }

    internal void Reset()
    {
        lock (_snapshotLock)
        {
            var current = _snapshot;
            Volatile.Write(ref _snapshot, current with { Accumulated = 0, Epoch = current.Epoch + 1 });
        }
    }

    internal int Depth => SafeInvokeDepth(Volatile.Read(ref _snapshot).Active?.Depth);

    internal int? Capacity => Volatile.Read(ref _snapshot).Capacity;

    internal long TotalDropped => Volatile.Read(ref _snapshot).Accumulated;

    private static int SafeInvokeDepth(Func<int>? depth)
    {
        if (depth is null)
        {
            return 0;
        }

        try
        {
            return depth();
        }
        catch
        {
            return 0;
        }
    }

    private sealed class Registration(QueueMetrics owner, Func<int> depth, int? capacity) : IDisposable
    {
        private QueueMetrics? _owner = owner;

        internal Func<int> Depth { get; } = depth;

        internal int? Capacity { get; } = capacity;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(this);
    }

    private void Release(Registration registration)
    {
        lock (_snapshotLock)
        {
            var current = _snapshot;
            if (ReferenceEquals(current.Active, registration))
            {
                Volatile.Write(ref _snapshot, current with { Active = null });
            }
        }
    }
}
