# WebSocket

The `Namotion.Interceptor.WebSocket` package provides bidirectional WebSocket communication for synchronizing subject graphs between .NET servers and clients. It's optimized for industrial, digital twin, and IoT scenarios.

## Key Features

- Bidirectional synchronization between server and clients
- JSON serialization (extensible for MessagePack in future)
- Hello/Welcome handshake with initial state delivery
- Sequence numbers on server-to-client messages for gap detection
- Periodic heartbeat messages for liveness checking
- Automatic reconnection with exponential backoff
- Write retry queue for resilience during disconnection
- Multiple client support with broadcast updates

## Choosing a Server Mode

The package offers two server modes with identical performance (both use Kestrel):

| Mode | Method | Best For |
|------|--------|----------|
| **Standalone** | `AddWebSocketSubjectServer` | Dedicated sync servers, edge nodes, SCADA systems, console apps |
| **Embedded** | `AddWebSocketSubjectHandler` + `MapWebSocketSubjectHandler` | Adding sync to existing ASP.NET apps (API + WebSocket on same port) |

**Use standalone mode** when WebSocket sync is the primary purpose of your application. It creates a dedicated Kestrel server with minimal overhead.

**Use embedded mode** when you already have an ASP.NET application (with controllers, Blazor, etc.) and want to add WebSocket sync without running a second server.

## Server Setup (Standalone)

Creates a dedicated WebSocket server on its own port. Best for edge nodes, industrial gateways, and dedicated sync services.

```csharp
[InterceptorSubject]
public partial class Device
{
    public partial string Status { get; set; }
    public partial decimal Temperature { get; set; }
}

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithLifecycle()
    .WithHostedServices(builder.Services);

var device = new Device(context);
builder.Services.AddSingleton(device);
builder.Services.AddWebSocketSubjectServer<Device>(configuration =>
{
    configuration.Port = 8080;
});

var host = builder.Build();
host.Run();
// Server listens on ws://localhost:8080/ws
```

> See [Hosting](hosting.md) for details on `WithHostedServices()`.

## Server Setup (Embedded)

Adds WebSocket sync to an existing ASP.NET application. Best when you already have a web app and want to add real-time sync.

```csharp
var builder = WebApplication.CreateBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithLifecycle()
    .WithHostedServices(builder.Services);

var device = new Device(context);
builder.Services.AddSingleton(device);
builder.Services.AddWebSocketSubjectHandler<Device>("/ws");

var app = builder.Build();

// Your existing middleware
app.MapControllers();
app.MapBlazorHub();

// Add WebSocket sync endpoint
app.UseWebSockets();
app.MapWebSocketSubjectHandler("/ws");

app.Run();
// WebSocket available alongside your existing endpoints
```

## Client Setup

Connect to a WebSocket server as a subscriber with `AddWebSocketSubjectClientSource`. The client automatically connects, performs the handshake, receives initial state, and synchronizes property changes bidirectionally.

```csharp
var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithContextInheritance()
    .WithHostedServices(builder.Services);

var device = new Device(context);
builder.Services.AddSingleton(device);
builder.Services.AddWebSocketSubjectClientSource<Device>(configuration =>
{
    configuration.ServerUri = new Uri("ws://localhost:8080/ws");
});

var host = builder.Build();

// Register IServiceProvider so DefaultSubjectFactory can create subjects with context
context.AddService(host.Services);

host.Run();
// Client receives initial state and ongoing updates from server
// Local property changes are sent to server
```

## Configuration

### Server Configuration

