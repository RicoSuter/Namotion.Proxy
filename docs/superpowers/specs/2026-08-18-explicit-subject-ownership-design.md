# Explicit Subject Ownership and Lifecycle Design

**Date:** 2026-08-18

**Status:** Approved after three independent written-spec review rounds

**Stack position:** PR 2 on `feature/effective-ownership-route`

**Pull request:** Complete rewrite of #419

## Purpose

Fallback contexts currently perform three different jobs: service composition, explicit lifecycle
attachment, and parent-context inheritance. One fallback mutation can therefore change service
resolution, lifecycle membership, object-graph traversal, reference counts, and cache invalidation
at the same time. The overlap also lets one subject participate in several independent lifecycle
domains, even though the normal and intended consumer model uses one configured context and one
lifecycle coordinator.

This pull request separates those responsibilities. Public fallback contexts become service
composition only. Explicit attachment and property inheritance use the ownership route introduced
by PR 1. Core owns compact per-subject state and atomic transition mechanics. Tracking owns
object-graph discovery, route-selection policy, lifecycle callbacks, and reconciliation.

The result is one effective ownership domain per subject, deterministic parent transfer, rejection
before an incompatible property value commits, and less fallback-specific lifecycle machinery.

## Concepts and Terms

- **Subject executor:** the per-subject `InterceptorExecutor`. It remains the subject's
  `IInterceptorSubjectContext`, stores subject-local services and caches, and owns the subject's
  compact ownership record.
- **Plain configured context:** a context created and configured independently of a subject. An
  explicit root attaches to one of these. Another subject's executor is not a valid explicit
  attachment target.
- **Object-reference graph:** relationships expressed by subject-valued properties, collections,
  and dictionaries. Cycles and repeated references are valid.
- **Explicit attachment:** an application-owned root relationship created by `AttachToContext` and
  released by `DetachFromContext`.
- **Parent membership:** a distinct parent property that currently references a subject. Repeated
  occurrences inside the same collection property form one membership, matching current reference
  count semantics.
- **Lifecycle ownership:** responsibility for coordinating one subject's attach, detach, handlers,
  and recursive graph transitions.
- **Ownership domain:** the plain configured context supplied to an explicit root. Its reference
  identity names the domain. Descendants retain that identity even when their effective route
  targets a parent executor.
- **Ownership coordinator:** the zero-or-one `ILifecycleInterceptor` resolved for an ownership
  domain. Tracking's `LifecycleInterceptor` is the built-in implementation. One coordinator
  instance serves at most one active domain.
- **Effective ownership route:** the single internal PR 1 route used for service resolution. An
  explicit attachment wins. Otherwise the earliest surviving compatible parent membership that
  can form an acyclic route wins.
- **Fallback composition:** a relationship created by `AddFallbackContext`. It aggregates services
  but creates no lifecycle membership and invokes no lifecycle callbacks.
- **Registry projection:** an optional observer of lifecycle and property changes. It indexes the
  graph but neither defines the graph nor owns subjects.

## Goals

- Give explicit root attachment a strict, direct API.
- Keep explicit attachment and property membership separate.
- Give every owned subject one ownership domain and at most one effective ownership route.
- Reject incompatible attachment or property assignment before it commits a backing value or
  ownership, route, lifecycle, registry, or reference-count state.
- Support multiple parents, repeated references, cycles, and deterministic route transfer.
- Preserve subject-local and branch-local nonunique services and interceptors.
- Preserve the normal single-context lifecycle callback sequence.
- Keep graph traversal and lifecycle policy in Tracking while Core owns atomic state mechanics.
- Remove functional context inheritance as an optional handler capability.
- Keep the ordinary scalar interception path allocation-free and at least as fast as `master`.
- Keep warmed-up ordinary structural writes allocation-free and serialize them per ownership domain.
- Release every subject, context, property reference, reservation, and reconciliation entry when
  the final external ownership anchor disappears.

## Non-goals

- Enforcing every unique context authority. PR 3 generalizes the lifecycle rule to registry,
  transaction, hosting, and other audited authorities.
- Changing hosted-service start, stop, or drain semantics. PR 4 owns those changes.
- Removing fallback composition or nonunique branch services.
- Replacing or renaming `InterceptorExecutor`.
- Supporting optional shallow lifecycle tracking. `WithLifecycle()` always includes recursive
  property inheritance.
- Recovering arbitrary application side effects after a lifecycle callback violates its no-throw
  contract.
- Recomputing the complete object graph for every reference mutation.

## Public Model and API

### Explicit attachment

Core provides subject extension methods with these semantics:

```csharp
public static void AttachToContext(
    this IInterceptorSubject subject,
    IInterceptorSubjectContext context);

public static void DetachFromContext(
    this IInterceptorSubject subject,
    IInterceptorSubjectContext context);

public static bool TryGetAttachContext(
    this IInterceptorSubject subject,
    out IInterceptorSubjectContext? context);
```

The interface parameter preserves existing generated and Dynamic constructor signatures. At entry,
`AttachToContext` requires the exact built-in plain `InterceptorSubjectContext` implementation. It
rejects an `InterceptorExecutor`, another subclass, or any unsupported
`IInterceptorSubjectContext` implementation before service resolution, route publication,
ownership mutation, or callbacks.

Explicit attachment is strict rather than idempotent:

- the first attachment succeeds;
- any second explicit attachment throws, including attachment to the same context;
- a successful detach permits a later new attachment;
- the first explicit attachment of a subject already inherited in the same exact domain succeeds,
  changes only the active route and external anchor, and emits no new subject-attach callbacks;
- attachment of an inherited subject to a different domain throws;
- a subject can have compatible parent memberships while explicitly attached, but the explicit
  route remains active.

`DetachFromContext` requires the exact context used for attachment. It throws when the subject has
no explicit attachment or when the supplied context differs. It removes only explicit ownership.
Compatible parent memberships may retain lifecycle ownership without a detach and attach callback
pair.

`TryGetAttachContext` reports only the explicit attachment. It returns `true` with the exact attach
context when an explicit anchor exists. It returns `false` with `null` for a subject owned solely
through parent membership or not owned at all.

### Reference count

`GetReferenceCount` remains a Tracking extension because it describes object-reference graph
membership. Core stores the count in the subject's ownership record so the read is direct and does
not use `IInterceptorSubject.Data`.

The count includes distinct parent properties. It excludes explicit attachment and collapses
repeated occurrences of the same child inside one parent property. Index changes inside a retained
collection membership refresh lifecycle metadata without changing the count.

Every parent-membership addition or removal that survives reconciliation produces the existing
property-reference lifecycle change with the updated count. Concurrent structural commits may be
coalesced into their final net transition, as they are today. PR 2 does not add a revision journal
or promise one callback per superseded intermediate value. Only the transition between unowned and
owned produces `SubjectAttached` or `SubjectDetaching`. Route transfer by itself produces neither.

### Removed configuration surface

`WithContextInheritance()` is removed. `ContextInheritanceHandler` is removed as a functional and
configurable capability. `WithLifecycle()` always installs complete recursive lifecycle tracking.

This is intentional. A shallow lifecycle that attaches a parent while leaving its children in a
different service and registry state creates surprising partial graphs and no longer has a
supported first-party use case.

### Fallback composition

`AddFallbackContext` and `RemoveFallbackContext` retain their service-composition behavior and
return values. They no longer invoke `ILifecycleInterceptor`, attach subjects, detach subjects,
establish parent membership, or recurse into properties.

Fallbacks can still contribute nonunique services and interceptors. Repeated paths still
deduplicate the same service instance. Late nonunique additions remain legal and invalidate
dependent caches.

## Package Boundary

Core and Tracking communicate through a deliberate public provider contract. PR 2 does not add
friend-assembly access.

`ILifecycleInterceptor` remains the one public lifecycle authority contract and expands to include
prospective structural-write coordination. It inherits `IWriteInterceptor`, formalizing what the
built-in `LifecycleInterceptor` already implements. PR 2 does not add a second independently
configurable ownership service. Tracking's `LifecycleInterceptor` implements the complete
contract. Core uses that same instance as the captured lifecycle-authority identity, explicit
attach and detach coordinator, and terminal structural-write coordinator. The plain configured
context, not the coordinator instance, remains the ownership-domain identity.

The contract uses three public stack-only input contexts plus one controlled operation facade:

```csharp
public interface ILifecycleInterceptor : IWriteInterceptor
{
    void AttachSubjectToContext(
        ref SubjectAttachmentContext context,
        ref SubjectOwnershipOperation operation);

    void DetachSubjectFromContext(
        ref SubjectDetachmentContext context,
        ref SubjectOwnershipOperation operation);

    void PrepareSubjectPropertyWrite<TProperty>(
        ref SubjectOwnershipWriteContext<TProperty> context,
        ref SubjectOwnershipOperation operation);
}
```

