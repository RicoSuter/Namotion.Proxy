# Effective Ownership-Route State Design

**Date:** 2026-08-17

**Status:** Approved

**Stack position:** PR 1 on `master`

## Purpose

The final lifecycle model needs one effective ownership route per subject executor, separate from
public fallback composition. Introducing attachment callbacks before the complete root and property
membership ledger exists would create a transitional model that cannot safely handle reentrancy,
plural lifecycle interceptors, or attach and final-detach races.

PR 1 therefore adds only the permanent Core context-state mechanism. It can represent and resolve
one internal ownership route, but no production code installs a route yet. Existing lifecycle and
fallback behavior remains unchanged until PR 2 lands the complete semantic change.

## Concepts and Terms

- **Fallback composition:** a public context relationship created by `AddFallbackContext`. On
  `master`, `InterceptorExecutor` also gives it lifecycle side effects. PR 1 does not change those
  side effects.
- **Ownership route:** one internal context-resolution relationship that PR 2 will use for explicit
  attachment or the active parent branch. It is stored separately from fallbacks.
- **Ownership domain:** a plain configured context whose reference identity names one lifecycle
  domain. An inherited route can target a parent executor while retaining the root configured
  context as its domain identity.
- **Route descriptor:** one immutable object holding the route target and ownership-domain identity.
  The descriptor's own reference identity is the generation token used for exact transitions.
- **Using context:** a context whose resolution depends on another context. Reverse using-context
  registration drives downstream cache invalidation.

## Goals

- Represent one ownership route independently from public fallbacks.
- Publish the complete route atomically with the immutable context state.
- Resolve local services, public fallbacks, then the ownership route.
- Keep public composition and ownership-route relationships independently removable when they
  target the same context.
- Prevent a stale clear from removing a later same-target route generation.
- Preserve delegation, repeated-path deduplication, cycle termination, deep-graph safety, and
  downstream invalidation.
- Keep existing route-free context objects and states at their current instance size.
- Add no public API and change no production behavior.

## Non-goals

- Adding subject attachment APIs.
- Making fallbacks composition-only.
- Calling lifecycle interceptors through the new route.
- Changing generated or dynamic constructors.
- Changing Tracking, Registry, connectors, OPC UA, Hosting, or HomeBlaze.
- Defining root and property membership or parent transfer.
- Enforcing a single ownership domain or unique context authorities.
- Freezing any context mutation.
- Renaming or separating `InterceptorExecutor` from `IInterceptorSubjectContext`.

## Architecture

### Immutable route descriptor

Core adds a nested internal immutable descriptor with exactly two fields:

```csharp
internal sealed class ContextOwnershipRoute
{
    internal ContextOwnershipRoute(
        InterceptorSubjectContext target,
        InterceptorSubjectContext ownershipDomain);

    internal InterceptorSubjectContext Target { get; }

    internal InterceptorSubjectContext OwnershipDomain { get; }
}
```

Both references are required. The descriptor object is also the transition generation. A later
route that happens to use the same target and domain has a different descriptor identity, so an old
operation cannot clear it accidentally.

The descriptor does not contain lifecycle callbacks, parent references, reference counts, or
reservations. Those belong to PR 2's ownership ledger.

### Route-free and routed context states

The existing `ContextState` remains the route-free state representation. It keeps the same instance
fields and therefore the same object size. It becomes a base class for a private routed state that
adds exactly one descriptor reference:

```csharp
private sealed class RoutedContextState : ContextState
{
    internal readonly ContextOwnershipRoute OwnershipRoute;
}
```

A factory creates `ContextState` when no route exists and `RoutedContextState` when a route exists.
Every service, fallback, invalidation, and cache-reset publication preserves the current descriptor.
No separate mutable route field exists on the context.

This shape has two purposes:

1. services, fallbacks, the route, delegation, and all derived caches are one atomically published
   snapshot;
2. contexts that never receive an ownership route pay no extra state bytes and allocate no route
   descriptor.

### Internal transition

Core exposes one internal compare-by-descriptor operation:

```csharp
internal bool TryChangeOwnershipRoute(
    ContextOwnershipRoute? expected,
    ContextOwnershipRoute? replacement);
```

The operation linearizes under the context's existing mutation lock:

- install uses `expected: null` and a new descriptor;
- transfer uses the exact current descriptor as `expected` and a new descriptor as `replacement`;
- clear uses the exact current descriptor as `expected` and `replacement: null`;
- an identity mismatch returns `false` without publication or invalidation;
- passing the same descriptor as expected and replacement returns `true` as a no-op.

The descriptor constructor and transition remain internal. PR 1 tests are their only caller. PR 2's
ownership ledger becomes the production caller without changing this contract.

## Resolution Semantics

For every entered context, service traversal remains depth-first and gathers in this order:

1. services on the entered context;
2. public fallback contexts in insertion order;
3. the ownership-route target, when present.

The existing visited set deduplicates contexts reached through repeated paths. If a public fallback
and the ownership route target the same context, its services appear once at the public fallback's
earlier position.

Ordering attributes continue to reorder the gathered services by declared dependencies. Route
order breaks only otherwise unconstrained ties.

### Delegation

An empty context can delegate directly when its distinct reachable target is exactly one context:

- one public fallback and no ownership route delegates to that fallback as today;
- one ownership route and no public fallback delegates to the route target;
- one public fallback and an ownership route to the same target delegate to that target;
- different public-fallback and ownership-route targets require the normal service walk.

The existing resolved-terminal cache remains attached to the immutable state. Route publication
creates a cache-free state, so a terminal recorded from an older topology cannot survive a route
change.

### Empty-state behavior

`ContextState.IsEmpty` keeps its existing direct check over services and public fallbacks, with no
new route-free branch. A route-only state has a delegation target and is resolved before
`GetServicesFromState` reaches the empty-state check. It therefore does not allocate a service cache
on the source state. This caller invariant is documented next to that check.

## Reverse Dependency and Invalidation

The target context's existing `_usedByContexts` set records a boolean dependency, not a relationship
count. PR 1 preserves this representation and makes registration depend on the union of public
fallbacks and the ownership route.

For a route install or transfer:

1. register the source in the new target's using set before publishing the new state;
2. publish the new immutable state;
3. remove the source from the old target only when the published state has no fallback and no
   ownership route to that old target;
4. invalidate every context that resolves through the source.

For fallback removal, the source remains registered in the target while an ownership route still
targets it. For route removal, it remains registered while a public fallback still targets it.

This keeps the using set a superset of the true dependency set through every transition. An extra
entry can cause a harmless invalidation. A missing entry can leave a compiled chain permanently
stale and is therefore forbidden.

## Concurrency and Failure Semantics

- The existing `_mutationLock` serializes services, fallbacks, and ownership-route transitions for
  one context.
- Queries take no context lock. They pin one immutable state with the existing volatile read.
- A route descriptor and all its fields become visible in one state publication. Readers cannot
  observe a target without its domain identity or the reverse.
- The exact expected descriptor prevents an old clear or transfer from acting on a later route
  generation, including a later generation with the same target and domain.
- Reverse using-set locks remain leaf locks. No path takes a second context mutation lock.
- Route construction and state construction happen before publication. An allocation failure leaves
  the old state and relationships unchanged.
- The ownership-route transition executes no user code, service factory, lifecycle callback, or
  virtual context method while the mutation lock is held. Existing service registration semantics
  are outside this statement.
- Internal route graphs use the existing visited sets and iterative worklists. Cycles terminate and
  deep graphs do not recurse.

## Public and Consumer Behavior

PR 1 adds no public type, member, or capability. `IInterceptorSubjectContext`, generated code,
`InterceptorExecutor`, and all feature packages behave as on `master` because no production path
creates an ownership route.

Public API snapshots must remain unchanged. Existing binaries and generated model assemblies do not
need rebuilding specifically for PR 1.

## Performance Contract

For a route-free context:

- `InterceptorSubjectContext` instance fields do not change;
- the base `ContextState` instance fields and object size do not change;
- initial state, service mutation, fallback mutation, and cache invalidation allocate the same state
  shape as `master`;
- steady-state intercepted read, write, invoke, delegation, and cached service-resolution paths do
  not inspect a route descriptor or allocate new objects;
- first uncached service traversal performs at most one routed-state type check per entered context.

Route-free production paths allocate no descriptor. Each attempted replacement supplies one
descriptor, including an attempt that loses the expected-descriptor comparison. Only a successful
route publication uses the one-reference-larger routed state. PR 2 must compare that cost with the
one-element fallback representation it replaces.

Local verification uses static layout and hot-path inspection. Local benchmark timings are
diagnostic only. Before PR finalization, the maintainer runs the approved comparison against the
exact base commit on the stable benchmark machine. The acceptance criterion is no repeatable timing
regression outside control-row noise and no new steady-state allocation.

## Test Design

Core tests cover:

- route-only resolution;
- local, fallback, then route ordering;
- ordering attributes across all routes;
- repeated target deduplication when fallback and route share a target;
- independent removal in both orders when fallback and route share a target;
- downstream cache invalidation after service mutation through the relationship that remains;
- route transfer and exact-descriptor stale-clear rejection;
- route-only delegation and delegation-cache invalidation;
- cycles containing fallbacks and ownership routes;
- a deep route chain without recursion;
- concurrent route install, transfer, clear, service resolution, and downstream invalidation;
- unchanged public API snapshots;
- unchanged focused and full non-integration suites.

Tests follow repository naming and Arrange, Act, Assert conventions. Deterministic concurrency tests
use barriers or events rather than hardcoded waits.

## Documentation

PR 1 adds `docs/design/context-resolution.md` with an introduction, the terms used by that document,
the route-order and publication invariants, and the cache/invalidation mechanics. It describes the
ownership route as an internal foundation not yet used by public attachment.

User-facing attachment and lifecycle documentation does not change until PR 2 changes public
semantics. PR 2 makes `docs/interceptor.md` the canonical user-facing ownership and context model and
cross-references it from affected feature pages.

## Release Boundary

PR 1 is independently releasable and behavior-neutral for consumers. Its diff is limited to Core
context state and traversal, focused Core tests, the internal design document, and the roadmap. It
does not touch source generation, Tracking, Registry, Hosting, connectors, OPC UA, or HomeBlaze.

PR 2 can use the internal descriptor and transition without replacing them. PR 3 adds authority
validation around the established ownership route. PR 4 consumes the resulting ownership and
authority contracts.
