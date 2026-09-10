# Context Resolution

Namotion.Interceptor contexts resolve interceptors and coordination services through an immutable,
copy-on-write state. This document defines the internal relationship types, traversal order, and
cache invalidation rules used by Core. The ownership route is an internal foundation in the first
pull request and receives its production lifecycle owner in the following attachment pull request.

## Concepts and terms

- **Local services:** Services registered directly on the context being queried.
- **Fallback composition:** Public service composition created by `AddFallbackContext`. Existing
  lifecycle side effects remain until the attachment pull request separates them.
- **Ownership route:** One internal resolution relationship used later by explicit attachment or an
  active parent branch.
- **Ownership domain:** The plain configured context whose reference identity names one lifecycle
  domain.
- **Using context:** A context whose resolution depends on another context and must be invalidated
  when that dependency changes.

## Contract at a glance

- A query pins one immutable state and takes no context mutation lock.
- Resolution visits local services, public fallbacks in insertion order, then one ownership route.
- Ordering attributes override route order only where they declare a dependency.
- Repeated contexts and service instances are returned once according to the existing visited and
  distinct rules.
- Services, fallbacks, the ownership route, delegation, and caches belong to one published state.
- A route change compares the exact previous descriptor instance before it publishes.
- Reverse dependency registration remains while either a fallback or ownership route uses a target.
- Topology changes publish a cache-free state and invalidate every upstream using context.
- Traversal and invalidation are iterative and terminate on cyclic graphs.

## Immutable state and publication

Route-free contexts use the existing `ContextState`. A context with an ownership route uses a
derived state containing one immutable route descriptor. The descriptor contains the target and
ownership-domain references and also serves as the transition generation token.

Mutators serialize on one context's mutation lock, build the complete replacement state, register a
new reverse dependency before publication, publish once, conditionally remove the old reverse
dependency, and invalidate upstream contexts after releasing the mutation lock. Queries continue to
use one volatile state read.

## Resolution and delegation

The service walk is depth-first. Each entered context contributes local services, then each public
fallback, then its ownership route. The existing visited set cuts cycles and gives the earliest
route to a repeated context precedence.

An empty context delegates directly when it has one distinct target: one fallback, one ownership
route, or both relationships to the same target. Different fallback and ownership targets require
the normal service walk. A pure delegation cycle raises the existing delegation-cycle exception.

## Reverse dependencies and invalidation

`_usedByContexts` represents whether a source depends on a target, not how many relationship kinds
connect them. Removing a fallback must retain the reverse entry while an ownership route still uses
the target. Clearing a route must retain it while a fallback still uses the target.

Registration occurs before publication so the reverse set is always a superset of the true using
set. Conditional removal occurs after publication. An extra entry can cause a harmless invalidation;
a missing entry can preserve a stale compiled chain and is forbidden.

## Performance

Route-free contexts keep the existing base state layout. Cached service queries and steady-state
intercepted reads, writes, and invocations do not inspect an ownership descriptor. A route attempt
allocates its descriptor before the exact comparison; only a successful route publication uses the
derived routed state.

Timing comparisons on a development machine are diagnostic. Final performance acceptance uses the
stable benchmark machine and compares the exact pull request head with its exact base commit.
