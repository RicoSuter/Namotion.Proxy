using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Client.Connection;

/// <summary>
/// Manages OPC UA session lifecycle, reconnection handling, and thread-safe session access.
/// Optimized for fast session reads (hot path) with simple lock-based writes (cold path).
/// </summary>
internal sealed class SessionManager : IAsyncDisposable, IDisposable
{
    private readonly OpcUaSubjectClientSource _source;
    private readonly SubjectPropertyWriter _propertyWriter;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;

    private volatile SessionReconnectHandler _reconnectHandler;

    private Session? _session;

    private readonly object _reconnectingLock = new();

    // Session transitions are enqueued under _reconnectingLock (or other serialized contexts)
    // by SetSession and drained outside the lock by DrainPendingSessionTransitions. This keeps
    // user-supplied CurrentSessionChanged handlers from running with the reconnection lock held,
    // eliminating a class of deadlocks when handlers call back into the connector.
    private readonly ConcurrentQueue<(Session? Previous, Session? Current)> _pendingSessionTransitions = new();

    // Serializes CurrentSessionChanged handler invocations across concurrent drainers.
    // Never held together with _reconnectingLock (drains always run after that lock has been released).
    private readonly object _drainLock = new();

    private int _isReconnecting; // 0 = false, 1 = true (thread-safe via Interlocked)
    private int _pendingSdkReconnection; // 1 = an SDK auto-reconnect started (Total incremented by OnKeepAlive) but not yet resolved
    private int _needsFullStateSync; // 0 = false, 1 = true (thread-safe via Interlocked)
    private int _disposed; // 0 = false, 1 = true (thread-safe via Interlocked)
    // Enqueued by SDK reconnection callbacks (sync), drained by health check loop via DisposePendingSessionsAsync (async).
    private readonly ConcurrentQueue<Session> _sessionsToDispose = new();

    /// <summary>
    /// Gets the current session. WARNING: Can change at any time due to reconnection. Never cache - read immediately before use.
    /// </summary>
    public Session? CurrentSession => Volatile.Read(ref _session);

    /// <summary>
    /// Gets a value indicating whether the session is currently connected and not reconnecting.
    /// Returns true only if there is a session, it reports as connected, AND we're not in a reconnection cycle.
    /// </summary>
    public bool IsConnected =>
        Volatile.Read(ref _isReconnecting) == 0 &&
        (Volatile.Read(ref _session)?.Connected ?? false);

