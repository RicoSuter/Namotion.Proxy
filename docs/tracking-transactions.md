# Transactions

The `Namotion.Interceptor.Tracking` package provides transaction support for batching property changes and committing them atomically. This is particularly useful when integrating with external data sources (OPC UA, MQTT, databases) where you want to write multiple changes as a single operation, or when you need to ensure consistency across multiple property updates.

## When to Use Transactions

Without transactions, property changes are applied to the local model immediately, while external sources synchronize asynchronously in the background. This is suitable when the local model is the source of truth, or eventual consistency is acceptable.

Use transactions when you need guarantees about what was actually persisted to external sources.

## Overview

Transactions provide:
- **Configurable commit modes**: Choose between best-effort or rollback behavior on partial failures
- **Read-your-writes consistency**: Reading a property inside a transaction returns the pending value
- **Notification suppression**: Captured non-derived writes stay silent until commit replay applies them
- **External source integration**: Changes can be written to external sources before being applied to the local model
- **Rollback on dispose**: Uncommitted changes are discarded when the transaction is disposed

## Setup

Enable transaction support in your interceptor context:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithTransactions() // Required for in-memory transaction support (opt-in)
    .WithSourceTransactions(); // Required for source write transaction support (opt-in)
```

## Basic Usage

### Starting a Transaction

```csharp
var person = new Person(context);

using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
{
    person.FirstName = "John";
    person.LastName = "Doe";

    // Changes are captured but not yet applied to the model
    // No change notifications are fired yet

    await transaction.CommitAsync(cancellationToken);

    // All changes are now applied and notifications are fired
}
```

### Read-Your-Writes Consistency

Inside a transaction, reading a property returns the pending value:

```csharp
using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
{
    person.FirstName = "John";

    // Reading returns the pending value, not the committed value
    Console.WriteLine(person.FirstName); // Output: John

    await transaction.CommitAsync(cancellationToken);
}
```

### Rollback (Implicit)

If a transaction is disposed without committing, changes are discarded:

```csharp
using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
{
    person.FirstName = "John";

    // No commit - changes are discarded when using block exits
}

Console.WriteLine(person.FirstName); // Output: (original value, not "John")
```

### Inspecting Pending Changes

You can inspect which changes are pending before committing:

```csharp
using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
{
    person.FirstName = "John";
    person.LastName = "Doe";

    var pendingChanges = transaction.GetPendingChanges();
    foreach (var change in pendingChanges)
    {
        Console.WriteLine($"{change.Property.Name}: {change.GetOldValue<object?>()} -> {change.GetNewValue<object?>()}");
    }

    await transaction.CommitAsync(cancellationToken);
}
```

### Last-Write-Wins Semantics

If the same property is written multiple times within a transaction, only the final value is committed:

```csharp
using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
{
    person.FirstName = "John";
    person.FirstName = "Jane";
    person.FirstName = "Bob";

    // Only one pending change for FirstName
    // OldValue = original value, NewValue = "Bob"

    await transaction.CommitAsync(cancellationToken);
}
```

## Configuration Options

### Failure Handling

Controls behavior when some writes fail during commit.

```csharp
using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
```

| Value | Description |
|-------|-------------|
| `BestEffort` | Apply successful changes, rollback failed ones to keep each property in sync with its source. |
| `Rollback` | All-or-nothing across all properties - any failure reverts everything. |

**Behavior comparison:**

| Scenario | BestEffort | Rollback |
|----------|------------|----------|
| All succeed | All changes applied | All changes applied |
| Source write fails | Successful applied, failed not applied | All reverted |
| Local apply fails | Successful applied, failed sources rolled back | All reverted |
| Consistency | Per-property (each stays in sync) | All-or-nothing |
| Use when | Partial progress acceptable | Full atomicity required |

### Locking

Controls how concurrent transactions are synchronized.

```csharp
// Exclusive (default) - lock at begin, serializes all transactions
using var tx = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    TransactionLocking.Exclusive);

// Optimistic - lock only at commit, allows concurrent capture
using var tx = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    TransactionLocking.Optimistic,
    conflictBehavior: TransactionConflictBehavior.FailOnConflict);
