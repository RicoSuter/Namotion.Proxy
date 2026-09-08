# Phase 2 ordered delivery: chain-build findings

Investigation output of Task 2 of `2026-07-30-ordered-delivery-phase2.md`. No production code changed.

**Method:** code reading of `src/Namotion.Interceptor/InterceptorSubjectContext.cs`,
`Cache/WriteInterceptorFactory.cs`, `Interceptors/InterceptorExecutor.cs`,
`src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs` and
`Change/PropertyChangeInterceptor.cs`, plus a throwaway probe program (outside the repository) that
exercised the same paths through the public API and read the private cache fields by reflection.
Every "observed" line below is probe output, not inference.

---

## Q1. Can the ordered registry be fetched from the same consistent service snapshot as the interceptors, and under which lock?

**Evidence**

- `InterceptorSubjectContext.cs:226-237` (`GetWriteInterceptorFunction`) holds **no lock at all**. It
  reads `_writeInterceptorFunction` (`:228`), calls `GetServices<IWriteInterceptor>()` (`:233`),
  builds the chain (`:234`) and then `TryAdd`s it (`:235`).
- The only lock in the resolve path is inside `GetServices<T>` (`:55-61`): the
  `ConcurrentDictionary.GetOrAdd` factory takes `_lock` and computes
  `GetServicesWithoutCache<T>()` under it. That lock is held across the recursion into fallback
  contexts (`:280`, `:290-327`), so one `GetServices<T>` call is internally consistent.
- Two `GetServices<T>` calls are two separate `_lock` acquisitions with a gap between them. Nothing
  in the current code closes that gap.
- A combined fetch is mechanically possible: `GetServicesWithoutCache<T>()` (`:262-288`) is a private
  instance method with no per-call state beyond the `[ThreadStatic] _serviceQueryVisited` set, which
  it clears in a `finally` (`:284-287`), so two sequential calls under one `lock (_lock)` are safe.
  Nesting them would not be (they share that set), but the terminal only needs them sequentially.

**Conclusion: NO as written today, YES if changed.** The two fetches are not in one critical
section, and `_lock` on this context is anyway only atomic against local mutations. A change
originating in a *fallback* context clears this context's caches from `OnContextChanged` (`:350-353`)
without holding this context's `_lock`, so no lock on this context can serialize against it.

**Recommended modification (removes the question rather than answering it).** Do not query the
registry as a second service at all. Have `PropertyChangeInterceptor` **own** its registry and expose
it through a core-owned interface, then have `WriteInterceptorFactory<TProperty>.Create` derive the
registry array from the `interceptors` array it already receives
(`Cache/WriteInterceptorFactory.cs:9`):

```csharp
// core: internal interface IOrderedSubscriptionSource { OrderedSubscriptionRegistry OrderedRegistry { get; } }
// factory: registries = interceptors.OfType<IOrderedSubscriptionSource>().Select(s => s.OrderedRegistry)
```

This makes the pair atomic by construction (one query, one snapshot), makes the dangerous state
"`PropertyChangeInterceptor` in the chain but its registry missing from the closure" unrepresentable,
and removes `InterceptorSubjectContext.cs` from Task 6's file list entirely. It also answers Q4 for
free: one registry per aggregated interceptor instance, in chain order.

---

## Q2. Does a `Subscribe` on the parent's registry reach every delegating subject?

**Evidence**

- `ExecuteInterceptedWrite` (`:183-195`) forwards to `_noServicesSingleFallbackContext` (`:185-190`)
  before touching `EnsureInitialized`/`GetWriteInterceptorFunction`, so a delegating executor never
  builds or caches a chain. The forward is recursive, so a chain of delegating contexts collapses to
  the first non-delegating one.
- `GetServices<T>` delegates the same way (`:46-50`), so a delegating context resolves the identical
  service instances the delegation target does.
