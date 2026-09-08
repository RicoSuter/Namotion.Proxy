using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Packets;
using MQTTnet.Server;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Mqtt.Server;

/// <summary>
/// Background service that hosts an MQTT broker and publishes property changes.
/// </summary>
public class MqttSubjectServer : SubjectConnectorBase, IFaultInjectable, IAsyncDisposable
{
    private sealed class RunClientCounter
    {
        internal int Count;
    }

    // NOTE: We cannot pool UserProperties here because InjectApplicationMessages queues messages
    // asynchronously. The server may still be serializing packets after this method returns,
    // which would cause a race condition if we returned the lists to a pool.

    private readonly string _serverClientId;
    private readonly IInterceptorSubject _subject;
    private readonly IInterceptorSubjectContext _context;
    private readonly MqttServerConfiguration _configuration;
    private readonly ILogger _logger;

    /// <inheritdoc />
    public override IInterceptorSubject RootSubject => _subject;

    // Per-instance sentinel source used for values received from MQTT clients.
    // Using a different source than `this` ensures the server's ChangeQueueProcessor
    // re-publishes client-originated values to all subscribers (server-authoritative relay).
    private readonly object _mqttClientSource = new();

    private readonly ConcurrentDictionary<PropertyReference, (string? Topic, MqttPropertyMapping? Mapping)> _propertyToTopic = new();
    private readonly ConcurrentDictionary<string, PropertyReference?> _pathToProperty = new();

    private List<Task>? _runningInitialStateTasks;
    private readonly Lock _initialStateTasksLock = new();

    // Serializes WriteChangesAsync and PublishInitialStateAsync so initial state reads+publishes can't interleave with CQP flushes.
    private readonly SemaphoreSlim _publishSemaphore = new(1, 1);

    private int _disposed;
    private MqttServer? _mqttServer;
    private volatile RunClientCounter? _currentClientCounter;

    /// <inheritdoc cref="SubjectConnectorBase.Diagnostics" />
    public override MqttServerDiagnostics Diagnostics { get; }

    /// <summary>
    /// Gets the number of clients currently connected to the broker.
    /// </summary>
    internal int ConnectedClientCount
    {
        get
        {
            var clientCounter = _currentClientCounter;
            return clientCounter is null
                ? 0
                : Math.Max(0, Volatile.Read(ref clientCounter.Count));
        }
    }

    public MqttSubjectServer(
        IInterceptorSubject subject,
        MqttServerConfiguration configuration,
        ILogger<MqttSubjectServer> logger)
        : base(new ConnectorMetrics())
    {
        _subject = subject ?? throw new ArgumentNullException(nameof(subject));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _context = subject.Context;
        _serverClientId = _configuration.ClientId;

        Diagnostics = new MqttServerDiagnostics(this, Metrics);

        configuration.Validate();
    }

    private bool IsPropertyIncluded(PropertyReference propertyReference) =>
        propertyReference.TryGetRegisteredProperty() is { } property &&
        _configuration.Mapper.TryGetMapping(property, _subject, out _);

