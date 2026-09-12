# TwinCAT ADS

The `Namotion.Interceptor.Ads` package provides integration between Namotion.Interceptor and Beckhoff TwinCAT PLCs via the ADS (Automation Device Specification) protocol, enabling bidirectional synchronization between C# objects and PLC variables. It operates as a client source, connecting to a running TwinCAT runtime.

## Key Features

- Bidirectional synchronization between C# objects and PLC variables
- Attribute-based mapping with `[AdsVariable]` for direct symbol path control
- Code-based fluent mapping (`AdsFluentMapperBuilder`) as a complete alternative to attributes
- Automatic notification-to-polling demotion when notification limits are exceeded
- Batch reads via `SumSymbolRead` with concurrent individual fallback (writes are always per symbol)
- Debounced rescan on connection restore, PLC state change, and symbol version change
- Circuit breaker pattern for connection resilience
- Write retry queue for buffering during disconnection
- Comprehensive diagnostics for monitoring connection health

## Client Setup

Connect to a TwinCAT PLC by registering a client source with `AddAdsSubjectClientSource`. The client automatically establishes connections, subscribes to variable changes, and synchronizes values with your C# properties.

```csharp
[InterceptorSubject]
public partial class PlcModel
{
    [AdsVariable("GVL.Temperature")]
    public partial double Temperature { get; set; }

    [AdsVariable("GVL.Speed")]
    public partial int Speed { get; set; }
}

builder.Services.AddAdsSubjectClientSource<PlcModel>("192.168.1.100");

// Use in application
var plc = serviceProvider.GetRequiredService<PlcModel>();
await host.StartAsync();
Console.WriteLine(plc.Temperature); // Read property synchronized with PLC
plc.Speed = 100; // Writes to PLC
```

### Registering a client source

There are three overloads. The first two pick the connection mode at the call site; the third gives full control.

**Embedded by host or IP.** Pass a `string` host. The connector runs an in-process AMS router and routes to that address, so no TwinCAT install is required:

```csharp
builder.Services.AddAdsSubjectClientSource<PlcModel>("192.168.1.100");
// optional: amsPort, amsNetId (AmsNetId?), connectorName
```

**System router by net id.** Pass an `AmsNetId`. The connector connects through the machine's existing AMS router:

```csharp
builder.Services.AddAdsSubjectClientSource<PlcModel>(AmsNetId.Local); // loopback
builder.Services.AddAdsSubjectClientSource<PlcModel>(AmsNetId.Parse("5.23.45.67.1.1")); // routed target
```

**Full control.** Pass a subject selector and a configuration provider to set any field on `AdsClientConfiguration`:

```csharp
builder.Services.AddAdsSubjectClientSource(
    subjectSelector: sp => sp.GetRequiredService<PlcModel>(),
    configurationProvider: sp => new AdsClientConfiguration
    {
        Host = "192.168.1.100",            // embedded mode; omit to use the system router by AmsNetId
        AmsNetId = AmsNetId.Parse("192.168.1.100.1.1"),
        AmsPort = 851,

        // Property mapping (optional - defaults to attribute-based)
        Mapper = AdsCompositeMapper.CreateDefault("ads"),

        // Timeouts
        Timeout = TimeSpan.FromSeconds(5),

        // Reading strategy
        DefaultReadMode = AdsReadMode.Auto,
        DefaultCycleTime = 100,        // Notification cycle time in ms
        DefaultMaxDelay = 0,           // Max delay for notification batching in ms
        MaxNotifications = 500,        // Max concurrent notifications before demotion
        PollingInterval = TimeSpan.FromMilliseconds(100),
        MaxConcurrentReads = 0,        // Individual reads in flight at once; 0 is no limit
        PrefetchSymbols = true,        // Fetch symbols and data types up front

        // Resilience
        CircuitBreakerFailureThreshold = 5,
        CircuitBreakerCooldown = TimeSpan.FromSeconds(60),
        WriteRetryQueueSize = 1000,
        HealthCheckInterval = TimeSpan.FromSeconds(5),
        RescanDebounceTime = TimeSpan.FromSeconds(1),

        // Performance tuning
        BufferTime = TimeSpan.FromMilliseconds(8),
        RetryTime = TimeSpan.FromSeconds(10),

        // Type conversion
        ValueConverter = new AdsValueConverter()
    });
```

