# Single Effective Context Stack Roadmap

**Date:** 2026-08-17

**Status:** Approved direction, detailed designs are approved one pull request at a time

## Purpose

Namotion.Interceptor currently uses fallback contexts for three separate jobs:

1. composing services;
2. attaching a subject to lifecycle handling;
3. inheriting a parent subject's effective context.

That overlap lets one local topology mutation change service resolution, lifecycle ownership,
reference-derived membership, cache invalidation, and authority selection at the same time. The
result is difficult to make atomic and difficult for consumers to understand.

This roadmap separates those responsibilities into four stacked, independently releasable pull
requests:

```text
master
  -> PR 1: internal effective ownership-route state
       -> PR 2: explicit attachment and one effective ownership route (#419 rewrite)
            -> PR 3: stable unique authorities (#472 rewrite)
                 -> PR 4: hosted lifecycle coordination (#440 rewrite)
```

The current experimental #419, #472, and #440 branches remain sources of tests and reproduced
schedules. Their production implementations are not transplanted wholesale.

PR 1 is intentionally behavior-neutral. It lands only the permanent context-resolution mechanism
needed by PR 2. PR 2 then changes attachment and inheritance as one complete semantic unit. This
avoids a transitional lifecycle model that would be unsafe with plural lifecycle interceptors and
would be removed by the next pull request.

## Shared Model

### Separate concepts

The final model keeps four concepts distinct:

- **Object-reference graph:** relationships formed by subject-valued properties, collections, and
  dictionaries. The object model is the source of truth for these edges.
- **Lifecycle ownership:** the lifecycle domain responsible for a subject. Explicit attachment can
  establish ownership while the subject has no incoming property reference and a reference count
  of zero. Property references can establish or retain inherited ownership.
- **Context resolution:** services and interceptors registered on the subject executor, public
  composition fallbacks, and the subject's one effective ownership route.
- **Registry projection:** an optional index of lifecycle-owned subjects and their
  object-reference relationships. It observes lifecycle changes. It does not create ownership and
  is not the source of truth for the object graph.

One ownership domain may manage several disconnected explicit roots. They share lifecycle,
registry, transaction, and hosting authorities without becoming one object-reference graph.

### Subject executor

Every subject keeps its own `InterceptorExecutor`. The executor remains an
`IInterceptorSubjectContext` and holds subject-local interception state and services. A child does
not replace its executor with the parent's context. Instead, its one effective ownership route
points through the active parent executor, which lets subject-local branch services flow to that
subject and its descendants.

The current type and public names remain unchanged. Separating or renaming the executor would add
API and generator churn without simplifying the ownership model.

### Context relationships

The final model distinguishes three relationships:

- **Explicit root attachment:** intentionally gives one subject root ownership in a supplied plain
  configured context.
- **Branch inheritance:** gives a subject one effective route from its active parent branch when
  explicit root ownership does not supply the route.
- **Fallback composition:** explicitly aggregates services and interceptors. It has no lifecycle
  meaning.

Each subject has at most one effective ownership route. An explicit root owns it when present.
Otherwise, the earliest surviving compatible parent reference owns it. Public fallback composition
never owns or changes lifecycle membership.

The plain configured context supplied to explicit root attachment is the ownership-domain identity
token, compared by reference. Descendants copy that identity even though their effective route
targets a distinct parent executor. Several parents are compatible only when they carry the same
identity.

### Service resolution

Resolution gathers services in this route order:

1. services registered on the current context;
2. explicitly composed fallback contexts;
3. the one effective ownership route, when present.

Ordering attributes apply across the complete gathered service set. Route order breaks only ties
that have no declared dependency.

Nonunique services can aggregate from all reachable paths. This preserves branch interceptors,
ordered lifecycle handlers, and other intentionally plural facilities. Reaching the same service
instance through several paths still produces it once.

### Runtime mutation and performance

Late nonunique service registration remains supported. Adding one publishes a new immutable context
state and invalidates dependent compiled chains as it does today.

PR 3 makes authority-bearing configuration stable after activation. A fresh branch can still link
to an active parent, and lifecycle transitions can change the one internal ownership route.

The stable path does not run an invalidation walk. Steady-state intercepted reads, writes, method
invocations, and cached service resolution remain allocation-free apart from work already required
by configured interceptors.

## Pull Request 1: Internal Effective Ownership-Route State

### Contract

