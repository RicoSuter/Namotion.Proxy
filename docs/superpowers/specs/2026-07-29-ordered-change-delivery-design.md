# Ordered and exactly-once property change delivery

Date: 2026-07-29 (reworked 2026-07-30 after two independent adversarial review rounds)
Status: Draft, awaiting user review (review round 2 verdict: mechanism core holds; its seven
required text fixes are applied below)
Fixes: #385 (both proposals: the commit sequence number ships as `Revision`, and the FIFO delivery
investigation resolves into the slot mechanism). One #385 sub-claim is superseded: consumer-side
completeness detection via contiguous sequences is unsound at the subscribe boundary (see
Findings); completeness comes from in-lock reservation instead.

## Problem

The library exposes three property change delivery channels (per-property callbacks, an Rx
observable, and a pull queue), and none of them guarantees delivery order. All dispatch happens
after the commit and outside the subject lock, so under concurrent writers a change committed
second can be delivered first. Consumers have no way to tell which of two deliveries reflects the
newer stored value.

This is not only a theoretical gap. `ChangeQueueProcessor.TryFlushAsync` deduplicates a flush batch
by array position (backward iteration, last occurrence wins, `ChangeQueueProcessor.cs:243-256`).
Array position is enqueue order, which is post-commit race order, not commit order. Under
concurrent writers to one property the dedup can keep the earlier commit's value and hand a
connector a stale final value. That is a live correctness bug on the state-mirroring path. This
design fixes the within-flush case (the common burst) and documents the cross-flush case as a
remaining limit with a recorded follow-up; see section 9.