The public operation inputs are stack-only and have these read contracts:

```csharp
public readonly ref struct SubjectAttachmentContext
{
    public IInterceptorSubject Subject { get; }
    public IInterceptorSubjectContext AttachContext { get; }
}

public readonly ref struct SubjectDetachmentContext
{
    public IInterceptorSubject Subject { get; }
    public IInterceptorSubjectContext AttachContext { get; }
}

public readonly ref struct SubjectOwnershipWriteContext<TProperty>
{
    public PropertyReference Property { get; }
    public TProperty FinalValue { get; }
    public long ExpectedRevision { get; }
}
```

Core also passes one stack-only `SubjectOwnershipOperation`. It is the only mutation facade:

```csharp
public ref struct SubjectOwnershipOperation
{
    public IInterceptorSubjectContext OwnershipDomain { get; }
    public object? ProviderState { get; set; }

    public SubjectOwnershipView GetView(IInterceptorSubject subject);

    public void ReserveExplicitAttachment(IInterceptorSubject subject);
    public void ReserveExplicitDetachment(IInterceptorSubject subject);

    public void ReserveParentAddition(
        IInterceptorSubject subject,
        PropertyReference parentProperty);

    public void ReserveParentRemoval(
        IInterceptorSubject subject,
        PropertyReference parentProperty);

    public void SelectActiveParent(
        IInterceptorSubject subject,
        PropertyReference? parentProperty);

    public void ReserveFinalRelease(IInterceptorSubject subject);

    // Called by attach and detach providers. The property terminal performs the
    // corresponding commit itself around the backing-field write.
    public bool TryCommit();
    public void PublishSelectedRoute(IInterceptorSubject subject);
    public void FinalizeSelectedRoute(IInterceptorSubject subject);
}
```

`ReserveParentAddition` derives the candidate route target from `parentProperty.Subject.Context`.
One parent property is one Core membership even when a collection contains the same child several
times. Tracking retains per-occurrence index metadata in its reconciliation state; Core does not.
`SelectActiveParent` accepts `null` only for an explicit route or final release already reserved in
the same operation. `TryCommit` returns true only when every reserved subject still has its
expected generation. On false, the provider returns without callbacks and Core retries from the
winning generation; the operation never partially commits. A true ownership conflict throws before
`TryCommit`. Property-write commit also requires the initiating revision to remain current and is
performed by the Core terminal, not the provider.
Route publication and finalization require a matching committed reservation and exact PR 1 route
descriptor. Misordered or repeated calls throw before changing state.

The read-only view and its allocation-free ordered membership enumerator are public provider
contracts:

```csharp
public readonly ref struct SubjectOwnershipView
{
    public bool Exists { get; }
    public IInterceptorSubjectContext? ExplicitAttachContext { get; }
    public IInterceptorSubjectContext? OwnershipDomain { get; }
    public PropertyReference? ActiveParentProperty { get; }
    public int ReferenceCount { get; }
    public long Generation { get; }
    public SubjectParentMembershipEnumerable ParentMemberships { get; }
}

public readonly struct SubjectParentMembership
{
    public PropertyReference ParentProperty { get; }
}

public readonly ref struct SubjectParentMembershipEnumerable
{
    public SubjectParentMembershipEnumerator GetEnumerator();
}

public ref struct SubjectParentMembershipEnumerator
{
    public SubjectParentMembership Current { get; }
    public bool MoveNext();
}
```

The enumerator exposes memberships in stable insertion order. Views and enumerators are valid only
for the synchronous operation generation that created them. Tracking uses them to implement direct
reference-count reads, deterministic transfer, and affected-component anchor detection. It cannot
replace an ownership record, construct a generation, publish an arbitrary route, or bypass the
operation's generation checks. `IInterceptorExecutor` adds this read contract so Tracking's
existing `GetReferenceCount` extension can read the Core-owned count without new friend access:

```csharp
int OwnershipReferenceCount { get; }
```

Reading the count for a never-used subject may create its normal lazy executor, but never creates
an ownership record.

For a structural property write in a lifecycle-coordinated domain, Core enters the ownership
domain's reentrant structural-write gate before it selects the cached action. It then re-reads the
ownership generation and context state, so an action compiled while a subject was route-free cannot
run after that subject has joined a domain. The ordinary resolved chain contains the exact
coordinator once in its characterized ordering position. A subject that is reserved before its
ownership route becomes visible uses a transition-generation action built from its still-visible
local interceptors plus that exact coordinator. This changes write coordination only; it does not
expose the pending ownership route or parent services to service lookup. A valid explicitly
attached domain with no lifecycle coordinator has no recursive property ownership and keeps the
ordinary cached structural-write path.

Core stores the committed operation token and provider-owned reconciliation state in the
by-reference `PropertyWriteContext<TProperty>`. The exact coordinator's existing `WriteProperty`
call receives control after `next`, obtains a fresh stack-only facade through
`context.TryGetSubjectOwnershipOperation(out var operation)`, reads `ProviderState`, updates its
baseline, and invokes callbacks. The method returns `false` when no structural operation committed.
Core wraps that exact coordinator node in `finally` so pending Core reservations and deferred route
work are released even when a provider violates the no-throw contract. The transition-generation
action uses the coordinator's concrete ordering identity, does not add a second provider node, and
is discarded when that transition generation ends.

The write context exposes the operation only during that exact committed unwind:

```csharp
public bool TryGetSubjectOwnershipOperation(
    out SubjectOwnershipOperation operation);
```

Because fallback composition can contribute interceptors before a subject belongs to any ownership
domain, the same coordinator may also appear in an ordinary route-free write chain. That call has no
activation record and no domain gate. `WriteProperty` must call `next` normally, then treat
`TryGetSubjectOwnershipOperation == false` as a transparent non-ownership write: it touches no
lifecycle membership, reconciliation baseline, provider state, or callback projection. Core
guarantees the matching gate only for attach, detach, prepare, and unwind of a committed ownership
operation. A route-free transparent call may run concurrently with the coordinator's bound active
domain because it does not access the coordinator's lifecycle state.

The contract is public so the separately published Core and Tracking packages can coordinate
without adding another friend-assembly dependency. It is advanced package infrastructure, not an
application extension point. Applications should configure and observe the built-in
`LifecycleInterceptor` rather than implement `ILifecycleInterceptor` or retain or construct its
operation types. Only the built-in coordinator is supported. The infrastructure contract may
change in a future breaking release as Core, Tracking, authority, and hosting coordination evolve.

The advanced provider API has these contracts:

- at most one distinct coordinator may resolve in one active effective context;
- one coordinator instance is bound to at most one active configured-context domain;
- that exact coordinator instance occurs exactly once in the ordered write chain;
- calls are synchronous;
- Core holds the matching domain structural-write gate for attach, detach, prepare, and committed
  ownership unwind; an ordinary route-free `WriteProperty` call has no operation and is transparent;
- `WriteProperty` calls `next` at most once and performs postcommit reconciliation during unwind;
- implementations must not throw from lifecycle callbacks;
- implementations must not retain an operation context, view, enumerator, or reservation beyond
  the synchronous call;
- application code configures the supported built-in coordinator through `WithLifecycle()` and
  does not implement the provider contract.

Core already grants Tracking friend access for commit revision and raw timestamp propagation. PR 2
adds no new use. Removing those two existing uses and the friend declaration is explicitly deferred
to a focused follow-up so PR 2 does not expose unrelated raw storage encoding or enlarge its public
surface further.

## Core Ownership State

Every `InterceptorExecutor` has one nullable ownership field and one compact route-free structural
admission word:

```text
SubjectOwnershipState?
  ExplicitAttachment?
  FirstParentMembership
  AdditionalParentMemberships?
  ActiveParentMembership?
  OwnershipDomain
  LifecycleCoordinator?
  ReferenceCount
  Generation
  PendingTransition

RouteFreeStructuralAdmission
  ActiveWriterCount
  Generation
```

The ownership field remains `null` until the subject first participates in explicit or inherited
ownership. When the final attachment, parent membership, and transition disappear, Core clears the
field back to `null`. The admission word is inline executor storage, not a heap object. It is touched
only by potentially structural writes while the executor is still route-free and by the cold
operation that adopts such a subject.

