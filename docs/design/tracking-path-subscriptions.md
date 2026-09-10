# Path Subscriptions: Internal Design

This document describes the internal architecture, concurrency model, and correctness guarantees of the path subscription feature (`SubscribeToPath`) in `Namotion.Interceptor.Tracking`. For user-facing documentation, see the [Tracking](../tracking.md) documentation.

## Overview

A path subscription observes "the value at `x.Engine.PrimarySensor.Temperature`" as the path itself changes over time: `Engine` may be null at first and appear later, `PrimarySensor` may be replaced with a different subject, and the leaf value changes. The subscriber reads the current observed state on demand via `Current` and receives one event per observable transition, each carrying the real property write that caused it.

The core idea is a **chain of per-property subscriptions, one per segment**. The feature composes the merged per-property primitive (`PropertyChangeInterceptor` plus `PropertyChangeSubscription`): it decomposes a chained expression into an ordered segment list and installs one primitive listener per resolved segment on the subject that segment reads on. When a segment fires, the tracker retracks only the suffix below the change. Steady-state cost is O(path depth) per delivery, with rebuild work only on structural changes, which are rare relative to leaf writes.

This is chosen over the obvious alternative of subscribing to the context-wide change firehose and re-resolving the path on every change anywhere: that costs O(all writes) per watcher, exactly the overhead the per-property primitive was built to eliminate, and is strictly worse than the O(depth) segment chain. The motivating consumers (UI bindings, automation rules, connector mappings) care about a location in the graph, not a fixed instance, so without this feature they would either pay the firehose cost or hand-roll fragile re-subscription logic.

Two facts make the chain complete without any Registry or lifecycle coupling:

- The library has no in-place tracked collections. Collections change only by property reassignment, so every structural change (including "items moved in a collection") is a property write on a segment-holding property. Per-property listeners on the segments therefore capture all structural change.
- Paths are expression-based, not string-based. This buys compile-time type safety, typed indices, a typed leaf value, and zero coupling to the connector `IPathProvider` machinery.

The feature adds no core API and references no `Namotion.Interceptor.Registry` type. It lives in `Namotion.Interceptor.Tracking/Paths/` (namespace `Namotion.Interceptor.Tracking.Paths`), next to but separate from `Change/`.

Vocabulary: "resolved" means every intermediate segment is non-null and every index is in range, so the leaf property exists; its value may itself be null. "Attach/detach" is deliberately not used for path state, because those words already mean graph membership (lifecycle). The two concepts diverge: a subject moved elsewhere in the graph stays attached while the watched path becomes unresolved.

## Components

All types live in `Namotion.Interceptor.Tracking.Paths`.

| Type | File | Role |
|------|------|------|
| `SubjectPathSubscriptionExtensions` | `SubjectPathSubscriptionExtensions.cs` | Public entry points: the two `SubscribeToPath` overloads (callback and observer), each taking a required `SubjectPathValidation` mode. Enforces null-argument and transaction guards, decomposes, and constructs the tracker. |
| `PathExpressionDecomposer` | `PathExpressionDecomposer.cs` | Internal. Decomposes the lambda into an ordered `PathSegment[]` and enforces the static tier of validation (shape defects that can never become valid throw here). Evaluates index and key arguments exactly once. |
| `PathSegment` / `PathSegmentKind` | `PathSegment.cs` | Internal. One decomposed node: a property name plus an optional fixed collection index or dictionary key, and the static-type metadata that drives accessor construction. Kind is `Property`, `CollectionIndex`, or `DictionaryKey`. |
| `PathWalker` | `PathWalker.cs` | Internal. The runtime tier of validation: walks the segments against a concrete graph, resolves the leaf value, and records the subject each segment reads on. Never throws; any failure resolves the whole path to unresolved. |
| `PathValueAccessors` | `PathValueAccessors.cs` | Internal. Compiled typed accessors (leaf getter, `ImmutableArray<T>` indexer, dictionary lookup) cached per declaring type and property, plus the lenient reference-collection indexer. Keeps the walk allocation-free. |
| `SubjectPathSubscription<TValue>` | `SubjectPathSubscription.cs` | Public sealed `IDisposable`. The live tracker: holds the segment chain, computes events, runs the exclusive-drain delivery, and exposes `Current`. |
| `SubjectPathValue<TValue>` | `SubjectPathValue.cs` | Public readonly struct. One observed state: `IsResolved`, `Value`, `TryGetValue`, `GetValueOrDefault`. Internal `AreEquivalent` is the suppression comparison. |
| `SubjectPathChange<TValue>` | `SubjectPathChange.cs` | Public readonly struct. One delivered transition: `Kind`, `OldState`, `NewState`, `Cause`. |
| `SubjectPathChangeKind` | `SubjectPathChangeKind.cs` | Public enum: `ValueChange`, `PathChange`. |
| `SubjectPathValidation` | `SubjectPathValidation.cs` | Public enum: `Full`, `LeafOnly`. The required per-subscription choice of how much of the path a leaf write revalidates (see the cost model). |
| `SubjectPathChangeCallback<TValue>` / `ISubjectPathChangeObserver<TValue>` | `SubjectPathChangeCallback.cs` | Public delegate and zero-closure observer interface; both receive the change by `in` reference. |

