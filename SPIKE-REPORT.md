# Callback support feasibility spike

This is a throwaway investigation, not a production-ready fix. Base: PR #494 commit `7ecca33b`. Branch: `codex/pr494-callback-support-spike`. No original test was edited, deleted or weakened. No benchmark was run by this spike agent.

## Result

Queuing notifications supports useful callback mutation cases in a small local change, but does not satisfy the full requested contract. Final Tracking suite: **729 passed, 28 failed, 757 total**. Of these, **9/10 added feasibility tests pass** and **720/747 original tests pass**. The decisive remaining corruption is a structural getter that replaces its own owner during attach: the root points at its newer replacement and the intermediate wrapper is detached, but the wrapper's child remains attached as an orphan. This must not ship.

## Mechanism and visibility

Graph mutation remains synchronous under the existing context gate. Lifecycle/property callbacks become typed records in a reusable per-context list. The outermost gate exit drains the list FIFO while retaining the gate. Built-in recursive lifecycle descent runs during graph publication, independently of the queued lifecycle handler fan-out. Callback-generated setters update their backing fields and graph edges before returning, and append their notifications to the existing queue.

A released subject retains its context claim through its queued teardown notifications so property handlers and actual detach callbacks still resolve `GetContext()`. A typed final-release record drops the claim only if a callback has not reestablished graph ownership. The releasing marker remains until that final release. A callback that reattaches the subject therefore does not lose its new claim to the old teardown.

Each callback notification is a historical transition, not a promise that the current graph still has that state. For example replacing old with intermediate, then assigning final from old's detach callback produces: old-detach, intermediate-attach, intermediate-detach, final-attach. The nested setter returns with final already stored and owned, before intermediate's pending attach notification is delivered. Its own new callback exceptions cannot be caught by the nested setter's caller; they surface at the outer drain. This is a deliberate incompatibility requiring an explicit public contract, not an implementation detail that can be hidden.

Callback failures are collected per handler and later handlers still run; the outer drain throws an `AggregateException`. This preserves graph settlement for the tested callback failures and cannot undo external side effects. A downstream interceptor failure after the terminal now reconciles the authoritative stored value in a `finally` before the failure escapes.

## Tests and TDD evidence

`/private/tmp/callback-spike-red.log`: the first three new tests failed on PR code as expected: structural attach initialization was forbidden; a detach callback observed replacement reference count zero; a throwing detach callback left replacement unowned.

`/private/tmp/callback-spike-green.log`: those three tests passed after queued publication.

`/private/tmp/callback-spike-red2.log`: seven expanded callback cases passed, while the post-terminal interceptor exception case failed with replacement context null. `/private/tmp/callback-spike-green2.log`: all eight passed after reconciliation on unwind.

`/private/tmp/callback-spike-getters.log`: a stable boxed ArraySegment getter self-write terminates, but the owner-replacement getter test exposes an attached orphan. The two existing detach-admission tests pass after keeping the releasing marker through notification delivery.

Final command: `dotnet test src/Namotion.Interceptor.Tracking.Tests --no-build --no-restore --filter 'Category!=Integration' -m:1 -p:UseSharedCompilation=false --logger 'trx;LogFileName=callback-final.trx'`. Logs: `/private/tmp/callback-spike-final.log`; machine-readable results: `src/Namotion.Interceptor.Tracking.Tests/TestResults/callback-final.trx`. The last build was the preceding targeted `dotnet test` with the same single-process compiler options. Local IPC required the approved sandbox escalation.

Passing added cases cover attach initialization, attach replacing itself, detach reassigning the same property, detach reattaching the exact old subject, throwing detach callback settlement, nested interceptor foreign-child rejection before changing its field, cross-context structural rejection while scalar writes remain allowed, post-terminal interceptor exception settlement, and stable boxed struct getter termination.

## Known incompatibilities and remaining defects

