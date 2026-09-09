using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry.Paths;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Client;

public class OpcUaClientConfiguration
{
    private static readonly IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey> DefaultMapper =
        new OpcUaCompositeMapper(
            new OpcUaPathProviderMapper(new AttributeBasedPathProvider(OpcUaConstants.DefaultConnectorName)),
            new OpcUaAttributeMapper(OpcUaConstants.DefaultConnectorName));

    private ISessionFactory? _resolvedSessionFactory;

    /// <summary>
    /// Gets the OPC UA server endpoint URL to connect to (e.g., "opc.tcp://localhost:4840").
    /// </summary>
    public required string ServerUrl { get; set; }

    /// <summary>
    /// Gets the optional root path segments to start browsing from under the Objects folder.
    /// Each element is a browse name to resolve one level deeper (e.g., ["Machines", "MyMachine"] browses Objects/Machines/MyMachine).
    /// If not specified or empty, browsing starts from the ObjectsFolder root.
    /// </summary>
    public string[]? RootPath { get; set; }
    
    /// <summary>
    /// Gets the OPC UA client application name used for identification and certificate generation.
    /// Default is "Namotion.Interceptor.Client".
    /// </summary>
    public string ApplicationName { get; set; } = "Namotion.Interceptor.Client";
    
    /// <summary>
    /// Gets the default namespace URI to use when a [OpcUaNode] attribute defines a NodeIdentifier but no NodeNamespaceUri.
    /// </summary>
    public string? DefaultNamespaceUri { get; set; }
    
    /// <summary>
    /// Gets the maximum number of monitored items per subscription. Default is 1000.
    /// </summary>
    public int MaxItemsPerSubscription { get; set; } = 1000;