## Mechanism

### Expression decomposition

`PathExpressionDecomposer.Decompose` walks the lambda body from the outermost member down to the parameter, collecting segments leaf-first, then reverses so the root is first and the leaf is last. It:

- Unwraps only the compiler's sanctioned leaf `Convert` (boxing or reference widening, recognized by the target type being assignable from the operand type). Any other convert is a user cast and is rejected.
- Accepts property access (`MemberExpression` on a `PropertyInfo`), single-dimensional array index (`ArrayIndex` binary node), and `get_Item` calls (list and dictionary). `IndexExpression` never appears in compiler-generated lambdas.
- Evaluates each index or key argument exactly once, now, via `Expression.Lambda(argument).Compile().DynamicInvoke()`. `[3]` is a fixed position; `[i]` does not re-read `i` later. An argument that references the lambda parameter is rejected (it cannot be evaluated at subscribe time).
- Marks the first-collected segment `IsLeaf` only when it is a property. A path ending in an index yields no leaf and is rejected.
- Step 7: every intermediate (non-leaf) segment must resolve to a subject reference type (`IsSubjectReferenceType()`), computed from the property static type, the collection element type, or the dictionary value type. This is what makes the accessor `TypeAs` on a value-typed element unreachable (see Validation boundary).

Each `PathSegment` carries the property name, the kind, the fixed index or key, and the static-type metadata the accessors need (for a value-typed collection, `CollectionElementType` is resolved once here so the walk does not recompute it).

### The validating walk

`PathWalker.Walk` is the single traversal primitive, used by both `Current` and event computation. It clears the caller's `resolvedSubjects` buffer, sets `[0] = root`, and walks the segments inside one never-throw `try/catch`. For each segment it:

1. Bails to unresolved if the current subject is null.
2. Bails to unresolved if the property is missing (`Properties.TryGetValue`) or fails `IsIntercepted || IsDerived`.
3. For a leaf, reads through `ReadLeaf`.
4. For an intermediate, resolves the next subject through `ResolveChild`; a null child is unresolved. Otherwise it records the child in `resolvedSubjects[i + 1]` and descends.

There is no recover-and-continue path: any exception from a getter, an accessor invoke, or a container operation lands in the outer catch and resolves the whole path to unresolved. The already-resolved prefix stays recorded; entries past it stay null so a caller never observes a stale subject from a prior walk. This is the **resolve-to-unresolved boundary**: no failure ever propagates into a writer's chain or out of `Current`.

`ResolveChild` dispatches on segment kind: a property reads `metadata.GetValue` and casts to `IInterceptorSubject`; a reference-typed collection reads the boxed collection then calls the lenient `IndexReferenceCollection` (negative or out-of-range index, a non-subject slot, and a boxed default `ImmutableArray<T>` all return null); a value-typed collection (`ImmutableArray<T>`) uses the compiled indexer; a dictionary uses the compiled `TryGetValue` lookup.

### Chain build (subscribe-before-read)

`BuildFrom(startPosition, startSubject)` installs the chain under the lock. For each segment it creates a `PathSegmentObserver` recording its position, records the subject the segment reads on, and installs the primitive listener **before** reading the value that resolves the next subject:

```
subscribe segment p on subject  →  read the value to resolve subject p+1  →  subscribe segment p+1  →  ...
```