The common first parent membership is stored inline. An ordered overflow collection is allocated
only for a second distinct parent property. The collection preserves insertion order so transfer is
deterministic. A membership records only the source property, candidate route target, ownership
domain, and transition identity. Core does not traverse property values or interpret registry
state.

The active membership refers to one of the recorded memberships. Its PR 1 route descriptor is the
exact publication generation. A stale detach or transfer can change the route only when both the
ownership generation and route descriptor still match.

Explicit attachment is a separate field, not a zero-property sentinel and not part of the
reference count.

### Domain activation

The first explicit root activates its plain configured ownership domain and captures the resolved
zero-or-one lifecycle coordinator, including the valid no-coordinator case. Several disconnected
roots may share the same activated domain.

Prospective coordinator discovery uses a Core authority walk over the exact immutable context
states and does not call ordinary `GetServices<ILifecycleInterceptor>()`. It deduplicates only by
`ReferenceEquals`, allowing the same instance through repeated paths and rejecting two distinct
instances even when a custom `Equals` implementation considers them equal. The walk uses the same
cycle-aware pooled traversal shape as service resolution but does not apply default-equality service
deduplication before cardinality validation. Activation performs this raw walk, binding check, and
activation-record publication under one authority-publication-gate interval, so a context or
fallback mutation cannot publish between discovery and capture. The walk invokes no user code.
The implementation is a permanent contract-type-plus-reference-identity collector; PR 3 reuses it
for every declared unique authority instead of replacing a lifecycle-specific algorithm.

An activated plain context uses a derived immutable context state that references one lazy
`ContextAuthorityActivation` record. Route-free, inactive contexts retain the PR 1 state shape.
The record contains:

```text
ContextAuthorityActivation
  Status: Activating | Active | Releasing
  LifecycleCoordinator: exact instance or null
  ExplicitRootLeaseCount
  Generation
  TransitionToken
  ReentrantStructuralWriteGate
```

`ExplicitRootLeaseCount` counts explicit anchors in this exact domain, not subjects. The activation
record itself is the monitor target, so the gate adds no second managed allocation. The record is
removed after the last explicit anchor and every ownership transition from that anchor has
quiesced. PR 3 extends the authority value stored in this same record shape and mechanism rather
than replacing them; an individual activation-record instance is not permanent after final release.

One gate per lifecycle domain is deliberate. A structural transition can reserve and reconcile
several subjects, so independent per-subject serializers would still require ordered multi-lock
coordination and a separate graph-wide callback order. The domain gate provides that order with the
same lifecycle synchronization domain the library already has and without allocating a queue or
gate per subject. The per-subject route-free admission word is only a temporary adoption handshake,
not another serializer.

One lifecycle coordinator instance may be bound to at most one active ownership domain. The first
activation records that binding under the authority publication gate; activation of another exact
plain context with the same coordinator rejects before ownership publication or callbacks. Several
explicit roots in the same exact domain still share the coordinator and gate. Final quiescent
release removes the binding. This keeps the coordinator's mutable reconciliation state protected by
one gate without retaining Tracking's second lock or manufacturing per-domain copies of provider
state.

Core stores active non-null bindings in one lazily created reference-identity map protected by the
authority publication gate. An entry points to the exact activation record, is consulted only by
cold activation and release, and is removed before that record becomes unreachable. It adds no
steady-state lookup or per-subject allocation.

If first activation later fails before an explicit lease or ownership batch commits, Core removes
the zero-lease activation record and its coordinator binding under the authority publication gate.
The same coordinator can then bind immediately to another domain. No failed precommit activation
leaves an `Activating` record or identity-map entry behind.

Every context-state replacement, including cache invalidation through `WithoutCaches()`, preserves
the exact activation record just as PR 1 preserves an exact ownership-route descriptor. An inactive
plain context uses the base state and pays no activation-record allocation.

Every owned subject pins the captured coordinator identity, including `null`, across its complete
effective context. A service, fallback, or ownership-route mutation that would change that identity
for any active downstream subject is rejected before publication. Adding another path to the same
coordinator instance inside its already bound domain remains valid. Capturing `null` prevents adding
the first coordinator anywhere in that active effective cone. The cold mutation path may walk
active reverse dependencies; steady-state resolution does not. Non-lifecycle service mutation
remains legal.

The lazy activation-record mechanism is permanent infrastructure that PR 3 can extend for the
complete unique-authority map. PR 2 must not add a lifecycle-only compatibility bridge that PR 3
removes.

### Authority publication gate

One permanent Core gate linearizes only authority-relevant publication:

- domain activation and final release;
- exact coordinator-to-domain binding and unbinding;
- ownership reservation publication and cancellation;
- ownership-route publication, transfer, and clear;
- service and fallback state publication whose prospective state must be checked against active
  domains.

The authority publication gate is never entered by cached service resolution, intercepted reads,
scalar writes, method invocations, lifecycle callbacks, reconciliation, property getters, or target
code. It is distinct from the per-domain structural-write gate. Context-local `TryAddService`
predicates and factories retain their current behavior under that context's mutation lock and run
before the publication gate is entered. The publisher re-reads the state, validates the complete
prospective authority result, and publishes while holding both locks.

The order for context mutation is the existing context mutation lock followed by the authority
publication gate. A holder of the publication gate never waits for another context's mutation lock.
Multi-context ownership work publishes a pending activation token first, then reserves each subject
under the publication gate and its existing `SyncRoot` one at a time. It never holds several
subject locks simultaneously. Later phases match the token and generation. This two-phase protocol
allows the implementation to release one local lock before entering another without exposing an
unprotected interval.

Ownership-ledger reservation uses publication-gate-then-`SyncRoot` order. Ownership-route state
uses the context-publisher order instead: the committed ownership token first authorizes one exact
route descriptor, then the executor takes its own context mutation lock followed by the publication
gate and publishes that descriptor without holding `SyncRoot`. No publication-gate holder acquires
a context mutation lock.

Reverse dependency registration and the prospective context state are both stable while the gate
is held. Therefore adding a fallback cannot race a coordinator mutation in its target: one
publication wins, and the loser validates against that winner. A mutation of a deeper target walks
the reverse dependency graph under the same gate and compares the prospective coordinator with
every active domain it reaches. The walk uses pooled, cycle-aware buffers and occurs only on the
cold mutation path.

Domain activation follows this state table:

| Current state | Operation | Result |
|---|---|---|
| Inactive | First valid explicit attach | Publish `Activating` with one lease and captured coordinator |
| Activating | Same transition token | Continue reservation or commit |
| Activating | Structural write in the same synchronous domain operation | Enter the reentrant gate and join the live operation |
| Activating | Competing structural write on another thread | Wait at the domain gate without holding a Core publication or subject lock, then re-read state and execute once |
| Activating | Context mutation preserving the captured coordinator | Publish without waiting for the domain gate; advance the activation generation so discovery retries |
| Activating | Context mutation changing the captured coordinator | Reject before publication without waiting for the domain gate |
| Activating | Reentrant explicit ownership transition | Reject before publication rather than waiting on itself |
| Activating | Competing explicit ownership transition on another thread | Wait at the domain gate without holding a context or publication lock, then re-evaluate |
| Active | Compatible explicit attach | Increment lease count |
| Active | Context mutation preserving captured coordinator identity | Publish normally without waiting for the domain gate |
| Active | Context mutation changing captured coordinator identity | Reject before publication without waiting for the domain gate |
| Active | Final explicit-anchor release | Publish `Releasing` until ownership cleanup quiesces |
| Releasing | Same transition token | Complete cleanup and remove the activation record |
| Releasing | Reentrant structural write in the same operation | Enter the reentrant gate and reconcile against the live generation |
| Releasing | Context mutation preserving the captured coordinator | Publish without waiting for the domain gate; advance the release generation so cleanup revalidates |
| Releasing | Context mutation changing the captured coordinator | Reject before publication without waiting for the domain gate |
| Releasing | Reentrant explicit ownership transition | Reject before publication rather than waiting on itself |
| Releasing | Competing explicit ownership transition on another thread | Wait at the domain gate without holding a context or publication lock, then re-evaluate |

The domain gate is intentionally held across the synchronous structural chain and lifecycle
reconciliation, except while a cold adoption waits for a route-free writer to leave. This matches
today's rule that lifecycle callbacks execute inside lifecycle synchronization. A different thread
can therefore wait while a fast no-throw callback completes, but it holds no context,
authority-publication, or subject lock while waiting. Monitor reentrancy allows structural writes
from the current lifecycle operation. A reentrant operation that cannot be joined safely fails
immediately instead of waiting on itself. Context publication never waits for the domain gate:
compatible publication advances the transition generation, and coordinator-changing publication
is rejected under the authority publication gate. Generation retry remains library controlled and
never reruns an already-executed write-interceptor prefix.

