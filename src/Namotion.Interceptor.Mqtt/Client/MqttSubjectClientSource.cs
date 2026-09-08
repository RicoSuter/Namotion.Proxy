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
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Performance;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Mqtt.Client;

/// <summary>
/// MQTT client source that subscribes to an MQTT broker and synchronizes properties.
/// </summary>
internal sealed class MqttSubjectClientSource : SubjectSourceBase, IFaultInjectable, IAsyncDisposable
{
    // Pool for UserProperties lists to avoid allocations on hot path
    private static readonly ObjectPool<List<MqttUserProperty>> UserPropertiesPool
        = new(() => new List<MqttUserProperty>(1));

    private readonly IInterceptorSubject _subject;
    private readonly MqttClientConfiguration _configuration;
    private readonly ILogger _logger;

    private readonly MqttClientFactory _factory;

    private readonly ConcurrentDictionary<string, PropertyReference?> _topicToProperty = new();
    private readonly ConcurrentDictionary<PropertyReference, (string? Topic, MqttPropertyMapping? Mapping)> _propertyToTopic = new();

    private readonly SourceOwnershipManager _ownership;

    // Publishes and retires the client/ownership pair atomically. Never held across an await or a
    // property commit, so user interceptors cannot participate in this lock order.
    private readonly Lock _transportPublicationLock = new();

    private volatile IMqttClient? _client;
    private volatile ConnectorCommitLease? _transportOwnership;
    private volatile Func<MqttApplicationMessageReceivedEventArgs, Task>? _applicationMessageHandler;
    private volatile SubjectPropertyWriter? _propertyWriter;
    private volatile MqttConnectionMonitor? _connectionMonitor;

    private int _disposed;

    public MqttSubjectClientSource(
        IInterceptorSubject subject,
        MqttClientConfiguration configuration,
        ILogger<MqttSubjectClientSource> logger)
        : base(subject.Context, logger, configuration.BufferTime, configuration.RetryTime, configuration.WriteRetryQueueSize)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _subject = subject;
        _configuration = configuration;
        _logger = logger;

        _factory = new MqttClientFactory();
        _ownership = new SourceOwnershipManager(
            this,
            onSubjectDetaching: CleanupTopicCachesForSubject);

        Metrics.RegisterClaimedProperties(() => _ownership.Count);