Installation uses the internal non-validating `PropertyChangeSubscription.Create` (not the public `Subscribe`/`SubscribeToProperty`, which validate and throw), and non-throwing name resolution (`TryResolveChild`: `Properties.TryGetValue` plus the `IsIntercepted || IsDerived` check, with `ResolveChild` wrapped in a local `try/catch`). The build stops at the first unresolved intermediate, leaving the suffix null.

Subscribe-before-read is load-bearing for the missed-write guarantee. A write racing a just-subscribed segment either commits before the tracker's read (the read sees the new value and the walk continues on the true graph) or commits after the install (the primitive's post-commit listener resolution then guarantees dispatch, which serializes on the tracker lock into a fresh retrack). The reverse order would leave a window where a replacement of an already-read segment goes undispatched. The same discipline applies to the initial subscribe-time build, not only retracks.

A third prerequisite comes from the primitive's interceptor ordering (`PropertyChangeInterceptor` runs `[RunsBefore(typeof(LifecycleInterceptor))]`): dispatch runs after lifecycle reconciliation, so any subject the retrack resolves was already attached and context-inherited. A freshly assigned child is notifying before the suffix is subscribed and read, so a write to it from the subscription's own callback enters interception normally.

### Event computation: divergence and suffix retrack

Every processed callback revalidates via a fresh walk, with one exception: under `SubjectPathValidation.LeafOnly`, a callback fired by the resolved leaf's own listener re-reads only the cached leaf (see the leaf-write validation modes note in the cost model). In the full-walk case, `ProcessSegmentCallback` walks from the root into `_scratch`, then finds the **divergence point**: the first position where the fresh walk read on a different subject (reference identity) than the subscribed chain (`_resolvedSubjects`). This one comparison covers every structural case; an intact chain never diverges.

On divergence, the tracker retracks: `DisposeSuffix(divergencePoint)` tears down the stale listeners, `BuildFrom(divergencePoint, divergentSubject)` reinstalls the subscribe-before-read chain (a null divergent subject is a break, so dispose only), then it **re-walks** so `NewState` comes from the retrack's post-install reads. This matters for convergence: a write landing between the initial walk's read and the install can dispatch to nobody, so sourcing `NewState` from the pre-retrack walk would omit it with no later callback guaranteed. In the intact-chain case the walk's values are safe as `NewState` because every segment is already subscribed. This from-root revalidation is what lets a callback from a subject that silently left the path heal the chain instead of delivering off-path state; the slot-identity guard alone cannot catch that, because a never-rebuilt chain's stale observer *is* the current observer for its position.

`Kind` is `ValueChange` only when the chain did not diverge, the result is resolved, and the triggering write's property is the current resolved leaf (subject reference and name match). Everything else, including a leaf write whose revalidating walk fails above the leaf, is `PathChange`.