Core applies one deterministic domain-entry rule to structural writes, explicit attach, and
explicit detach. It tracks the current domain-entry stack and `TryAddService` predicate/factory
callback depth in reusable thread-local state. Same-domain entry is reentrant. An ordinary caller
holding neither a different domain gate, a context-mutation callback scope, nor the initiating
subject's `SyncRoot` waits for the gate and proceeds. Ordinary contention never changes a valid
operation into an exception.

An operation that is not same-domain reentrancy and begins while the thread holds another domain
gate, a context-mutation callback scope, or the initiating subject's `SyncRoot` is always rejected
with `SubjectOwnershipNestingException` before probing whether the target gate is available and
before a write-interceptor prefix or ownership mutation. The result therefore depends on the
unsupported synchronous call structure, never on thread scheduling. This prevents A-to-B/B-to-A,
context-lock-to-domain-gate, and same-subject `SyncRoot`-to-domain-gate inversions without global
lock ordering or redesigning atomic service registration. Core can detect only the initiating
subject's public `SyncRoot`. Consumers and custom interceptors must not invoke a domain-gated
operation while manually holding another subject's `SyncRoot`; the runtime cannot discover
arbitrary externally held monitors. Library-owned paths never enter a domain gate while holding
any subject's `SyncRoot`.

## Tracking Responsibilities

Tracking owns policy and graph knowledge:

- interpret structural object, collection, and dictionary shapes after Core admission;
- enumerate subject references in objects, collections, dictionaries, and read-only enumerable
  shapes;
- maintain the committed property-value reconciliation baseline;
- discover affected subtrees with pooled, cycle-aware worklists;
- select the earliest compatible acyclic parent route;
- identify anchored and unanchored cyclic components;
- propose batched Core ownership reservations;
- invoke lifecycle handlers, subject handlers, events, and property handlers in characterized
  order;
- update parent and registry projections through their existing handlers;
- clean reconciliation entries after final detach.

Core is the source of truth for ownership membership. Tracking dictionaries are reconciliation and
callback projections, not independent ownership ledgers.

### Structural classification contract

Core owns the conservative property-type classification because admission happens before a Core
write action is selected. The public package boundary is:

```csharp
public static class SubjectPropertyTypeClassifier
{
    public static bool CanContainSubjects(Type type);
    public static bool IsSubjectReferenceType(Type type);
    public static bool IsSubjectCollectionType(Type type);
    public static bool IsSubjectDictionaryType(Type type);
}

public readonly record struct SubjectPropertyMetadata
{
    public bool CanContainSubjects { get; }
}
```

The cached classifier moves from Tracking to Core. Existing Tracking
`SubjectPropertyTypeExtensions` methods remain as forwarding compatibility APIs, so there is one
runtime classification implementation. Every public `SubjectPropertyMetadata` constructor computes
and stores the flag from its `Type`; no caller-supplied boolean can bypass admission. Generated
setters receive a compile-time mirror directly, so the null-context and stable generated paths do
not perform a metadata lookup. Generated metadata still computes the stored flag through the Core
runtime classifier during cold type initialization. Dynamic construction and non-generic paths read
the stored metadata flag. Tracking consumes the same flag before interpreting the value shape.
Generator snapshot and cross-package tests require the compile-time mirror and runtime
classification to agree for scalar, subject, object, interface, collection, dictionary, and
ambiguous enumerable shapes. Conservatively classified `object` and interface writes enter
admission even when a particular runtime value is scalar; those rows are included in performance
verification.

## Structural Property Write Protocol

A structural assignment must validate the final proposed value before the generated backing field
changes. A post-commit handler cannot provide that guarantee.

### Compiled write seam

Known scalar property types use the ordinary cached Core path. They do not inspect ownership state,
enter a lifecycle gate, or perform a service lookup.

Potentially structural shapes that can participate in lifecycle ownership enter Core ownership
admission before selecting their cached action. An owned subject with a coordinator captures its
activation record, enters that record's reentrant structural-write gate, re-reads its ownership
generation and context state, and only then selects and executes the action. Except for the cold
route-free-writer drain described below, the gate remains held through the existing interceptor
chain, backing-field commit, provider unwind, and lifecycle reconciliation. This serializes
structural operations within one lifecycle-coordinated ownership domain. Different ownership
domains remain independent. An explicitly attached no-coordinator domain keeps the ordinary cached
structural path because it has no inherited membership or lifecycle reconciliation.

A reserved subject whose ownership route is not visible yet uses a transition-generation action.
Core orders the exact captured coordinator with the interceptors visible from the subject's current
local and fallback composition, using the coordinator's normal concrete ordering identity. The
action is cached only for that transition generation and is discarded when the route is published,
transferred, cancelled, or cleared. Parent services and the pending route do not become visible
early. The exact coordinator is therefore present for postcommit unwind even when an older
route-free action had previously been compiled.

A structurally capable executor that is still route-free uses its inline route-free admission word.
Core increments the active-writer count under `SyncRoot` before action selection and decrements it in
`finally`. Adoption can publish a pending domain only after that count reaches zero. If it observes
active writers, it first cancels and clears the operation's complete tentative reservation batch.
It then releases the domain gate and every Core publication lock, waits on the subject's generation,
re-enters the domain gate, and restarts terminal discovery from the still-uncommitted transformed
value. No partial reservation remains visible while the gate is released, and the write-interceptor
prefix does not run again. It never waits while holding the domain gate, authority publication gate,
context mutation lock, or another subject lock. Once the pending domain is published, a new write
joins that domain gate before choosing an action.
Core tracks current route-free admissions in a reusable thread-static stack so a synchronous
self-upgrade is rejected instead of waiting on its own active-writer count. The stack is cleared in
`finally` and has no allocation after thread warm-up.

The existing `LifecycleInterceptor` remains in its characterized `IWriteInterceptor` position.
Stable chains contain the normally resolved instance. Transition-generation actions inject that
same exact instance only as write coordination, at the same position, without exposing services or
creating a second instance. The terminal hook performs prospective ownership reservation and
commit. Lifecycle reconciliation occurs when the coordinator unwinds.

The generated `_context is null` fast path needs one narrow handshake because an unowned subtree
can be reserved for attachment while one of its descendants is being changed. The generator emits
a conservative compile-time `canContainSubjects` flag for each property setter:

- scalar unowned setters retain the existing direct write with no lock, branch, or allocation
  beyond today's `_context` check;
- a potentially structural unowned setter locks the subject's already-existing `SyncRoot`,
  re-reads `_context`, and writes directly only when it is still null;
- ordinary `Context` access may continue to publish the executor by compare-and-swap; a structural
  setter or adopting operation takes `SyncRoot`, re-reads that publication, creates and publishes an
  executor only when still absent, and uses the compare-and-swap winner;
- after the executor exists, its route-free admission word covers the longer intercepted-write
  interval, regardless of which path first published it;
- attachment waits for pre-existing route-free writers to leave before it publishes the pending
  domain and reads the subject's structural properties;
- once a pending domain exists, the setter enters that domain's gate and selects either the
  transition-generation or stable routed action.

A setter that acquired `SyncRoot` first either finishes the direct null-context write or publishes
its route-free active-writer admission before discovery can reserve it. An adopting operation that
acquired it first ensures the executor and pending domain are published before the setter can choose
an action. A concurrent nonstructural `Context` access may win executor publication, but it does not
bypass the `SyncRoot` handshake used by the structural setter and adopter. No
structural mutation can therefore land between final subtree validation and the parent commit, and
no interceptor prefix is replayed. Dynamic and non-generic property entry points already create or
receive an executor and use the same admission protocol.

Calling explicit attach, explicit detach, or a coordinator-changing context mutation synchronously
from inside an active route-free structural interceptor chain is rejected before ownership
publication. Such an operation cannot synchronously wait for its own admission to finish. Ordinary
same-domain property writes from lifecycle callbacks are supported: the domain gate is reentrant,
and the generation protocol makes the later committed value the reconciliation winner. A nested
operation targeting another domain is always rejected before side effects, regardless of whether
the target gate is free or occupied. The callback must defer that work until the current domain
operation returns.

### Transition sequence

For a potentially structural write:

1. Core completes route-free admission or enters the captured ownership domain's reentrant gate,
   then re-reads the ownership generation and selects the matching stable or transition action.
2. Write interceptors run once in their characterized order and may suppress or transform the
   value.
3. At the terminal boundary, Tracking sees the final proposed value and compares it with its last
   committed reconciliation baseline.
