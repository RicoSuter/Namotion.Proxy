using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class OpcUaSubjectServer : SubjectConnectorBase, IOpcUaSubjectServer, IFaultInjectable
{
    // Per-instance key so multiple servers can expose the same property tree without
    // overwriting each other's BaseDataVariableState reference on shared properties.
    internal string OpcUaVariableKey { get; } = "OpcUaVariable:" + Guid.NewGuid();

    // A client write reaches the node before UpdateProperty applies it, so an applied value is settled
    // here by construction, which is what SourceValuesAreSettled requires. Named once because the write
    // loop and the processor must agree: if only one of them ranked against the last commit, the other
    // would still write an older one out.
    internal const ChangeDeliveryRule DeliveryRule = ChangeDeliveryRule.SourceValuesAreSettled;

    private readonly IInterceptorSubject _subject;
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger _logger;
    private readonly OpcUaServerConfiguration _configuration;

    private volatile OpcUaStandardServer? _server;
    private int _consecutiveFailures;

    internal ThroughputCounter IncomingThroughput => Metrics.Incoming!;

    internal ThroughputCounter OutgoingThroughput => Metrics.Outgoing!;

    // Thread-scoped, not an instance field: a client write on another thread must not be caught by it.
    [ThreadStatic]
    internal static bool IsWritingOwnNodeValues;

    // The value the loop just wrote, so our own reflection is identified by what it carries rather than
    // by the flag alone. The flag says only that this thread is in the loop, which stops being the same
    // thing as soon as anything sets node.Value between our assignment and our ClearChangeMasks: the
    // flush then reports THEIR value on our thread, and dropping it loses the value permanently, since
    // the node keeps serving it to clients while the subject never receives it.
    //
    // The SDK's own write service cannot do that, because it takes the node manager lock we hold for the
    // whole batch. This is what makes the identification exact rather than a guess about who else writes.
    [ThreadStatic]
    internal static object? SelfWrittenNodeValue;

    /// <inheritdoc />
    public override IInterceptorSubject RootSubject => _subject;

    /// <inheritdoc />
    async Task IFaultInjectable.InjectFaultAsync(FaultType faultType, CancellationToken cancellationToken)
    {
        // For a multi-connection server, all fault types are treated as force-kill.
        // There's no meaningful "soft disconnect" when the server has multiple clients.
        await ForceKillCurrentAttemptAsync().ConfigureAwait(false);
    }

    /// <inheritdoc cref="SubjectConnectorBase.Diagnostics" />
    public override OpcUaServerDiagnostics Diagnostics { get; }

    /// <inheritdoc />
    public StandardServer? CurrentServer => _server;

    /// <summary>
    /// Gets the number of active sessions.
    /// </summary>
    internal int ActiveSessionCount => _server?.ActiveSessionCount ?? 0;

    /// <summary>
    /// Gets the consecutive failure count.
    /// </summary>
    internal int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    internal int RecordConsecutiveFailure() => Interlocked.Increment(ref _consecutiveFailures);

    private void ResetConsecutiveFailures() => Interlocked.Exchange(ref _consecutiveFailures, 0);

    public OpcUaSubjectServer(
        IInterceptorSubject subject,
        OpcUaServerConfiguration configuration,
        ILogger logger)
        : base(new ConnectorMetrics(new ThroughputCounter(), new ThroughputCounter()))
    {
        _subject = subject;
        _context = subject.Context;
        _logger = logger;
        _configuration = configuration;
        Diagnostics = new OpcUaServerDiagnostics(this, Metrics);
    }

    /// <inheritdoc />
    public bool TryGetVariableNode(PropertyReference property, [NotNullWhen(true)] out BaseDataVariableState? variable)
    {
        if (property.TryGetPropertyData(OpcUaVariableKey, out var data) && data is BaseDataVariableState resolved)
        {
            variable = resolved;
            return true;
        }

        variable = null;
        return false;
    }

    /// <summary>
    /// Builds the outbound processor. Extracted so a test can read back the rule it selected: asserting
    /// the constant alone would not catch a different value being inlined at the construction site,
    /// which is the mistake the constant exists to prevent.
    /// </summary>
    internal ChangeQueueProcessor CreateChangeQueueProcessor() =>
        new(source: this, _context,
            propertyFilter: IsPropertyIncluded, writeHandler: WriteChangesAsync,
            DeliveryRule,
            _configuration.BufferTime, maxQueueDepth: null, logger: _logger,
            dropHandler: Metrics.OutboundChanges.CreateDropReporter());

    private bool IsPropertyIncluded(PropertyReference propertyReference)
    {
        return propertyReference.TryGetRegisteredProperty() is { } property &&
               property.IsPropertyIncluded(_configuration.Mapper, _subject);
    }

    internal ValueTask WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        var server = _server;
        var currentInstance = server?.CurrentInstance;
        if (currentInstance == null)
        {
            return ValueTask.CompletedTask;
        }

        // Use the SDK's NodeManager.Lock for thread-safe node updates.
        // This is the same lock the SDK uses for Read/Write/subscription operations.
        // ClearChangeMasks → OnMonitoredNodeChanged also acquires this lock,
        // but Monitor is reentrant on the same thread so no deadlock.
        var nodeManagerLock = server?.NodeManagerLock;
        if (nodeManagerLock == null)
        {
            return ValueTask.CompletedTask;
        }

        var span = changes.Span;
        var written = 0;
        lock (nodeManagerLock)
        {
            IsWritingOwnNodeValues = true;
            try
            {
                for (var i = 0; i < span.Length; i++)
                {
                    var change = span[i];

                    // Decided again here rather than only when the batch was assembled: a client write
                    // takes this same lock, so one can land between the two and leave the node holding
                    // its value while we are about to overwrite it with an older commit.
                    if (ChangeDelivery.IsSuperseded(in change, DeliveryRule))
                    {
                        continue;
                    }

                    if (change.Property.TryGetPropertyData(OpcUaVariableKey, out var data) &&
                        data is BaseDataVariableState node &&
                        change.Property.TryGetRegisteredProperty() is { } registeredProperty)
                    {
                        var value = change.GetNewValue<object?>();
                        var convertedValue = _configuration.ValueConverter
                            .ConvertToNodeValue(value, registeredProperty);

                        node.Value = convertedValue;
                        node.Timestamp = change.ChangedTimestamp.UtcDateTime;
                        SelfWrittenNodeValue = convertedValue;
                        node.ClearChangeMasks(currentInstance.DefaultSystemContext, false);
                        written++;
                    }
                }
            }
            finally
            {
                IsWritingOwnNodeValues = false;
                SelfWrittenNodeValue = null;
            }
        }

        // What reached a node, not what the batch offered: a superseded or unmapped change is not traffic.
        OutgoingThroughput.Add(written);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        _context.WithRegistry();

        // Null when the context has no lifecycle interceptor, which using treats as nothing to release.
        using var detachingSubscription = SubscribeToSubjectDetaching();

        await ExecuteServerLoopAsync(stoppingToken).ConfigureAwait(false);
    }

    private IDisposable? SubscribeToSubjectDetaching()
    {
        if (_context.TryGetLifecycleInterceptor() is not { } lifecycleInterceptor)
        {
            return null;
        }

        lifecycleInterceptor.SubjectDetaching += OnSubjectDetaching;
        return new SubjectDetachingSubscription(lifecycleInterceptor, OnSubjectDetaching);
    }

    private async Task ExecuteServerLoopAsync(CancellationToken stoppingToken)
    {
        // Reset failure counter on fresh start so that accumulated failures from
        // previous stop/start cycles don't cause excessive backoff delays.
        ResetConsecutiveFailures();

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunAttemptAsync(stoppingToken, async attempt =>
            {
                var linkedToken = attempt.Token;

                var application = await _configuration.CreateApplicationInstanceAsync().ConfigureAwait(false);

                if (_configuration.CleanCertificateStore)
                {
                    CleanCertificateStore(application);
                }

                var server = new OpcUaStandardServer(_subject, this, _configuration, _logger);

                try
                {
                    try
                    {
                        _server = server;

                        // Create the ChangeQueueProcessor (and its subscription) BEFORE starting the server.
                        // This ensures property changes during OPC UA node creation are captured in the queue
                        // and not lost in the gap between node creation and processing start.
                        using var changeQueueProcessor = CreateChangeQueueProcessor();

                        // Declared after the processor so it is released first, which is what lets the
                        // next restart register its own: a second Register while one is still live throws.
                        using var outboundRegistration = Metrics.OutboundChanges.Register(
                            () => changeQueueProcessor.QueueDepth, capacity: null);

                        await application.CheckApplicationInstanceCertificatesAsync(true, ct: linkedToken).ConfigureAwait(false);
                        await application.StartAsync(server).ConfigureAwait(false);

                        ResetConsecutiveFailures();

                        // LastError is deliberately left in place: clearing it on recovery would erase
                        // the only evidence of a transient fault.
                        Metrics.MarkOperational();

                        await changeQueueProcessor.ProcessAsync(linkedToken);
                    }
                    finally
                    {
                        Metrics.MarkNotOperational();
                        var serverToClean = _server;
                        _server = null;
                        serverToClean?.ClearPropertyData();
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Normal shutdown takes priority over force-kill (checked first intentionally).
                }
                catch (OperationCanceledException) when (attempt.WasForceKilled)
                {
                    // Not reported as an error: an injected fault the server recovers from by restarting.
                    _logger.LogWarning("OPC UA server force-killed. Restarting...");
                }
                catch (Exception ex)
                {
                    // A stop tears the server down with an arbitrary exception rather than a cancellation,
                    // so only the stopping token tells a shutdown apart from a genuine fault.
                    if (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var consecutiveFailures = RecordConsecutiveFailure();

                    // Nothing outside this loop reports its failures.
                    Metrics.ReportError(ex);
                    _logger.LogError(ex, "Failed to start OPC UA server (attempt {Attempt}).", consecutiveFailures);

                    // Exponential backoff with jitter: 1s, 2s, 4s, 8s, 16s, 30s (capped) + 0-2s random jitter
                    // Jitter prevents thundering herd when multiple servers fail simultaneously
                    var baseDelay = Math.Min(Math.Pow(2, consecutiveFailures - 1), 30);
                    var jitter = Random.Shared.NextDouble() * 2;
                    await Task.Delay(TimeSpan.FromSeconds(baseDelay + jitter), stoppingToken);
                }
                finally
                {
                    try
                    {
                        if (attempt.WasForceKilled)
                        {
                            // Force-kill: close transport listeners immediately so clients see
                            // an abrupt connection loss (realistic crash simulation).
                            if (application.Server is OpcUaStandardServer s)
                            {
                                s.CloseTransportListeners();
                            }
                        }

                        // Always run ShutdownServerAsync to ensure the SDK's internal tasks
                        // (SubscriptionManager publish/refresh threads) are properly signaled
                        // to exit via OnServerStoppingAsync. Without StopAsync, these
                        // fire-and-forget tasks keep the entire server object graph alive as
                        // GC roots, causing ~8-16 MB leak per server restart.
                        // On force-kill the transport is already dead, so this only cleans up
                        // internal state and does not change what clients observe.
                        await ShutdownServerAsync(application).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to shutdown OPC UA server.");
                    }
                    finally
                    {
                        try { server.Dispose(); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Error disposing OPC UA server."); }
                    }
                }
            }).ConfigureAwait(false);
        }
    }

    private async Task ShutdownServerAsync(ApplicationInstance application)
    {
        try
        {
            if (application.Server is OpcUaStandardServer server)
            {
                // Close transport listeners first to stop accepting new connections.
                // Without this, clients reconnect during shutdown faster than sessions
                // can be closed, causing StopAsync to hang indefinitely.
                server.CloseTransportListeners();

                if (server.CurrentInstance?.SessionManager is { } sessionManager)
                {
                    var sessions = sessionManager.GetSessions();
                    foreach (var session in sessions)
                    {
                        try { session.Close(); } catch (Exception ex) { _logger.LogDebug(ex, "Error closing session during shutdown."); }
                    }
                }
            }

            // Timeout prevents hang when clients keep reconnecting during shutdown
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await application.StopAsync().AsTask().WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                _logger.LogWarning("OPC UA server shutdown timed out after 10s. Continuing with disposal.");
            }
        }
        catch (ServiceResultException e) when (e.StatusCode == StatusCodes.BadServerHalted)
        {
            // Server already halted
        }
    }

    private void CleanCertificateStore(ApplicationInstance application)
    {
        var path = application
            .ApplicationConfiguration
            .SecurityConfiguration
            .ApplicationCertificate
            .StorePath;

        if (string.IsNullOrEmpty(path))
        {
            _logger.LogWarning("Certificate store path is empty, skipping cleanup.");
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
                _logger.LogDebug("Cleaned certificate store at {Path}.", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean certificate store at {Path}. Continuing with existing certificates.", path);
        }
    }

    internal void UpdateProperty(PropertyReference property, DateTimeOffset changedTimestamp, object? value)
    {
        if (IsWritingOwnNodeValues && Equals(value, SelfWrittenNodeValue))
        {
            // Our own node write, reflected back synchronously by ClearChangeMasks. Compared by value
            // rather than by the flag alone, for the reason on the field above.
            return;
        }

        IncomingThroughput.Add(1);
        var receivedTimestamp = DateTimeOffset.UtcNow;

        var registeredProperty = property.TryGetRegisteredProperty();
        if (registeredProperty is not null)
        {
            var convertedValue = _configuration.ValueConverter.ConvertToPropertyValue(value, registeredProperty);

            try
            {
                property.SetValueFromSource(this, changedTimestamp, receivedTimestamp, convertedValue);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to apply property update from OPC UA client.");
            }
        }
    }

    private void OnSubjectDetaching(SubjectLifecycleChange change)
    {
        _server?.RemoveSubjectNodes(change.Subject);
    }

    private sealed class SubjectDetachingSubscription(
        LifecycleInterceptor lifecycleInterceptor, Action<SubjectLifecycleChange> handler) : IDisposable
    {
        public void Dispose() => lifecycleInterceptor.SubjectDetaching -= handler;
    }
}