## Connection Modes

How the connector reaches the PLC is decided by whether `Host` is set.

### Embedded router (set `Host`)

The connector runs Beckhoff's in-process `AmsTcpIpRouter`, adds a route `AmsNetId -> Host`, and connects through it. This is cross-platform and needs no system TwinCAT router, so it suits hosts without TwinCAT installed. `AmsNetId` is optional: for an IP host it is derived as `{Host}.1.1`; for a hostname it is required, because a net id cannot be derived from a name.

The router is process-wide and reference-counted across all embedded sources: it starts on first use and stops when the last source is disposed. Because it owns the host's AMS TCP port, it cannot run alongside a system TwinCAT router on the same host. `LocalAmsNetId` (the client net id the PLC sees) is honored only by the first source that starts the shared router; it defaults to the local IP + ".1.1". On an isolated host with no default network route, set `LocalAmsNetId` explicitly.

```csharp
builder.Services.AddAdsSubjectClientSource<PlcModel>("192.168.1.100"); // embedded, AmsNetId derived
```

### System router (`Host` null)

The connector connects by `AmsNetId` through the machine's existing AMS router (full TwinCAT, TwinCAT/BSD, or a router you configure via `RouterConfiguration`). No embedded router is started. `AmsNetId.Local` gives a loopback/shared-memory connection with very low overhead; a remote net id uses a route already present in that router. The connector does not add routes itself in this mode.

```csharp
builder.Services.AddAdsSubjectClientSource<PlcModel>(AmsNetId.Local);
```

### Setting up the route on the PLC

ADS is bidirectional and trust-based. The embedded router adds the client-to-PLC route for you, but that is only half of it: the PLC must also have a route back to this client and be configured to accept the connection. This is a one-time setup on the PLC, and it applies whenever you reach a real remote PLC (embedded mode, or system-router mode with a remote net id). A loopback connection (`AmsNetId.Local`) needs no route.

You need two values from the client:

- **Client AMS Net ID** - the `LocalAmsNetId` the embedded router presents to the PLC. By default it is this host's IP + ".1.1" (for example `192.168.1.50.1.1`). Set `LocalAmsNetId` explicitly when you want a fixed, known value (recommended, so the PLC route stays valid if the host IP changes).
- **Client IP** - the address the PLC can reach this host at.

On the PLC, add a static route to that client, using either method:

1. **TwinCAT route dialog.** On the PLC, open the TwinCAT system-tray icon and choose Router, Edit Routes, Add (or do this from TwinCAT XAE connected to the PLC). Enter:
   - Route name: any label.
   - AmsNetId: the client AMS Net ID.
   - Transport type: TCP/IP.
   - Address: the client IP.
   - Mark it Static so it survives a reboot.

   Adding the route authenticates with the PLC's credentials (its TwinCAT user and password).

2. **StaticRoutes.xml.** Edit the file on the PLC (Windows: `C:\TwinCAT\3.1\Target\StaticRoutes.xml`; TwinCAT/BSD: `/usr/local/etc/TwinCAT/3.1/Target/StaticRoutes.xml`) and add a route entry with the client route name, AMS Net ID, IP address, and `TCP_IP` transport, then restart the TwinCAT system service.

Also make sure:

- The PLC's firewall allows inbound TCP port **48898** (the AMS/ADS port).
- The `AmsNetId` you connect to is the PLC's actual net id. It is often the PLC IP + ".1.1", but confirm it in the PLC's TwinCAT router settings and set `AmsNetId` explicitly if it differs.
- On TwinCAT 3.1 (build 4024 and later) ADS-Secure may apply: the route's connection mode and credentials must match what the PLC requires. Configure the route on the PLC accordingly.

Once the PLC has a route to the client net id and IP and the firewall allows port 48898, the embedded client connects without any TwinCAT installation on the client host.

