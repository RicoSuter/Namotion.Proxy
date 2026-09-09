using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.OpcUa.Client.Resilience;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Performance;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;
using ReferenceEqualityComparer = System.Collections.Generic.ReferenceEqualityComparer;

namespace Namotion.Interceptor.OpcUa.Client.Connection;

/// <summary>
/// How a monitored item that failed to (re-)create should be handled, aligned with
/// <see cref="OpcUaStatusCodeClassifier.IsRecoverableWithinSession"/>.
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

    // Subjects whose detach ran while setup was still tracking items; drained by the sweep in
    // CompleteSetup. Used as a set; the value is ignored.
    private readonly ConcurrentDictionary<IInterceptorSubject, byte> _detachedDuringSetup = new(ReferenceEqualityComparer.Instance);

    // True from BeginSetup until CompleteSetup starts; see RemoveItemsForSubject.
    private volatile bool _setupInProgress;

    // Consecutive failed heal ticks a retryable item tolerates before it is escalated to polling
    // instead of being retried forever. With polling disabled there is no escalation target, so the
    // item keeps being retried and self-heals once the node recovers.
    internal const int MaxHealAttemptsBeforeEscalation = 3;

    // Prevents new callbacks during cleanup. Monotonic by design: set once by DisposeAsync, never
    // cleared. Disposal is terminal because SessionManager.DisposeAsync is Interlocked-guarded and
    // its callers null out the session manager, so the next connect builds a fresh instance instead
    // of reusing this one. Reconnect does reuse the instance, but it never sets this flag, so
    // CreateBatchedSubscriptionsAsync has nothing to reset. Do not add a reset there: it would let a
    // reconnect racing a disposal resume callbacks on a disposed manager, and it would make the
    // !_shuttingDown guard in CompleteSetup dead.
    private volatile bool _shuttingDown;
    private volatile bool _callbacksEnabled; // Gated to false until subscription setup completes

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
        BeginSetup();

        try
        {
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

                // Add to collection AFTER initialization (temporal separation - health monitor never sees partial state)
                TryAddSubscription(subscription);
            }

            CompleteSetup(_subscriptions.Keys.SelectMany(subscription => subscription.MonitoredItems));
        }
        finally
        {
            // A setup that throws before CompleteSetup must still stop recording, or every later
            // detach accumulates in _detachedDuringSetup with nothing to drain it.
            _setupInProgress = false;
        }
    }

    /// <summary>
    /// Enters the state subscription setup runs in: closes the callback gate, forgets the previous
    /// session's subscriptions and monitored items, and records detaches until
    /// <see cref="CompleteSetup"/> sweeps them.
    /// </summary>
    internal void BeginSetup()
    {
        // Closed first so a reconnection re-setup cannot let an in-flight or newly-entering
        // notification pass on the previous setup's stale-true flag.
        _callbacksEnabled = false;

        // Old subscriptions belong to the dead session, so only the references are dropped.
        foreach (var oldSubscription in _subscriptions.Keys)
        {
            oldSubscription.FastDataChangeCallback -= OnFastDataChange;
        }
        ClearTrackedCollections();
        _healAttempts.Clear();
        // Entries a setup that threw left behind would make this cycle's sweep drop items for a
        // subject that has since re-attached.
        _detachedDuringSetup.Clear();
        _setupInProgress = true;
        // On reconnect, re-attempt every owned property as a real subscription; failed nodes are
        // re-added to polling. Prevents double delivery of an escalated item that later recovers.
        _pollingManager?.Clear();
    }

    internal void TrackMonitoredItem(MonitoredItem item)
    {
        if (item.Handle is RegisteredSubjectProperty property)
        {
            SetMonitoredItem(item.ClientHandle, property);
        }
    }

    /// <summary>
    /// Finishes subscription setup: stops recording detaches, drops monitored items whose subject
    /// detached while the subscriptions were being created, registers what survived for
    /// read-after-write tracking, then opens the callback gate.
    /// </summary>
    /// <remarks>
    /// Sweeping first is what keeps a detached subject out of the read-after-write index, and the
    /// gate stays closed across both steps so no notification can reach a subject mid-setup.
    /// Notifications arriving after the gate opens but before the initial state load are not lost:
    /// the caller starts the property writer's buffering before setup and replays it afterwards.
    /// </remarks>
    internal void CompleteSetup(IEnumerable<MonitoredItem> monitoredItems)
    {
        // Every TrackMonitoredItem call precedes this method, so from here a detach finds its own
        // items and needs no recording, and the sweep can remove through the same entry point.
        _setupInProgress = false;

        SweepDetachedSubjects();
        RegisterSurvivors(monitoredItems);

        // Never re-open a gate that DisposeAsync closed: it may have run concurrently with setup.
        _callbacksEnabled = !_shuttingDown;
    }

    // Inbound notifications are dropped while shutting down or before the setup gate opens.
    private bool AreCallbacksSuppressed => _shuttingDown || !_callbacksEnabled;

    internal void OnFastDataChange(Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
    {
        if (AreCallbacksSuppressed)
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

        ApplyChanges(changes, receivedTimestamp);
    }

    /// <summary>
    /// Writes a batch of property updates through the property writer and returns the pooled
    /// list, whether or not the batch was empty. A single property write that throws is logged
    /// and the remaining changes in the batch still apply.
    /// </summary>
    private void ApplyChanges(List<PropertyUpdate> changes, DateTimeOffset receivedTimestamp)
    {
        if (changes.Count == 0)
        {
            ChangesPool.Return(changes);
            return;
        }

        _source.IncomingThroughput.Add(changes.Count);

        // Pool item returned inside callback. Safe because ApplyUpdate never throws:
        // It wraps callback execution in try-catch and only throws on catastrophic failures (lock/memory corruption).
        var state = (manager: this, source: _source, receivedTimestamp, changes, logger: _logger);
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

        return OpcUaStatusCodeClassifier.IsRecoverableWithinSession(statusCode)
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
                if (!_monitoredItems.ContainsKey(monitoredItem.ClientHandle))
                {
                    // Swept because its subject detached, but the SDK subscription still holds it.
                    // Escalating would resurrect it into polling under a subject nobody tracks.
                    continue;
                }

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
        // A detach that lands while items are still being tracked is recorded for the sweep in
        // CompleteSetup, whose registry check cannot see it: the lifecycle interceptor raises
        // SubjectDetaching before the registry handler runs. Recording stops once CompleteSetup
        // starts because nothing drains the set outside a setup cycle.
        if (_setupInProgress)
        {
            _detachedDuringSetup[subject] = 0;
        }

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
    /// Removes monitored items for every subject that is no longer in the registry.
    /// </summary>
    private void SweepDetachedSubjects()
    {
        // Drop anything that detached while setup was running, before the registry check below,
        // which cannot see those subjects (see RemoveItemsForSubject).
        if (!_detachedDuringSetup.IsEmpty)
        {
            foreach (var entry in _detachedDuringSetup)
            {
                RemoveItemsForSubject(entry.Key);
                _pollingManager?.RemoveItemsForSubject(entry.Key);
            }

            _detachedDuringSetup.Clear();
        }

        if (_monitoredItems.IsEmpty)
        {
            return;
        }

        // Enumerate the dictionary rather than its Values property, which takes every bucket lock
        // and copies. Testing the seen-set first also keeps the registry lookup to one per distinct
        // subject instead of one per monitored item.
        var seen = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance);
        foreach (var entry in _monitoredItems)
        {
            var subject = entry.Value.Reference.Subject;
            if (seen.Add(subject) && subject.TryGetRegisteredSubject() is null)
            {
                RemoveItemsForSubject(subject);
                _pollingManager?.RemoveItemsForSubject(subject);
            }
        }
    }

    /// <summary>
    /// Registers read-after-write tracking for monitored items whose subject survived the sweep.
    /// Only items that are created on the server and still present in <c>_monitoredItems</c>
    /// (the sweep removed detached subjects' handles) are registered.
    /// </summary>
    private void RegisterSurvivors(IEnumerable<MonitoredItem> monitoredItems)
    {
        if (_readAfterWriteManager is null)
        {
            return;
        }

        foreach (var item in monitoredItems)
        {
            if (item is { Handle: RegisteredSubjectProperty property, Status.Created: true } &&
                _monitoredItems.ContainsKey(item.ClientHandle))
            {
                _readAfterWriteManager.RegisterProperty(
                    item.StartNodeId,
                    property,
                    GetRequestedSamplingInterval(property),
                    TimeSpan.FromMilliseconds(item.Status.SamplingInterval));
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

    /// <summary>
    /// Number of subjects recorded as having detached during the current setup cycle. Lets tests
    /// assert the set is drained rather than growing without bound.
    /// </summary>
    internal int DetachedDuringSetupCountForTesting => _detachedDuringSetup.Count;

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
        _callbacksEnabled = false;

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