4. Tracking discovers additions, removals, transfers, and affected descendants with pooled
   worklists. Before reading a newly discovered subject's structural properties, Core ensures its
   executor exists and inspects route-free admission. If a prior route-free writer is active, Core
   cancels the complete tentative batch, releases the domain gate and publication locks, waits for
   that writer, re-enters, and restarts terminal discovery without replaying the interceptor prefix.
   Otherwise it reserves the subject against its current generation before reading it.
5. Discovery completes only after every affected subject is reserved without publishing a route or
   lifecycle membership.
6. A stale baseline or generation cancels the complete reservation and retries the still-uncommitted
   terminal work under the domain gate. It does not rerun an interceptor prefix. If the gate was
   released to drain a route-free writer, another operation may commit first; after re-entry, this
   operation revalidates its already-transformed proposed value and linearizes at its later terminal
   commit. A true ownership-domain conflict cancels the complete reservation and throws. In both
   retry and conflict cases the backing property is still unchanged by this operation.
7. The terminal performs the backing-field write under the existing subject synchronization.
8. If the backing write throws, Core cancels every reservation and publishes no ownership change.
9. Core commits the write revision and the reserved ownership ledger. Effective route visibility
   remains unchanged until the characterized lifecycle-handler phase.
10. `LifecycleInterceptor` records the committed reconciliation baseline before invoking callbacks,
   then reconciles the transition under its lifecycle synchronization.
11. At its former `ContextInheritanceHandler` position, attach publishes the selected route before
    recursive descent; detach performs recursive descent before clearing or transferring the route.
12. It continues the established lifecycle sequence without holding Core ownership-transition
    locks.
13. Core finalizes any deferred route work in `finally`, so a callback contract violation cannot
    strand the ownership record on the old route.
14. A write from another thread enters the domain gate afterward. A reentrant write enters the same
    monitor immediately. Exact generations stop a stale outer tail, and the final committed value
    wins. A value whose lifecycle attachment was already published is balanced by detach before its
    replacement attaches; a value superseded before publication produces no artificial attach and
    detach pair.

Reservation handles are compact Core values. Tracking owns and pools any multi-subject batch
buffers. Scalar writes allocate neither.

Structural write state follows this table:

| Current state | Operation | Result |
|---|---|---|
| Unowned | Compatible parent addition | Reserve domain, membership, and selected route |
| Unowned | Incompatible child or descendant | Reject before backing-field commit |
| Owned in domain A | Parent addition from domain A | Reserve membership; select only when needed |
| Owned in domain A | Parent addition from another domain | Reject before backing-field commit |
| Owned | Non-active parent removal | Reserve membership removal only |
| Owned | Active parent removal with replacement | Reserve deterministic transfer |
| Owned | Final external-anchor removal | Reserve affected-component release |
| Any stable state | Stale expected generation or revision | Cancel the complete batch and retry |
| Any reserved state | Competing write on another thread | Wait at the domain gate, then re-read and execute once |
| Any reserved state | Reentrant write in the same domain operation | Enter reentrantly; the later committed generation wins |
| Any committed state | Superseded before reconciliation | Reconcile the final net transition |

### Direct collection mutation

The prospective terminal protocol applies to property assignment. Directly mutating an ordinary
collection instance without passing through the intercepted property setter remains outside the
lifecycle protocol, as it is today. PR 2 does not add collection proxies or claim that an unrelated
`List<T>.Add` passed through the property terminal. Consumers use property replacement or an
explicit property write when they need lifecycle-visible collection changes.

## Explicit Attach and Detach Protocol

### Attach

`AttachToContext` performs this sequence:

1. Validate that the supplied target is a supported plain configured context.
2. Validate that the subject has no existing explicit attachment.
3. Resolve and validate the prospective effective zero-or-one lifecycle coordinator before any
   factory, route, membership, or callback side effect.
4. Acquire or publish the configured domain's activation record, enter its reentrant gate under the
   universal entry rule, then activate or join its explicit lease. A failed gate entry publishes no
   lease or subject ownership.
5. When a coordinator exists, discover the complete current subtree incrementally. For each
   route-free executor, release domain and publication locks while any earlier route-free structural
   writer finishes, then re-enter, revalidate, publish the pending domain, and read its structural
   properties.
6. Reject an incompatible descendant by cancelling all reservations. No route, ledger entry,
   callback, registry entry, or property write remains.
7. Commit the explicit anchor and reserved ownership ledger. Publish the explicit root route.
8. Invoke lifecycle callbacks in the characterized order. Each inherited route becomes visible
   only when callback iteration reaches the coordinator's former `ContextInheritanceHandler`
   position for that subject, immediately before its recursive descent.
9. A same-domain inherited subject that merely gains an explicit anchor emits no new attach
   callbacks.

A no-coordinator context attaches only the explicit subject route. It performs no graph traversal
or lifecycle callback. Its activation record serializes strict explicit transitions, but ordinary
property writes do not enter that gate. The captured `null` coordinator cannot change while that
attachment is active.

### Detach

`DetachFromContext` first validates the exact explicit context, enters the captured domain gate
under the universal entry rule, and only then reserves the transition.

If no compatible parent membership survives, lifecycle detach runs while the old route remains
resolvable. Core clears the explicit record and route after the required detach callbacks, including
from a `finally` path when application callback code violates the no-throw contract. It then removes
the domain lease and releases the ownership state when empty.

If a compatible parent membership survives, Core transfers the effective route to that parent.
The subject remains in the same lifecycle domain, so no final `SubjectDetaching` or new
`SubjectAttached` callback occurs. Service resolution may change because the explicit configured
route and inherited parent branch can contribute different nonunique services. Core publishes a
new context state and invalidates affected compiled chains. The route switch is the explicit-detach
linearization point and completes before `DetachFromContext` returns.

An explicit root remains owned when the application merely drops its last CLR reference. The
application must call `DetachFromContext` to release explicit ownership.

Explicit transitions follow this table:

| Current state | Operation | Result |
|---|---|---|
| No explicit anchor | Attach to valid plain context | Reserve and commit one explicit anchor |
| Explicitly attached | Any attach, including same context | Reject before mutation |
| Inherited in same domain | Attach to that exact domain | Add anchor and select explicit route without lifecycle churn |
| Inherited in another domain | Attach | Reject before mutation |
| No explicit anchor | Detach | Reject before mutation |
| Explicitly attached | Detach with different context | Reject before mutation |
| Explicitly attached with compatible parent | Exact detach | Remove anchor and transfer route without lifecycle churn |
| Explicitly attached without surviving parent | Exact detach | Run final detach with old route visible, then clear |
| Any transition | Stale token or generation | Leave the winner intact and retry or report its strict result |

## Parent Membership and Route Selection

### Compatibility

An unowned child is compatible with the parent's ownership domain. An already owned child is
compatible only when its exact configured ownership-domain identity matches. Merely sharing the
same coordinator instance is insufficient.

The complete proposed child subtree is checked. A compatible root with an incompatible descendant
is rejected before the parent backing property changes.

Fallback composition does not establish compatibility and is not an ownership anchor.

### Deterministic selection

Explicit attachment supplies the active route whenever present. Otherwise Tracking selects the
earliest surviving compatible parent membership that does not create an ownership-route cycle.

Secondary compatible memberships are recorded but do not churn the route. Adding or removing one
is O(1) in the common case. Removing the active membership scans the subject's remaining ordered
memberships and ancestry until it finds a valid replacement.

### Cycles and repeated references

The complete object-reference graph may contain cycles and repeated references. The active
ownership routes form an acyclic forest over that graph:

- back-edges remain valid parent memberships and registry relationships;
- a back-edge does not become an active route when that would make a route cycle;
- several occurrences of one child in the same parent property remain one membership;
- different parent properties remain distinct memberships;
- removing one of several references does not detach the child;
- removing the active parent transfers to a surviving compatible membership when possible.

Reference count alone does not keep an unanchored cycle alive. When the final explicit or external
route anchor disappears, Tracking walks only the affected component. If no explicit root or
incoming membership from outside the component remains, it detaches the complete component and
clears every internal membership and route despite nonzero internal reference counts.

Full component traversal is therefore limited to attach, final detach, active-route transfer, and
possible anchor loss. Ordinary reads, scalar writes, service resolution, and compatible secondary
membership changes do not walk the graph.

## Lifecycle Traversal and Callback Order

`WithLifecycle()` registers one `LifecycleInterceptor` that performs recursive traversal itself.
The interceptor also implements `ILifecycleHandler` and occupies the existing lifecycle-handler
ordering phase. Its handler invocation performs the descendant transition directly. Ordering
attributes that currently target `ContextInheritanceHandler` move to `LifecycleInterceptor`.

