# Connector Tester

The Connector Tester covers three aspects of connector quality:

- **Resilience**: eventual consistency holds under kill/disconnect chaos (chaos profiles).
- **Load**: throughput and latency meet expectations at high change rates (load profiles).
- **Memory**: no leaks over extended runs. `cycles.csv` records post-GC heap size per cycle for trend analysis.

Run it locally before merging connector work that could affect correctness or throughput. It does not run in CI, and the mode and duration depend on the change, so see [How long to run](#how-long-to-run).

## Quick Start

Run all commands from the repository root. Each run creates a timestamped directory under `logs/`, so previous runs are preserved automatically.

```bash
# Chaos test: correctness under kill/disconnect disruptions (exits with code 1 on failure)
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile opcua-chaos --configuration Release
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile mqtt-chaos --configuration Release
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile websocket-chaos --configuration Release

# Load test: throughput and latency at 20k changes/sec
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile opcua-load --configuration Release
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile mqtt-load --configuration Release
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile websocket-load --configuration Release
```

**Chaos profiles** inject kill/disconnect faults and verify all participants converge to identical state. **Load profiles** push high throughput (configurable via `ValueMutationRate`) and report latency percentiles, memory, and allocation rate. Both modes use the same verification cycle: mutate, pause, compare snapshots. Whichever mode the change warrants has to pass before merging, run for long enough to mean something.

The tester runs indefinitely until a cycle fails (exit code 1) or you stop it with Ctrl-C (exit code 0).

### How long to run

Neither mode runs in CI or stops on its own. Pick by risk: reconnection, session handling and write ordering want chaos; batching, queueing and hot path allocation want load.

**Chaos** is counted in cycles, not minutes, a cycle being a one minute mutate phase plus a convergence check. Five profiles rotate round robin, so a short run leaves most unseen: a hundred cycles is a starting point, several hundred for a change you would call dangerous. Failures look like one bad convergence after many good ones, which is why stopping early is how you miss them.

**Load** answers two questions. Throughput and latency come from a single fifteen minute cycle; memory needs eight or more, read as a post-GC heap trend in `cycles.csv`, because one cycle shows a level and a leak is a trend.

Narrowing `ChaosProfiles` to `full-chaos` reaches a known failure sooner, so it helps when reproducing one, but it is not a shorter verification: it drops the `no-chaos` baseline, which catches convergence bugs unrelated to disruption, and `client-a-only`, the only profile checking that one client survives another going down.

## How It Works

The tester hosts a server and one or more clients in a single process, connected via the selected connector (OPC UA, MQTT, or WebSocket). Each participant has its own `TestNode` object graph and `MutationEngine` that continuously modifies properties. A `VerificationEngine` orchestrates repeating mutate/converge cycles: mutations run for a configured duration, then all engines pause and snapshots are compared. If all participants have identical state, the cycle passes. If not, the process logs the diff and exits with code 1.

```
                         +------------------+
                         |     Server       |
                         |  TestNode graph  |
                         |  MutationEngine  |
                         |  ChaosEngine?    |
                         +--------+---------+
                                  |
                          Connector (OPC UA / MQTT / WebSocket)
                                  |
                 +----------------+----------------+
                 |                                 |
          +------+------+                   +------+------+
          |   Client A  |                   |   Client B  |
          |  TestNode   |                   |  TestNode   |
          |  Mutation   |                   |  Mutation   |
          |  Chaos?     |                   |  Chaos?     |
          +-------------+                   +-------------+

    MutationEngine: RandomMutationEngine (chaos) or BatchMutationEngine (load)
    VerificationEngine orchestrates mutate/converge cycles
    TestCycleCoordinator pauses all engines during convergence
    PerformanceProfiler collects latency/throughput/memory metrics
```

### Mutation Engines

- **RandomMutationEngine** (chaos profiles, `NumberOfBatches = 0`): Picks a random node and random property, one at a time, at the configured `ValueMutationRate`. Good for chaos testing where mutation patterns should be unpredictable. When `UseTransactions` is enabled, each tick's batch of mutations is wrapped in a single transaction.

- **BatchMutationEngine** (load profiles, `NumberOfBatches > 0`): Mutates `ValueMutationRate` nodes per second, spread across `NumberOfBatches` parallel batches within 1-second windows. Each participant mutates a single fixed property to avoid subscription coalescing. When `UseTransactions` is enabled, each batch is wrapped in a transaction and runs sequentially.

### Mutate/Converge Cycle

Each test cycle has two phases:

1. **Mutate phase**: All MutationEngines and ChaosEngines run concurrently for `MutatePhaseDuration`.

2. **Converge phase**: The VerificationEngine pauses all engines via the TestCycleCoordinator, recovers any active chaos disruptions, waits a grace period (20s for OPC UA) for reconnection, then polls snapshots every 5 seconds. `SnapshotComparer.Capture` produces a normalized, deterministic JSON per participant. Structural property timestamps (Collection, Dictionary, Object) are stripped since they reflect local creation time. Value property timestamps are preserved and must converge. A null timestamp on either side matches any value (legitimate after server rebuild or when the equality interceptor suppresses a redundant write).

A cycle **passes** when all participant snapshots match. It **fails** if the convergence timeout expires. On failure, the process writes per-participant JSON snapshots to disk, logs per-property diffs with write timestamps, runs a re-sync diagnostic, gracefully shuts down all hosted services, and exits with code 1.

### Chaos via IFaultInjectable

Each connector implements `IFaultInjectable` (separate from the production `ISubjectConnector` interface) with a single `InjectFaultAsync(FaultType, CancellationToken)` method supporting two chaos modes:

- **Kill** (`FaultType.Kill`): Hard kill. Stops the connector entirely. The background service loop auto-restarts.
  - *OPC UA Server*: Cancels the current attempt's loop token and closes transport listeners (TCP RST to all clients) before shutting the server down and restarting. The backoff after a failed start runs inside the attempt, so a kill arriving during it is accepted and cancels a token whose work has already ended: it does nothing for as long as the backoff lasts, which after repeated failures is up to 32 seconds. Only a kill landing between one attempt being released and the next being created has nothing to cancel and is dropped, and that call returns successfully having done nothing.
  - *OPC UA Client*: Attempts graceful session close, then kills the transport channel. Health check detects missing session and triggers full reconnection.
  - *MQTT Server*: Cancels the current attempt's loop token, so the processing loop exits and the broker restarts. Its backoff after a failed start behaves like the OPC UA server's, over a fixed five seconds.
  - *MQTT Client*: Cancels the current iteration's token, so the connection monitor exits and the transport is replaced. The replacement loop publishes no current attempt, so a second Kill during teardown, reconnect backoff, or replacement connection attempts has none to cancel and is dropped.
  - *WebSocket Server*: Cancels the current attempt's loop token, triggering full teardown and rebuild of the Kestrel HTTP listener. The attempt is released before the five second backoff a failed attempt takes rather than after it, so a kill arriving anywhere in that window has no attempt to cancel and is dropped: the call returns successfully having done nothing.
  - *WebSocket Client*: Cancels the current iteration's token; the monitor loop's kill clause aborts the WebSocket and reconnects. A kill landing between iterations is dropped the same way the MQTT client drops one, and the reconnect backoff runs inside the iteration, so a kill during it is honoured.

- **Disconnect** (`FaultType.Disconnect`): Soft kill. Breaks the transport connection without stopping the connector. Lets the connector's built-in reconnection logic detect the failure and recover.
  - *OPC UA Server*: Delegates to Kill (no meaningful "soft disconnect" for a multi-connection server).
  - *OPC UA Client*: Disposes the transport channel. Keep-alive failure triggers `SessionReconnectHandler.BeginReconnect`, exercising the session preservation/transfer path.
  - *MQTT Server*: Enumerates connected clients and disconnects each one individually.
  - *MQTT Client*: Calls `DisconnectAsync()` on the MQTT client for a graceful disconnect.
  - *WebSocket Server*: Closes all client connections via `CloseAllConnectionsAsync()`.
  - *WebSocket Client*: Aborts the underlying `ClientWebSocket`, triggering reconnection.

The ChaosEngine picks an action based on the configured `Mode` ("kill", "disconnect", or "both"). In "both" mode, each event randomly chooses between kill and disconnect. After the action, the engine holds a "disruption window" for a random duration (representing the outage period for verification purposes).

### Key Design Decisions

- **Global mutation counter**: A static `Interlocked.Increment` counter ensures every mutation produces a globally unique value, preventing the equality interceptor from dropping duplicate changes.
- **Explicit timestamp scoping**: Mutations use `SubjectChangeContext.WithChangedTimestamp()` so all interceptors and change queue observers see the same timestamp.
- **Shutdown timeout**: The OPC UA server's `ShutdownServerAsync` wraps `application.StopAsync()` with a 10s timeout to prevent hang when clients keep reconnecting during graceful shutdown.

## Supported Connectors

| Connector | Status | Notes |
|-----------|--------|-------|
| OPC UA | Working | Server kill drops TCP connections, client kill abandons session. 1 min convergence timeout. `decimal` round-trips through `double`. |
| MQTT | Working | Server and client kill/disconnect. 2 min convergence timeout. |
| WebSocket | Working | Server kill cancels the current attempt, client kill aborts socket. Sequence gap detection triggers reconnection. |

### Connector-Specific Behaviors

**OPC UA**: Uses `OpcUaValueConverter` for type mapping. `decimal` values lose precision beyond ~15 significant digits due to `decimal` -> `double` -> `decimal` round-trip. `BufferTime=100ms` batches changes. Server chaos closes transport listeners before dispose, so clients get an immediate TCP RST rather than waiting for keep-alive timeout. Client chaos disposes the session without `CloseAsync`, simulating an abrupt disconnection.

**MQTT**: Uses server-authoritative relay pattern where client publishes are intercepted, applied to the server model, and re-published to all clients. Ticks-based timestamp serialization (`UtcTicks`) for full precision. QoS=AtLeastOnce with retained messages.

**WebSocket**: Uses Hello/Welcome handshake for initial state delivery. Server broadcasts all changes to all clients (including originator) with monotonic sequence numbers. Client tracks sequences and triggers reconnection on gap detection. Server kill cancels the current attempt, and the still-running background loop rebuilds the listener; client kill aborts the underlying `ClientWebSocket`. Disconnect mode closes all server connections or aborts the client socket respectively. Circuit breaker (5 failures, 60s cooldown) pauses reconnection during prolonged outages.

## Running

### Multi-Process Mode

Run server and client in separate processes for isolated load testing:

```bash
# Terminal 1: Run server only
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile opcua-load --configuration Release -- --participant server

# Terminal 2: Run client only
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile opcua-load --configuration Release -- --participant client
```

When `--participant` is specified, only the named participant starts and the verification engine is skipped. Mutations run continuously with performance metrics.

This isolates each side's CPU and throughput and stops one shared heap hiding which side grew, but it is not the mode for measuring memory: without the verification engine there are no cycles and no `cycles.csv`, and `HeapMB` in `performance-{participant}.csv` is sampled without forcing a collection, so it is not leak evidence. Split the run for throughput and attribution, and use a single-process run for the post-GC heap trend.

### What to Look For

**Success**: Each cycle prints `PASS` with convergence time and chaos event counts:
```
=== Cycle 1: PASS (converged in 5.1s, cycle 85s) ===
--- Cycle 1 Statistics ---
Duration: 85s (converged in 5.1s)
Total mutations: 17,200 | Total chaos events: 7
  server: 11,300 value mutations, 0 structural mutations
  client-a: 2,950 value mutations, 0 structural mutations
  client-b: 2,950 value mutations, 0 structural mutations
  client-b: disconnect at 21:08:37 (2.8s)
  server: kill at 21:08:38 (5.4s)
```

**Failure**: Prints `FAIL`, runs failure diagnostics, then exits with code 1:

```
=== Cycle 3: FAIL (did not converge within 00:01:00) ===
Snapshot [server] written to logs/2026-02-08T22-40-00Z-opcua-chaos/cycle-0003-fail-server.json
Snapshot [client-a] written to logs/2026-02-08T22-40-00Z-opcua-chaos/cycle-0003-fail-client-a.json
  ROOT.IntValue: server=42 (written 12:34:56.789), client-a=37 (written 12:34:55.110)
Re-sync check: client-a converged after applying reference complete update -> transient delivery gap
```

The per-cycle JSON files are formatted (indented) and can be diffed with any text tool. The `cycle-NNNN-fail-{participant}.json` files are the canonical artifact for investigating divergence.

### Findings Log

`logs/{run}/findings.log` records non-failure observations during passing cycles. Two finding types:

- **`slow-convergence`**: convergence took >10s with no chaos active. May indicate a performance regression.
- **`null-timestamp`**: the null-timestamp rule forgave a mismatch (one participant had a write timestamp, the other did not). Typically happens after a server rebuild or when the equality interceptor suppressed a redundant write. Not a failure, but worth investigating if frequent.

### Chaos Events Log

`logs/{run}/chaos-events.csv` records every chaos disruption with columns: Timestamp, Cycle, Participant, FaultType, DurationSeconds. Use this to correlate chaos events with performance anomalies or convergence delays across long-running tests.

### Log Files

Each run creates a timestamped directory under `logs/`. Previous runs are preserved automatically. The most recent run is always the last directory when sorted alphabetically (the timestamp format sorts chronologically): `ls logs/ | tail -1`.

```
logs/
  2026-02-08T22-40-00Z-opcua-chaos/
    cycles.csv
    chaos-events.csv
    findings.log
    performance-server.csv
    performance-client-a.csv
    cycle-0001-pass.log
    cycle-0002-pass.log
    cycle-0003-FAIL.log
    cycle-0003-fail-server.json
    cycle-0003-fail-client-a.json
  2026-02-09T10-15-00Z-mqtt-chaos/
    ...
```

Cycle log files are created as `pending` and renamed to `pass` or `FAIL` on completion. Each contains all INFO+ log output for that cycle. To prevent disk exhaustion during long-running tests, only the 50 most recent passing log files are kept per run. FAIL logs are always preserved.

**Use the log files to analyze results and diagnose problems.** On failure, the `FAIL` log contains full snapshot diffs showing exactly which participants diverged and what their state was. Look for:
- Chaos event timing (`Chaos: force-killing ...`) to correlate disruptions with convergence delays.
- Reconnection logs (`OPC UA server connection lost. Beginning reconnect...`, `SDK reconnection initialization completed successfully.`) to verify recovery.
- Mutation counts in the statistics block to confirm all engines were active.
- Snapshot diffs at the end of failed cycles to identify which properties or participants didn't converge.

### Performance Logs

Performance metrics are written to `performance-{participant}.csv` per participant. Each file has a header row and one data row per reporting interval. Columns: Timestamp, Participant, Cycle, Received/s, Received-Average, Received-P50, Received-P90, Received-P95, Received-P99, Received-P999, Received-Max, Received-Processing, Published, Received, CPU%, ProcessMB, HeapMB, AllocationMB/s. HeapMB in performance logs is sampled at reporting time without forcing GC, so it fluctuates with allocation patterns and is not suitable for leak detection.

### Cycle Logs

`cycles.csv` records one row per verification cycle. Columns: Timestamp, Cycle, Result, Profile, MutateSeconds, ConvergeSeconds, CycleSeconds, ValueMutations, StructuralMutations, ChaosEvents, HeapMB, ProcessMB. HeapMB is measured after a full GC with LOH compaction, giving a stable post-GC baseline. Use this column for memory leak detection: a steady upward trend across cycles indicates a leak.

## Configuration

Configuration is loaded from `appsettings.json` with environment-specific overrides (e.g., `appsettings.opcua-chaos.json`). The root section is `"ConnectorTester"`. Use `--launch-profile` to select a profile (e.g., `--launch-profile opcua-chaos`), which sets `DOTNET_ENVIRONMENT` and loads the corresponding `appsettings.{environment}.json` file.

See `appsettings.opcua-chaos.json` and `appsettings.opcua-load.json` for examples.

| Key | Default | Description |
|-----|---------|-------------|
| `Connector` | `"opcua"` | `"opcua"`, `"mqtt"`, or `"websocket"` |
| `MutatePhaseDuration` | `00:01:00` | How long mutations run before convergence check |
| `ConvergenceTimeout` | `00:01:00` | Max wait for snapshots to match |
| `CollectionCount` | `20` | Collection children in the test graph |
| `DictionaryCount` | `10` | Dictionary entries in the test graph |
| `NumberOfBatches` | `0` | `0` = random mutations (chaos), `> 0` = batched mutations (load) |
| `MetricsReportingInterval` | `00:01:00` | How often performance metrics are logged |
| `Server.ValueMutationRate` | `50` | Server value mutations per second |
| `Server.StructuralMutationRate` | `0` | Server structural mutations per second (collection/dictionary changes) |
| `Clients[].ValueMutationRate` | `50` | Client value mutations per second |
| `Clients[].StructuralMutationRate` | `0` | Client structural mutations per second |
| `*.UseTransactions` | `false` | Wrap each mutation batch in a transaction |
| `Clients[].Chaos.Mode` | `"both"` | `"kill"`, `"disconnect"`, or `"both"` |
| `Clients[].Chaos.IntervalMin/Max` | `00:01:00`/`00:05:00` | Time between disruptions |
| `Clients[].Chaos.DurationMin/Max` | `00:00:05`/`00:00:30` | Disruption hold time |
| `ChaosProfiles` | `[]` | Named profiles that rotate round-robin. Empty = all chaos always active |

Full type definitions in `ConnectorTesterConfiguration.cs`, `ParticipantConfiguration.cs`, and `ChaosConfiguration.cs`. Chaos intervals must be shorter than `MutatePhaseDuration`. Set `IntervalMax` to at most half of `MutatePhaseDuration`.

## Failure Scenario Coverage

### Covered (OPC UA)

| Scenario | Kill | Disconnect | Recovery Mechanism |
|----------|------|------------|-------------------|
| Abrupt server crash | Current attempt cancelled, TCP listeners closed (RST to clients) | Delegates to Kill | Background loop auto-restarts with exponential backoff (1s-30s + jitter) |
| Abrupt client crash | Session disposed without CloseSession RPC | Transport channel disposed, session preserved | Health check detects missing session, triggers full reconnection |
| Network partition | N/A | Client transport disposed, keep-alive detects within 5-10s | SDK `SessionReconnectHandler.BeginReconnect` with subscription transfer |
| Server restart (clean state) | Full server restart, new node manager | N/A | Client subscription transfer fails, falls back to full state reload |
| Session stall / hung reconnect | N/A | N/A | Stall detection after 30s forces SDK handler reset, triggers manual reconnection |
| Subscription creation failure | N/A | N/A | `SubscriptionHealthMonitor` retries failed items every 5s, falls back to polling |
| Port already in use on restart | N/A | N/A | Retried with exponential backoff |
| Resource exhaustion (polling) | N/A | N/A | Circuit breaker (5 failures, 30s cooldown) |
| Concurrent server + client chaos | Both engines run independently, overlapping disruptions possible | Same | Each connector recovers independently |
| Bidirectional mutations during chaos | Server and clients mutate concurrently during disruptions | Same | WriteRetryQueue buffers outbound writes, full state sync on reconnect |

### Covered (WebSocket)

| Scenario | Kill | Disconnect | Recovery Mechanism |
|----------|------|------------|-------------------|
| Abrupt server crash | Current attempt cancelled, full Kestrel teardown and rebuild | All client connections closed | Background loop rebuilds HTTP listener and restarts |
| Abrupt client crash | Current monitor iteration cancelled, WebSocket aborted, monitor loop reconnects | Socket aborted, receive loop exits | Monitor loop reconnects with exponential backoff |
| Sequence gap detection | N/A | N/A | Client detects missed sequence number, exits receive loop, reconnects with full state via Welcome |
| Heartbeat timeout | N/A | N/A | Receive timeout fires, client exits receive loop and reconnects |
| Concurrent server + client chaos | Both engines run independently | Same | Each side recovers independently, Welcome handshake re-syncs state |
| Bidirectional mutations during chaos | Server and clients mutate concurrently | Same | WriteRetryQueue buffers outbound writes, Welcome snapshot on reconnect |

### Not Covered

| Scenario | Why Not Covered | Production Impact |
|----------|-----------------|-------------------|
| Partial network failure (slow/lossy connections) | Keep-alive uses binary pass/fail detection | Low - TCP keepalive settings at OS level handle this |
| Certificate expiry mid-session | No certificate rotation mechanism | Low for tests, relevant for >6 month production deployments |
| Server returning persistent error status codes | Polling ignores some error codes | Low - circuit breaker limits impact |
| Out-of-order or duplicate notifications | No deduplication logic | Very low - OPC UA SDK handles within subscriptions |
| Notification duplication after subscription transfer | Full state read overwrites stale values | Low - brief inconsistency only |
| Server overload / adaptive backpressure | No response-time-based throttling | Low - not a connectivity failure |
| Flapping connections (rapid up/down cycles) | Backoff resets on each success | Low - stall detection provides upper bound |

### What the Tester Detects

The snapshot comparison **reliably detects**:
- Value divergence between any participants
- Collection/dictionary structural mismatches
- Object reference integrity breaks
- Data loss that isn't recovered within the convergence window

The snapshot comparison **does not detect** (by design):
- Transient state changes lost to merging (ChangeQueueProcessor collapses each flush to one change per property within its buffer window, and drops a commit that a later commit has already superseded)
- Timestamp accuracy for same-value updates (equality interceptor suppresses writes when value is unchanged, even if timestamp differs)
- Temporal ordering of changes (only final converged state is checked, not causality)
- Multi-property atomicity (no transaction support; A and B may converge independently)

The failure diagnostics localize bugs:

- **Per-cycle JSON snapshots** (`cycle-NNNN-fail-{participant}.json`): full normalized state per participant for diff investigation.
- **Per-property diffs with timestamps**: lists every diverged property with each side's value and write timestamp. `written never` means the property was never written via the interceptor chain on that participant. `written T` means it was written at time T.
- **Re-sync classifier**: applies the reference participant's complete state to each diverged participant and re-compares. If the result matches, the failure is a **transient delivery gap** (look at the connector wire: lost or out-of-order messages, missed reconnect catch-up). If it still diverges, the failure is in the snapshot logic, `SubjectUpdate.CreateCompleteUpdate`, `ApplySubjectUpdate`, or the `TestNode` model itself: the connector wire is exonerated.

The MutationEngine's global counter ensures every mutation produces a unique value, which prevents the equality interceptor from ever suppressing test mutations. This makes the tester resilient to the same-value suppression issue, though that issue could still affect production workloads.

## Long-Running Tests

The tester is designed to run continuously for extended periods (days/weeks). Key features for long-running viability:

- **Log rotation**: Only the 50 most recent passing log files are kept. FAIL logs are always preserved.
- **Graceful shutdown on failure**: On convergence failure, the host shuts down cleanly (flushing logs, disposing connectors) before setting exit code 1.
- **Reduced GC pressure**: Snapshot polling uses a 5-second interval to minimize allocation churn from JSON serialization.
- **Bounded retry queues**: `WriteRetryQueue` uses a ring buffer (default 1000 items) to prevent unbounded memory growth during long disconnections.

For multi-day runs, consider using longer cycle durations and lower mutation rates to reduce overall resource consumption:

```json
{
  "ConnectorTester": {
    "MutatePhaseDuration": "00:02:00",
    "ConvergenceTimeout": "00:02:00",
    "Server": { "ValueMutationRate": 500 },
    "Clients": [
      { "Name": "client-a", "ValueMutationRate": 25 },
      { "Name": "client-b", "ValueMutationRate": 25 }
    ],
    "ChaosProfiles": []
  }
}
```

## Adding a New Connector

Follow the pattern of an existing connector (e.g., OPC UA). The key touchpoints are: `appsettings.{name}.json`, a launch profile in `Properties/launchSettings.json`, the connector switch cases in `Program.cs`, `IFaultInjectable` on the connector's background service, and `[Path]` attributes on `TestNode.cs` properties.

## Troubleshooting

### Convergence Timeout

If cycles consistently fail to converge:
- Check that chaos recovery is working (look for "Chaos: force-killing" and "recovered from force-kill" log messages).
- Increase `ConvergenceTimeout` if the connector needs more time for full state synchronization.
- Verify the grace period in `VerificationEngine` is sufficient for your connector's reconnection chain.

### No Chaos Events

If cycles pass but show 0 chaos events, the chaos intervals are too long relative to `MutatePhaseDuration`. Reduce `IntervalMin`/`IntervalMax` so that `IntervalMax < MutatePhaseDuration`.

### Structural Timestamp Mismatch

Structural properties (Collection, Dictionary, Object) have local creation timestamps that differ per participant. These are **stripped during snapshot comparison** and should not cause failures. If you see mismatches involving only structural timestamps, the stripping logic in `SnapshotComparer.Capture()` may need updating.

### Decimal Precision (OPC UA)

OPC UA maps `decimal` through `double`, which has ~15-17 significant digits. The mutation engine uses `counter / 100m`, which stays well within this range. If you add mutations with larger decimal values, expect precision loss.

### Wrong Connector Running

If the test appears to run the wrong connector, verify you're using `--launch-profile` (not `--environment`). The launch profile sets `DOTNET_ENVIRONMENT` which controls appsettings loading.