Core can represent one internal ownership route independently from public fallback composition.
The route participates in service resolution, delegation, cycle handling, and reverse cache
invalidation. No production code creates such a route in this pull request.

### Included

- Add one immutable internal route descriptor containing a target context and ownership-domain
  identity.
- Publish the descriptor as part of the same immutable context state as services and fallbacks.
- Keep route-free contexts on the existing base state shape. Allocate the derived routed state only
  for a successful route publication. Route-free production paths create no route descriptors.
- Add an internal compare-by-descriptor transition that can install, transfer, or clear a route
  without an ABA-prone context-only clear.
- Resolve the ownership route after public fallbacks.
- Keep public composition and the internal route independently removable even when both target the
  same context.
- Preserve reverse invalidation until the final relationship to a target is removed.
- Cover ordering, repeated paths, delegation, cycles, deep graphs, invalidation, and concurrent
  route changes.
- Add an internal context-resolution design document using the agreed terminology.

### Capability removed

None. There is no public API change and no production caller of the new internal route transition.
Fallback composition and current lifecycle behavior remain unchanged.

### Release contract

PR 1 is independently releasable as an internal foundation. Existing binaries and generated models
continue to behave as on `master`. Public API snapshots do not change.

## Pull Request 2: Explicit Attachment and One Effective Ownership Route

### Contract

Fallback composition has no lifecycle meaning. A subject has at most one explicit root attachment,
one lifecycle-ownership domain, and one effective ownership route. Root membership and
property-reference membership are represented separately and reconciled through one permanent
ledger and reservation protocol.

### Included

- Add strict `AttachToContext`, `DetachFromContext`, and conventional boolean/out
  `TryGetAttachContext` subject APIs.
- Make `AddFallbackContext` and `RemoveFallbackContext` pure service composition operations.
- Resolve zero or one lifecycle coordinator, capture its identity for the active ownership domain,
  and reject mutations that would change it while the domain owns subjects.
- Replace Tracking's empty-property-set root sentinel with separate explicit-root and property-edge
  membership.
- Add the complete ordered parent-reference ledger, generation-aware transition state, and
  prospective reservation before any property value commits.
- Serialize potentially structural operations through one reentrant gate per ownership domain;
  different domains remain independent and the final committed property value wins.
- Bind one lifecycle coordinator instance to at most one active ownership domain so its mutable
  reconciliation state is protected by exactly one domain gate.
- Add one compact, allocation-free route-free admission handshake so a materialized unowned
  executor drains its old cached write before adoption; leave the unowned scalar fast path
  unchanged.
- Move the canonical conservative subject-property classifier into Core, keep the existing Tracking
  extensions as forwarders, and feed generated, Dynamic, Core admission, and Tracking from that one
  contract.
- Support several parent references only when they share one ownership-domain identity.
- Keep the earliest surviving compatible parent active and transfer deterministically when its
  final reference disappears.
- Keep explicit root ownership active when compatible parent references exist. Transfer to a
  surviving parent without final lifecycle-detach callbacks when the root is removed.
- Reject incompatible root or parent ownership before lifecycle membership changes or an
  incompatible property value commits.
- Preserve subject-local services and interceptors as branch overlays for the subject and its
  descendants.
- Make `WithLifecycle()` include recursive inheritance and remove `WithContextInheritance()` and
  the functional `ContextInheritanceHandler` capability.
- Make `ILifecycleInterceptor` formally inherit `IWriteInterceptor` and use complete public,
  stack-only Core operation facades for Tracking ownership coordination. Add no new friend-assembly
  access between the published packages. Treat this as changeable package infrastructure and
  support only the built-in coordinator, not application implementations.
- Keep a coordinator reached by route-free fallback composition transparent when no committed Core
  ownership operation exists; lifecycle state is touched only under its bound domain gate.
- Reuse one short Core authority-publication gate for domain activation, reservations, routes, and
  authority-relevant context publication. Keep resolution, scalar writes, callbacks, getters, and
  service factories outside that gate; PR 3 extends the same activation record.
- Merge recursive inheritance into `LifecycleInterceptor` at the former
  `ContextInheritanceHandler` phase so callback ordering and service visibility remain unchanged.
- Update generated and dynamic constructors, first-party root call sites, connectors, OPC UA, and
  HomeBlaze in the same coordinated change.
- Publish connector-created children through the parent edge before recursive population and
  verify the committed child before continuing.