- `ContextInheritanceHandler.cs:21` attaches a child subject's executor to the **parent subject's
  executor**, not to the configured root, so real graphs are executor chains that terminate at the
  configured context.
- Observed on a `WithFullPropertyTracking` root with `parent.Child = child`:
  - parent executor delegates to root: `True`
  - child executor delegates to parent executor: `True`
  - compiled write chains: root `2`, parent executor never initialized, child executor never
    initialized
  - `child.Context.GetServices<PropertyChangeInterceptor>()[0]` is reference-equal to the root's
    instance: `True`
- A context that does have its own services builds its own chain, but `GetServicesWithoutCache`
  (`:290-327`) walks into the fallbacks and returns the fallback's **instances**, so the closure
  captures the same registry object either way.

**Conclusion: YES.** The chain and its closure live on the shared delegation target, and every
delegating subject executes that one chain. A `Subscribe` that mutates the array inside the
registry object is visible to all of them with no chain rebuild and no per-subject install. This is
exactly how `PropertyChangeInterceptor` already works today: its `volatile DispatchState?
_dispatchState` (`Change/PropertyChangeInterceptor.cs:29,52-57`) is swapped on subscribe while the
interceptor instance stays baked into the chain. The registry is the same pattern, so it is
precedented rather than novel.

Caveat carried into Q5: this only holds while the delegation target's chain is current.

---

## Q3. Is the registry registered at configuration time, so a chain built before any subscribe still carries it? (the load-bearing question)

**Evidence**

- `Tracking/InterceptorSubjectContextExtensions.cs:86-90`: `WithPropertyChangeSubscriptions()` calls
  `context.TryAddService(() => new PropertyChangeInterceptor(), _ => true)` **eagerly**. There is no
  lazy path, no first-subscribe creation, and no lifecycle hook involved.
- `InterceptorSubjectContext.cs:133-146` (`TryAddService`) and `:148-155` (`AddService`) construct
  and store the instance immediately under `_lock`. The sibling `WithService` helpers
  (`InterceptorSubjectContextExtensions.cs:16-20,31-36`) funnel into the same call, so every `With*`
  extension in the tracking file registers eagerly.
- Observed: on a bare `InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions()` with
  zero subjects and zero subscribers, `GetServices<PropertyChangeInterceptor>().Length == 1`
  immediately. Same for a plain marker service through `WithService`.
- The `AddFallbackContext` fast path (`:96-99`) does skip `OnContextChanged`, but its guard requires
  `_serviceCache is null`, and `_serviceCache` and `_writeInterceptorFunction` are created together
  in `EnsureInitialized` (`:67-83`). A context that has never initialized has no cached chain, so
  there is nothing stale to invalidate on **that** context.

**Conclusion: YES, and it is not a blocker.** A registry created by
`WithPropertyChangeSubscriptions()` exists from the moment the context is configured, long before
any chain is built, so a chain built before the first `Subscribe` still closes over the registry
object and a later `Subscribe` is a pure array swap inside it. The design's expected answer holds.

Two conditions that must be preserved when Task 5 writes the registry:

1. The registry must be created by the `With*` extension, not by `OrderedSubscription`'s
   constructor or by a lazy property on the interceptor. Lazily creating it on first subscribe would
   reintroduce exactly the failure this question was asked about.
2. If the registry is registered as a separate service rather than owned by the interceptor
   (see Q1), the two `TryAddService` calls are two separate lock acquisitions. Register the
   **registry first** so that any torn snapshot is "registry without interceptor" (a harmless miss)
   rather than "interceptor without registry" (a permanent silent loss of ordered delivery).

---

## Q4. Under aggregation, one registry instance per subscribing context, or a deduplicated set?

**Evidence**

- `GetServicesWithoutCache(type, visited)` (`:321-324`) concatenates local services with the
  fallbacks' results and applies `Distinct()`. `Distinct()` on `object` uses the default comparer,
  which for a class without an `Equals` override is reference equality. It deduplicates *the same
  instance reached twice*, not *different instances of the same type*.