```csharp
builder.Services.AddWebSocketSubjectServer<Device>(configuration =>
{
    // Network settings
    configuration.Port = 8080;              // Default: 8080
    configuration.Path = "/ws";             // Default: "/ws"
    configuration.BindAddress = "127.0.0.1"; // Default: null (localhost)

    // Performance tuning
    configuration.BufferTime = TimeSpan.FromMilliseconds(8);  // Batch updates
    configuration.WriteBatchSize = 1000;    // Max properties per message

    // Connection limits
    configuration.MaxConnections = 1000;    // Default: 1000
    configuration.MaxMessageSize = 10 * 1024 * 1024;  // Default: 10 MB
    configuration.HelloTimeout = TimeSpan.FromSeconds(10);  // Default: 10s

    // Heartbeat / sequence numbers
    configuration.HeartbeatInterval = TimeSpan.FromSeconds(30);  // Default: 30s (0 to disable, otherwise min 1s)

    // Broadcast
    configuration.BroadcastTimeout = TimeSpan.FromSeconds(10);  // Default: 10s

    // Path mapping
    configuration.PathProvider = new AttributeBasedPathProvider("ws");

    // Subject creation for client updates
    configuration.SubjectFactory = new DefaultSubjectFactory();

    // Update processing
    configuration.Processors = new ISubjectUpdateProcessor[] { /* custom processors */ };
});
```

### Client Configuration

```csharp
builder.Services.AddWebSocketSubjectClientSource<Device>(configuration =>
{
    // Connection
    configuration.ServerUri = new Uri("ws://localhost:8080/ws");  // Required
    configuration.ConnectTimeout = TimeSpan.FromSeconds(30);      // Default: 30s
    configuration.ReceiveTimeout = TimeSpan.FromSeconds(60);      // Default: 60s
    configuration.MaxMessageSize = 10 * 1024 * 1024;             // Default: 10 MB

    // Reconnection settings
    configuration.ReconnectDelay = TimeSpan.FromSeconds(5);       // Initial delay
    configuration.MaxReconnectDelay = TimeSpan.FromSeconds(60);   // Exponential backoff cap

    // Performance tuning
    configuration.BufferTime = TimeSpan.FromMilliseconds(8);      // Batch updates
    configuration.WriteBatchSize = 1000;    // Max properties per message

    // Write retry queue
    configuration.RetryTime = TimeSpan.FromSeconds(10);           // Retry interval
    configuration.WriteRetryQueueSize = 1000;                     // Buffer size (0 to disable)

    // Circuit breaker
    configuration.CircuitBreakerFailureThreshold = 5;             // Open after 5 consecutive failures
    configuration.CircuitBreakerCooldown = TimeSpan.FromSeconds(60); // Wait before retrying

    // Path mapping
    configuration.PathProvider = new AttributeBasedPathProvider("ws");

    // Subject creation for server updates
    configuration.SubjectFactory = new DefaultSubjectFactory();

    // Update processing
    configuration.Processors = new ISubjectUpdateProcessor[] { /* custom processors */ };
});
```

## Protocol

The WebSocket protocol uses a simple message envelope for all communication.

### Message Envelope

All messages use the same structure:

```
[MessageType, Payload]
```

- `MessageType`: Integer discriminator (0-4)
- `Payload`: Type-specific JSON payload

### Message Types

| Type | Value | Direction | Description |
|------|-------|-----------|-------------|
| Hello | 0 | Client -> Server | Client initiates connection |
| Welcome | 1 | Server -> Client | Server responds with initial state |
| Update | 2 | Bidirectional | Property changes |
| Error | 3 | Bidirectional | Error notification |
| Heartbeat | 4 | Server -> Client | Periodic liveness check with current sequence |

### Connection Sequence

```
Client                                 Server
   |                                      |
   |-------- WebSocket Connect ---------->|
   |                                      |
   |-------- Hello ---------------------->|
   |  [0, {version:1}]                    |
   |                                      |
   |              (server registers connection for broadcasts)
   |              (server reads current sequence under update lock)
   |              (server builds snapshot under update lock)
   |                                      |
   |<------- Welcome ---------------------|
   |  [1, {version:1, format:"json", state: SubjectUpdate, sequence: 5}]
   |                                      |
   |  (client sets expectedNext = 6)      |
   |                                      |
   |<------- Update ----------------------|  (queued broadcasts flushed after Welcome)
   |  [2, {sequence:6, root:..., subjects:...}]
   |                                      |
   |  (client verifies sequence==6, sets expectedNext=7)
   |  (client applies snapshot,           |
   |   then replays buffered updates)     |
   |                                      |
   |<------- Update ----------------------|  (server pushes changes)
   |  [2, {sequence:7, root:..., subjects:...}]
   |                                      |
   |-------- Update --------------------->|  (client writes changes, no sequence)
   |  [2, {root:..., subjects:...}]       |
   |                                      |
   |<------- Heartbeat -------------------|  (periodic, every 30s by default)
   |  [4, {sequence: 7}]                  |
   |                                      |
   |  (client checks: 7 < 8 → in sync)   |
```

