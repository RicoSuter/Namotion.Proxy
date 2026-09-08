# Initialize subjects once, not once per attach

Status: approved, not yet implemented
Issue: #430
Supersedes the approach in PR #429
Targets `master`. PR #419 strengthens this design but is not a prerequisite.

## Problem

Three extension points add properties to a subject while it attaches:

| Extension point | Interface | Example |
|---|---|---|
| property initializer | `ISubjectPropertyInitializer` | `UnitAttribute` adds a `Unit` attribute |
| property lifecycle handler | `IPropertyLifecycleHandler` | `PropertyAttributeInitializer` adds `State` and `Configuration` |
| subject lifecycle handler | `ILifecycleHandler` | `MethodPropertyInitializer` adds a property per `[Operation]` method |

All three re-run whenever a subject re-attaches, which an ordinary move between parents triggers
as soon as the old reference is removed before the new one is added. Their effect does not re-run
with them: the properties they added live on `subject.Properties` and survive detach. The second
run therefore adds a property that is already there, and `RegisteredSubject.AddProperty` throws.

### The re-run is redundant, not merely unsafe

`RegisteredSubject`'s constructor rebuilds its property set from `subject.Properties`
(`RegisteredSubject.cs:143`), which still holds everything the previous run added. On re-attach
the entire prior effect is already present before any initializer runs.

`LifecycleInterceptor.AttachToContext` snapshots `subject.Properties.Keys` before invoking
handlers, and on re-attach that snapshot already contains the dynamic properties. So the second
run does not repeat the first. It runs every initializer over a strictly larger property set,
including the properties the initializers themselves created.

### The documented workaround makes it worse

Guarding each implementation with a check before adding is what the docs currently teach.
Measured, it converts a loud failure into a silent one:

```
initializer invocations:                    2
capture#1 Parent == live registration:      False
capture#1 parent still in KnownSubjects:    False
capture#1 sees marker:                      True
```

An initializer may capture the `RegisteredSubjectProperty` it was handed. `TryGetAttribute`
resolves through `Parent.TryGetPropertyAttribute(...)` (`RegisteredSubjectProperty.cs:301`), and
`Parent` is the `RegisteredSubject` captured at the time. `SubjectRegistry` builds a new one on
every re-attach, so a guarded initializer keeps its first-run attribute alive with a getter that
answers from a discarded registration instead of throwing.

This is not hypothetical. A downstream initializer creates a mutable cell in a closure and wires
a getter and setter to it, capturing `property` in both.

### Root cause

Initialization is driven by attach, which is episodic, while its effect is structural and
persists on the subject. Re-invocation is an artifact of that mismatch, not a use case: no
implementation found across this repository, HomeBlaze, downstream consumers or the docs benefits
from running twice. Metadata that must change over time is already served by derived attributes,
whose getters re-evaluate on read.

## Decision

Initializers run **once per subject**, driven by registration and by property creation rather
than by attach.

`RegisteredSubject` ownership does not change. It stays owned by its registry and is rebuilt per
attach exactly as today. Retaining it per subject was considered and rejected; see Alternatives.

## Design

### 1. Two flags at two granularities

**Subject flag**, persistent for the life of the subject: whether initializers have ever run for
it. Stored in `subject.Data` under a single key, following the `SubjectIdKey` precedent. It
survives detach and dies with the subject, so nothing leaks.

`subject.Data` rather than `InterceptorExecutor` deliberately. The executor is the better home
and is where PR #419 moves other per-subject state, but that PR rewrites the executor heavily and
this design is meant to stay independent of it. Move the flag there once #419 lands.

**Registration flag**, on the `RegisteredSubject`: whether this registration should initialize its
properties. Set when the registration is created, from an atomic read-and-set of the subject flag.

Both are needed. The subject flag alone cannot drive a per-property check, because it flips to
"done" while the first pass is still running and would skip every property after the first.

### 2. Three events, not one

| Event | Initializes |
|---|---|
| a subject is registered for the first time | all of its properties |
| `AddProperty` creates a property | that property only |
| a subject re-attaches | nothing |

`AddProperty` initializes unconditionally, ignoring both flags: a property that was just created
has never been initialized whatever the subject's history. This covers a property added by a
consumer at runtime, and a property added by one initializer during another's pass.

A property added during the first pass is initialized exactly once, not twice.
`AttachToContext` snapshots `subject.Properties.Keys` before the pass, so a property created
during it is not in the snapshot and is not revisited by the loop, while `AddProperty` has
already initialized it directly.

### 3. Add `ISubjectInitializer`

```csharp
public interface ISubjectInitializer
{
    void InitializeSubject(RegisteredSubject subject);
}
```