During attach handler iteration, handlers ordered before the coordinator keep seeing the child's
pre-inheritance local context. Reaching the coordinator publishes the selected route and performs
descendant traversal. Handlers ordered after it and the subject handler see the inherited route and
keep their current deepest-first observations.

During final detach, `SubjectDetaching`, the subject handler, and service handlers ordered before
the coordinator see the old route. Reaching the coordinator performs descendant traversal and then
clears or transfers the route at the same phase where `ContextInheritanceHandler` currently removes
the fallback. Handlers ordered after it see the new route or the route-free subject. A compatible
property detach that selects another parent uses that same phase. An explicit-to-inherited transfer
has no lifecycle callback sequence and publishes its route atomically as part of explicit detach.

When lifecycle tracking is configured, its coordinator is required and cannot be registered twice.
A valid domain with no coordinator still provides strict explicit ownership but performs no
recursive lifecycle callbacks. `ContextInheritanceHandler` is no longer registered or functional.
Its public type is removed in this coordinated breaking release; custom ordering attributes migrate
from that type to `LifecycleInterceptor`.

The built-in coordinator owns the lifecycle-handler dispatch loop. It resolves the ordered handler
array containing its own `ILifecycleHandler` identity and, when iteration reaches that exact
instance, invokes its internal recursive seam with the live stack-only ownership operation instead
of making an ordinary handler callback. This preserves the ordering position without ambient
suppression, a second service instance, or a retainable operation facade. All other handlers are
invoked normally. Attach, detach, structural writes, and direct collection reconciliation enter the
same dispatch helper with a live Core operation.

The implementation must preserve checked-in characterization for:

- service lifecycle handler order;
- subject lifecycle handler order;
- `SubjectAttached` and `SubjectDetaching` placement;
- property lifecycle handler placement;
- registry-before-parent relationships;
- attach and detach descent direction;
- prepopulated explicit-root subtrees;
- derived-property initialization relative to lifecycle callbacks.

The normal single-context sequence is a compatibility contract. Live-state checks may stop a stale
reentrant callback tail after membership changes, but they may not reorder an ordinary successful
sequence.

Callback-time service visibility is pinned explicitly:

| Phase | Attach visibility | Detach visibility |
|---|---|---|
| `SubjectAttached` / `SubjectDetaching` | Selected inherited or explicit route | Old route |
| Subject `ILifecycleHandler` | Inherited route | Old route |
| Service handler before coordinator | Local or previous route | Old route |
| Coordinator and recursive descent | Publish selected route | Traverse, then clear or transfer |
| Service handler after coordinator | Selected inherited route | New route or no route |
| Property lifecycle handlers | Existing characterized post-subject phase | Existing characterized pre-final-release phase |

## Concurrency

Each subject ownership record has a monotonic generation and at most one pending transition.
Reservations compare the expected generation before commit. A later generation cannot be cleared
or transferred by an older operation, including a later route that uses the same target.

Structural transitions reserve all affected subjects before the backing property commits.
Reservation acquisition never executes callbacks and never holds several subject synchronization
locks while waiting. Potentially structural operations in one lifecycle-coordinated ownership
domain are serialized by the activation record's reentrant monitor. Another thread waits before
action selection; the current thread can reenter. A true ownership-domain incompatibility throws;
ordinary contention does not become a spurious incompatibility. A no-coordinator domain has no
inherited ownership work and does not gate ordinary property writes.

The activation record's structural-write gate replaces Tracking's separate lifecycle lock for
owned structural operations, reconciliation, and callback ordering. Core route publication uses the
PR 1 context mutation protocol plus the authority publication gate. The exact orders are:

1. a context publisher takes its one context mutation lock, completes any existing
   `TryAddService` predicate or factory work, then takes the authority publication gate for
   validation and publication;
2. an owned structural operation with a lifecycle coordinator enters its one domain gate before
   selecting an action and retains it through reconciliation; it holds no context mutation or
   authority publication lock while executing interceptors or callbacks;
3. an ownership publisher takes the authority publication gate, then at most one subject
   `SyncRoot`, publishes or cancels that subject's reservation, and releases both before moving to
   another subject;
4. a route-free structural write announces itself under its subject's `SyncRoot`, releases that lock
   while its chain runs, and clears the announcement in `finally`;
5. an adopting operation that observes such an announcement cancels its complete tentative batch,
   leaves the domain gate and every publication lock before waiting, then re-enters and restarts
   terminal discovery from the winning generation.

No path holding the authority publication gate waits for another context mutation lock, a domain
gate, or route-free writer. Generated backing-field writers retain the existing rule that they
perform only the field write while `SyncRoot` is held. User callbacks, events, lifecycle handlers,
reconciliation, property getters, and service factories never run under the authority publication
gate or a Core ownership reservation lock. Synchronous lifecycle work does run under its one
reentrant domain gate, as it runs under `LifecycleInterceptor` synchronization today.
`TryAddService` predicates and factories continue to run under their one existing context mutation
lock, exactly as documented today. The universal entry rule applies to structural writes and
explicit ownership transitions invoked by those callbacks. The same rule applies when the current
thread already holds a different domain gate or the initiating subject's `SyncRoot`. The two
monitors used by an ordinary structural write are therefore nested domain-gate-then-`SyncRoot`, but
the detectable same-subject reverse entry is rejected before gate availability is inspected.
Calling a domain-gated operation while
manually holding another subject's `SyncRoot` is an unsupported consumer/custom-interceptor lock
order. A static lock audit and focused tests ensure first-party code never does so.

Subtree discovery alternates short reserve phases with unlocked reads: verify route-free admission,
reserve one subject, release Core publication locks, read that subject's structural properties,
then reserve each discovered child before reading it. Encountering an earlier route-free writer
cancels the complete tentative batch before any wait. A reserved subject's structural setters
observe its published executor and pending domain before choosing an action. Another thread waits
at the domain gate; a same-thread reentrant write uses the transition-generation action. A stale
generation restarts terminal discovery from the committed winner without retaining partial
reservations or replaying an interceptor prefix.

Deterministic schedules must pin at least these seams:

- a structural write inside a reserved but previously unowned descendant;
- an already-materialized route-free executor with a cached no-coordinator chain racing adoption in
  both admission orders;
- first executor publication racing a null-context structural setter;
- two different subjects writing structurally in one domain, proving serialization and final-value
  reconciliation;
- reentrant structural writes joining the same domain operation without deadlock;
- explicit activation racing a lifecycle service mutation;
- a getter or `TryAddService` factory reentrantly publishing a coordinator-preserving mutation while
  activation retries, and a coordinator-changing mutation failing without waiting;
- ordinary callers contending for one domain gate, proving that every caller waits, commits, and
  reconciles without a contention-dependent exception;
- two lifecycle callbacks attempting A-to-B and B-to-A structural writes, proving each nested
  different-domain entry fails before side effects whether its target gate is free or occupied;
- attach, detach, and structural writes from a `TryAddService` predicate or factory, proving the
  same deterministic rejection with a free and an occupied target gate;
- a structural setter invoked while its caller already holds the same subject's `SyncRoot`, proving
  deterministic rejection before the interceptor prefix regardless of target-gate availability;
- structural writes in two different domains, proving independent callers remain concurrent;
- fallback publication racing a coordinator mutation in its target or deeper cone;
- two structural commits where the first is superseded before reconciliation;
- callback-time route visibility before, at, and after the recursive lifecycle phase;
- stale attach, detach, transfer, and final-component release using the same route target but a
  later descriptor generation.

In addition to the controlled schedules, bounded concurrent-load tests start many writers together
and repeatedly combine structural replacement, reparenting, explicit attach and detach, repeated
references, shared DAGs, and cycles. Ordinary contention must produce no ownership exception. Each
round waits for quiescence and checks the property values, ownership routes, reference counts,
registry, parents, lifecycle callbacks, and retained-object set against the final committed model.
The tests use barriers and events, never sleeps or timing-dependent success criteria.

Quiescent invariants are:

1. every attached subject has exactly one ownership domain;
2. every inherited subject has exactly one active acyclic route unless explicit attachment wins;
3. every recorded parent membership corresponds to a committed parent property baseline;
4. the Tracking reconciliation baseline matches the latest committed structural value;
5. registry and parent projections agree with ownership after callbacks settle;
6. no pending reservation or stale route descriptor survives quiescence;
7. an unanchored component has no ownership record, route, or lifecycle projection.

## Failure Semantics

Expected contract failures are fail-fast with no library-owned commit:

- invalid explicit target;
- duplicate explicit attachment;
- missing or wrong-context explicit detach;
- more than one prospective lifecycle coordinator;
- reuse of one lifecycle coordinator instance by another active ownership domain;
- lifecycle coordinator mutation on an active domain;
- incompatible property child or descendant;
- an explicit ownership transition or coordinator-changing context mutation invoked reentrantly
  from a route-free structural interceptor chain that has not reached its terminal;
- a domain-gated operation begun from another domain, a `TryAddService` predicate/factory callback
  scope, or the initiating subject's `SyncRoot`, unless it is same-domain reentrancy.

For property assignment, these failures happen before the backing property, ownership ledger,
route, lifecycle projection, registry projection, or reference count changes. For explicit
attachment, they happen before route publication and lifecycle callbacks.

Exception types are part of the contract:

- `ArgumentNullException` reports a null subject or context API argument;
- `ArgumentException` reports an explicit attach or detach target that is not the exact supported
  plain context shape;
- `InvalidOperationException` reports an invalid ownership state, including duplicate attach,
  missing or wrong-context detach, incompatible domains, coordinator conflicts, active-authority
  mutation, and provider protocol misuse;
- public sealed `SubjectOwnershipNestingException : InvalidOperationException` with the single
  public constructor `SubjectOwnershipNestingException(string message)` reports a recognized
  unsupported synchronous nesting scope. It is thrown deterministically before gate availability
  is inspected and tells the caller to defer the operation until the current callback or ownership
  operation returns;
- ordinary contention waits and never throws a contention or timeout exception;
- application callback exceptions propagate unchanged under the no-throw contract described
  below.

Complete-subtree validation may initialize the normal lazy executor on a traversed subject so its
structural setters can participate in reservation. Prospective coordinator resolution may also fill
the normal lazy service cache on an immutable context state. A rejected operation does not unpublish
either preparation: the executor is retained only by its own subject, contains no ownership record
or route, and creates no external lifetime edge; a cached service array is read-only and still
subject to normal invalidation. These are infrastructure preparation, not lifecycle or ownership
commits.

The final proposed property value is incompatible when the child or any reachable descendant is
already owned by a different exact configured-context domain, or when its branch composition would
change the lifecycle coordinator identity captured by the parent's domain. An unowned subtree, a
subtree already owned by the same exact domain, repeated references, same-domain multiple parents,
and same-domain cycles remain compatible. Sharing one coordinator instance does not make two plain
configured contexts the same ownership domain, and the coordinator binding rule prevents both from
being active with that instance at once.

Ownership validation runs at the terminal because preceding write interceptors may transform
`NewValue`. A custom outer interceptor may therefore observe or externally record a write attempt
before the terminal rejects it. Arbitrary custom interceptor side effects are not transactional and
cannot be rolled back. Moving validation ahead of those interceptors would validate the wrong value
and change existing interceptor order. Built-in interceptors must defer ownership, lifecycle, and
registry mutation until the terminal accepts the final value.

Lifecycle handlers and events are synchronous no-throw contracts. Built-in implementations must
catch or otherwise isolate expected operational failures so they uphold that contract. If
application callback code nevertheless throws, its exception propagates. The property and
ownership commit is not rolled back, deferred Core route finalization still runs, and arbitrary
callback side effects may be partial. PR 2 does not add fault states, callback compensation, or
retry machinery for contract violations.

## Memory Lifetime

Final release clears all strong references introduced by ownership:

- explicit attachment context;
- active and secondary parent memberships;
- PR 1 route descriptor and reverse using-context entry;
- lifecycle coordinator and ownership-domain references;
- exact coordinator-to-domain binding entry;
- transition-generation actions and captured provider references;
- Tracking reconciliation baselines;
- registry and parent projection entries;
- pending reservations and pooled batch contents.

Every route-free active-writer count returns to zero in `finally`. A transition-generation action
is discarded with its exact generation and must not retain a coordinator or provider after the
transition commits, cancels, or is superseded.

An installed ownership route intentionally retains its source executor through the target's reverse
dependency entry, and the executor retains its subject. This is what keeps an explicitly attached
root alive until explicit detach. After final route removal, no ownership infrastructure may retain
the executor or subject. Tracking must not use a permanent subject-keyed ownership dictionary as
the source of truth. Any callback or reconciliation projection keyed by a subject is removed on
final detach.

Weak-reference tests cover an implicitly detached child, an explicitly retained and then detached
root, a released cyclic component, a multi-parent final detach, and failed or superseded
reservations.

## Consumer Migration

The headline ownership migration is constructor choice. A context-taking constructor creates an
explicit ownership anchor that survives removal from every parent property:

```csharp
var child = new Child(context);
parent.Child = child;
parent.Child = null;

// Still explicitly owned. The creator releases the anchor explicitly.
child.DetachFromContext(context);
```

A route-free child acquires and releases ownership automatically through parent membership:

```csharp
var child = new Child();
parent.Child = child;
parent.Child = null; // Final parent membership releases ownership.
```

If an explicitly attached child is detached while a compatible parent membership remains, it
transfers to inherited ownership without a lifecycle detach and attach callback pair. Multiple
references, shared DAGs, and cycles continue to use the parent-membership ledger and release when
their final external anchor disappears.

The runtime and all first-party producers migrate in one pull request:

- generated context-taking constructors call `AttachToContext`;
- `DynamicSubject` context-taking construction uses explicit attachment;
- HomeBlaze and first-party application roots attach explicitly and detach at their ownership
  boundary;
- connector, subject-update, and OPC UA child factories create route-free children, publish them
  through the parent property, and verify the committed child before recursive population;
- paths that intentionally create independent roots attach explicitly;
- application code stops implementing custom `ILifecycleInterceptor` providers and configures the
  supported built-in coordinator through `WithLifecycle()`;
- old generated model assemblies must be rebuilt;
- tests that used fallback mutation as lifecycle shorthand move to explicit APIs.

The migration audit includes every `new Subject(context)`, `AddFallbackContext`,
`RemoveFallbackContext`, `WithContextInheritance`, and direct `ContextInheritanceHandler`
registration in production, tests, samples, and documentation.

### Subject factory contract

`ISubjectFactory.CreateSubject` returns a route-free, unowned subject when the caller is creating a
property child. The result may contain subject-local services and fallback composition, but it has
no explicit attachment, parent membership, pending ownership transition, or installed ownership
route. `DefaultSubjectFactory` must not select a context-taking constructor merely because the
service provider can supply a context; it uses the route-free construction path for property
children.

Connector, subject-update, and OPC UA callers check the stable public indicators of this
postcondition before publishing the result into a parent property, so an already attached or
referenced custom result fails clearly before recursive population. This check is diagnostic, not a
new atomic ownership API: it cannot prove that unrelated application code did not start a concurrent
transition after the factory returned. The parent property's normal Core reservation is the atomic
authority. If the result changes concurrently, that terminal waits or retries and applies the
general compatibility rules; an incompatible domain is rejected, while a compatible same-domain
membership may commit. A custom factory that races or returns an owned subject has violated its
contract, but it cannot violate Core ownership invariants. The caller never implicitly detaches or
steals an explicit root; its creator remains responsible for explicit detach. The interface XML
documentation, connector documentation, default factory tests, custom factory tests, and
DI-constructor-selection tests state this boundary.

## Documentation

`docs/interceptor.md` becomes the canonical user-facing explanation of:

- subject executors and plain configured contexts;
- explicit attachment;
- parent membership and reference counts;
- ownership domains and active parent transfer;
- composition-only fallbacks;
- registry projection;
- strict errors and migration examples.

Affected feature documents start with a short introduction, a local terms list, and a contract at
a glance. They link to `docs/interceptor.md` rather than repeating the complete model.

`docs/design/tracking-lifecycle.md` is rewritten around the Core ownership record, prospective
reservation, route forest, reconciliation, callback phase, concurrency, and memory release. It no
longer describes fallback mutation or `ContextInheritanceHandler` as lifecycle machinery.

## Performance Contract

Performance must be the same as or better than `master` for the normal one-global-context use case.
A repeatable regression requires redesign, not an assumption that the new semantics justify it.

Required properties are:

- a never-owned subject allocates no ownership record;
- the route-free admission word is inline executor storage and allocates no waiter or gate object;
- the common single parent is inline and allocates no membership collection;
- reference counts no longer use `IInterceptorSubject.Data` or boxed integers;
- the ordinary scalar Core write terminal is unchanged without a coordinator;
- unowned scalar setters retain their current direct fast path;
- only potentially structural writes use ownership admission;
- a warmed-up route-free structural write and a warmed-up owned structural write allocate zero
  managed bytes beyond allocations deliberately performed by configured custom interceptors;