An event is suppressed when the new observed state `AreEquivalent` to `_lastObserved` (same resolvedness, and when both resolved, values equal under `EqualityComparer<TValue>.Default`). `_lastObserved` is seeded from the subscribe-time walk (so the first delivered event's `OldState` reflects the state at subscribe and no false unresolved-to-resolved edge fires) and advances **before** the callback runs, so the chained-transition invariant survives a throwing callback and event N+1's `OldState` equals event N's `NewState`. A suppressed transition advances the baseline too: a retrack to a distinct-but-`Equals`-equal leaf instance (only reachable for a value-equality `TValue`) must not leave the baseline pinning the departed instance or surface it later as a stale `OldState`. The transition stays unobservable because the equivalence is value-based, so the advanced baseline is value-equal to the old one.

### Cost model and the per-segment accessor cache

Delivery is allocation-free for inline value types and strings, including a path with an `ImmutableArray<T>` intermediate. Three techniques achieve this:

- A single reusable per-subscription scratch buffer (`_scratch`) for every under-lock walk (the ctor seed, each event walk, each retrack re-walk). These run under `_lock` and never nest, so one buffer is safe.
- `Current` cannot share `_scratch` (it is lock-free and concurrent), so it rents a throwaway buffer from `ArrayPool` and returns it cleared.
- Typed compiled accessors: a leaf `Func<IInterceptorSubject, TValue>` reads a value-typed leaf without boxing (the metadata getter returns `object`); a value-typed intermediate uses a read-and-index delegate (`Func<IInterceptorSubject, int, IInterceptorSubject?>`) that reads the typed `ImmutableArray<T>` struct and indexes it internally, returning only the reference-typed child, so the collection struct is never boxed; the collection element type is cached on the segment. All accessors are cached per declaring type, property name, and (for the leaf) `TValue`.

The one boxing exception is a non-`IEquatable<T>` struct leaf, which boxes both operands in the `EqualityComparer<TValue>.Default` suppression comparison.

**Leaf-write validation modes.** How much of the path a leaf write revalidates is a required per-subscription choice, the `SubjectPathValidation` parameter of `SubscribeToPath`, stored on the tracker and applied in `TryComputeEvent`. Structural (upper-segment) callbacks always run the full from-root walk, divergence check, and retrack in both modes; the modes differ only on a callback fired by the resolved leaf's own listener:

- `Full`: a leaf write runs the same from-root validating walk as a structural write, because the walk both recomputes `NewState` (a fresh re-read, not the write payload) and revalidates the chain against a silently departed subject. The per-segment accessor cache makes this cheap, but it still visits every segment. A divergence created while a segment was dormant is healed on the next delivered callback of any kind.
- `LeafOnly`: a leaf write re-reads only the leaf through the cached leaf binding (`PathSubscriptionChain.ReadResolvedLeaf`, called under the lock, sharing the walk's never-throw contract) and skips the walk, the divergence check, and the retrack. This is sound for an intact chain, whose upper segments cannot have changed without their own listeners firing, and it drops the per-delivery cost from O(depth) to O(1) on leaf-write-heavy paths. The trade is dormant-divergence healing on leaf writes: a divergence created while a segment was dormant is not healed by a leaf write, only by a later structural callback, so a stale off-path leaf can keep delivering until then. This is a deliberate, bounded consistency carve-out chosen per subscription (see the dormant-divergence carve-out). The kind is `ValueChange` when the re-read resolves, otherwise `PathChange`; the shared computation tail (suppression, the `_lastObserved` advance before the callback, and delivery through the drain queue) is identical in both modes, and `Current` is always a fresh full walk regardless of the mode.

**Per-segment accessor cache.** Each position caches a `ResolvedPathSegment<TValue>`: the immutable `(subject, accessor delegate)` binding for the subject that segment currently reads on, stored in the `_resolvedSegments` array alongside the listener handles. When the cached subject still reference-equals the walked subject, the steady-state walk invokes the cached accessor directly, skipping the per-walk `Properties.TryGetValue` and the accessor-cache lookup; it falls back to fresh by-name resolution only on a subject mismatch, which is exactly the divergence signal. The cache stores only the binding, never a resolved child or leaf value, so a `Property` intermediate keeps the boxed `metadata.GetValue` accessor and every walk still re-reads the live child, leaving divergence detection unchanged. Entries are written under `_lock` during the chain build and retrack, cleared with the same suffix as their listener, and read by the lock-free `Current` through `Volatile`; the entry is immutable, so a stale read simply misses and falls back to by-name. Accessor construction and invocation go through the shared `PathWalker.BuildLeafAccessor`/`BuildChildAccessor`/`ReadLeaf`/`ResolveChild` helpers, so the cached path and the by-name slow path resolve identically. The cache cuts per-delivery and `Current`-read CPU; delivery stays allocation-free, while the rare structural retrack and subscribe/dispose paths allocate the small `ResolvedPathSegment` objects.

## Concurrency Model

- **One `System.Threading.Lock` per subscription** (`_lock`). All mutable chain state (`_segmentHandles`, `_segmentObservers`, `_resolvedSubjects`, `_scratch`, `_lastObserved`, `_pending`, `_draining`, `_deferredRevalidation`) is guarded by it. Every walk during event computation and every retrack read happens under the lock (precedent: `LifecycleInterceptor` invokes getters under its own lock). Getters must therefore be side-effect free, the same expectation the lifecycle and derived machinery already place on them.
- **Callbacks always run outside the lock.** Event computation (walk, retrack, kind, suppression, `_lastObserved` advance, enqueue decision) happens under the lock; delivery happens after the lock is released. Running user code under the lock is rejected: with one lock per subscription, a callback on path A that writes a segment of path B while B's callback writes a segment of A would be a textbook lock-ordering deadlock.
- **Exclusive-drain queue.** At most one thread is the drainer (`_draining`). A computing thread that finds no active drainer claims it and delivers; every other computed event enqueues into `_pending` (a plain `Queue<T>` guarded by the lock, steady-state allocation-free) and the current drainer delivers it FIFO. This flattens nested and concurrent writes into one totally ordered stream.
- **Direct-dispatch fast path.** When `_pending` is empty and no later pass or backlog exists, the single event is held in a stack local and delivered directly, skipping the enqueue and dequeue copies of the large change struct. The drainer re-acquires the lock afterward to drain anything that raced in and to clear the flag.
- **Throw-reset.** A throwing callback abandons the drain. The catch resets `_draining` to false (leaving `_pending` intact) and rethrows, so the next computed event can claim the drainer and deliver the stranded backlog. Without the reset `_draining` would stay true forever and every later event would strand permanently.
- **Thread-affine reentrancy guard.** At computation entry the tracker checks `_lock.IsHeldByCurrentThread`. If this thread already holds the lock, a getter invoked by the outer walk wrote a watched segment and re-entered synchronously; the tracker sets `_deferredRevalidation` and returns without computing. The outer computation loops (`do { ... } while (_deferredRevalidation)`) and recomputes a fresh event until no deferral remains, so a side-effecting getter never deadlocks nor runs a callback under the lock. The check is a thread-local read, free on the common path. A suppressed event still claims the drainer and drains any existing backlog, so events stranded by an earlier throwing callback are not left waiting for the next non-suppressed write.
- **Lock-free `Current`.** `Current` reads only the immutable `_root` and `_segments`, a rented buffer, and the volatile `_disposed` flag. It takes no tracker lock, so a UI render or debugger watch never triggers subscription churn or lock contention. It neither retracks nor advances `_lastObserved`; healing is the event path's job.
- **Slot-identity guard.** Each `PathSegmentObserver` records its position. A callback is processed only when the firing observer is still the current observer recorded for that position (`_segmentObservers[observer.Position] == observer`). This rejects a bounded trailing invocation from a torn-down listener. A chain-wide generation counter would be wrong: suffix rebuilds do not recreate upper-segment observers, so surviving segments would carry old generations and their next legitimate callback would be ignored; it also misbehaves when the same subject and property appear at two positions of a cyclic path. Slot identity handles both.

## Concurrency Scenarios

### Reentrant getter-write during a walk

A getter invoked by the event walk writes a watched segment of the same subscription and dispatches synchronously on the same thread. The thread-affine guard (`IsHeldByCurrentThread`) records a deferred revalidation and returns; the outer loop recomputes after the current walk finishes. No callback runs under the lock, ordering stays total, and there is no AB-BA deadlock. The guard does not preserve the reentrant write's position, so under `SubjectPathValidation.LeafOnly` a deferred re-pass cannot assume the write was a leaf write: only the first pass may take the leaf-only fast path, and every deferred pass runs the full from-root walk, so a structural change a leaf getter makes is still detected and retracked rather than stranding the chain on the departed branch. Termination assumes convergence; a getter that writes a fresh, non-suppressed value on every walk is a caller contract violation, not a supported case.

### Cross-path callbacks

Subscription A's callback writes a segment of path B while B's callback writes a segment of A, concurrently. Because callbacks run outside the per-subscription lock, no lock-ordering cycle forms and there is no deadlock.

### The missed-write window

A structural write races a retrack between a suffix segment's subscribe and its read. Two interleavings: (a) the writer's listener lookup runs after the install, closed by tracker-lock serialization (the dispatch serializes into a fresh retrack); (b) the writer passes the primitive's pre-commit idle gate before the install and commits after the tracker's read, closed by the primitive's post-commit listener resolution behind a full fence. The retrack's own post-install reads supply `NewState`, so no write is lost.

### Cyclic path

For a path like `x => x.Child.Child.Name` on a self-referencing graph, the same subject and property appear at two positions, so the subject holds two listeners. One write dispatches to the listener snapshot in install order; the upper segment's observer fires first and its retrack disposes the lower segment's subscription, whose in-snapshot dispatch is then skipped by the primitive's cleared-observer guard. The path-side slot-identity guard is the concurrency backstop for a late cross-thread stale invocation. Delivery is exactly once per write, and the chain invariant holds after the retrack.

### Concurrent dispose racing a direct-dispatch callback

`Dispose` on one thread while a write drives a direct-dispatch callback on another. `Dispose` and the drainer coordinate under the lock: `Dispose` sets `_disposed`, tears down all segments, and clears `_pending`; the drain loop re-checks `_disposed` under the lock and starts no new delivery once disposal is observed. A callback already dispatched outside the lock may run to completion using its stack-local event, and the slot-identity guard keeps a stale callback from corrupting a rebuilt chain. No `ObjectDisposedException` or torn state escapes. `Dispose` does not block for in-flight callbacks (a blocking handshake would reintroduce deadlock risk).

## Dispose contract

`Dispose` is one-shot and idempotent. Under the lock it checks `_disposed` (double dispose is a no-op), sets it, calls `DisposeSuffix(0)` to tear down every installed segment listener (returning the process-wide subscription count to zero), and clears `_pending` so queued-but-undelivered events are dropped. It also clears the reusable scratch buffer, resets `_lastObserved`, and nulls the graph root and the observer (via `Volatile.Write`), so a retained disposed handle pins neither the graph nor the callback closure. Root, the decomposed segment array (whose fixed dictionary-key or index arguments may be arbitrary objects, up to the root itself), and observer are released the same way the per-property primitive releases its subject and observer: each is nulled via `Volatile.Write`, and the lock-free `Current` walk and an in-flight drain read them into a local first, so a null seen there resolves to unresolved (or drops delivery) and an already-claimed callback still runs to completion. The segment array is swapped for null rather than mutated in place, so a racing walk that captured it sees a fully valid array; the cached segment count keeps `Current`'s buffer sizing valid afterward. It never throws. After it returns, `Current` reads the volatile flag and returns unresolved rather than throwing into a racing reader. Self-dispose from inside a callback is supported: the drain loop observes `_disposed` and stops delivering further queued events. The drain loop's post-dispose check bounds delivery to at most the one callback already dispatched outside the lock (zero if dispose nulled the observer before the drainer read it).

Teardown on throw during the build: the constructor's `BuildFrom` runs inside a `try`; on a reserved-key contract violation (`PropertyChangeSubscription.Create` raising `InvalidOperationException` for a foreign `ni.pcl` value) the catch calls `DisposeSuffix(0)` to dispose the segments already installed, rolling back the process-wide count before the exception propagates, so a rejected subscribe never permanently degrades the idle write fast path and no partial subscription escapes. Name resolution during the build is non-throwing and the seed walk never throws, so this is the only build-time throw path.

## Transactions

- **Subscribe guard.** `SubscribeToPath` throws `InvalidOperationException` when `SubjectTransaction.Current is { IsCommitting: false }`, that is, an active not-yet-committing transaction is on the current flow. Because the tracker builds its chain by walking intercepted getters, and reads on a transaction-holding flow return staged read-your-writes values, subscribing there would install listeners on speculative subjects a rollback would strand.
- **Event walk suppresses the ambient transaction.** Event computation normally runs at commit replay (reading the being-committed values), but two narrow edges can run it on an open transaction's flow: a real `[Derived]`-with-setter write (which bypasses staging and dispatches immediately) or a cross-context write, made on a transaction-holding flow. On such a flow an unsuppressed walk would read staged values, see them differ from the subscribed chain, and persistently retrack onto a speculative subject, disposing the committed suffix's listeners; a rollback would then strand the subscription with no callback owed. To prevent this, `ProcessSegmentCallback` checks `HasActiveTransaction` and, when set, calls `SubjectTransaction.SetCurrent(null)` for the whole computation, reading the committed graph. It restores the caller's transaction in the `finally`, before the drain section runs callbacks (so callbacks still observe the caller's transaction) and even if a retrack throws. Cost is a single AsyncLocal round-trip, paid only when a transaction is active during dispatch.
- **`Current` is unconstrained.** It never throws and creates no persistent state; read inside a transaction flow it returns that transaction's read-your-writes view, which self-corrects on the next callback.
- **Composition edge.** A path callback that throws during commit replay is caught by the transaction apply loop (`SubjectPropertyChangeOperations.TryApplyLocalChange`) and surfaced as a `SubjectTransactionException` rather than propagated to the writer; under Rollback the affected change is reported not-applied and is not reverted although the model keeps its value.

## Validation boundary (two tiers)

Validation is split so that shape defects fail loud and runtime-validity defects fail soft:

- **Static tier (throws at subscribe).** `PathExpressionDecomposer` throws `ArgumentException` for shapes that can never become valid: casts, method calls other than a single-argument `get_Item`, multi-argument or multi-dimensional indexers, fields, captured-object chains (`m => other.Speed`), static members, the identity path (`x => x`), a path ending in an indexed element, an index argument that references the lambda parameter, a `get_Item` on a non-subject-collection non-dictionary property, a nested indexer whose receiver is another indexer, a negative collection index, a dictionary key that evaluates to null, and a non-subject intermediate (step 7). The extension methods additionally throw `ArgumentNullException` for a null subject, expression, callback, or observer, and the transaction guard throws `InvalidOperationException`.
- **Runtime tier (resolves unresolved, never throws).** `PathWalker` treats every runtime-validity defect leniently, whether reachable at subscribe time or only on a later walk (a different runtime type after a heal or `new`-hiding, a dynamic subject): a segment property missing on the runtime type, a segment failing `IsIntercepted || IsDerived` (including a plain, non-derived interface default, which the generator emits non-intercepted), a getter or container operation that throws, and a leaf whose runtime value is not assignable to `TValue`. Runtime validity is per runtime type and not permanent, so it is never a subscribe-time error. A `[Derived]` leaf or a `[Derived]` intermediate that selects a child subject is subscribable and carries the derived-change-detection prerequisite.

Note on the accessor `TypeAs`: `BuildImmutableArrayIndexer` and `BuildDictionaryLookup` emit `Expression.TypeAs(..., typeof(IInterceptorSubject))` on the element or value. `TypeAs` on a non-nullable value type would throw at expression-build time, but that path is unreachable: step-7 static validation rejects any non-subject intermediate before subscribe, so the element and value types are always subject reference types here.

## Correctness guarantees and their limits

Under the resolve-to-unresolved boundary and the subscribe-before-read chain, the subscription provides:

- **Missed-write closure.** Any write that commits after a segment's install is delivered (subscribe-before-read plus the primitive's post-commit resolution), provided the interceptor chain returns normally after the commit and no earlier synchronous observer of the same write throws.
- **Quiescent consistency (narrowly scoped).** Once observed writes settle, the last event's `NewState` and `Current` agree with the real graph.
- **`Current` always accurate.** Computed fresh on every read, independent of the subscription chain, even while events are dormant.
- **Never-throws delivery.** No walk failure ever escapes into a writer's chain or out of `Current`.
- **Chained transitions.** Event N+1's `OldState` equals event N's `NewState` (the `_lastObserved` advance runs before the callback).
- **Total ordering** per subscription via the drain queue; cross-subscription ordering is not guaranteed.
- **Allocation-free delivery** for inline value types and strings (see the cost model), except a non-`IEquatable<T>` struct leaf.