        configuration.Validate();
    }

    internal SourceOwnershipManager Ownership => _ownership;

    private void CleanupTopicCachesForSubject(IInterceptorSubject subject)
    {
        // Clean up topic caches for the detached subject
        // TODO(perf): O(n) scan over all cached entries per detached subject.
        // Consider adding a reverse index for O(1) cleanup if profiling shows this as a bottleneck.
        foreach (var kvp in _propertyToTopic)
        {
            if (kvp.Key.Subject == subject)
            {
                _propertyToTopic.TryRemove(kvp.Key, out _);
            }
        }

        foreach (var kvp in _topicToProperty)
        {
            if (kvp.Value?.Subject == subject)
            {
                _topicToProperty.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <inheritdoc />
    public override IInterceptorSubject RootSubject => _subject;

    /// <inheritdoc />
    public override int WriteBatchSize => 0; // No server-imposed limit for MQTT

    /// <inheritdoc />
    protected override async Task<IAsyncDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        _propertyWriter = propertyWriter;

        IMqttClient? client = null;
        MqttConnectionMonitor? connectionMonitor = null;
        Func<MqttApplicationMessageReceivedEventArgs, Task>? applicationMessageHandler = null;
        ConnectorCommitLease? transportOwnership = null;
        try
        {
            (client, connectionMonitor, applicationMessageHandler, transportOwnership) =
                await CreateMqttConnectionAsync(cancellationToken).ConfigureAwait(false);
            Metrics.MarkOperational();
            await SubscribeToPropertiesAsync(cancellationToken).ConfigureAwait(false);

            var clientForLifetime = client;
            var monitorForLifetime = connectionMonitor;
            var applicationMessageHandlerForLifetime = applicationMessageHandler;
            var transportOwnershipForLifetime = transportOwnership;
            return BackgroundTaskLifetime.Start(
                cancellationToken,
                _logger,
                ct => RunMonitorWithKillRestartAsync(
                    clientForLifetime,
                    monitorForLifetime,
                    applicationMessageHandlerForLifetime,
                    transportOwnershipForLifetime,
                    ct),
                () => DisposeMqttConnectionAsync(
                    _client,
                    _connectionMonitor,
                    _applicationMessageHandler,
                    _transportOwnership,
                    clearPropertyWriter: true));
        }
        catch
        {
            await DisposeMqttConnectionAsync(
                client,
                connectionMonitor,
                applicationMessageHandler,
                transportOwnership,
                clearPropertyWriter: true).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<(
        IMqttClient Client,
        MqttConnectionMonitor Monitor,
        Func<MqttApplicationMessageReceivedEventArgs, Task> ApplicationMessageHandler,
        ConnectorCommitLease TransportOwnership)> CreateMqttConnectionAsync(
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Connecting to MQTT broker at {Host}:{Port}.",
            _configuration.BrokerHost,
            _configuration.BrokerPort);

        IMqttClient? client = null;
        Func<MqttApplicationMessageReceivedEventArgs, Task>? applicationMessageHandler = null;
        ConnectorCommitLease? transportOwnership = null;
        try
        {
            client = _factory.CreateMqttClient();
            transportOwnership = new ConnectorCommitLease();
            applicationMessageHandler = e => OnMessageReceivedAsync(client, transportOwnership, e);
            client.ApplicationMessageReceivedAsync += applicationMessageHandler;
            client.DisconnectedAsync += OnDisconnectedAsync;

            lock (_transportPublicationLock)
            {
                _client = client;
                _transportOwnership = transportOwnership;
                _applicationMessageHandler = applicationMessageHandler;
            }

            await client.ConnectAsync(GetClientOptions(), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Connected to MQTT broker successfully.");

            var connectionMonitor = new MqttConnectionMonitor(
                client,
                _configuration,
                GetClientOptions,
                async ct => await OnReconnectedAsync(ct).ConfigureAwait(false),
                () =>
                {
                    Metrics.MarkNotOperational();
                    _propertyWriter?.StartBuffering();
                    return Task.CompletedTask;
                },
                Metrics.ReportError,
                _logger);
            _connectionMonitor = connectionMonitor;
            return (client, connectionMonitor, applicationMessageHandler, transportOwnership);
        }
        catch
        {
            await DisposeMqttConnectionAsync(
                client,
                connectionMonitor: null,
                applicationMessageHandler,
                transportOwnership,
                clearPropertyWriter: false)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async Task RunMonitorWithKillRestartAsync(
        IMqttClient initialClient,
        MqttConnectionMonitor initialConnectionMonitor,
        Func<MqttApplicationMessageReceivedEventArgs, Task> initialApplicationMessageHandler,
        ConnectorCommitLease initialTransportOwnership,
        CancellationToken stoppingToken)
    {
        // stoppingToken (from the lifetime) breaks out for good on host shutdown
        // or when the listen lifetime is disposed by the base retry path.
        var client = initialClient;
        var connectionMonitor = initialConnectionMonitor;
        var applicationMessageHandler = initialApplicationMessageHandler;
        var transportOwnership = initialTransportOwnership;
        while (!stoppingToken.IsCancellationRequested)
        {
            var wasForceKilled = false;

            await RunAttemptAsync(stoppingToken, async attempt =>
            {
                try
                {
                    await connectionMonitor.MonitorConnectionAsync(attempt.Token).ConfigureAwait(false);
                    wasForceKilled = attempt.WasForceKilled;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                }
                catch (OperationCanceledException) when (attempt.WasForceKilled)
                {
                    wasForceKilled = true;
                }
            }).ConfigureAwait(false);

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (!wasForceKilled)
            {
                continue;
            }

            _logger.LogWarning("MQTT client force-killed. Replacing the transport connection...");
            Metrics.MarkNotOperational();
            _propertyWriter?.StartBuffering();
            await DisposeMqttConnectionAsync(
                client,
                connectionMonitor,
                applicationMessageHandler,
                transportOwnership,
                clearPropertyWriter: false)
                .ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_configuration.ReconnectDelay, stoppingToken).ConfigureAwait(false);
                    (client, connectionMonitor, applicationMessageHandler, transportOwnership) =
                        await CreateMqttConnectionAsync(stoppingToken).ConfigureAwait(false);
                    await SubscribeToPropertiesAsync(stoppingToken).ConfigureAwait(false);

                    if (_propertyWriter is not null)
                    {
                        await _propertyWriter.LoadInitialStateAndResumeAsync(stoppingToken).ConfigureAwait(false);
                    }

                    Metrics.MarkOperational();
                    break;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Metrics.ReportError(exception);
                    _logger.LogError(exception, "Failed to replace the force-killed MQTT client connection.");
                    await DisposeMqttConnectionAsync(
                        _client,
                        _connectionMonitor,
                        _applicationMessageHandler,
                        _transportOwnership,
                        clearPropertyWriter: false)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    private async ValueTask DisposeMqttConnectionAsync(
        IMqttClient? client,
        MqttConnectionMonitor? connectionMonitor,
        Func<MqttApplicationMessageReceivedEventArgs, Task>? applicationMessageHandler,
        ConnectorCommitLease? transportOwnership,
        bool clearPropertyWriter)
    {
        // The disconnect below cannot report this: the event handler is detached first, so
        // OnDisconnectedAsync never runs for a teardown.
        Metrics.MarkNotOperational();

        Task retirementTask;
        lock (_transportPublicationLock)
        {
            // Mark retirement before clearing the published identity. A final writer action either
            // acquired the lease first (and this teardown waits for it) or is rejected from now on.
            retirementTask = transportOwnership?.RetireAsync() ?? Task.CompletedTask;
            if (ReferenceEquals(_client, client) &&
                ReferenceEquals(_transportOwnership, transportOwnership))
            {
                _client = null;
                _transportOwnership = null;

                if (ReferenceEquals(_applicationMessageHandler, applicationMessageHandler))
                {
                    _applicationMessageHandler = null;
                }
            }
        }

        if (client is not null)
        {
            if (applicationMessageHandler is not null)
            {
                client.ApplicationMessageReceivedAsync -= applicationMessageHandler;
            }
            client.DisconnectedAsync -= OnDisconnectedAsync;
        }

        if (connectionMonitor is not null)
        {
            try { await connectionMonitor.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "MQTT connection monitor threw during disposal."); }
        }

        await retirementTask.ConfigureAwait(false);
        if (transportOwnership is not null)
        {
            AfterTransportCommitDrain?.Invoke();
        }

        if (client is not null)
        {
            try
            {
                if (client.IsConnected)
                {
                    await client.DisconnectAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disconnecting MQTT client during disposal."); }

            try { client.Dispose(); } catch { /* ignore */ }
        }

        if (ReferenceEquals(_connectionMonitor, connectionMonitor))
        {
            _connectionMonitor = null;
        }

        if (clearPropertyWriter)
        {
            _propertyWriter = null;
        }
    }

    /// <inheritdoc />
    public override Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        // Retained messages are received through the normal message handler: No separate initial load needed/possible
        return Task.FromResult<Action?>(null);
    }

    /// <inheritdoc />
    public override async ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        try
        {
            var client = _client;
            if (client is null || !client.IsConnected)
            {
                return WriteResult.Failure(changes, new InvalidOperationException("MQTT client is not connected."));
            }

            var length = changes.Length;
            if (length == 0) return WriteResult.Success;

            var messagesPool = ArrayPool<MqttApplicationMessage>.Shared;
            var userPropsArrayPool = ArrayPool<List<MqttUserProperty>?>.Shared;
            var changeIndicesPool = ArrayPool<int>.Shared;

            var messages = messagesPool.Rent(length);
            var changeIndices = changeIndicesPool.Rent(length); // Track which original change index each message corresponds to
            var userPropertiesArray = _configuration.SourceTimestampPropertyName is not null
                ? userPropsArrayPool.Rent(length)
                : null;

            var messageCount = 0;
            try
            {
                var changesSpan = changes.Span;

                // Build all messages first
                for (var i = 0; i < length; i++)
                {
                    var change = changesSpan[i];
                    var property = change.Property.TryGetRegisteredProperty();
                    if (property is null || property.CanContainSubjects)
                    {
                        continue;
                    }

                    var (topic, mapping) = TryGetTopicForProperty(change.Property, property);
                    if (topic is null) continue;

                    byte[] payload;
                    try
                    {
                        payload = _configuration.ValueConverter.Serialize(
                            change.GetNewValue<object?>(),
                            property.Type);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to serialize value for property {PropertyName}.", property.Name);
                        continue;
                    }

                    var message = new MqttApplicationMessage
                    {
                        Topic = topic,
                        PayloadSegment = new ArraySegment<byte>(payload),
                        QualityOfServiceLevel = mapping?.QualityOfService ?? _configuration.DefaultQualityOfService,
                        Retain = mapping?.Retain ?? _configuration.UseRetainedMessages
                    };

                    if (userPropertiesArray is not null)
                    {
                        var userProps = UserPropertiesPool.Rent();
                        userProps.Clear();
                        userProps.Add(new MqttUserProperty(
                            _configuration.SourceTimestampPropertyName!,
                            _configuration.SourceTimestampSerializer(change.ChangedTimestamp)));
                        message.UserProperties = userProps;
                        userPropertiesArray[messageCount] = userProps;
                    }

                    changeIndices[messageCount] = i;
                    messages[messageCount++] = message;
                }

                if (messageCount <= 0)
                    return WriteResult.Success;

                Exception? publishException = null;
                var failedStartIndex = messageCount; // Index where failures start (in message space)

#if USE_LOCAL_MQTTNET
                try
                {
                    await client.PublishMessagesAsync(
                        new ArraySegment<MqttApplicationMessage>(messages, 0, messageCount),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    publishException = ex;
                    failedStartIndex = 0; // All failed
                }
#else
                for (var i = 0; i < messageCount; i++)
                {
                    try
                    {
                        await client.PublishAsync(messages[i], cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        publishException = ex;
                        failedStartIndex = i; // This and all remaining failed
                        break;
                    }
                }
#endif

                if (publishException is not null)
                {
                    // Build failed changes array from the failed message indices
                    var failedCount = messageCount - failedStartIndex;
                    var failedChanges = new SubjectPropertyChange[failedCount];
                    for (var i = 0; i < failedCount; i++)
                    {
                        failedChanges[i] = changes.Span[changeIndices[failedStartIndex + i]];
                    }
                    return failedStartIndex > 0
                        ? WriteResult.PartialFailure(failedChanges, publishException)
                        : WriteResult.Failure(failedChanges, publishException);
                }

                return WriteResult.Success;
            }
            finally
            {
                if (userPropertiesArray is not null)
                {
                    for (var i = 0; i < messageCount; i++)
                    {
                        if (userPropertiesArray[i] is { } list)
                        {
                            UserPropertiesPool.Return(list);
                        }
                    }
                    userPropsArrayPool.Return(userPropertiesArray);
                }

                changeIndicesPool.Return(changeIndices);
                messagesPool.Return(messages);
            }
        }
        catch (Exception ex)
        {
            return WriteResult.Failure(changes, ex);
        }
    }

    private async Task SubscribeToPropertiesAsync(CancellationToken cancellationToken)
    {
        var registeredSubject = _subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            _logger.LogWarning("Subject is not registered. No MQTT subscriptions will be created.");
            return;
        }

        var properties = registeredSubject
            .GetAllProperties()
            .Where(p => !p.CanContainSubjects && _configuration.Mapper.TryGetMapping(p, _subject, out _))
            .ToList();

        if (properties.Count == 0)
        {
            _logger.LogWarning("No MQTT properties found to subscribe.");
            return;
        }

        var subscribeOptionsBuilder = _factory.CreateSubscribeOptionsBuilder();

        foreach (var property in properties)
        {
            var (topic, mapping) = TryGetTopicForProperty(property.Reference, property);
            if (topic is null) continue;

            if (!_ownership.ClaimSource(property.Reference))
            {
                _logger.LogError(
                    "Property {Subject}.{Property} already owned by another source. Skipping MQTT subscription.",
                    property.Subject.GetType().Name, property.Name);
                continue;
            }

            _topicToProperty[topic] = property.Reference;
            var qos = mapping?.QualityOfService ?? _configuration.DefaultQualityOfService;
            subscribeOptionsBuilder.WithTopicFilter(f => f
                .WithTopic(topic)
                .WithQualityOfServiceLevel(qos));
        }

        await _client!.SubscribeAsync(subscribeOptionsBuilder.Build(), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Subscribed to {Count} MQTT topics.", properties.Count);
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

    internal async ValueTask<PropertyReference?> TryGetPropertyForTopicAsync(string topic)
    {
        if (_topicToProperty.TryGetValue(topic, out var cachedProperty))
        {
            return cachedProperty;
        }

        var path = MqttHelper.StripTopicPrefix(topic, _configuration.TopicPrefix);
        var registered = _subject.TryGetRegisteredSubject();
        var property = registered is null
            ? null
            : await _configuration.Mapper.TryGetPropertyAsync(new MqttLookupKey(path), registered, CancellationToken.None).ConfigureAwait(false);
        var propertyReference = property?.Reference;

        // Add first, then validate (guarantees no memory leak)
        if (_topicToProperty.TryAdd(topic, propertyReference) &&
            propertyReference is { } resolvedProperty &&
            !IsRetainable(resolvedProperty.Subject))
        {
            _topicToProperty.TryRemove(topic, out _);
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

    private async Task OnMessageReceivedAsync(
        IMqttClient client,
        ConnectorCommitLease transportOwnership,
        MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;

        // Isolate per-message failures: a bad message must not escape into the MQTT receive loop and tear
        // down the subscription. No cancellation token flows in, so every failure is logged and skipped.
        try
        {
            if (await TryGetPropertyForTopicAsync(topic).ConfigureAwait(false) is not { } propertyReference)
            {
                return;
            }

            var registeredProperty = propertyReference.TryGetRegisteredProperty();
            if (registeredProperty is null)
            {
                return;
            }

            object? value;
            try
            {
                var payload = e.ApplicationMessage.Payload;
                value = _configuration.ValueConverter.Deserialize(payload, registeredProperty.Type);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize MQTT message for topic {Topic}.", topic);
                return;
            }

            var propertyWriter = _propertyWriter;
            if (propertyWriter is null)
            {
                return;
            }

            // Extract timestamps
            var receivedTimestamp = DateTimeOffset.UtcNow;
            var sourceTimestamp = MqttHelper.ExtractSourceTimestamp(
                e.ApplicationMessage.UserProperties,
                _configuration.SourceTimestampPropertyName,
                _configuration.SourceTimestampDeserializer) ?? receivedTimestamp;

            // Static delegate and value-tuple state keep the message hot path allocation-free. The
            // lease lock is not held while SetValueFromSource invokes interceptors or user code.
            propertyWriter.Write(
                (propertyReference, value, source: this, client, transportOwnership, sourceTimestamp, receivedTimestamp),
                static state =>
                {
                    if (!state.transportOwnership.TryAcquireCommit())
                    {
                        return;
                    }

                    try
                    {
                        if (ReferenceEquals(state.source._client, state.client))
                        {
                            state.propertyReference.SetValueFromSource(
                                state.source,
                                state.sourceTimestamp,
                                state.receivedTimestamp,
                                state.value);
                        }
                    }
                    finally
                    {
                        state.transportOwnership.ReleaseCommit();
                    }
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle MQTT message for topic {Topic}.", topic);
        }
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
        {
            return Task.CompletedTask;
        }

        _logger.LogWarning(e.Exception, "MQTT client disconnected. Reason: {Reason}.", e.Reason);

        // The callback can arrive after the monitor has already reconnected this client. Let the
        // monitor confirm the loss before it marks the source down and starts buffering.
        _connectionMonitor?.SignalReconnectNeeded();

        return Task.CompletedTask;
    }

    private async Task OnReconnectedAsync(CancellationToken cancellationToken)
    {
        await SubscribeToPropertiesAsync(cancellationToken).ConfigureAwait(false);
        if (_propertyWriter is not null)
        {
            await _propertyWriter.LoadInitialStateAndResumeAsync(cancellationToken).ConfigureAwait(false);
        }

        // Last, because the connection monitor treats a throw from anywhere above as a failed
        // reconnect: a client that could not resubscribe is not serving anything.
        Metrics.MarkOperational();
    }

    /// <inheritdoc />
    async Task IFaultInjectable.InjectFaultAsync(FaultType faultType, CancellationToken cancellationToken)
    {
        switch (faultType)
        {
            case FaultType.Kill:
                await ForceKillCurrentAttemptAsync().ConfigureAwait(false);
                break;

            case FaultType.Disconnect:
                var client = _client;
                if (client is not null && client.IsConnected)
                {
                    await client.DisconnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(faultType), faultType, null);
        }
    }

    private MqttClientOptions GetClientOptions()
    {
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_configuration.BrokerHost, _configuration.BrokerPort)
            .WithClientId(_configuration.ClientId)
            .WithCleanSession(_configuration.CleanSession)
            .WithKeepAlivePeriod(_configuration.KeepAliveInterval)
            .WithTimeout(_configuration.ConnectTimeout);

        if (_configuration.UseTls)
        {
            options.WithTlsOptions(o => o.UseTls());
        }

        if (_configuration.Username is not null)
        {
            options.WithCredentials(_configuration.Username, _configuration.Password);
        }

        var clientOptions = options.Build();
#if USE_LOCAL_MQTTNET
        clientOptions.AcknowledgeQoS1OnReceive = true;
#endif
        return clientOptions;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);

        await DisposeMqttConnectionAsync(
            _client,
            _connectionMonitor,
            _applicationMessageHandler,
            _transportOwnership,
            clearPropertyWriter: true).ConfigureAwait(false);

        _ownership.Dispose();
        _topicToProperty.Clear();
        _propertyToTopic.Clear();

        Dispose();
    }

    // Test seam for an interleaving with no externally observable synchronization point: the instant
    // the commit drain releases a teardown. Always null in production; the tests sample ordering
    // from it.
    internal Action? AfterTransportCommitDrain { get; set; }
}