## Configuration Reference

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Host` | `string?` | null | PLC IP/hostname; when set, enables the embedded router and is the route target |
| `AmsNetId` | `AmsNetId?` | derived | Target net id; optional when Host is an IP (defaults to `{Host}.1.1`). Use `AmsNetId.Local` for an in-process connection |
| `LocalAmsNetId` | `AmsNetId?` | null | Embedded mode only: the client net id the PLC sees; defaults to local IP + .1.1 |
| `AmsPort` | int | 851 | AMS port for TwinCAT3 PLC runtime |
| `Mapper` | `IPropertyMapper<AdsPropertyMapping>` | attribute-based | Resolves each property's symbol path and ADS settings |
| `Timeout` | TimeSpan | 5s | ADS communication timeout |
| `DefaultReadMode` | AdsReadMode | Auto | Default read mode for variables without explicit config |
| `DefaultCycleTime` | int | 100 | Default notification cycle time in ms |
| `DefaultMaxDelay` | int | 0 | Default max delay for notification batching in ms |
| `MaxNotifications` | int | 500 | Max concurrent ADS notifications before demotion; 0 leaves no budget for `Auto` |
| `PollingInterval` | TimeSpan | 100ms | Polling timer interval for polled/demoted variables |
| `MaxConcurrentReads` | int | 0 | Reads in flight at once when sum commands cannot resolve the symbols; 0 is no limit, 1 is sequential |
| `PrefetchSymbols` | bool | true | Fetch the symbol table and data types when the loader is created, rather than on first symbol access |
| `WriteRetryQueueSize` | int | 1000 | Max queued write retries (0 to disable) |
| `HealthCheckInterval` | TimeSpan | 5s | Connection monitoring interval |
| `RescanDebounceTime` | TimeSpan | 1s | Coalesce rapid rescan requests |
| `BufferTime` | TimeSpan | 8ms | Batch inbound updates |
| `RetryTime` | TimeSpan | 10s | Failed write retry delay |
| `CircuitBreakerFailureThreshold` | int | 5 | Failures before circuit opens |
| `CircuitBreakerCooldown` | TimeSpan | 60s | Circuit breaker recovery period |
| `ValueConverter` | AdsValueConverter | new() | Type conversion handler |
| `RouterConfiguration` | IConfiguration? | null | Custom loopback port (testing) |

## Property Mapping

### Using [AdsVariable]

The `[AdsVariable]` attribute maps a property directly to an ADS symbol path. It extends `[Path]`, so it works with the standard `AttributeBasedPathProvider`.

```csharp
[InterceptorSubject]
public partial class PlcModel
{
    // Simple mapping
    [AdsVariable("GVL.Temperature")]
    public partial double Temperature { get; set; }

    // With per-property read mode and timing
    [AdsVariable("GVL.Speed", ReadMode = AdsReadMode.Notification, CycleTime = 50)]
    public partial int Speed { get; set; }

    // Polled variable with custom priority
    [AdsVariable("GVL.DiagCounter", ReadMode = AdsReadMode.Polled)]
    public partial long DiagCounter { get; set; }

    // Low-priority variable (demoted first when notification limit reached)
    [AdsVariable("GVL.AmbientTemp", Priority = 10)]
    public partial double AmbientTemperature { get; set; }
}
```

### Using [Path]

Alternatively, use the generic `[Path]` attribute with the connector name:

```csharp
[InterceptorSubject]
public partial class PlcModel
{
    [Path("ads", "GVL.Temperature")]
    public partial double Temperature { get; set; }
}
```

### [AdsVariable] Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `SymbolPath` | string | *required* | The ADS symbol path (e.g., "GVL.Temperature") |
| `ReadMode` | AdsReadMode | Auto | How this variable is read from the PLC |
| `CycleTime` | int | *global default* | Notification cycle time in ms |
| `MaxDelay` | int | *global default* | Max delay for notification batching in ms |
| `Priority` | int | 0 | Demotion priority; higher values are demoted first |

### Code-based (fluent) mapping

`AdsFluentMapperBuilder<TRoot>` is a complete, code-based alternative to `[AdsVariable]` attributes. Configure a type once with `ForType<T>().Map(...)`, then call `Build(...)` to produce an `AdsFluentMapper`. The mapping resolves everywhere that type appears in the object graph, including collection and dictionary elements.

`WithSymbolPath(...)` sets the relative symbol-path segment for the property (composed with parent segments into the full PLC symbol path, exactly like `[AdsVariable]`). `WithReadMode(...)`, `WithCycleTime(...)`, `WithMaxDelay(...)`, and `WithPriority(...)` set the per-property ADS settings.

```csharp
var fluentMapper = new AdsFluentMapperBuilder<PlcModel>()
    .ForType<PlcModel>()
        .Map(m => m.Temperature, b => b.WithSymbolPath("GVL.Temperature"))
        .Map(m => m.Speed,       b => b.WithSymbolPath("GVL.Speed").WithReadMode(AdsReadMode.Notification).WithCycleTime(50))
    .Build();
