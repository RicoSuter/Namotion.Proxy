namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// Write side of the diagnostics a source reports on top of <see cref="ConnectorMetrics"/>.
/// </summary>
public class SourceMetrics : ConnectorMetrics
{
    private Func<int>? _claimedPropertyCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceMetrics"/> class.
    /// </summary>
    public SourceMetrics(ThroughputCounter? incoming = null, ThroughputCounter? outgoing = null)
        : base(incoming, outgoing)
    {
    }

    /// <summary>
    /// Gets the metrics of the queue holding outbound writes awaiting retry.
    /// </summary>
    public QueueMetrics OutboundRetries { get; } = new(nameof(OutboundRetries));

    /// <summary>
    /// Gets the metrics of the buffer holding inbound updates while the initial state loads.
    /// </summary>
    public QueueMetrics InboundBuffer { get; } = new(nameof(InboundBuffer));

    /// <summary>
    /// Points the claimed-property gauge at the source's ownership manager. A source that registers
    /// nothing reports an unavailable count.
    /// </summary>
    /// <remarks>
    /// The delegate must return a non-negative count, must not throw (a throwing delegate is sampled
    /// as unavailable) and must not take a lock owned by this library, because a diagnostics read can
    /// happen while a monitor holds its own lock.
    /// </remarks>
    public void RegisterClaimedProperties(Func<int> count)
    {
        ArgumentNullException.ThrowIfNull(count);

        Volatile.Write(ref _claimedPropertyCount, count);
    }

    internal int? ClaimedPropertyCount
    {
        get
        {
            var count = Volatile.Read(ref _claimedPropertyCount);
            if (count is null)
            {
                return null;
            }

            try
            {
                return count();
            }
            catch
            {
                return null;
            }
        }
    }

    private protected override void ResetTotals()
    {
        base.ResetTotals();
        OutboundRetries.Reset();
        InboundBuffer.Reset();
    }
}
