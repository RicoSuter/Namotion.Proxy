# Publication follow-up spike

This is a throwaway investigation on branch `codex/pr494-support-publication-v2`, based on frozen support commit `2e0ee43bb861b9ce79e59528443cc528adec4b99`. The underlying PR base remains `7ecca33b`; newer PR exception reconciliation is not integrated here. No original tests were changed. No benchmark, push, or GitHub edit was performed.

## Result

The common notification boundary fixes both ordinary property-observer regressions and restores the original lifecycle handler ordering. The full Tracking nonintegration suite has **735 passed, 24 failed, 759 total**: **724/747 original tests pass** and **11/12 added spike tests pass**. This improves the frozen support result by four original tests: child registration at observer delivery, departing scalar write suppression, bottom-up RunsAfter attach order, and the reparent stream's whole-chain detach/reattach ordering. The sole failing added test remains the getter owner-replacement orphan, assigned to a separate investigation.

## Mechanism

The notifier captures each lifecycle handler at its original ordered slot. Only the exact descent handler instance executes immediately; its recursive descent appends children's notifications between the parent's before/after handler groups. This preserves top-down RunsBefore and bottom-up RunsAfter delivery without a hardcoded list of built-in maintenance classes. Both detach handler groups remain top-down. Subject events and property lifecycle handlers retain FIFO positions.

PropertyChangeInterceptor still resolves subscribers once after the terminal and captures the original write's change data. When the current thread owns the lifecycle gate, it queues that typed publication alongside lifecycle notifications. The ordinary write's preceding Registry notifications and final claim release therefore run before its property-change observers. Nested callback writes append their publications after their own structural notifications. Writes on threads that do not hold this lifecycle gate retain immediate publication.

Property publications use a separate reusable typed list, with one corresponding marker in the common notification list. This avoids closure allocation, boxing, and widening every lifecycle notification with the large change struct. It retains peak list capacity and adds copying plus a lifecycle lookup to active property publication. No performance claim is made.

## Observable contract and limits

- A nested structural setter returns with its field and graph ownership committed. Registry registration, property admission, and derived maintenance can still be pending. An observer queued behind that transition sees its preceding maintenance. The nested setter caller itself has no such guarantee.
- Lifecycle and property events describe historical transitions. Callback mutation can advance graph ownership before older notifications deliver. This does not provide an attachment epoch/version contract to consumers.
- The original detach-before-attach FIFO ordering remains intact. Departing subjects keep GetContext through historical teardown events, then release their claim before the following ordinary property observer. A scalar write from the teardown callback itself is still an attached write; a scalar write from the later removal observer is untracked.
- Subscription resolution remains at write unwind, before deferred delivery. Listeners installed later by pending admission are not retroactively included. Existing inline registrations on a not-yet-admitted child are captured successfully in the new probe. No complete derived-initialization contract is established by these tests.
- Queued property observer exceptions join the drain AggregateException, so their timing and exception type differ from immediate publication. Fail-fast ordering within one property publication is preserved. Failures do not stop subsequent queued transitions.
- A scalar write on another thread while lifecycle publication is pending still publishes immediately. The prototype does not establish cross-thread publication isolation or change the existing gate timeout policy.
- Attach rollback/provisional-anchor behavior, the derived retry failure, nested-admission stream behavior, and the getter orphan remain unresolved. The production comments inherited from the frozen spike are not exhaustively updated; this report describes the experiment.

## Verification

The original targeted red run failed exactly the two observer cases and bottom-up RunsAfter order (3 failed / 4 passed). After the patch, those seven original cases and all eight prior callback support cases passed (15/15).

Two new behavior tests assert full callback traces: nested attach initialization writes a grandchild scalar before its pending property admission, then the observer sees it registered after grandchild-attached; nested removal emits departing-detach before the removal observer, which sees null context/Registry and performs an untracked scalar write. Both tests passed on the candidate, failed against the frozen prior spike with the expected missing registration/still-attached departure assertions, and passed again in the final full suite. The detach probe also checks that the historical detach event can still call GetContext.

Final command: `dotnet test src/Namotion.Interceptor.Tracking.Tests -m:1 -p:UseSharedCompilation=false --filter 'Category!=Integration' --logger 'trx;LogFileName=publication-v2-full.trx'`. Log: `/private/tmp/publication-v2-full.log`. Results: `src/Namotion.Interceptor.Tracking.Tests/TestResults/publication-v2-full.trx`. Red logs: `/private/tmp/publication-v2-red.log` and `/private/tmp/publication-v2-new-red.log`. The final build succeeded with no compiler errors. `git diff --check` passes.

