using Namotion.Interceptor.Connectors.Diagnostics;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// What the OPC UA client reports about its session, on top of the shared source diagnostics.
/// </summary>
/// <remarks>
/// A value of <c>true</c> means the client has a live session with its subscriptions set up and
/// <c>false</c> means it is not serving, either because the client reported that or because it has
/// stopped. It reads <c>null</c> only while the client runs before its first liveness report. It
/// stays false for the whole address space browse and subscription creation, which on a large server
/// takes minutes, and drops whenever the session is lost, killed or torn down. True does not mean the model is in sync: while the initial value read runs the
/// source state is
/// <see cref="Namotion.Interceptor.Connectors.Monitoring.SourceState.Synchronizing"/>, so read the
/// two together to tell a network outage from a connected client still loading. See
/// docs/connectors-monitoring.md.
/// </remarks>
public sealed class OpcUaClientDiagnostics : SourceDiagnostics
{
    private readonly OpcUaSubjectClientSource _source;

    internal OpcUaClientDiagnostics(OpcUaSubjectClientSource source, SourceMetrics metrics)
        : base(metrics)
    {
        _source = source;
        Reconnects = new ReconnectDiagnostics(source.ReconnectionMetrics);
    }

    /// <summary>
    /// Gets the session manager every member below reads through, or <c>null</c> once it has been
    /// disposed. The source keeps its field pointing at the manager of the attempt that just ended,
    /// so without this check the members would report a session that is already gone.
    /// </summary>
    private Connection.SessionManager? ActiveSessionManager =>
        _source.SessionManager is { IsDisposed: false } sessionManager ? sessionManager : null;

    private Connection.SessionManager? ActiveSessionManagerWithCurrentSession =>
        ActiveSessionManager is { CurrentSession: not null } sessionManager ? sessionManager : null;

    /// <summary>
    /// Gets a value indicating whether the client is currently attempting to reconnect.
    /// </summary>
    public bool IsReconnecting => ActiveSessionManager?.IsReconnecting ?? false;

    /// <summary>
    /// Gets the current session identifier, or <c>null</c> if there is no session.
    /// </summary>
    public NodeId? SessionId => ActiveSessionManager?.CurrentSession?.SessionId;

    /// <summary>
    /// Gets the number of active OPC UA subscriptions.
    /// </summary>
    public int SubscriptionCount => ActiveSessionManager?.SubscriptionManager.SubscriptionCount ?? 0;

    /// <summary>
    /// Gets the number of monitored items across all subscriptions.
    /// </summary>
    public int MonitoredItemCount => ActiveSessionManager?.SubscriptionManager.MonitoredItemCount ?? 0;

    /// <summary>
    /// Gets the reconnection history.
    /// </summary>
    public ReconnectDiagnostics Reconnects { get; }

    /// <summary>
    /// Gets polling diagnostics, or <c>null</c> when the polling fallback is off, no session has been
    /// set up yet, or the client is between connect attempts.
    /// </summary>
    /// <remarks>
    /// The underlying totals are owned by the source rather than by the session, so they reappear at
    /// their previous values once a session exists again.
    /// </remarks>
    public PollingDiagnostics? Polling => ActiveSessionManagerWithCurrentSession?.PollingDiagnostics;

    /// <summary>
    /// Gets read-after-write diagnostics, or <c>null</c> when read-after-write is off, no session has
    /// been set up yet, or the client is between connect attempts.
    /// </summary>
    /// <remarks>
    /// Its totals survive between attempts in the same way as <see cref="Polling"/>.
    /// </remarks>
    public ReadAfterWriteDiagnostics? ReadAfterWrite => ActiveSessionManagerWithCurrentSession?.ReadAfterWriteDiagnostics;
}

/// <summary>
/// The client's reconnection history. Every counter is monotonic since
/// <see cref="ConnectorDiagnostics.StartTime"/>, while <see cref="LastConnectionTime"/> deliberately
/// survives the epoch reset because it records a past event rather than an accumulated amount.
/// </summary>
public sealed class ReconnectDiagnostics
{
    private readonly ReconnectionMetrics _metrics;

    internal ReconnectDiagnostics(ReconnectionMetrics metrics)
    {
        _metrics = metrics;
    }