```

| Value | Description |
|-------|-------------|
| `Exclusive` | Acquires lock at begin, holds until dispose. Other transactions wait. (default) |
| `Optimistic` | No lock at begin, acquires only during commit. Multiple transactions can run concurrently. |

### Conflict Behavior

Controls how optimistic transactions detect concurrent modifications.

| Value | Description |
|-------|-------------|
| `FailOnConflict` | Throw `SubjectTransactionConflictException` if values changed since transaction started. (default) |
| `Ignore` | Overwrite any concurrent changes without checking. |

Conflict detection is best-effort and not atomic with respect to non-transactional writes that happen between detection and apply. Transactions are serialized against each other, but a raw property write or an external source callback can still interleave during that window.

### Requirements

Enforces constraints on transaction scope, useful for protocols like OPC UA.

```csharp
using var tx = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    requirement: TransactionRequirement.SingleWrite);
```

| Value | Description |
|-------|-------------|
| `None` | No constraints - multiple sources and batches allowed. (default) |
| `SingleWrite` | Requires single source and changes within `WriteBatchSize` limit. Validated at commit time. |

**SingleWrite validation errors:**
- Multiple sources: `"SingleWrite requirement violated: Transaction spans 2 sources, but only 1 is allowed."`
- Exceeds batch size: `"SingleWrite requirement violated: Transaction contains 10 changes, but WriteBatchSize is 5."`

### Commit Timeout

**Default: 30-second timeout**, applied only to the external source write/revert phase (commits with no sources are not timed). If a source write exceeds it, that write is reported as a failure in a `SubjectTransactionException`; if a revert is in progress when it fires, an `OperationCanceledException` is thrown.

```csharp
// Custom timeout
using var tx = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    commitTimeout: TimeSpan.FromSeconds(60));

// Disable timeout
using var tx = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    commitTimeout: Timeout.InfiniteTimeSpan);
```

**Important:** The `cancellationToken` passed to `CommitAsync()` is **ignored during commit** - only the timeout can cancel. This prevents partial commits leaving sources inconsistent.

## Commit Flow

When `CommitAsync()` is called, changes are processed in stages. The exact flow depends on whether `WithSourceTransactions()` is configured.

### Without Source Transactions

When only `WithTransactions()` is configured (no external sources):

1. **Apply all changes** to the local model (calls property setters, triggers `OnChanging/OnChanged` methods)
2. If any apply fails and `Rollback` mode: revert successful applies
3. Fire change notifications

### With Source Transactions

When `WithSourceTransactions()` is configured, commits execute in two stages:

```
┌───────────────────────────────────────────────────────────────────────────┐
│  Stage 1: External sources (parallel when multiple)                       │
│  Write source-bound changes to OPC UA, MQTT, databases, etc.              │
│  Nothing is applied to the local model yet.                               │
├───────────────────────────────────────────────────────────────────────────┤
│  Stage 2: Apply to the local model in a single pass                       │
│  Source-bound and local changes are applied together, excluding           │
│  any whose source write failed (triggers OnChanging/OnChanged).           │
└───────────────────────────────────────────────────────────────────────────┘
```

Stage 2 applies source-bound changes marked with a `Confirmed` origin carrying the source that accepted them in stage 1, so the outbound change queue treats their notifications as echoes and a committed value is written to its source exactly once. See [Change notification source semantics](connectors.md#change-notification-source-semantics) for the full contract, including cascade and derived property behavior. Value-transforming write interceptors are not supported with source transactions: the source would receive the stage 1 value while the local model applies the transformed one.

Stage 1 is performed by an `ITransactionWriter`; the built-in `SourceTransactionWriter` is registered by `WithSourceTransactions()`. Replacing it is an advanced scenario, see [Implementing a Custom Transaction Writer](#implementing-a-custom-transaction-writer).

**Rollback behavior on failure:**

| Failure Stage | BestEffort | Rollback |
|---------------|------------|----------|
| Source write fails | Successful sources written and applied | All sources reverted, nothing applied |
| Local apply fails (source-bound or local) | Successful kept, failed sources reverted | All reverted |

Both modes ensure **per-property consistency**: if a property's local apply fails, its source write is reverted. The difference is whether successful properties are kept (BestEffort) or also reverted (Rollback).

Revert operations call setters with old values, which also trigger `OnChanging/OnChanged` methods.

### Source Association

Properties must be associated with a source for external writes:

```csharp
// In your source implementation
propertyReference.SetSource(this);

