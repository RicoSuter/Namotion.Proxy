using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Resilience;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;

/// <summary>
/// Manages read-after-writes for properties where the server revised SamplingInterval=0 to non-zero.
/// Maintains a NodeId-to-property index for O(1) lookups and handles automatic cleanup.
/// Thread-safe. All state is protected by a single lock for simplicity.
/// </summary>
internal sealed class ReadAfterWriteManager : IAsyncDisposable
{
    private readonly Func<ISession?> _sessionProvider;
    private readonly ISubjectSource _source;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly Lock _lock = new();
    private readonly Timer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly ReadAfterWriteMetrics _metrics;
    private readonly Action<Exception> _reportError;

    // NodeId -> (RevisedInterval, Property) for properties that need read-after-writes
    private readonly Dictionary<NodeId, (TimeSpan RevisedInterval, RegisteredSubjectProperty Property)> _trackedProperties = new();

    // NodeId -> (ReadAt, Property) for pending scheduled reads
    private readonly Dictionary<NodeId, (DateTime ReadAt, RegisteredSubjectProperty Property)> _pendingReads = new();

    // Reusable list for due reads (avoids allocation per timer tick)
    private readonly List<(NodeId NodeId, RegisteredSubjectProperty Property)> _dueReadsList = new();

    private DateTime _earliestReadTime = DateTime.MaxValue;
    private ISession? _lastKnownSession;
    private int _pendingReadCount;
    private int _disposed;
    private int _isProcessing; // 0 = not processing, 1 = processing (for timer callback serialization)

    internal int PendingReadCount => Volatile.Read(ref _pendingReadCount);

    /// <summary>
    /// Creates a new read-after-write manager.
    /// </summary>
    /// <param name="sessionProvider">Function to get current session.</param>
    /// <param name="source">The subject source for applying read values.</param>
    /// <param name="configuration">OPC UA client configuration.</param>
    /// <param name="metrics">The counters to report into.</param>
    /// <param name="reportError">Reports genuine background failures to the owning source.</param>
    /// <param name="logger">Logger instance.</param>
    public ReadAfterWriteManager(
        Func<ISession?> sessionProvider,
        ISubjectSource source,
        OpcUaClientConfiguration configuration,
        ReadAfterWriteMetrics metrics,
        Action<Exception> reportError,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(reportError);

        _sessionProvider = sessionProvider;
        _source = source;
        _configuration = configuration;
        _logger = logger;
        _metrics = metrics;
        _reportError = reportError;
        _circuitBreaker = new CircuitBreaker(
            configuration.PollingCircuitBreakerThreshold,
            configuration.PollingCircuitBreakerCooldown);
        _timer = new Timer(OnTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Registers a property for read-after-writes if needed.
    /// Only tracks properties where requested SamplingInterval=0 but server revised to non-zero.
    /// </summary>
    /// <param name="nodeId">The OPC UA node ID.</param>
    /// <param name="property">The property.</param>
    /// <param name="requestedSamplingInterval">The requested sampling interval (0 = exception-based).</param>
    /// <param name="revisedSamplingInterval">The server's revised sampling interval.</param>
    public void RegisterProperty(
        NodeId nodeId,
        RegisteredSubjectProperty property,
        int? requestedSamplingInterval,
        TimeSpan revisedSamplingInterval)
    {
        // Only track if: requested 0 (exception-based) but server revised to > 0
        if (requestedSamplingInterval != 0 || revisedSamplingInterval <= TimeSpan.Zero)
        {
            return;
        }

        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        lock (_lock)
        {
            _trackedProperties[nodeId] = (revisedSamplingInterval, property);

            _logger.LogDebug(
                "Property {PropertyName} registered for read-after-writes: " +
                "requested SamplingInterval=0, revised to {RevisedInterval}ms.",
                property?.Name ?? nodeId.ToString(), revisedSamplingInterval.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Unregisters a property. Call when a property is released or subject detaches.
    /// Removes from tracking and cancels any pending reads.
    /// </summary>
    /// <param name="nodeId">The OPC UA node ID.</param>
    public void UnregisterProperty(NodeId nodeId)
    {
        lock (_lock)
        {
            _trackedProperties.Remove(nodeId);

            if (_pendingReads.Remove(nodeId))
            {
                UpdatePendingReadCountLocked();
                RecalculateEarliestLocked();
            }
        }
    }

    /// <summary>
    /// Notifies that a property was successfully written. Schedules a read-after-write if needed.
    /// </summary>
    /// <param name="nodeId">The OPC UA node ID that was written.</param>
    public void OnPropertyWritten(NodeId nodeId)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        // Get session outside lock to avoid calling external code while holding lock
        var currentSession = _sessionProvider();
        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            // Check for session change
            if (!ReferenceEquals(_lastKnownSession, currentSession))
            {
                ClearPendingReadsLocked();
                _lastKnownSession = currentSession;
                _circuitBreaker.Reset();

                _logger.LogDebug("Session changed. Cleared pending read-after-writes.");
            }

            // Only schedule if this property needs read-after-writes
            if (!_trackedProperties.TryGetValue(nodeId, out var tracked))
            {
                return;
            }

            var readAt = DateTime.UtcNow + tracked.RevisedInterval + _configuration.ReadAfterWriteBuffer;

            if (_pendingReads.ContainsKey(nodeId))
            {
                _metrics.RecordCoalesced();
            }
            else
            {
                _metrics.RecordScheduled();
            }

            _pendingReads[nodeId] = (readAt, tracked.Property);
            UpdatePendingReadCountLocked();

            // Only reschedule timer if this is earlier than current earliest
            if (readAt < _earliestReadTime)
            {
                _earliestReadTime = readAt;
                RescheduleTimerLocked();
            }
        }
    }

    /// <summary>
    /// Clears all pending reads. Call on session change or reconnection.
    /// Does NOT clear registered properties or revised intervals - those remain valid.
    /// </summary>
    public void ClearPendingReads()
    {
        lock (_lock)
        {
            ClearPendingReadsLocked();
        }
    }

    /// <summary>
    /// Clears all state including tracked properties. Call on full reconnection.
    /// </summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _trackedProperties.Clear();
            ClearPendingReadsLocked();
            _lastKnownSession = null;
        }
    }

    /// <summary>
    /// Clears pending reads and stops the timer. Must be called while holding _lock.
    /// </summary>
    private void ClearPendingReadsLocked()
    {
        _pendingReads.Clear();
        UpdatePendingReadCountLocked();
        _earliestReadTime = DateTime.MaxValue;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private async void OnTimerCallback(object? state)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        // Serialize timer callbacks - if already processing, skip this callback
        // The timer will be rescheduled when processing completes
        if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) == 1)
        {
            return;
        }