- the activation record itself is the reentrant monitor target, so one domain adds no separate gate
  allocation, waiter object, task, closure, or per-write queue node;
- different ownership domains remain concurrent; only structural operations in the same
  lifecycle-coordinated domain are serialized;
- the common non-reentrant operation stores its pending reconciliation inline, and any traversal or
  reentrant overflow storage uses reusable thread-static pools;
- known scalar writes perform no ownership dispatch, reservation, or graph work;
- cached reads, method calls, and service resolution add no ownership allocation;
- the authority publication gate is absent from every stable resolution and scalar-interception
  path;
- compatible secondary-parent changes do not traverse the complete graph;
- structural traversal uses reusable pooled buffers;
- stable effective routes do not invalidate caches;
- only route changes and legal service topology changes invalidate dependent states.

Local development performs static field-layout, allocation, chain-shape, lock, and invalidation
analysis. In particular, it confirms that the normal structural path still enters the same two
uncontended monitor domains as `master`: lifecycle-domain synchronization and the generated backing
write's `SyncRoot`. They become explicitly nested domain-gate-then-`SyncRoot`; detectable reverse
entry is rejected deterministically before target-gate availability is inspected, and first-party
code never enters while holding another subject's `SyncRoot`. The lifecycle gate moves before
action selection and covers a larger region, but does not add a third monitor. Conservative
`object`, interface, and enumerable classification is measured as well as concrete subject shapes.
Local benchmark timings are diagnostic only. Before
the pull request is declared ready, the maintainer is asked to run the agreed comparisons on the
stable benchmark machine against both:

- exact stacked PR 1 base `a88b456ef681dc4505f1edce040b56fb83a6a034`;
- exact rebased `master` `55df0a84ebc19489cc114297b1e5fb6b4aa0b4b9`.

Existing unchanged registry, context-depth, construction, and structural-mutation rows compare PR 2
with both exact bases. Focused unowned structural-initialization and contended structural-write rows
use one temporary benchmark-only harness patch applied identically to the PR 2, PR 1, and `master`
checkouts on the stable machine. The patch touches only the benchmark project, and its exact hash and
application instructions are recorded with the result. It is not committed to PR 1 or treated as a
product dependency.

Results must show no repeatable regression outside control-row noise for normal one-global-context
workloads and no new steady-state allocation. The comparison includes an uncontended structural
write, graph attach and detach, and the existing concurrent structural stress workload so a serious
serialization regression is not hidden by scalar rows. Any signal above noise, including an
unacceptable construction cost from route-free admission or a material normal-workload loss from
the longer lifecycle critical section, reopens the design. The maintainer is asked before the
external handoff; development-machine numbers never accept the change.

## Test and Verification Design

Core tests cover:

- plain target validation;
- strict first attach, duplicate attach, exact detach, and reattach;
- explicit context reporting;
- lazy ownership allocation and final clearing;
- zero and one coordinator activation;
- reference-identity coordinator discovery rejecting equal-but-distinct instances while accepting
  one instance through repeated paths;
- one coordinator instance binding to one active domain and rebinding only after final quiescent
  release;
- failed first activation clearing its zero-lease record and binding so another domain can bind the
  coordinator immediately;
- no-coordinator explicit ownership retaining the ordinary cached property-write path;
- rejection of coordinator mutation after activation;
- activation racing direct service addition, conditional service creation, fallback addition, and
  mutation of a fallback target;
- inline and overflow parent membership behavior;
- allocation-free route-free admission and per-domain reentrant structural serialization;
- exact generation, stale cancel, stale clear, and route transfer;
- fallback composition without lifecycle callbacks;
- complete public provider facade, view, enumerator, reference-count, and write-context API
  snapshots;
- Core-to-Tracking package-boundary compilation without new friend access.

Generator and Dynamic tests cover:

- canonical structural classification matching for scalar, subject, object, interface, collection,
  dictionary, and ambiguous enumerable properties;
- a scalar null-context setter retaining the existing direct generated shape;
- a potentially structural null-context setter locking `SyncRoot` and re-reading `_context`;
- first executor publication racing that setter in both orders;
- an already-materialized route-free executor draining its cached write before adoption and choosing
  a fresh transition or routed action afterward;
- generated and Dynamic context-taking constructors using strict explicit attachment;
- no new steady-state allocation after an executor is present.

Tracking tests cover:

- first parent attach and final detach;
- several parents in one domain;
- repeated collection occurrences and index refresh;
- deterministic earliest-parent selection and transfer;
- explicit-to-inherited transfer without lifecycle churn;
- cross-domain root, child, and descendant rejection before property commit;
- a reserved unowned child accepting no later incompatible descendant before the parent commit;
- cycles with one anchor, several anchors, and final anchor removal;
- shared DAGs and back-edge route selection;
- callback-order and service-visibility characterization before, at, and after the recursive phase
  for attach, detach, registry, parents, properties, and derived initialization;
- superseded structural commits producing the current final net reconciliation rather than a
  per-commit callback journal;
- different-thread structural assignments serializing so the previous visible child detaches before
  the final child attaches;
- same-thread reentrant structural assignments completing without deadlock, with stale callback
  tails stopped by generation checks;
- a test-only provider double proving prepare, postcommit unwind, and Core `finally` finalization
  through the complete public package-infrastructure contract without advertising application
  implementations as supported;
- a route-free fallback-composed coordinator acting as a transparent write interceptor while the
  same instance's active domain performs lifecycle work concurrently;
- callback contract violations without rollback machinery;
- concurrent write, reparent, attach, detach, and stale-reservation schedules;
- weak-reference memory release.

Connector and factory tests additionally cover a route-free `ISubjectFactory` result, a custom
factory returning an explicitly attached subject, DI constructor selection, rejection before parent
publication, and successful recursive population only after the parent property owns the child.

Consumer verification includes generator snapshots, Dynamic, Registry, Hosting, Connectors,
non-integration OPC UA, HomeBlaze Services, public API snapshots, the full non-integration solution
suite, solution build, and pack.

Because connector-created child publication changes, the stable-machine handoff includes the
agreed Connector Tester matrix in addition to benchmarks: 100 cycles per connector, every rotating
chaos profile, and server plus both-client structural mutation at rate 1. Long-running benchmark
and Connector Tester work is not treated as authoritative on the development machine.

Tests follow repository naming and Arrange, Act, Assert conventions. Concurrency tests use
barriers, events, or other deterministic synchronization and never hardcoded sleeps.

## Capability Changes

PR 2 removes these capabilities:

- fallback mutation as implicit attach or detach;
- several explicit root attachments for one subject;
- explicit attachment to a subject executor;
- optional shallow lifecycle without recursive inheritance;
- several lifecycle coordinators in one effective ownership context;
- one lifecycle coordinator instance serving several active ownership domains;
- one subject participating in unrelated ownership domains;
- temporary publication of an incompatible child;
- aggregation of every parent branch as an ownership route;
- concurrent structural execution inside one ownership domain; operations are serialized and the
  final committed value wins;
- synchronously attaching, detaching, or changing the lifecycle coordinator from inside a
  route-free structural interceptor chain before its terminal completes;
- synchronously entering a different ownership domain, or entering any domain from a
  `TryAddService` predicate/factory callback or the initiating subject's `SyncRoot`; these calls are
  rejected deterministically before target-gate availability is inspected, while same-domain
  reentrancy remains supported;
- invoking a domain-gated operation while manually holding a different subject's public `SyncRoot`;
- retaining an unanchored cycle solely through internal reference counts.

PR 2 preserves:

- pure fallback service composition;
- nonunique branch services and interceptors;
- late nonunique service additions and invalidation;
- multiple compatible parents;
- repeated object-reference paths;
- cycles and shared DAGs;
- several disconnected explicit roots in one ownership domain;
- subject-local executor services;
- current single-context callback ordering.

## Release Boundary

PR 2 is independently releasable on PR 1 as a coordinated breaking release. Core, Tracking, source
generation, Dynamic, Registry, connector child factories, OPC UA loaders, HomeBlaze migrations,
tests, and affected documentation ship together.

The pull request contains no unique-authority framework beyond the permanent lifecycle activation
seam and no hosting state-machine changes. PR 3 generalizes authority stability without replacing
the ownership ledger. PR 4 consumes the completed ownership and authority contracts.

The old #419 production implementation is not transplanted wholesale. Reproduced schedules,
callback-order characterization, and focused regression tests may be reused. The finished pull
request is reviewed both alone and stacked with PR 1.
