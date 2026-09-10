---
title: Time-Series History
navTitle: History
status: Implemented
---

# Time-Series History Design

## Overview

The history system records eligible property changes for charts, historical queries, and AI tools. It is designed for both low-powered HomeBlaze installations and larger industrial deployments.

Each history store is an independent `[InterceptorSubject]` `BackgroundService`. It records and serves its own data, exposes immutable coverage information, and is discovered through the subject registry. A stateless merger combines the available stores. There is no central history coordinator.

The implemented stores are:

| Store | Default priority | Purpose |
|---|---:|---|
| In-Memory History | 100 | Full-resolution recent data and live edge |
| SQLite History | 50 | Durable local history |

A TimescaleDB store is planned as a lower-priority industrial tier. Snapshots and structural history are deferred.

## Architecture

Each store has two parts:

- A graph-free engine implementing `IHistoryStore`, operating on canonical property paths and typed values.
- A thin subject adapter that owns graph discovery, eligibility, path resolution, move detection, configuration, metrics, and the change queue.

The engine and adapter split keeps storage and query behavior testable without a HomeBlaze graph and makes future extraction into a generic library mechanical.

The package responsibilities are:

| Package | Responsibility |
|---|---|
| `HomeBlaze.History.Abstractions` | Store and recorder interfaces, query and result records, aggregations, column routing, bucket alignment, move-chain resolution |
| `HomeBlaze.History` | Cross-store query planner and merger, recording glue, graph eligibility |
| `HomeBlaze.History.InMemory` | In-memory engine and subject |
| `HomeBlaze.History.Sqlite` | SQLite engine and subject |
| `HomeBlaze.History.Blazor` | Property history dialog |
| `HomeBlaze.History.Mcp` | `get_property_history` MCP tool |
| Store-specific `.Blazor` packages | Store configuration components |

`HomeBlaze.History.Abstractions` has no project references at all. That is enforced by its own csproj and, indirectly, by its test project, which references nothing else either. Everything that needs the object graph, meaning `HasHistory` and the recording glue, lives one layer up in `HomeBlaze.History`.

The MCP tool ships with the history packages rather than with `HomeBlaze.AI`, so installing history brings its tool along and the AI package does not reference the history stack. The host composes the two by passing the provider into `WithHomeBlazeMcpTools`.

## Store contract

```csharp
public interface IHistoryStore
{
    int Priority { get; }
    ImmutableArray<HistoryCoverage> CoverageRanges { get; }
    IReadOnlySet<string> SupportedAggregations { get; }

    Task<HistorySeries> QueryAsync(
        HistoryQuery query,
        CancellationToken cancellationToken);

    ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(
        string propertyPath,
        DateTimeOffset asOf,
        CancellationToken cancellationToken);
}

public readonly record struct HistoryCoverage(
    DateTimeOffset From,
    DateTimeOffset To);

public record HistoryQuery(
    string PropertyPath,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan? Bucket = null,
    string Aggregation = HistoryAggregations.Last,
    int MaxPoints = 10_000,
    HistoryPoint? CarrySeed = null);

public record HistorySeries(
    string PropertyPath,
    ImmutableArray<HistoryPoint> Points,
    bool Truncated,
    ImmutableArray<HistoryCoverage> CoverageRanges);
```

Queries require a non-empty path and aggregation, `From < To`, a positive optional bucket, and a positive `MaxPoints`. Every entry point validates these invariants and honors cancellation.

Stores return the newest `MaxPoints` results in ascending timestamp order. `Truncated` is true when older results were omitted. `HistorySeries.CoverageRanges` describes effective coverage within the requested range.

## Coverage

Coverage is the basis for correct cross-store routing. It is not inferred from whether a particular property has samples.

Each `HistoryCoverage` is a half-open interval `[From, To)`. `CoverageRanges` is an immutable, ordered, non-overlapping snapshot. Empty intervals are omitted and touching or overlapping intervals are normalized.

A range means:

> The store was actively collecting its configured history stream throughout this interval and detected no loss.