```

#### Composing with the connector

`Build()` produces a mapper you set on the `Mapper` property through the configuration overload. Layer it after the default attribute composite (`AdsCompositeMapper.CreateDefault()`) so fluent wins on conflicts and attribute and fluent mapping compose:

```csharp
builder.Services.AddAdsSubjectClientSource(
    subjectSelector: sp => sp.GetRequiredService<PlcModel>(),
    configurationProvider: sp => new AdsClientConfiguration
    {
        AmsNetId = AmsNetId.Parse("192.168.1.100.1.1"),
        Mapper = new AdsCompositeMapper(
            AdsCompositeMapper.CreateDefault(),
            new AdsFluentMapperBuilder<PlcModel>()
                .ForType<PlcModel>().Map(m => m.Speed, b => b.WithSymbolPath("GVL.Speed"))
                .Build())
    });
```

### Complex Hierarchies

The library automatically handles nested object hierarchies, traversing through subject references, collections, and dictionaries:

```csharp
[InterceptorSubject]
public partial class Factory
{
    public partial ProductionLine[] Lines { get; set; }
}

[InterceptorSubject]
public partial class ProductionLine
{
    [AdsVariable("GVL.Line.Speed")]
    public partial int Speed { get; set; }

    public partial Machine? MainMachine { get; set; }
}