Called once per subject, on the same first-registration event as the property initializers.

This is the missing grain size. `ISubjectPropertyInitializer` is per property, and the only
per-subject hook is `ILifecycleHandler`, which is episodic by design. Work that is genuinely
per-subject-once had nowhere correct to live, which is why `MethodPropertyInitializer` is an
`ILifecycleHandler` today.

The only new public type in this design.

### 4. `AddProperty` keeps throwing on a duplicate

Under once-semantics a duplicate is a real error again rather than an artifact of re-attach, so
the throw is correct behaviour and stays.

## Where each member lands

### Registry layer, structure, runs once per subject

| Interface | Implementations |
|---|---|
| `ISubjectPropertyInitializer` | `UnitAttribute` (SampleWeb), `DefaultValueInitializer` (docs), `PropertyAttributeInitializer` (moved in), downstream attribute and service initializers |
| `ISubjectInitializer` (new) | `MethodPropertyInitializer` (moved in) |

### Lifecycle layer, episodes, runs every attach and detach

| Interface | Implementations |
|---|---|
| `ILifecycleHandler` | `ContextInheritanceHandler`, `ParentTrackingHandler`, `SubjectRegistry`, `HostedServiceHandler`, `SubjectPathResolver`, `TestLifecycleHandler`, `LogPropertyChangesHandler` (sample) |
| `IPropertyLifecycleHandler` | `SubjectRegistry`, `DerivedPropertyChangeHandler`, `TestLifecycleHandler` |

Everything remaining in the lifecycle layer is paired: it starts what detach stops, or maintains
graph state that detach empties, or is deliberately fire-every-time.

## Callback signatures stay as they are

Considered and rejected: passing richer context so handlers could detect re-attach and defend
against it. Fixing the scope removes the need to detect anything, and adding the flags to the
public callbacks would preserve the shape of a problem that has been deleted, putting the burden
back on every implementer to check correctly.

The registry callbacks lose nothing by staying as they are. Everything reachable from
`RegisteredSubjectProperty` includes `Parent` and the whole registration, `Reference.Metadata`
with `IsDynamic`, `IsDerived`, `IsIntercepted`, `IsPublic` and `PropertyInfo`,
`ReflectionAttributes`, and `Subject.Context`. `SubjectRegistry.AttachProperty` holds only
`change.Subject` and `change.Property`, both derivable from the parameter.

Two gaps in the lifecycle layer are recorded and deferred, neither having a consumer today:

- `SubjectLifecycleChange` drops the attaching context, which for a child is the parent's context
  rather than `change.Subject.Context`.
- `SubjectPropertyLifecycleChange` cannot distinguish a subject-wide attach sweep from a
  standalone `AddProperty` on an already-attached subject.

Deferring the first costs nothing: `SubjectLifecycleChange` is a `readonly struct` with `init`
properties and handlers only read it, so a new non-required property is source compatible.

Constraint for the second: `SubjectPropertyLifecycleChange` is a positional `record struct`, so a
new positional parameter would break its constructor. Any future field must be an `init` property.

## Concurrency

`AddProperty` is public and may be called concurrently, at runtime, on a subject that is being
actively read and written. The design must hold for that, not only for the initializer path.

### One lock, not two

`AddProperty` locks on `subject.SyncRoot`. `_addPropertyLock` from PR #429 is removed.

`SyncRoot` is already the lock guarding a subject's property dictionary, identically in both
implementations (`SubjectCodeGenerator.cs:155` and `DynamicSubject.cs:39`):

```csharp
void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
{
    lock (((IInterceptorSubject)this).SyncRoot) { _properties = ...ToFrozenDictionary(); }
}
```

A separate lock guards only the registry's copy, leaving the subject's copy free to be mutated
concurrently through `subject.AddProperties(...)`, which `DynamicSubjectFactory.cs:49` does. The
check and the add must be atomic across both views, so `SyncRoot` is required for correctness
rather than chosen for convenience.

### Lock graph

```
SyncRoot  ->  RegisteredSubject._lock     (parent state; leaf, never calls back into the subject)
```

Acyclic. `SyncRoot` is a plain `object`, so `lock` is `Monitor` and re-entrant, which resolves the
cycle recorded in `f95dde99`: a getter or setter running under `SyncRoot` that calls `AddProperty`
re-enters instead of deadlocking.

### Cost

`AddProperty` is not on the hot path, so correctness, thread safety and freedom from deadlock
decide the design and performance is optimised only within what those allow.