#### Register-Before-Welcome Design

The server registers the connection for broadcasts **before** building and sending the Welcome snapshot. This follows the buffer-flush-load-replay pattern (see [Connectors](connectors.md)) and ensures eventual consistency:

1. **Register**: Connection is added to the broadcast list. Any concurrent property changes are **queued per-connection** along with their sequence numbers (not sent yet). The client does not receive any messages until the Welcome is sent.
2. **Snapshot**: The server builds the complete state snapshot under `_applyUpdateLock`, the same lock used when applying client updates. This ensures the snapshot is a consistent cut: every update applied before the lock is included, every update applied after will be sent as a separate Update message.
3. **Welcome**: The snapshot is sent to the client with the current sequence number. Immediately after (under the same send lock), queued updates are flushed, but only those with a sequence **greater than** the Welcome sequence are sent, since the snapshot already includes all earlier changes. After Welcome, any further broadcasts whose sequence is ≤ the Welcome sequence are also skipped. The client always sees Welcome as the first message, followed by only the updates that were not yet included in the snapshot.
4. **Buffer replay**: The client applies the snapshot as a baseline, then replays all buffered updates (received between connection and snapshot application) to catch up to current state.

The snapshot does not need to be fully up-to-date; it is just a baseline. The buffered updates are what guarantee correctness. After replay, the client is fully caught up and subsequent updates flow directly.

### Payload Structures

**HelloPayload**
```json
{
  "version": 1,
  "format": "json"
}
```

**WelcomePayload**
```json
{
  "version": 1,
  "format": "json",
  "state": { /* Complete SubjectUpdate */ },
  "sequence": 5
}
```

- `sequence`: Server's current sequence number at snapshot time. Clients initialize their expected next sequence to `sequence + 1`.

**HeartbeatPayload**
```json
{
  "sequence": 42
}
```

- `sequence`: Server's current sequence number (last broadcast batch). Does **not** increment the counter; it reflects the current value.

Example wire format: `[4, {"sequence": 42}]`

**ErrorPayload**
```json
{
  "code": 100,
  "message": "Property not found",
  "failures": [
    { "path": "Motor/Speed", "code": 101, "message": "Read-only" }
  ]
}
```

### Error Codes

| Code | Name | Description |
|------|------|-------------|
| 100 | UnknownProperty | Property not found |
| 101 | ReadOnlyProperty | Cannot write to read-only property |
| 102 | ValidationFailed | Validation error |
| 200 | InvalidFormat | Malformed message |
| 201 | VersionMismatch | Protocol version not supported |
| 500 | InternalError | Server error |

### SubjectUpdate Wire Format

See [Subject Updates](connectors-subject-updates.md) for details on the update format.

## Resilience

### Write Retry Queue

