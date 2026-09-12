using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Manages buffered changes, the active delivery count, and dropped changes for <see cref="ChangeQueueProcessor"/>.
/// </summary>
/// <remarks>
/// The caller serializes delivery cycles. After draining, the caller owns the scratch batch until
/// delivery begins; a merge failure in that window is not counted here.
/// <para>
/// When the write handler owns delivery, the caller bypasses delivery tracking. Only buffered changes
/// and their drops are tracked here; handed-off changes belong to the handler.
/// </para>
/// </remarks>
internal sealed class ChangeQueueState
{
    private const int ClosedDelivery = -1;

    private readonly ConcurrentQueue<SubjectPropertyChange> _changes = new();

    // Closure must observe cancellation requeue and failure accounting atomically.
    // Never held across an await or a consumer callback.
    private readonly Lock _ownershipGate = new();

    private readonly int? _maxQueueDepth;
    private readonly Action<long>? _dropHandler;
    private readonly ILogger _logger;

    private long _dropCount;
    // 0 = idle, positive = active delivery size, ClosedDelivery = permanently closed.
    private int _deliveryState;

    public ChangeQueueState(
        int? maxQueueDepth,
        Action<long>? dropHandler,
        ILogger logger,
        bool tracksDeliveryOutcomes)
    {
        _maxQueueDepth = maxQueueDepth;
        _dropHandler = dropHandler;
        _logger = logger;
        TryBeginDeliveryCallback = tracksDeliveryOutcomes ? TryBeginDeliveryOrCountAsDropped : null;
    }

    /// <summary>
    /// Cached callback that begins a delivery or counts it as dropped; null when the handler owns delivery.
    /// </summary>
    public Func<int, bool>? TryBeginDeliveryCallback { get; }

    /// <summary>Changes dropped by overflow or failure, or left locally unconfirmed at closure.</summary>
    public long DropCount => Interlocked.Read(ref _dropCount);

    /// <summary>Changes buffered right now. Approximate while the pump is running.</summary>
    public int BufferedCount => _changes.Count;

    /// <summary>
    /// Enqueues a change, dropping the oldest buffered changes when the queue bound is exceeded.
    /// Trimming is best effort when a concurrent flush drains the queue.
    /// </summary>
    public void Enqueue(in SubjectPropertyChange change)
    {
        _changes.Enqueue(change);
        if (_maxQueueDepth is int maxQueueDepth && _changes.Count > maxQueueDepth)
        {
            TrimToBound(maxQueueDepth);
        }
    }

    // Split out so the per-change Enqueue stays loop-free and inside the JIT's inline budget.
    private void TrimToBound(int maxQueueDepth)
    {
        var droppedCount = 0L;
        while (_changes.Count > maxQueueDepth && _changes.TryDequeue(out _))
        {
            Interlocked.Increment(ref _dropCount);
            droppedCount++;
        }

        if (droppedCount > 0)
        {
            InvokeDropHandler(droppedCount);
        }
    }

    /// <summary>
    /// Clears <paramref name="buffer"/> and drains queued changes into it. The caller holds the flush gate.
    /// </summary>
    public void DrainBufferedChangesInto(List<SubjectPropertyChange> buffer)
    {
        buffer.Clear();
        while (_changes.TryDequeue(out var change))
        {
            buffer.Add(change);
        }
    }

    private bool TryBeginDelivery(int count) => Interlocked.CompareExchange(ref _deliveryState, count, 0) == 0;

    /// <summary>
    /// Begins a delivery of <paramref name="count"/> changes. If busy or closed, counts them as dropped and returns false.
    /// </summary>
    public bool TryBeginDeliveryOrCountAsDropped(int count)
    {
        var started = TryBeginDelivery(count);
        if (!started)
        {
            CountTerminalDrops(count);
        }

        return started;
    }

    /// <summary>Releases a delivery that the handler completed. A no-op once ownership has closed.</summary>
    public void CompleteDelivery(int count) => Interlocked.CompareExchange(ref _deliveryState, 0, count);

    /// <summary>
    /// Requeues the active delivery without overflow trimming. A no-op once closure has claimed it.
    /// </summary>
    public void RequeueCancelledDelivery(ReadOnlySpan<SubjectPropertyChange> changes, int count)
    {
        lock (_ownershipGate)
        {
            if (Interlocked.CompareExchange(ref _deliveryState, 0, count) != count)
            {
                return;
            }

            foreach (var change in changes)
            {
                _changes.Enqueue(change);
            }
        }
    }

    /// <summary>
    /// Ends the active delivery and reports its changes as dropped. Returns false if it was already settled.
    /// </summary>
    public bool TryCompleteFailedDelivery(int count)
    {
        lock (_ownershipGate)
        {
            if (Interlocked.CompareExchange(ref _deliveryState, 0, count) != count)
            {
                return false;
            }

            Interlocked.Add(ref _dropCount, count);
        }

        InvokeDropHandler(count);
        return true;
    }

    /// <summary>
    /// Permanently prevents new deliveries and counts the active delivery and buffered changes as dropped.
    /// </summary>
    public void CloseAndCountRemainingAsDropped() => CountTerminalDrops(CloseAndDrain());

    private int CloseAndDrain()
    {
        lock (_ownershipGate)
        {
            var count = Math.Max(0, Interlocked.Exchange(ref _deliveryState, ClosedDelivery));
            while (_changes.TryDequeue(out _))
            {
                count++;
            }

            return count;
        }
    }

    private void CountTerminalDrops(int count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _dropCount, count);
        // Consumer callbacks must not block the teardown caller after ownership has been settled.
        _ = Task.Run(() =>
        {
            InvokeDropHandler(count);
            try
            {
                _logger.LogWarning(
                    "Gave up waiting after {Timeout} for {Count} changes to be written while stopping. " +
                    "A write handler may already have completed them remotely or may still complete them.",
                    ChangeQueueProcessor.TeardownFlushBound,
                    count);
            }
            catch
            {
                // Reporting is best effort after ownership has already been settled.
            }
        });
    }

    private void InvokeDropHandler(long count)
    {
        try { _dropHandler?.Invoke(count); } catch { }
    }
}