    /// <inheritdoc />
    async Task IFaultInjectable.InjectFaultAsync(FaultType faultType, CancellationToken cancellationToken)
    {
        switch (faultType)
        {
            case FaultType.Kill:
                await ForceKillCurrentAttemptAsync().ConfigureAwait(false);
                break;

            case FaultType.Disconnect:
                var server = _mqttServer;
                if (server is not null)
                {
                    var clientStatuses = await server.GetClientsAsync().ConfigureAwait(false);
                    var disconnectOptions = new MqttServerClientDisconnectOptions();
                    foreach (var clientStatus in clientStatuses)
                    {
                        try
                        {
                            await server.DisconnectClientAsync(clientStatus.Id, disconnectOptions).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Error disconnecting MQTT client {ClientId} during fault injection.", clientStatus.Id);
                        }
                    }
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(faultType), faultType, null);
        }
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        var optionsBuilder = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(_configuration.BrokerPort)
            .WithMaxPendingMessagesPerClient(_configuration.MaxPendingMessagesPerClient);

        if (!string.IsNullOrEmpty(_configuration.BrokerHost))
        {
            var boundAddress = System.Net.IPAddress.Parse(_configuration.BrokerHost);
            optionsBuilder.WithDefaultEndpointBoundIPAddress(boundAddress);
        }

        var options = optionsBuilder.Build();
        var server = new MqttServerFactory().CreateMqttServer(options);
        var lifecycleInterceptor = _context.TryGetLifecycleInterceptor();
        var shutdownCts = new CancellationTokenSource();
        var shutdownToken = shutdownCts.Token;
        var initialStateTasks = new List<Task>();
        var clientCounter = new RunClientCounter();
        var publishLease = new ConnectorCommitLease();

        Task ClientConnectedForRunAsync(ClientConnectedEventArgs args) =>
            ClientConnectedAsync(args, server, shutdownToken, initialStateTasks, clientCounter);

        Task ClientDisconnectedForRunAsync(ClientDisconnectedEventArgs args) =>
            ClientDisconnectedAsync(args, clientCounter);

        Task InterceptingPublishForRunAsync(InterceptingPublishEventArgs args) =>
            InterceptingPublishAsync(args, server, shutdownToken, publishLease);

        _mqttServer = server;
        _currentClientCounter = clientCounter;
        lock (_initialStateTasksLock)
        {
            _runningInitialStateTasks = initialStateTasks;
        }

        if (lifecycleInterceptor is not null)
        {
            lifecycleInterceptor.SubjectDetaching += OnSubjectDetaching;
        }

        server.ClientConnectedAsync += ClientConnectedForRunAsync;
        server.ClientDisconnectedAsync += ClientDisconnectedForRunAsync;
        server.InterceptingPublishAsync += InterceptingPublishForRunAsync;

        try
        {
            var stopRequested = false;
            while (!stoppingToken.IsCancellationRequested && !stopRequested)
            {
                await RunAttemptAsync(stoppingToken, async attempt =>
                {
                    var linkedToken = attempt.Token;

                    try
                    {
                        await server.StartAsync().ConfigureAwait(false);
                        Metrics.MarkOperational();

                        _logger.LogInformation("MQTT server started on port {Port}.", _configuration.BrokerPort);

                        try
                        {
                            using var changeQueueProcessor = CreateChangeQueueProcessor();

                            // Declared after the processor so it is released first, which is what lets the
                            // next restart register its own: a second Register while one is still live throws.
                            using var outboundRegistration = Metrics.OutboundChanges.Register(
                                () => changeQueueProcessor.QueueDepth, capacity: null);

                            await changeQueueProcessor.ProcessAsync(linkedToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            if (!stoppingToken.IsCancellationRequested)
                            {
                                await server.StopAsync().ConfigureAwait(false);
                            }

                            Metrics.MarkNotOperational();
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        Metrics.MarkNotOperational();
                        stopRequested = true;
                    }
                    catch (OperationCanceledException) when (attempt.WasForceKilled)
                    {
                        // Not reported as an error: an injected fault the broker recovers from by restarting.
                        Metrics.MarkNotOperational();
                        _logger.LogWarning("MQTT server force-killed. Restarting...");
                    }
                    catch (Exception ex)
                    {
                        Metrics.MarkNotOperational();

                        if (stoppingToken.IsCancellationRequested || Volatile.Read(ref _disposed) == 1)
                        {
                            stopRequested = true;
                            return;
                        }

                        // MQTTnet latches its started flag before binding, so a start that failed on the
                        // bind leaves the broker claiming to be started and every retry would then fail
                        // as "already started", hiding the genuine error. Stop it to release the latch.
                        if (server.IsStarted)
                        {
                            try
                            {
                                await server.StopAsync().ConfigureAwait(false);
                            }
                            catch (Exception stopException)
                            {
                                _logger.LogWarning(stopException, "Error stopping half-started MQTT server before retry.");
                            }
                        }

                        // Nothing outside this loop reports its failures.
                        Metrics.ReportError(ex);
                        _logger.LogError(ex, "Error in MQTT server.");

                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            await CleanupRunAsync(
                server,
                lifecycleInterceptor,
                ClientConnectedForRunAsync,
                ClientDisconnectedForRunAsync,
                InterceptingPublishForRunAsync,
                shutdownCts,
                initialStateTasks,
                clientCounter,
                publishLease).ConfigureAwait(false);
        }
    }

    private async Task CleanupRunAsync(
        MqttServer server,
        LifecycleInterceptor? lifecycleInterceptor,
        Func<ClientConnectedEventArgs, Task> clientConnectedHandler,
        Func<ClientDisconnectedEventArgs, Task> clientDisconnectedHandler,
        Func<InterceptingPublishEventArgs, Task> interceptingPublishHandler,
        CancellationTokenSource shutdownCts,
        List<Task> initialStateTasks,
        RunClientCounter clientCounter,
        ConnectorCommitLease publishLease)
    {
        try
        {
            await shutdownCts.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A callback registered on the token can throw out of the cancel; skipping the rest of
            // this cleanup would leave handlers attached and the port bound.
            _logger.LogWarning(ex, "Error cancelling MQTT server shutdown token.");
        }

        if (lifecycleInterceptor is not null)
        {
            lifecycleInterceptor.SubjectDetaching -= OnSubjectDetaching;
        }

        server.ClientConnectedAsync -= clientConnectedHandler;
        server.ClientDisconnectedAsync -= clientDisconnectedHandler;
        server.InterceptingPublishAsync -= interceptingPublishHandler;

        await publishLease.RetireAsync().ConfigureAwait(false);

        Task[] tasksSnapshot;
        lock (_initialStateTasksLock)
        {
            tasksSnapshot = initialStateTasks.ToArray();
        }

        try
        {
            await Task.WhenAll(tasksSnapshot).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
        {
            // Expected for work that cancellation reached before its delegate started.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error waiting for initial state tasks to complete.");
        }

        if (server.IsStarted)
        {
            try
            {
                await server.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping MQTT server.");
            }
        }

        try
        {
            server.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing MQTT server.");
        }

        if (ReferenceEquals(_mqttServer, server))
        {
            _mqttServer = null;
        }

        lock (_initialStateTasksLock)
        {
            if (ReferenceEquals(_runningInitialStateTasks, initialStateTasks))
            {
                _runningInitialStateTasks = null;
            }
        }

        if (ReferenceEquals(_currentClientCounter, clientCounter))
        {
            _currentClientCounter = null;
        }

        _propertyToTopic.Clear();
        _pathToProperty.Clear();
        Metrics.MarkNotOperational();
        shutdownCts.Dispose();
    }

    private async ValueTask WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        var length = changes.Length;
        if (length == 0) return;

        var server = _mqttServer;
        if (server is null) return;

        await _publishSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var messagesPool = ArrayPool<InjectedMqttApplicationMessage>.Shared;
            var messages = messagesPool.Rent(length);
            var messageCount = 0;

            try
            {
                var changesSpan = changes.Span;
                var timestampPropertyName = _configuration.SourceTimestampPropertyName;

                // Build all messages first
                for (var i = 0; i < length; i++)
                {
                    var change = changesSpan[i];
                    var registeredProperty = change.Property.TryGetRegisteredProperty();
                    if (registeredProperty is not { CanContainSubjects: false })
                    {
                        continue;
                    }

                    var (topic, mapping) = TryGetTopicForProperty(change.Property, registeredProperty);
                    if (topic is null) continue;

                    byte[] payload;
                    try
                    {
                        payload = _configuration.ValueConverter.Serialize(
                            change.GetNewValue<object?>(),
                            registeredProperty.Type);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to serialize value for topic {Topic}.", topic);
                        continue;
                    }

                    var message = new MqttApplicationMessage
                    {
                        Topic = topic,
                        PayloadSegment = new ArraySegment<byte>(payload),
                        QualityOfServiceLevel = mapping?.QualityOfService ?? _configuration.DefaultQualityOfService,
                        Retain = mapping?.Retain ?? _configuration.UseRetainedMessages
                    };

                    if (timestampPropertyName is not null)
                    {
                        message.UserProperties =
                        [
                            new MqttUserProperty(
                                timestampPropertyName,
                                _configuration.SourceTimestampSerializer(change.ChangedTimestamp))
                        ];
                    }

                    messages[messageCount++] = new InjectedMqttApplicationMessage(message)
                    {
                        SenderClientId = _serverClientId
                    };
                }

                if (messageCount > 0)
                {
#if USE_LOCAL_MQTTNET
                    await server.InjectApplicationMessagesAsync(
                        new ArraySegment<InjectedMqttApplicationMessage>(messages, 0, messageCount),
                        cancellationToken).ConfigureAwait(false);
#else
                    for (var i = 0; i < messageCount; i++)
                    {
                        await server.InjectApplicationMessage(messages[i], cancellationToken).ConfigureAwait(false);
                    }
#endif
                }
            }
            finally
            {
                messagesPool.Return(messages);
            }
        }
        finally
        {
            _publishSemaphore.Release();
        }
    }

    internal (string? Topic, MqttPropertyMapping? Mapping) TryGetTopicForProperty(PropertyReference propertyReference, RegisteredSubjectProperty property)
    {
        if (_propertyToTopic.TryGetValue(propertyReference, out var cached))
        {
            return cached;
        }

        string? topic = null;
        MqttPropertyMapping? resolvedMapping = null;
        if (_configuration.Mapper.TryGetMapping(property, _subject, out var mapping) && mapping.Topic is not null)
        {
            topic = MqttHelper.BuildTopic(mapping.Topic, _configuration.TopicPrefix);
            resolvedMapping = mapping;
        }

        var entry = (topic, resolvedMapping);

        // Add first, then validate (guarantees no memory leak)
        if (_propertyToTopic.TryAdd(propertyReference, entry) && !IsRetainable(propertyReference.Subject))
        {
            _propertyToTopic.TryRemove(propertyReference, out _);
        }

        return entry;
    }

    internal async ValueTask<PropertyReference?> TryGetPropertyForTopicAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (_pathToProperty.TryGetValue(path, out var cachedProperty))
        {
            return cachedProperty;
        }

        var registered = _subject.TryGetRegisteredSubject();
        var property = registered is null
            ? null
            : await _configuration.Mapper.TryGetPropertyAsync(new MqttLookupKey(path), registered, cancellationToken).ConfigureAwait(false);
        var propertyReference = property?.Reference;

        // Add first, then validate (guarantees no memory leak)
        if (_pathToProperty.TryAdd(path, propertyReference) &&
            propertyReference is { } resolvedProperty &&
            !IsRetainable(resolvedProperty.Subject))
        {
            _pathToProperty.TryRemove(path, out _);
        }

        return propertyReference;
    }

    /// <summary>
    /// Whether a cache entry for a property of <paramref name="subject"/> may stay. The reference count
    /// drops before LifecycleInterceptor.SubjectDetaching fires, where the eviction scan runs, and the registry
    /// deregisters only after that. Only the count catches a lookup that inserts in between. The connector root is
    /// exempt: it is anchored to the context rather than to a property, so its count is zero for its whole life.
    /// </summary>
    private bool IsRetainable(IInterceptorSubject subject)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        return registeredSubject is not null &&
            (registeredSubject.ReferenceCount > 0 || ReferenceEquals(subject, _subject));
    }

    private Task ClientConnectedAsync(
        ClientConnectedEventArgs arg,
        MqttServer server,
        CancellationToken shutdownToken,
        List<Task> initialStateTasks,
        RunClientCounter clientCounter)
    {
        var count = Interlocked.Increment(ref clientCounter.Count);
        _logger.LogInformation("Client {ClientId} connected. Total clients: {Count}.", arg.ClientId, count);

        // Publish all current property values to new client
        lock (_initialStateTasksLock)
        {
            if (shutdownToken.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            var task = Task.Run(async () =>
            {
                try
                {
                    await PublishInitialStateAsync(server, shutdownToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish initial state to client {ClientId}.", arg.ClientId);
                }
            }, shutdownToken);

            // Clean up completed tasks to prevent memory leak
            initialStateTasks.RemoveAll(t => t.IsCompleted);
            initialStateTasks.Add(task);
        }

        return Task.CompletedTask;
    }

    private async Task PublishInitialStateAsync(MqttServer server, CancellationToken cancellationToken)
    {
        try
        {
            // Wait for the client to complete subscription setup before sending initial values.
            // This delay is configurable; set to zero to rely on retained messages only.
            var delay = _configuration.InitialStateDelay;
            if (delay <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            var allProperties = _subject
                .TryGetRegisteredSubject()?
                .GetAllProperties()
                .Where(p => !p.CanContainSubjects);

            if (allProperties is null) return;

            var properties = allProperties
                .Select(p => (property: p, hasMapping: _configuration.Mapper.TryGetMapping(p, _subject, out var m), mapping: m))
                .Where(x => x.hasMapping && x.mapping!.Topic is not null)
                .Select(x => (path: x.mapping!.Topic!, property: x.property, mapping: x.mapping!));

            await _publishSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var timestampPropertyName = _configuration.SourceTimestampPropertyName;

                foreach (var (path, property, mapping) in properties)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var topic = MqttHelper.BuildTopic(path, _configuration.TopicPrefix);

                    byte[] payload;
                    try
                    {
                        payload = _configuration.ValueConverter.Serialize(
                            property.GetValue(),
                            property.Type);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to serialize initial value for topic {Topic}.", topic);
                        continue;
                    }

                    var message = new MqttApplicationMessage
                    {
                        Topic = topic,
                        PayloadSegment = new ArraySegment<byte>(payload),
                        QualityOfServiceLevel = mapping.QualityOfService ?? _configuration.DefaultQualityOfService,
                        Retain = mapping.Retain ?? _configuration.UseRetainedMessages
                    };

                    if (timestampPropertyName is not null)
                    {
                        var writeTimestamp = property.Reference.TryGetWriteTimestamp();
                        if (writeTimestamp.HasValue)
                        {
                            message.UserProperties =
                            [
                                new MqttUserProperty(
                                    timestampPropertyName,
                                    _configuration.SourceTimestampSerializer(writeTimestamp.Value))
                            ];
                        }
                    }

                    await server.InjectApplicationMessage(
                        new InjectedMqttApplicationMessage(message) { SenderClientId = _serverClientId },
                        cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _publishSemaphore.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown requested, stop publishing initial state
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish initial state to client.");
        }
    }

    private async Task InterceptingPublishAsync(
        InterceptingPublishEventArgs args,
        MqttServer server,
        CancellationToken shutdownToken,
        ConnectorCommitLease publishLease)
    {
        // Skip messages published by this server (injected messages may have null/empty ClientId)
        if (string.IsNullOrEmpty(args.ClientId) || args.ClientId == _serverClientId)
        {
            return;
        }

        if (shutdownToken.IsCancellationRequested || args.CancellationToken.IsCancellationRequested)
        {
            args.ProcessPublish = false;
            return;
        }

        var topic = args.ApplicationMessage.Topic;

        // Isolate per-message failures: a bad message must not escape into the broker's publish pipeline.
        // The client token flows through mapping; run cancellation is checked between external work.
        try
        {
            var path = MqttHelper.StripTopicPrefix(topic, _configuration.TopicPrefix);

            var resolvedPropertyReference = await TryGetPropertyForTopicAsync(path, args.CancellationToken).ConfigureAwait(false);

            if (shutdownToken.IsCancellationRequested || args.CancellationToken.IsCancellationRequested)
            {
                args.ProcessPublish = false;
                return;
            }

            if (resolvedPropertyReference is not { } propertyReference)
            {
                return;
            }

            var registeredProperty = propertyReference.TryGetRegisteredProperty();
            if (registeredProperty is null)
            {
                return;
            }

            // Server-authoritative relay: prevent the broker from distributing this client
            // message directly to other subscribers. Instead, we apply the value locally with
            // a non-self source (_mqttClientSource) so the server's ChangeQueueProcessor picks
            // it up and re-publishes it to all clients via InjectApplicationMessage.
            // This ensures consistent ordering of all values through the server.
            args.ProcessPublish = false;

            try
            {
                var payload = args.ApplicationMessage.Payload;
                var value = _configuration.ValueConverter.Deserialize(payload, registeredProperty.Type);

                var receivedTimestamp = DateTimeOffset.UtcNow;
                var sourceTimestamp = MqttHelper.ExtractSourceTimestamp(
                    args.ApplicationMessage.UserProperties,
                    _configuration.SourceTimestampPropertyName,
                    _configuration.SourceTimestampDeserializer) ?? receivedTimestamp;

                if (shutdownToken.IsCancellationRequested ||
                    args.CancellationToken.IsCancellationRequested ||
                    !publishLease.TryAcquireCommit())
                {
                    return;
                }

                try
                {
                    if (ReferenceEquals(_mqttServer, server))
                    {
                        propertyReference.SetValueFromSource(
                            _mqttClientSource,
                            sourceTimestamp,
                            receivedTimestamp,
                            value);
                    }
                }
                finally
                {
                    publishLease.ReleaseCommit();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize MQTT payload for topic {Topic}.", topic);
            }
        }
        catch (Exception ex)
        {
            if (shutdownToken.IsCancellationRequested || args.CancellationToken.IsCancellationRequested)
            {
                args.ProcessPublish = false;
            }

            _logger.LogError(ex, "Failed to handle MQTT message for topic {Topic}.", topic);
        }
    }

    private Task ClientDisconnectedAsync(ClientDisconnectedEventArgs arg, RunClientCounter clientCounter)
    {
        var count = Interlocked.Decrement(ref clientCounter.Count);
        _logger.LogInformation("Client {ClientId} disconnected. Total clients: {Count}.", arg.ClientId, count);
        return Task.CompletedTask;
    }

    private void OnSubjectDetaching(SubjectLifecycleChange change)
    {
        // TODO(perf): O(n) scan over all cached entries per detached subject.
        // Consider adding a reverse index (Dictionary<IInterceptorSubject, List<PropertyReference>>) for O(1) cleanup
        // if profiling shows this as a bottleneck with large object graphs and frequent attach/detach cycles.
        foreach (var kvp in _propertyToTopic)
        {
            if (kvp.Key.Subject == change.Subject)
            {
                _propertyToTopic.TryRemove(kvp.Key, out _);
            }
        }

        foreach (var kvp in _pathToProperty)
        {
            if (kvp.Value?.Subject == change.Subject)
            {
                _pathToProperty.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);

        // Clear caches to allow GC of subject references
        _propertyToTopic.Clear();
        _pathToProperty.Clear();

        _publishSemaphore.Dispose();
        Dispose();
    }

    internal Task[] GetRunningInitialStateTasksSnapshot()
    {
        lock (_initialStateTasksLock)
        {
            return _runningInitialStateTasks?.ToArray() ?? [];
        }
    }

    /// <summary>
    /// Builds the outbound processor. Extracted so the delivery rule it selects can be pinned by a test:
    /// choosing the wrong one is silent, so "it compiles" is not evidence that it chose correctly.
    /// </summary>
    internal ChangeQueueProcessor CreateChangeQueueProcessor() =>
        new(source: this,
            _context,
            propertyFilter: IsPropertyIncluded,
            writeHandler: WriteChangesAsync,
            // Safe only because inbound client messages are applied under _mqttClientSource rather than
            // this, so none of them is skipped here as our own echo and every superseding value is
            // relayed on. Applying them under this would break it.
            ChangeDeliveryRule.SourceValuesAreSettled,
            _configuration.BufferTime,
            maxQueueDepth: null,
            logger: _logger,
            dropHandler: Metrics.OutboundChanges.CreateDropReporter());
}