// On commit, WriteChangesInBatchesAsync is called on the source
```

Properties without an associated source are applied in Stage 2 alongside source-bound properties.

**Changing a source while a commit runs:** the source a property writes to is decided when the transaction commits, not when you set the value. So if you call `SetSource` or `RemoveSource` on a property at the same time a commit is running, it is undefined whether that commit uses the old or the new binding for that property.

Rollback is not affected by this: a revert always undoes the writes at the sources they were actually written to, even if the binding changed in the meantime.

## Error Handling

### SubjectTransactionException

Thrown when one or more changes fail during commit:

```csharp
try
{
    await transaction.CommitAsync(cancellationToken);
}
catch (SubjectTransactionException ex)
{
    Console.WriteLine($"Failed: {ex.FailedChanges.Count}, Applied: {ex.AppliedChanges.Count}");

    foreach (var error in ex.Errors)
    {
        Console.WriteLine($"  {error.Message}");
    }
}
```

The built-in `SourceTransactionWriter` never throws: a source that fails or throws is reported as a failed write and reverted through the normal failure handling. Only a custom `ITransactionWriter` that throws instead of reporting violates this contract. In that case the commit fails with a `SubjectTransactionException` reporting every change as failed, applies nothing to the local model, and the transaction becomes terminal (it must be disposed, not retried). Its sources are not reverted then, because the writer never returned the written set and revert state that reverting requires, so they may be left in an undefined state. A custom writer that throws from `RevertSourceWritesAsync` is handled the same way: the changes it was asked to revert are reported as failed reverts and the commit fails terminally. An in-place marking contract violation detected by `SubjectTransaction.ValidateWriterContract` (see [Implementing a Custom Transaction Writer](#implementing-a-custom-transaction-writer)) also fails terminally. A commit timeout during a source revert (`OperationCanceledException`) is the one exception and stays retryable; a timeout during a source write is reported as a failed write and handled like any other source failure, which is terminal.

### SubjectTransactionConflictException

Thrown when optimistic locking detects concurrent modifications:

```csharp
try
{
    await transaction.CommitAsync(cancellationToken);
}
catch (SubjectTransactionConflictException ex)
{
    Console.WriteLine($"Conflicts on: {string.Join(", ", ex.ConflictingProperties.Select(p => p.Name))}");
}
```

### Other Exceptions

| Exception | Cause |
|-----------|-------|
| `InvalidOperationException` | Nested transactions, already committed, transactions not enabled, committing from a different async flow |
| `ObjectDisposedException` | Using a disposed transaction |
| `OperationCanceledException` | Commit timeout during source revert (source-write timeouts are reported via `SubjectTransactionException`) |

### Failure Flows and Consistency

A commit attempt ends in one of three states:

- **Committed**: the commit succeeded. The transaction is finished and must be disposed.
- **Failed, retryable**: the commit aborted before the local model was touched, either before anything was written at all (conflict detected, optimistic lock not acquired) or because a commit timeout interrupted it. The pending changes remain intact and `CommitAsync` can be called again on the same transaction. See [Retry After Conflict Detection](#retry-after-conflict-detection).
- **Failed, terminal**: the commit got past the source-write stage, so writes may have reached sources or the local model, and compensation (reverts) already ran once. The pending changes are cleared, a second `CommitAsync` throws `InvalidOperationException`, and the transaction must be disposed and replaced with a new one. Retrying would replay the snapshot onto state that has already moved, for example re-pushing values to a source that already accepted them.

Note that "terminal" describes the transaction, not the result: a successful commit is also terminal. Transactions are one-shot once anything has moved.

The tables below list every flow and its end state per property. "Old" means the pre-transaction value, "new" means the transaction's value.

**Local-only commits** (no `WithSourceTransactions()`). There is only the local model, so nothing can diverge; the only question is which properties end old and which end new:

| Scenario | Mode | Local model ends as | Outcome |
|----------|------|---------------------|---------|
| All applies succeed | any | new | committed |
| Some applies fail | BestEffort | applied keep new, failed keep old | terminal failure, reported per change |
| Some applies fail, reverts succeed | Rollback | all old | terminal failure |
| Some applies fail, a revert also fails | Rollback | mixed, despite Rollback | terminal failure, stuck properties are in `FailedChanges` |

Commits with source writes (`WithSourceTransactions()` or a custom `ITransactionWriter`) involve two models that must agree: the external source and the local model. The next two tables split these flows by how they end.

**Source writes, consistent end state.** The commit fully succeeds, fails before anything is written, or every needed revert succeeds, so source and local model agree on every property:

| Scenario | Mode | Source ends | Local ends | Outcome |
|----------|------|-------------|------------|---------|
| All source writes and applies succeed | any | new | new | committed |
| Conflict detected or optimistic lock not acquired (nothing written yet) | any | old | old | retryable failure |
| A source write fails or times out, reverts succeed | Rollback | all old | all old | terminal failure |
| A source write fails or times out | BestEffort | succeeded new, failed old | matches source | terminal failure |
| A local apply fails, all reverts succeed | Rollback | all old | all old | terminal failure |
| A local apply fails, its source revert succeeds | BestEffort | applied new, failed-apply old | matches source | terminal failure |
| Writer reports an error without failed changes (custom writers only), reverts succeed | Rollback | all old | all old | terminal failure |
| Writer reports an error without failed changes (custom writers only) | BestEffort | new | new | terminal failure, error surfaced in `Errors` |

**Source writes, diverged end state.** A revert failed, was interrupted, or never ran, so a property can end with different values at the source and in the local model. The end state depends only on which revert got stuck, not on which stage triggered it, so each row covers every path that reaches it. Diverged properties are always reported in `FailedChanges` and `Errors`, except for a throwing writer where the transaction cannot know which sources were touched:

| Scenario | Mode | Source ends | Local ends | Outcome | Divergence |
|----------|------|-------------|------------|---------|------------|
| Commit timeout during source revert | Rollback | partially reverted | old | retryable failure | transient, a successful retry re-pushes everything |
| A source revert fails or throws | any | new on the stuck source | old | terminal failure | source ahead of local |
| A local revert fails after a failed apply | Rollback | old | new | terminal failure | local ahead of source |
| Custom writer throws from `WriteToSourcesAsync` | any | unknown, never reverted | old | terminal failure | unknown and unreported |

Three root causes account for every divergence:

1. **A compensating write failed.** Reverts are inverse writes, not an undo. A source that just failed or timed out is asked to accept another write, so this is the most likely divergence in practice. It is always terminal and always reported through `FailedChanges` and `Errors`, so the caller knows exactly which properties are stuck.
2. **The writer threw instead of reporting.** Only a custom `ITransactionWriter` can cause this (the built-in writer always reports). The transaction has no written set and no revert state, so it cannot compensate and cannot tell which sources were touched. See [SubjectTransactionException](#subjecttransactionexception).
3. **The inherent in-flight window.** Sources are written before the local model is applied, so even a fully successful commit has a moment where a source holds the new value and the local model does not, and during Rollback compensation a source briefly holds a value that is then taken back. Transactions provide quiescent consistency (both sides agree once the commit settles), not isolation from external observers of the source.

## Advanced Topics

### Local Property Failures

Properties can fail during commit if their `OnChanging/OnChanged` partial methods throw exceptions. This is useful for hardware integrations like GPIO.

```csharp
[InterceptorSubject]
public partial class GpioDevice
{
    public partial bool LedA { get; set; }
    public partial bool LedB { get; set; }