Write retry queue behavior (ring buffer, reconcile by commit order on reconnection) is provided by `SubjectSourceBase`. See [Connectors: Write Retry Queue](connectors.md#write-retry-queue). Configure via the client configuration:

```csharp
configuration.WriteRetryQueueSize = 1000;  // Buffer up to 1000 writes (default, 0 to disable)
configuration.RetryTime = TimeSpan.FromSeconds(10);
```

### Reconnection

The outer retry loop (buffer → listen → load initial state → replay → process) is handled by `SubjectSourceBase` (see [Pump Lifecycle](connectors.md#pump-lifecycle)). The WebSocket client adds exponential backoff with jitter within its monitor loop:

```csharp
configuration.ReconnectDelay = TimeSpan.FromSeconds(5);      // Initial delay
configuration.MaxReconnectDelay = TimeSpan.FromSeconds(60);  // Maximum delay
```

On reconnection, the client performs the Hello/Welcome handshake to obtain a state snapshot from the server. The base class then handles loading initial state, replaying buffered updates, and reconcile of queued writes by commit order (see [Connectors: Initialization Sequence](connectors.md#initialization)).

The circuit breaker pauses reconnection attempts after repeated failures:

```csharp
configuration.CircuitBreakerFailureThreshold = 5;               // Open after 5 consecutive failures (default)
configuration.CircuitBreakerCooldown = TimeSpan.FromSeconds(60); // Wait before retrying (default)
```

### Sequence Numbers and Gap Detection

The server maintains a monotonically increasing sequence counter that is incremented atomically (`Interlocked.Increment`) each time an update batch is broadcast. This enables clients to detect lost updates.

**Server behavior:**
- Each `BroadcastUpdateAsync` call increments the counter and sets the sequence in the `UpdatePayload` (e.g., `[2, {sequence:7, root:..., subjects:...}]`). The sequence is carried in `UpdatePayload`, which inherits from `SubjectUpdate`.
- The Welcome payload includes the current sequence at snapshot time. Clients initialize their expected next sequence to `welcome.sequence + 1`.
- Heartbeat messages include the current sequence in their payload but do **not** increment it.

**Client behavior:**
- On receiving an Update: the client reads the sequence from the `UpdatePayload`. If `sequence != expectedNextSequence`, the client logs a warning and exits the receive loop, triggering reconnection via the existing recovery flow.
- On receiving a Heartbeat: if `heartbeat.sequence >= expectedNextSequence`, the server has sent updates the client never received. The client exits the receive loop and reconnects.
- A heartbeat with `sequence < expectedNextSequence` means the client is fully caught up; no action is needed.
- A null or zero sequence is treated as "unassigned" for client-to-server messages which do not carry sequence numbers.

**Recovery flow on gap detection:**
Gap detected -> receive loop exits -> `RunMonitorLoopAsync` detects connection lost -> `StartBuffering` -> exponential backoff delay -> `ConnectAsync` -> Welcome with full state + new sequence -> `SubjectPropertyWriter.LoadInitialStateAndResumeAsync` calls the source's `LoadInitialStateAsync` to fetch the apply action, runs it under the buffer lock, and replays buffered updates. No new recovery logic is needed; the existing reconnection flow handles everything.

**Why only server-to-client messages carry sequence numbers:**
Client-to-server writes are covered by the write retry queue (ring buffer, oldest-dropped-when-full) and flush-before-load on reconnection. The server applies updates synchronously under a lock, so silent drops within the server are impossible.

### Heartbeat

The server periodically sends Heartbeat messages to all connected clients. This allows clients to detect lost updates even during quiet periods (no property changes).

```csharp
configuration.HeartbeatInterval = TimeSpan.FromSeconds(30);  // Default
configuration.HeartbeatInterval = TimeSpan.Zero;              // Disable heartbeats
// Anything positive must be at least WebSocketServerConfiguration.MinimumHeartbeatInterval (1s):
// a heartbeat is broadcast to every connected client, so a sub-second interval floods rather than probes.
```

- The heartbeat loop runs as a parallel task alongside the change queue processor.
- If a transient send failure occurs during heartbeat broadcast, the error is logged and the loop continues.
- Zombie connections (repeated send failures) are cleaned up during heartbeat broadcast, using the same logic as update broadcasts.

### Echo Behavior

The server broadcasts every update to **all** connected clients, including the client that sent the change. This is intentional:

- **Sequence number consistency**: Every client must see the same monotonic sequence progression. Skipping an update for the originator would create a gap, triggering a false reconnection.
- **Implicit acknowledgment**: The echo acts as a server-side ACK that the client's update was applied.
- **No correctness issue**: The client applies inbound updates with `ChangeOrigin.FromSource(this)`, so echoed values are deduplicated by the change tracking layer and do not trigger outbound writes or loops.

### Conflict Resolution

The system uses **last-write-wins (LWW)** at the server. If two clients modify the same property simultaneously, the last update to reach the server wins and is broadcast to all clients.

- All clients and the server converge to the same value after updates propagate (eventual consistency).
- No vector clocks, version stamps, or merge logic needed.
- Acceptable for the target use cases (IoT, industrial automation, UI binding) where properties represent current state rather than accumulated operations.

### Error Handling

Tiered error handling preserves connections when possible:

| Error | Response | Connection |
|-------|----------|------------|
| Unknown property | Log warning, send Error | Stays open |
| Read-only property | Send Error | Stays open |
| Validation failed | Send Error | Stays open |
| Malformed JSON | - | Disconnect |
| Version mismatch | Send Error in close frame | Disconnect |

## Diagnostics

Both built-in WebSocket connectors implement liveness monitoring and report through the shared model: the member tree, the three buffers, `LastError` stickiness and the read guarantees are described once in [Connector Diagnostics](connectors.md#connector-diagnostics). Before the first protocol-specific liveness observation, `IsOperational` can be `null`; after that observation, the connectors publish explicit `true` or `false` values. What follows is what is specific to WebSocket.

**`IsOperational` for the standalone server means the listener is accepting connections.** It is set once Kestrel has started and drops first in the teardown, so a server that has stopped accepting connections never reports that it is still serving. It also drops and rises again on every internal restart, where the inherited `StartTime` marks the current run of the hosted service and does not move.

**`IsOperational` for the client means the handshake completed and the receive loop is running.** It is set after the server's Welcome message has been accepted, so a socket that connected but failed version negotiation never counts as operational, and it drops on every way out of the receive loop: a close frame, a sequence or heartbeat gap, a socket error, a receive timeout, or teardown.

Neither connector measures throughput, so `Throughput.IncomingPerSecond` and `Throughput.OutgoingPerSecond` are both `null` rather than `0.0`.

The client's diagnostics are a plain `SourceDiagnostics` with no WebSocket specific additions. `OutboundRetries.Capacity` echoes `WriteRetryQueueSize`; at capacity 0 no writes are retained, but failed, terminally unconfirmed, and owned connect-window writes are still counted in `TotalDropped`. Writes made before the source claims their property remain unattributable; see [Known Limitations](connectors.md#known-limitations). The built-in client registers the `ClaimedPropertyCount` gauge, so it reports a measured value, including zero.

The standalone server's diagnostics are a `WebSocketServerDiagnostics`, which adds two members:

| Member | Meaning |
|---|---|
| `ConnectionCount` | Clients currently connected. |
| `CurrentSequence` | The sequence number most recently assigned to an outgoing message. A monotonic position in the message stream rather than a count of events, which is why it carries no `Total` prefix. See [Sequence Numbers and Gap Detection](#sequence-numbers-and-gap-detection). |

A server has no inbound buffer or retry queue, so only `OutboundChanges` applies there. It is registered while the change queue processor is running and its capacity is `null`, because that queue is unbounded.

**Embedded mode has no connector diagnostics.** `WebSocketSubjectHandler` is not an `ISubjectConnector`, and the change processor it runs deliberately does not register into any server's metrics, so an embedded handler cannot wire itself into a standalone server's numbers. The handler exposes the same two transport numbers directly as `ConnectionCount` and `CurrentSequence`; resolve it with `serviceProvider.GetRequiredKeyedService<WebSocketSubjectHandler>("/ws")`, keyed by the path you registered.

Both connector types are public but are registered under a private key, so pick them out of the registered hosted services with `serviceProvider.GetServices<IHostedService>().OfType<WebSocketSubjectServer>()` or `OfType<WebSocketSubjectClientSource>()`, or hold the instance if you constructed it yourself. The client is also reachable as an `ISubjectSource` through the source monitor's `SourceSubscription.Sources`, or through `property.TryGetSource(out var source)` on a property it owns.

## Thread Safety

**Server side**: Incoming updates from multiple clients are applied in serialized order. Each message is fully applied before the next one starts, ensuring no interleaving of property writes from different clients. Individual property writes use last-write-wins semantics. Multiple clients can connect and receive broadcasts concurrently.

**Client side**: Updates received from the server are not serialized with local property changes. If the application writes to a property while the client is applying an incoming server update, the two may race. In practice this is rarely an issue because property ownership is typically split (the server owns some properties, the client owns others).

## Lifecycle Management

Unlike MQTT and OPC UA connectors which maintain per-property topic/node caches that require cleanup on subject detach (see [Subject Lifecycle Tracking](tracking.md#subject-lifecycle-tracking)), the WebSocket connector synchronizes the entire subject graph as a unit. There are no per-property caches to clean up. The server builds a fresh snapshot for each new client connection, and broadcast updates are derived from the change tracking layer. Connection-level resources (WebSocket, send lock, cancellation tokens) are cleaned up when a client disconnects or the server stops.

## Known Limitations

- **Snapshot lock during client connection**: When a new client connects, the server builds a full state snapshot under the same lock used for applying updates. This blocks incoming updates for the duration of the snapshot, which is proportional to graph size. This is acceptable because new-client connections are infrequent relative to the update rate, but could become a concern with very large subject graphs and frequent client reconnections.

- **Broadcast timeout**: A slow client can delay broadcast completion for other clients. Broadcasts have a 10-second timeout to mitigate this. Sends that haven't completed continue in the background, and zombie detection cleans up persistently slow connections. However, very slow clients may still cause temporary backpressure before being removed. This should be revisited if it becomes a bottleneck in high-throughput scenarios.

## Future Extensibility

The protocol is designed for future enhancements:

- **MessagePack support**: `format` field in Hello/Welcome enables negotiation for 3-4x smaller payloads
- **Commands/RPC**: Message types 5-6 reserved for invoking methods on subjects
- **Subscriptions**: Message types 7-8 reserved for subscribing to specific subjects/properties
- **Message compression**: Per-message or per-frame compression to reduce bandwidth
- **Authentication/authorization hooks**: Token-based auth during handshake or per-message access control
- **Throughput counters**: Message throughput tracking

## Performance

The library includes optimizations:

- Batched outbound updates with configurable `BufferTime` to reduce per-message overhead
- `RecyclableMemoryStream` and `ArrayPool<byte>` pooling for read/write buffers
- Per-connection queuing during Welcome handshake to avoid blocking broadcasts
- Configurable `WriteBatchSize` to cap message size and control serialization latency

## Benchmark Results

Intel(R) Core(TM) Ultra 7 258V

```
Server Benchmark - 1 minute - [2026-02-18 22:19:57.430]

Total received changes:          1197999
Total published changes:         1196897
Process memory:                  366.27 MB (188.56 MB in .NET heap)
Avg allocations over last 60s:   78.49 MB/s

Metric                               Avg        P50        P90        P95        P99      P99.9        Max        Min     StdDev      Count
-------------------------------------------------------------------------------------------------------------------------------------------
Received (changes/s)            19966.69   19952.27   20330.10   20395.31   20694.54   20694.54   20694.54   19240.79     299.87          -
End-to-end latency (ms)            21.78      18.33      34.56      38.97      48.64     107.65     152.63       0.22       9.26    1197999
```

```
Client Benchmark - 1 minute - [2026-02-18 22:19:59.994]

Total received changes:          1198798
Total published changes:         1198000
Process memory:                  314.71 MB (166.21 MB in .NET heap)
Avg allocations over last 60s:   72.03 MB/s

Metric                               Avg        P50        P90        P95        P99      P99.9        Max        Min     StdDev      Count
-------------------------------------------------------------------------------------------------------------------------------------------
Received (changes/s)            19990.74   19933.24   20515.98   20677.66   21017.99   21017.99   21017.99   18971.91     399.01          -
End-to-end latency (ms)            22.45      19.17      36.27      40.33      51.50      70.56      85.93       0.24       9.65    1198798
```