- Preserve the ordinary callback sequence for successful single-context consumers.
- Update user-facing context, lifecycle, registry, connector, and migration documentation that the
  semantic change affects.

### Capability removed

- Adding or removing a fallback no longer attaches or detaches a subject.
- A subject can no longer use several fallback calls as several lifecycle roots.
- A subject can no longer have several explicit root attachments, including repeated attachment to
  the same context without an intervening detach.
- Public fallback composition no longer establishes property inheritance.
- A subject can no longer aggregate unrelated parent ownership domains or all parent branch
  overlays.
- Reparenting into an incompatible ownership domain no longer succeeds temporarily.
- A property-child factory must return a route-free, unowned child. It cannot return an explicitly
  attached or inherited subject for the caller to steal or silently retain as an independent root.
- Concurrent structural operations inside one ownership domain no longer commit and reconcile in
  parallel. They serialize through one reentrant domain gate and the final committed value wins.
- Explicit ownership transitions and coordinator-changing context mutations cannot reenter from an
  unfinished route-free structural interceptor chain.
- One lifecycle coordinator instance cannot serve several active ownership domains.
- A nested different-domain operation, or a domain operation begun from a service
  predicate/factory callback scope or its initiating subject's `SyncRoot`, is rejected
  deterministically before target-gate availability is inspected. Ordinary contention waits, and
  same-domain reentrancy remains supported.
- Consumers and custom interceptors cannot start domain-gated work while manually holding another
  subject's `SyncRoot`; first-party code never uses that lock order.
- Explicit attachment to another subject executor is rejected. Explicit roots attach to a plain
  configured context; property children inherit through their parent.
- Shallow lifecycle without recursive property inheritance is no longer configurable.

Fallback composition, cycles, repeated service paths, nonunique services, late nonunique service
registration, and branch-local services remain supported.

### Release contract

PR 2 is independently releasable on PR 1 as a coordinated binary-semantic breaking release. The
runtime, source generator, Dynamic package, connectors, OPC UA loader, and HomeBlaze call sites ship
together. Consumer model assemblies generated against the old constructor behavior must be rebuilt.

PR 2 is the complete #419 replacement. It does not depend on the unique-authority marker or hosting
changes.

## Pull Request 3: Stable Unique Authorities

### Contract

Every active effective context exposes at most one distinct instance per declared authority
contract, and an authority-bearing mutation cannot publish an invalid active topology.

### Included

- Introduce `IUniqueContextService<TContract>`.
- Validate authority identity across the complete effective context using reference identity.
- Allow the same instance through repeated paths.
- Mark lifecycle, registry, transaction, and other individually audited authority services unique.
- Make authority-establishing configuration helpers reject incompatibility before factories or
  other side effects run.
- Audit every built-in `WithX()` helper and every first-party composition site.
- Preserve intentionally plural facilities such as source monitors without manufacturing several
  lifecycle authorities.
- Freeze only mutations that would change an active unique-authority map.
- Keep late nonunique additions and their normal invalidation behavior legal.
- Move first-party late authority configuration to bootstrap before activation.

### Capability removed

- An effective context can no longer expose two distinct instances of one unique authority
  contract.
- Unique authorities can no longer be added, replaced, or introduced through a new route after the
  affected context is active.
- Feature helpers that establish an authority can no longer be used late.

Independent ownership domains may still use different authorities. Nonunique branch services and
interceptors remain mutable.

### Release contract

PR 3 is independently releasable on PR 2. It replaces #472 and settles the transaction-authority
ambiguity tracked by issue #466. It does not change hosted-service behavior.

## Pull Request 4: Hosted Lifecycle Coordination

### Contract

One hosting authority per ownership domain serializes start and stop for each hosted target and
defines one atomic drain boundary for host shutdown.

### Included

- Mark the hosting handler as the ownership domain's unique hosting authority.
- Serialize transitions per hosted target while allowing unrelated targets to progress
  concurrently.
- Define awaited attach, awaited detach, refusal, cancellation, failure, and drain completion.
- Take the drain snapshot under the same lock that accepts target starts.
- Observe and log drain and cancellation-callback failures without losing the shared drain.
- Preserve caller ownership of hosted-service instances and subject objects.
- Keep public factory APIs out until a production consumer ships with them.

### Capability removed

- Several hosting handlers can no longer compete for one target inside one ownership domain.
- A target start is no longer accepted after draining begins.
- Accidental handler-wide ordering across unrelated targets is not a contract.