- **Unresolved correctness failure:** the getter owner-replacement case leaves a child attached without a supported owner. Moving callbacks does not make user getters safe during internal graph descent.
- **Ordinary observer regression:** `PropertyChangeInterceptor` delivers its notification during chain unwind, before the outermost gate drains lifecycle notifications. An inline subscriber sees a new child with its context but without Registry registration. A departing child's scalar write is still tracked because its queued final release has not run. Fixing this requires a common publication boundary or a split between internal maintenance and external callbacks.
- **Ordered handler contract changed:** handlers ordered after `LifecycleInterceptor` now run top-down rather than bottom-up. The existing ordered descent seam cannot be preserved by merely queuing each whole handler fan-out.
- **Explicit attach exceptions changed:** a failing callback now runs after the attach graph committed, so the old attach rollback and provisional-anchor restoration tests fail. Commit-then-notify can be consistent, but it is a different exception contract.
- **Historical event/Registry interactions:** nested admissions and reparent change-stream expectations fail. A pending event can encounter newer graph ownership than the event describes; each built-in consumer needs a deliberate transition/version contract.
- **Exception limits remain:** reconciliation in `finally` can replace a downstream exception if the getter or validation itself throws; an exception from drain can also replace an in-flight operation exception. No rollback of arbitrary terminal/getter/user callback effects exists.
- **Cross-context limits:** same-thread acquisition of a second context gate retains PR's deterministic early rejection for tested attached structural writes. PR's timeout/thread-state waiter policy is unchanged, so this spike does not meet a global promise of no timing-dependent exceptions. Unattached/equal-value setter paths and cross-thread dispatch are not given a new contract by this spike.
- **Allocation limits:** notifications are structs in a reusable list, avoiding one closure per queued event. The list grows and retains peak capacity; every event subscriber fan-out currently calls `GetInvocationList()`, allocating an array. No performance claims are made, and no pool/capacity tuning was attempted.
- Production comments from the PR that describe immediate callbacks/guarded topology are intentionally not exhaustively rewritten in this throwaway experiment. This report is authoritative for the experiment.

## Scope and recommendation

The frozen production diff is six Tracking lifecycle files, **147 added / 63 removed lines (net +84)** before formatting-only cleanup. Added tests are separate new files. Core and generated setters are unchanged.

Full support is feasible only with a larger explicit design covering internal graph transition versus external notification, getter/interceptor reentrancy during discovery/publication, built-in Registry and derived-property maintenance, synchronous property-change publication, claims and attachment epochs, and exception settlement. The small queue is useful evidence for a future design, not a safe minimal fix. Benchmarking this frozen experiment can quantify its ordinary-path cost, but favorable numbers cannot make its correctness failures acceptable.

## Exact final failing tests

- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AdversarialRollbackTests.WhenARejectedAttachNestsInsideAnAcceptedOne_ThenEachHandsBackOnlyItsOwnAnchors`: Assert.Null() Failure: Value is not null
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenTheAttachEvaluationExposesAnUnattachedSubject_ThenNoValueIsCommitted`: Assert.IsType() Failure: Value is not the exact type
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenALifecycleCallbackDetachesASubject_ThenItThrows`: Assert.IsType() Failure: Value is null
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.SameContextGateDeadlockTests.WhenTheGateHolderIsRunningALargeAttach_ThenAContendingWriteWaitsForItInsteadOfFailing`: the write only waited 00:00:00.0812764, so it never overlapped the attach and proves nothing
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.OwnershipChangeStreamTests.WhenANestedAdmissionAdoptsAProvisionalRootUnderAStaleAncestorEdge_ThenTheAnchorSurvives`: Assert.Equal() Failure: Values differ
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AddPropertiesLifecycleTests.WhenAPropertyCallbackAddsPropertiesToASubjectOfAnotherContext_ThenTheCallIsRejectedBeforeEnumeration`: Assert.Throws() Failure: Exception type was not an exact match
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenAPropertyCallbackWritesStructuralPropertyAtTopLevel_ThenItThrows`: Assert.IsType() Failure: Value is null
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AddPropertiesLifecycleTests.WhenAPropertyHandlerThrowsDuringAdmissionFanOut_ThenMetadataStaysPublishedAndClaimsAreReleased`: Assert.Throws() Failure: Exception type was not an exact match
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenAPropertyCallbackWritesStructuralPropertyBelowTheFirstLevel_ThenItThrows`: Assert.IsType() Failure: Value is null
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenALifecycleCallbackAttachesASubject_ThenItThrows`: Assert.IsType() Failure: Value is null
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AdversarialRollbackTests.WhenARejectedAttachConsumedAProvisionalAnchor_ThenTheAnchoredSubjectAndItsSubtreeStayAttached`: Assert.Equal() Failure: Values differ
- `Namotion.Interceptor.Tracking.Tests.Change.PerPropertySubscriptionLifecycleTests.WhenCallbackWritesToTheDepartingChild_ThenThatWriteIsNotTracked`: Assert.Equal() Failure: Values differ
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.LifecycleHandlerOrderTests.WhenASubtreeAttaches_ThenAHandlerAfterTheLifecycleObservesItBottomUp`: Assert.Equal() Failure: Collections differ
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.GraphOwnershipTests.WhenLifecycleCallbackWritesStructuralProperty_ThenTheGuardRejectsIt`: Assert.IsType() Failure: Value is null
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenAnObjectDeclaredDerivedPropertyExposesAnUnattachedSubject_ThenItThrows`: Assert.IsType() Failure: Value is not the exact type
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.GraphOwnershipTests.WhenPropertyDetachCallbackReleasesTheWritingParent_ThenTheGuardRejectsIt`: Assert.IsType() Failure: Value is null
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenADerivedPropertyExposesAnUnattachedSubject_ThenItThrows`: Assert.IsType() Failure: Value is not the exact type
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AddPropertiesLifecycleTests.WhenACallbackAddsPropertiesToASubjectOfAnotherContext_ThenTheCallIsRejectedBeforeEnumeration`: Assert.Throws() Failure: Exception type was not an exact match
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AdversarialRollbackTests.WhenAnEdgeFromOutsideTheRejectedComponentConsumedTheAnchor_ThenTheAnchorStaysConsumed`: Assert.Equal() Failure: Values differ
- `Namotion.Interceptor.Tracking.Tests.LifecycleInterceptorTests.WhenAddingPropertyInLifecycleHandlerAttach_ThenItIsAttachedOnlyOnce`: VerifyException : Directory: /private/tmp/pr494-spike-callback-support/src/Namotion.Interceptor.Tracking.Tests
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackSupportGetterSpikeTests.WhenGetterReplacesOwnerPropertyDuringAttach_ThenFinalGraphMatchesTheLatestWrite`: Assert.Null() Failure: Value is not null
- `Namotion.Interceptor.Tracking.Tests.Change.PerPropertySubscriptionLifecycleTests.WhenSubjectAssigned_ThenNewChildIsAlreadyAttachedAtCallbackTime`: Assert.True() Failure
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenADerivedValueKeepsExposingAnUnattachedSubject_ThenTheRecalculationThrowsAfterTheRetryBound`: Assert.IsType() Failure: Value is null
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.OwnershipChangeStreamTests.WhenAReparentTargetIsAlreadyOwned_ThenTheWholeChainDetachesAndReattaches`: Assert.Equal() Failure: Collections differ
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AdversarialRollbackTests.WhenAnAttachIsRejectedAfterABackEdgeAttachedTheRoot_ThenTheRootIsFullyRolledBack`: the root is still owned by the graph after a rejected attach
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AttachResidueTests.WhenARollbackCallbackThrows_ThenTheAttachExceptionIsTheOneThatEscapes`: Assert.Null() Failure: Value is not null
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AdversarialRollbackTests.WhenTheProvisionalRootIsPromotedWhileTheRollbackDrains_ThenTheExplicitAnchorSurvives`: Assert.Equal() Failure: Values differ
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AdversarialRollbackTests.WhenARejectedAttachConsumedSeveralProvisionalAnchors_ThenEveryOneIsHandedBack`: Assert.Equal() Failure: Values differ