    partial void OnLedAChanged(bool newValue) => _gpio.Write(pinA, newValue); // can throw!
    partial void OnLedBChanged(bool newValue) => _gpio.Write(pinB, newValue); // can throw!
}
```

When `OnChanging/OnChanged` throws:
- **BestEffort mode**: Other successful changes are applied, failure reported
- **Rollback mode**: All previous stages are reverted (sources + successful local changes)

### Derived Properties

Derived properties (marked with `[Derived]`) are handled specially. The pending read view belongs to each execution flow carrying a live ambient transaction, while derived tracking is shared across flows and therefore uses only values that have landed in the model:

- **Direct reads**: Getters called by application code in a flow carrying a live transaction use that transaction's pending values. A flow without a live transaction reads the landed model.
- **Shared tracking**: Cached derived values, dependencies, timestamps, and notifications use values that have landed in the model.
- **Captured writes**: Non-derived writes remain silent until commit replay applies them.
- **Landed writes**: Writes to settable derived properties are not captured. They land, recalculate, and notify immediately while a transaction is open, and survive transaction disposal or rollback.
- **Derived chains**: Derived-of-derived tracking uses the landed model view and retains flattened source dependencies.

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }
    public partial string LastName { get; set; }

    [Derived]
    public string FullName => $"{FirstName} {LastName}";
}

using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
{
    person.FirstName = "John";
    person.LastName = "Doe";

    Console.WriteLine(person.FullName); // Output: John Doe (from pending values)

    await transaction.CommitAsync(cancellationToken);
    // FullName tracking is updated when the captured writes land during commit.
}
```

