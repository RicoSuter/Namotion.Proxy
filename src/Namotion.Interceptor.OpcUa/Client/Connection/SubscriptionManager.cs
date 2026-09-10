using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.OpcUa.Client.Resilience;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Performance;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client.Connection;

/// <summary>
/// How a monitored item that failed to (re-)create should be handled, aligned with
/// <see cref="OpcUaStatusCodeClassifier.IsTransientError"/>.
/// </summary>
internal enum FailedMonitoredItemDisposition
{
    /// <summary>Transient failure: leave the item in the subscription so the health monitor retries it.</summary>
    KeepForRetry,

    /// <summary>Node does not support subscriptions: move it to the polling fallback.</summary>
    FallbackToPolling,

    /// <summary>Permanent failure: remove the item; retrying cannot succeed.</summary>
    Drop
}

internal class SubscriptionManager : IAsyncDisposable
{
    private static readonly ObjectPool<List<PropertyUpdate>> ChangesPool
        = new(() => new List<PropertyUpdate>(16));

    private readonly OpcUaSubjectClientSource _source;
    private readonly SubjectPropertyWriter _propertyWriter;
    private readonly PollingManager? _pollingManager;
    private readonly ReadAfterWriteManager? _readAfterWriteManager;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly Action<Exception> _reportError;
    private readonly ILogger _logger;
    private readonly Func<Subscription, CancellationToken, Task> _applyChangesAsync;

    private readonly ConcurrentDictionary<uint, RegisteredSubjectProperty> _monitoredItems = new();
    private readonly ConcurrentDictionary<Subscription, byte> _subscriptions = new();
    private readonly ConcurrentDictionary<uint, int> _healAttempts = new();
    private readonly Lock _trackingMutationLock = new();

    private int _subscriptionCount;
    private int _monitoredItemCount;

    // Consecutive failed heal ticks a retryable item tolerates before it is escalated to polling
    // instead of being retried forever. With polling disabled there is no escalation target, so the
    // item keeps being retried and self-heals once the node recovers.
    internal const int MaxHealAttemptsBeforeEscalation = 3;

    private volatile bool _shuttingDown; // Prevents new callbacks during cleanup

    /// <summary>
    /// Gets the current list of subscriptions (thread-safe collection).
    /// </summary>
    public IReadOnlyCollection<Subscription> Subscriptions => (IReadOnlyCollection<Subscription>)_subscriptions.Keys;

    /// <summary>
    /// Gets how many subscriptions are currently held. Counting through <see cref="Subscriptions"/>
    /// would allocate, because the underlying concurrent dictionary snapshots its keys.
    /// </summary>
    public int SubscriptionCount => Volatile.Read(ref _subscriptionCount);

    /// <summary>
    /// Gets the current monitored items (thread-safe dictionary).
    /// </summary>
    public IReadOnlyDictionary<uint, RegisteredSubjectProperty> MonitoredItems => _monitoredItems;

    /// <summary>
    /// Gets how many monitored items are currently held without locking their backing dictionary.
    /// </summary>
    public int MonitoredItemCount => Volatile.Read(ref _monitoredItemCount);