Delivery can also be lost entirely: a throwing lifecycle handler suppresses the write's
notification (documented in #377, shipped in practice as #386), and a derived getter that throws
during payload construction suppresses it too. The second case is not even a contract violation.

Four consumer needs motivate the work:

1. State-mirroring sources (OPC UA, MQTT, WebSocket) must converge to the newest committed value.
2. Cross-channel consistency: a consumer reading two channels needs them to agree.
3. Cross-property causal order: "the flag was set after the value".
4. A principled, documented contract for the whole notification surface.

Expected usage profile (a design input, not an afterthought): a typical application holds **many
per-property subscriptions** (often ordered) as a high-performance alternative to context-wide
observables, and sources sit on the **queue channel**, which must stay allocation-free and as fast
as possible.

## Goals

- A delivery mode with a real guarantee: exactly-once, in commit order. Not a heuristic.
- Preserve the existing fast path for consumers that do not opt in.
- Per-property subscription cost must scale with what the written property has subscribed, not with
  the total subscription count in the process.
- The armed queue path stays allocation-free in steady state.
- Make every consumer state which guarantee it wants, at the call site.
- Document precisely what each channel and mode does and does not promise, with conditional
  guarantees stated as conditional.

## Non-goals

- Global (cross-subject) ordered delivery and cross-subject labelling. See "Limits".
- Turning the synchronous channels into ordered channels. Ordering requires buffering, and a
  synchronous channel has no buffer: the writer thread is the delivery thread.
- Ordered delivery for hand-written subjects whose `Context` is not an `InterceptorExecutor`.
  Explicitly unsupported (today `SetPropertyValueWithInterception` already silently no-ops for
  them, `PropertyReferenceExtensions.cs:15-16`).

## Findings that constrain the design

Verified in code; these drive the decisions.

**The commit point is already under a per-subject lock.** `WriteInterceptorFactory.cs:14-21`
(chain terminal) and `WriteInterceptorFactory.cs:12-22` (interceptor-less terminal; both terminals
must be instrumented):

```csharp
lock (context.Property.Subject.SyncRoot)
{
    innerWriteValue(context.Property.Subject, context.NewValue);
    context.IsWritten = true;
    context.FinalizeOrigin();
    context.Property.SetWriteTimestamp(raw > 0 ? raw : 0);
}
```

`SyncRoot` is generated per subject instance (`SubjectCodeGenerator.cs:151`). This is the
linearization point and it gives per-subject granularity for free.

**Every subject owns a per-subject executor, and the write chain is compiled per aggregated
context set and cached with invalidation.** `SubjectCodeGenerator.cs:142` emits
`_context ??= new InterceptorExecutor(this)`; the executor caches the compiled write chain and
rebuilds it on `OnContextChanged` (attach, detach, service changes). Chain compilation is therefore
an existing, correctly-invalidated point at which per-context state can be bound to the write path.

**Post-commit subscription resolution is unsound for ordered delivery.** This finding overturned an
earlier revision of this design. If writers resolve the subscription list after the commit
(outside the lock), resolution order is not commit order: two writes racing a `Subscribe` can
resolve in inverted real-time order, so revision k's writer sees the new subscription while
revision k+1's writer (which committed later but resolved earlier) misses it. The subscription then
receives k, k+2, k+3 and any gap-waiting consumer stalls forever, while any non-waiting consumer
breaks ordering. No baseline heuristic, timeout, or sorting fixes this: the information "k+1 is not
coming" exists only in a writer that has published nothing. The subscription state must be read at
a point serialized with installs, which means inside the subject lock. The only alternative family
(pre-chain claims plus an install quiescence wait) was worked out in full and rejected: it requires
two interlocked operations per write on a shared line even with zero subscribers, a `Subscribe`
that blocks on in-flight lifecycle handlers, and a revision-sorting round-based pump.

**A timeout-based reorder buffer cannot work.** The window between commit and any post-commit
publication spans the entire unwind including lifecycle reconciliation, so it is bounded by the
slowest handler, not a constant. A late arrival leaves only emit-out-of-order or drop.

**`GetFinalValue()` invokes user code for derived properties** (`IWriteInterceptor.cs:210-217`):
`metadata.GetValue?.Invoke(...)` when `metadata.IsDerived`. Running user getters under `SyncRoot`
risks lock-order inversion (a getter such as `TotalPower => Devices.Sum(d => d.Power)` takes other
subjects' locks), so payload construction must stay outside the lock.

**Derived properties have two distinct write paths.**

1. Recalculation: `DerivedPropertyChangeHandler.NotifyDerivedPropertyChanged:365` calls
   `SetPropertyValueWithInterception(newValue, oldValue, NoOpWriteDelegate, rawTimestamp)` where
   `newValue` is the stabilized getter output already committed to `data.LastKnownValue`. On this
   path `NewValue` is the correct final value, and `GetFinalValue()`'s re-invocation of the getter
   is what creates both a throw window and a torn pair (`OldValue` from recalculation N, `NewValue`
   from a later state).
2. Derived-with-setter (`DerivedPropertyChangeHandler.cs:156`): user code calls the setter of a
   derived property that has one. `NewValue` is the setter input while the observable value is the
   getter output, so re-invocation is load-bearing and must stay.

**Aggregated contexts put multiple `PropertyChangeInterceptor` instances into one chain** (the
existing `ArePropertyObserversResolved` flag exists for exactly this), but the **terminal runs
exactly once per logical write**: one chain, one terminal; contexts contribute interceptor
instances, not chains.

**The generated null-context fast path bypasses interception entirely.**
`SubjectCodeGenerator.cs:378-389`: when `_context is null` the setter stores the field directly
(no lock, no revision, no reservation), and `_context` is published with a plain `??=`. All
guarantees in this document are scoped to intercepted writes.

**Transactions compose cleanly.** During capture, `SubjectTransactionInterceptor.WriteProperty`
returns without calling `next` (`SubjectTransactionInterceptor.cs:85`), so the terminal never runs:
no revision is assigned, no slot is reserved. During commit replay (`IsCommitting: true`) writes
flow through the normal chain, so slots are reserved at replay, in replay order, under the same
locks as ordinary writes. The echo suppression surviving from #344/#343/#366 is consumer-side
origin filtering: commit applies carry the confirming source in their origin
(`SubjectTransaction.cs:400`) and `ChangeQueueProcessor.ProcessAsync:145` skips changes whose
`Origin.Source` equals its own source. Ordered subscriptions deliver commit applies as ordinary
ordered changes; consumers filter echoes by origin exactly as today.

## Design

The mechanism is a claim/publish split mapped onto the existing lock: **reserve slots inside the
subject lock (order), publish the payload in a `finally` outside it (content), deliver FIFO with a
wait-on-pending head (guarantee)**.

### 1. Per-subject `Revision` on the executor

Every committed write increments an internal `long` field on the subject's `InterceptorExecutor`,
inside `SyncRoot`: a plain increment on an object that is hot for the whole write. No dictionary
lookup, no `Interlocked`, no shared cache line, no public API, no leak. Both terminal variants
(chain and interceptor-less) are instrumented. For subjects whose `Context` is not an
`InterceptorExecutor`, the label falls back to a per-subject `long[1]` holder in `Subject.Data`
(one `GetOrAdd` per write, the same cost class as the existing `SetWriteTimestamp` storage); ordered
delivery is not offered for such subjects (see Non-goals).

The revision is stamped into an internal field on `PropertyWriteContext<TProperty>` (the pattern
#383 established with `Terminal`). It is a **label**, not the ordering mechanism: ordering comes
from slot positions. It exists for the `ChangeQueueProcessor` dedup fix, last-writer-wins
convergence on the `Immediate` channels, and cross-channel agreement within a subject. Because it
is not load-bearing for ordering, no contiguity invariant exists: vetoed writes, no-op writes, and
transaction capture need no special care.

The revision and the write timestamp cannot share a slot (timestamp is per property, revision per
subject), and no merged operation is needed: consumers read both from the `SubjectPropertyChange`,
stamped inside the same lock hold and therefore mutually consistent. Nobody polls the storage.

### 2. Subscription topology: two kinds, two homes

**Context-wide ordered subscriptions** (queue, observable, async stream: the source-facing
channels; typically 1-2 per context) live in an **ordered registry captured in the chain closure**.
The registry is a small core-owned object per context holding a volatile subscription array. It is
resolved at chain-build time like any other service and baked into the terminal's closure.
`Subscribe` mutates the array inside the registry; the terminal reads it in-lock.

Why the chain closure and not per-subject state: the chain is already compiled from the aggregated
context set and invalidated on every attach, detach, and service change (`OnContextChanged`). That
machinery is exactly the install/evict protocol an executor-side field would need to reinvent, it
already handles multi-context aggregation, and it works for every context regardless of which
services are configured (a context with only the observable channel has no lifecycle interceptor,
so lifecycle-based installation would silently never run). Because the registry and
`PropertyChangeInterceptor` are registered together (`WithPropertyChangeSubscriptions()`), one
chain-build snapshot contains both or neither.

**Per-property ordered subscriptions** (the high-frequency kind per the usage profile) are
**indexed subject-side**, mirroring the #377 `Immediate` listener layout but as a **parallel
core-owned structure**: the terminal (core) cannot read Tracking-internal types
(`InternalsVisibleTo` runs core to Tracking only), so the ordered index gets its own core-owned
element type, `Subject.Data` key, and gate. The gate is a **per-subject count on the executor**, so
subjects with no ordered per-property subscriptions pay a field load, and only subjects that have
them pay the indexed dictionary lookup, which now runs inside `SyncRoot` (unlike today's
post-commit lookup; benchmark gate 4 owns this). The terminal reserves **only into subscriptions
matching the written property**: typically 0 or 1 reservations. Cost scales with what the written
subject and property have subscribed, never with the process-wide count.

**The gate is not a plain field.** It follows the discipline #377 established for its process-wide
counterpart (`PropertyChangeSubscriptions.cs:12-22`, `PropertyChangeSubscription.cs:33,60,67`):
`Interlocked` increment and decrement, **increment before install**, **decrement after removal**,
and an `Interlocked.MemoryBarrier()` after the install, because a `ConcurrentDictionary` bucket-lock
release is not a full fence. A plain field loses updates under concurrent subscribes to two
properties of one subject, and a lost increment permanently closes the gate on a live subscription:
every later write reads zero, reserves nothing, and the section 7 guarantee is silently violated
forever. That trailing fence is also what makes the section 7 boundary argument airtight on weak
memory: the in-lock reader's fence comes from `Monitor.Enter`, but the installer needs its own.

Cost model per write, in-lock:

| Write target | In-lock ordered-delivery cost |
|---|---|
| Subject without ordered per-property subs, no context-wide subs | marker check + per-subject count load |
| Subject with ordered subs, write to an unobserved property | + one indexed lookup miss |
| Property with one ordered per-property sub | + one lookup + one reservation |
| Context-wide subs armed | + one reservation per context-wide sub |

### 3. Slot reservation inside the lock (core)

The terminal gains, inside the existing `lock (SyncRoot)`:

```csharp
var executor = subject.Context as InterceptorExecutor;
var revision = executor is not null
    ? ++executor.Revision
    : IncrementRevisionFallback(subject);          // Data long[1] holder: label only;
                                                   // ordered delivery unsupported here

// Reservation is gated on the publisher marker: PropertyChangeInterceptor sets an internal
// flag on the write context BEFORE calling next(). Reservation therefore cannot happen unless
// the resolver is provably above the terminal on this call stack. A chain without the
// interceptor (misconfiguration, teardown race) skips reservation: a miss, never a stall.
if (context.PublisherPresent && executor is not null)   // ordered delivery requires an executor
{                                                      // (Non-goals); the null branch above keeps
    var record = context.ReservationRecord;             // the label working for other subjects
                                                   // pooled, rented by the interceptor
                                                   // OUTSIDE the lock (see below)
    // Context-wide subscriptions, from the chain-closure registries. The chain build
    // captures ALL ordered registries visible in the aggregated context set (an executor
    // aggregating two subscribing contexts has two; instances are captured, not types).
    // The in-lock read is serialized per subject by SyncRoot: once one write to a subject
    // observes a subscription, every later write observes it too. The owed set is a clean
    // per-subject suffix by construction; this is the boundary-correctness argument.
    foreach (var registry in registries)           // captured at chain build
    {
        var subs = registry.Subscriptions;         // volatile read
        for (var i = 0; i < subs.Length; i++)
        {
            var slot = subs[i].Buffer.ReserveSlot();  // interlocked cursor bump into a
            slot.Revision = revision;                 // segmented array of structs
            slot.State = Pending;
            record.Track(subs[i], slot);           // progress count incremented AFTER a
        }                                          // successful reserve: failure-atomic
    }

    // Per-property ordered subscriptions: per-subject count gate (interlocked executor field,
    // read here under the lock), then the core-owned indexed lookup.
    if (Volatile.Read(ref executor.OrderedPropertyListenerCount) != 0 &&
        TryGetOrderedListeners(context.Property, out var listeners))
    {
        // same reserve-and-track loop
    }
}

innerWriteValue(subject, context.NewValue);        // reservation BEFORE the store: the store
context.IsWritten = true;                          // is a field write on the generated path;
context.FinalizeOrigin();                          // if reservation throws, nothing was
context.Property.SetWriteTimestamp(...);           // committed and the finally cancels the
                                                   // tracked prefix. Committed implies reserved.
```

Properties of this step:

- **Unarmed cost**: the publisher-marker check (a bool on the by-ref context) plus, when the marker
  is set, a registry array read and a count-gate load. Fully accounted: `subject.Context` interface
  dispatch, `isinst InterceptorExecutor`, the revision increment, and the context stamp are part of
  the always-on cost and are what benchmark gate 1 measures.
- **Armed cost**: interlocked cursor bump and two field writes per matching subscription. **No
  per-write allocator inside the lock**: the reservation record is pooled and rented by the
  interceptor before `next()`; slot buffers are segmented arrays whose growth allocates only
  amortized, occasionally inside the lock (the `ConcurrentQueue` model that already measures 0 B;
  benchmark gate 4's tail latencies own the occasional in-lock segment allocation).
- **Failure atomicity**: the record's progress count is incremented only after a successful
  reserve, so a throw anywhere in the loop leaves exactly the tracked prefix, which the `finally`
  cancels. The value store happens after all reservations; an aborted reservation aborts the write
  before commit.
- **Aggregation**: the terminal runs once per logical write, so reservation is deduplicated by
  construction. Publish-side dedup is separate (section 4).
- The cross-subject caveat, stated: the per-subject lock does not serialize different subjects
  reserving into the same subscription's buffer, hence the interlocked cursor. Per-subject commit
  order is preserved (same-subject reservations are under `SyncRoot`), which is exactly the
  promised guarantee. Cursor contention across subjects is inherent to any shared totally-ordered
  queue.

Layering: the slot buffer, slot state, registry, and reservation record are small **core-owned
internals**; Tracking consumes them via `InternalsVisibleTo` (the #383 pattern). No interface, no
virtual call, no delegate. No public API is added to core.

### 4. Publish in the `finally` (Tracking)

`PropertyChangeInterceptor.WriteProperty`:

```csharp
// Rent the pooled reservation record and set the publisher marker BEFORE next(), but only
// when ordered delivery is armed anywhere (registry subs or ordered property-subscription
// count nonzero): the idle fast path stays free of both the rental and the try/finally.
var armed = /* registry subs present || subject's ordered per-property count != 0 */;
if (armed && context.ReservationRecord is null)     // idempotent under aggregation: only the
{                                                   // outermost armed instance rents; an
    context.ReservationRecord = RecordPool.Rent();  // unconditional rent would overwrite the
    context.PublisherPresent = true;                // field and leak one pooled record per
}                                                   // write at aggregation depth >= 2

try
{
    next(ref context);                              // commit + lifecycle reconciliation
    DispatchImmediateChannels(ref context);         // unchanged post-unwind dispatch
}
finally
{
    if (context.ReservationRecord is { } record)    // consume-once: the innermost aggregated
    {                                               // instance resolves and nulls the record;
        context.ReservationRecord = null;           // outer instances see null and no-op
        if (context.IsWritten)
            PublishSlots(record, ref context);      // payload built outside the lock, struct
        else                                        // copied into each tracked slot, then
            CancelSlots(record);                    // State = Released (volatile), drains
        RecordPool.Return(record);                  // signaled. Cancel covers vetoes and
    }                                               // partial reservations.
}
```

- The `finally` guarantees every tracked slot resolves to Released or Cancelled, so a drain waiting
  on a Pending head always makes progress. A throwing lifecycle handler no longer suppresses
  delivery to ordered subscribers: the value was committed, so it is delivered, and the exception
  still propagates unchanged. This is a deliberate, documented semantic change.
- The innermost aggregated instance's `finally` runs first on the unwind and is still outside
  `LifecycleInterceptor` (`[RunsBefore(LifecycleInterceptor)]`), so publication happens after
  attach/detach reconciliation, at the earliest safe point. Consume-once makes outer instances
  no-ops (F3).
- **Publish-time getter failure (derived-with-setter only): cancel the slot.** After section 5, the
  only publish path that runs user code is the derived-with-setter getter re-invocation. If it
  throws, the slot is cancelled: the consumer skips that delivery and converges on the next write.
  Never publish a value that was not observable (the setter input is not; a fabricated pair is
  not). This mirrors the codebase's actual precedent of keeping the last known value on getter
  failure (`DerivedPropertyChangeHandler.cs:76-79`, `254-258`) rather than fabricating one. The
  resulting footnote on the guarantee is removed entirely by the recorded follow-up (suppressing
  the setter-write publication).
- The `Immediate` channels keep today's dispatch path and semantics exactly. The idle fast path
  (`PropertyChangeInterceptor.cs:156-161`) is entered only when ordered delivery is also unarmed,
  so an armed write always has the publisher above the terminal.
- **The payload is built once per write and shared** between the `Immediate` dispatch and the slot
  publish. Building it twice would run the derived-with-setter getter twice, and the two channels
  could then disagree on the delivered value for the same write, violating the cross-channel
  consistency goal.

### 5. Close the dominant derived publish window: `FinalValueIsNewValue`

An internal flag on `PropertyWriteContext<TProperty>`, set only by the recalculation entry point
(`DerivedPropertyChangeHandler.cs:365` through `PropertyReferenceExtensions.cs:25-30` and
`InterceptorExecutor.cs:39-49`, all internal and already reachable). `GetFinalValue()` returns
`NewValue` when set and re-invokes the getter otherwise (derived-with-setter). Effects: the dominant
derived path stops running user code at publish time, removing its throw window and the torn old/new
pair. Cost: one internal bool and one branch.

Note for the "zero breaking changes" framing of Phase 1: this **is** an observable behavior change on
the `Immediate` channels (a derived recalculation now publishes the stabilized value rather than a
re-invoked getter's possibly-newer one). It is a bug fix, not a regression, but per the
no-untested-regressions policy it must be called out in the PR description rather than folded in
silently.

### 6. Delivery: FIFO with wait-on-pending, deliver-before-advance

The drain walks slots in reservation order. Slot order **is** commit order per subject, so there is
no reordering, no sorting, no gap detection, no baseline logic:

```csharp
while (buffer.TryPeekHead(out var slot))
{
    AwaitReleasedOrCancelled(slot);                 // bounded: the finally always resolves
    if (slot.State == Released)
        Deliver(in slot.Payload);                   // deliver BEFORE advancing: the slot
                                                    // cannot be reclaimed while the head
                                                    // has not passed it (no torn reads)
    slot.Payload = default;                         // clear on advance: a delivered slot
    buffer.AdvanceHead();                           // must not pin subjects or boxed values
}                                                   // (the #390 lesson; ChangeQueueProcessor
                                                    // clears its buffers for the same reason)
```

- Push faces (callback, observable): a **per-subscription on-demand drain**, not a dedicated task:
  a CAS gate plus a ThreadPool work item scheduled when the buffer turns non-empty and no drain is
  in flight (the `ChangeQueueProcessor._flushGate` pattern). Zero cost while idle; a slow consumer
  delays only itself.
- Pull and async-enumerable faces: no pump at all; the consumer's own thread runs the same loop
  inside `TryDequeue`/`MoveNextAsync` (the `out` copy makes deliver-before-advance automatic).
- The `in`-reference cannot escape the callback (C# ref-safety), so delivering from the live slot
  is safe; per-subscription buffers die with the subscription.
- A head slot held Pending by a write stuck in lifecycle handlers delays that subscription's later
  entries. That is what in-order delivery means; the entries are later commits.
- **The pending-head wait is event-based and cancellable** (the `PropertyChangeQueueSubscription`
  signal pattern): it honors the caller's `CancellationToken` and is released by subscription
  disposal, so `TryDequeue`, `MoveNextAsync`, and drains never hang on a producer stuck in user
  code once cancelled or disposed.
- **Consumer contract**: a callback that blocks waiting for a *later* change of its own ordered
  subscription deadlocks; dispatch is sequential by design. (A callback that writes properties,
  including of the same subject, is fine: no lock is held during delivery and reservation does not
  depend on the drain.)
- Rx is a face, not the engine: `AsObservable` adapts the ordered subscription. `ObserveOn` would
  add a second queue, and Rx has no resequencing operator, so the wait-on-pending loop is custom
  either way.

### 7. Boundary and contract scope

**Subscribe versus write**: sound by lock serialization. `Subscribe` publishes into the registry
(or the subject-side index) and returns immediately; the terminal's in-lock read is serialized per
subject, so once one write observes the subscription, all later writes to that subject do: the owed
set is a per-subject suffix. A write that commits after `Subscribe` returned performs its in-lock
read after the install is visible and is delivered.

**Attach carve-out**: registry visibility rides chain-cache invalidation, and a write racing an
attach may still execute the old chain (no registry). Contract: delivery is guaranteed for writes
committing after *both* `Subscribe` returned *and* the subject's attach completed. Writes racing an
attach or a subject-side install may be **missed, never stalled, never disordered** (FIFO waits
only on reserved slots; a missed write reserved nothing). This is the same shape as the
already-documented dormancy limit: an unattached subject delivers nothing on any channel.

**Contract scope**: all guarantees apply to **intercepted writes on generated subjects**. The
generated null-context fast path bypasses interception entirely and `_context` is published without
fences; writes racing the very first context attach may be unintercepted. Hand-written subjects
whose `Context` is not an `InterceptorExecutor` are excluded from ordered delivery (Non-goals).

**Validation**: `Subscribe(..., PropertyChangeDelivery.Ordered)` throws upfront when the subject or
context is attached and its aggregated context set has no `PropertyChangeInterceptor`, making the
misconfiguration loud at the right moment. It also throws when `subject.Context is not
InterceptorExecutor`: ordered delivery is unsupported there (Non-goals), and the per-subject gate has
no home, so allowing it would mean silent never-delivery instead of a loud failure. A per-property
`Ordered` subscribe on a subject **not yet attached to any context is allowed and dormant**
(consistent with `Immediate` subscriptions, which are context-free at install); delivery begins per
the attach carve-out above. The publisher marker (section 3) keeps every later misconfiguration safe
(teardown races, reattachment to a tracking-less context): a miss, never a stall.

**Prerequisite: the executor must be published race-free.** The generated
`IInterceptorSubject.Context => _context ??= new InterceptorExecutor(this)`
(`SubjectCodeGenerator.cs:142`, same shape in `DynamicSubject.cs:30`) is a racy lazy initializer:
two threads racing the first `Context` access each construct an executor and one store is discarded.
That is a pre-existing latent defect (any state on the discarded instance is lost), and this design
makes it a correctness hole: a pre-attach `Subscribe(Ordered)` can increment the gate on the
discarded executor while a concurrent first attach installs the surviving one, leaving an installed
listener that no write ever reserves into, **permanently**, in direct violation of the guarantee
above. The generator must therefore publish the executor with
`Interlocked.CompareExchange(ref _context, new InterceptorExecutor(this), null)` and return the
winner (the loser's instance is discarded before any state reaches it). This is a Phase 2
prerequisite and a standalone bug fix; the existing `??=` carve-out in Contract scope covers only
unintercepted writes racing the first attach, never the loss of a subscription.

### 8. Public API: a required delivery mode

```csharp
public enum PropertyChangeDelivery
{
    /// <summary>
    /// Synchronous, on the writing thread. Arrival order, which can differ from commit order under
    /// concurrent writers (compare <see cref="SubjectPropertyChange.Revision"/> to converge).
    /// Exactly-once provided lifecycle handlers and observers honor their no-throw contracts;
    /// a violation loses delivery for the remaining consumers of that write (at-most-once).
    /// <para>Performance: lowest per-write overhead, no per-change allocation. The consumer runs
    /// inline on the writing thread, so its cost is added to every write. Prefer for short
    /// non-blocking work such as setting a flag.</para>
    /// </summary>
    Immediate,

    /// <summary>
    /// Exactly-once, in commit order per subject. The order guarantee is strictly per subject:
    /// all changes of one subject (across all its properties) arrive in commit order, while
    /// changes of different subjects may interleave in any order, and their
    /// <see cref="SubjectPropertyChange.Revision"/> values are not comparable. Delivery survives
    /// throwing lifecycle handlers and throwing observers of other channels. One exception: a
    /// derived property with a setter whose getter throws at publish time skips that delivery.
    /// <para>Performance: adds a slot reservation inside the subject lock and a payload publish per
    /// committed write, allocation-free in steady state; delivery runs off the writer thread, so
    /// consumer cost leaves the write path. A change can be held briefly while a concurrent write
    /// to the same subject completes its publish.</para>
    /// </summary>
    Ordered,
}
```

Required on every entry point, so each call site states its guarantee:

```csharp
using var h1 = car.SubscribeToProperty(c => c.Speed, callback, PropertyChangeDelivery.Ordered);
using var h2 = property.Subscribe(callback, PropertyChangeDelivery.Immediate);

using var h3 = context.GetPropertyChangeObservable(PropertyChangeDelivery.Ordered)
    .Subscribe(change => ...);

using var queue = context.CreatePropertyChangeQueueSubscription(PropertyChangeDelivery.Ordered);
while (queue.TryDequeue(out var change, cancellationToken)) { ... }

await foreach (var change in context.GetPropertyChangesAsync(PropertyChangeDelivery.Ordered, ct)) { ... }
```

The callback type stays `void (in SubjectPropertyChange)` for both modes; a pumped delivery invokes
the same synchronous callback on the drain thread. `SubjectPropertyChange` gains a public
`long Revision`.

One method name covers two threading models; this is inherent (ordered delivery can never be
inline) and the enum plus documentation carry the signal. The synchronous channels' documentation
states explicitly that observers and callbacks must not throw, matching the Rx grammar and the
lifecycle-handler contract; this is what makes the conditional exactly-once claim for `Immediate`
precise.

### 9. `ChangeQueueProcessor`

- `bufferTime > 0` (dedup mode): the processor's internal subscription is **`Immediate`**. The
  queue channel must stay as cheap as possible for connector deployments (the usage profile), and
  within-flush convergence needs only the revision label: the per-property survivor keeps the
  **highest `Revision`** (the dedup dictionary is keyed by `PropertyReference`, so compared
  revisions always belong to one subject and are comparable), and the merged old value is taken
  from the **lowest** revision. The flush pass tracks both bounds alongside the survivor index (a
  widened dictionary value); still O(n). Changes carrying `Revision` 0 (hand-constructed, outside
  the terminal) fall back to positional merging. Batch emit order stays **last-occurrence arrival
  order**: cross-property commit order is not restored in this mode (a cross-subject revision sort
  would be meaningless).

  **Scope of the fix, stated honestly: within-flush only.** Dedup is scoped to one `_flushChanges`
  batch (`ChangeQueueProcessor.cs:243-256`), so a commit-order inversion that straddles a flush tick
  survives it: writer A commits revision 5 and is preempted before its post-commit enqueue, writer B
  commits revision 6 and enqueues, the tick emits 6, then A enqueues and the next tick emits 5,
  leaving the external system stale until the next real write. This is strictly better than master
  (which inverts within a batch too, the common burst case) but it is **not** full convergence. Full
  convergence needs a persistent per-property last-emitted-revision filter, whose state must be
  evicted when subjects detach (a `PropertyReference`-keyed map holds subjects alive), so it is
  recorded as a follow-up rather than smuggled into this design. Deployments that need exactly-once
  commit order today use `bufferTime = 0` with `Ordered`.
- `bufferTime = 0`: the processor subscribes **`Ordered`** and becomes an exactly-once,
  commit-order forwarder. This arms the context: every write in it pays the reservation path. The
  trade is explicit and belongs to deployments that choose zero buffering.
- No constructor signature change.

## Core footprint

Everything added to `Namotion.Interceptor` is internal; core's public API snapshot stays
byte-identical. Core holds mechanism, never policy: the same division of labor as `SyncRoot`, the
write-timestamp slot, `FinalizeOrigin`, and #383's `Terminal` threading.

| Addition to core | What it is | Why core |
|---|---|---|
| `InterceptorExecutor.Revision` | one `long`, incremented in the terminal | must happen inside `SyncRoot` |
| `InterceptorExecutor.OrderedPropertyListenerCount` | one `int` gate, `Interlocked`-maintained by Tracking (increment before install, decrement after removal, trailing fence) | read in-lock by the terminal |
| Race-free executor publication | generator emits `Interlocked.CompareExchange` instead of `??=` | prerequisite for the gate's home to be stable; standalone bug fix |
| `PropertyWriteContext` fields | revision stamp, publisher marker, record ref, `FinalValueIsNewValue` | the per-call vehicle between layers |
| Ordered registry | volatile subscription array per context | read in-lock; captured at chain build |
| Slot buffer + reservation record | generic `SlotBuffer<TPayload>` (Tracking instantiates with `SubjectPropertyChange`; core reserves headers only) and the pooled tracker | `ReserveSlot` runs in-lock |
| Terminal + chain-build code | ~15-30 lines | this is the commit section |
| `InternalsVisibleTo(Tracking)` | one attribute | Tracking arms and consumes the mechanism |

Untouched core is not achievable: the design rests on capturing order inside the commit's critical
section, which core owns, and the Tracking-only alternative (post-commit resolution) is proven
unsound, not merely inconvenient (see Findings). The mechanism is inert without Tracking: marker
unset, the terminal's addition collapses to the revision increment and a false branch, which is
what benchmark gate 1 protects.

## Guarantee matrix

The deliverable for `docs/tracking.md`.

| Channel | Delivery | Exactly-once | Order | Consumer runs on | Per-write cost |
|---|---|---|---|---|---|
| Per-property callback | `Immediate` | conditional (a) | arrival | writer thread | lowest, no allocation |
| Per-property callback | `Ordered` | yes (b) | commit, per subject | drain thread | gated lookup + slot reserve + publish |
| Observable | `Immediate` | conditional (a) | arrival | writer thread | lowest |
| Observable | `Ordered` | yes (b) | commit, per subject | drain thread | slot reserve + publish |
| Pull queue | `Immediate` | conditional (a) | arrival | consumer thread | post-commit enqueue |
| Pull queue | `Ordered` | yes (b) | commit, per subject | consumer thread | slot reserve + publish |
| `ChangeQueueProcessor`, buffer = 0 | `Ordered` | yes (b) | commit | processor thread | as pull queue |
| `ChangeQueueProcessor`, buffer > 0 | dedup (`Immediate`-fed) | no, latest-state-wins | arrival, of survivors; per-property newest **within a flush** (c) | processor thread | plus dedup pass per flush |

(a) Exactly-once provided lifecycle handlers and observers honor their no-throw contracts, and
except (b).
(b) A derived-with-setter property whose getter throws at publish time skips that delivery on all
channels (the getter re-invocation is load-bearing there; a fabricated value is never published).
Removed entirely by the recorded setter-write-suppression follow-up.
(c) Dedup is per flush batch, so an inversion straddling a flush tick still emits stale-last. Better
than master, not full convergence; see section 9 and the recorded follow-up.

All guarantees scoped to intercepted writes on generated subjects (section 7). "Commit, per
subject" is strict: one subject's changes arrive complete and in commit order, different subjects'
changes may interleave arbitrarily, and their revisions are not comparable. Every entry in the
last column stays qualitative until measured (see benchmark plan).

## Limits, stated explicitly

**Ordered delivery and the label are per subject, not global.** Different subjects hold different
`SyncRoot` locks, so their slot reservations into one subscription can interleave in either order,
and revisions of different subjects are not comparable. Within one subject, ordering holds across
all its properties, and per-property order follows as a subsequence. Cross-subject causal order is
not provided; it would require a process-wide serialization point on every write. If ever needed, a
`WithGlobalChangeOrdering()` opt-in can add a cross-subject label without disturbing this design.

**`Immediate` guarantees are conditional on consumer contracts** (matrix note a). The library does
not add per-observer `try/catch` to the hot path: even with it, the guarantee would remain
conditional on user code in new ways.

**Ordered mode trades memory for liveness.** Unbounded slot buffers by default, matching today's
raw queue. A slow consumer grows the buffer rather than dropping (incompatible with exactly-once)
or blocking the writer. A blocking bound is a later knob.

**A producer preempted between reservation and publish briefly holds back that subscription's
head.** Latency, not corruption; bounded by the producer's unwind (including its lifecycle
handlers).

**A stuck producer parks the drain thread.** A push-face drain blocked on a Pending head holds its
ThreadPool thread for the producer's entire unwind; a hanging lifecycle handler holds it
unboundedly, and many ordered subscriptions make this a ThreadPool starvation vector. The pull
faces block only their own consumer thread, and every wait is cancellable (section 6).

## Decided

1. **Ordering core: in-lock slot reservation + `finally` publish.** The alternative family
   (pre-chain claims plus install quiescence) was fully designed and rejected; an earlier no-hook
   revision (post-commit resolution + contiguous-prefix pump) was found unsound (see Findings).
2. **Context-wide subscription state lives in chain-closure registries** (plural: all registries
   in the aggregated context set are captured; rides existing chain-cache invalidation, no
   executor-side install/evict protocol, no lifecycle dependency); **per-property ordered
   subscriptions are indexed subject-side** in a core-owned parallel of the #377 layout, gated by
   a per-subject count on the executor, so cost scales with the written subject's and property's
   subscribers (usage-profile requirement).
3. **Publisher marker**: reservation is gated on `PropertyChangeInterceptor` having set a per-write
   context flag before `next()`, making "reserved but no resolver on the stack" unrepresentable;
   plus a `Subscribe`-time validation throw for contexts without the interceptor.
4. **Pooled reservation record** rented outside the lock, progress-count tracked, consume-once in
   the `finally` (also the aggregation publish dedup), returned to the pool.
5. **Deliver-before-advance and clear-on-advance** in the drain (no torn reads on recycled slots,
   no pinned references).
6. **Publish-time getter failure cancels the slot** (never publish a fabricated value); footnote
   (b) until the setter-write-suppression follow-up lands.
7. **Pump model: per-subscription on-demand drain** (CAS gate + ThreadPool work item), pull faces
   pumpless.
8. **Release timing: 0.9.0 publishes on its own schedule; this work does not gate it.** The
   required-enum change breaks the 0.8.0-era APIs and the #377 subscription API one release after
   it ships; accepted, the fix is mechanical.
9. **Transactions: resolved by code reading** (see Findings).
10. **Fallback values**: recalculation-path derived publishes use `NewValue` via
    `FinalValueIsNewValue` (it is the stabilized value); no `Faulted` flag anywhere.
11. **`ChangeQueueProcessor` modes**: `bufferTime > 0` rides an `Immediate` subscription plus
    revision-oriented dedup (arrival-order survivors, per-property latest-state; keeps connector
    contexts unarmed and the queue channel cheap); `bufferTime = 0` rides `Ordered` and arms the
    context, an explicit trade for zero-buffer deployments.

## Delivery structure: three stacked PRs

Superseded plan, kept for context: this section originally described two PRs and argued that the
first must not merge before the second validated it. Both parts changed once the work was reviewed.

The label and its consumer were split apart, because six independent review dimensions found no
correctness or thread-safety defect in the revision mechanism and every defect they did find in the
connector layer consuming it. The consumer went through three retention designs and two write-back
attempts; the label went through none. Holding the stable half hostage to the volatile one bought
nothing.

- **PR 1 (#399, additive plus one declared binary break):** per-subject `Revision` in both
  terminals, `SubjectPropertyChange.Revision`, `FinalValueIsNewValue`, the compare-and-swap executor
  publication, the `docs/tracking.md` matrix, benchmark gate 1.
- **PR 2 (#420, additive):** the `ChangeQueueProcessor` merge fix, cross-flush convergence, the
  transaction write-back, `docs/connectors.md`, the CI path filters.
- **PR 3 (#422, breaking):** the ordered channel (marker, registries, per-property index,
  reservation record, slot buffers, publish, drain, required enum, `bufferTime = 0` riding
  `Ordered`), remaining gates.

**PR 1 may merge before the later two.** The original argument was that Phase 2's benchmarks are the
first measurement of Phase 1's always-on cost under load, so a regression could force Phase 1
decisions to change: where the revision counter lives, whether the context stamp is needed, or the
struct-size trade. That still holds, but all three of those live behind `internal` members
(`InterceptorExecutor.Revision`, and `Revision`/`Executor`/`FinalValueIsNewValue` on
`PropertyWriteContext`), so correcting them later is a follow-up rather than a public break. Gate 1
measured clean in isolation, and this repository accepts justified breaking changes with approval,
so even a public correction is a decision rather than a wall.

The residual risk, named so it is not discovered later: the executor threaded through
`PropertyWriteContext` is the load-bearing internal decision, it is what makes the increment a plain
field increment under a lock the caller already holds, and it is already in tension with the
`TODO: Get rid of the executor completely` on `IInterceptorExecutor`. If the in-context gates argue
against it, that correction touches the always-on write path.

`bufferTime = 0` riding `Ordered` makes PR 2's supersession gate on the immediate path dead code,
since an ordered channel cannot deliver an older commit after a newer one. PR 3 deletes it
deliberately rather than leaving it.

### `SubjectPropertyChange.Create` and revision propagation

`Create` has roughly 100 call sites across production and tests, and is the only overload today
(`SubjectPropertyChange.cs:43-49`). It gains an **optional** `long revision = 0`: one method, no
call-site churn, and 0 means exactly "constructed outside a terminal write", which the dedup already
falls back to positional merging for.

This is source-compatible but **binary breaking** (a consumer compiled against the six-parameter
method would get `MissingMethodException`). Accepted deliberately: the library ships a source
generator, so consumers recompile against every new version regardless, which makes binary
compatibility far less load-bearing here than a clean single-method surface. The alternative
considered and rejected was a seven-parameter overload with the six-parameter one forwarding: binary
safe, but it permanently doubles a public API for a constraint this project does not operate under.
`PublicApiGenerator` captures optional-parameter defaults, so the snapshot test surfaces the change
either way. Phase 1 is therefore "additive plus one declared binary break", not literally
break-free; the PR description must say so.

Revision must also propagate through every derived-instance path, none of which the private
constructor knows about today:

- the private constructor (`SubjectPropertyChange.cs:14-32`) gains the field;
- `MergeWithNewer` (`:193-204`) carries the **newer** change's revision, since it is called inside
  the dedup being fixed (`ChangeQueueProcessor.cs:254`); a merged survivor defaulting to 0 would
  degrade every three-occurrence property to positional comparison mid-batch;
- `WithOrigin` (`:211-219`) preserves it, or every confirmed transaction snapshot
  (`SourceTransactionWriter.cs:306,325,394`) loses its revision.

## Plan-level verification items

- Chain-build snapshot consistency: registry and `PropertyChangeInterceptor` must come from one
  consistent service snapshot under concurrent context mutation (the existing interceptor chain has
  the same exposure; verify the locking covers the registry).
- `OnContextChanged`/chain-rebuild fires on every attach flow, including the
  `new Subject(context)` constructor path.
- #344 history: confirm no suppression mechanism exists in the enqueue path beyond consumer-side
  origin filtering.
- Subject-side ordered-listener install visibility (`ConcurrentDictionary` add versus in-lock
  read): confirm monotone visibility, mirroring the #377 Dekker analysis.
- Shared-payload optimization: with N matching subscriptions the payload struct is copied into N
  slots; a shared payload table may win for large N. Measure.
- Slot buffer segment size, growth policy, and disposal; drain state eviction verified by test.
  Per-property subscriptions are the many-instances case (usage profile: 100+ per app), so their
  initial segment must be small (4-8 slots, grow on demand) to keep idle memory per subscription
  in the low hundreds of bytes rather than kilobytes.
- Document the drain concurrency contract: callbacks are serial within one subscription and may
  run in parallel across subscriptions.
- Pending-head wait mechanism: event-based, cancellable, released on disposal, allocation profile.
- Chain-build capture of ordered registries: `GetWriteInterceptorFunction` today fetches only
  `IWriteInterceptor` (`InterceptorSubjectContext.cs:226-237`); the registry fetch must come from the
  same consistent snapshot. Also cover the `_noServicesSingleFallbackContext` delegation path
  (`InterceptorSubjectContext.cs:183-195`), where the chain and its closure live on the shared parent
  context and the first-attach fast path skips `OnContextChanged` entirely (`:96-99`): the "invalidated
  on every attach" claim is literally false there, and it happens to work only because the parent's
  chain already carries the registry. Verify, do not assume.
- `record.Track(subscription, slot)` cannot store a `ref` to a struct slot: it stores
  `(subscription, slotIndex)` and publish/cancel re-address the slot. The pseudo-code shape is
  illustrative, not literal.
- The generic slot buffer needs a **non-generic header surface**: the terminal is generic over
  `TProperty`, not over the payload, so core reserves header segments (revision, state) through a
  non-generic base and Tracking's derived type owns the `SubjectPropertyChange` payload segments.
- Home for the once-built shared payload (`Immediate` dispatch plus slot publish): the pooled
  reservation record is the natural armed-only home; a field on `PropertyWriteContext<TProperty>`
  would grow the struct on the unarmed path, which gate 9 must then cover. Today's per-instance build
  under aggregation (`PropertyChangeInterceptor.cs:185`) is the retained unarmed baseline.
- The reservation record's tracking list can grow inside the lock when one write matches many
  subscriptions: same amortized class as slot segments, and covered by the same caveat.

## Benchmark plan (gate, not garnish)

Everything quantitative in this document is mechanism-derived reasoning and must be measured before
merge. This codebase has been burned exactly here: #383's apparent "+3% write regression" was JIT
code-placement luck, and #388 made `--memoryRandomization` the default for that reason. Method:
`pwsh scripts/benchmark.ps1 -Stash`, CPU pinned at a fixed frequency, allocation columns treated as
first-class results (priority order per AGENTS.md).

1. **Unarmed regression gate** (the make-or-break number): `RegistryBenchmark` `Write`, `WriteNoOp`,
   `WriteWithTimestampScope`, `Read` with zero ordered subscriptions. Measures the full always-on
   set: `subject.Context` dispatch, `isinst`, revision increment, context stamp, marker check.
   Acceptance: no measurable time or allocation regression; if it regresses, the design changes.
2. **Armed steady-state allocation gate**: `Ordered` counterparts of every existing
   `PropertyChangeSubscriptionsBenchmark` `Write*` variant. Acceptance: 0 B steady state on
   primitive-valued writes (including record pool traffic and slot clearing), matching today's
   queue channel; time within a stated envelope of the `Immediate` counterparts.
3. **Many-per-property-subscriptions scenario** (usage-profile gate): e.g. 100 ordered per-property
   subscriptions across a graph; measure writes to observed and unobserved properties. Acceptance:
   writes to subjects without ordered subscriptions pay only the per-subject count load; writes to
   an observed subject's unobserved properties pay one indexed lookup miss; observed properties pay
   only their own subscribers.
4. **In-lock cost isolation**: armed single-writer benchmark isolating lock-hold extension
   (cursor bump + field writes) against the unarmed baseline.
5. **Contention matrix**: multi-writer benchmarks over {same subject, distinct subjects} x
   {unarmed, one ordered subscription, four ordered subscriptions}. Verifies no cross-subject
   contention when unarmed and characterizes cursor contention when armed.
6. **Delivery latency and throughput**: commit-to-consumer latency distribution and sustained
   throughput for `Ordered` push and pull faces under single and concurrent writers, including the
   held-head case.
7. **Pump overhead**: on-demand drain scheduling cost under bursty writes versus sustained load.
8. **`ChangeQueueProcessor` dedup**: flush-pass cost with revision comparison versus positional, at
   realistic batch sizes.
9. **Struct-size check**: `SubjectPropertyChange` +8 bytes (`Revision`) across the copy-heavy paths
   #389 measured, plus `PropertyWriteContext` growth (record reference, marker, flags) on the
   unarmed path.
10. **Transactions**: `SubjectTransactionBenchmark` `CommitChanges` (Local / SingleSource /
    MultiSource), unarmed and armed.

Results are recorded in the PR with raw BenchmarkDotNet output, and the guarantee matrix's cost
column is updated from measurements before `docs/tracking.md` ships.

## Testing

- Ordering under concurrent writers: many threads writing one subject, assert per-subject commit
  order (by revision) and every committed write delivered exactly once on `Ordered`.
- Boundary: subscriptions installed concurrently with write storms; assert no stall ever, every
  write committed after `Subscribe` returned (and attach completed) is delivered, and delivered
  prefixes are gap-free per subject.
- Attach carve-out: subjects attached mid-storm; racing writes may miss but never stall or
  disorder.
- Publisher marker: a chain without `PropertyChangeInterceptor` and an armed subject-side
  subscription; assert miss-not-stall; `Subscribe` validation throws on such contexts.
- Publish guarantee: a throwing lifecycle handler; assert `Ordered` consumers receive the change,
  the drain does not stall, and the exception propagates unchanged.
- Publish-time getter throw (derived-with-setter): slot cancelled, consumer skips, next write
  converges, no stall.
- Atomicity: fault injection between reservations; assert the tracked prefix is cancelled and
  nothing was committed.
- Aggregation: exactly one reservation set AND exactly one publish per write at aggregation depth
  >= 2 (consume-once).
- Torn-read stress: concurrent producers recycling slots while a consumer delivers; assert payload
  integrity (deliver-before-advance) and cleared slots after advance (no pinned references,
  `WeakReference` assertions in the #390 style).
- `FinalValueIsNewValue`: recalculation publishes the stabilized value without re-invoking the
  getter (counting getter); derived-with-setter still re-invokes.
- Veto and no-op writes: no slot reserved, no revision-related machinery invoked.
- Transactions: capture reserves nothing; commit replay delivers in replay order; origin-based echo
  filtering still suppresses self-echoes in `ChangeQueueProcessor`.
- `ChangeQueueProcessor` dedup with deliberately inverted enqueue order: highest `Revision` wins
  and the merged old value comes from the lowest revision. Fails on current code.
- Subscribe and dispose races against in-flight writes, both modes; drain state eviction on
  subscription disposal and subject detach (no leak).
- Record-pool balance at aggregation depth >= 2 (rent idempotence: exactly one rent and one return
  per write; catches the double-rent leak the reservation/publish counts alone cannot see).
- Two aggregated contexts each with their own ordered registry: reservations land in both.
- Cancellation and disposal during a pending-head wait unblock `TryDequeue`, `MoveNextAsync`, and
  drains.
- Cross-channel value agreement for derived-with-setter on one write (payload built once).
- Drain behavior under a producer stuck in a lifecycle handler: thread parked, released on
  resolve, cancellable throughout.
- Pre-attach ordered per-property subscribe: dormant, delivers after attach to a tracking context,
  validation throws when the attached context set lacks the interceptor.
- Public API snapshot updated: enum, required parameters, `SubjectPropertyChange.Revision`.

## Follow-ups, tracked separately

- Derived-with-setter: suppress the setter-write publication and let the recalculation publish
  alone. Removes footnote (b) everywhere and deletes the last publish-time user-code path; changes
  observable notification count and needs an origin-semantics pass (the suppressed setter-write may
  carry `FromSource`; the surviving recalc publishes `Local`, which matches what sources receive
  today).
- `WithGlobalChangeOrdering()` opt-in if a cross-subject label is ever needed.
- Blocking-bound backpressure knob for `Ordered` subscriptions.
- **Cross-flush convergence for `ChangeQueueProcessor` dedup mode**: a persistent per-property
  last-emitted-revision filter, so an inversion straddling a flush tick cannot emit stale-last. Needs
  an eviction story, since a `PropertyReference`-keyed map holds subjects alive; candidate homes are
  the subject's own `Data` (dies with the subject) or lifecycle-driven eviction. Completes the
  convergence contract that section 9 currently scopes to within-flush.
- **Extract the exclusive-drain primitive.** #381's `PathDeliveryQueue` independently implements the
  same pattern as section 6 (single-drainer flag, FIFO backlog, callbacks outside the lock,
  zero-copy uncontended fast path, nested writes handed to the active drainer). The convergence
  validates the design; the duplication should be lifted into one shared primitive used by both.
- `SubscribeToPath` (#381) does **not** simplify by migrating to `Ordered`: its queue orders
  composite events across multiple subjects, which is out of scope here, and per-hop `Ordered`
  subscriptions would give it N independent drains to re-serialize. Delayed walks could also
  coalesce transitions, weakening its one-event-per-transition contract. Matches the analysis in
  #385. Available later as an opt-in mode purely to move the path walk off the writer thread.
- Use #381's `PathBenchmarkModels` / `SubjectPathSubscriptionBenchmark` shape as the basis for
  benchmark gate 3 (a path is N per-property subscriptions, so 100 paths is the many-subscriptions
  profile with an existing baseline), and do one throwaway `Ordered` migration as a stress test of
  drain-per-subscription at hundreds of subscriptions (empirical answer to the ThreadPool
  starvation limit).
- `ChangeQueueProcessor.bufferTime` is a hidden mode switch (interval, dedup on/off, and after this
  design the delivery guarantee: 0 = exactly-once commit order, > 0 = latest-state-wins). Follow up
  with an explicit mode in the API (for example `LatestState(interval)` / `EveryChange()`) instead
  of a magic zero; a pure rename such as `deduplicationTime` would still hide the guarantee switch
  and misname the batching half. Interacts with #353's proposed configuration object.