The configured stream coalesces repeated updates to the same property within `BufferTimeMilliseconds`, keeping the oldest old value and newest new value. It is a time-series sampling policy, not an audit log of every setter invocation. After that policy is applied, no sample for a property inside a covered interval means that the property did not change. Coverage is intentionally store-wide and all-or-nothing. Per-property coverage would make routing and correctness dependent on a large and continuously changing metadata set.

Ranges are necessary because continuity can be lost independently of retention:

- An application or store restart creates a gap between the last successful coverage heartbeat and the next store session.
- A temporary database failure does not create a gap when every pending sample remains buffered and is later committed.
- A bounded pending queue overflow creates a gap from the first dropped change until persistence catches up and recording resumes.
- Retention trims or removes old portions of ranges.

The merger snapshots each store's immutable ranges once per query. Planning then uses those snapshots. It does not load ranges per property or per bucket. A persistent store may hold years of ranges, but the normal count is approximately the number of discontinuities, not the number of samples. For example, one daily restart over five years is about 1,800 small metadata rows.

### In-memory coverage

The in-memory store has at most one current range. Its initial lower bound is the later of store start and `now - MaxAge`.

`MaxPointsPerProperty` is an in-memory safety cap, not a query limit. Each property has its own bounded ring. A ring that has never discarded a sample does not narrow coverage because absence of an earlier sample still means no earlier change was observed. When any ring evicts data by age or capacity, a monotonic store-wide floor advances to the worst retained boundary across all properties.

The worst-case floor is deliberate. A quiet property may still have older physical samples, but the store can only claim the interval that is complete for every eligible property. The merger can ask a lower-priority persistent store for the older interval.

The store returns no coverage when its clock has not advanced past the range start. A backward wall-clock adjustment cannot reclaim already evicted history.

### SQLite coverage

SQLite persists raw history separately from coverage:

- Partition databases contain samples and per-path column metadata.
- The existing `moves.db` sidecar is the metadata database and contains both the `moves` table and the `coverage_ranges` table. Keeping the filename preserves existing move history.

Coverage rows use:

```sql
CREATE TABLE coverage_ranges (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    from_ts INTEGER NOT NULL,
    to_ts INTEGER NOT NULL,
    CHECK (from_ts < to_ts)
);
```

Each engine session begins a new durable range. Every successful flush updates a small coverage heartbeat even when no properties changed. This records healthy quiet periods and makes application downtime visible after restart. Coverage metadata is loaded once at engine construction and exposed through a cached immutable snapshot.

The public snapshot normalizes overlapping or touching rows. The durable rows remain session-oriented, which keeps writes simple and preserves outage boundaries.

SQLite never exposes coverage beyond an uncommitted engine sample. The immutable public snapshot is conservatively clipped at the earliest pending sample, pending move, or detected drop, even when a prior heartbeat extended farther. Both inputs to that clip (the durable ranges and the earliest uncommitted instant) are published independently, so reading coverage never waits behind a flush holding the connection lock. It extends durable coverage only after partition writes, move writes, and the metadata update succeed. On a transient failure, the batch is put back at the front of the queue and retried. `INSERT OR REPLACE` makes sample retries idempotent.

A fixed limit of `100_000` bounds the samples waiting for durable persistence. This is a memory safety valve, not a tuning knob, so it is not exposed as configuration. Once the combined pending and in-flight count reaches the limit:

1. New samples are dropped and `DropCount` increments.
2. Dropping continues while the accepted backlog is drained.
3. The existing coverage range ends at the first dropped change.
4. A successful drain ends drop mode.
5. The next healthy period starts a new coverage range.

This policy keeps memory bounded, does not block the property-change hot path, and never claims completeness over lost changes.

The subject constructs the engine, installs its change subscription, and only then calls `BeginCoverageSession`, so no change can fall inside claimed coverage without reaching the engine. During graceful shutdown SQLite performs a final bounded engine flush. This persists samples that already reached the engine, but `ChangeQueueProcessor` currently provides no contract for draining its coalescing queue when service cancellation begins.

Retention removes complete partition files whose interval is older than `now - MaxAge`. It also removes or clamps coverage rows at that cutoff. Coverage does not depend on finding a sample near either boundary, so a quiet store remains honestly covered.

## Recording

`HistoryEligibility.HasHistory()` is the single predicate used by recording and UI discovery. A property is eligible when it:

- has `[State]`;
- does not contain subjects;
- uses a supported scalar type.

Supported values route through one shared column decision:

| Types | Storage |
|---|---|
| `double`, `float`, `decimal` | `value_double` |
| signed and unsigned integers, `bool` | `value_long` |
| `string`, enum | `value_json` |

`ulong` values above `long.MaxValue` spill into `value_json` and are folded back into numeric reads. SQLite also archives exact decimal JSON text while exposing decimals as `double` for charting and aggregation.

Only strings are unbounded. A string larger than `MaxJsonSize` records an oversize placeholder with its original serialized size, preserving the timeline without retaining an arbitrary payload.

The subject adapter resolves each canonical subject path and caches the last path per `(subject, property)`. Structural lifecycle changes invalidate the resolver cache. The storage engines only see complete property path strings.

## Move tracking

Move support remains local to every store. When a subject is renamed or reparented, the adapter compares the newly resolved path with the last path for that subject and property. It records:

```text
MoveRecord(change timestamp, old property path, new property path)
```

The per-property cache is required so that the first changed sibling does not consume move detection for every other history property.

SQLite stores moves in the `moves` table in its metadata database, currently named `moves.db` for compatibility. In-memory stores keep them in memory. Queries walk move records backwards from the current path, guard against cycles, and time-scope every path leg to its valid interval. `QueryAsync` and `GetSampleAtOrBeforeAsync` both follow the chain, so the merger does not need move-specific logic.

Move detection uses runtime object identity. Moves performed while HomeBlaze is stopped cannot be inferred after restart.

## Query semantics

A null bucket requests raw samples. A non-null bucket requests one point per epoch-aligned bucket. Alignment uses floor division anchored at the Unix epoch and is correct for timestamps before and after the epoch.

Empty bucket behavior is:

| Aggregation | Empty bucket |
|---|---|
| `Count` | `0` |
| `Last` | carried value, or null when unknown |
| `TimeWeightedAverage` | carried value integrated over known duration, or null when unknown |
| Other aggregations | null |

Bucketed merger results include the complete newest bucket grid, subject to `MaxPoints`. Planning and store aggregation start at the first bucket that can appear in that output, so a multi-year request with a small point budget does not enumerate every older bucket. Uncovered buckets are explicit null points. This lets the chart render gaps without guessing whether an omitted point means no data or truncation.

Direct store queries apply the same coverage rule. A bucket that is not fully contained in one of that store's ranges is null and clears carried state, so `Last` and `TimeWeightedAverage` never synthesize values through a restart or drop gap.

Numeric aggregations on JSON properties throw `HistoryAggregationNotSupportedException`. Aggregation identifiers are PascalCase strings so stores can add capabilities without changing a closed enum:

| Identifier | Meaning |
|---|---|
| `Last` | newest sample in the bucket, else the carried value (the UI default for non-numeric properties) |
| `First` | oldest sample in the bucket |
| `TimeWeightedAverage` | each value weighted by how long it held (the UI default for numeric properties, labelled "Average") |
| `SampleAverage` | count-weighted mean (labelled "Sample Average") |
| `Minimum`, `Maximum`, `Sum` | numeric reductions over the bucket's samples |
| `Count` | number of samples in the bucket |
| `StandardDeviation` | sample standard deviation, null for fewer than two values |

`HistoryAggregations.AlwaysAvailable` is `{ Last, Count }`; the capability check skips those.

## Cross-store merge

The merger orders stores by descending priority and snapshots coverage once. It uses two planners:

- Raw queries use coverage subtraction. Higher-priority ranges claim their overlap first and lower-priority stores fill uncovered pieces.
- Bucketed queries assign each bucket to one highest-priority store that both covers the bucket and supports the aggregation. A bucket is never split across stores, avoiding invalid combinations such as an average of averages. The newest bucket is clipped to the end of the requested range for both purposes, so it is still served when the range ends mid-bucket and its point never aggregates samples from after that end.

Consecutive buckets with the same owner become one store query. Coverage gaps break segments even when the same store owns both sides.

Point budgets favor the newest data. Non-carry queries execute newest-first with the remaining budget. Carry-dependent queries first select the newest segments that fit, then execute those segments oldest-to-newest to thread state correctly.

