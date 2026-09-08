using System.Collections;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Resilience;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client.Polling;

/// <summary>
/// Polling fallback for nodes that don't support subscriptions. Circuit breaker prevents resource exhaustion.
/// Thread-safe. Start() is idempotent. Values reset on reconnection (some data loss during disconnection is expected).
/// </summary>
internal sealed class PollingManager : IAsyncDisposable
{
    private readonly OpcUaSubjectClientSource _source;
    private readonly ILogger _logger;
    private readonly Func<ISession?> _sessionProvider;
    private readonly SubjectPropertyWriter _propertyWriter;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly PollingMetrics _metrics;
    private readonly Action<Exception> _reportError;

    private readonly ConcurrentDictionary<string, PollingItem> _pollingItems = new();
    private readonly Lock _pollingItemsMutationLock = new();
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _startLock = new();

    private Task? _pollingTask;
    private ISession? _lastKnownSession;
    private int _pollingItemCount;
    private int _disposed;

    public PollingManager(OpcUaSubjectClientSource source,
        Func<ISession?> sessionProvider,
        SubjectPropertyWriter propertyWriter,
        OpcUaClientConfiguration configuration,
        PollingMetrics metrics,
        Action<Exception> reportError,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(sessionProvider);
        ArgumentNullException.ThrowIfNull(propertyWriter);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(reportError);

        _source = source;
        _logger = logger;
        _sessionProvider = sessionProvider;
        _propertyWriter = propertyWriter;
        _configuration = configuration;
        _metrics = metrics;
        _reportError = reportError;

        _circuitBreaker = new CircuitBreaker(configuration.PollingCircuitBreakerThreshold, configuration.PollingCircuitBreakerCooldown);
        _timer = new PeriodicTimer(configuration.PollingInterval);
    }

    /// <summary>
    /// Gets the number of items currently being polled.
    /// </summary>
    public int PollingItemCount => Volatile.Read(ref _pollingItemCount);

    /// <summary>
    /// Gets whether the circuit breaker is currently open (polling suspended due to persistent failures).
    /// </summary>
    public bool IsCircuitOpen => _circuitBreaker.IsOpen;

    /// <summary>
    /// Gets whether the polling manager is currently running.
    /// Returns false if disposed or if the polling task has completed/faulted.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            if (Volatile.Read(ref _disposed) == 1)
                return false;

