# OPC UA Client

> Part of the [OPC UA integration](connectors-opcua.md). See also: [Server](connectors-opcua-server.md) | [Mapping Guide](connectors-opcua-mapping.md)

Connect to an OPC UA server to synchronize C# objects with OPC UA nodes. The client automatically establishes connections, subscribes to node changes, and synchronizes values bidirectionally.

## Setup

```csharp
[InterceptorSubject]
public partial class Machine
{
    [Path("opc", "Temperature")]
    public partial decimal Temperature { get; set; }

    [Path("opc", "Speed")]
    public partial decimal Speed { get; set; }
}

builder.Services.AddSingleton(machine);
builder.Services.AddOpcUaSubjectClientSource<Machine>(
    serverUrl: "opc.tcp://plc.factory.com:4840",
    connectorName: "opc",
    rootPath: ["MyMachine"]);

// ...
var host = builder.Build();
await host.StartAsync();
Console.WriteLine(machine.Temperature); // Read property which is synchronized with OPC UA server
machine.Speed = 100; // Writes to OPC UA server
```

For multiple client sources, use `AddKeyedOpcUaSubjectClientSource` with a name and resolve via `[FromKeyedServices("name")]` (see [Diagnostics](#diagnostics)).

**Parameters:**
- `serverUrl` - The OPC UA server endpoint (e.g., `"opc.tcp://localhost:4840"`)
- `connectorName` - The connector name used to match `[Path]` attributes (e.g., `"opc"` matches `[Path("opc", "Temperature")]`)
- `rootPath` - Optional path segments to the root node to start browsing from under the Objects folder (e.g., `["MyMachine"]`)

Two DI overloads are available: the simple generic shown above and a full configuration overload (shown below).

## Resolving the Client Source

After registration, resolve `IOpcUaSubjectClientSource` to access diagnostics, the underlying session, and node ID lookups.

**DI (unnamed registration):**

```csharp
var source = serviceProvider.GetRequiredService<IOpcUaSubjectClientSource>();
```

**DI (keyed registration):**

```csharp
var source = serviceProvider.GetRequiredKeyedService<IOpcUaSubjectClientSource>("server1");
```

**From a property reference** (useful deep in business code where you hold a `PropertyReference` but not the DI container):

```csharp
if (property.TryGetSource(out var subjectSource) &&
    subjectSource is IOpcUaSubjectClientSource source)
{
    // use source.Diagnostics, source.CurrentSession, etc.
}
```

For direct instantiation (without DI), `CreateOpcUaClientSource` returns `IOpcUaSubjectClientSource` directly.

## Configuration

For advanced scenarios, use the full configuration API to customize connection behavior, subscription settings, and dynamic property discovery.

```csharp
builder.Services.AddOpcUaSubjectClientSource(
    subjectSelector: sp => sp.GetRequiredService<MyRoot>(),
    configurationProvider: sp => new OpcUaClientConfiguration
    {
        ServerUrl = "opc.tcp://localhost:4840",
        TypeResolver = new OpcUaTypeResolver(logger),
        ValueConverter = new OpcUaValueConverter(),
        SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),

        // Optional
        RootPath = ["Machines"],
        DefaultNamespaceUri = "http://factory.com/machines",
        ApplicationName = "MyOpcUaClient",
        ReconnectInterval = TimeSpan.FromSeconds(5),

        // Subscription settings (null = use OPC UA library defaults)
        DefaultSamplingInterval = 0,      // 0 = exception-based (immediate), null = server decides
        DefaultPublishingInterval = 100,
        DefaultQueueSize = 10,
        MaxItemsPerSubscription = 1000,

        // Data change filter (null = use OPC UA library defaults)
        DefaultDataChangeTrigger = null,  // StatusValue (report on value change)
        DefaultDeadbandType = null,       // None (report all changes)
        DefaultDeadbandValue = null,      // 0.0

        // Performance tuning
        BufferTime = TimeSpan.FromMilliseconds(10),
        RetryTime = TimeSpan.FromSeconds(10)   // delay between reconnect attempts
    });
```

**Dynamic Property Discovery:**

```csharp
ShouldAddDynamicProperty = async (node, ct) =>
{
    return node.BrowseName.Name.StartsWith("Sensor");
}
```

**Dynamic Attribute Discovery:**

When loading variable nodes, any child reached by a hierarchical reference to a Variable node, typically HasProperty, that is not found in the C# model can be added as a dynamic attribute:

```csharp
ShouldAddDynamicAttribute = async (node, ct) =>
{
    // Add standard OPC UA metadata attributes dynamically
    return node.BrowseName.Name is "EURange" or "EngineeringUnits";
}
```

By default, all unknown attributes are added. Set to `null` to disable dynamic attribute discovery.

### Configuration Reference

Beyond the settings shown above, the following properties are available on `OpcUaClientConfiguration`:

**Connection & Session:**

| Property | Default | Description |
|----------|---------|-------------|
| `ApplicationName` | "Namotion.Interceptor.Client" | Application name for identification and certificate generation |
| `UseSecurity` | false | Enable signing and encryption (see [Transport Security](#transport-security)) |
| `CreateUserIdentity` | null | Async factory for user credentials (see [Authentication](#authentication)) |
| `CertificateStoreBasePath` | "pki" | Base directory for certificate stores |
| `SessionFactory` | null | Custom session factory (uses `DefaultSessionFactory` when null) |
| `TelemetryContext` | NullTelemetryContext | Telemetry integration for logging and diagnostics |
| `Mapper` | OpcUaCompositeMapper | `IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>` that maps C# properties to OPC UA nodes (see [Mapping Guide](connectors-opcua-mapping.md)) |

**Subscription Tuning:**

| Property | Default | Description |
|----------|---------|-------------|
| `DefaultPublishingInterval` | 0 | Publishing interval in ms (0 = server default) |
| `SubscriptionKeepAliveCount` | 10 | Keep-alive count for subscriptions |
| `SubscriptionLifetimeCount` | 100 | Lifetime count (must be >= 3x keep-alive count) |
| `SubscriptionPriority` | 0 | Subscription priority (0 = server default) |
| `SubscriptionMaxNotificationsPerPublish` | 0 | Max notifications per publish (0 = server default) |
| `MinPublishRequestCount` | 3 | Minimum outstanding publish requests |
| `SubscriptionSequentialPublishing` | false | Process messages in order (reduces throughput) |

**Polling Fallback:**

| Property | Default | Description |
|----------|---------|-------------|
| `EnablePollingFallback` | true | Fall back to polling when subscriptions fail |
| `PollingInterval` | 1s | Polling interval (min 100ms) |
| `PollingBatchSize` | 100 | Items per polling read batch |
| `PollingDisposalTimeout` | 10s | Timeout for polling cleanup during disposal |
| `PollingCircuitBreakerThreshold` | 5 | Consecutive failures before circuit breaker opens |
| `PollingCircuitBreakerCooldown` | 30s | Cooldown after circuit breaker opens |

**Read After Write:**

| Property | Default | Description |
|----------|---------|-------------|
| `EnableReadAfterWrite` | true | Schedule reads after writes for non-exception-based items |
| `ReadAfterWriteBuffer` | 50ms | Buffer added to revised interval before read-back |

**Browsing:**

| Property | Default | Description |
|----------|---------|-------------|
| `MaxReferencesPerNode` | 0 | Max references per browse request (0 = server default) |
| `MaxBrowseContinuationRounds` | 100 | Max BrowseNext rounds per browse, where one round drains every continuation point pending at that time. Must be positive |
| `MaxAttributeTraversalDepth` | 100 | Max attribute levels (attributes of attributes) walked per load. Must be positive |

Both limits are backstops against a server that never stops handing out work, and both degrade to a retry rather than to an error. A node still paginating when `MaxBrowseContinuationRounds` is reached is omitted from the browse result and logged as a warning, so the loader keeps that property's current value and reloads the node on the next load. When `MaxAttributeTraversalDepth` is reached, the attributes still pending for the next level are skipped for this load and logged as a warning, while everything matched on earlier levels stays monitored. A value of zero or less on either fails configuration validation with an `ArgumentException`.

## Security

### Transport Security

```csharp
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://plc.factory.com:4840",
    UseSecurity = true,  // Enable signing and encryption (recommended for production)
    // ... other settings
};
```

When `UseSecurity = true`, the client prefers secure endpoints with message signing and encryption. The default is `false` for development convenience.

### Authentication

By default, the client connects with anonymous authentication. Use `CreateUserIdentity` to provide credentials:

```csharp
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://plc.factory.com:4840",
    CreateUserIdentity = _ => Task.FromResult(new UserIdentity("user", "password")),
    // ... other settings
};
```

For credentials from a secret manager or vault:

```csharp
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://plc.factory.com:4840",
    CreateUserIdentity = async cancellationToken =>
    {
        var secret = await vault.GetSecretAsync("opcua-credentials", cancellationToken);
        return new UserIdentity(secret.Username, secret.Password);
    },
    // ... other settings
};
```

For certificate-based authentication:

```csharp
CreateUserIdentity = _ =>
{
    var certificate = X509Certificate2.CreateFromPemFile("client.pem", "client.key");
    return Task.FromResult(new UserIdentity(certificate));
}
```

### Custom Application Configuration

Override `CreateApplicationInstanceAsync()` in a derived configuration class for full control over OPC UA application settings (certificates, transport quotas, security policies):

```csharp
public class MyOpcUaClientConfiguration : OpcUaClientConfiguration
{
    public override async Task<ApplicationInstance> CreateApplicationInstanceAsync()
    {
        var application = await base.CreateApplicationInstanceAsync();

        // Customize the application configuration
        var config = application.ApplicationConfiguration;
        config.SecurityConfiguration.AutoAcceptUntrustedCertificates = false;
        config.TransportQuotas.MaxMessageSize = 64_000_000;

        return application;
    }
}
```

## Monitoring & Subscriptions

### Sampling vs Exception-Based Monitoring

OPC UA supports two monitoring modes for value changes:

**Sampling-based** (`SamplingInterval > 0`): The server checks the value at fixed intervals and reports if it changed since the last sample. Fast changes that occur between samples may be missed.

**Exception-based** (`SamplingInterval = 0`): The server reports immediately whenever the value changes. This requires the server to support exception-based monitoring (indicated by `MinimumSamplingInterval = 0` on the node).

**When to use exception-based monitoring:**
- Discrete variables (boolean flags, state indicators) where you need to catch every transition
- Handshake patterns (client writes `true`, PLC sets back to `false`)
- Any value where missing a transition would cause system failures

```csharp
// Request exception-based monitoring for a discrete variable
[OpcUaNode("StartCommand", SamplingInterval = 0)]
public partial bool StartCommand { get; set; }
```

**Note:** Even with `SamplingInterval = 0`, the server may revise this based on its capabilities. Check the server's `RevisedSamplingInterval` to verify exception-based monitoring is active.

### Discrete vs Analog Variables

Industrial automation distinguishes between two types of variables:

| Type | Characteristics | Examples | OPC UA Monitoring |
|------|-----------------|----------|-------------------|
| **Analog** | Continuous values, gradual changes, sampling is fine | Temperature, pressure, speed | Sampling-based (`SamplingInterval > 0`) with optional deadband |
| **Discrete** | Binary on/off, every transition matters | Handshake flags, command triggers, state indicators | Exception-based (`SamplingInterval = 0`) |

For **discrete variables**, missing a transition can cause system failures. A classic example is the handshake pattern: client writes `1`, PLC acknowledges by writing `0`. If sampling misses the `1->0` transition (because both samples see `0`), the client never knows the PLC acknowledged.

OPC UA's sampling-based monitoring compares each sample against the *previous sample*, not against what the client knows. When a value changes faster than the sampling rate (e.g., `0->1->0` between samples), the server sees `0` at both sample points and reports no change.

```
Sampling-based (SamplingInterval = 100ms):
t=0ms:   Sample value=0, baseline=0 -> no change reported
t=50ms:  Client writes 1 -> value=1 (no sample happens)
t=60ms:  PLC writes 0 -> value=0 (no sample happens)
t=100ms: Sample value=0, baseline=0 -> no change reported
```

This is spec-compliant behavior per [OPC UA Part 4](https://reference.opcfoundation.org/Core/Part4/v104/docs/5.12.1.2), not a bug.

### Data Change Filters

Control which value changes generate notifications:

| Trigger | Reports when... |
|---------|-----------------|
| `Status` | Status code changes |
| `StatusValue` | Status or value changes (default) |
| `StatusValueTimestamp` | Status, value, or timestamp changes |

| Deadband Type | Filters out... |
|---------------|----------------|
| `None` | Nothing - reports all changes (default) |
| `Absolute` | Changes smaller than the absolute threshold |
| `Percent` | Changes smaller than the percentage of range |

```csharp
// Analog sensor - only report changes > 0.5 units
[OpcUaNode("Temperature",
    DeadbandType = DeadbandType.Absolute,
    DeadbandValue = 0.5)]
public partial double Temperature { get; set; }
```

### Read After Write Fallback

When a server doesn't support exception-based monitoring, the library provides an automatic read-after-write fallback.

**Solution hierarchy:**
1. **Best: Exception-based monitoring** (`SamplingInterval = 0`) - Server reports every change immediately
2. **Fallback: Read-after-write** - When the server revises `SamplingInterval = 0` to non-zero, automatically read values after writes

The library automatically detects when `SamplingInterval = 0` was revised to a non-zero value (common with legacy PLCs or servers that don't support exception-based monitoring). For these properties, after a successful write, it schedules a read-back to catch server-side changes that sampling would miss.

```csharp
// Mark as discrete variable - request exception-based monitoring
[OpcUaNode("CommandTrigger", SamplingInterval = 0)]
public partial bool CommandTrigger { get; set; }
```

**Configuration:**
```csharp
var config = new OpcUaClientConfiguration
{
    // Enable/disable read-after-write fallback (default: true)
    EnableReadAfterWrite = true,

    // Buffer added to revised interval before reading back (default: 50ms)
    ReadAfterWriteBuffer = TimeSpan.FromMilliseconds(50)
};
```

**Behavior:**
- Only triggers for properties where `SamplingInterval = 0` was revised to > 0
- Multiple rapid writes are coalesced into a single read
- Reads are batched for efficiency
- Circuit breaker prevents repeated failures from overwhelming the server
- Logged when activated so you can verify which properties need this fallback

**Limitations:**
If the server's minimum sampling rate is slower than the value changes, no client-side workaround can help. In that case, consider:
- Configuring the OPC UA server to support faster sampling or exception-based monitoring
- Changing the PLC code to use a counter-based pattern instead of boolean handshakes
- Using OPC UA Methods for command/response patterns (requires PLC support)

### Subscription Configuration

Configure monitored item behavior at global or per-property level:

| Setting | Default | Description |
|---------|---------|-------------|
| `DefaultSamplingInterval` | null | Sampling interval in ms (null = server decides, 0 = exception-based) |
| `DefaultQueueSize` | null | Values to buffer (null = library default of 1) |
| `DefaultDiscardOldest` | null | Discard oldest when queue full (null = library default of true) |
| `DefaultDataChangeTrigger` | null | When to report changes (null = StatusValue) |
| `DefaultDeadbandType` | null | Deadband filter type (null = None) |
| `DefaultDeadbandValue` | null | Deadband threshold (null = 0.0) |

All settings can be overridden per-property using `[OpcUaNode]` attribute.

**Notification gating during setup.** Incoming data change notifications are dropped while subscriptions and monitored items are being created, and start being applied only once setup finishes, so no notification reaches a subject whose items are still being registered. A subject that detaches while setup runs is recorded and its items are dropped by a sweep at the end of setup, before read-after-write registration. Nothing is lost by the gate: the initial state read that follows supplies the current value of every owned property, and notifications arriving after the gate opens but before that read are buffered and replayed. Disposal closes the gate permanently, so a reconnect that races disposal never resumes callbacks; the next connect builds a fresh subscription manager instead.

## Resilience

### Write Retry Queue During Disconnection

Write retry queue behavior (ring buffer, reconcile by commit order on reconnection) is provided by `SubjectSourceBase`. See [Connectors: Write Retry Queue](connectors.md#write-retry-queue). Configure via `WriteRetryQueueSize`:

```csharp
builder.Services.AddOpcUaSubjectClientSource(
    subjectSelector: sp => sp.GetRequiredService<Machine>(),
    configurationProvider: sp => new OpcUaClientConfiguration
    {
        ServerUrl = "opc.tcp://plc.factory.com:4840",
        WriteRetryQueueSize = 1000 // Buffer up to 1000 writes (default, 0 to disable)
    });

// Writes are automatically queued during disconnection
machine.Speed = 100; // Queued if disconnected, written immediately if connected
```

`Diagnostics.OutboundRetries` reports this queue: `Depth` is what is parked right now, `Capacity` echoes `WriteRetryQueueSize`, and `TotalDropped` counts capacity eviction, failed or terminally unconfirmed writes, and owned connect-window writes that cannot be retained since `StartTime`. A capacity of 0 retains nothing but still counts those owned writes. Writes made before the source claims their property remain unattributable; see [Known Limitations](connectors.md#known-limitations).

### Polling Fallback for Unsupported Nodes

The library automatically falls back to periodic polling when OPC UA nodes don't support subscriptions. This ensures all properties remain synchronized even with legacy servers or special node types.

```csharp
builder.Services.AddOpcUaSubjectClientSource(
    subjectSelector: sp => sp.GetRequiredService<Machine>(),
    configurationProvider: sp => new OpcUaClientConfiguration
    {
        ServerUrl = "opc.tcp://plc.factory.com:4840",
        EnablePollingFallback = true, // Default
        PollingInterval = TimeSpan.FromSeconds(1), // Default: 1 second
        PollingBatchSize = 100 // Default: 100 items per batch
    });
```

**Automatic behavior:**
- Nodes automatically switch to polling when subscriptions fail
- Batched reads for efficiency (reduces network overhead)
- Same value change detection as subscriptions (only updates on actual changes)
- No configuration required - works out of the box

### Auto-Healing of Failed Monitored Items

The library retries failed subscription items that may succeed later (for example when server resources free up), bounded so a persistently failing item escalates rather than retrying forever.

```csharp
builder.Services.AddOpcUaSubjectClientSource(
    subjectSelector: sp => sp.GetRequiredService<Machine>(),
    configurationProvider: sp => new OpcUaClientConfiguration
    {
        ServerUrl = "opc.tcp://plc.factory.com:4840",
        SubscriptionHealthCheckInterval = TimeSpan.FromSeconds(5) // Default: 5 seconds
    });
```

**Retry and escalation logic:**
- Retries transient errors (for example `BadTooManyMonitoredItems`, `BadOutOfService`) for up to three consecutive health checks (roughly 15 seconds at the default interval), so a genuinely transient failure recovers on its own.
- If an item keeps failing past that bound and `EnablePollingFallback` is enabled (the default), it escalates to polling rather than retrying the subscription forever.
- With polling disabled there is nothing better to escalate to, so the item keeps being retried and recovers on its own once the node returns; it is never dropped.
- Nodes that do not support subscriptions (`BadNotSupported`, `BadMonitoredItemFilterUnsupported`) fall back to polling immediately when enabled.
- Skips permanent design-time errors (`BadNodeIdUnknown`, `BadAttributeIdInvalid`, `BadIndexRangeInvalid`).
- Reconnect re-attempts a real subscription for every item, so escalation to polling is not permanent.
- Health checks run at configurable intervals (minimum: 1 second, default: 5 seconds).

**Configuration validation:**
- `SubscriptionHealthCheckInterval` minimum of 1 second enforced
- `PollingInterval` minimum of 100 milliseconds enforced
- Fail-fast with clear error messages on invalid configuration

### Connection Loss Recovery

The client handles connection loss through two cooperating mechanisms: the OPC UA SDK's built-in `SessionReconnectHandler` (automatic) and a health check loop that performs manual reconnection when the SDK handler fails or is insufficient.

**All reconnection paths guarantee eventual consistency** through a full server state read. The client never relies solely on subscription notifications for state synchronization after a reconnection.

#### Reconnection Flows

**Flow A: SDK reconnection with subscription transfer**

When the connection drops, the OPC UA SDK's `SessionReconnectHandler` attempts to reconnect automatically. If it succeeds and transfers existing subscriptions to the new session:

1. Keep-alive detects dead connection and triggers the SDK reconnect handler
2. SDK creates a new session and transfers subscriptions via `TransferSubscriptions`
3. `OnReconnectComplete` accepts the new session and sets a `NeedsFullStateSync` flag
4. The health check loop detects the flag on the next iteration:
   - Starts buffering incoming subscription notifications
   - Reads ALL property values from the server
   - Applies server values, replays the buffer, resumes normal operation
5. Full consistency is restored

**Flow B: SDK reconnection fails or returns a preserved session**

If the SDK handler cannot transfer subscriptions (e.g., server restarted and old subscriptions are gone), or if it returns the same session object ("preserved session"), the client abandons the session entirely:

1. `OnReconnectComplete` calls `AbandonCurrentSession()` (session nulled, transport killed, and incoming notifications buffered so stale values from the abandoned subscription are not applied during the gap before manual reconnection)
2. The health check loop detects the dead session and triggers manual reconnection
3. Manual reconnection creates a fresh session, new subscriptions, and performs a full state read
4. Full consistency is restored

Preserved sessions are always abandoned because subscriptions may have been silently deleted by the server (subscription lifetime expired during the disconnect period), leaving no mechanism for future data change notifications.

**Flow C: No SDK handler active (session dead without reconnection in progress)**

If the session dies without the SDK handler being active (e.g., after `KillAsync`, `ClearSessionAsync`, or a previous failed reconnection):

1. The health check loop detects the dead session
2. Triggers manual reconnection: fresh session, new subscriptions, full state read
3. Full consistency is restored

#### Stall Detection

When the SDK's reconnection handler gets stuck (e.g., server never responds), the client automatically detects the stall and forces a reconnection reset. Configure via `MaxReconnectDuration` (default: 30 seconds). If the SDK reconnection hasn't succeeded within this duration, a full session reset and manual reconnection is triggered.

#### Consistency Guarantees and Trade-offs

**Guarantee**: After any reconnection completes, all client property values will match the server's current state. The full state read (buffer + read all values + replay + resume pattern) ensures no property is left stale, regardless of what happened to subscription notifications during the disconnect.

**Trade-off: brief stale data window after SDK reconnection (Flow A)**

Between the SDK's `OnReconnectComplete` (step 3) and the health check's full state read (step 4), there is a window of up to one health check interval (~5 seconds by default) where:

- Subscription notifications from the transfer may apply partially (some notifications may have been lost due to server-side queue overflow or subscription lifetime expiration)
- Property values may temporarily reflect incomplete state

This window is bounded and self-correcting: the health check's full state read overwrites all values with the server's current state. For most industrial applications, this brief window is acceptable. If tighter consistency is required, reduce `SubscriptionHealthCheckInterval`.

**Eventual consistency for writes**: Client-to-server writes during disconnection are buffered in the write retry queue (see above). On reconnection, after loading the server's current state, each queued change is sent unless a later local write superseded it, so a write that already committed is not discarded by the reload. Combined with the full state read for server-to-client values, this provides bidirectional eventual consistency.

### Resilience Configuration

For 24/7 production use, the default configuration provides robust resilience:

| Setting | Default | Description |
|---------|---------|-------------|
| `KeepAliveInterval` | 5s | How quickly disconnections are detected |
| `ReconnectInterval` | 5s | Time between SDK reconnection attempts |
| `ReconnectHandlerTimeout` | 60s | Max time the reconnect handler attempts before giving up |
| `MaxReconnectDuration` | 30s | Max time for SDK reconnection before forcing manual reset |
| `SessionTimeout` | 120s | Server-side session lifetime (should be > OperationTimeout) |
| `OperationTimeout` | 30s | Timeout for individual OPC UA operations |
| `SubscriptionHealthCheckInterval` | 5s | Interval for health checks and post-reconnection state sync |
| `WriteRetryQueueSize` | 1000 | Updates buffered during disconnection |
| `SessionDisposalTimeout` | 5s | Max wait for graceful session close |
| `SubscriptionSequentialPublishing` | false | Process subscription messages in order (see Thread Safety) |

Final outbound delivery on stop shares the internal five-second safety bound described in [Flushing On Stop](connectors.md#flushing-on-stop). It cannot be configured per connector.

## Extensibility

For custom type conversions (used by both client and server), see [Custom Value Converter](connectors-opcua.md#custom-value-converter).

### Custom Type Resolver

Extend `OpcUaTypeResolver` to customize how the client infers C# types from OPC UA node metadata during dynamic property discovery. This is useful when you want specific OPC UA nodes to map to custom C# classes.

The resolver reads the address space in batches, so it has one seam per decision. `ResolveObjectNodeType` classifies one Object node from the children the loader already browsed. `ResolveVariableNodeTypeAsync` types one Variable node from its already-read DataType and ValueRank attributes, which is where a rule based on the browse name belongs. `TryMapBuiltInType` maps one OPC UA built-in type for every node at once. Overriding `ResolveVariableNodeTypesAsync` itself replaces the batched read, which is only worth doing when the types come from somewhere other than the server.

```csharp
public class CustomTypeResolver : OpcUaTypeResolver
{
    public CustomTypeResolver(ILogger logger)
        : base(logger)
    {
    }

    public override Type ResolveObjectNodeType(
        ReferenceDescription node, IReadOnlyList<ReferenceDescription> children)
    {
        if (node.BrowseName.Name.StartsWith("CustomDevice"))
        {
            return typeof(MyCustomDevice);
        }

        return base.ResolveObjectNodeType(node, children);
    }

    protected override Task<Type?> ResolveVariableNodeTypeAsync(
        OpcUaVariableNodeContext node, CancellationToken cancellationToken)
    {
        if (node.Reference.BrowseName.Name == "Timestamp")
        {
            return Task.FromResult<Type?>(typeof(DateTime));
        }

        return base.ResolveVariableNodeTypeAsync(node, cancellationToken);
    }

    protected override Type? TryMapBuiltInType(BuiltInType builtInType)
    {
        return builtInType == BuiltInType.ExtensionObject
            ? typeof(string)
            : base.TryMapBuiltInType(builtInType);
    }
}
```

`ResolveVariableNodeTypeAsync` returns null when the type cannot be inferred, and the loader then skips that node. Its two attribute values have already been classified, so a bad status reaching it is permanent and null is the right answer. A transient status never reaches it: `ResolveVariableNodeTypesAsync` throws `OpcUaTransientServiceException` first, which aborts the load so the source retries it, and an override that replaces the batched read must do the same.

### Custom Subject Factory

Extend `OpcUaSubjectFactory` to control how subject instances are created when the client discovers OPC UA object nodes. This allows custom initialization logic or dependency injection when creating subjects.

```csharp
public class CustomSubjectFactory : OpcUaSubjectFactory
{
    public override async Task<IInterceptorSubject> CreateSubjectAsync(
        RegisteredSubjectProperty property, ReferenceDescription node,
        ISession session, CancellationToken ct)
    {
        if (property.Type == typeof(Machine))
        {
            var machine = new Machine(property.Subject.Context);
            machine.Name = node.BrowseName.Name;
            return machine;
        }
        return await base.CreateSubjectAsync(property, node, session, ct);
    }
}
```

## Advanced Usage

### Complex Hierarchies

The library automatically handles nested object hierarchies, traversing through properties that reference other interceptor subjects. This enables modeling complex industrial systems with multiple levels of composition.

```csharp
[InterceptorSubject]
public partial class Factory
{
    [Path("opc", "Lines")]
    public partial ProductionLine[] Lines { get; set; }
}

[InterceptorSubject]
public partial class ProductionLine
{
    [Path("opc", "Machines")]
    public partial Machine[] Machines { get; set; }
}
```

The library automatically traverses the entire hierarchy.

### Dynamic Properties

When the client discovers OPC UA nodes not defined in your C# model, they can be added as dynamic properties at runtime. Access these properties through the [registry](registry.md) after the client completes its initial load. See also [Dynamic property and attribute creation](registry.md#dynamic-property-and-attribute-creation).

```csharp
var registered = machine.TryGetRegisteredSubject();
var dynamicProperty = registered.Properties.FirstOrDefault(p => p.Name == "UnknownSensor");
if (dynamicProperty != null)
{
    var value = dynamicProperty.Reference.GetValue();
}
```

#### Type Resolution

The `OpcUaTypeResolver` maps OPC UA nodes to CLR types during dynamic discovery:

- **Object nodes** become `DynamicSubject` (named sub-properties on the parent subject).
- **Object nodes with `[numeric]` convention** (e.g., `People[0]`, `People[1]`) become `DynamicSubject[]` collections.
- **Object nodes with `[string]` convention** (e.g., `Devices[SensorA]`) become `IReadOnlyDictionary<string, DynamicSubject>` dictionaries. The key of each entry is the bracket content of the child's browse name, so `Items[1]` yields the key `1`, and a child whose browse name has no brackets uses that full browse name as its key.
- **Variable nodes** are mapped to CLR types based on their OPC UA DataType. The resolver uses `session.TypeTree` to walk the type hierarchy, so custom DataType subtypes (e.g., a server-specific `LocalizedText` variant) are correctly resolved to their base built-in type.

The classification of an Object node reads its first browsed child, and only when that child is itself an Object. The bracket convention is produced by this library's OPC UA server when exposing C# collections and dictionaries. Standard OPC UA servers typically use named children and are always treated as single subjects.

| OPC UA BuiltInType | CLR Type | Notes |
|---|---|---|
| Boolean, SByte, Byte, Int16, ... | bool, sbyte, byte, short, ... | Direct mapping |
| String | string | |
| DateTime | DateTime | |
| LocalizedText | LocalizedText | |
| Enumeration | int | Mapped to underlying Int32 |
| Number | double | Abstract numeric base type |
| Integer | long | Abstract signed integer |
| UInteger | ulong | Abstract unsigned integer |
| ExtensionObject | ExtensionObject | Complex structured types |
| XmlElement | string | |
| Variant, Null | (skipped) | Type cannot be determined |

Override `ResolveVariableNodeTypeAsync` on `OpcUaTypeResolver` to customize the type of specific Variable nodes, or `TryMapBuiltInType` to change one built-in type's mapping for all of them. See [Custom Type Resolver](#custom-type-resolver).

#### Subject Deduplication

When the same OPC UA node appears at multiple paths in the address space (e.g., `Identification` referenced from both `MyMachine` and `MachineryBuildingBlocks`), the client reuses the same subject instance. Reuse applies to single references as well as collection and dictionary elements: any property that resolves to the same `NodeId` during a load is bound to the existing subject, which receives a single set of monitored items. The same applies within a single browse call: if a server exposes one target through multiple reference types (e.g., both `HasComponent` and `HasProperty`), the duplicate browse references are filtered so the underlying node is processed exactly once per parent, at both the property and attribute level.

The rule above collapses several references to one node. The opposite collision, several distinct nodes landing on the same destination in the model, is resolved the other way round: the first reference in browse order wins and each later one is skipped with a warning. It applies in three places, all within one parent: two sibling references that map to the same subject reference, collection or dictionary property; two siblings with the same browse name that would both add a dynamic property; and two children of a dictionary node whose browse names reduce to the same dictionary key. The loser is dropped before its subject is created, so nothing is staged, claimed or monitored for a node the model has no place for.

Round-trip identity is preserved for the common cross-parent DAG: if the server-side C# model has a single instance reachable from two different parent paths, the client materializes one instance bound to both parent properties. The case where two properties on the **same parent** reference the same instance under different names does not round-trip because OPC UA stores the BrowseName on the target node rather than on the reference. See [connectors-opcua-server.md](connectors-opcua-server.md#subject-deduplication) for the full discussion of the server-side behavior and its limitations.

## Write Error Handling

When a batch write to the OPC UA server partially fails, the client throws an `OpcUaWriteException`. The exception distinguishes between transient failures (connectivity issues, timeouts that may succeed on retry) and permanent failures (invalid nodes, access denied; should not be retried). The write retry queue (see [Resilience](#write-retry-queue-during-disconnection)) handles transient failures automatically during disconnection, but writes that fail while connected surface this exception.

## Diagnostics

`IOpcUaSubjectClientSource.Diagnostics` exposes a live facade of type `OpcUaClientDiagnostics`. Resolve it once and poll (see [Resolving the Client Source](#resolving-the-client-source)).

`OpcUaClientDiagnostics` derives from `SourceDiagnostics`, whose members, buffer semantics and read guarantees are described once in [Connector Diagnostics](connectors.md#connector-diagnostics). What follows is what is specific to this client.

**`IsOperational` here means the client has a live session with its subscriptions set up.** This built-in client implements liveness monitoring, but `IsOperational` is `null` before its first protocol-specific observation. It then publishes false for the whole address space browse and subscription creation, which on a large server still takes a while: the browse costs a batched call per level of the address space, and subscription creation grows with the number of monitored items. Which step raises it depends on how the session came about: the first health check tick on an initial connect, the completed subscription transfer on an SDK reconnect, and the completed state reload on a manual reconnect. It drops whenever the session is lost, killed or torn down, and whenever a connect attempt ends, so a client sitting in its retry delay reports an explicit false rather than serving.

It is not a claim that the model is in sync, and the two are not ordered against each other: the initial value read can run either side of the rise on an initial connect, and on a manual reconnect the reload always finishes first. While that read runs, `ISubjectSource.State` is `Synchronizing`, so reading it together with `IsOperational` is how a dropped network is told apart from a connected client that is still loading. See [Diagnostics and State answer different questions](connectors-monitoring.md#diagnostics-and-state-answer-different-questions).

This client measures both throughput directions, so `Throughput.IncomingPerSecond` and `Throughput.OutgoingPerSecond` are never `null` here.

This built-in client registers the `ClaimedPropertyCount` gauge, so it reports the measured number of currently owned properties, including zero.

| Member | Meaning |
|---|---|
| `IsReconnecting` | A reconnection attempt is in flight. A distinct sub-state of not being operational, not a second spelling of it. |
| `SessionId` | The current session identifier, `null` when there is no session. |
| `SubscriptionCount` | Active OPC UA subscriptions. |
| `MonitoredItemCount` | Monitored items across all subscriptions. |

`Reconnects` is the reconnection history. Every counter is monotonic since `StartTime`, so a reconnect storm is visible as `TotalAttempts` climbing without `TotalSucceeded` keeping up. `LastConnectionTime` is the exception listed first below: it is not a counter and deliberately survives the epoch reset, because it records a discrete past event rather than an amount accumulated during the run.

| Member | Meaning |
|---|---|
| `Reconnects.LastConnectionTime` | When a session was last established, `null` if never. Records a past event and survives the disconnection that follows it. |
| `Reconnects.TotalAttempts` | Attempts started. Once all in-flight attempts resolve, this equals the three below summed. |
| `Reconnects.TotalSucceeded` | Attempts that produced a usable session. |
| `Reconnects.TotalFailed` | Attempts that ended with a genuine fault, meaning an exception raised while the attempt was still live. |
| `Reconnects.TotalAbandoned` | Attempts that ended without a usable session and without a fault: a null session, a failed transfer, a preserved session after a server restart, a stall reset, or a cancellation from a kill or from the listen attempt being torn down, which happens both when the source stops and when the retry loop ends an attempt. |

`Polling` is `null` when the [polling fallback](#polling-fallback-for-unsupported-nodes) is off, no session has been set up yet, or the client is between connect attempts. That last case is not a startup-only condition: the block reads through the session manager and goes `null` as soon as that manager is disposed, which every way out of a connect attempt does, so it stays `null` for the whole retry delay. The totals underneath survive that and reappear at their previous values once a session exists again. Otherwise it reports:

| Member | Meaning |
|---|---|
| `Polling.ItemCount` | Items currently being polled. |
| `Polling.TotalSuccessfulReads` | Reads that succeeded. |
| `Polling.TotalFailedReads` | Reads that failed. |
| `Polling.TotalValueChanges` | Value changes detected by polling. |
| `Polling.TotalSlowPolls` | Polls whose duration exceeded the polling interval. |
| `Polling.TotalCircuitBreakerTrips` | Times the circuit breaker tripped. |
| `Polling.IsCircuitBreakerOpen` | The circuit breaker is currently open. |
| `Polling.IsRunning` | The polling loop is running. This is a sub-component's own state, not a second spelling of `IsOperational`, which describes the connector as a whole. |

`ReadAfterWrite` is `null` when [read-after-write](#read-after-write-fallback) is off, no session has been set up yet, or the client is between connect attempts, for the same reason as `Polling` above and with its totals surviving the same way. Every counter here describes a read that follows a write, and each member names its noun so a failed verification read does not read as a failed write:

| Member | Meaning |
|---|---|
| `ReadAfterWrite.PendingReads` | Verification reads currently pending. |
| `ReadAfterWrite.TotalScheduledReads` | Verification reads scheduled. |
| `ReadAfterWrite.TotalExecutedReads` | Verification reads executed. |
| `ReadAfterWrite.TotalCoalescedReads` | Scheduled reads replaced by a subsequent write. |
| `ReadAfterWrite.TotalFailedReads` | Verification reads that failed. |

The sub-block counters survive a reconnect. `PollingManager` and `ReadAfterWriteManager` are rebuilt on every connect attempt, including failed ones, but their counters are owned by the source, so they do not rebase to zero during the reconnect storm that is exactly when they matter.

For outbound retry capacity, depth, and drop accounting, including capacity 0, see [Write Retry Queue During Disconnection](#write-retry-queue-during-disconnection).

## Direct Session Access

For scenarios the connector does not cover natively (OPC UA **Methods**, **Alarms & Conditions**), `IOpcUaSubjectClientSource.CurrentSession` exposes the underlying `ISession` (see [Resolving the Client Source](#resolving-the-client-source) for how to obtain the source):

```csharp
if (source.CurrentSession is { } session)
{
    var outputs = await session.CallAsync(parentNodeId, methodId, inputArgs, cancellationToken);
}
```

**Lifecycle contract:** read `CurrentSession` immediately before each use. It may be `null` during reconnection and the instance changes after a manual reconnect (Flow C), a transferred-subscription failure (Flow B), or a stall reset. Never cache the reference. For long-running session-bound state (e.g. an A&C `Subscription`), subscribe to `CurrentSessionChanged` (below) or recreate on demand when calls fail with `BadSessionIdInvalid` / `BadSessionNotActivated`.

### Reacting to session swaps with `CurrentSessionChanged`

For consumers holding session-bound state (typically A&C subscriptions), `CurrentSessionChanged` fires on every transition (including to/from `null`). Method-call consumers usually do not need it: they re-read `CurrentSession` per call and surface a stale session as a failure on the next call. The event is for consumers that have no such inbound traffic.

```csharp
opcUaSource.CurrentSessionChanged += (_, args) =>
{
    if (args.PreviousSession is not null)
    {
        // Synchronous local cleanup only; transport may already be closed.
        DisposeMyAlarmsSubscription(args.PreviousSession);
    }

    if (args.CurrentSession is not null)
    {
        // Async work fire-and-forget so the handler returns quickly.
        var newSession = args.CurrentSession;
        _ = Task.Run(async () =>
        {
            try { await RecreateMyAlarmsSubscriptionAsync(newSession); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to recreate A&C subscription"); }
        });
    }
};
```

The event fires on the connector's own thread but **outside** the reconnection lock, in transition order, so a slow handler will not stall reconnection. Use `PreviousSession` only for synchronous local cleanup (its transport may already be closed). For async work on `CurrentSession` use fire-and-forget (`_ = Task.Run(...)`) and tolerate the session being swapped again before the task completes; the next `CurrentSessionChanged` event will surface the new state. Handler exceptions are caught and logged, but per standard event semantics a throwing subscriber skips later subscribers, so isolate exceptions in your own handler if multiple must run.

## Node ID Resolution

`IOpcUaSubjectClientSource.TryGetNodeId` resolves the OPC UA `NodeId` bound to a tracked property. This is useful when making raw `ISession` calls that require a `NodeId`:

```csharp
if (source.TryGetNodeId(machine.GetPropertyReference(nameof(Machine.Temperature)), out var nodeId))
{
    // Use nodeId with source.CurrentSession for direct OPC UA operations
}
```

Returns `false` if the property is not owned by this source or has not been resolved yet (e.g. before initial connection).

## Thread Safety

The library ensures thread-safe operations across all OPC UA interactions. Property operations are synchronized via `SyncRoot` when interceptors are present, subscription callbacks use thread-safe concurrent queues, and multiple OPC UA clients can connect concurrently.

Write queue operations use `Interlocked` operations for thread-safe counter updates and flush operations are protected by semaphores to prevent concurrent flush issues.

**Update ordering:**
By default (`SubscriptionSequentialPublishing = false`), subscription callbacks may be processed in parallel for higher throughput. This means that for the same property, if two rapid updates arrive in different publish responses, they could theoretically be applied out of order. Each update carries a `SourceTimestamp` from the server, but the library does not enforce timestamp-based ordering.

For most use cases (sensor values, status updates), this is acceptable since you typically want the latest value. If your application requires strict ordering guarantees, set `SubscriptionSequentialPublishing = true` to process all subscription messages sequentially at the cost of reduced throughput.

To prevent feedback loops when external sources update properties, apply inbound values with the `SetValueFromSource()` extension method, which stamps the write with a `FromSource` origin (source marking is per write, not through an ambient scope):

```csharp
propertyReference.SetValueFromSource(
    source: opcUaSource,
    changedTimestamp: sourceTimestamp,
    receivedTimestamp: DateTimeOffset.Now,
    valueFromSource: newValue);
```

## Lifecycle

The OPC UA client hooks into the interceptor lifecycle system (see [Subject Lifecycle Tracking](tracking.md#subject-lifecycle-tracking)) to clean up resources when subjects are detached.

- When a subject is detached, monitored items in `SubscriptionManager._monitoredItems` are removed
- Polling items in `PollingManager._pollingItems` are also cleaned up
- Property data (OPC UA node IDs) associated with the subject is cleared
- OPC UA subscription items remain on the server until session ends
- Cleanup runs lock-free from the synchronous detach callback, so it cannot deadlock against a load in progress
- A subject that detaches while subscriptions are being created is recorded, and its items are dropped by a sweep at the end of setup

See also [Lifecycle Limitations](connectors-opcua.md#lifecycle-limitations) that apply to both client and server.

## Internal Design

> For the reasoning behind the address space loader, its staging and commit model, and the browse primitives, see [OPC UA Client Loader Design](design/opcua-client-loader.md).

### Class Dependency Graph

```
OpcUaSubjectClientSource (SubjectSourceBase: BackgroundService + ISubjectSource)
 ├── owns SourceMetrics                    (from the base: liveness, error, buffers, throughput)
 ├── owns ReconnectionMetrics              (standalone, thread-safe counters)
 ├── owns PollingMetrics                   (standalone, handed to each SessionManager)
 ├── owns ReadAfterWriteMetrics            (standalone, handed to each SessionManager)
 ├── owns IncomingThroughput               (standalone, ThroughputCounter)
 ├── owns OutgoingThroughput               (standalone, ThroughputCounter)
 ├── owns SubscriptionHealthMonitor        (standalone)
 ├── owns OpcUaSubjectLoader               (back-ref to source)
 │    ├── owns OpcUaAttributeLoader        (back-ref to the loader)
 │    └── creates OpcUaLoadContext         (one per load, disposed when the load ends)
 ├── owns OpcUaClientDiagnostics           (back-ref to source, read-only facade over SourceMetrics)
 ├── creates SessionManager                (back-ref to source)
 │    ├── creates SubscriptionManager      (back-ref to source)
 │    │    ├── uses PollingManager
 │    │    └── uses ReadAfterWriteManager
 │    ├── creates PollingManager           (back-ref to source, receives PollingMetrics)
 │    └── creates ReadAfterWriteManager    (receives ReadAfterWriteMetrics)
 └── creates OutboundWriter
      ├── receives SessionManager
      └── receives ThroughputCounter
```

### Responsibilities

| Class | Role |
|-------|------|
| `OpcUaSubjectClientSource` | Orchestrator. Inherits `SubjectSourceBase` (which owns the pump skeleton: buffer, listen, load initial state, run change queue, retry on failure). Adds the OPC UA-specific health check loop, reconnection logic, and the `ISubjectSource` contract. |
| `SessionManager` | Manages the OPC UA session lifecycle (create, reconnect, dispose). Owns `SubscriptionManager`, `PollingManager`, and `ReadAfterWriteManager`. |
| `SubscriptionManager` | Creates and manages OPC UA subscriptions and monitored items. Routes incoming data change notifications. |
| `OutboundWriter` | Writes property changes to the OPC UA server. Tracks outgoing throughput. |
| `PollingManager` | Polling fallback for nodes that don't support subscriptions. Includes a circuit breaker. |
| `ReadAfterWriteManager` | Schedules read-backs after writes for nodes where exception-based monitoring was revised to sampling. |
| `SubscriptionHealthMonitor` | Retries failed monitored items that may succeed later (transient server errors). |
| `OpcUaSubjectLoader` | Discovers the address space breadth-first and maps nodes to C# properties. Every level is browsed in a fixed number of batched calls, so the number of round-trips grows with the depth of the address space rather than with the number of nodes. |
| `OpcUaAttributeLoader` | Loads the attribute nodes of variable properties round by round, so attributes of attributes are discovered until nothing new appears or `MaxAttributeTraversalDepth` is reached. |
| `OpcUaLoadContext` | Per-load state: the browse cache, the staged subjects, and the queued ownership claims and property bindings that the commit at the end of the load applies. |
| `OpcUaSessionExtensions` | Batched browse and read primitives over `ISession`, including continuation-point handling, batch splitting when the server rejects a size, and deduplication by resolved `NodeId`. |
| `OpcUaClientDiagnostics` | Read-only public facade, a `SourceDiagnostics` narrowed for this connector, that aggregates diagnostics from all internal components. |
| `ReconnectionMetrics` | Thread-safe counters for reconnection tracking (attempts, successes, failures, abandoned). |
| `PollingMetrics`, `ReadAfterWriteMetrics` | Thread-safe counters for the polling fallback and the read-after-write fallback. Owned by the source rather than by the manager that feeds them, so a rebuilt session does not rebase them. |
| `ThroughputCounter` | Lock-free 60-second sliding window rate counter for incoming/outgoing changes per second. |

### Key Design Decisions

**Single-threaded health loop.** `OpcUaSubjectClientSource` runs a single `RunHealthCheckLoopAsync` task that checks session health, triggers reconnection, and detects stalls. The loop is spawned from `StartListeningAsync` via `BackgroundTaskLifetime.Start`, so it is started and stopped together with the listener. The pump skeleton itself lives in `SubjectSourceBase`. All reconnection coordination flows through this loop.

**Back-reference pattern.** Several classes (`SessionManager`, `SubscriptionManager`, `PollingManager`) receive a reference to `OpcUaSubjectClientSource` to access shared state (metrics, throughput counters, error tracking). `OutboundWriter` demonstrates the preferred alternative: receiving only the specific dependencies it needs via constructor parameters.

**Diagnostics as a facade.** `OpcUaClientDiagnostics` navigates through `OpcUaSubjectClientSource` and `SessionManager` to expose a flat public API. `SessionManager` creates and caches its `PollingDiagnostics` and `ReadAfterWriteDiagnostics` wrappers, so repeated reads reuse the same objects without exposing internal types.

### Load Failure and Rollback

A load discovers the address space first and changes the model only at the end. Ownership claims and property bindings are queued while browsing and applied by a single commit, claims first, then the bindings deepest level first. A failure before that commit therefore leaves the model at its pre-load state, with one exception: dynamic properties and dynamic attributes are added eagerly during discovery and survive a failed load. They are transient rather than torn state, because the next load re-matches each of them through the OPC UA node attribute it carries and monitors it again.

A failure inside the commit undoes what that commit did: the ownership claims it established are released, while ownership from a previous successful load is kept, and the bindings it applied are restored in reverse order, except where the application has since written the property itself, in which case the newer value wins.

A subject whose browse did not complete this load is skipped rather than loaded from a truncated child list. Its properties keep their current values and the next load reloads it.

### Known Limitations

**The staging link can survive a successful load in a graph-shaped address space.** A subject reached under two parents can keep delegating to its discovering parent's context after a successful load. The consequence is a retained context reference rather than a wrong model: the subject stays registered, monitored and correct. The cause and the fix, which belongs in the core, are described in [OPC UA Client Loader Design](design/opcua-client-loader.md#the-remaining-leak-is-in-the-core).
