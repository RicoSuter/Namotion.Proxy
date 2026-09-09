# OPC UA client loader: why the load works the way it does

Maintainer notes for the address space loader in `src/Namotion.Interceptor.OpcUa/Client/`. Consumer-facing behaviour is in [OPC UA Client](../connectors-opcua-client.md); this covers the reasoning behind it, which is not recoverable from the code.

The moving parts are `OpcUaSubjectLoader` (the pipeline), `OpcUaAttributeLoader` (the attribute rounds), `OpcUaLoadContext` (per-load state, staging and commit) and `OpcUaSessionExtensions` (the batched browse and read primitives).

## Discovery runs on attached subjects

A load could in principle build a detached object graph and hand it to the model at the end. It does not, because almost everything the loader does per node needs the registry's `RegisteredSubject`, and the registry only knows attached subjects.

- The mapper resolves a browse reference to a property against `RegisteredSubject`, so an unregistered subject has no properties to map to.
- A dynamic property is created with `RegisteredSubject.AddProperty` and a dynamic attribute with `RegisteredSubjectProperty.AddAttribute`. There is no off-graph equivalent.
- Attribute matching reads `RegisteredSubjectProperty.Attributes`, which is registry state as well.

`OpcUaSubjectLoader.FilterAndBrowseSubjectsAsync` makes that explicit: a subject whose `TryGetRegisteredSubject()` returns null is skipped for the load and reloaded on the next one.

The secondary reason is reuse. `RegisteredSubjectProperty.Children` is written only by `SubjectRegistry`, and the loader reads it to bind an existing child subject rather than constructing a new one, both for single references and for collection and dictionary elements. Discovering off-graph would silently lose that reuse and rebuild the subtree on every load.

So a newly constructed subject is attached to its discovering parent's context by `OpcUaLoadContext.RegisterStagedSubject` before it is browsed, and that link is what the rollback path has to undo.

## One recursion per level

Every phase of a level (variable-typed subject references, collections and dictionaries, single subject references, attributes) used to recurse on its own. That makes the number of browse calls grow with the number of branch kinds along a path, not just with depth: a subject carrying both a collection and a subject reference browsed its two subtrees in separate passes, and the passes multiplied down the tree.

`LoadChildPropertiesAsync` now collects the children of all phases into one list and calls `LoadSubjectsAsync` once for the level below, with the attribute rounds running last. The browse cache in `OpcUaLoadContext` does the rest: the browse that resolves the type of a dynamic Object node is the same browse the container phase and the next level need, so each of those is a cache hit.

On the unit fixture of three container levels that each carry both a collection and a subject reference, the load makes 8 browse calls where the previous structure needed 23 (`OpcUaSubjectLoaderBatchingTests.WhenEveryLevelCarriesACollectionAndASubjectReference_ThenBrowseCallCountGrowsWithDepthNotWithBranches`, which pins the node count of every call). The remaining growth is in depth and in attribute depth alone.

The attribute rounds terminate on a cycle through a visited set of node IDs per root variable property, so an attribute pointing back at a node already traversed under that property is skipped and logged. `MaxAttributeTraversalDepth` stays as the backstop for the shapes the visited set does not cover.

## Staging is keyed to the discovering parent

`RegisterStagedSubject` adds the immediate parent's context as a fallback context, not the root context. In the ordinary tree case that link is exactly the one `ContextInheritanceHandler` removes when the subject's last property reference goes away, so the two agree and nothing is left behind. The handler adds no link of its own for a staged subject, because its add is gated on the subject not already being context-attached, which staging has already made true.

Re-keying the link to the parent that finally binds the subject was implemented and rejected. Staging-parent keying is what keeps the fallback chain acyclic. In an address space where two subjects reference each other, re-keying makes each one's context delegate to the other's, and the next context resolution throws the delegation cycle error from `InterceptorSubjectContext`. `OpcUaSubjectLoaderTests.WhenAddressSpaceHasCycle_ThenLoaderTerminatesWithoutInfiniteRecursion` is the test that catches it.

## Everything the load changes is deferred to one commit

`OpcUaLoadContext` queues both kinds of mutation while browsing: `QueueClaim` for a source ownership claim and its monitored item, `QueueBinding` for a property assignment. `Commit` applies them at the end, and the order inside it is load-bearing.

Claims run first. An observer that sees a new child appear in the model then finds all of that child's leaves already source-owned, rather than watching ownership arrive property by property behind the structure.

Bindings are applied deepest level first, which is the reverse of the order they were queued in, because levels are discovered top down. A subject therefore becomes reachable from the root only once its own subtree is bound, so an observer never walks into a half-built branch.

A queued claim whose subject the application detached during the load is dropped, and the monitored item is added to the load's result only when the claim succeeded, so a property that another source owns by the time `Commit` runs is never monitored.

The only mutation that is not deferred is the creation of dynamic properties and dynamic attributes, which needs the registry to hand back a `RegisteredSubjectProperty` to browse and monitor against. A failed load leaves those behind, which is transient rather than torn state: each carries an `OpcUaNodeAttribute` with its exact node ID, so the next load re-matches it through the mapper and monitors it again.

## Rollback sheds what nothing references, in a fixpoint loop

`Dispose` before `Commit` is the rollback. It walks the staged subjects deepest first and removes the fallback context of every one whose reference count is zero, repeating the sweep until a pass removes nothing, because detaching one subject releases its own references and can drop another staged subject to zero.