Quiescent consistency lags in exactly **five carve-outs**. In all five `Current` stays accurate and the event stream heals on the next delivered callback (or dispose and re-subscribe); reading `Current` does not heal the subscription.

1. **Throwing callback.** It abandons the drain; events it stranded are delivered late on the next write, or not at all if no further write occurs.
2. **Dormant divergence.** A structural change made while a segment's subject is dormant is not observed when it happens; the chain re-syncs only on the next callback delivered to any still-subscribed segment. No callback is guaranteed in the meantime. Under `SubjectPathValidation.Full` every processed callback revalidates via the fresh walk, so any delivered callback heals; under `SubjectPathValidation.LeafOnly` a leaf callback skips the walk, so only a structural callback heals and a stale off-path leaf can keep delivering until one arrives.
3. **Foreign throwing observer.** An earlier synchronous observer of the same write throws and aborts dispatch before the segment callback runs (per-property listeners dispatch after each interceptor's queue and Rx channels), so a committed write's callback never fires. This is another consumer violating the must-not-throw contract; isolating each observer in the primitive was deferred to avoid swallowing its fail-fast signal.
4. **Getter or container throw during the walk.** A getter, or a container `Count`, indexer, `TryGetValue`, or comparer, that throws publishes unresolved and recovers only on a later delivered segment callback, not on the mere clearing of the failing condition, because the tracker subscribes to path segments and not to whatever off-path or container state the traversal reads.
5. **`Equals`-suppressed derived-intermediate recompute.** A derived subject-valued intermediate recomputes to a distinct but `Equals`-equal instance; the equality handler suppresses the synthetic write, so no segment callback fires, yet the getter now returns a new instance with its own subtree. The chain stays on the old subtree until the next non-suppressed segment callback retracks it. (Replacing an intermediate with an `Equals`-equal instance through an ordinary intercepted property is not a divergence at all: the equality handler drops the terminal field write, so chain, graph, and `Current` all keep agreeing on the old instance.)

## Lock ordering, dormancy, and derived-intermediate notes

- **Context-inheritance prerequisite.** A segment fires only while its subject is attached to a context with a `PropertyChangeInterceptor`. Multi-segment paths that dynamically introduce context-free children require context inheritance. `WithPropertyChangeSubscriptions()` alone registers only the interceptor; without `ContextInheritanceHandler` (registered by `WithContextInheritance()`, bundled by `WithFullPropertyTracking()`), a context-free child assigned into the graph never inherits the root's context, so its writes bypass interception: segment 0 fires, deeper segments through that child are silently inert, `Current` still works, and nothing errors. A child that carries its own notifying context works without inheritance. This is a user-controlled dormancy edge for the primitive; for paths, whose purpose is observing subjects that appear dynamically, it is a prerequisite.
- **Dormancy.** Subscribing before the root is first attached is valid; the subscription is dormant until attach. Assigning a previously detached subject into a watched path is not a dormancy case, because dispatch-after-lifecycle ordering means the subject is attached and notifying before the structural callback and retrack run. Dormancy covers writes that happen while a subject is genuinely outside every notifying context.
- **Derived-intermediate exclusive-hold caveat.** A child reachable only through a `[Derived]` intermediate (held by no intercepted property anywhere) is not attached at graph attach, because `LifecycleInterceptor` discovers children only through intercepted properties and derived properties are emitted non-intercepted. Its segment subscriptions are installed but stay dormant until the derived property first recalculates to an `Equals`-distinct value through the write chain (which triggers the attach). A stable or equal-recomputing derived intermediate leaves the suffix inert. A derived intermediate that aliases a child already held by an intercepted property (for example `Best => Candidates[0]`) is attached via that property and is unaffected.
- **Process-wide count.** Each segment subscription increments the primitive's process-wide subscription count; only `Dispose` decrements. A path subscription dropped without disposal permanently degrades the idle write fast path by its chain depth, the primitive's forgotten-handler cost multiplied across segments.

## Future extensions

These are intentionally not built yet:

- **Prefix sharing**: many subscriptions under a common prefix each hold their own segment chain; a shared prefix tree is a pure optimization to add once real workloads show it is needed.
- **String-path subscriptions**: compose this primitive at the Registry or connector level with `IPathProvider` parsing for externally supplied paths (OPC UA node addresses, MQTT topics).
- **Dynamic (runtime-named) segments and late-added-property discovery** (issue #387): the ordered segment list is already the internal contract, decoupled from the expression decomposer, so a dynamic or string-path front-end can reuse the subscribe-before-read, retrack, and by-name resolution machinery.
- **Cast support in selectors** (`x => ((Car)x.Vehicle).Speed`).
- **Paths ending in an indexed element** (`x => x.Items[3]` with no trailing property).
- **In-place collection mutation tracking** (`INotifyCollectionChanged`); today all collections change by reassignment, which the design relies on.
- **Asynchronous delivery**; consumers hand off to their own `Channel`, as with the primitive.
- **`Refresh()`** on the handle: an explicit re-sync hook for the dormancy edge and for tests.

Leaf-only validation on value events is implemented as the per-subscription `SubjectPathValidation.LeafOnly` mode (see the cost model). If the validating walk ever becomes a bottleneck beyond that at realistic depths, one further option remains, not used here: amortized validation (validate one ancestor link per leaf event, round-robin, bounding off-path deliveries after a dormant divergence to at most depth events).