[InterceptorSubject]
public partial class Machine
{
    [AdsVariable("GVL.Machine.Temperature")]
    public partial double Temperature { get; set; }
}
```

The subject graph loader recursively traverses:
- **Subject references**: Uses property segment as path prefix (dot-separated)
- **Subject collections**: Uses `[index]` notation for array elements
- **Subject dictionaries**: Uses `.{key}` notation for dictionary entries
- **Circular references**: Detected and skipped via HashSet tracking

## Read Modes

The connector supports three read modes that control how variables are read from the PLC:

### Notification (Push-Based)

The PLC sends real-time device notifications whenever a value changes. This is the most responsive mode with the lowest latency, but each notification consumes a TwinCAT resource.

```csharp
[AdsVariable("GVL.CriticalSensor", ReadMode = AdsReadMode.Notification, CycleTime = 10)]
public partial double CriticalSensor { get; set; }
```

### Polled (Pull-Based)

The client periodically reads values in batches using `SumSymbolRead`. This mode is efficient for many variables but introduces latency equal to the polling interval.

```csharp
[AdsVariable("GVL.DiagCounter", ReadMode = AdsReadMode.Polled)]
public partial long DiagCounter { get; set; }
```

### Auto (Default)

Starts as notification mode but automatically demotes to polling when the `MaxNotifications` limit is exceeded. This is the recommended default for most applications.

```csharp
[AdsVariable("GVL.Temperature")] // ReadMode = AdsReadMode.Auto by default
public partial double Temperature { get; set; }
```

### Automatic Demotion Algorithm

When the total number of notification-mode variables exceeds `MaxNotifications`, the system automatically demotes excess variables from notifications to polling using a two-pass algorithm:

1. **Pass 1**: Count all properties, categorizing them by read mode (Notification, Polled, Auto)
2. **Pass 2**: If `notificationCount > MaxNotifications`, demote Auto-mode properties to Polled:
   - Sort Auto-mode properties by **Priority** descending (higher values demoted first)
   - Tiebreaker: **CycleTime** descending (slower cycle times demoted first)
   - Demote until the notification count is within the limit

**Notification-mode properties are never demoted**: only Auto-mode properties participate in demotion. Polled-mode properties are never promoted.

**Example**: With `MaxNotifications = 500` and 600 Auto-mode variables, the 100 variables with the highest Priority (then slowest CycleTime) are demoted to polling.

Setting `MaxNotifications = 0` leaves no notification budget for Auto-mode variables, demoting all of them to polling. A variable that asks for `Notification` explicitly is never demoted and still gets one, so this is not the same as turning notifications off. It differs from `DefaultReadMode = Polled` only for the fluent mapper: the attribute mapper treats `Auto` as "unset", so an explicit `[AdsVariable(ReadMode = AdsReadMode.Auto)]` is already covered by `DefaultReadMode`, whereas `WithReadMode(AdsReadMode.Auto)` on the fluent builder is not.

## Read and Write Operations

### Batch reads

Reads use TwinCAT's `SumSymbolRead`, one round trip for the whole batch:

- **Initial state load**: reads all property values in a single batch
- **Polling**: reads all polled variables in a single batch per interval

### Writes are always per symbol

Writes never use `SumSymbolWrite`. A sum write addresses by index group and offset, which a property carrying `{attribute 'monitoring' := 'call'}` does not have, so the write lands as raw bytes past the variable and faults the PLC, and the sum command reports no error for it. The per-symbol value API calls the setter instead.

### Individual read fallback

Any `SumSymbolRead` failure falls back to one `ReadValue` per symbol, whether it returns an error, throws, or the symbols cannot be resolved by the type system. The round trip count is then fixed, so the reads are issued concurrently rather than one at a time, bounded by `MaxConcurrentReads`. This applies both to the initial state load and to each polling pass.

Only a failure the sum command cannot recover from latches, in practice `DeviceServiceNotSupported`, in which case the polling snapshot stops attempting sum reads until it is rebuilt. A transient failure such as `DeviceBusy` or a timeout degrades that one pass, because it says nothing about whether the next sum read succeeds.

### Notifications

A notification is registered per property through the raw ADS API, and the connector holds the handle it gets back. Every registration is delivered through a single `AdsNotificationEx` handler on the client's receive thread, so the notification count does not drive the thread count.

Holding the handles is what makes the release explicit. Nothing else deletes a device notification, so a re-scan that only forgot them would leave the previous registrations standing on the controller. With re-scans firing on connection restore, PLC state change and symbol version change, a long-lived connection would accumulate them until the PLC hit its own notification limit.

A property is registered only when its .NET type marshals to exactly what the PLC symbol holds. An enum registers as its underlying integer and a nullable as its underlying type. A string takes its length from the symbol's PLC string type, and an array its element count from the symbol's array type; a byte size alone cannot validate either, since the marshalled size of a string is its length plus one by definition and a byte array divides evenly into any width.

A type that cannot be marshalled falls back to polling rather than registering. That includes a `WSTRING`, which holds two bytes per character where the any-type path marshals single-byte text, and a nullable enum, whose boxed integer cannot be unboxed back into the property. So does a width that disagrees with the PLC: a narrower type is refused by the controller anyway, and a wider one would be accepted and read past the variable.

A registration the PLC refuses also falls that property back to polling on its own, so one symbol that cannot carry a notification does not affect any other.

### Symbols whose PLC type will not resolve

The TwinCAT type system cannot resolve a PLC enum whose definition was not loaded. Such a symbol is read as its underlying integer, picked by byte size, and written through the any-type path, which marshals from the .NET runtime type. The first failure is remembered per symbol path, so the doomed typed read happens once rather than every cycle, and handles are cached and dropped on a failed read, since a handle does not survive a reconnect or a download.

### Error classification

Write failures are classified as **transient** or **permanent**:

| Classification | Error codes |
|---------------|-------------|
| **Permanent** | SymbolNotFound, InvalidSize, InvalidData, ServiceNotSupported, InvalidAccess, InvalidOffset |
| **Transient** | PortNotFound, MachineNotFound, ClientPortNotOpen, DeviceError, Timeout, Busy |
| **Unknown** | All other codes, treated as transient because that is the safer way to be wrong |

Classification decides only the `TransientCount` and `PermanentCount` carried on `AdsWriteException`. It does not decide what is reported: **every change that did not reach the PLC is returned in `WriteResult.FailedChanges`**, permanent ones included. `FailedChanges` has to stay complete or the write retry queue and the source transaction writer both treat an unlisted change as written, which is what makes `TransactionFailureHandling.Rollback` able to fire at all. The OPC UA connector reports failures the same way.

That covers a permanently rejected symbol write, a property that is no longer registered, a value converter that throws, and a failed any-type write for the unresolvable types above. Unbounded retrying is prevented by `WriteRetryQueueSize` rather than by dropping: the queue is a ring buffer that evicts oldest and counts the evictions.

The one change that is deliberately **not** reported is a null property value. ADS has no null, so there is nothing to write, retrying could only produce the same outcome, and a later non-null assignment arrives as its own change. It is logged at Debug level.

The ADS error code is read from `AdsErrorException.ErrorCode`, never from `Exception.HResult`: `HResult` carries the generic managed `0x80131500` for every ADS error, so classifying on it would put every error in the same bucket.

## Type Conversions

The `AdsValueConverter` handles bidirectional type conversion between PLC types and .NET types:

| PLC Type | .NET Type | Direction |
|----------|-----------|-----------|
| `DATE_AND_TIME` (DateTime) | DateTimeOffset | PLC → .NET |
| DateTimeOffset | DateTime (UTC) | .NET → PLC |

All other types pass through unchanged. For custom type mappings, extend `AdsValueConverter`:

```csharp
public class CustomAdsValueConverter : AdsValueConverter
{
    public override object? ConvertToPropertyValue(
        object? adsValue, RegisteredSubjectProperty property)
    {
        if (property.Type == typeof(MyEnum) && adsValue is int intValue)
            return (MyEnum)intValue;
        return base.ConvertToPropertyValue(adsValue, property);
    }