    /// <summary>
    /// Returns true if any active subscription has stopped receiving publish responses from the server.
    /// </summary>
    public bool HasStoppedPublishing
    {
        get
        {
            foreach (var subscription in _subscriptions.Keys)
            {
                if (subscription.PublishingStopped)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public SubscriptionManager(
        OpcUaSubjectClientSource source,
        SubjectPropertyWriter propertyWriter,
        PollingManager? pollingManager,
        ReadAfterWriteManager? readAfterWriteManager,
        OpcUaClientConfiguration configuration,
        Action<Exception> reportError,
        ILogger logger,
        Func<Subscription, CancellationToken, Task>? applyChangesAsync = null)
    {
        _source = source;
        _propertyWriter = propertyWriter;
        _pollingManager = pollingManager;
        _readAfterWriteManager = readAfterWriteManager;
        _configuration = configuration;
        _reportError = reportError;
        _logger = logger;
        _applyChangesAsync = applyChangesAsync ??
            (static (subscription, cancellationToken) => subscription.ApplyChangesAsync(cancellationToken));
    }

    public async Task CreateBatchedSubscriptionsAsync(
        IReadOnlyList<MonitoredItem> monitoredItems,
        Session session,
        CancellationToken cancellationToken)
    {
        // Clear any existing subscriptions and monitored items from previous session (reconnection scenario).
        // Old subscriptions are orphaned (belong to dead session), so we just need to remove our references.
        foreach (var oldSubscription in _subscriptions.Keys)
        {
            oldSubscription.FastDataChangeCallback -= OnFastDataChange;
        }
        ClearTrackedCollections();
        _healAttempts.Clear();
        // On reconnect, re-attempt every owned property as a real subscription; failed nodes are
        // re-added to polling. Prevents double delivery of an escalated item that later recovers.
        _pollingManager?.Clear();

        // Reset shutdown flag AFTER clearing collections - prevents old callbacks from processing
        // during the window between flag reset and collection clearing (defense-in-depth).
        _shuttingDown = false;

        var itemCount = monitoredItems.Count;
        var maxItemsPerSubscription = _configuration.MaxItemsPerSubscription;
        for (var i = 0; i < itemCount; i += maxItemsPerSubscription)
        {
            var subscription = new Subscription(session.DefaultSubscription)
            {
                PublishingEnabled = true,
                PublishingInterval = _configuration.DefaultPublishingInterval,
                DisableMonitoredItemCache = true, // not needed as we use fast data change callback
                MinLifetimeInterval = 60_000,
                KeepAliveCount = _configuration.SubscriptionKeepAliveCount,
                LifetimeCount = _configuration.SubscriptionLifetimeCount,
                Priority = _configuration.SubscriptionPriority,
                MaxNotificationsPerPublish = _configuration.SubscriptionMaxNotificationsPerPublish,
                RepublishAfterTransfer = true, // Enable SDK's automatic republish of missed messages after transfer
                SequentialPublishing = _configuration.SubscriptionSequentialPublishing,
            };

            if (!session.AddSubscription(subscription))
            {
                throw new InvalidOperationException("Failed to add OPC UA subscription.");
            }

            subscription.FastDataChangeCallback += OnFastDataChange;
            await subscription.CreateAsync(cancellationToken).ConfigureAwait(false);

            var batchEnd = Math.Min(i + maxItemsPerSubscription, itemCount);
            for (var j = i; j < batchEnd; j++)
            {
                var item = monitoredItems[j];
                subscription.AddItem(item);

                TrackMonitoredItem(item);
            }

            await ApplyChangesAndFilterFailedMonitoredItemsAsync(subscription, cancellationToken).ConfigureAwait(false);

            // Register properties with ReadAfterWriteManager now that we know revised sampling intervals
            RegisterPropertiesWithReadAfterWriteManager(subscription);

            // Add to collection AFTER initialization (temporal separation - health monitor never sees partial state)
            TryAddSubscription(subscription);
        }
    }

    internal void TrackMonitoredItem(MonitoredItem item)
    {
        if (item.Handle is RegisteredSubjectProperty property)
        {
            SetMonitoredItem(item.ClientHandle, property);
        }
    }

    internal void OnFastDataChange(Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
    {
        if (_shuttingDown)
        {
            return;
        }

        var monitoredItemsCount = notification.MonitoredItems.Count;
        if (monitoredItemsCount == 0)
        {
            return;
        }

        var receivedTimestamp = DateTimeOffset.UtcNow;
        var changes = ChangesPool.Rent();

        try
        {
            for (var i = 0; i < monitoredItemsCount; i++)
            {
                var item = notification.MonitoredItems[i];
                if (_monitoredItems.TryGetValue(item.ClientHandle, out var property))
                {
                    changes.Add(new PropertyUpdate
                    {
                        Property = property,
                        Value = _configuration.ValueConverter.ConvertToPropertyValue(item.Value.Value, property),
                        Timestamp = item.Value.SourceTimestamp
                    });
                }
            }
        }
        catch (Exception error)
        {
            // Return pooled list on exception to prevent pool exhaustion
            changes.Clear();
            ChangesPool.Return(changes);
            ReportErrorIfRunning(error);
            throw;
        }

        if (changes.Count > 0)
        {
            _source.IncomingThroughput.Add(changes.Count);

            // Pool item returned inside callback. Safe because ApplyUpdate never throws:
            // It wraps callback execution in try-catch and only throws on catastrophic failures (lock/memory corruption).
            var state = (manager: this, source: _source, subscription, receivedTimestamp, changes, logger: _logger);
            _propertyWriter.Write(state, static s =>
            {
                for (var i = 0; i < s.changes.Count; i++)
                {
                    var change = s.changes[i];
                    try
                    {
                        change.Property.SetValueFromSource(s.source, change.Timestamp, s.receivedTimestamp, change.Value);
                    }
                    catch (Exception e)
                    {
                        s.manager.ReportErrorIfRunning(e);
                        s.logger.LogError(e, "Failed to apply change for property {PropertyName}.", change.Property.Name);
                    }
                }

                s.changes.Clear();
                ChangesPool.Return(s.changes);
            });
        }
        else
        {
            ChangesPool.Return(changes);
        }
    }

    private void ReportErrorIfRunning(Exception error, CancellationToken cancellationToken = default)
    {
        if (!_shuttingDown && !cancellationToken.IsCancellationRequested)
        {
            _reportError(error);
        }
    }

    /// <summary>
    /// Updates the subscription list to reference subscriptions transferred by SessionReconnectHandler.
    /// Called after successful session transfer to embrace OPC Foundation's subscription preservation.
    /// </summary>
    public void UpdateTransferredSubscriptions(IReadOnlyCollection<Subscription> transferredSubscriptions)
    {
        var oldSubscriptions = _subscriptions.Keys.ToArray();
        foreach (var subscription in transferredSubscriptions)
        {
            subscription.FastDataChangeCallback -= OnFastDataChange;
            subscription.FastDataChangeCallback += OnFastDataChange;
            TryAddSubscription(subscription);
        }

        foreach (var oldSubscription in oldSubscriptions)
        {
            TryRemoveSubscription(oldSubscription);
            oldSubscription.FastDataChangeCallback -= OnFastDataChange;
        }


        _logger.LogInformation("Updated subscription manager with {Count} transferred subscriptions (removed {OldCount} old)",
            transferredSubscriptions.Count, oldSubscriptions.Length);
    }

    internal async Task ApplyChangesAndFilterFailedMonitoredItemsAsync(
        Subscription subscription,
        CancellationToken cancellationToken)
    {
        try
        {
            await _applyChangesAsync(subscription, cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceResultException error)
        {
            ReportErrorIfRunning(error, cancellationToken);
            _logger.LogWarning(error, "ApplyChanges failed for a batch; attempting to keep valid OPC UA monitored items by removing failed ones.");
        }

        await FilterOutFailedMonitoredItemsAsync(subscription, cancellationToken).ConfigureAwait(false);
    }

    private async Task FilterOutFailedMonitoredItemsAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        List<MonitoredItem>? removedItems = null;
        List<MonitoredItem>? polledItems = null;
        var keptForRetry = 0;

        var pollingEnabled = _configuration.EnablePollingFallback && _pollingManager != null;

        foreach (var monitoredItem in subscription.MonitoredItems)
        {
            if (!SubscriptionHealthMonitor.IsUnhealthy(monitoredItem))
            {
                continue;
            }

            var statusCode = monitoredItem.Status.Error?.StatusCode ?? StatusCodes.Good;

            switch (ClassifyFailedItem(statusCode, pollingEnabled))
            {
                case FailedMonitoredItemDisposition.KeepForRetry:
                    // Keep it in the subscription so the health monitor heals it; removing it here
                    // silently orphaned transiently-failed items.
                    keptForRetry++;
                    _logger.LogWarning("OPC UA monitored item {DisplayName} failed transiently ({Status}); keeping it for the health monitor to retry.",
                        monitoredItem.DisplayName, statusCode);
                    break;

                case FailedMonitoredItemDisposition.FallbackToPolling:
                    removedItems ??= [];
                    removedItems.Add(monitoredItem);
                    TryRemoveMonitoredItem(monitoredItem.ClientHandle);
                    polledItems ??= [];
                    polledItems.Add(monitoredItem);
                    _logger.LogWarning("Monitored item {DisplayName} does not support subscriptions ({Status}), falling back to polling",
                        monitoredItem.DisplayName, statusCode);
                    break;

                case FailedMonitoredItemDisposition.Drop:
                    removedItems ??= [];
                    removedItems.Add(monitoredItem);
                    TryRemoveMonitoredItem(monitoredItem.ClientHandle);
                    _logger.LogError("OPC UA monitored item creation failed permanently for {DisplayName} (Handle={Handle}): {Status}",
                        monitoredItem.DisplayName, monitoredItem.ClientHandle, statusCode);
                    break;
            }
        }

        if (removedItems is { Count: > 0 })
        {
            await RemoveAndFallBackToPollingAsync(subscription, removedItems, polledItems ?? [], cancellationToken).ConfigureAwait(false);
        }

        if (removedItems?.Count > 0 || keptForRetry > 0)
        {
            _logger.LogWarning(
                "Subscription {SubscriptionId}: removed {Removed} failed monitored items " +
                "({Polled} switched to polling), kept {Kept} for the health monitor to retry.",
                subscription.Id, removedItems?.Count ?? 0, polledItems?.Count ?? 0, keptForRetry);
        }
    }

    /// <summary>
    /// Checks if a status code indicates that subscriptions are not supported for this node.
    /// These items should fall back to polling if enabled.
    /// </summary>
    private static bool IsSubscriptionUnsupported(StatusCode statusCode)
    {
        // BadNotSupported - Server doesn't support subscriptions for this node
        // BadMonitoredItemFilterUnsupported - Filter not supported (data change filter)
        // Note: BadAttributeIdInvalid is a permanent error - polling won't work either, so excluded
        return statusCode == StatusCodes.BadNotSupported ||
               statusCode == StatusCodes.BadMonitoredItemFilterUnsupported;
    }

    /// <summary>
    /// Decides how a failed monitored item should be handled. Transient failures are kept in the
    /// subscription so <see cref="SubscriptionHealthMonitor"/> can heal them (previously they were
    /// dropped, which silently orphaned the item until an unrelated full reconnect).
    /// </summary>
    internal static FailedMonitoredItemDisposition ClassifyFailedItem(StatusCode statusCode, bool pollingEnabled)
    {
        if (IsSubscriptionUnsupported(statusCode))
        {
            return pollingEnabled
                ? FailedMonitoredItemDisposition.FallbackToPolling
                : FailedMonitoredItemDisposition.Drop;
        }

        return OpcUaStatusCodeClassifier.IsTransientError(statusCode)
            ? FailedMonitoredItemDisposition.KeepForRetry
            : FailedMonitoredItemDisposition.Drop;
    }

    /// <summary>
    /// Whether a retryable item that keeps failing should be escalated to polling (its retry bound
    /// was exceeded) rather than kept for the health monitor to retry.
    /// </summary>
    internal static bool ShouldEscalateToPolling(int consecutiveFailures, int maxAttempts)
    {
        return consecutiveFailures >= maxAttempts;
    }

    /// <summary>
    /// Removes the given items from the SDK subscription and applies the change (tolerating an
    /// ApplyChanges failure), then hands the polled items to the polling manager.
    /// </summary>
    internal async Task RemoveAndFallBackToPollingAsync(
        Subscription subscription,
        IReadOnlyList<MonitoredItem> toRemove,
        IReadOnlyList<MonitoredItem> toPoll,
        CancellationToken cancellationToken)
    {
        foreach (var monitoredItem in toRemove)
        {
            subscription.RemoveItem(monitoredItem);
        }

        try
        {
            await _applyChangesAsync(subscription, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportErrorIfRunning(ex, cancellationToken);
            _logger.LogWarning(ex, "ApplyChanges after removing failed OPC UA monitored items failed. Continuing with the remaining items.");
        }

        if (_pollingManager is not null)
        {
            foreach (var monitoredItem in toPoll)
            {
                _pollingManager.AddItem(monitoredItem);
            }
        }
    }

    /// <summary>
    /// Escalates a retryable item that keeps failing past <see cref="MaxHealAttemptsBeforeEscalation"/>
    /// to polling instead of retrying forever. Runs only when polling is enabled; reconnect clears the
    /// polling set and re-attempts every item, so escalation is not permanent.
    /// </summary>
    public async Task EscalatePersistentlyFailedItemsAsync(CancellationToken cancellationToken)
    {
        if (!_configuration.EnablePollingFallback || _pollingManager is null)
        {
            return;
        }

        foreach (var subscription in _subscriptions.Keys)
        {
            List<MonitoredItem>? toEscalate = null;

            foreach (var monitoredItem in subscription.MonitoredItems)
            {
                if (!SubscriptionHealthMonitor.IsUnhealthy(monitoredItem))
                {
                    if (!_healAttempts.IsEmpty)
                    {
                        _healAttempts.TryRemove(monitoredItem.ClientHandle, out _); // recovered: reset
                    }
                    continue;
                }

                if (!SubscriptionHealthMonitor.IsRetryable(monitoredItem))
                {
                    // A runtime transition to permanent-bad is left in the subscription until reconnect,
                    // but drop any heal counter so the map only tracks currently-retryable failing items.
                    if (!_healAttempts.IsEmpty)
                    {
                        _healAttempts.TryRemove(monitoredItem.ClientHandle, out _);
                    }
                    continue;
                }

                var attempts = _healAttempts.AddOrUpdate(monitoredItem.ClientHandle, 1, static (_, current) => current + 1);

                if (ShouldEscalateToPolling(attempts, MaxHealAttemptsBeforeEscalation))
                {
                    (toEscalate ??= []).Add(monitoredItem);
                }
            }

            if (toEscalate is not { Count: > 0 })
            {
                continue;
            }

            foreach (var monitoredItem in toEscalate)
            {
                TryRemoveMonitoredItem(monitoredItem.ClientHandle);
                _healAttempts.TryRemove(monitoredItem.ClientHandle, out _);
            }

            await RemoveAndFallBackToPollingAsync(subscription, toEscalate, toEscalate, cancellationToken).ConfigureAwait(false);

            _logger.LogWarning(
                "Escalated {Count} persistently-failing monitored items to polling in subscription {SubscriptionId} after {Max} retries.",
                toEscalate.Count, subscription.Id, MaxHealAttemptsBeforeEscalation);
        }
    }

    /// <summary>
    /// Removes monitored items for a detached subject. Idempotent.
    /// Note: OPC UA subscription items remain on server until session ends.
    /// This just cleans up local tracking to avoid memory leaks.
    /// </summary>
    public void RemoveItemsForSubject(IInterceptorSubject subject)
    {
        foreach (var kvp in _monitoredItems)
        {
            if (kvp.Value.Reference.Subject == subject)
            {
                TryRemoveMonitoredItem(kvp.Key);
                _healAttempts.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// Registers all successfully created monitored items with ReadAfterWriteManager.
    /// Called after ApplyChangesAsync when we know the revised sampling intervals.
    /// </summary>
    private void RegisterPropertiesWithReadAfterWriteManager(Subscription subscription)
    {
        if (_readAfterWriteManager is null)
        {
            return;
        }

        foreach (var item in subscription.MonitoredItems)
        {
            if (item.Handle is RegisteredSubjectProperty property && item.Status?.Created == true)
            {
                var requestedInterval = GetRequestedSamplingInterval(property);
                var revisedInterval = TimeSpan.FromMilliseconds(item.Status.SamplingInterval);
                _readAfterWriteManager.RegisterProperty(item.StartNodeId, property, requestedInterval, revisedInterval);
            }
        }
    }

    /// <summary>
    /// Gets the requested sampling interval for a property from the mapper or configuration default.
    /// </summary>
    private int? GetRequestedSamplingInterval(RegisteredSubjectProperty property)
    {
        if (_configuration.Mapper.TryGetMapping(property, _source.RootSubject, out var mapping) &&
            mapping.SamplingInterval.HasValue)
        {
            return mapping.SamplingInterval;
        }

        return _configuration.DefaultSamplingInterval;
    }

    private bool TryAddSubscription(Subscription subscription)
    {
        lock (_trackingMutationLock)
        {
            if (!_subscriptions.TryAdd(subscription, 0))
            {
                return false;
            }

            Volatile.Write(ref _subscriptionCount, _subscriptionCount + 1);
            return true;
        }
    }

    private void TryRemoveSubscription(Subscription subscription)
    {
        lock (_trackingMutationLock)
        {
            if (_subscriptions.TryRemove(subscription, out _))
            {
                Volatile.Write(ref _subscriptionCount, _subscriptionCount - 1);
            }
        }
    }

    private void SetMonitoredItem(uint clientHandle, RegisteredSubjectProperty property)
    {
        lock (_trackingMutationLock)
        {
            if (_monitoredItems.TryAdd(clientHandle, property))
            {
                Volatile.Write(ref _monitoredItemCount, _monitoredItemCount + 1);
            }
            else
            {
                _monitoredItems[clientHandle] = property;
            }
        }
    }

    private void TryRemoveMonitoredItem(uint clientHandle)
    {
        lock (_trackingMutationLock)
        {
            if (_monitoredItems.TryRemove(clientHandle, out _))
            {
                Volatile.Write(ref _monitoredItemCount, _monitoredItemCount - 1);
            }
        }
    }

    private void ClearTrackedCollections()
    {
        lock (_trackingMutationLock)
        {
            _subscriptions.Clear();
            _monitoredItems.Clear();
            Volatile.Write(ref _subscriptionCount, 0);
            Volatile.Write(ref _monitoredItemCount, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shuttingDown = true;

        var subscriptions = _subscriptions.Keys.ToArray();
        ClearTrackedCollections();

        foreach (var subscription in subscriptions)
        {
            subscription.FastDataChangeCallback -= OnFastDataChange;
        }

        // Use session.RemoveSubscriptionsAsync instead of subscription.DeleteAsync
        // to also remove subscriptions from session.m_subscriptions. DeleteAsync alone
        // only deletes on the server but does not remove from the session's internal list,
        // keeping the entire Subscription object graph alive until session disposal.
        if (subscriptions.Length > 0)
        {
            var session = subscriptions[0].Session;
            if (session != null)
            {
                var disposalTimeout = _configuration.SessionDisposalTimeout;
                try
                {
                    await session.RemoveSubscriptionsAsync(subscriptions, default)
                        .WaitAsync(disposalTimeout).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove subscriptions during disposal.");
                }
            }
        }
    }
}