- `TryAddService(factory, _ => true)` (`:133-146`, via `WithPropertyChangeSubscriptions`) checks the
  **aggregated** set, so configuring a context that already has a configured fallback adds nothing.
  Independently configured contexts joined afterwards each keep their instance.
- Observed:
  - two contexts each configured with `WithPropertyChangeSubscriptions()`, then joined:
    `2` instances
  - a context configured **after** its fallback was attached: `1` instance
  - diamond (`g -> h -> j`, `g -> i -> j`, service on `j` only): `1` instance, so the same instance
    reached by two paths is deduplicated
  - two contexts each with their own plain marker object, joined: `2` instances

**Conclusion: one instance per registering context, NOT deduplicated by type.** The terminal must
loop over an array of registries. A nested loop is correct without any dedup pass, because a given
`OrderedSubscription` is added to exactly one registry, so no subscription can be reserved twice by
one write. That is why `ArePropertyObserversResolved` (`Interceptors/IWriteInterceptor.cs:46`,
`Change/PropertyChangeInterceptor.cs:264-280`) is needed for the *per-property* index (one shared
subject-side structure that every aggregated instance would otherwise resolve) but is **not** needed
for the registries.

Worth knowing for test design: the multi-registry case is currently unreachable through the public
subscription API. `TryGetService<T>` throws when more than one instance exists (`:157-166`), and both
`GetPropertyChangeObservable` and `CreatePropertyChangeQueueSubscription` go through
`GetService<PropertyChangeInterceptor>()` (`Tracking/InterceptorSubjectContextExtensions.cs:115,137`).
Observed on the two-instance context: both throw
`InvalidOperationException: There must be exactly one service of type ... PropertyChangeInterceptor`.
So in practice the loop runs over 0 or 1 registries today, and the spec's test item "two aggregated
contexts each with their own ordered registry: reservations land in both" cannot be written against
the public API without first relaxing that constraint.

---

## Q5. Counter-examples to the closure-capture approach

Two were found. Both are pre-existing defects in the current code rather than things ordered delivery
introduces, but both bear directly on the design.

### 5a. The `AddFallbackContext` fast path can leave a chain permanently stale (confirmed)

The spec says registry visibility "rides chain-cache invalidation" and that a write "racing an
attach" may miss. The word *racing* overstates the guarantee: the miss can be **permanent**, not
transient.

`AddFallbackContext` (`:85-109`) takes the fast path at `:96-99` when the context being extended has
no caches, no services and this is its first fallback, and in that case does not call
`OnContextChanged` at all. `OnContextChanged` (`:343-388`) is also the only thing that propagates
invalidation upward to `_usedByContexts` (`:377-387`). So if some other context is already using this
one as a fallback and has already cached a chain, that chain is never invalidated.

Observed, using only public API:

```
A = Create().WithService(() => new CountingInterceptor("own"));
B = Create();                       // fresh, never used
A.AddFallbackContext(B);            // A has services -> no fast path for A; registers A in B._usedByContexts
person = new Person(A); person.FirstName = "x";   // builds and caches A's chain
C = Create().WithService(() => new CountingInterceptor("late"));
B.AddFallbackContext(C);            // B is fresh -> FAST PATH -> OnContextChanged skipped
```

- `A` chain entries after the attach: `1` (unchanged, not invalidated)
- `A` cached `IWriteInterceptor` array after the attach: `1`, should be `2`
- the late interceptor invoked by a post-attach write: `False`
- `A.GetServices<CountingInterceptor>()`: `2` (a service type never queried before recomputes
  correctly, which is what makes this so easy to miss)

With a real tracking context in place of `C` the symptom is the intended one for this design:

- `A.GetServices<PropertyChangeInterceptor>()`: `1` (resolvable)
- change events delivered by a write after the attach and after `Subscribe`: **`0`**