Holding `SyncRoot` across the registry's property rebuild blocks intercepted reads and writes on
that subject (`ReadInterceptorFactory.cs:19`, `WriteInterceptorFactory.cs:19`). The delta is
bounded: both `AddProperties` implementations already perform an O(n) rebuild inside
`lock (SyncRoot)`, so this makes the critical section roughly twice as long rather than
introducing one.

Cheap work that does not need the lock still stays outside it: the property metadata and the
`RegisteredSubjectProperty` are built before it is taken. Building the frozen dictionary outside
the lock as well, and swapping it under the lock with a re-validation retry, would return to
roughly one rebuild. Recorded as available, not planned, because it trades a straight-line
critical section for a retry loop to optimise a path that is not hot.

### Reject an add from inside an accessor

Calling `AddProperty` from a getter or setter of the same subject leaves the post-lock section
running under the caller's lock. Re-entrancy prevents a deadlock but cannot prevent the
situation, so `AddProperty` rejects it before taking the lock:

```csharp
if (Monitor.IsEntered(Subject.SyncRoot))
    throw new InvalidOperationException(
        $"Cannot add property '{name}' to '{Subject.GetType().Name}' from a getter or setter of " +
        "the same subject: the accessor already holds the subject's lock.");
```

The check is not racy, because it is not the question that races. `Monitor.IsEntered` asks
whether the *current thread* holds the lock, which is thread-local: no other thread can change the
answer, and this thread does nothing between the check and the `lock`. The racy question would be
whether the lock is free, which is not asked.

Take-or-throw does not work as an alternative. `Monitor` is re-entrant, so
`Monitor.TryEnter(SyncRoot, 0)` returns true when the current thread already owns the lock, which
is exactly the case to reject. A non-re-entrant primitive such as `SemaphoreSlim(1,1)` would block
forever there rather than throw, turning a diagnosable error into the deadlock, and it cannot
replace `SyncRoot` in any case.

Measured cost, Release build: `Monitor.IsEntered` is 1.56 ns when the lock is not held and 1.76 ns
when it is, against 2309 ns for a single `ToFrozenDictionary` rebuild of a 12-property subject.
`AddProperty` performs two such rebuilds, so the check is about 0.03% of one of them.

The check is precise because `SyncRoot` is held only around a user accessor. The read and write
terminals take it around `innerReadValue` and `innerWriteValue`, and the zero-interceptor terminal
does not take it at all. No library flow that adds properties holds it:
`LifecycleInterceptor.WriteProperty` calls `next(ref context)` first, and the terminal takes and
releases `SyncRoot` within that call, so all attach work including every initializer runs
afterwards with no lock held. That holds for a self-referencing subject too, which is the case
most likely to look like a false positive.

Not detectable this way, and recorded rather than solved: a cross-subject cycle where A's getter
adds a property to B while B's getter adds one to A. `Monitor.IsEntered` answers only for the
current thread and the subject being added to.

### Mutate under the lock, notify outside it

`Subject.AttachSubjectProperty(...)` and `SetPropertyValueWithInterception` stay outside the lock.
Both invoke handler and initializer code that can do anything, and holding `SyncRoot` across it
reintroduces the hazard this removes.

## Consumer migration

Two HomeBlaze classes change interface. Both are registered as one-line context services in
`SubjectContextFactory.cs:31-36`, so each is an interface change plus a registration type argument.

| Class | From | To |
|---|---|---|
| `MethodPropertyInitializer` | `ILifecycleHandler` | `ISubjectInitializer` |
| `PropertyAttributeInitializer` | `IPropertyLifecycleHandler` | `ISubjectPropertyInitializer` |

Both get shorter, which is the signal that the interface was wrong rather than the code.
`MethodPropertyInitializer` loses its `IsContextAttach` wrapper, its
`TryGetRegisteredSubject() ?? throw` lookup, and the re-attach guard added in PR #429.
`PropertyAttributeInitializer` loses its `TryGetRegisteredProperty` lookup and null check, two
empty interface stubs, and both `TryGetAttribute(...) is null` guards.

`HomeBlaze.Services/Lifecycle/` then holds no lifecycle handlers and should be renamed to
`Initializers/`.

No public API breaks. `ISubjectPropertyInitializer` keeps its signature, so no downstream
implementation must change. Existing guards become redundant and stay harmless, and the guard
added downstream for PR #429 can be reverted.

## Alternatives considered

### Retain `RegisteredSubject` per subject

Pin the registration on the subject and reuse it, so initialization is once per registration by
construction and captured references never go stale. Rejected because it is unsound on `master`.

`RegisteredSubject` holds two kinds of state with different lifetimes: the property set, which
belongs to the subject, and `Parents` and `Children`, which belong to the graph. Sharing the
object shares both. Measured with two co-resolved registries:

