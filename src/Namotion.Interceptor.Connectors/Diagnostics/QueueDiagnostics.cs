namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// Read-only view over one buffer. No read takes a lock owned by this library and no read throws.
/// </summary>
public sealed class QueueDiagnostics
{
    private readonly QueueMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueDiagnostics"/> class.
    /// </summary>
    public QueueDiagnostics(QueueMetrics metrics)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    /// <summary>
    /// Gets the buffer's current item count, or 0 when no buffer currently exists.
    /// Approximate: it is read while producers and consumers are running.
    /// </summary>
    /// <remarks>
    /// Not free: the change queue's count is a segment walk over a
    /// <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/>, which briefly takes that
    /// queue's own internal lock once the queue spans several segments. Sample this rather than
    /// polling it tightly.
    /// </remarks>
    public int Depth => _metrics.Depth;

    /// <summary>
    /// Gets the buffer's bound: <c>null</c> when it is unbounded, 0 when the buffer is disabled and
    /// was never constructed.
    /// </summary>
    public int? Capacity => _metrics.Capacity;

    /// <summary>
    /// Gets the number of items this buffer has thrown away since the connector's
    /// <c>ConnectorDiagnostics.StartTime</c>. Monotonic within an epoch and never rebased by the
    /// buffer being recreated.
    /// </summary>
    public long TotalDropped => _metrics.TotalDropped;
}