    public override object? ConvertToAdsValue(
        object? propertyValue, RegisteredSubjectProperty property)
    {
        if (propertyValue is MyEnum enumValue)
            return (int)enumValue;
        return base.ConvertToAdsValue(propertyValue, property);
    }
}
```

## Resilience

### Connection Retry with Circuit Breaker

The connector automatically retries connections when the PLC is unavailable. A circuit breaker prevents resource exhaustion during prolonged outages.

```csharp
new AdsClientConfiguration
{
    CircuitBreakerFailureThreshold = 5,          // Open after 5 consecutive failures
    CircuitBreakerCooldown = TimeSpan.FromSeconds(60), // Wait before retrying
    HealthCheckInterval = TimeSpan.FromSeconds(5)       // Time between retry attempts
}
```

**Behavior:**
- Connection attempts use `ConnectWithRetryAsync`, retrying on a fixed `HealthCheckInterval` delay. There is no exponential backoff, so size that value for the outage you expect to ride out
- After `CircuitBreakerFailureThreshold` consecutive failures, the circuit breaker opens
- While open, connection attempts are skipped until the cooldown period expires
- Successful connection resets the circuit breaker

### Write Retry Queue

The client automatically queues write operations when the connection is lost. Queued writes are flushed in FIFO order when the connection is restored. This is provided by `SubjectSourceBase`.

```csharp
new AdsClientConfiguration
{
    WriteRetryQueueSize = 1000 // Buffer up to 1000 writes (default)
}
```

- Ring buffer semantics: drops oldest when full
- Automatic flush after reconnection
- Set to 0 to disable

### Write Behavior During Rescan

When a rescan is in progress (triggered by connection restore, PLC state change, or symbol version change), the symbol path cache is temporarily cleared. Writes that arrive during this window are handled as follows:

- **Unresolved symbol paths** (cache temporarily cleared): reported as failures and queued for retry. Once the rescan completes and the symbol cache is rebuilt, the retry succeeds.
- **Transient ADS errors** (timeout, busy, port not found): queued for retry via the write retry queue.
- **Permanent ADS errors** (symbol not found, invalid size/data): also reported and queued, per [Error classification](#error-classification). They are not dropped; `WriteRetryQueueSize` bounds how long they are retried, and the queue evicts oldest and counts the evictions.

This ensures configuration changes and command triggers are not silently lost during a rescan window.

### Debounced Rescan

When the connection state changes, the PLC enters Run state, or the symbol version changes, the connector triggers a full rescan. Multiple rapid events (common during reconnection) are coalesced into a single rescan using debouncing:

1. Event handler calls `RequestRescan()`, recording the timestamp and signaling a semaphore
2. The `ExecuteAsync` background loop wakes up and waits for the debounce period to elapse
3. If new events arrive during the debounce wait, the timer restarts
4. After the debounce period, a single `FullRescan()` executes

A full rescan:
1. Clears all existing subscriptions and caches
2. Recreates the symbol loader from the current connection
3. Reloads the subject graph (property → symbol path mappings)
4. Re-registers all notification and polling subscriptions
5. Loads initial state and replays buffered updates

```csharp
new AdsClientConfiguration
{
    RescanDebounceTime = TimeSpan.FromSeconds(1) // Coalesce events within 1 second
}
```

### Connection Events

The connector responds to four connection events:

| Event | Response |
|-------|----------|
| **Connection Restored** | Start buffering updates, request debounced rescan |
| **Connection Lost** | Start buffering updates (pause direct writes) |
| **PLC Entered Run State** | Request debounced rescan |
| **Symbol Version Changed** | Request debounced rescan |

### First-Occurrence Error Logging

To reduce log noise during sustained error conditions, the connector uses a first-occurrence logging pattern:

- **First occurrence**: Logged at Warning level
- **Subsequent occurrences**: Logged at Debug level
- **After recovery**: The first-occurrence flag is cleared, so the next error is logged at Warning again

This applies to connection failures, symbol lookup failures, and batch polling errors.

## Diagnostics

Monitor client health in production via the `Diagnostics` property on the source:

```csharp
var source = serviceProvider.GetRequiredKeyedService<AdsSubjectClientSource>(key);
var diagnostics = source.Diagnostics;

