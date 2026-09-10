using System.Collections.Immutable;

namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// Write side of the diagnostics every connector reports. Created and owned by the connector and
/// never reachable through <see cref="ISubjectConnector"/>, so only the connector itself can move
/// its liveness or record its errors.
/// </summary>
/// <remarks>
/// An epoch opens with liveness unavailable, which diagnostics expose as <c>null</c>, and closes at
/// <c>false</c> when <see cref="MarkStopped"/> runs. A connector that reports liveness refines the
/// value in between, so <c>null</c> means "running and not measured" and never survives a stop.
/// </remarks>
public class ConnectorMetrics
{
    private sealed record Liveness(bool? IsOperational, long ChangeTicks, bool IsStopped);

    // Writers serialize on this lock; readers take an immutable snapshot without locking, so no
    // getter can throw or block. Transitions are rare (per connect/disconnect, not per item).
    private readonly Lock _livenessLock = new();

    private Liveness _liveness = new(null, 0, false);
    private long _startTicks;
    private Exception? _lastError;
    private ImmutableArray<IResettableMetrics> _resettables = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectorMetrics"/> class.
    /// </summary>
    /// <param name="incoming">Counts changes flowing into the subject tree, or <c>null</c> if this connector does not measure that.</param>
    /// <param name="outgoing">Counts changes flowing out of the subject tree, or <c>null</c> if this connector does not measure that.</param>
    public ConnectorMetrics(ThroughputCounter? incoming = null, ThroughputCounter? outgoing = null)
    {
        Incoming = incoming;
        Outgoing = outgoing;
    }

    /// <summary>
    /// Gets the incoming throughput counter, for the connector to feed. <c>null</c> when this
    /// connector does not measure the direction.
    /// </summary>
    public ThroughputCounter? Incoming { get; }

    /// <summary>
    /// Gets the outgoing throughput counter, for the connector to feed. <c>null</c> when this
    /// connector does not measure the direction.
    /// </summary>
    public ThroughputCounter? Outgoing { get; }

    /// <summary>
    /// Gets the metrics of the outbound change queue that carries subject changes to the external system.
    /// </summary>
    public QueueMetrics OutboundChanges { get; } = new(nameof(OutboundChanges));

    /// <summary>
    /// Opens a new counter epoch: stamps a fresh start time, clears the last error, returns liveness
    /// to unavailable, releases the <see cref="MarkStopped"/> latch and resets every <c>Total</c>
    /// counter, including those of registered hoisted metrics.
    /// </summary>
    /// <remarks>
    /// Deliberately not idempotent. Called once per <c>ExecuteAsync</c> entry, so a host stop and
    /// start moves the epoch while a transport reconnect inside the connector's own loop does not.
    /// <see cref="SubjectConnectorBase"/> permits a sequential restart only after its previous
    /// execution completes. A connector driving these methods without that base must enforce the
    /// same non-overlap so an old <see cref="MarkStopped"/> cannot latch a new epoch.
    /// </remarks>
    public void MarkStarted()
    {
        var startTicks = DateTimeOffset.UtcNow.UtcTicks;
        Volatile.Write(ref _lastError, null);
        ResetLiveness();
        ResetTotals();

        foreach (var resettable in _resettables)
        {
            resettable.Reset();
        }

        Interlocked.Exchange(ref _startTicks, startTicks);
    }

    /// <summary>
    /// Enrolls metrics the connector owns outside this object into the <see cref="MarkStarted"/> reset.
    /// </summary>
    /// <remarks>
    /// Register once before the connector starts, never per reconnect: there is no deregistration
    /// counterpart, and a resettable enrolled after <see cref="MarkStarted"/> carries its pre-epoch
    /// counts into the new epoch.
    /// </remarks>
    public void RegisterResettable(IResettableMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        ImmutableInterlocked.Update(ref _resettables, static (current, item) => current.Add(item), metrics);
    }

    /// <summary>
    /// Reports that the connector is now serving. The first liveness report establishes the liveness
    /// change timestamp. Ignored between <see cref="MarkStopped"/> and the next <see cref="MarkStarted"/>.
    /// </summary>
    public void MarkOperational() => SetOperational(true, terminal: false);

    /// <summary>
    /// Reports that the connector is no longer serving but may recover. The first liveness report
    /// establishes the liveness change timestamp. Ignored between <see cref="MarkStopped"/> and the
    /// next <see cref="MarkStarted"/>.
    /// </summary>
    public void MarkNotOperational() => SetOperational(false, terminal: false);

    /// <summary>
    /// Reports that the connector has stopped for good and terminally latches liveness for the rest
    /// of the epoch, until the next <see cref="MarkStarted"/>. Always publishes <c>false</c>, because a
    /// stopped connector is known not to be serving whether or not it measures liveness itself, and
    /// rejects later liveness reports until the next start.
    /// </summary>
    /// <remarks>
    /// Terminal because a liveness transition detected off the pump thread can otherwise land after
    /// the pump has exited and resurrect a stopped connector, as for
    /// <see cref="Monitoring.SourceState.Stopped"/>.
    /// </remarks>
    public void MarkStopped() => SetOperational(false, terminal: true);

    /// <summary>
    /// Records the most recent failure. Sticky: it survives recovery and is cleared only by the next
    /// <see cref="MarkStarted"/>.
    /// </summary>
    public void ReportError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        Volatile.Write(ref _lastError, error);
    }

    internal bool? IsOperational => Volatile.Read(ref _liveness).IsOperational;

    internal DateTimeOffset? OperationalChangeTime
    {
        get
        {
            var ticks = Volatile.Read(ref _liveness).ChangeTicks;
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    internal Exception? LastError => Volatile.Read(ref _lastError);

    internal DateTimeOffset? StartTime
    {
        get
        {
            var ticks = Interlocked.Read(ref _startTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    private protected virtual void ResetTotals() => OutboundChanges.Reset();

    // The value and its timestamp both clear, so a new epoch starts with nothing observed rather than
    // carrying the previous epoch's liveness and a timestamp from before this run began.
    private void ResetLiveness()
    {
        lock (_livenessLock)
        {
            Volatile.Write(ref _liveness, new Liveness(null, 0, false));
        }
    }

    private void SetOperational(bool isOperational, bool terminal)
    {
        lock (_livenessLock)
        {
            var current = _liveness;
            if (current.IsStopped && !terminal)
            {
                return;
            }

            var stopped = current.IsStopped || terminal;
            if (current.IsOperational == isOperational && current.IsStopped == stopped)
            {
                return;
            }

            // The timestamp moves only when the value does, so a repeated report keeps reading as
            // "up since T" or "down since T". It is sampled inside the lock, so it cannot move
            // backwards relative to an already published transition.
            var updated = current.IsOperational == isOperational
                ? current with { IsStopped = stopped }
                : new Liveness(isOperational, DateTimeOffset.UtcNow.UtcTicks, stopped);

            Volatile.Write(ref _liveness, updated);
        }
    }
}