That is "delivery silently never happens", indefinitely, for a subject whose attach completed and
whose `Subscribe` returned. It breaks the letter of the spec's attach carve-out.

Reachability is narrow but real. It needs a plain `InterceptorSubjectContext` (not an executor) as
the user, because `InterceptorExecutor.AddFallbackContext` (`Interceptors/InterceptorExecutor.cs:84-98`)
calls `context.GetServices<ILifecycleInterceptor>()` on the fallback at `:89`, which runs
`EnsureInitialized` on it and therefore disarms the fast path. Observed: a fresh context's
`_serviceCache` goes from uninitialized to initialized across an executor attach. Context-to-context
composition is a supported public pattern and is used in the test suite
(`Tracking.Tests/Change/WritePipelineOrderTests.cs:64`,
`Tracking.Tests/Change/PerPropertySubscriptionLifecycleTests.cs:115,169,455`).

**Impact on the design:** the ordered failure mode here is a permanent **miss**, not a stall. The
chain that is stale lacks `PropertyChangeInterceptor` entirely, so `PublisherPresent` is never set,
so the terminal reserves nothing and no drain can be left waiting on a pending head. That is the
correct degradation, and it is the same outcome immediate delivery already has today. It does not
invalidate closure capture. It does mean the spec's carve-out wording must be widened, or the hole
closed.

**Options (human decision, no code changed here):**

1. Widen the documented carve-out from "a write racing an attach" to "a context that gains its first
   fallback after another context has already cached a chain over it". Zero code, honest, leaves a
   silent-failure trap in place.
2. Close it: also require `_usedByContexts.Count == 0` for the fast path at `:96-99`. Two lines, no
   hot-path cost (the fast path is a construction-time branch), and it makes "invalidated on every
   attach" true. This is the recommended option.
3. Drop the fast path. Not recommended, it exists for construction throughput.

### 5b. Lock-order inversion between a fallback service change and a concurrent chain build (confirmed deadlock)

Not specific to ordered delivery, but it constrains what Task 6 may do and it is the reason the
closure-capture design is right.

- Writer direction: `GetServices<T>` takes **this** context's `_lock` in the `GetOrAdd` factory
  (`:57`) and holds it across the recursion into fallbacks, where `GetServicesWithoutCache(type,
  visited)` takes the **fallback's** `_lock` (`:301`). Order: user then fallback.
- Invalidation direction: `AddService` takes the **fallback's** `_lock` (`:150`) and calls
  `OnContextChanged()` inside it (`:153`); the propagation to `_usedByContexts` (`:377-387`) then
  takes the **user's** `_lock` (`:357`). Order: fallback then user.

The class comment at `:12` documents `_lock -> UsedByContextsLock` but says nothing about ordering
between two contexts' `_lock`s, and these two paths take them in opposite orders.

Reproduced from the public API (`fallback.AddService(x)` concurrent with property writes on a subject
whose context uses that fallback), hanging on attempt 2 of 200. Managed stacks from the live process:

```
Thread A (writer)
  InterceptorSubjectContext.GetServicesWithoutCache(Type, HashSet)   <- blocked on fallback._lock
  ...
  InterceptorSubjectContext.GetServices()
  InterceptorSubjectContext.GetWriteInterceptorFunction()
  InterceptorSubjectContext.ExecuteInterceptedWrite(...)
  Person.set_FirstName(String)

Thread B (registrar)
  InterceptorSubjectContext.OnContextChanged(HashSet)                <- blocked on parent._lock
  InterceptorSubjectContext.OnContextChanged(HashSet)
  InterceptorSubjectContext.OnContextChanged()
  InterceptorSubjectContext.AddService(...)
```

**Impact on the design:** it is a strong argument *for* closure capture. Any design where `Subscribe`
mutates the service set (and therefore calls `AddService`/`OnContextChanged` to force a chain
rebuild) would run this deadlock every time a subscription is created while another thread is
writing. The chosen design has `Subscribe` touch only a volatile array inside an already-registered
object, taking no context lock, so it never enters this cycle. Task 6 must preserve that: **do not
add any path where `Subscribe` triggers a chain rebuild.**