### Release contract

PR 4 is independently releasable on PR 3. It replaces #440 and changes only hosted lifecycle
coordination plus the small tracking seam required for already-attached targets.

## Documentation Strategy

Documentation evolves with the behavior it describes:

| Pull request | Documentation outcome |
| --- | --- |
| PR 1 | Add the internal context-resolution terms, invariants, route order, and cache rules. |
| PR 2 | Make `docs/interceptor.md` the canonical user-facing ownership and context model. Explain explicit roots, property membership, reference counts, parent transfer, composition-only fallbacks, and migration. |
| PR 3 | Explain unique authorities, activation, mutation restrictions, and helper configuration timing. |
| PR 4 | Explain one hosting authority, per-target transitions, signalling, cancellation, and drain. |

Affected user-facing feature documents use this lightweight structure:

1. an opening paragraph stating what the feature does and when to use it;
2. a short **Concepts and terms** list containing only vocabulary used by that document;
3. a short **Contract at a glance** list stating important guarantees and limits;
4. detailed setup, behavior, examples, and edge cases;
5. links to the canonical model instead of repeating its complete glossary.

Internal documents under `docs/design/` use purpose, terms, invariants, and mechanics. Each pull
request restructures only documents it materially changes. After PR 4, a repository-wide audit can
identify unrelated legacy pages for a dedicated documentation change.

## Review, Verification, and Delivery

Each pull request:

1. receives an approved detailed design and implementation plan before production changes;
2. is implemented test-first from the preceding exact base commit;
3. preserves callback-order characterization unless its design explicitly changes the contract;
4. runs focused consumer suites, Public API snapshots, and the full non-integration suite;
5. runs connector integration and Connector Tester coverage when connector behavior changes, with
   the exact long-running scope agreed during that pull request's planning;
6. receives an independent whole-PR review;
7. updates its title and body to describe only its actual scope;
8. completes local static hot-path and allocation analysis;
9. records the exact head and base commits for external performance verification;
10. asks the maintainer before handing benchmark work to the stable external machine.

PR 1 is expected to be a small internal net addition. The combined PR 1 plus PR 2 review also
includes a production simplification audit. Every new production block must enforce a named
invariant, legacy fallback and lifecycle coupling must be deleted, and no compatibility bridge or
temporary multi-domain recovery path may survive into the pair.

Local benchmark timing is diagnostic only on the development machine. It can catch an obvious
regression but does not accept or reject a performance change.

The final performance acceptance criterion for every pull request is:

> For the normal one-global-context use case, the pull request introduces no repeatable performance
> regression relative to its exact base commit. Steady-state intercepted reads, scalar writes,
> method invocations, cached service resolution, and warmed-up ordinary structural writes add no
> ownership allocations. Targeted comparisons on the stable benchmark machine show no repeatable
> timing regression outside contemporaneous control-row noise. A regression above noise requires
> redesign or explicit maintainer approval.

The maintainer supplies the external benchmark result before a pull request is finalized. PR 1's
route-free state shape and hot paths also receive static inspection because the new route has no
production caller yet.

PR 2 is additionally compared with exact `master`, because the semantic rewrite is expected to
remove enough legacy fallback and reference-count machinery to keep the normal one-global-context
case at least as fast as the released baseline. Its stable-machine handoff compares both the exact
stacked PR 1 base and exact master. Focused unowned structural-initialization and contended
structural-write rows use one temporary benchmark-only harness patch applied identically to all
three checkouts on the stable machine. The patch touches only the benchmark project and is recorded
with the result; it is not committed to PR 1. The handoff also covers ordinary and concurrent
structural mutation so per-domain serialization cannot hide a meaningful regression. A repeatable
regression reopens the design.

## Whole-Stack Success Criteria

The stack is complete when:

- one-global-context and HomeBlaze consumers retain expected behavior;
- branch-local services and interceptors reach descendants and remain sibling-isolated;
- fallback composition never invokes lifecycle callbacks;
- every subject has at most one explicit root attachment and one effective ownership route;
- active effective contexts cannot publish conflicting unique authorities;
- late nonunique services continue to invalidate dependent caches;
- hosted targets have deterministic start, stop, detach, cancellation, and drain completion;
- documentation consistently distinguishes object-reference relationships, lifecycle ownership,
  context resolution, ownership domains, and registry projection;
- stable property and service access remains allocation-free apart from configured interceptor
  work.