        try
        {
            await ProcessDueReadsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportErrorIfRunning(ex);
            _logger.LogError(ex, "Unexpected error in read-after-write timer callback.");
        }
        finally
        {
            Volatile.Write(ref _isProcessing, 0);
        }
    }

    private Task ProcessDueReadsAsync() => ProcessDueReadsAsync(DateTime.UtcNow);

    internal async Task ProcessDueReadsAsync(DateTime utcNow)
    {
        if (!_circuitBreaker.ShouldAttempt())
        {
            _logger.LogDebug("Read-after-write circuit breaker open, skipping.");
            RescheduleTimer();
            return;
        }

        int dueCount;
        lock (_lock)
        {
            _dueReadsList.Clear();

            foreach (var kvp in _pendingReads)
            {
                if (kvp.Value.ReadAt <= utcNow)
                {
                    _dueReadsList.Add((kvp.Key, kvp.Value.Property));
                }
            }

            foreach (var (nodeId, _) in _dueReadsList)
            {
                _pendingReads.Remove(nodeId);
            }

            UpdatePendingReadCountLocked();
            RecalculateEarliestLocked();
            dueCount = _dueReadsList.Count;
        }

        if (dueCount == 0)
        {
            RescheduleTimer();
            return;
        }

        var session = _sessionProvider();
        if (session is null || !session.Connected)
        {
            _logger.LogDebug("Skipping read-after-writes - session not connected.");
            RescheduleTimer();
            return;
        }

        var successCount = 0;
        var failedCount = 0;
        var skippedCount = 0;

        try
        {
            try
            {
                var readValues = new ReadValueIdCollection(dueCount);
                for (var i = 0; i < dueCount; i++)
                {
                    readValues.Add(new ReadValueId
                    {
                        NodeId = _dueReadsList[i].NodeId,
                        AttributeId = Opc.Ua.Attributes.Value
                    });
                }

                ReadResponse response;
                try
                {
                    response = await session.ReadAsync(
                        requestHeader: null,
                        maxAge: 0,
                        timestampsToReturn: TimestampsToReturn.Source,
                        readValues,
                        _cts.Token).ConfigureAwait(false);
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    return;
                }

                var receivedTimestamp = DateTimeOffset.UtcNow;

                for (var i = 0; i < response.Results.Count && i < dueCount; i++)
                {
                    var result = response.Results[i];
                    if (!StatusCode.IsGood(result.StatusCode))
                    {
                        failedCount++;
                        continue;
                    }

                    var (_, property) = _dueReadsList[i];
                    var sourceTimestamp = (DateTimeOffset)result.SourceTimestamp;

                    // Skip if property already has a newer value from subscription notification
                    var currentWriteTimestamp = property.Reference.TryGetWriteTimestamp();
                    if (currentWriteTimestamp.HasValue && currentWriteTimestamp.Value >= sourceTimestamp)
                    {
                        skippedCount++;
                        continue;
                    }

                    var value = _configuration.ValueConverter.ConvertToPropertyValue(result.Value, property);
                    property.SetValueFromSource(_source, sourceTimestamp, receivedTimestamp, value);
                    successCount++;
                }
            }
            catch (Exception ex)
            {
                // Every due read that did not succeed or get skipped failed, including those a
                // thrown batch never processed.
                failedCount = dueCount - successCount - skippedCount;
                _metrics.RecordExecuted(successCount);
                _metrics.RecordFailed(failedCount);
                ReportErrorIfRunning(ex);
                if (_circuitBreaker.RecordFailure())
                {
                    _logger.LogError(ex, "Read-after-write circuit breaker opened after failures.");
                }
                else
                {
                    _logger.LogWarning(ex, "Failed to execute read-after-writes.");
                }

                return;
            }

            // A response can carry fewer results than requested; the unanswered remainder failed.
            failedCount = dueCount - successCount - skippedCount;
            _metrics.RecordExecuted(successCount);
            _metrics.RecordFailed(failedCount);
            _circuitBreaker.RecordSuccess();

            // Logging provider failures are not read failures. Let them propagate to the timer callback's
            // unexpected-error guard without replaying metrics or changing the circuit state.
            _logger.LogDebug(
                "Completed {SuccessCount}/{TotalCount} read-after-writes ({SkippedCount} skipped as stale).",
                successCount, dueCount, skippedCount);
        }
        finally
        {
            RescheduleTimer();
        }
    }

    private void ReportErrorIfRunning(Exception error)
    {
        if (Volatile.Read(ref _disposed) == 0 && !_cts.IsCancellationRequested)
        {
            _reportError(error);
        }
    }

    private void RescheduleTimer()
    {
        lock (_lock)
        {
            RescheduleTimerLocked();
        }
    }

    private void RescheduleTimerLocked()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        if (_earliestReadTime == DateTime.MaxValue)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        var delay = _earliestReadTime - DateTime.UtcNow;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void RecalculateEarliestLocked()
    {
        if (_pendingReads.Count == 0)
        {
            _earliestReadTime = DateTime.MaxValue;
            return;
        }

        var earliest = DateTime.MaxValue;
        foreach (var pending in _pendingReads.Values)
        {
            if (pending.ReadAt < earliest)
            {
                earliest = pending.ReadAt;
            }
        }
        _earliestReadTime = earliest;
    }

    private void UpdatePendingReadCountLocked()
    {
        Volatile.Write(ref _pendingReadCount, _pendingReads.Count);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _logger.LogDebug("Disposing ReadAfterWriteManager. Metrics: {Metrics}", _metrics);

        await _cts.CancelAsync().ConfigureAwait(false);

        lock (_lock)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        await _timer.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();

        lock (_lock)
        {
            _trackedProperties.Clear();
            _pendingReads.Clear();
            UpdatePendingReadCountLocked();
            _earliestReadTime = DateTime.MaxValue;
        }
    }
}