```
[1 registry]    parents=1  children=1
[2 registries]  A parents=2  A children=2   B parents=1
[after detach]  A parents=1  A children=1
```

Two registries already resolve as separate `ILifecycleHandler` services and both receive every
change. Today each holds its own registration, so they cannot corrupt each other. A shared
registration would receive every `AddParent` twice by construction, and `RemoveParent` clears only
one of the pair.

This is safe only under a guarantee that a subject belongs to at most one graph, which is what
PR #419 introduces. Building on `master` cannot assume it.

### Richer lifecycle context

Give handlers a first-attach flag so they can defend themselves. Rejected: it preserves the
problem's shape after the problem has been deleted, and it requires every implementer to get the
check right. See "Callback signatures stay as they are".

## Testing

- An initializer runs exactly once across a move between parents that passes the reference count
  through zero.
- A property added through `AddProperty` after the subject is initialized is itself initialized.
- A property added by one initializer during the first pass is initialized exactly once.
- `AddProperty` still throws on a genuine duplicate.
- A concurrent `AddProperty` and `subject.AddProperties(...)` on the same subject leave the
  registry's and the subject's property sets in agreement.
- `AddProperty` from a property getter throws rather than deadlocking.
- Aggregating a second registry no longer throws, where today it does. Confirms the flag is
  per subject and not per registration.
- The existing ancestor-resolution tests pass unchanged, pinning that initializer invocation
  ordering did not move.

## Benchmarks

`RegistryBenchmark` covers attach and detach churn only for *new* subjects
(`AddLotsOfPreviousCars` replaces 1000 cars per iteration, `ChangeAllTires` replaces four tires),
and no benchmark registers an `ISubjectPropertyInitializer` at all. So the paths this design
changes are unmeasured. Add before and after:

- re-attach of the *same* subjects, which is where skipping initialization pays
- attach with initializers registered, since `AttachProperty` currently allocates a LINQ iterator
  per property per attach through `ReflectionAttributes.OfType<ISubjectPropertyInitializer>()`
- `AddProperty` throughput, which is where the lock change and the accessor check land

## Out of scope, to file separately

- **`AddParent` and `AddChild` are not idempotent.** Measured above: aggregating a second
  lifecycle-bearing context makes a registry record the same parent edge twice, and detach clears
  only one of the pair. A pre-existing `master` bug, the same root cause as the initializer
  double-add, and independent of this design.
- **Stale captures.** This design stops initializers re-running but still rebuilds
  `RegisteredSubject` per attach, so an initializer that captured the `RegisteredSubjectProperty`
  it was handed keeps a delegate resolving through a discarded registration. Two downstream
  initializers do this. Neither is fixed nor caused here: the same state already results from the
  guard pattern the docs currently teach.

  Making `Parent` resolve the live registration was measured and rejected. It costs 19.5x on
  attribute lookup (`TryGetAttribute` 2.47 ns becomes 48.02 ns, because
  `subject.TryGetRegisteredSubject()` is a service resolution plus a lock plus a dictionary
  lookup at 45.56 ns against a 0.26 ns field read), it lands on read paths since `TryGetAttribute`
  runs inside derived-property getters, it affects 27 call sites including `PathExtensions` loops,
  and it does not solve the problem: `Children` and `AttributesCache` are fields on the property
  instance, not on the parent, so a captured collection property still reports zero children after
  re-attach.

  The correct fix is on the capturing side, because a `RegisteredSubjectProperty` is a
  per-registration object and must not be captured as if it were permanent. Capture
  `property.Reference` instead, a `PropertyReference` of subject and name that are both permanent,
  and resolve through `TryGetRegisteredProperty()` when needed. Fully correct, free for the
  library, and a two-line change at each of the two sites. Document this on
  `ISubjectPropertyInitializer` and file the issue.
- **PR #419.** Independent. It strengthens this design by guaranteeing one graph per subject, which
  is what would make retaining the registration safe, but nothing here depends on it. When it
  lands, move the subject flag from `subject.Data` onto `InterceptorExecutor`.
- **Property removal (#210).** No removal API exists. A future one must clear the subject flag.
- **PR #429.** One part survives: the fix for `AddProperty` corrupting a declared property's
  metadata when a rejected add had already called `Subject.AddProperties`. Its locking is replaced
  rather than kept, since `_addPropertyLock` guards only one of the two views. Its idempotency
  documentation, its `ISubjectPropertyInitializer` remarks, and the consumer guards it added all
  come out.

## Permanent home

This spec is temporary. On implementation, fold the layer split and the concurrency rules into
`docs/design/tracking-lifecycle.md` and update `docs/registry.md`, whose two initializer samples
currently teach the guard pattern this removes.