Console.WriteLine($"Connected: {diagnostics.IsConnected}");
Console.WriteLine($"PLC State: {diagnostics.State}");
Console.WriteLine($"Notifications: {diagnostics.NotificationVariableCount}");
Console.WriteLine($"Polled: {diagnostics.PolledVariableCount}");
Console.WriteLine($"Reconnection Attempts: {diagnostics.TotalReconnectionAttempts}");
Console.WriteLine($"Circuit Breaker Open: {diagnostics.IsCircuitBreakerOpen}");
Console.WriteLine($"Last poll pass: {diagnostics.Polling.LastPassDurationMilliseconds} ms");
```

`AdsClientDiagnostics` derives from `SourceDiagnostics`, so everything a source reports is available alongside the ADS-specific members above, including `IsOperational`, `ClaimedPropertyCount`, `OutboundRetries` and `InboundBuffer`. See [connectors-monitoring.md](connectors-monitoring.md).

### Diagnostic Properties

| Property | Type | Description |
|----------|------|-------------|
| `State` | AdsState? | Current PLC state (Run, Stop, etc.) |
| `IsConnected` | bool | Whether the ADS client is connected |
| `NotificationVariableCount` | int | Active notification subscriptions |
| `PolledVariableCount` | int | Active polling subscriptions |
| `TotalReconnectionAttempts` | long | Total reconnection attempts since startup |
| `SuccessfulReconnections` | long | Successful reconnections |
| `FailedReconnections` | long | Failed reconnections |
| `Polling` | AdsPollingDiagnostics | Individual-read poll passes: `TotalPasses`, `TotalFailedReads`, `LastPassDurationMilliseconds`, `LastPassSymbolCount`. Stays at zero while sum reads serve the batch |
| `LastConnectedAt` | DateTimeOffset? | Last successful connection time |
| `IsCircuitBreakerOpen` | bool | Whether the circuit breaker is currently open |
| `CircuitBreakerTripCount` | long | Number of times the circuit breaker has tripped |

## Thread Safety

The connector ensures thread-safe operations across all ADS interactions:

- Property caches use `ConcurrentDictionary` with `PropertyReference.Comparer`
- Reconnection and state counters use `Interlocked` operations
- The rescan signal uses `SemaphoreSlim` with a capacity of 1
- Subscription and polling collections are safely cleared and rebuilt during rescan

Property updates from ADS notifications and polling callbacks are applied via `SubjectPropertyWriter`, which handles buffering during initialization and ensures correct ordering.

## Lifecycle Management

The TwinCAT connector hooks into the interceptor lifecycle system (see [Subject Lifecycle Tracking](tracking.md#subject-lifecycle-tracking)) to clean up resources when subjects are detached via `SourceOwnershipManager`.

### Automatic Cleanup on Subject Detach

When a subject is detached from the object graph:

- The subject's properties stop being notification-backed. The device notifications themselves are released when the subscription set is cleared, on the next re-scan or at shutdown; a notification arriving in between is dropped because the property is no longer registered
- Properties are removed from the batch polling collection (and polling is marked dirty)
- Property-to-symbol-path cache entries are removed
- Source ownership is released

### Automatic Cleanup on Property Release

When an individual property is released:

1. The property stops being notification-backed (see above for when its device notification is released)
2. Property is removed from the polled collection (if polling mode)
3. The property-to-symbol-path lookup is cleared

## Architecture

```
┌──────────────────────────────────────────────────┐
│  Subject Graph (with [AdsVariable] attributes)   │
└──────────────┬───────────────────────────────────┘
               │
               ▼