For `Last` and `TimeWeightedAverage`, the merger resolves a value held at the start of each contiguous served region. It threads that value across adjacent segments. It does not carry across an uncovered interval. After each segment it asks for the last raw event rather than using an aggregate point as the next carry.

Query errors propagate. A failed store must not look like an empty store.

## Time-weighted average

`TimeWeightedAverage` uses step interpolation, also called last observation carried forward. A value holds until the next event.

For each bucket:

```text
sum(value * known duration) / sum(known duration)
```

An explicit null event clears the held value. Unknown intervals contribute to neither the numerator nor the denominator. A later numeric event establishes a known value again.

In-memory integrates directly over its ordered buffer. SQLite streams one ascending event sequence across move legs and partition files. It keeps only the pending prior event plus bucket partials, so sample memory is constant and it does not use SQLite `ATTACH`. This also avoids SQLite's attached-database limit for long queries.

The parity test suite feeds identical cases to both engines. Any future TimescaleDB fast path must preserve the same explicit-null and carry semantics.

## Store implementation notes

### In-Memory History

- `ConcurrentDictionary<string, PropertyBuffer>` by property path.
- Array-backed ring with a lock per property.
- Lock-free monotonic store-wide eviction watermark.
- Immediate query visibility.
- No persistence across restart.
- Default `MaxAgeSeconds`: `60`.
- Default `MaxPointsPerProperty`: `1_000`.
- Default `BufferTimeMilliseconds`: `250`.
- Default `MaxJsonSize`: `8_192`.
- Metrics include recorded, oversize, evicted, property and sample counts, memory size in bytes, and throughput.

### SQLite History

- One `WITHOUT ROWID` history database per daily, weekly, or monthly partition.
- `(path, ts)` primary key and `INSERT OR REPLACE` retry behavior.
- WAL mode with pooling disabled.
- `moves.db` metadata sidecar for moves and coverage ranges.
- Serialized connection access because `SqliteConnection` and commands are not thread-safe.
- Pending and in-flight sample accounting under one lock.
- Flush serialization under a separate gate.
- No lock path acquires the pending lock while already holding the connection lock.
- Default `MaxAgeDays`: `365`.
- Default `FlushIntervalSeconds`: `10`.
- Default `BufferTimeMilliseconds`: `250`.
- Default `PartitionInterval`: `Weekly`.
- Default `MaxJsonSize`: `8_192`.
- Metrics include recorded and oversize counts, queue depth, drop count, storage size in bytes, last successful flush, errors, and throughput.

`InMemory.MaxAge` should remain comfortably larger than a persistent store's flush interval. The defaults provide a recent overlap so the live edge stays available while SQLite commits.

### Configuration summary

| Knob | InMemory | SQLite | TimescaleDB (planned) |
|---|---|---|---|
| `Priority` | 100 | 50 | 10 |
| `MaxAge` (retention) | 60 s | 365 d | 365 d |
| `FlushInterval` | n/a (direct) | 10 s | 5 s |
| `BufferTime` (coalesce) | 250 ms | 250 ms | 250 ms |
| `PartitionInterval` | n/a | Weekly | n/a (1-day chunks) |
| `MaxPointsPerProperty` | 1000 | n/a | n/a |
| `MaxJsonSize` | 8 KB | 8 KB | 8 KB |

## UI and MCP

The history subjects implement `ITitleProvider` and render as "In-Memory History" and "SQLite History".

The property history dialog is available for eligible `[State]` properties when at least one store exists. It supports preset and custom ranges, raw or bucketed queries, and type-aware aggregation choices. Numeric properties render as a line chart, split at explicit null gaps.

### State timeline

A discrete property renders as a state timeline instead: a proportional band of constant-value runs, a legend, and a newest-first table of transitions with durations. Discrete means `bool`, `enum` or `string` by type, or anything marked `[State(IsDiscrete = true)]`.

The reason is that a line chart misrepresents a discrete value three ways at once: the value axis auto-scales far past a 0/1 range, an averaging default reports fractions for a state that was never fractional, and joining the points implies the value passed through everything in between.

Discrete properties default to the raw period, so transitions keep their exact times rather than being rounded to a bucket edge, and only `Last`, `First` and `Count` are offered.

