# OPC UA client loader: why the load works the way it does

Maintainer notes for the address space loader in `src/Namotion.Interceptor.OpcUa/Client/`. Consumer-facing behaviour is in [OPC UA Client](../connectors-opcua-client.md); this covers the reasoning behind it, which is not recoverable from the code.

The moving parts are `OpcUaSubjectLoader` (the pipeline), `OpcUaAttributeLoader` (the attribute rounds), `OpcUaLoadContext` (per-load state, staging and commit) and `OpcUaSessionExtensions` (the batched browse and read primitives).

## Discovery runs on attached subjects

A load could in principle build a detached object graph and hand it to the model at the end. It does not, because almost everything the loader does per node needs the registry's `RegisteredSubject`, and the registry only knows attached subjects.

- The mapper resolves a browse reference to a property against `RegisteredSubject`, so an unregistered subject has no properties to map to.
- A dynamic property is created with `RegisteredSubject.AddProperty` and a dynamic attribute with `RegisteredSubjectProperty.AddAttribute`. There is no off-graph equivalent.
- Attribute matching reads `RegisteredSubjectProperty.Attributes`, which is registry state as well.

The secondary reason is reuse. `RegisteredSubjectProperty.Children` is written only by `SubjectRegistry`, and the loader reads it to bind an existing child subject rather than constructing a new one, both for single references and for collection and dictionary elements. Discovering off-graph would silently lose that reuse and rebuild the subtree on every load.

So a newly constructed subject is attached to its discovering parent's context by `OpcUaLoadContext.RegisterStagedSubject` before it is browsed, and that link is what the rollback path has to undo.

## Staging is keyed to the discovering parent

`RegisterStagedSubject` adds the immediate parent's context as a fallback context, not the root context. In the ordinary tree case that link is exactly the one `ContextInheritanceHandler` removes when the subject's last property reference goes away, so the two agree and nothing is left behind. The handler adds no link of its own for a staged subject, because its add is gated on the subject not already being context-attached, which staging has already made true.

Re-keying the link to the parent that finally binds the subject was implemented and rejected. Staging-parent keying is what keeps the fallback chain acyclic. In an address space where two subjects reference each other, re-keying makes each one's context delegate to the other's, and the next context resolution throws the delegation cycle error from `InterceptorSubjectContext`. `OpcUaSubjectLoaderTests.WhenAddressSpaceHasCycle_ThenLoaderTerminatesWithoutInfiniteRecursion` is the test that catches it.

## The commit order is load-bearing

`Commit` applies claims before bindings, so an observer that sees a new child appear finds that child's leaves already source-owned, and applies bindings deepest level first, so a subject becomes reachable from the root only once its own subtree is bound. Neither order is an implementation detail to be reversed. The one mutation that cannot be deferred is the creation of dynamic properties and attributes, because the registry has to hand back the property that is then browsed and monitored.

## Rollback sheds what nothing references

Rollback keeps a reference count guard even though deferring the bindings means a staged subject normally has no references left on the failure path. Removing the link from a subject that a property still points at would break a graph invariant rather than restore one: the subject would be evicted from the registry while the model still referenced it, the next load would reuse it from `RegisteredSubjectProperty.Children` without re-staging it, and the loader would then skip it as unregistered, leaving the subtree unmonitored for good.

It walks the staged subjects deepest first because a nested one reaches the lifecycle interceptor through its parent's fallback chain, so removing the parent's link first would cut the child off from the handler that has to see its detach.

## The detach path takes no lock

`OpcUaSubjectClientSource.RemoveItemsForSubject` runs from the synchronous subject-detach callback, which the lifecycle interceptor raises while holding its attached-subject lock. Taking the source's structure lock there deadlocks in two different ways.

Same-thread: the initial load holds the structure lock across the whole load, and a failed load's rollback detaches its staged subjects inline, so the callback re-enters a `SemaphoreSlim` that is not reentrant.

Cross-thread: an external detach holds the lifecycle lock while waiting on the structure lock, which inverts against the load thread holding the structure lock and needing the lifecycle lock to write properties.

Either way the block happens while the lifecycle lock is held, so it stalls every attach and detach in the process, not only this connector's. The removals themselves are safe unsynchronised: both tracking dictionaries are concurrent and the removals are idempotent, and nothing needs the two of them to be atomic together. Ordering against a subscription setup that is running concurrently is not this method's job either, and is covered by the sweep at the end of setup and by a reconnect rebuilding the monitored items from the owned properties.

## The continuation-point cap is conditional

`GetBrowseBatchSize` caps a browse batch by the server's continuation-point quota only when `MaxReferencesPerNode` is non-zero, because at zero the server returns every reference in the first response and issues no continuation point for the quota to bind on.

That is not a theoretical saving. In the round-trip measurement on the 241-subject test address space, whose server reports a quota of 100 against an operation limit of 4000, capping unconditionally cost 7 of the 16 round-trips the same load then took. The measurement predates the once-per-level restructuring, so the absolute numbers have moved; the reason the cap is conditional has not.

## Two status predicates, not one

`OpcUaStatusCodeClassifier` answers two different questions, and the access-scoped codes answer them oppositely, which is why one shared list does not work.

`IsRecoverableWithinSession` asks whether a status can recover without a new session, and backs the subscribe and write paths. `BadUserAccessDenied`, `BadNotReadable` and `BadNotImplemented` are recoverable there, because role permissions and the `AccessLevel` attribute are mutable server-side, so a monitored item can start succeeding mid-session and has to be kept for retry rather than dropped. `BadSecurityModeInsufficient` is permanent, because it is bound to the secure channel's message security mode, which only a new channel can change, and a reconnect re-attempts everything anyway.

`ThrowIfLoadMustRetry` asks whether the load must abort and retry, and backs the browse and read paths. Here the same three access-scoped codes repeat: the session's identity and permissions have not changed within the load, so throwing would crash-loop the whole load instead of skipping the one unreachable node. The load-skip set is therefore a superset of the session-permanent set by exactly those three codes.

## The remaining leak is in the core

In a graph-shaped address space the staging link can survive a successful load. `ContextInheritanceHandler` adds an inherited fallback context when a subject gains its first property reference, keyed to the parent holding that reference, and removes it when the last reference goes away, keyed to the parent holding that last one. For a subject reachable under two parents those are different parents, so the removal does not match the addition and a link to the first parent's context remains.

This is not specific to the loader. The same mismatch exists in the core for any subject reachable under two parents, however the model was built, and the loader only makes it easy to reach because a server-side DAG produces exactly that shape. The consequence is a retained context reference, not a wrong model: the subject stays registered, monitored and correct.

The fix belongs in the core rather than in the connector: record the inherited context per subject when it is added, and remove the recorded one on the last detach instead of re-deriving it from whichever parent happens to be last. Re-keying the loader's staging link to the binding parent is not a substitute, for the delegation cycle reason above. This is a follow-up.
