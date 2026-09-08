# Burst Flattening for ChangeQueueProcessor

## Problem

The `ChangeQueueProcessor` currently flushes all deduplicated changes every tick (default 8ms). During heavy spikes (e.g., bulk sensor updates, reconnection floods), this can overload CPU with too many writes, pushing the system past sustainable levels. Under sustained overload (write handler permanently slower than mutation rate), the internal `ConcurrentQueue` grows without bound, causing a GC pressure death spiral (#281).

Key constraint: legitimate bursts (e.g., loading a recipe with 100 property updates) should pass through unthrottled if the system can handle them. The mechanism must only intervene when the write handler is actually struggling, not when load merely increases.

## Solution

Add an adaptive bounded buffer with drop-oldest overflow to the `ChangeQueueProcessor`. The system learns the per-item write cost at runtime via dual EMA and uses it to predict whether a batch will exceed the time budget *before* writing. When a batch is too large, the excess is carried over to the next tick where it merges with new arrivals and re-deduplicates across the full combined set. Under sustained overload, oldest carry-over entries are evicted to bound memory, following the same pattern as OPC UA monitored item queue overflow (`DiscardOldest`) and .NET `BoundedChannel` with `DropOldest` mode.

The mechanism is opt-in and defaults to off, preserving existing behavior.

## API

New options class replaces the current `TimeSpan? bufferTime` constructor parameter (clean break, no backward-compat overload - pre-1.0 library):

```csharp
public class ChangeQueueProcessorOptions
{
    /// <summary>
    /// Time interval between flush ticks. Each tick, queued changes are
    /// deduplicated and sent to the write handler.
    /// Default: 8ms.
    /// </summary>
    public TimeSpan BufferTime { get; init; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Controls adaptive burst flattening behavior.
    /// When set to <see cref="BurstFlatteningMode.Adaptive"/>, the processor
    /// learns the per-item write cost at runtime and predictively caps batches
    /// to keep write duration within the time budget. Excess changes carry over
    /// to the next tick and re-deduplicate with new arrivals, flattening bursts
    /// across multiple windows. Under sustained overload, oldest carry-over
    /// entries are evicted to bound memory.
    /// Default: <see cref="BurstFlatteningMode.None"/>.
    /// Requires <see cref="BufferTime"/> > TimeSpan.Zero (throws otherwise).
    /// </summary>
    public BurstFlatteningMode BurstFlattening { get; init; }
        = BurstFlatteningMode.None;

    /// <summary>
    /// Fraction of <see cref="BufferTime"/> used as the write duration budget.
    /// The processor predicts write duration from batch size and learned per-item
    /// cost. When predicted duration exceeds BufferTime * WriteTimeBudgetRatio,
    /// the batch is capped and excess changes are carried over.
    /// Lower values leave more headroom but reduce throughput.
    /// Default: 0.8 (80% of BufferTime).
    /// </summary>
    public double WriteTimeBudgetRatio { get; init; } = 0.8;

    /// <summary>
    /// Maximum number of changes held in carry-over before oldest entries
    /// are evicted. When carry-over exceeds this threshold, the oldest
    /// entries are dropped (freshest data survives). A warning is logged
    /// and <see cref="ChangeQueueProcessor.TotalDroppedChanges"/> is incremented.
    /// Default: 10000.
    /// </summary>
    public int MaxCarryOverCount { get; init; } = 10_000;
}

/// <summary>
/// Controls how the <see cref="ChangeQueueProcessor"/> handles bursts
/// of property changes.
/// </summary>
public enum BurstFlatteningMode
{
    /// <summary>
    /// No burst flattening. Every flush tick sends all deduplicated changes
    /// to the write handler. This is the default and matches the original behavior.
    /// </summary>
    None,

    /// <summary>
    /// Enables adaptive burst flattening:
    /// <list type="bullet">
    /// <item>Learns per-item write cost via dual EMA (stable across batch sizes)</item>
    /// <item>Predicts write duration before each flush and caps the batch to stay
    /// within the time budget (BufferTime * WriteTimeBudgetRatio)</item>
    /// <item>Excess changes carry over and re-deduplicate with next tick's arrivals
    /// across the full combined set</item>
    /// <item>Oldest carry-over entries are evicted when MaxCarryOverCount is exceeded</item>
    /// </list>
    /// Legitimate bursts (e.g., recipe loads) pass through unthrottled as long as
    /// the write handler completes within the time budget.
    /// </summary>
    Adaptive
}
```

**Validation:** The constructor throws if `BurstFlattening == Adaptive` and `BufferTime <= TimeSpan.Zero`, since burst flattening requires a buffer window for cost prediction.

Constructor signature:

```csharp
public ChangeQueueProcessor(
    object? source,
    IInterceptorSubjectContext context,
    Func<RegisteredSubjectProperty, bool> propertyFilter,
    Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> writeHandler,
    ChangeQueueProcessorOptions options,
    ILogger logger)
```

## Algorithm

### Per-Item Cost Learning (Dual EMA)

After each flush, update two separate EMAs for write duration and item count:

```
durationEma = a * actualWriteDuration + (1 - a) * durationEma
itemsEma    = a * itemsSent           + (1 - a) * itemsEma
perItemCost = durationEma / itemsEma
```

The alpha (a) is derived from a ~1 second smoothing window relative to `BufferTime`.

**Why dual EMA?** Tracking duration and items separately and dividing naturally weights larger batches more heavily. A 1000-item batch contributes 1000x more to `itemsEma` than a 1-item batch. The resulting ratio reflects throughput-weighted per-item cost - the correct metric for predicting large-batch performance. This makes the estimate stable across varying batch sizes and converge faster during transitions (cold start, entering/leaving carry-over) compared to a direct per-item cost EMA which gives equal weight to small and large batches.

**Initialization:** On the first flush, no prediction is available. All changes are sent (calibration tick). The first measurement seeds both EMAs.

**Idle behavior:** The EMA only updates after non-empty flushes (when items are actually sent to the write handler). Empty ticks are skipped. This means the ~1 second window refers to ~1 second worth of non-empty flushes, not wall-clock time. If the system is idle for 30 seconds, the EMA retains its last known value. This is correct because per-item cost is a property of the write handler's performance, not of idle time. Idle ticks diluting the EMA would destroy the learned cost and force recalibration after every idle period. If system performance changed during idle (e.g., other processes consuming CPU), the EMA corrects within a few non-empty flushes.

**Guard:** `itemsEma` has a minimum floor (e.g., 1.0) to prevent division by zero during early ticks.

### Predictive Batch Cap

Before writing, predict whether the batch fits within the time budget:

```
budget   = bufferTime * writeTimeBudgetRatio    // e.g., 8ms * 0.8 = 6.4ms
maxItems = budget / perItemCost                 // e.g., 6.4ms / 0.04ms = 160 items
```

**If `dedupedCount <= maxItems`:** Send all changes (including any carry-over merged this tick). System is healthy. No intervention.

**If `dedupedCount > maxItems`:** Send the first `maxItems` changes, carry over the rest. Ordering is preserved: carry-over entries maintain their position, so properties waiting longest are sent first (FIFO drain). No property starvation is possible because sent entries are removed and new arrivals for existing carry-over properties update values in-place without changing position. Eventual consistency is guaranteed because carry-over re-deduplicates with new arrivals (latest value wins).

### Carry-Over Merge and Dedup (Each Tick)

Each tick, deduplication runs on the **full combined set** of carry-over + new arrivals:

1. Drain `ConcurrentQueue` into scratch buffer (new arrivals)
2. Merge with carry-over buffer: for each property, newer values overwrite carried-over values (value updated in-place, position preserved)
3. Deduplicate the combined set - new arrivals for properties already in carry-over collapse without growing the entry count
4. Apply the predictive cap to the merged result

This produces progressively more aggressive deduplication under sustained load. The longer changes sit in carry-over, the more same-property updates collapse into single writes. In sensor scenarios (same properties updating repeatedly), carry-over acts as a natural sponge - it can absorb thousands of new changes per tick without growing, as long as they hit the same properties.

### Drop-Oldest Overflow

When `carryOverCount > MaxCarryOverCount`, oldest carry-over entries are evicted to stay at the cap:

1. Evict oldest entries (freshest data survives)
2. Log a warning (the system is genuinely overloaded and needs tuning)
3. Increment `TotalDroppedChanges` diagnostic counter

This ensures:
- Memory stays bounded (no infinite backlog)
- CPU stays healthy (system continues at its proven sustainable rate)
- Freshest data survives (newest property values are more relevant)
- The operator gets a clear signal their system needs tuning (via log warning and diagnostic counter)

### Carry-Over Storage

The carry-over buffer is a separate `ArrayPool`-rented buffer, only allocated on the first carry-over event. When the system is healthy (no carry-over), the second buffer is never rented - zero cost. Once rented, it is reused across ticks.

Each tick, carry-over is merged with new arrivals into the existing `_flushDedupedBuffer` for dedup. After sending `maxItems`, excess is copied to the carry-over buffer. One `Array.Copy` per tick when carry-over is active - negligible compared to the write handler cost that caused carry-over in the first place.

### Example Flows

#### Burst with High Dedup (Sensor Scenario)

System with 8ms buffer, 80% budget (6.4ms), learned per-item cost of 0.04ms, `maxItems = 160`. 1000 sensors updating the same properties repeatedly:

| Tick | Arrivals | Carry-in | After merge+dedup | maxItems | Sent | Carry-out | Notes |
|---|---|---|---|---|---|---|---|
| N | 50 | 0 | 50 | 160 | 50 | 0 | Healthy |
| N+1 | 2000 (burst) | 0 | 2000 | 160 | 160 | 1840 | Burst hits |
| N+2 | 200 | 1840 | 1840 | 160 | 160 | 1680 | 200 arrivals all hit existing carry-over properties, dedup absorbs them |
| N+3 | 200 | 1680 | 1680 | 160 | 160 | 1520 | Same: arrivals collapse into carry-over |
| ... | (same properties keep updating, carry-over drains 160/tick) | | | | | | |
| N+12 | 200 | 100 | 100 | 160 | 100 | 0 | Recovered |

Carry-over absorbed ~2000 new arrivals across 10 ticks without growing because they all hit existing properties. The burst resolved in ~12 ticks with CPU never spiking.

#### Burst with Low Dedup (Worst Case)

Same system, but all 2000 burst changes are to distinct properties and new arrivals are also distinct:

| Tick | Arrivals | Carry-in | After merge+dedup | maxItems | Sent | Carry-out | Notes |
|---|---|---|---|---|---|---|---|
| N+1 | 2000 (burst) | 0 | 2000 | 160 | 160 | 1840 | Burst hits |
| N+2 | 50 (distinct) | 1840 | 1890 | 160 | 160 | 1730 | No dedup collapse |
| N+3 | 50 (distinct) | 1730 | 1780 | 160 | 160 | 1620 | Still growing slightly |
| ... | (burst subsides, carry-over drains at 160 - arrivals per tick) | | | | | | |

Carry-over drains linearly. If arrivals consistently exceed maxItems for distinct properties, carry-over eventually hits `MaxCarryOverCount` and drop-oldest activates.

## Diagnostics

Expose read-only diagnostic properties on `ChangeQueueProcessor`. Diagnostics are injected via DI; each background service exposes its processor's diagnostics through its own surface.

### PerformanceProfiler Integration

The `PerformanceProfiler` (in `Namotion.Interceptor.SamplesModel`) receives burst flattening diagnostics via DI and includes them in the periodic stats output. When burst flattening is enabled, the profiler prints an additional section:

```
Burst Flattening
  Per-item write cost:       0.04 ms
  Max items per flush:       160
  Carry-over count:          0
  Dropped changes:           0
  Flushed throughput:        2500.00 changes/s
  Received throughput:       2500.00 changes/s
```

This enables hardware comparison: run the same sample app on different machines and compare the `Per-item write cost` to evaluate which CPU/hardware fits the application's throughput requirements. The `Max items per flush` derived from it shows the effective capacity of each system.

All sample apps (OPC UA, MQTT, WebSocket - both client and server) already create a `PerformanceProfiler` and will automatically show these metrics when burst flattening is enabled.

### Diagnostic Properties

```csharp
/// <summary>Total changes sent to the write handler (lifetime counter).</summary>
public long TotalFlushedChanges => Volatile.Read(ref _totalFlushedChanges);

/// <summary>Total changes received from the subscription (lifetime counter).</summary>
public long TotalReceivedChanges => Volatile.Read(ref _totalReceivedChanges);

/// <summary>Total changes dropped due to carry-over overflow (lifetime counter).</summary>
public long TotalDroppedChanges => Volatile.Read(ref _totalDroppedChanges);

/// <summary>
/// Smoothed rate of changes sent to the write handler, in changes per second.
/// Computed using an EMA over actual elapsed time between flushes.
/// </summary>
public double FlushedThroughputPerSecond { get; }

/// <summary>
/// Smoothed rate of changes received from the subscription, in changes per second.
/// The difference between received and flushed throughput shows how much
/// deduplication and carry-over are reducing write volume in real time.
/// </summary>
public double ReceivedThroughputPerSecond { get; }

/// <summary>Current number of changes held in carry-over.</summary>
public int CurrentCarryOverCount { get; }

/// <summary>Current learned per-item write cost in milliseconds.</summary>
public double PerItemWriteCostMs { get; }

/// <summary>Current maximum items per flush (derived from per-item cost and budget).</summary>
public int CurrentMaxItemsPerFlush { get; }
```

### Throughput Calculation

After each flush, throughput is computed from actual elapsed time and smoothed with an EMA:

```csharp
var elapsed = Stopwatch.GetElapsedTime(_lastFlushTimestamp);
_lastFlushTimestamp = Stopwatch.GetTimestamp();

var instantFlushedRate = flushedCount / elapsed.TotalSeconds;
var instantReceivedRate = receivedCount / elapsed.TotalSeconds;

_flushedThroughput = alpha * instantFlushedRate + (1 - alpha) * _flushedThroughput;
_receivedThroughput = alpha * instantReceivedRate + (1 - alpha) * _receivedThroughput;
```

Using actual elapsed time (via `Stopwatch`) ensures accuracy. The EMA smoothing (alpha derived from a ~1 second window) filters out per-tick noise while remaining responsive to real trends.

Cost: one `Stopwatch.GetTimestamp()` call + two multiply-adds per flush. Effectively free. This throughput EMA is purely diagnostic - it is not used for throttling decisions. The per-item cost dual EMA (used for throttling) is a separate calculation.

## Performance

- **When disabled** (`None`): Zero overhead. Code path is identical to today via a simple `if` branch. Diagnostic throughput EMA is still updated (two multiply-adds per flush - negligible).
- **When enabled** (`Adaptive`): Allocation-free in steady state. Per-item cost learning is two multiply-adds per flush plus one `Stopwatch.GetElapsedTime` call. Predictive cap is one division per flush. Carry-over uses a separate `ArrayPool`-rented buffer, only allocated on first carry-over event and reused thereafter. One `Array.Copy` per tick when carry-over is active. Drop-oldest overflow is one comparison + array shift. No LINQ, no closures, no boxing.

## Testability

Key internal state is exposed via `internal` properties with `[InternalsVisibleTo]` for the test project:

- Carry-over count and contents
- Per-item cost EMA values (duration, items, derived cost)
- Last write duration measurement
- Max items per flush (derived)

This replaces the current reflection-based test pattern with proper internal test seams.

## Callers

Four production call sites need updating (clean break - old constructor removed):

1. `SubjectSourceBackgroundService` - accepts `ChangeQueueProcessorOptions` instead of `TimeSpan? bufferTime`
2. `MqttSubjectServerBackgroundService` - constructs options from `MqttServerConfiguration`
3. `OpcUaSubjectServerBackgroundService` - constructs options from `OpcUaServerConfiguration`
4. `WebSocketSubjectHandler.CreateChangeQueueProcessor` - constructs options from `WebSocketServerConfiguration`

Each connector's configuration class gains `BurstFlattening` (default `None`) plus optionally `WriteTimeBudgetRatio` and `MaxCarryOverCount` with defaults that preserve existing behavior.

## Testing

### Unit Tests

New tests for `ChangeQueueProcessorTests` (using internal test seams, not reflection):

1. No throttling on first flush (calibration tick sends everything)
2. Per-item cost EMA converges after multiple flushes of varying size
3. Dual EMA weights larger batches more heavily than smaller ones
4. Predictive cap activates when predicted duration exceeds budget
5. Carry-over merges and re-deduplicates with new arrivals across full combined set
6. New arrivals for properties already in carry-over collapse without growing carry-over
7. Carry-over drains when batch fits within budget (burst recovery)
8. Legitimate burst passes through if write duration stays within budget
9. Drop-oldest evicts oldest entries when carry-over exceeds MaxCarryOverCount
10. Drop-oldest logs warning and increments TotalDroppedChanges
11. Carry-over ordering is FIFO (properties waiting longest are sent first)
12. Features disabled (`None`) = current behavior unchanged, zero overhead
13. Always sends at least some items (never fully stalls - min maxItems >= 1)
14. Diagnostic counters (flushed, received, dropped) are correct
15. Per-item cost and max items per flush diagnostics are correct
16. Throws when BurstFlattening is Adaptive and BufferTime <= TimeSpan.Zero

### Load Testing

Validate with the ConnectorTester using the MQTT 80k topic scenario from #281:
- 20k objects x 4 mqtt-pathed properties = 80k topic subscriptions
- 20k mutations/sec per participant
- Verify: memory stays bounded, throughput stays stable, no GC death spiral
- Compare with/without burst flattening to confirm the fix

## Baseline Benchmark (Pre-Implementation)

Run on 2026-02-19, master branch at commit `fd38d148`.

```
BenchmarkDotNet v0.15.5, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
11th Gen Intel Core i7-11700K 3.60GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]     : .NET 9.0.11 (9.0.11, 9.0.1125.51716), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 9.0.11 (9.0.11, 9.0.1125.51716), X64 RyuJIT x86-64-v4

| Method                  | Mean     | Error     | StdDev    | Allocated |
|------------------------ |---------:|----------:|----------:|----------:|
| WriteToRegistrySubjects | 2.328 ms | 0.0071 ms | 0.0063 ms |         - |
| WriteToSource           | 1.716 ms | 0.0116 ms | 0.0103 ms |         - |
```

- **WriteToRegistrySubjects**: 1M property writes through the full pipeline (queue -> dedup -> write handler). Buffer time: 1ms.
- **WriteToSource**: 5000 property writes through the source pipeline. Buffer time: 1ms.
- **Allocated: 0 bytes** - confirms the current implementation is allocation-free.

These numbers serve as the baseline to verify the implementation introduces no performance regression when burst flattening is disabled (`None`).

## Documentation

After implementation, update the project documentation (`docs/` folder) with:

- **Burst flattening configuration guide**: How to enable adaptive burst flattening, what each option does, recommended settings for different scenarios (high-throughput sensor data, low-latency UI updates, etc.)
- **Algorithm explanation**: How the per-item cost dual EMA works, the adaptive bounded buffer with carry-over, and idle behavior (EMA retains last value during inactivity, corrects within a few non-empty flushes)
- **Diagnostics reference**: Available diagnostic properties (`PerItemWriteCostMs`, `CurrentMaxItemsPerFlush`, throughput counters, dropped changes, etc.) and how to use them for hardware comparison and capacity planning
- **PerformanceProfiler output**: What the burst flattening section in the profiler output means and how to interpret it for performance tuning

## Follow-Up

- #282 - Support non-deduplicating properties in ChangeQueueProcessor (signal-type properties where every individual change matters)
