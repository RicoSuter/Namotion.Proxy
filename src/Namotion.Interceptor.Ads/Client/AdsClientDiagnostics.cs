using Namotion.Interceptor.Connectors.Diagnostics;

using TwinCAT.Ads;

namespace Namotion.Interceptor.Ads.Client;

/// <summary>
/// What the ADS client reports about its connection, on top of the shared source diagnostics.
/// </summary>
/// <remarks>
/// <see cref="ConnectorDiagnostics.IsOperational"/> means the client holds an ADS connection and
/// has registered its symbols. It does not mean the model is in sync: the initial value read runs
/// while the source state is
/// <see cref="Namotion.Interceptor.Connectors.Monitoring.SourceState.Synchronizing"/>, so read the
/// two together to tell a lost connection from a connected client still loading. See
/// docs/connectors-monitoring.md.
/// </remarks>
public sealed class AdsClientDiagnostics : SourceDiagnostics
{
    private readonly AdsSubjectClientSource _source;

    internal AdsClientDiagnostics(AdsSubjectClientSource source, SourceMetrics metrics)
        : base(metrics)
    {
        _source = source;
        Polling = new AdsPollingDiagnostics(source.SubscriptionManager.PollingMetrics);
    }

    /// <summary>
    /// Gets the current PLC state.
    /// </summary>
    public AdsState? State => _source.ConnectionManager.CurrentAdsState;

    /// <summary>
    /// Gets whether the ADS client is currently connected.
    /// </summary>
    public bool IsConnected => _source.ConnectionManager.IsConnected;

    /// <summary>
    /// Gets the number of variables using notification mode.
    /// </summary>
    public int NotificationVariableCount => _source.SubscriptionManager.NotificationCount;

    /// <summary>
    /// Gets the number of variables using polling mode.
    /// </summary>
    public int PolledVariableCount => _source.SubscriptionManager.PolledCount;

    /// <summary>
    /// Gets the total reconnection attempts since startup.
    /// </summary>
    public long TotalReconnectionAttempts => _source.ConnectionManager.TotalReconnectionAttempts;

    /// <summary>
    /// Gets the successful reconnections since startup.
    /// </summary>
    public long SuccessfulReconnections => _source.ConnectionManager.SuccessfulReconnections;

    /// <summary>
    /// Gets the failed reconnections since startup.
    /// </summary>
    public long FailedReconnections => _source.ConnectionManager.FailedReconnections;

    /// <summary>
    /// Gets the last successful connection time.
    /// </summary>
    public DateTimeOffset? LastConnectedAt => _source.ConnectionManager.LastConnectedAt;

    /// <summary>
    /// Gets whether the circuit breaker is currently open.
    /// </summary>
    public bool IsCircuitBreakerOpen => _source.ConnectionManager.IsCircuitBreakerOpen;

    /// <summary>
    /// Gets the number of times the circuit breaker has tripped.
    /// </summary>
    public long CircuitBreakerTripCount => _source.ConnectionManager.CircuitBreakerTripCount;

    /// <summary>
    /// Gets what the individual-read poll passes recorded. Zero throughout when the polled symbols
    /// resolve, because a sum read then serves the whole batch in one round trip.
    /// </summary>
    public AdsPollingDiagnostics Polling { get; }
}

/// <summary>
/// What the individual-read polling fallback reports.
/// </summary>
public sealed class AdsPollingDiagnostics
{
    private readonly AdsPollingMetrics _metrics;

    internal AdsPollingDiagnostics(AdsPollingMetrics metrics)
    {
        _metrics = metrics;
    }

    /// <summary>Gets the number of individual-read passes completed.</summary>
    public long TotalPasses => _metrics.Passes;

    /// <summary>Gets the reads that threw, across all passes.</summary>
    public long TotalFailedReads => _metrics.FailedReads;

    /// <summary>Gets how long the last pass took, in milliseconds.</summary>
    public long LastPassDurationMilliseconds => _metrics.LastPassDurationMilliseconds;

    /// <summary>Gets how many symbols the last pass read.</summary>
    public long LastPassSymbolCount => _metrics.LastPassSymbolCount;
}