The reference count guard stays even though deferring the bindings means a staged subject normally has no references on the failure path. Removing the link from a subject a property still points at would break a graph invariant rather than restore one: the subject would be evicted from the registry while the model still referenced it, the next load would reuse it from `RegisteredSubjectProperty.Children` without re-staging it, and the loader would then skip it as unregistered, leaving the subtree unmonitored for good.

Deepest first matters because a nested staged subject reaches the lifecycle interceptor through its parent's fallback chain, so removing the parent's link first would cut the child off from the handler that has to see its detach.

A failure inside `Commit` is a different case, because part of the work is already visible. It releases the claims that call established, keeping ownership from a previous successful load, which `ClaimSource` returns true for again on a reload and which must not be stripped or application writes stop being routed until the next successful retry. It restores the bindings it applied in reverse order, skipping any property that no longer holds the value this load wrote, because the application has re-set it since and the newer value wins. Then it clears the monitored items and rethrows.

## The detach path takes no lock

`OpcUaSubjectClientSource.RemoveItemsForSubject` runs from the synchronous subject-detach callback, which the lifecycle interceptor raises while holding its attached-subject lock. Taking the source's structure lock there deadlocks in two different ways.

Same-thread: the initial load holds the structure lock across the whole load, and a failed load's rollback detaches its staged subjects inline, so the callback re-enters a `SemaphoreSlim` that is not reentrant.

Cross-thread: an external detach holds the lifecycle lock while waiting on the structure lock, which inverts against the load thread holding the structure lock and needing the lifecycle lock to write properties.

Either way the block happens while the lifecycle lock is held, so it stalls every attach and detach in the process, not only this connector's. The removals themselves are safe unsynchronised: both tracking dictionaries are concurrent and the removals are idempotent, and nothing needs the two of them to be atomic together. Ordering against a subscription setup that is running concurrently is not this method's job either, and is covered by the sweep at the end of setup and by a reconnect rebuilding the monitored items from the owned properties.

## A browsed node is all or nothing

`BrowseNodesAsync` puts a node in its result only when that node was browsed to completion, possibly to an empty reference list. A node whose first page returned a permanent bad status, whose pagination stopped part-way, or that was still paginating when `MaxBrowseContinuationRounds` was reached, is omitted, and the pages already collected for it are dropped.

The alternative, reporting a truncated child list, is worse than reporting nothing. Consumers align positionally: collection children are reused by index, so a shortened list rebinds existing subjects to the wrong items and replaces the collection with a shorter one. Every caller instead treats an absent node as "keep the current value and reload next time", which is a retry rather than a loss.

## The continuation-point cap is conditional

A browse batch that opens more continuation points than the server's quota allows fails with `BadNoContinuationPoints` or has its oldest points evicted, so `GetBrowseBatchSize` can cap the batch by `MaxBrowseContinuationPoints`. It does that only when `MaxReferencesPerNode` is non-zero. At zero the server returns every reference in the first response and issues no continuation point at all, so the quota cannot bind and the cap only shrinks batches for nothing.

That is not a theoretical saving. In the round-trip measurement on the 241-subject test address space, whose server reports a quota of 100 against an operation limit of 4000, capping unconditionally cost 7 of the 16 round-trips the same load then took. The measurement predates the once-per-level restructuring above, so the absolute numbers have moved; the reason the cap is conditional has not.

A server that under-reports or dynamically shrinks its quota is still handled: `BadNoContinuationPoints` on any result of a batch releases the points the server did issue, then splits the batch in half and retries. Splitting rather than retrying is what makes it converge, since a same-size retry fails identically forever. The check runs before any result is merged, because the merge appends and a partially merged retry would duplicate references.

## Two status predicates, not one

`OpcUaStatusCodeClassifier` answers two different questions, and the access-scoped codes answer them oppositely, which is why one shared list does not work.

`IsRecoverableWithinSession` asks whether a status can recover without a new session, and backs the subscribe and write paths. `BadUserAccessDenied`, `BadNotReadable` and `BadNotImplemented` are recoverable there, because role permissions and the `AccessLevel` attribute are mutable server-side, so a monitored item can start succeeding mid-session and has to be kept for retry rather than dropped. `BadSecurityModeInsufficient` is permanent, because it is bound to the secure channel's message security mode, which only a new channel can change, and a reconnect re-attempts everything anyway.

`ThrowIfLoadMustRetry` asks whether the load must abort and retry, and backs the browse and read paths. Here the same three access-scoped codes repeat: the session's identity and permissions have not changed within the load, so throwing would crash-loop the whole load instead of skipping the one unreachable node. The load-skip set is therefore a superset of the session-permanent set by exactly those three codes.

The write path uses `IsRecoverableWithinSession` for diagnostics only. `WriteResult.FailedChanges` has to stay complete for the retry queue and the transaction writer, so a permanently failed write is still requeued.

## The remaining leak is in the core

In a graph-shaped address space the staging link can survive a successful load. `ContextInheritanceHandler` adds an inherited fallback context when a subject gains its first property reference, keyed to the parent holding that reference, and removes it when the last reference goes away, keyed to the parent holding that last one. For a subject reachable under two parents those are different parents, so the removal does not match the addition and a link to the first parent's context remains.

This is not specific to the loader. The same mismatch exists in the core for any subject reachable under two parents, however the model was built, and the loader only makes it easy to reach because a server-side DAG produces exactly that shape. The consequence is a retained context reference, not a wrong model: the subject stays registered, monitored and correct.

The fix belongs in the core rather than in the connector: record the inherited context per subject when it is added, and remove the recorded one on the last detach instead of re-deriving it from whichever parent happens to be last. Re-keying the loader's staging link to the binding parent is not a substitute, for the delegation cycle reason above. This is a follow-up.
