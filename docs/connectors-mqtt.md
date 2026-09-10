# MQTT

The `Namotion.Interceptor.Mqtt` package provides integration between Namotion.Interceptor and MQTT (Message Queuing Telemetry Transport), enabling bidirectional synchronization between C# objects and MQTT brokers. It supports both client and server modes.

## Key Features

- Bidirectional synchronization between C# objects and MQTT topics
- Attribute-based mapping for property-to-topic relationships
- JSON serialization by default (customizable)
- Retained messages for initial state delivery
- QoS support for reliable message delivery
- Automatic reconnection with circuit breaker pattern

## Client Setup

Connect to an MQTT broker as a subscriber with `AddMqttSubjectClientSource`. The client automatically establishes connections, subscribes to topics, and synchronizes values with your C# properties.

```csharp
[InterceptorSubject]
public partial class Sensor
{
    [Path("mqtt", "Temperature")]
    public partial decimal Temperature { get; set; }

    [Path("mqtt", "Humidity")]
    public partial decimal Humidity { get; set; }
}

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithHostedServices(builder.Services);

builder.Services.AddSingleton(new Sensor(context));
builder.Services.AddMqttSubjectClientSource<Sensor>(
    brokerHost: "mqtt.example.com",
    connectorName: "mqtt",
    topicPrefix: "sensors/room1");

var host = builder.Build();
var sensor = host.Services.GetRequiredService<Sensor>();
await host.StartAsync();

Console.WriteLine(sensor.Temperature); // Synchronized with broker
sensor.Temperature = 25.5m;             // Published to broker
```

## Server Setup

Host an embedded MQTT broker that publishes property changes with `AddMqttSubjectServer`. External MQTT clients can subscribe to receive updates when properties change.

```csharp
[InterceptorSubject]
public partial class Device
{
    [Path("mqtt", "Status")]
    public partial string Status { get; set; }
}

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithHostedServices(builder.Services);

builder.Services.AddSingleton(new Device(context));
builder.Services.AddMqttSubjectServer<Device>(
    connectorName: "mqtt",
    brokerPort: 1883,
    topicPrefix: "devices/mydevice");

var host = builder.Build();
var device = host.Services.GetRequiredService<Device>();
await host.StartAsync();

device.Status = "Online"; // Published to all connected MQTT clients
```

## Configuration

### Client Configuration

For advanced scenarios, use the full configuration API to customize connection behavior and MQTT settings.

```csharp
builder.Services.AddMqttSubjectClientSource(
    subjectSelector: sp => sp.GetRequiredService<Sensor>(),
    configurationProvider: sp => new MqttClientConfiguration
    {
        BrokerHost = "mqtt.example.com",
        BrokerPort = 1883,
        Mapper = new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),

        // Authentication
        Username = "user",
        Password = "password",
        UseTls = true,

        // Connection settings
        ClientId = "my-client-id",
        CleanSession = true,
        TopicPrefix = "sensors",
        ConnectTimeout = TimeSpan.FromSeconds(10),
        KeepAliveInterval = TimeSpan.FromSeconds(15),

        // QoS settings
        DefaultQualityOfService = MqttQualityOfServiceLevel.AtLeastOnce,
        UseRetainedMessages = true,

        // Reconnection settings
        ReconnectDelay = TimeSpan.FromSeconds(2),
        MaximumReconnectDelay = TimeSpan.FromSeconds(60),
        HealthCheckInterval = TimeSpan.FromSeconds(30),
        ReconnectStallThreshold = 10,

        // Circuit breaker
        CircuitBreakerFailureThreshold = 5,
        CircuitBreakerCooldown = TimeSpan.FromSeconds(60),

        // Performance tuning
        BufferTime = TimeSpan.FromMilliseconds(8),
        RetryTime = TimeSpan.FromSeconds(10),
        WriteRetryQueueSize = 1000,

        // Serialization
        ValueConverter = new JsonMqttValueConverter(),
        SourceTimestampPropertyName = "ts"
    });
```

### Server Configuration

The server configuration controls the embedded MQTT broker behavior.

```csharp
builder.Services.AddMqttSubjectServer(
    subjectSelector: sp => sp.GetRequiredService<Device>(),
    configurationProvider: sp => new MqttServerConfiguration
    {
        BrokerHost = "127.0.0.1", // Optional: bind to specific interface (default: all interfaces)
        BrokerPort = 1883,
        Mapper = new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),

        // Connection settings
        ClientId = "my-server-id",
        TopicPrefix = "devices",

        // QoS settings
        DefaultQualityOfService = MqttQualityOfServiceLevel.AtLeastOnce,
        UseRetainedMessages = true,
        MaxPendingMessagesPerClient = 25000,

        // Initial state
        InitialStateDelay = TimeSpan.FromMilliseconds(500),

        // Performance tuning
        BufferTime = TimeSpan.FromMilliseconds(8),

        // Serialization
        ValueConverter = new JsonMqttValueConverter(),
        SourceTimestampPropertyName = "ts"
    });
```