    /// <summary>
    /// Gets the maximum number of write operations to queue for retry when disconnected. Default is 1000.
    /// When the session is disconnected, write operations are queued up to this limit.
    /// Once reconnected, queued writes are flushed to the server in order (FIFO).
    /// Set to 0 to disable write retry queue (writes will be dropped when disconnected).
    /// </summary>
    public int WriteRetryQueueSize { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the interval for subscription health checks and auto-healing attempts. Default is 5 seconds.
    /// Failed monitored items (excluding design-time errors like BadNodeIdUnknown) are retried at this interval.
    /// This also determines how quickly the health check loop picks up work deferred from SDK reconnection.
    /// </summary>
    public TimeSpan SubscriptionHealthCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the maximum time to wait for SDK reconnection before forcing a manual reconnection.
    /// If the SDK's reconnect handler hasn't succeeded within this duration, stall detection triggers
    /// a full session reset and manual reconnection attempt.
    /// Default is 30 seconds. Use lower values (e.g., 15s) for faster recovery in tests.
    /// </summary>
    public TimeSpan MaxReconnectDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets an async predicate that is called when an unknown (not statically typed) OPC UA node or variable is found during browsing.
    /// If the function returns true, the node is added as a dynamic property to the given subject.
    /// Default is add all missing as dynamic properties.
    /// </summary>
    public Func<ReferenceDescription, CancellationToken, Task<bool>>? ShouldAddDynamicProperty { get; set; } =
        static (_, _) => Task.FromResult(true);

    /// <summary>
    /// Gets or sets an async predicate called when an unknown attribute is found on a variable node.
    /// If returns true, the attribute is added as a dynamic attribute to the parent property.
    /// Default is add all missing as dynamic attributes.
    /// </summary>
    public Func<ReferenceDescription, CancellationToken, Task<bool>>? ShouldAddDynamicAttribute { get; set; } =
        static (_, _) => Task.FromResult(true);

    /// <summary>
    /// Gets the type resolver used to infer C# types from OPC UA nodes during dynamic property discovery.
    /// </summary>
    public OpcUaTypeResolver? TypeResolver { get; set; }
    
    /// <summary>
    /// Gets the value converter used to convert between OPC UA node values and C# property values.
    /// Handles type conversions such as decimal to double for OPC UA compatibility.
    /// </summary>
    public OpcUaValueConverter ValueConverter { get; set; } = new();
    
    /// <summary>
    /// Gets the subject factory used to create interceptor subject instances for OPC UA object nodes.
    /// </summary>
    public OpcUaSubjectFactory SubjectFactory { get; set; } = new(DefaultSubjectFactory.Instance);

    /// <summary>
    /// Gets or sets the time window to buffer incoming changes (default: 8ms).
    /// </summary>
    public TimeSpan? BufferTime { get; set; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Gets or sets the retry time (default: 10s).
    /// </summary>
    public TimeSpan? RetryTime { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the default sampling interval in milliseconds for monitored items when not specified on the [OpcUaNode] attribute.
    /// When null (default), uses the OPC UA library default (-1 = server decides).
    /// Set to 0 for exception-based monitoring (immediate reporting on every change).
    /// </summary>
    public int? DefaultSamplingInterval { get; set; }

    /// <summary>
    /// Gets or sets the default queue size for monitored items when not specified on the [OpcUaNode] attribute.
    /// When null (default), uses the OPC UA library default (1).
    /// </summary>
    public uint? DefaultQueueSize { get; set; }

    /// <summary>
    /// Gets or sets whether the server should discard the oldest value in the queue when the queue is full for monitored items.
    /// When null (default), uses the OPC UA library default (true).
    /// </summary>
    public bool? DefaultDiscardOldest { get; set; }

    /// <summary>
    /// Gets or sets the default data change trigger for monitored items when not specified on the [OpcUaNode] attribute.
    /// When null (default), uses the OPC UA library default (StatusValue).
    /// </summary>
    public DataChangeTrigger? DefaultDataChangeTrigger { get; set; }

    /// <summary>
    /// Gets or sets the default deadband type for monitored items when not specified on the [OpcUaNode] attribute.
    /// When null (default), uses the OPC UA library default (None).
    /// Use Absolute or Percent for analog values to filter small changes.
    /// </summary>
    public DeadbandType? DefaultDeadbandType { get; set; }

    /// <summary>
    /// Gets or sets the default deadband value for monitored items when not specified on the [OpcUaNode] attribute.
    /// When null (default), uses the OPC UA library default (0.0).
    /// The interpretation depends on DeadbandType: absolute units for Absolute, percentage for Percent.
    /// </summary>
    public double? DefaultDeadbandValue { get; set; }

    /// <summary>
    /// Gets or sets the buffer time added to the revised sampling interval when scheduling
    /// read-after-writes after writes. This ensures the PLC has time to respond before
    /// the read occurs. Only applies when SamplingInterval = 0 was requested but the
    /// server revised it to a non-zero value.
    /// Default: 50 milliseconds.
    /// </summary>
    public TimeSpan ReadAfterWriteBuffer { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Gets or sets the default publishing interval for subscriptions in milliseconds (default: 0).
    /// Larger values reduce overhead by batching more notifications per publish.
    /// </summary>
    public int DefaultPublishingInterval { get; set; } = 0;

    /// <summary>
    /// Gets or sets the subscription keep-alive count (default: 10).
    /// </summary>
    public uint SubscriptionKeepAliveCount { get; set; } = 10;

    /// <summary>
    /// Gets or sets the subscription lifetime count (default: 100).
    /// SubscriptionLifetimeCount shall be at least 3 * SubscriptionKeepAliveCount.
    /// </summary>
    public uint SubscriptionLifetimeCount { get; set; } = 100;

    /// <summary>
    /// Gets or sets the subscription priority (default: 0 = server default).
    /// </summary>
    public byte SubscriptionPriority { get; set; } = 0;

    /// <summary>
    /// Gets or sets the maximum notifications per publish that the client requests (default: 0 = server default).
    /// </summary>
    public uint SubscriptionMaxNotificationsPerPublish { get; set; } = 0;

    /// <summary>
    /// Gets or sets whether to process subscription messages sequentially (in order).
    /// When true, callbacks are invoked one at a time in sequence order, reducing throughput but guaranteeing order.
    /// When false (default), messages may be processed in parallel for higher throughput.
    /// Only enable this if your application requires strict ordering of property updates.
    /// Default is false for optimal performance.
    /// </summary>
    public bool SubscriptionSequentialPublishing { get; set; } = false;

    /// <summary>
    /// Gets or sets the minimum number of publish requests the client keeps outstanding at all times.
    /// Higher values improve reliability during traffic spikes and brief network issues by ensuring
    /// multiple requests are always in flight. The OPC Foundation's reference client uses 3.
    /// Default is 3 for optimal reliability.
    /// </summary>
    public int MinPublishRequestCount { get; set; } = 3;

    /// <summary>
    /// Gets or sets the maximum references per node to read per browse request. 0 uses server default.
    /// </summary>
    public uint MaxReferencesPerNode { get; set; } = 0;

    /// <summary>
    /// Gets or sets whether to enable automatic polling fallback when subscriptions are not supported.
    /// When enabled, items that fail subscription creation automatically fall back to periodic polling.
    /// Default is true.
    /// </summary>
    public bool EnablePollingFallback { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to enable automatic read-after-writes after writes.
    /// When enabled and SamplingInterval=0 is requested but the server revises it to non-zero,
    /// a read is automatically scheduled after each write to ensure the written value is read back.
    /// This compensates for servers that don't support true exception-based monitoring.
    /// Default is true.
    /// </summary>
    public bool EnableReadAfterWrite { get; set; } = true;

    /// <summary>Enables the experimental scalar source reconciliation strategy with authoritative reads.</summary>
    public bool EnableExperimentalSourceReconciliation { get; set; }

    /// <summary>
    /// Gets or sets the base path for certificate stores.
    /// Default is "pki". Change this to isolate certificate stores for parallel test execution.
    /// </summary>
    public string CertificateStoreBasePath { get; set; } = "pki";

    /// <summary>
    /// Gets or sets the polling interval for items that don't support subscriptions.
    /// Only used when EnablePollingFallback is true.
    /// Default is 1000ms (1 second).
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the maximum batch size for polling read operations.
    /// Larger batches reduce network calls but increase latency.
    /// Default is 100 items per batch.
    /// </summary>
    public int PollingBatchSize { get; set; } = 100;

    /// <summary>
    /// Gets or sets the timeout to wait for the polling manager to complete during disposal.
    /// If the polling task does not complete within this timeout, it will be abandoned.
    /// Default is 10 seconds.
    /// </summary>
    public TimeSpan PollingDisposalTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the number of consecutive failures before the circuit breaker opens
    /// for background read operations (polling fallback and read-after-writes).
    /// When the circuit breaker opens, reads are suspended temporarily to prevent resource exhaustion.
    /// Default is 5 failures.
    /// </summary>
    public int PollingCircuitBreakerThreshold { get; set; } = 5;

    /// <summary>
    /// Gets or sets the cooldown period after the circuit breaker opens before attempting
    /// to resume background read operations (polling fallback and read-after-writes).
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan PollingCircuitBreakerCooldown { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the OPC UA session timeout.
    /// This determines how long the server will maintain the session after losing communication.
    /// Should be greater than OperationTimeout to allow time for failure detection and reconnection.
    /// Default is 120 seconds.
    /// </summary>
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Gets or sets the keep-alive interval for the OPC UA session.
    /// This determines how often the client sends keep-alive messages to detect disconnection.
    /// Shorter intervals allow faster disconnection detection but increase network traffic.
    /// Default is 5 seconds.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the operation timeout for OPC UA requests.
    /// This determines how long to wait for a response before timing out.
    /// Shorter timeouts allow faster disconnection detection but may cause false positives on slow networks.
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the maximum time the reconnect handler will attempt to reconnect before giving up.
    /// Default is 60 seconds.
    /// </summary>
    public TimeSpan ReconnectHandlerTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the interval between reconnection attempts when connection is lost.
    /// Default is 5 seconds.
    /// </summary>
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets whether to use security (signing and encryption) when connecting to the OPC UA server.
    /// When true, the client will prefer secure endpoints with message signing and encryption.
    /// When false, the client will prefer unsecured endpoints (faster, but no protection).
    /// Default is false for development convenience. Set to true for production deployments
    /// that require secure communication.
    /// </summary>
    public bool UseSecurity { get; set; } = false;

    /// <summary>
    /// Gets or sets an async factory for creating the user identity used when connecting to the OPC UA server.
    /// When null (default), anonymous authentication is used.
    /// </summary>
    public Func<CancellationToken, Task<UserIdentity>>? CreateUserIdentity { get; set; }

    /// <summary>
    /// Gets or sets the telemetry context for OPC UA operations. When null and the connector is registered
    /// through the AddOpcUaSubject* DI extensions, it is filled with a DI-backed telemetry context (and the type
    /// resolver is created from the DI logger when unset); otherwise it falls back to NullTelemetryContext. Set a
    /// value (including NullTelemetryContext.Instance) to keep your own.
    /// </summary>
    public ITelemetryContext? TelemetryContext { get; set; }

    /// <summary>
    /// Gets or sets the session factory for creating OPC UA sessions.
    /// If not specified, a DefaultSessionFactory using the configured TelemetryContext is created automatically.
    /// </summary>
    public ISessionFactory? SessionFactory { get; set; }

    /// <summary>
    /// Maps C# properties to OPC UA nodes.
    /// Defaults to composite of OpcUaPathProviderMapper and OpcUaAttributeMapper, both filtered by the "opc" connector name.
    /// </summary>
    public IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey> Mapper { get; set; } = DefaultMapper;

    /// <summary>
    /// Gets or sets the timeout for session disposal during shutdown.
    /// If the session doesn't close gracefully within this timeout, it will be forcefully disposed.
    /// Default is 5 seconds.
    /// </summary>
    public TimeSpan SessionDisposalTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the actual session factory, creating a default one using TelemetryContext if not explicitly set.
    /// The default factory is cached after first access (thread-safe).
    /// </summary>
    public ISessionFactory ActualSessionFactory => SessionFactory ?? LazyInitializer.EnsureInitialized(
        ref _resolvedSessionFactory, () => new DefaultSessionFactory(ResolvedTelemetryContext))!;

    /// <summary>
    /// The telemetry context to use for SDK calls, never null (falls back to the no-op context when unset).
    /// </summary>
    public ITelemetryContext ResolvedTelemetryContext => TelemetryContext ?? NullTelemetryContext.Instance;

    /// <summary>
    /// Creates and configures an OPC UA application instance for the client.
    /// Override this method to customize application configuration, security settings, or certificate handling.
    /// </summary>
    /// <returns>A configured <see cref="ApplicationInstance"/> ready for connecting to OPC UA servers.</returns>
    public virtual async Task<ApplicationInstance> CreateApplicationInstanceAsync()
    {
        var application = new ApplicationInstance(ResolvedTelemetryContext)
        {
            ApplicationName = ApplicationName,
            ApplicationType = ApplicationType.Client
        };

        var host = System.Net.Dns.GetHostName();
        var applicationUri = $"urn:{host}:Namotion.Interceptor:{ApplicationName}";

        var config = new ApplicationConfiguration
        {
            ApplicationName = ApplicationName,
            ApplicationType = ApplicationType.Client,
            ApplicationUri = applicationUri,
            ProductUri = "urn:Namotion.Interceptor",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = $"{CertificateStoreBasePath}/own",
                    SubjectName = $"CN={ApplicationName}, O=Namotion"
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = $"{CertificateStoreBasePath}/trusted"
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = $"{CertificateStoreBasePath}/rejected"
                },
                AutoAcceptUntrustedCertificates = true,
                AddAppCertToTrustedStore = true,
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = (int)OperationTimeout.TotalMilliseconds,
                MaxStringLength = 4_194_304,
                MaxByteStringLength = 16_777_216,
                MaxMessageSize = 16_777_216,
                ChannelLifetime = 600000,
                SecurityTokenLifetime = 3600000
            },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = (int)SessionTimeout.TotalMilliseconds
            },
            DisableHiResClock = true,
            TraceConfiguration = new TraceConfiguration
            {
                OutputFilePath = "Logs/UaClient.log",
                TraceMasks = 0
            },
            CertificateValidator = new CertificateValidator(ResolvedTelemetryContext)
        };

        await config.CertificateValidator.UpdateAsync(config).ConfigureAwait(false);

        application.ApplicationConfiguration = config;
        return application;
    }

    /// <summary>
    /// Validates configuration values and throws ArgumentException if invalid.
    /// Call this method during initialization to fail fast with clear error messages.
    /// </summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(ServerUrl);
        ArgumentNullException.ThrowIfNull(TypeResolver);
        ArgumentNullException.ThrowIfNull(ValueConverter);
        ArgumentNullException.ThrowIfNull(SubjectFactory);

        if (WriteRetryQueueSize < 0)
        {
            throw new ArgumentException(
                $"WriteRetryQueueSize must be non-negative, got: {WriteRetryQueueSize}",
                nameof(WriteRetryQueueSize));
        }

        if (SubscriptionHealthCheckInterval < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentException(
                $"SubscriptionHealthCheckInterval must be at least {TimeSpan.FromSeconds(1).TotalSeconds}s (got: {SubscriptionHealthCheckInterval.TotalSeconds}s)",
                nameof(SubscriptionHealthCheckInterval));
        }

        if (MaxItemsPerSubscription <= 0)
        {
            throw new ArgumentException(
                $"MaxItemsPerSubscription must be positive, got: {MaxItemsPerSubscription}",
                nameof(MaxItemsPerSubscription));
        }

        if (EnablePollingFallback)
        {
            if (PollingInterval < TimeSpan.FromMilliseconds(100))
            {
                throw new ArgumentException(
                    $"PollingInterval must be at least {TimeSpan.FromMilliseconds(100).TotalMilliseconds}ms when EnablePollingFallback is true (got: {PollingInterval.TotalMilliseconds}ms)",
                    nameof(PollingInterval));
            }

            if (PollingBatchSize <= 0)
            {
                throw new ArgumentException(
                    $"PollingBatchSize must be positive, got: {PollingBatchSize}",
                    nameof(PollingBatchSize));
            }

            if (PollingCircuitBreakerThreshold <= 0)
            {
                throw new ArgumentException(
                    $"PollingCircuitBreakerThreshold must be positive, got: {PollingCircuitBreakerThreshold}",
                    nameof(PollingCircuitBreakerThreshold));
            }

            if (PollingCircuitBreakerCooldown < TimeSpan.FromSeconds(1))
            {
                throw new ArgumentException(
                    $"PollingCircuitBreakerCooldown must be at least {TimeSpan.FromSeconds(1).TotalSeconds}s when EnablePollingFallback is true (got: {PollingCircuitBreakerCooldown.TotalSeconds}s)",
                    nameof(PollingCircuitBreakerCooldown));
            }
        }

        if (SessionTimeout < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentException(
                $"SessionTimeout must be at least 1000ms, got: {SessionTimeout}",
                nameof(SessionTimeout));
        }

        if (ReconnectHandlerTimeout < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentException(
                $"ReconnectHandlerTimeout must be at least 1000ms, got: {ReconnectHandlerTimeout}",
                nameof(ReconnectHandlerTimeout));
        }

        if (ReconnectInterval < TimeSpan.FromSeconds(0.1))
        {
            throw new ArgumentException(
                $"ReconnectInterval must be at least 100ms, got: {ReconnectInterval}",
                nameof(ReconnectInterval));
        }

        if (MaxReconnectDuration < TimeSpan.FromSeconds(5))
        {
            throw new ArgumentException(
                $"MaxReconnectDuration must be at least 5 seconds, got: {MaxReconnectDuration.TotalSeconds}s",
                nameof(MaxReconnectDuration));
        }

        if (MinPublishRequestCount < 1)
        {
            throw new ArgumentException(
                $"MinPublishRequestCount must be at least 1, got: {MinPublishRequestCount}",
                nameof(MinPublishRequestCount));
        }

        if (ReadAfterWriteBuffer < TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"ReadAfterWriteBuffer must be non-negative, got: {ReadAfterWriteBuffer}",
                nameof(ReadAfterWriteBuffer));
        }
    }
}