    /// <summary>
    /// Gets when the client last established a session, or <c>null</c> if it never has.
    /// </summary>
    public DateTimeOffset? LastConnectionTime => _metrics.LastConnectedAt;

    /// <summary>
    /// Gets the number of reconnection attempts started. Once all in-flight attempts have resolved,
    /// this equals <see cref="TotalSucceeded"/> + <see cref="TotalFailed"/> + <see cref="TotalAbandoned"/>.
    /// </summary>
    public long TotalAttempts => _metrics.TotalAttempts;

    /// <summary>
    /// Gets the number of attempts that produced a usable session.
    /// </summary>
    public long TotalSucceeded => _metrics.Successful;

    /// <summary>
    /// Gets the number of attempts that ended with a genuine fault, which is an exception raised while
    /// the attempt was still live. An exception raised by a teardown counts as
    /// <see cref="TotalAbandoned"/> instead.
    /// </summary>
    public long TotalFailed => _metrics.Failed;

    /// <summary>
    /// Gets the number of attempts that ended without a usable session and without a fault: a null
    /// session, a failed transfer, a preserved session after a server restart, a stall reset, or a
    /// cancellation from a kill or from the listen attempt being torn down, which happens both when the
    /// source stops and when the retry loop ends an attempt.
    /// </summary>
    public long TotalAbandoned => _metrics.Abandoned;
}

/// <summary>
/// The polling fallback used for nodes that do not support subscriptions.
/// </summary>
public sealed class PollingDiagnostics
{
    private readonly Polling.PollingManager _pollingManager;
    private readonly Polling.PollingMetrics _metrics;

    internal PollingDiagnostics(Polling.PollingManager pollingManager, Polling.PollingMetrics metrics)
    {
        _pollingManager = pollingManager;
        _metrics = metrics;
    }

    /// <summary>
    /// Gets the number of items currently being polled.
    /// </summary>
    public int ItemCount => _pollingManager.PollingItemCount;

    /// <summary>
    /// Gets the number of reads that succeeded.
    /// </summary>
    public long TotalSuccessfulReads => _metrics.TotalReads;

    /// <summary>
    /// Gets the number of reads that failed.
    /// </summary>
    public long TotalFailedReads => _metrics.FailedReads;

    /// <summary>
    /// Gets the number of value changes detected.
    /// </summary>
    public long TotalValueChanges => _metrics.ValueChanges;

    /// <summary>
    /// Gets the number of polls whose duration exceeded the polling interval.
    /// </summary>
    public long TotalSlowPolls => _metrics.SlowPolls;

    /// <summary>
    /// Gets the number of times the circuit breaker has tripped.
    /// </summary>
    public long TotalCircuitBreakerTrips => _metrics.CircuitBreakerTrips;

    /// <summary>
    /// Gets whether the circuit breaker is currently open.
    /// </summary>
    public bool IsCircuitBreakerOpen => _pollingManager.IsCircuitOpen;

    /// <summary>
    /// Gets whether the polling loop is currently running.
    /// </summary>
    public bool IsRunning => _pollingManager.IsRunning;
}

/// <summary>
/// The verification reads issued after an outbound write to a discrete property.
/// </summary>
public sealed class ReadAfterWriteDiagnostics
{
    private readonly ReadAfterWrite.ReadAfterWriteManager _manager;
    private readonly ReadAfterWrite.ReadAfterWriteMetrics _metrics;

    internal ReadAfterWriteDiagnostics(
        ReadAfterWrite.ReadAfterWriteManager manager, ReadAfterWrite.ReadAfterWriteMetrics metrics)
    {
        _manager = manager;
        _metrics = metrics;
    }

    /// <summary>
    /// Gets the number of pending verification reads.
    /// </summary>
    public int PendingReads => _manager.PendingReadCount;

    /// <summary>
    /// Gets the number of verification reads scheduled.
    /// </summary>
    public long TotalScheduledReads => _metrics.Scheduled;

    /// <summary>
    /// Gets the number of verification reads executed.
    /// </summary>
    public long TotalExecutedReads => _metrics.Executed;

    /// <summary>
    /// Gets the number of scheduled verification reads replaced by a subsequent write.
    /// </summary>
    public long TotalCoalescedReads => _metrics.Coalesced;

    /// <summary>
    /// Gets the number of verification reads that failed.
    /// </summary>
    public long TotalFailedReads => _metrics.Failed;
}