The same landed-model rule applies when tracking evaluates a getter during initial attachment, automatic recalculation, manual `RecalculateDerivedProperty()`, or a stabilization retry. It does not clear or replace the ambient transaction, so non-derived writes performed as getter side effects are still captured and direct reads resume the pending view as soon as the tracking evaluation finishes.

Notification boundaries follow the evaluation nesting. A top-level derived recalculation publishes after its tracking evaluation has ended, so a synchronous subscriber reads pending values normally. If a getter causes a reentrant derived recalculation or a landed side-effect notification, that nested publication occurs while the outer getter is still being evaluated. Synchronous subscribers in that nested path read landed model values until the outer evaluation finishes.

### Capture and Commit Replay

Transactions buffer property writes in a pending dictionary instead of applying them immediately. Consider a motor with a configurable speed limit, where a validator rejects `MotorSpeed` values that exceed `MaxAllowedSpeed`:

```csharp
[InterceptorSubject]
public partial class Motor
{
    public partial int MaxAllowedSpeed { get; set; } // Default: 100
    public partial int MotorSpeed { get; set; }      // Validated: must be <= MaxAllowedSpeed
}
```

**During the transaction (capture phase):**

```
motor.MaxAllowedSpeed = 200;
    → ValidationInterceptor: validates 200 for MaxAllowedSpeed → OK
    → TransactionInterceptor: captures in pending[MaxAllowedSpeed] = 200, stops chain
    (no field write, no notifications)

motor.MotorSpeed = 150;
    → ValidationInterceptor: validates 150 for MotorSpeed
        reads MaxAllowedSpeed → TransactionInterceptor returns pending value 200
        150 <= 200 → OK
    → TransactionInterceptor: captures in pending[MotorSpeed] = 150, stops chain
    (no field write, no notifications)
```

**On commit (replay phase):**

Changes are replayed in insertion order through the full interceptor chain against the real model:

```
await transaction.CommitAsync(cancellationToken);

Apply pending[MaxAllowedSpeed] = 200:
    → ValidationInterceptor: origin is Local (no source bound) → validates 200 → OK
    → TransactionInterceptor: IsCommitting=true → calls next (no capture)
    → Field write: MaxAllowedSpeed = 200
    → Notifications fired

Apply pending[MotorSpeed] = 150:
    → ValidationInterceptor: origin is Local → validates 150
        reads MaxAllowedSpeed from real model → 200 (already committed above)
        150 <= 200 → OK
    → TransactionInterceptor: IsCommitting=true → calls next (no capture)
    → Field write: MotorSpeed = 150
    → Notifications fired
```