            var task = Volatile.Read(ref _pollingTask);
            return task is not null && !task.IsCompleted;
        }
    }

    /// <summary>
    /// Starts the polling loop in the background.
    /// Idempotent - safe to call multiple times (subsequent calls are ignored).
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if already disposed</exception>
    public void Start()
    {
        lock (_startLock)
        {
            // Check disposal inside lock to prevent race with Dispose()
            if (Volatile.Read(ref _disposed) == 1)
                throw new ObjectDisposedException(nameof(PollingManager));

            if (Volatile.Read(ref _pollingTask) != null)
            {
                _logger.LogDebug("Polling manager already started, ignoring duplicate start request");
                return;
            }

            var task = Task.Run(async () => await PollLoopAsync(_cts.Token));
            Volatile.Write(ref _pollingTask, task); // Ensure task assignment is visible to all threads
        }

        _logger.LogDebug("OPC UA polling manager started with interval {Interval}ms", _configuration.PollingInterval.TotalMilliseconds);
    }

    /// <summary>
    /// Adds a monitored item to polling fallback.
    /// Called when subscription creation fails for this item.
    /// </summary>
    public void AddItem(MonitoredItem monitoredItem)
    {
        var key = monitoredItem.StartNodeId.ToString();

        // Extract RegisteredSubjectProperty from handle
        if (monitoredItem.Handle is not RegisteredSubjectProperty property)
        {
            _logger.LogWarning("Cannot add item {NodeId} to polling - invalid handle", key);
            return;
        }

        var pollingItem = new PollingItem(
            monitoredItem.StartNodeId,
            Property: property,
            LastValue: null
        );

        if (TryAddPollingItem(key, pollingItem))
        {
            _logger.LogInformation("Added node {NodeId} to polling fallback (subscription not supported)", key);
        }
    }

    /// <summary>
    /// Removes polling items for a detached subject. Idempotent.
    /// </summary>
    public void RemoveItemsForSubject(IInterceptorSubject subject)
    {
        foreach (var kvp in _pollingItems)
        {
            if (kvp.Value.Property.Reference.Subject == subject)
            {
                TryRemovePollingItem(kvp.Key);
            }
        }
    }

    /// <summary>
    /// Removes all polled items so a subscription (re)creation re-attempts every owned property;
    /// nodes that fail again are re-added. Prevents double delivery of a recovered escalated item.
    /// </summary>
    public void Clear()
    {
        ClearPollingItems();
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                while (await _timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    await PollItemsAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal shutdown
                return;
            }
            catch (Exception ex)
            {
                ReportErrorIfRunning(ex, cancellationToken);
                _logger.LogError(ex, "Fatal error in polling loop. Restarting after delay...");

                // Wait before restarting to avoid tight failure loop
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task PollItemsAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        if (_pollingItems.IsEmpty)
        {
            return;
        }

        // Check circuit breaker
        if (!_circuitBreaker.ShouldAttempt())
        {
            var remaining = _circuitBreaker.GetCooldownRemaining();
            _logger.LogDebug("Circuit breaker is open, skipping poll (cooldown: {Remaining}s remaining)",
                (int)remaining.TotalSeconds);
            return;
        }

        var session = _sessionProvider();
        if (session is null || !session.Connected)
        {
            _logger.LogDebug("No active session available for polling (null: {IsNull}, connected: {Connected})",
                session is null, session?.Connected);
            Volatile.Write(ref _lastKnownSession, null);
            return;
        }

        // Detect session change (reconnection)
        var lastSession = Volatile.Read(ref _lastKnownSession);
        if (!ReferenceEquals(lastSession, session))
        {
            _logger.LogInformation("Session change detected, resetting polled item values and circuit breaker");
            ResetPolledValues();
            _circuitBreaker.Reset();
        }
        Volatile.Write(ref _lastKnownSession, session);

        var startTime = DateTimeOffset.UtcNow;
        var pollSucceeded = false;
        var pollCancelled = false;

        try
        {
            // Get snapshot of items to poll
            var itemsToRead = _pollingItems.Values.ToArray();

            // Process in batches using direct indexing
            for (int i = 0; i < itemsToRead.Length; i += _configuration.PollingBatchSize)
            {
                var batchSize = Math.Min(_configuration.PollingBatchSize, itemsToRead.Length - i);
                var batch = new ArraySegment<PollingItem>(itemsToRead, i, batchSize);
                await ReadBatchAsync(session, batch, cancellationToken).ConfigureAwait(false);
            }

            pollSucceeded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            pollCancelled = true;
            throw;
        }
        catch (Exception ex)
        {
            ReportErrorIfRunning(ex, cancellationToken);
            _logger.LogError(ex, "Error polling items");
            pollSucceeded = false;
        }
        finally
        {
            // Update circuit breaker
            if (pollSucceeded)
            {
                _circuitBreaker.RecordSuccess();
            }
            else if (!pollCancelled && _circuitBreaker.RecordFailure())
            {
                _metrics.RecordCircuitBreakerTrip();
                _logger.LogError("Circuit breaker opened after consecutive failures. Polling suspended temporarily.");
            }

            // Detect slow polls that exceed the polling interval
            var duration = DateTimeOffset.UtcNow - startTime;
            if (!pollCancelled && duration > _configuration.PollingInterval)
            {
                _metrics.RecordSlowPoll();
                _logger.LogWarning("Slow poll detected: polling took {Duration}ms, which exceeds interval of {Interval}ms. Consider increasing polling interval or batch size.",
                    duration.TotalMilliseconds, _configuration.PollingInterval.TotalMilliseconds);
            }
        }
    }

    private void ResetPolledValues()
    {
        // Clear cached values on session change to force re-notification
        // Take snapshot to avoid TOCTOU issues with concurrent modifications
        // Use TryUpdate to handle concurrent removal safely (matching pattern in ProcessValueChange)
        foreach (var item in _pollingItems.Values.ToArray())
        {
            var key = item.NodeId.ToString();
            _pollingItems.TryUpdate(key, item with { LastValue = null }, item);
            // If TryUpdate fails, item was removed or modified concurrently - skip silently
        }
    }

    private static bool ValuesAreEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;

        // Handle arrays using StructuralComparisons (avoids boxing for primitive arrays)
        if (a is Array arrayA && b is Array arrayB)
        {
            return StructuralComparisons.StructuralEqualityComparer.Equals(arrayA, arrayB);
        }

        return Equals(a, b);
    }

    private async Task ReadBatchAsync(ISession session, ArraySegment<PollingItem> batch, CancellationToken cancellationToken)
    {
        try
        {
            // Build read request - pre-size to avoid resizing
            var nodesToRead = new ReadValueIdCollection(batch.Count);
            foreach (var item in batch)
            {
                nodesToRead.Add(new ReadValueId
                {
                    NodeId = item.NodeId,
                    AttributeId = Opc.Ua.Attributes.Value
                });
            }

            // Execute read
            var response = await session.ReadAsync(
                requestHeader: null,
                maxAge: 0,
                timestampsToReturn: TimestampsToReturn.Both,
                nodesToRead,
                cancellationToken).ConfigureAwait(false);

            // Process results - count metrics per item for accurate monitoring
            for (var i = 0; i < Math.Min(response.Results.Count, batch.Count); i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var dataValue = response.Results[i];
                var pollingItem = batch[i];

                if (StatusCode.IsGood(dataValue.StatusCode))
                {
                    _metrics.RecordRead();
                    ProcessValueChange(pollingItem, dataValue, DateTimeOffset.UtcNow);
                }
                else if (StatusCode.IsBad(dataValue.StatusCode))
                {
                    _metrics.RecordFailedRead();
                    _logger.LogWarning("Polling read failed for {NodeId}: {Status}",
                        pollingItem.NodeId, dataValue.StatusCode);
                }
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex)
        {
            ReportErrorIfRunning(ex, cancellationToken);

            // Batch-level failure - count all items in batch as failed
            for (var i = 0; i < batch.Count; i++)
            {
                _metrics.RecordFailedRead();
            }
            _logger.LogError(ex, "Failed to read batch of {Count} polled items", batch.Count);
        }
    }

    private void ProcessValueChange(PollingItem pollingItem, DataValue dataValue, DateTimeOffset receivedTimestamp)
    {
        var newValue = dataValue.Value;
        var oldValue = pollingItem.LastValue;

        // Only notify on actual change (same as subscription behavior)
        if (!ValuesAreEqual(newValue, oldValue))
        {
            // Update cached value atomically - only if item still exists and hasn't changed
            // This prevents resurrection of items removed between snapshot and processing
            var key = pollingItem.NodeId.ToString();
            var updatedItem = pollingItem with { LastValue = newValue };

            if (!_pollingItems.TryUpdate(key, updatedItem, pollingItem))
            {
                // Item was removed or modified concurrently - skip notification
                _logger.LogTrace("Skipping update for concurrently modified/removed item {NodeId}", pollingItem.NodeId);
                return;
            }

            // Create update record (same pattern as subscription manager)
            var update = new PropertyUpdate
            {
                Property = pollingItem.Property,
                Value = newValue,
                Timestamp = dataValue.SourceTimestamp
            };

            // Queue update using same pattern as subscriptions
            var state = (source: _source, manager: this, update, receivedTimestamp, logger: _logger);
            _propertyWriter.Write(state, static s =>
            {
                try
                {
                    s.update.Property.SetValueFromSource(s.source, s.update.Timestamp, s.receivedTimestamp, s.update.Value);
                }
                catch (Exception e)
                {
                    s.manager.ReportErrorIfRunning(e);
                    s.logger.LogError(e, "Failed to apply polled value change for {Path}", s.update.Property.Name);
                }
            });

            _metrics.RecordValueChange();
            _source.IncomingThroughput.Add(1);

            _logger.LogTrace("Polled value changed for {NodeId}: {OldValue} -> {NewValue}",
                pollingItem.NodeId, oldValue, newValue);
        }
    }

    private void ReportErrorIfRunning(Exception error, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) == 0 &&
            !_cts.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            _reportError(error);
        }
    }

    private bool TryAddPollingItem(string key, PollingItem item)
    {
        lock (_pollingItemsMutationLock)
        {
            if (Volatile.Read(ref _disposed) == 1 || !_pollingItems.TryAdd(key, item))
            {
                return false;
            }

            Volatile.Write(ref _pollingItemCount, _pollingItemCount + 1);
            return true;
        }
    }

    private void TryRemovePollingItem(string key)
    {
        lock (_pollingItemsMutationLock)
        {
            if (_pollingItems.TryRemove(key, out _))
            {
                Volatile.Write(ref _pollingItemCount, _pollingItemCount - 1);
            }
        }
    }

    private void ClearPollingItems()
    {
        lock (_pollingItemsMutationLock)
        {
            _pollingItems.Clear();
            Volatile.Write(ref _pollingItemCount, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _logger.LogDebug("Disposing OPC UA polling manager (Total reads: {TotalReads}, Failed: {FailedReads}, Value changes: {ValueChanges}, Slow polls: {SlowPolls}, Circuit breaker trips: {Trips})",
            _metrics.TotalReads, _metrics.FailedReads, _metrics.ValueChanges, _metrics.SlowPolls, _metrics.CircuitBreakerTrips);

        // Cancel work and stop timer
        await _cts.CancelAsync();
        _timer.Dispose();

        // Wait for polling task to complete asynchronously (with timeout)
        try
        {
            if (_pollingTask != null)
            {
                await _pollingTask.WaitAsync(_configuration.PollingDisposalTimeout).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Polling task did not complete within {Timeout} timeout", _configuration.PollingDisposalTimeout);
        }
        catch (OperationCanceledException)
        {
            // Expected during cancellation
        }

        _cts.Dispose();
        ClearPollingItems();
    }

    private record struct PollingItem(
        NodeId NodeId,
        RegisteredSubjectProperty Property,
        object? LastValue
    );
}