It also argues against Q1's "fetch the registry in the same critical section" if that is implemented
by holding `_lock` longer, since it widens this window. The Q1 recommendation (derive registries from
the already-fetched interceptor array) adds no lock time at all.

Reporting this separately is worthwhile regardless of Phase 2, since it is a live deadlock on master.

### 5c. Chain cache `TryAdd`-after-`Clear` (structural, not reproduced)

`GetWriteInterceptorFunction` (`:226-237`) computes the chain outside any lock and then `TryAdd`s it
(`:235`). `OnContextChanged` clears `_writeInterceptorFunction` (`:352`). A concurrent invalidation
landing between the compute and the `TryAdd` leaves a stale chain cached permanently. The same shape
exists in `GetServices`'s `GetOrAdd` (`:55-61`), whose factory result is inserted after `_lock` is
released. Not reproduced in 400 attempts; the window is narrow, and reading the code shows it is
real. Pre-existing, applies equally to interceptors and registries, and would only ever cause a miss
(a chain without `PropertyChangeInterceptor` sets no marker), never a stall.

### 5d. Non-counter-examples checked and cleared

- `InternalsVisibleTo` from core to Tracking exists (`Namotion.Interceptor.csproj:17`), so
  Tracking can construct the core-owned registry and buffer types. Not a blocker.
- Both terminals in `Cache/WriteInterceptorFactory.cs` are currently `static` lambdas (`:13`, `:40`).
  Capturing registries makes them closures. Cost is one display-class allocation per
  `(context, TProperty)` chain build (rare) plus one field load per write on the armed path. Covered
  by the plan's benchmark gate 1, but note that the change is not free even when unarmed.
- The zero-interceptor terminal (`:11-35`) can never reserve: `PublisherPresent` is set by
  `PropertyChangeInterceptor`, which is an `IWriteInterceptor`, so `interceptors.Length == 0` implies
  the marker is never set. Task 6's step 5 says "in each of the two lock bodies"; the block in the
  zero-interceptor terminal would be unreachable code. Task 6's first test
  (`WhenMarkerAbsent_ThenNoSlotIsReserved`) uses a bare context and therefore passes trivially
  against that terminal; it should use a context **with** an interceptor but without the marker to
  actually exercise the gate.
- `_serviceQueryVisited` (`:20,277-287`) is a `[ThreadStatic]` cleared in a `finally`. Two sequential
  `GetServicesWithoutCache<T>()` calls under one lock are safe; nesting them would not be. Only
  relevant if Q1's combined-fetch variant is chosen over the recommended one.

---

## Overall verdict

**Viable with two stated modifications.** The load-bearing assumption (Q3) holds: the registry would
exist from configuration time, so the closure-capture approach does not depend on chain invalidation
to become armed, and `Subscribe` correctly needs no rebuild. Q2 confirms the delegation path makes
one registry reach every delegating subject. Q4 confirms the terminal needs a plain nested loop with
no dedup.

Modifications required before Task 6 is written:

1. **Derive the registries from the interceptor array, not from a second service query** (Q1). One
   snapshot, no tearing, no lock held longer, no `InterceptorSubjectContext.cs` change. Without this,
   "interceptor armed but registry missing from the closure" is a representable state whose symptom
   is exactly the silent non-delivery this task was asked to rule out.
2. **Decide the fast-path hole** (Q5a): widen the spec's attach carve-out, or add
   `_usedByContexts.Count == 0` to the guard at `:96-99`. Recommended: close it.

Constraint Task 6 must respect: `Subscribe` must never trigger a chain rebuild, because the rebuild
path deadlocks against a concurrent service change in a fallback context (Q5b).

Separately, Q5b is a live deadlock on master reachable from the public API and deserves its own
issue independent of Phase 2.