    /// <summary>
    /// Performs a full state synchronization after SDK reconnection if needed.
    /// Buffers incoming notifications, reads all property values from the server,
    /// replays the buffer, and resumes normal operation.
    /// On failure, clears the session to trigger manual reconnection via the health check loop.
    /// </summary>
    /// <remarks>
    /// Called only from the health check loop in OpcUaSubjectClientSource.ExecuteAsync (single-threaded).
    /// </remarks>
    internal async Task PerformFullStateSyncIfNeededAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _needsFullStateSync) != 1)
        {
            return;
        }

        _logger.LogInformation("Performing full state sync after SDK reconnection...");
        _propertyWriter.StartBuffering();
        try
        {
            await _propertyWriter.LoadInitialStateAndResumeAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _needsFullStateSync, 0);
            _logger.LogInformation("Full state sync completed successfully after SDK reconnection.");
        }
        catch (Exception ex)
        {
            _source.ReportBackgroundError(ex, cancellationToken);
            _logger.LogError(ex, "Full state sync failed after SDK reconnection. Clearing session for manual reconnection.");
            await ClearSessionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the session is currently reconnecting.
    /// </summary>
    public bool IsReconnecting => Volatile.Read(ref _isReconnecting) == 1;

    /// <summary>
    /// Gets a value indicating whether this manager has been disposed. The source keeps its field
    /// pointing here afterwards, so everything reachable through it then describes a session that is
    /// already gone.
    /// </summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <summary>
    /// Reports the source's liveness from the current session state: raised when the session is
    /// connected and not reconnecting, dropped otherwise.
    /// </summary>
    /// <remarks>
    /// The read and the report are one step under <c>_reconnectingLock</c>, which
    /// <see cref="OnKeepAlive"/>, <see cref="OnReconnectComplete"/> and <see cref="SetReconnecting"/>
    /// also take, so a stale read cannot raise liveness after OnKeepAlive dropped it and leave the
    /// client reporting itself as reconnecting and operational at once. That lock is not a leaf, so
    /// the invariant an edit has to preserve is the reverse direction: nothing takes
    /// <c>_reconnectingLock</c> while holding the property writer's lock, the source's state lock or
    /// a source monitor's lock.
    /// </remarks>
    internal void ReportLivenessFromSessionState()
    {
        lock (_reconnectingLock)
        {
            if (IsConnected)
            {
                _source.NotifySessionHealthy();
            }
            else
            {
                _source.NotifySessionNotHealthy();
            }
        }
    }

    /// <summary>
    /// Gets whether there are sessions waiting for async disposal by the health check loop.
    /// </summary>
    public bool HasSessionsToDispose => !_sessionsToDispose.IsEmpty;

    /// <summary>
    /// Sets or clears the reconnecting flag. When set, OnKeepAlive returns early
    /// without triggering the SDK reconnect handler. Used by manual reconnection
    /// (ReconnectSessionAsync) to prevent keep-alive on newly created sessions from
    /// triggering OnReconnectComplete → AbandonCurrentSession while the manual
    /// reconnection is still in progress.
    /// </summary>
    /// <remarks>
    /// Takes <c>_reconnectingLock</c>, so callers must hold none of the locks named in the remarks on
    /// <see cref="ReportLivenessFromSessionState"/>. The interlocked write stays because
    /// <see cref="IsReconnecting"/> reads the flag without the lock.
    /// </remarks>
    internal void SetReconnecting(bool value)
    {
        lock (_reconnectingLock)
        {
            Interlocked.Exchange(ref _isReconnecting, value ? 1 : 0);
        }
    }

    /// <summary>
    /// Gets the current subscriptions managed by the subscription manager.
    /// </summary>
    public IReadOnlyCollection<Subscription> Subscriptions => SubscriptionManager.Subscriptions;

    /// <summary>
    /// Gets the subscription manager for cleanup operations.
    /// </summary>
    internal SubscriptionManager SubscriptionManager { get; }

    /// <summary>
    /// Gets the polling manager for cleanup operations (may be null if polling disabled).
    /// </summary>
    internal PollingManager? PollingManager { get; }

    /// <summary>
    /// Gets the read-after-write manager for scheduling reads after writes.
    /// </summary>
    internal ReadAfterWriteManager? ReadAfterWriteManager { get; }

    /// <summary>
    /// Gets the diagnostics view over <see cref="PollingManager"/>, or <c>null</c> when the polling
    /// fallback is off. Built once here so a diagnostics read does not allocate.
    /// </summary>
    internal PollingDiagnostics? PollingDiagnostics { get; }

    /// <summary>
    /// Gets the diagnostics view over <see cref="ReadAfterWriteManager"/>, or <c>null</c> when
    /// read-after-write is off.
    /// </summary>
    internal ReadAfterWriteDiagnostics? ReadAfterWriteDiagnostics { get; }

    public SessionManager(
        OpcUaSubjectClientSource source,
        SubjectPropertyWriter propertyWriter,
        OpcUaClientConfiguration configuration,
        PollingMetrics pollingMetrics,
        ReadAfterWriteMetrics readAfterWriteMetrics,
        ILogger logger)
    {
        _source = source;
        _propertyWriter = propertyWriter;
        _logger = logger;
        _configuration = configuration;
        _reconnectHandler = new SessionReconnectHandler(configuration.ResolvedTelemetryContext, false, (int)configuration.ReconnectHandlerTimeout.TotalMilliseconds);

        if (_configuration.EnablePollingFallback)
        {
            PollingManager = new PollingManager(
                source,
                sessionProvider: () => CurrentSession,
                propertyWriter,
                _configuration,
                pollingMetrics,
                source.ReportBackgroundError,
                _logger);

            PollingDiagnostics = new PollingDiagnostics(PollingManager, pollingMetrics);

            PollingManager.Start();
        }

        if (_configuration.EnableReadAfterWrite)
        {
            ReadAfterWriteManager = new ReadAfterWriteManager(
                sessionProvider: () => CurrentSession,
                source,
                configuration,
                readAfterWriteMetrics,
                source.ReportBackgroundError,
                logger);

            ReadAfterWriteDiagnostics = new ReadAfterWriteDiagnostics(ReadAfterWriteManager, readAfterWriteMetrics);
        }

        SubscriptionManager = new SubscriptionManager(
            source,
            propertyWriter,
            PollingManager,
            ReadAfterWriteManager,
            configuration,
            source.ReportBackgroundError,
            logger);
    }

    /// <summary>
    /// Create a new OPC UA session with the specified configuration.
    /// Thread-safety: Cannot race with OnReconnectComplete because callers coordinate via IsReconnecting flag.
    /// Manual reconnection (ReconnectSessionAsync) only proceeds when !IsReconnecting, which blocks when
    /// automatic reconnection (OnReconnectComplete) is active. Therefore, no lock needed here.
    /// </summary>
    public async Task<Session> CreateSessionAsync(
        ApplicationInstance application,
        OpcUaClientConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var endpointConfiguration = EndpointConfiguration.Create(application.ApplicationConfiguration);
        var serverUri = new Uri(configuration.ServerUrl);

        EndpointDescriptionCollection endpoints;
        try
        {
            using var discoveryClient = await DiscoveryClient.CreateAsync(
                application.ApplicationConfiguration,
                serverUri,
                endpointConfiguration, ct: cancellationToken).ConfigureAwait(false);

            endpoints = await discoveryClient.GetEndpointsAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to discover OPC UA endpoints at '{configuration.ServerUrl}'. " +
                $"Verify the server is running and the URL is correct. " +
                $"The connection will be retried automatically.",
                ex);
        }

        var endpointDescription = CoreClientUtils.SelectEndpoint(
            application.ApplicationConfiguration,
            serverUri,
            endpoints,
            useSecurity: configuration.UseSecurity,
            configuration.ResolvedTelemetryContext);

        var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);
        var oldSession = Volatile.Read(ref _session);

        var sessionFactory = configuration.ActualSessionFactory;
        var sessionResult = await sessionFactory.CreateAsync(
            application.ApplicationConfiguration,
            endpoint,
            updateBeforeConnect: false,
            sessionName: application.ApplicationName,
            sessionTimeout: (uint)configuration.SessionTimeout.TotalMilliseconds,
            identity: configuration.CreateUserIdentity != null
                ? await configuration.CreateUserIdentity(cancellationToken).ConfigureAwait(false)
                : new UserIdentity(),
            preferredLocales: null,
            cancellationToken).ConfigureAwait(false);

        var newSession = sessionResult as Session
            ?? throw new InvalidOperationException(
                $"Session factory returned unexpected type '{sessionResult?.GetType().FullName ?? "null"}'. " +
                $"Expected '{typeof(Session).FullName}'. Ensure the configured SessionFactory returns a valid Session instance.");

        ConfigureSession(newSession);

        // Validate: a brand new session must be connected with a valid transport channel.
        // If not, something went wrong during creation (e.g., server not fully initialized).
        if (!newSession.Connected || newSession.NullableTransportChannel is null)
        {
            _logger.LogError(
                "Newly created OPC UA session is not usable (id={SessionId}, connected={Connected}, hasTransport={HasTransport}). " +
                "Disposing and throwing to trigger retry.",
                newSession.SessionId,
                newSession.Connected,
                newSession.NullableTransportChannel is not null);

            try { newSession.Dispose(); } catch { /* best effort */ }
            throw new InvalidOperationException(
                $"Newly created OPC UA session is not connected or has no transport channel. " +
                $"SessionId={newSession.SessionId}, Connected={newSession.Connected}.");
        }

        SetSession(newSession);
        DrainPendingSessionTransitions();

        if (oldSession is not null)
        {
            // Kill old transport immediately so any in-flight WriteAsync calls fail fast
            // and release SourceWriteLock, unblocking LoadInitialStateAndResumeAsync.
            oldSession.KeepAlive -= OnKeepAlive;
            KillTransportChannel(oldSession);
            _sessionsToDispose.Enqueue(oldSession);
        }

        return newSession;
    }

    public async Task CreateSubscriptionsAsync(
        IReadOnlyList<MonitoredItem> monitoredItems,
        Session session, CancellationToken cancellationToken)
    {
        await SubscriptionManager.CreateBatchedSubscriptionsAsync(monitoredItems, session, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Created {SubscriptionCount} subscriptions with {Subscribed} " +
            "total monitored items ({Polled} via polling).",
            SubscriptionManager.SubscriptionCount,
            SubscriptionManager.MonitoredItemCount,
            PollingManager?.PollingItemCount ?? 0);
    }

    /// <summary>
    /// Handles KeepAlive events and triggers automatic reconnection when connection is lost.
    /// </summary>
    private void OnKeepAlive(ISession sender, KeepAliveEventArgs e)
    {
        if (ServiceResult.IsGood(e.Status))
        {
            return;
        }

        if (!Monitor.TryEnter(_reconnectingLock, 0))
        {
            _logger.LogDebug("OPC UA reconnect already in progress, skipping duplicate KeepAlive event.");
            return;
        }

        try
        {
            if (Volatile.Read(ref _disposed) == 1 ||
                Volatile.Read(ref _isReconnecting) == 1)
            {
                return;
            }

            var session = Volatile.Read(ref _session);
            if (session is null || !ReferenceEquals(sender, session))
            {
                return;
            }

            if (_reconnectHandler.State is not SessionReconnectHandler.ReconnectState.Ready)
            {
                _logger.LogWarning("OPC UA reconnect handler not ready. State: {State}.", _reconnectHandler.State);
                return;
            }

            _logger.LogInformation("OPC UA server connection lost. Beginning reconnect...");

            // Without this, the source would report Synchronized for the whole outage: the SDK
            // reconnect path doesn't buffer until PerformFullStateSyncIfNeededAsync runs. Not
            // StartBuffering here either (see ReportConnectionLost remarks for why).
            _source.NotifyConnectionLost();

            // Set flag before BeginReconnect to avoid window where external observers see IsReconnecting=false
            Interlocked.Exchange(ref _isReconnecting, 1);

            try
            {
                var newState = _reconnectHandler.BeginReconnect(session, (int)_configuration.ReconnectInterval.TotalMilliseconds, OnReconnectComplete);
                if (newState is SessionReconnectHandler.ReconnectState.Triggered or SessionReconnectHandler.ReconnectState.Reconnecting)
                {
                    e.CancelKeepAlive = true;
                    _source.ReconnectionMetrics.RecordAttemptStart();
                    Interlocked.Exchange(ref _pendingSdkReconnection, 1);
                }
                else
                {
                    // BeginReconnect failed - reset flag
                    Interlocked.Exchange(ref _isReconnecting, 0);
                    _logger.LogError("Failed to begin OPC UA reconnect. Handler state: {State}.", newState);
                }
            }
            catch (Exception ex)
            {
                // BeginReconnect threw - reset flag for immediate recovery instead of waiting 30s for stall detection
                Interlocked.Exchange(ref _isReconnecting, 0);
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _source.ReportBackgroundError(ex);
                }
                _logger.LogError(ex, "BeginReconnect threw unexpected exception.");
            }
        }
        finally
        {
            Monitor.Exit(_reconnectingLock);
        }
    }

    private void OnReconnectComplete(object? sender, EventArgs e)
    {
        try
        {
            lock (_reconnectingLock)
            {
                // Ignore stale callbacks from a disposed/replaced reconnect handler.
                // After ClearSessionAsync or TryForceResetIfStalled replaces _reconnectHandler,
                // the old handler's callback may still fire (async void already queued on thread pool).
                if (!ReferenceEquals(sender, _reconnectHandler))
                {
                    _logger.LogDebug("Ignoring stale reconnect callback from replaced handler.");
                    return;
                }

                if (Volatile.Read(ref _disposed) == 1)
                {
                    return;
                }

                Interlocked.Exchange(ref _pendingSdkReconnection, 0);

                var reconnectedSession = _reconnectHandler.Session as Session;
                if (reconnectedSession is null)
                {
                    _logger.LogWarning(
                        "Reconnect completed with null session. " +
                        "Clearing session to trigger full reconnection via health check.");

                    AbandonCurrentSession();
                    Interlocked.Exchange(ref _isReconnecting, 0);
                    return;
                }

                var oldSession = Volatile.Read(ref _session);
                var transferSucceeded = false;

                if (!ReferenceEquals(oldSession, reconnectedSession))
                {
                    _logger.LogInformation(
                        "Reconnect created new OPC UA session (id={SessionId}, connected={Connected}).",
                        reconnectedSession.SessionId,
                        reconnectedSession.Connected);

                    // Check if subscriptions transferred BEFORE accepting the new session,
                    // to avoid having two sessions that need disposal with only one pending slot.
                    var transferredSubscriptions = reconnectedSession.Subscriptions.ToList();
                    if (transferredSubscriptions.Count > 0)
                    {
                        // Transfer succeeded - accept new session, defer old session disposal
                        if (oldSession is not null)
                        {
                            oldSession.KeepAlive -= OnKeepAlive;
                            _sessionsToDispose.Enqueue(oldSession);
                        }

                        SetSession(reconnectedSession);
                        ConfigureSession(reconnectedSession);

                        SubscriptionManager.UpdateTransferredSubscriptions(transferredSubscriptions);

                        // Signal the health check loop to perform a full state read.
                        // We don't rely on subscription notifications alone because they can be
                        // incomplete (notification queue overflow, timing gaps between address space
                        // creation and ChangeQueueProcessor subscription on the server).
                        Interlocked.Exchange(ref _needsFullStateSync, 1);

                        _source.ReconnectionMetrics.RecordSuccess();
                        transferSucceeded = true;

                        _logger.LogInformation(
                            "OPC UA session reconnected: Transferred {Count} subscriptions. Full state sync pending.",
                            transferredSubscriptions.Count);
                    }
                    else
                    {
                        // Transfer failed - reject new session, abandon old session.
                        // The reconnected session has a live TCP connection so it must be disposed.
                        // The old session's connection is already dead (server restarted) and will be GC'd.
                        _logger.LogWarning(
                            "OPC UA session reconnected but subscription transfer failed (server restart). " +
                            "Clearing session to trigger full reconnection via health check.");
                        AbandonCurrentSession();
                        reconnectedSession.KeepAlive -= OnKeepAlive;
                        _sessionsToDispose.Enqueue(reconnectedSession);
                    }
                }
                else
                {
                    // Same Session object reference — SDK "preserved" the session.
                    // After server restart, the SDK reconnects transport but may not create a new session.
                    // The old session ID doesn't exist on the restarted server.

                    // Preserved sessions are unreliable: subscriptions may have been silently
                    // deleted by the server (lifetime expired during disconnect), leaving no
                    // mechanism for future updates. Abandon the session and let the health check
                    // trigger a full manual reconnection with fresh subscriptions + full state read.
                    _logger.LogInformation(
                        "Reconnect preserved existing OPC UA session (id={SessionId}, connected={Connected}). " +
                        "Abandoning to trigger full reconnection with state sync.",
                        reconnectedSession.SessionId,
                        reconnectedSession.Connected);
                    AbandonCurrentSession();
                }

                Interlocked.Exchange(ref _isReconnecting, 0);

                // Notifications resume as soon as the transfer succeeds, and the only other rise on
                // this path is the health check loop's, so without this the connector reports itself
                // down for seconds while values update. Must stay after the reconnecting flag was
                // cleared above, and is guarded by IsConnected rather than the branch's own knowledge
                // because the session can have dropped again since.
                if (transferSucceeded && IsConnected)
                {
                    _source.NotifySessionHealthy();
                }
            }
        }
        finally
        {
            DrainPendingSessionTransitions();
        }
    }

    /// <summary>
    /// Attempts to force-reset if reconnection is stalled.
    /// Resets the SDK reconnect handler and clears state so health check can restart fresh.
    /// </summary>
    /// <returns>True if reset was performed, false otherwise.</returns>
    internal bool TryForceResetIfStalled()
    {
        try
        {
            lock (_reconnectingLock)
            {
                if (Volatile.Read(ref _isReconnecting) == 0)
                {
                    return false;
                }

                // Reset the SDK reconnect handler to prevent it from interfering
                try
                {
                    _reconnectHandler.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error disposing stalled reconnect handler.");
                }

                _reconnectHandler = new SessionReconnectHandler(
                    _configuration.ResolvedTelemetryContext,
                    false,
                    (int)_configuration.ReconnectHandlerTimeout.TotalMilliseconds);

                // Clear everything - health check will restart fresh
                Interlocked.Exchange(ref _pendingSdkReconnection, 0);
                AbandonCurrentSession();
                Interlocked.Exchange(ref _isReconnecting, 0);
                return true;
            }
        }
        finally
        {
            DrainPendingSessionTransitions();
        }
    }

    /// <summary>
    /// Disposes all sessions queued for deferred disposal.
    /// Called by health check loop after SDK reconnection completes.
    /// </summary>
    public async Task DisposePendingSessionsAsync(CancellationToken cancellationToken)
    {
        while (_sessionsToDispose.TryDequeue(out var session))
        {
            try
            {
                await DisposeSessionAsync(session, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing old session during reconnection cleanup.");
            }
        }
    }

    /// <summary>
    /// Clears the current session and resets the reconnect handler to allow health check to trigger reconnection.
    /// Called when initialization fails after session creation.
    /// </summary>
    public async Task ClearSessionAsync(CancellationToken cancellationToken)
    {
        Session? sessionToDispose;

        lock (_reconnectingLock)
        {
            // Reset the reconnect handler to prevent it from trying to use the disposed session
            try
            {
                _reconnectHandler.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing reconnect handler during session clear.");
            }

            _reconnectHandler = new SessionReconnectHandler(
                _configuration.ResolvedTelemetryContext,
                false,
                (int)_configuration.ReconnectHandlerTimeout.TotalMilliseconds);

            Interlocked.Exchange(ref _isReconnecting, 0);

            if (Interlocked.Exchange(ref _pendingSdkReconnection, 0) == 1)
            {
                _source.ReconnectionMetrics.RecordAbandoned();
            }

            Interlocked.Exchange(ref _needsFullStateSync, 0);

            // Read and clear session inside lock to prevent race with OnReconnectComplete
            sessionToDispose = Volatile.Read(ref _session);
            SetSession(null);
            ReadAfterWriteManager?.ClearPendingReads();
        }

        DrainPendingSessionTransitions();

        if (sessionToDispose is not null)
        {
            // Try graceful close first so the server can clean up the session and its subscriptions.
            // Without this, the server has an orphaned session with active subscriptions publishing
            // to a dead transport, which can disrupt the server's publish pipeline for other clients.
            await DisposeSessionAsync(sessionToDispose, cancellationToken).ConfigureAwait(false);

            // Kill transport after close to ensure any remaining in-flight operations fail fast.
            KillTransportChannel(sessionToDispose);
        }
    }

    private void ConfigureSession(Session session)
    {
        session.TransferSubscriptionsOnReconnect = true;
        session.DeleteSubscriptionsOnClose = false;
        session.MinPublishRequestCount = _configuration.MinPublishRequestCount;
        session.KeepAlive -= OnKeepAlive;
        session.KeepAlive += OnKeepAlive;
        session.KeepAliveInterval = (int)_configuration.KeepAliveInterval.TotalMilliseconds;
    }

    /// <summary>
    /// Disposes the transport channel without clearing the session.
    /// This triggers keep-alive failure -> OnKeepAlive -> SessionReconnectHandler.BeginReconnect,
    /// exercising the SDK's session preservation/transfer reconnection path.
    /// </summary>
    internal async Task DisconnectTransportAsync(CancellationToken cancellationToken)
    {
        var session = Volatile.Read(ref _session);
        if (session is not null)
        {
            await CloseTransportChannelAsync(session, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Queues the current session for deferred disposal and clears session state
    /// so the health check loop will trigger a full reconnection. Counts as one
    /// abandoned reconnection in diagnostics — every call site reaches this method
    /// after an SDK or stall-reset attempt produced an unusable result.
    /// Must be called inside <see cref="_reconnectingLock"/>; the caller is responsible
    /// for invoking <see cref="DrainPendingSessionTransitions"/> after releasing the lock
    /// so the queued <see cref="OpcUaSubjectClientSource.CurrentSessionChanged"/> notification
    /// fires without the lock held.
    /// </summary>
    private void AbandonCurrentSession()
    {
        Debug.Assert(Monitor.IsEntered(_reconnectingLock), "AbandonCurrentSession must be called inside _reconnectingLock.");

        // The abandoned subscription's callback stays attached, so buffer until the manual reconnect
        // reloads and replays state; the authoritative reload makes discarding these updates safe.
        _propertyWriter.StartBuffering();

        var oldSession = Volatile.Read(ref _session);
        if (oldSession is not null)
        {
            oldSession.KeepAlive -= OnKeepAlive;
            KillTransportChannel(oldSession);
            _sessionsToDispose.Enqueue(oldSession);
        }

        SetSession(null);
        Interlocked.Exchange(ref _needsFullStateSync, 0);
        ReadAfterWriteManager?.ClearPendingReads();
        _source.ReconnectionMetrics.RecordAbandoned();
    }

    /// <summary>
    /// Closes a session's transport channel asynchronously.
    /// Used by fault injection (Disconnect) to avoid blocking the calling thread.
    /// Falls back to synchronous Dispose if CloseAsync hangs.
    /// </summary>
    private async Task CloseTransportChannelAsync(Session session, CancellationToken cancellationToken)
    {
        try
        {
            var channel = session.NullableTransportChannel;
            if (channel is not null)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                await channel.CloseAsync(timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error closing transport channel, falling back to synchronous dispose.");
            KillTransportChannel(session);
        }
    }

    /// <summary>
    /// Kills a session's transport channel immediately (synchronous).
    /// Used for fast failover during reconnection — makes in-flight operations fail fast.
    /// </summary>
    private void KillTransportChannel(Session session)
    {
        try
        {
            session.NullableTransportChannel?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error killing transport channel (session may already be disposed).");
        }
    }

    private async Task DisposeSessionAsync(Session session, CancellationToken cancellationToken)
    {
        // Belt-and-suspenders: callers already unsubscribe before enqueuing, but ensure
        // no stale keep-alive fires during the close/dispose sequence.
        session.KeepAlive -= OnKeepAlive;

        // Clean up orphaned subscriptions on the server when closing this session.
        // ConfigureSession sets DeleteSubscriptionsOnClose = false for SDK transfer scenarios,
        // but during manual reconnection the old session's subscriptions are orphaned and must
        // be deleted to prevent server resource exhaustion that can affect other clients.
        session.DeleteSubscriptionsOnClose = true;

        try
        {
            // Timeout prevents hang when the session's TCP connection is dead.
            // CloseAsync sends a CloseSession request to the server; on a dead connection
            // it can wait for the full operation timeout before failing.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_configuration.SessionDisposalTimeout);
            await session.CloseAsync(timeoutCts.Token).ConfigureAwait(false);
            _logger.LogDebug("OPC UA session closed successfully (id={SessionId}).", session.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error closing OPC UA session (id={SessionId}, may be expected after force-kill).", session.SessionId);
        }
        try
        {
            session.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing OPC UA session.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        lock (_reconnectingLock)
        {
            // Disposing the SDK timer does not wait for an async reconnect already in progress, whose
            // completion callback can still arrive later. Consume its outcome before disposing the
            // handler; the disposed latch makes that callback return without classifying it again.
            if (Interlocked.Exchange(ref _pendingSdkReconnection, 0) == 1)
            {
                _source.ReconnectionMetrics.RecordAbandoned();
            }

            try { _reconnectHandler.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Error disposing reconnect handler."); }
        }
        if (ReadAfterWriteManager is not null)
        {
            try { await ReadAfterWriteManager.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.LogDebug(ex, "Error disposing read-after-write manager."); }
        }
        try { await SubscriptionManager.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.LogDebug(ex, "Error disposing subscription manager."); }
        if (PollingManager is not null)
        {
            try { await PollingManager.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.LogDebug(ex, "Error disposing polling manager."); }
        }

        // Dispose queued sessions synchronously — these are abandoned sessions whose transport
        // is already dead, so graceful CloseAsync would timeout. Fast sync Dispose is intentional.
        while (_sessionsToDispose.TryDequeue(out var pendingSession))
        {
            try { pendingSession.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Error disposing pending old session."); }
        }

        var sessionToDispose = Volatile.Read(ref _session);
        if (sessionToDispose is not null)
        {
            var timeout = _configuration.SessionDisposalTimeout;
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await DisposeSessionAsync(sessionToDispose, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "Session disposal timed out after {Timeout} during shutdown. " +
                    "Forcing synchronous disposal to complete cleanup.",
                    timeout);

                // Force immediate disposal without waiting for server acknowledgment
                try { sessionToDispose.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Error force-disposing session."); }
            }

            SetSession(null);
        }

        DrainPendingSessionTransitions();
    }

    /// <summary>
    /// Atomically updates the underlying session reference and enqueues a pending notification.
    /// Spurious writes of the same instance do not enqueue. The pending notification is fired by
    /// <see cref="DrainPendingSessionTransitions"/>, which callers must invoke after releasing
    /// any locks (notably <see cref="_reconnectingLock"/>) so that user-supplied event handlers
    /// run without those locks held.
    /// </summary>
    private void SetSession(Session? newSession)
    {
        var oldSession = Interlocked.Exchange(ref _session, newSession);
        if (!ReferenceEquals(oldSession, newSession))
        {
            _pendingSessionTransitions.Enqueue((oldSession, newSession));
        }
    }

    /// <summary>
    /// Fires <see cref="OpcUaSubjectClientSource.OnCurrentSessionChanged"/> for every pending session
    /// transition. Must be called outside <see cref="_reconnectingLock"/>. Concurrent drainers
    /// serialize on <see cref="_drainLock"/>, preserving enqueue order in handler invocations.
    /// </summary>
    internal void DrainPendingSessionTransitions()
    {
        lock (_drainLock)
        {
            while (_pendingSessionTransitions.TryDequeue(out var transition))
            {
                _source.OnCurrentSessionChanged(transition.Previous, transition.Current);
            }
        }
    }

    /// <summary>
    /// Satisfies IDisposable for interface compatibility.
    /// Delegates to DisposeAsync() via fire-and-forget to ensure cleanup.
    /// SubjectSourceBase awaits IAsyncDisposable directly via 'await using', so this is never called in normal operation.
    /// </summary>
    void IDisposable.Dispose()
    {
        _logger.LogWarning("Sync Dispose() called on SessionManager - prefer DisposeAsync() for proper cleanup.");
        _ = Task.Run(async () =>
        {
            try
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during fire-and-forget disposal of SessionManager.");
            }
        });
    }
}