The band distinguishes three states, which is the distinction a line chart cannot draw at all:

| Rendering | Meaning |
|---|---|
| Coloured run | a value was held over this span |
| Neutral run, "(no value)" | the span is covered, and the value there is genuinely absent (an explicit null, or a bucket with no sample under an aggregation that does not carry forward) |
| Hatched run, "No data" | no store covers this span, so nothing can be said about it |

Filling the start of a window needs the value held entering it, which a raw query does not return, so the merger exposes `GetValueAtAsync`. Without it a property whose last change predates the window renders as empty rather than as its actual state.

`get_property_history` takes:

| Parameter | Required | Default | Notes |
|---|---|---|---|
| `paths` | yes | | one or more canonical property paths |
| `from` | yes | | ISO 8601; bare timestamps treated as UTC |
| `to` | no | now | ISO 8601 |
| `bucket` | no | null (raw) | for example `5m`, `30s`, `1h`, `7d` |
| `aggregation` | no | `Last` | case-insensitive match against `HistoryAggregations` |

The response is a per-path map, each entry carrying:

- a value type hint (number / string / boolean / enum);
- points, including explicit null gaps;
- truncation state;
- effective coverage ranges.

Unknown or non-servable aggregations return a structured error with the `available` set; empty results and unknown paths are not errors. Aggregation input is normalized case-insensitively at the MCP boundary. Internal identifiers use ordinal comparison.

## Planned TimescaleDB tier

The planned store uses Npgsql binary `COPY`, a hypertable with daily chunks, `drop_chunks` retention, idempotent schema bootstrap, and an async gate for connection and reconnect state.

It must use the same coverage contract:

- a small durable coverage-range table;
- a new session range after restart or loss of continuity;
- a heartbeat advanced only after accepted writes are durable;
- no gap for an outage whose complete bounded backlog is later committed;
- a gap after any dropped sample;
- retention pruning of both chunks and coverage metadata.

Queries should send the relevant range predicate to PostgreSQL rather than loading coverage rows per property. A toolkit time-weighted-average path is only valid when it preserves explicit null boundaries and matches the portable parity suite.

## Known limitations and roadmap

- Changing a property's declared type changes its storage column. Older samples under another type may not be visible.
- Service cancellation or a hard crash can lose changes still in the coalescing queue. A hard crash can also lose samples accepted by the engine since the last durable flush. These losses cannot retroactively mark their exact tail as a coverage gap.
- The in-memory store loses all data on restart.
- Move detection cannot discover moves that occurred while HomeBlaze was stopped.
- Per-property time resolution is bounded by the change queue's coalescing interval.
- When the requested bucket size exceeds `InMemory.MaxAge`, the rightmost bucket can omit up to a persistent store's `FlushInterval` of samples. Raise `MaxAgeSeconds` for a pixel-perfect live edge.
- Subject-bearing state, full graph snapshots, `Rate`, `Delta`, `StateDuration`, interpolation, compression, and continuous aggregates are future work.

The planned snapshot layer stores periodic compressed whole-graph snapshots and reconstructs a requested time by finding the nearest prior snapshot and replaying scalar, structural, and move events. Planned MCP tools are `get_snapshot` and capped `get_snapshots`.

## Design decisions

| Decision | Reason |
|---|---|
| Independent store subjects | Matches HomeBlaze connector configuration and avoids a central lifecycle owner |
| Store-wide coverage ranges | Makes completeness explicit while keeping metadata and routing bounded |
| Immutable coverage snapshots | Prevents torn reads and avoids allocations during merger planning |
| Persisted SQLite health ranges | Preserves restart and drop gaps without scanning years of samples |
| Per-bucket single-owner dispatch | Prevents mathematically invalid aggregate merging |
| Cross-store carry with gap reset | Keeps sparse state correct without inventing values through outages |
| Typed value columns | Preserves integer precision and enables native numeric aggregation |
| Streaming SQLite TWA | Keeps long queries bounded in memory and avoids `ATTACH` limits |
| Bounded pending persistence queue | Protects 24/7 hosts from unbounded memory growth during outages |
| Engine and subject split | Keeps storage logic graph-free, testable, and extraction-ready |