┌──────────────────────────────────────────────────┐
│  AdsSubjectClientSource                      │
│  (BackgroundService + ISubjectSource)            │
│                                                  │
│  ├─ AdsConnectionManager                         │
│  │   ├─ AdsClient (connection to PLC)            │
│  │   ├─ CircuitBreaker (retry logic)             │
│  │   └─ Events: ConnectionRestored/Lost,         │
│  │            AdsStateChanged, SymbolVersion     │
│  │                                               │
│  ├─ AdsSubscriptionManager                       │
│  │   ├─ Notification subscriptions               │
│  │   ├─ Batch polling (SumSymbolRead)            │
│  │   └─ Auto-demotion algorithm                  │
│  │                                               │
│  ├─ AdsSubjectLoader                             │
│  │   └─ LoadSubjectGraph() → symbol paths        │
│  │                                               │
│  └─ ExecuteAsync loop                            │
│      └─ Debounced rescan on events               │
└──────────────────────────────────────────────────┘
```

### Initialization Sequence

1. `StartListeningAsync()` connects to the PLC via `ConnectWithRetryAsync()`
2. `FullRescan()` loads the subject graph and registers subscriptions
3. `LoadInitialStateAsync()` batch-reads all property values from the PLC
4. The `ExecuteAsync` background loop handles health checks and debounced rescans
5. Inbound notifications and polling updates are applied via `SubjectPropertyWriter`
6. Outbound property changes are written via `WriteChangesAsync()`, one symbol at a time

## Known Limitations

The following items are known limitations of the current implementation. They are tracked for future improvement.

### No active health probing

The connector relies on the `AdsClient`'s internal connection state machine for reconnection. If the `AdsClient` instance itself becomes unresponsive (e.g., due to a Beckhoff SDK bug or ADS router restart), no `ConnectionStateChanged` event fires and the system stays disconnected. `RunRescanLoopAsync` already wakes on the `HealthCheckInterval` and would be the natural place to add active health probing, for example a periodic `ReadStateAsync` to detect a dead `AdsClient` and recreate it.

### Open work

Reviewed and deliberately deferred rather than fixed, so they are visible to whoever picks this up next.

| Item | Why it is deferred |
|---|---|
| The embedded router is started fire-and-forget (`_ = _router.StartAsync(...)` in `AdsEmbeddedRouter`) with no readiness wait and no error surface. If AMS TCP port 48898 is already taken, typically because TwinCAT is installed, the lease looks valid and every connect fails with an opaque timeout instead. | Fixing it properly means designing a readiness handshake and deciding what a failed router should do to the sources depending on it. That is a design change, not a patch. |
| `AdsEmbeddedRouter.DefaultLocalNetId()` derives the local net id from a UDP socket "connected" to `8.8.8.8`. It throws with no default route and picks the internet-facing NIC, which need not be the PLC-facing one. | Same design discussion as above. Set `LocalAmsNetId` explicitly to avoid it. |
| Only the first lease's `LocalAmsNetId` takes effect. The router is process-wide and reference-counted, so a second source configured with a different local net id silently gets the first one. | Documented here instead of changed, since per-source routers would forfeit the pooling the design depends on. |
| The TwinCAT symbol and type system is not documented as thread-safe, and `Instance.DataType` resolves lazily without synchronisation. The connector touches symbols from the write path, the re-scan thread, the polling thread, and up to `MaxConcurrentReads` parallel read tasks. | Nothing in this repository can make a third-party type system thread-safe. Set `MaxConcurrentReads = 1` if you suspect it. |
| `Namotion.Interceptor.Ads.Tests` has no public-API snapshot test, unlike every sibling connector. | Repo-convention chore, better done in its own change so the generated baseline is reviewable on its own. |

### Null value write behavior

When a C# property value converts to `null` via `AdsValueConverter.ConvertToAdsValue`, the write is silently skipped with a Debug-level log message. PLCs generally do not have a concept of `null`, so passing `null` to the Beckhoff SDK could cause unexpected behavior. If you need to write a "zero" or default value, ensure your `AdsValueConverter` returns a non-null default instead of `null`.