Production increment versus frozen support: four files, **68 added / 47 removed lines (net +21)**. Two new tests are in `CallbackPublicationSpikeTests.cs`.

## Remaining failure categories

Six original tests require rejection of callback mutation that this spike intentionally permits. Six more fail on AggregateException versus the original exact exception type. Seven concern attach rollback, provisional-anchor restoration, or failure ownership. One nested-admission ownership stream assertion, one derived retry-bound assertion, and one lifecycle snapshot ordering assertion still fail. One original large-attach contention test fails its timing-evidence assertion rather than reporting a runtime ownership failure. The remaining added failure is the known getter orphan. These categories are evidence for further design, not waived correctness requirements.

## Exact remaining failures

- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AddPropertiesLifecycleTests.WhenACallbackAddsPropertiesToASubjectOfAnotherContext_ThenTheCallIsRejectedBeforeEnumeration`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AddPropertiesLifecycleTests.WhenAPropertyCallbackAddsPropertiesToASubjectOfAnotherContext_ThenTheCallIsRejectedBeforeEnumeration`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AddPropertiesLifecycleTests.WhenAPropertyHandlerThrowsDuringAdmissionFanOut_ThenMetadataStaysPublishedAndClaimsAreReleased`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AdversarialRollbackTests.WhenARejectedAttachConsumedAProvisionalAnchor_ThenTheAnchoredSubjectAndItsSubtreeStayAttached`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AdversarialRollbackTests.WhenARejectedAttachConsumedSeveralProvisionalAnchors_ThenEveryOneIsHandedBack`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AdversarialRollbackTests.WhenARejectedAttachNestsInsideAnAcceptedOne_ThenEachHandsBackOnlyItsOwnAnchors`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AdversarialRollbackTests.WhenAnAttachIsRejectedAfterABackEdgeAttachedTheRoot_ThenTheRootIsFullyRolledBack`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AdversarialRollbackTests.WhenAnEdgeFromOutsideTheRejectedComponentConsumedTheAnchor_ThenTheAnchorStaysConsumed`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AdversarialRollbackTests.WhenTheProvisionalRootIsPromotedWhileTheRollbackDrains_ThenTheExplicitAnchorSurvives`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.AttachResidueTests.WhenARollbackCallbackThrows_ThenTheAttachExceptionIsTheOneThatEscapes`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenADerivedPropertyExposesAnUnattachedSubject_ThenItThrows`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenADerivedValueKeepsExposingAnUnattachedSubject_ThenTheRecalculationThrowsAfterTheRetryBound`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenALifecycleCallbackAttachesASubject_ThenItThrows`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenALifecycleCallbackDetachesASubject_ThenItThrows`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenAPropertyCallbackWritesStructuralPropertyAtTopLevel_ThenItThrows`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenAPropertyCallbackWritesStructuralPropertyBelowTheFirstLevel_ThenItThrows`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenAnObjectDeclaredDerivedPropertyExposesAnUnattachedSubject_ThenItThrows`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackContractTests.WhenTheAttachEvaluationExposesAnUnattachedSubject_ThenNoValueIsCommitted`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.CallbackSupportGetterSpikeTests.WhenGetterReplacesOwnerPropertyDuringAttach_ThenFinalGraphMatchesTheLatestWrite`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.GraphOwnershipTests.WhenLifecycleCallbackWritesStructuralProperty_ThenTheGuardRejectsIt`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.GraphOwnershipTests.WhenPropertyDetachCallbackReleasesTheWritingParent_ThenTheGuardRejectsIt`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.OwnershipChangeStreamTests.WhenANestedAdmissionAdoptsAProvisionalRootUnderAStaleAncestorEdge_ThenTheAnchorSurvives`
- `Namotion.Interceptor.Tracking.Tests.Lifecycle.SameContextGateDeadlockTests.WhenTheGateHolderIsRunningALargeAttach_ThenAContendingWriteWaitsForItInsteadOfFailing`
- `Namotion.Interceptor.Tracking.Tests.LifecycleInterceptorTests.WhenAddingPropertyInLifecycleHandlerAttach_ThenItIsAttachedOnlyOnce`