**A change a source confirmed is not re-validated on replay.** The walkthrough above is a plain local transaction, so both changes replay as `Local` and are validated. A change a source accepted during the commit is marked `Confirmed` by the transaction writer and skips validation, because the model already validated it at capture and rejecting it would revert an accepted write. The distinction is per change, not per property and not per transaction: a single commit can mix confirmed changes that skip replay validation with local ones that do not. See [Validation](validation.md#what-is-not-validated).

**Why insertion order matters:** If `MotorSpeed` were committed before `MaxAllowedSpeed`, the validator would read `MaxAllowedSpeed = 100` from the real model and reject the write.

**Write order limitation:** Only the final value per property is stored (last write wins), and the commit position is determined by the *first* write to each property. If a property is re-written with a value that has different dependency requirements than the initial value, the commit order (based on first-write positions) may no longer match the dependency order needed by the final values. This can cause commit-time validation to fail even though the final set of values is consistent. It applies to changes that replay as local, since a change a source confirmed is not validated on replay. To avoid this, ensure that re-writes do not shift dependency requirements relative to the original insertion order. See [#192](https://github.com/RicoSuter/Namotion.Interceptor/issues/192) for details and potential solutions.

### Retry After Conflict Detection

When using `TransactionConflictBehavior.FailOnConflict`, a `SubjectTransactionConflictException` is thrown *before* any writes are applied. The pending changes remain intact, so you can modify them and retry:

```csharp
using var transaction = await context.BeginTransactionAsync(
    TransactionFailureHandling.BestEffort,
    conflictBehavior: TransactionConflictBehavior.FailOnConflict);

motor.MaxAllowedSpeed = 200;
motor.MotorSpeed = 150;

try
{
    await transaction.CommitAsync(cancellationToken);
}
catch (SubjectTransactionConflictException ex)
{
    // Conflict detected before any writes: pending changes are still intact.
    // Re-read current state, adjust, and retry.
    motor.MaxAllowedSpeed = 250;
    await transaction.CommitAsync(cancellationToken);
}
```

Post-write failures (`SubjectTransactionException` from `BestEffort` or `Rollback`) clear the pending changes as part of the commit process, so retry is not possible. Dispose the transaction and start a new one.

Note that concurrent `CommitAsync` calls on the same transaction are rejected: only one commit attempt can be in progress at a time.

### Thread Safety

- `BeginTransactionAsync()` uses `AsyncLocal<T>` to store the current transaction
- Exclusive transactions use a per-context semaphore
- Each async execution context has its own transaction scope
- `Dispose()` clears the transaction only for the disposing flow. A flow that captured its execution context earlier can retain the disposed transaction in its slot; reads and writes on that flow treat it as inactive.
- After `Dispose()`, ambient access is ordinary raw model access even while an already-started external-writer commit completes. Such a raw write is not isolated from the commit and can be overwritten when its frozen snapshot replays.
- A successful or terminally failed commit is inactive for property interception even before disposal. Later reads and writes use the landed model normally; retryable failures remain active.
- While a live ambient transaction is committing, property reads and writes on a flow carrying it throw `InvalidOperationException`. This includes custom writer callbacks. Getter reads are also rejected because sibling or landed-model state is outside the frozen snapshot and can make the source payload inconsistent.
- Commit-owned synchronous local replay remains allowed, including property hooks, change handlers, and derived cascades initiated by that replay. This authorization does not extend into custom writer callbacks or child tasks.
- A transaction must be begun, used, committed, and disposed within the same async flow; committing from another flow throws
- Concurrent `CommitAsync` calls on the same transaction instance are rejected

## Best Practices

1. **Always use `using` blocks** - Ensures proper disposal even on exceptions
2. **Keep transactions short** - Long transactions hold pending changes in memory
3. **Handle exceptions from CommitAsync** - Commits can fail partially
4. **Don't share transactions across threads** - Each async context should have its own transaction

## API Reference

### BeginTransactionAsync

```csharp
TransactionAwaitable BeginTransactionAsync(
    TransactionFailureHandling failureHandling,
    TransactionLocking locking = TransactionLocking.Exclusive,
    TransactionRequirement requirement = TransactionRequirement.None,
    TransactionConflictBehavior conflictBehavior = TransactionConflictBehavior.FailOnConflict,
    TimeSpan? commitTimeout = null, // default: 30 seconds
    CancellationToken cancellationToken = default)
```

### SubjectTransaction

| Member | Description |
|--------|-------------|
| `CommitAsync(CancellationToken)` | Commits all pending changes |
| `GetPendingChanges()` | Returns list of pending changes |
| `Dispose()` | Discards uncommitted changes and releases lock |
| `Context` | The context this transaction is bound to |
| `Locking` | The locking mode |
| `ConflictBehavior` | The conflict detection behavior |

### Enums

**TransactionFailureHandling:**
- `BestEffort` - Apply successful changes, rollback failed ones (per-property consistency)
- `Rollback` - All-or-nothing across all properties

**TransactionLocking:**
- `Exclusive` - Lock at begin, hold until dispose
- `Optimistic` - Lock only during commit

**TransactionRequirement:**
- `None` - No constraints
- `SingleWrite` - Single source, within batch size

**TransactionConflictBehavior:**
- `FailOnConflict` - Throw on concurrent changes
- `Ignore` - Overwrite without checking

## Limitations

### Single Context Binding

A transaction is bound to a single `IInterceptorSubjectContext`. Attempting to modify a property on a subject from a different context throws `InvalidOperationException`:

```csharp
var context1 = InterceptorSubjectContext.Create().WithTransactions();
var context2 = InterceptorSubjectContext.Create().WithTransactions();

var person1 = new Person(context1);
var person2 = new Person(context2);

using var tx = await context1.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

person1.FirstName = "John";  // OK - same context as transaction
person2.FirstName = "Jane";  // THROWS InvalidOperationException:
                              // "Cannot modify property 'FirstName': Transaction is bound to a different context."
```

### Nested Transactions

Nested transactions are not supported. Attempting to start a transaction while one is already active in the same execution context throws `InvalidOperationException`:

```csharp
using var tx1 = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

// THROWS: "Nested transactions are not supported."
using var tx2 = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
```

### Optimistic Concurrency - ABA Problem

`FailOnConflict` uses value-based detection. If a property changes A → B → A between start and commit, no conflict is detected. For strict version-based detection, implement external versioning.

### Multi-Source Transactions

When writing to multiple sources, true atomicity is not guaranteed:
1. Writes are parallel for performance
2. On failure, successful sources are rolled back (best effort)
3. During rollback, sources may temporarily have inconsistent values

For strict atomicity, use a single source per transaction or implement application-level compensation.

### Rollback is Best-Effort

Rollback operations can also fail. If revert fails, `SubjectTransactionException` includes both the original failure and the revert failure. The system cannot guarantee consistency in this case. See [Failure Flows and Consistency](#failure-flows-and-consistency) for the full matrix of end states.

## Implementing a Custom Transaction Writer

Most applications use the built-in `SourceTransactionWriter` registered by `WithSourceTransactions()` and never implement `ITransactionWriter`. A custom writer replaces stage 1 of the commit flow (registered as an `ITransactionWriter` service on the context) and is only needed when source writes require protocol-specific orchestration the built-in writer cannot provide.

Both writer callbacks execute while the caller's live ambient transaction is committing. Do not read or write subject properties from either callback, and do not suppress ambient execution-context flow to bypass this boundary. This includes getters: sibling or landed-model state is not represented by the supplied snapshot and can make source operations inconsistent with it. Construct writes from `changes` and `requirement`, and reverts from `written` and `revertState`, together with writer-owned configuration and any required subject state captured before `CommitAsync()`.

A custom writer must follow the in-place marking contract documented in the XML documentation of `ITransactionWriter.WriteToSourcesAsync`. Declining to mark also means the change replays as local, so any registered validator runs against it again. Moving or replacing a snapshot slot silently corrupts the local apply. Not marking an accepted slot is allowed, but its apply notification publishes without a `Confirmed` origin and the outbound queue pushes the committed value to the source again. While developing a writer, enable the runtime contract validation:

```csharp
SubjectTransaction.ValidateWriterContract = true;
```

The commit then fails terminally with a `SubjectTransactionException` if a slot was moved or replaced (sources may already have been written, so no retry). The check covers slot identity only: missing or wrong-source marks are not detected. Leave it disabled in production (one array allocation and sweep per commit); the built-in `SourceTransactionWriter` does not need it.
