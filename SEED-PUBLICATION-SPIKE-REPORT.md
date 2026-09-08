# Active-seeding publication follow-up

This is a throwaway probe on `codex/pr494-support-seeding-publication-v2`, based on combined support commit `9a283c0aa55b499684500835ff3667ede0feb10d`. No original tests were changed. No benchmark, push, or GitHub edit was performed.

## Reproduction

With full tracking and Registry, subscribe to a detached root's Children property before explicit attach. Its metadata getter writes that same property while the root is being seeded. The nested setter returns with reconciliation deferred to the active seed, but PropertyChangeInterceptor has already queued a publication marker. The active seed then queues the child's lifecycle and Registry notifications behind that marker. The ordinary property observer consequently receives the child before Registry registration.

Three new probes failed on the combined baseline: the simple self-writing getter observed a null registration, a reentrant lifecycle/property sequence observed pending registration, and a seed/lifecycle/property triple-failure test found property callback errors preceding lifecycle callback errors. The last test deliberately specifies the proposed lifecycle-first scheduling order while preserving every exception.

## Candidate

The property list no longer adds markers to the lifecycle notification list. Drain keeps one index for each list, exhausts currently pending lifecycle notifications, dispatches one property publication, then checks lifecycle work again. Reentrant lifecycle work therefore precedes the next property publication. Both lists remain FIFO individually, including entries appended by callbacks. The implementation adds no new storage or per-event object: it reuses both existing typed lists and removes one lifecycle marker per property publication.

## Observable contract and limitations

- Property observers begin delivery after all currently pending lifecycle notifications, Registry/property maintenance, and final claim releases. This includes lifecycle work discovered after the original self-writing setter returned.
- Global FIFO across lifecycle and property streams is replaced by lifecycle priority. A newer lifecycle transition may run before an older property event. Property payloads remain historical writes; a payload can name a child already detached by newer transitions, while the current graph and Registry reflect those transitions.
- The reentrant test pins this order: child attached, grandchild attached, root observer, grandchild detached, replacement attached, first child-write observer, second child-write observer. The two child-write observers keep write order even though the first historical grandchild is already detached.
- The boundary is one PropertyChange publication group. It is not a drain between individual Rx or inline subscribers. If an earlier subscriber mutates structurally, later subscribers in that same fan-out may see its pending maintenance. Likewise, a nested setter caller still sees graph ownership without guaranteed Registry completion. Cross-thread scalar publication behavior is unchanged.
- A lifecycle callback that perpetually creates more lifecycle work can starve property publications. The prototype has no convergence bound for arbitrary callback-generated work; the enclosing operation also cannot complete in that case.
- Failure collection continues through both streams. One failure preserves its original exception identity; multiple failures aggregate. An operation failure stays first, followed by failures in the new actual delivery order. The triple-failure probe preserves seed, lifecycle, and property exception instances in that order and confirms final detached graph/Registry state. Existing operation-plus-reconciliation failure grouping tests pass.

## Verification

Focused tests: **17 passed / 0 failed**, covering all three new probes, prior publication/release probes, original lifecycle handler ordering, and combined failure boundaries. Full Tracking nonintegration suite: **778 passed / 20 failed / 798 total**. The exact 20 failure names are unchanged from combined-v2's **775 passed / 20 failed / 795 total**. No original test was weakened.

Red log: `/private/tmp/seed-publication-v2-red.log`. Focused green log: `/private/tmp/seed-publication-v2-green.log`. Full log: `/private/tmp/seed-publication-v2-full.log`. Full results: `src/Namotion.Interceptor.Tracking.Tests/TestResults/seed-publication-v2-full.trx`.

The focused build/test used `dotnet test src/Namotion.Interceptor.Tracking.Tests -m:1 -p:UseSharedCompilation=false` with the new probe and existing order/publication/release/failure classes filtered. The full run reused that build with `--no-build --no-restore --filter 'Category!=Integration' --logger 'trx;LogFileName=seed-publication-v2-full.trx'`. `git diff --check` passes.

Production increment: **13 added / 8 removed lines** in LifecycleNotifier. The new tests are in CallbackSeedPublicationSpikeTests.cs.

## Remaining full-suite failures

- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AddPropertiesLifecycleTests.WhenAPropertyHandlerThrowsDuringAdmissionFanOut_ThenMetadataStaysPublishedAndClaimsAreReleased`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AdversarialRollbackTests.WhenARejectedAttachConsumedAProvisionalAnchor_ThenTheAnchoredSubjectAndItsSubtreeStayAttached`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AdversarialRollbackTests.WhenARejectedAttachConsumedSeveralProvisionalAnchors_ThenEveryOneIsHandedBack`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AdversarialRollbackTests.WhenARejectedAttachNestsInsideAnAcceptedOne_ThenEachHandsBackOnlyItsOwnAnchors`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AdversarialRollbackTests.WhenAnAttachIsRejectedAfterABackEdgeAttachedTheRoot_ThenTheRootIsFullyRolledBack`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AdversarialRollbackTests.WhenAnEdgeFromOutsideTheRejectedComponentConsumedTheAnchor_ThenTheAnchorStaysConsumed`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AdversarialRollbackTests.WhenTheProvisionalRootIsPromotedWhileTheRollbackDrains_ThenTheExplicitAnchorSurvives`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AttachResidueTests.WhenARollbackCallbackThrows_ThenTheAttachExceptionIsTheOneThatEscapes`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenALifecycleCallbackAttachesASubject_ThenItThrows`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenALifecycleCallbackDetachesASubject_ThenItThrows`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenAPropertyCallbackWritesStructuralPropertyAtTopLevel_ThenItThrows`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenAPropertyCallbackWritesStructuralPropertyBelowTheFirstLevel_ThenItThrows`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackSupportGetterRemainingLimitTests.WhenUnpublishedRootEnumerableRewritesItsProperty_ThenOwnershipMatchesTheStoredValue`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.GraphOwnershipTests.WhenLifecycleCallbackWritesStructuralProperty_ThenTheGuardRejectsIt`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.GraphOwnershipTests.WhenPropertyDetachCallbackReleasesTheWritingParent_ThenTheGuardRejectsIt`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.OwnershipChangeStreamTests.WhenANestedAdmissionAdoptsAProvisionalRootUnderAStaleAncestorEdge_ThenTheAnchorSurvives`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.ReentrantStructuralWriteTests.WhenAUserEnumerableWritesTheRootWhileTheAttachSeedsIt_ThenTheWritePassesThroughAndTheAttachCompletes`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.SameContextGateDeadlockTests.WhenTheGateHolderIsRunningALargeAttach_ThenAContendingWriteWaitsForItInsteadOfFailing`
- `Namotion.Interceptor.Tracking.Tests.LifecycleInterceptorTests.WhenAddingInterceptorCollection_ThenAllChildrenAreAlsoAttached`
- `Namotion.Interceptor.Tracking.Tests.LifecycleInterceptorTests.WhenAddingPropertyInLifecycleHandlerAttach_ThenItIsAttachedOnlyOnce`