### Mapper Configuration

The `Mapper` property accepts any `IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>`. The `MqttPropertyMapping` record carries protocol-specific fields:

| Field              | Type                         | Description                          |
|--------------------|------------------------------|--------------------------------------|
| `Topic`            | `string?`                    | The MQTT topic path for the property |
| `QualityOfService` | `MqttQualityOfServiceLevel?` | Per-property QoS override            |
| `Retain`           | `bool?`                      | Per-property retain flag override    |

The built-in mapper types:

| Mapper | Purpose |
|---|---|
| `MqttPathProviderMapper` | Wraps a `PathProviderBase` to produce topics from `[Path]` attributes |
| `MqttAttributeMapper` | Layers per-topic QoS and Retain from `[MqttTopic]` attributes onto the mapping (the topic segment itself is resolved by the path provider) |
| `MqttFluentMapper` | Code-based type-level mapper produced by `MqttFluentMapperBuilder<TRoot>.Build()` (see [Code-based mapping](#code-based-fluent-mapping)) |
| `MqttCompositeMapper` | Combines multiple mappers with merge semantics |

The simple DI overloads (`AddMqttSubjectClientSource<T>(brokerHost, connectorName)`) default to a composite of `MqttPathProviderMapper` and `MqttAttributeMapper`, so both `[Path]` and `[MqttTopic]` attributes work out of the box. See [Property Mappers](connectors.md#property-mappers) for the generic abstraction.

`MqttAttributeMapper` contributes only QoS and Retain; it relies on `MqttPathProviderMapper` to resolve the topic in both directions, so it must always be paired with one (the default composite does this). On its own it resolves no topics. This differs from the OPC UA attribute mapper, which is self-sufficient because OPC UA browses hierarchically and matches each node against a single level, whereas MQTT resolves a flat topic that needs full-path composition by the path provider.

## Topic Mapping

### [Path] attribute

Properties are mapped to MQTT topics using the `[Path]` attribute. The topic is constructed by combining the optional `TopicPrefix` with the path from the attribute.

```csharp
[InterceptorSubject]
public partial class Sensor
{
    // With TopicPrefix = "home/living-room":
    // Topic: home/living-room/Temperature
    [Path("mqtt", "Temperature")]
    public partial decimal Temperature { get; set; }

    // Nested paths supported:
    // Topic: home/living-room/metrics/Humidity
    [Path("mqtt", "metrics/Humidity")]
    public partial decimal Humidity { get; set; }
}
```

### [MqttTopic] attribute

`[MqttTopic]` is a `[Path]` for the `mqtt` context plus optional per-topic QoS and Retain metadata. The string is a single relative path segment composed hierarchically with parent property segments (and the optional `TopicPrefix`), exactly like `[Path]`. The QoS and Retain values are layered on top by the `MqttAttributeMapper`.

```csharp
[InterceptorSubject]
public partial class Sensor
{
    // With TopicPrefix = "home/living-room":
    // Topic: home/living-room/temperature
    [MqttTopic("temperature")]
    public partial decimal Temperature { get; set; }

    [MqttTopic("critical", QualityOfService = MqttQualityOfServiceLevel.ExactlyOnce,
        Retain = MqttRetainMode.True)]
    public partial string CriticalAlert { get; set; }
}
```

Multi-level topics are built by annotating each level of the object graph (parent reference properties carry their own segment), the same way `[Path]` composes nested paths. Because the path is split on the topic separator for reverse lookup (inbound messages), a single `[MqttTopic]` value should be one segment without embedded separators.

Since `[MqttTopic]` already provides the `mqtt` `[Path]` segment for a property, use one attribute per property rather than combining `[Path]` and `[MqttTopic]` on the same property.

### Code-based (fluent) mapping

`MqttFluentMapperBuilder<TRoot>` is a complete, code-based alternative to attribute mapping. Configure a type once with `ForType<T>().Map(...)`, then call `Build(...)` to produce an `MqttFluentMapper`. The mapping resolves everywhere that type appears in the object graph, including collection and dictionary elements (which get bracket indices such as `motors[1]/speed`).

`WithSegment(...)` sets the topic level for the property. `WithQualityOfService(...)` and `WithRetain(...)` set per-property protocol metadata.

```csharp
var fluentMapper = new MqttFluentMapperBuilder<Plant>()
    .ForType<Motor>()
        .Map(m => m.Speed,  b => b.WithSegment("speed").WithQualityOfService(MqttQualityOfServiceLevel.AtLeastOnce))
        .Map(m => m.Torque, b => b.WithSegment("torque"))
    .ForType<Pump>()
        .Map(p => p.Motor,  b => b.WithSegment("motor"))
    .ForType<Plant>()
        .Map(p => p.Motors,  b => b.WithSegment("motors"))
        .Map(p => p.Sensors, b => b.WithSegment("sensors"))
    .Build('/');
```

Because `Motor` is configured once at the type level, all motors in the graph (direct properties, collection elements, nested references) resolve the same topic segments without repeating configuration.

`ForType<T>()` registrations apply to all types derived from `T` and all types implementing `T` when `T` is an interface. The most specific registration wins.

#### Composing with the connector

`Build()` produces a mapper you compose into the `Mapper` property through the configuration overload. Layer it after the attribute mappers (the same pair the simple overloads use) so fluent wins on conflicts and attribute and fluent mapping compose:

```csharp
services.AddMqttSubjectServer<Plant>(
    sp => sp.GetRequiredService<Plant>(),
    _ => new MqttServerConfiguration
    {
        BrokerPort = 1883,
        Mapper = new MqttCompositeMapper(
            new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),
            new MqttAttributeMapper("mqtt"),
            new MqttFluentMapperBuilder<Plant>()
                .ForType<Motor>().Map(m => m.Speed, b => b.WithSegment("speed"))
                .Build('/'))
    });
```

For an attribute-only setup, use the simple `AddMqttSubjectServer<T>("mqtt")` overload, which builds the attribute composite for you.

## Serialization

By default, values are serialized as JSON. Custom value converters can be implemented for different serialization formats.

```csharp
public class CustomMqttValueConverter : IMqttValueConverter
{
    public byte[] Serialize(object? value, Type type)
    {
        // Custom serialization logic
        return Encoding.UTF8.GetBytes(value?.ToString() ?? "");
    }

    public object? Deserialize(ReadOnlySequence<byte> payload, Type type)
    {
        // Custom deserialization logic
        var str = Encoding.UTF8.GetString(payload);
        return Convert.ChangeType(str, type);
    }
}
```

## Resilience Features

### Write Retry Queue

Write retry queue behavior (ring buffer, reconcile by commit order on reconnection) is provided by `SubjectSourceBase`. See [Connectors: Write Retry Queue](connectors.md#write-retry-queue). Configure via `WriteRetryQueueSize`:

```csharp
new MqttClientConfiguration
{
    WriteRetryQueueSize = 1000 // Buffer up to 1000 writes (default, 0 to disable)
}
```

### Circuit Breaker

Prevents resource exhaustion during prolonged outages by pausing reconnection attempts.

```csharp
new MqttClientConfiguration
{
    CircuitBreakerFailureThreshold = 5, // Open after 5 consecutive failures
    CircuitBreakerCooldown = TimeSpan.FromSeconds(60) // Wait before retrying
}
```

### Reconnect Stall Detection

Detects hung reconnection attempts and forces a reset to allow recovery.

```csharp
new MqttClientConfiguration
{
    ReconnectStallThreshold = 10, // 10 health check iterations
    HealthCheckInterval = TimeSpan.FromSeconds(30) // 5 minute timeout
}
```

## Diagnostics

Both built-in MQTT connectors implement liveness monitoring and report through the shared model: the member tree, the three buffers, `LastError` stickiness and the read guarantees are described once in [Connector Diagnostics](connectors.md#connector-diagnostics). Before the first protocol-specific liveness observation, `IsOperational` can be `null`; after that observation, the connectors publish explicit `true` or `false` values. What follows is what is specific to MQTT.

**`IsOperational` for the client means the connection to the broker is up.** It is set as soon as the connect returns, before the property subscriptions are established, so on the first connect it leads them by the duration of the subscribe. After a reconnect it is raised only once resubscription and the initial-state reload have both succeeded, because a client that reconnected but could not resubscribe is not serving anything. It drops on every disconnect, including one the connection monitor is about to recover from, and on teardown.

**`IsOperational` for the server means the broker is listening.**

Neither connector measures throughput, so `Throughput.IncomingPerSecond` and `Throughput.OutgoingPerSecond` are both `null` rather than `0.0`.

The client's diagnostics are a plain `SourceDiagnostics` with no MQTT specific additions. `OutboundRetries.Capacity` echoes `WriteRetryQueueSize`; at capacity 0 no writes are retained, but failed, terminally unconfirmed, and owned connect-window writes are still counted in `TotalDropped`. Writes made before the source claims their property remain unattributable; see [Known Limitations](connectors.md#known-limitations). The built-in client registers the `ClaimedPropertyCount` gauge, so it reports a measured value, including zero. The count rises as topics are claimed during the subscribe step of the connect, which runs before the initial state is loaded, and falls as subjects detach.

The server's diagnostics are an `MqttServerDiagnostics`, which adds one member:

| Member | Meaning |
|---|---|
| `ConnectedClientCount` | Clients currently connected to the broker. |

A server has no inbound buffer or retry queue, so only `OutboundChanges` applies there. It is registered while the change queue processor is running and its capacity is `null`, because that queue is unbounded.

The client's broker connection state is transport health only. Whether the model can be trusted is `ISubjectSource.State`, and for MQTT `Synchronized` is weaker than for the other connectors (see [What Synchronized Means per Protocol](connectors-monitoring.md#what-synchronized-means-per-protocol)).

Reaching the diagnostics differs by connector. `MqttSubjectServer` is public, so pick it out of the registered hosted services with `serviceProvider.GetServices<IHostedService>().OfType<MqttSubjectServer>()`, or hold the instance if you constructed it yourself. The client source type is internal: reach it as an `ISubjectSource` through the source monitor's `SourceSubscription.Sources`, or through `property.TryGetSource(out var source)` on a property it owns.

## Thread Safety

The library ensures thread-safe operations across all MQTT interactions:
- Property operations are thread-safe
- Subscription callbacks use concurrent queues
- Cache operations use `ConcurrentDictionary`

## Lifecycle Management

The MQTT integration hooks into the interceptor lifecycle system (see [Subject Lifecycle Tracking](tracking.md#subject-lifecycle-tracking)) to clean up resources when subjects are detached.

### Automatic Cleanup on Subject Detach

**Client behavior:**
- Cache entries in `_topicToProperty` and `_propertyToTopic` are removed for detached subjects
- Cleanup happens both proactively (on detach event) and lazily (when stale entries are accessed)

**Server behavior:**
- Cache entries in `_propertyToTopic` and `_pathToProperty` are removed for detached subjects
- Same dual cleanup strategy as client

**Race condition prevention:**
- Cache lookups validate subject attachment AFTER cache access
- Stale entries are detected and removed even if the detach event already fired
- This ensures we never return stale data even with concurrent attach/detach

## Performance

The library includes optimizations:

- Batched message publishing with configurable buffer time
- Object pooling for change buffers and user property lists
- Fast path for retained message handling
- Efficient topic-to-property caching with lazy cleanup

## Benchmark Results

MQTTnet has currently serious performance issues:

Intel(R) Core(TM) Ultra 7 258V

```
Server Benchmark - 1 minute - [2026-02-18 22:15:10.262]

Total received changes:          206800
Total published changes:         1196400
Process memory:                  329.44 MB (161.17 MB in .NET heap)
Avg allocations over last 60s:   86.49 MB/s

Metric                               Avg        P50        P90        P95        P99      P99.9        Max        Min     StdDev      Count
-------------------------------------------------------------------------------------------------------------------------------------------
Received (changes/s)             3451.56    3519.77    3768.97    3815.79    3861.35    3861.35    3861.35    2057.52     314.58          -
Processing latency (ms)             0.04       0.01       0.03       0.05       0.47       1.38      24.47       0.00       0.39     206800
End-to-end latency (ms)          3468.57    3510.97    5277.64    5530.69    5888.33    6458.14    6555.45    1058.94    1331.77     206800
```

```
Client Benchmark - 1 minute - [2026-02-18 22:15:13.630]

Total received changes:          1402251
Total published changes:         1183600
Process memory:                  549.1 MB (314.15 MB in .NET heap)
Avg allocations over last 60s:   63.86 MB/s

Metric                               Avg        P50        P90        P95        P99      P99.9        Max        Min     StdDev      Count
-------------------------------------------------------------------------------------------------------------------------------------------
Received (changes/s)            23375.11   23411.68   24244.79   24403.03   24940.71   24940.71   24940.71   20190.70     817.26          -
Processing latency (ms)             0.06       0.01       0.04       0.26       0.85       2.12      38.53       0.00       0.55    1402251
End-to-end latency (ms)           544.87      42.58    2581.02    4165.70    5476.68    6132.41    6682.53       4.70    1315.95    1402251
```
