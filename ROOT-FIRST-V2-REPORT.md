# Root-first publication experiment

This throwaway experiment starts from getter candidate `b63d80fe06d0bf65f673dce8a43ac9ef4e82049d` and applies publication candidate `2838fcda07b11c958a44cfd41fb3c93f64177586` as local cherry-pick `506c4e81`. The branch is `codex/pr494-support-root-first-v2`, in `/private/tmp/pr494-support-root-first-v2`. No original or earlier-spike test was edited, removed, or weakened. No benchmark, integration test, push, or GitHub write was performed.

## Outcome

The tiny root-first variant fixes the public-state enumerable consistency probe, but **fails the required stable boxed self-writing getter termination contract**. It must not be treated as a complete or safe fix. The result supports the ownership-timing hypothesis while demonstrating that changing root publication alone merely moves the problem into recursive authoritative getter evaluation.

The variant changes only `LifecycleInterceptor.cs`: **2 added / 9 removed lines, net -7** relative to the combined getter/publication baseline. `SeedAndAttachComponent` calls `AttachRoot` immediately, and the ordered lifecycle descent handles every context attach, including an anchored root. The publication candidate already invokes the internal descent synchronously in its ordered slot and removed the redundant fallback seed in `AttachTraversal.Publish`.

## Investigation and verification

Before changing production code, the new enumerable test failed: it triggered only when public `root.TryGetContext()` became non-null, replaced the root property during enumeration, and found the latest child unowned after attach. Unlike the prior characterization, its trigger does not depend on the private ownership map or require the claimed-but-unpublished phase to continue existing.

Before variant: **746 passed, 26 failed, 772 total**. After variant: **745 passed, 27 failed, 772 total**. Both runs use `dotnet test src/Namotion.Interceptor.Tracking.Tests --no-restore --filter 'Category!=Integration' -m:1 -p:UseSharedCompilation=false`, with TRX logger names `root-first-before.trx` and `root-first-after.trx`. Logs are `/private/tmp/root-first-before.log` and `/private/tmp/root-first-after.log`; TRX files are under this worktree's `src/Namotion.Interceptor.Tracking.Tests/TestResults/`. The initial baseline invocation restored packages. `git diff --check` passes.

All five added root-first probes pass after the change:

- Public-state enumeration rewrites the root property, leaving only the latest child owned. Publishing root ownership first makes the nested setter reconcile normally; the baseline revision guard discards the stale seed.
- An explicit root with a child back edge emits exactly one context-attach notification for each subject and retains its explicit anchor and expected incoming counts.
- An explicit root's attach callback replaces a child, and the nested write settles the final graph.
- A seed getter failure releases the root's anchor and both root/child claims in the tested simple rollback case.
- A handler ordered before Lifecycle receives an explicit root before its descendants.

The last probe verifies an intentional ordering change, not preserved compatibility. Before the variant, an explicit root was seeded before its root notification, so the before-descent handler observed child, leaf, root. With root-first publication it observes root, child, leaf. The old `LifecycleInterceptorTests.WhenAddingInterceptorCollection_ThenAllChildrenAreAlsoAttached` snapshot newly fails for this same root-order change: root now precedes its descendants.

## Exact changes in test outcomes

Two tests change from failing to passing:

- `CallbackRootFirstSpikeTests.WhenRootEnumerableWritesDuringAttach_ThenOwnershipMatchesTheStoredValue`.
- `CallbackRootFirstSpikeTests.WhenExplicitRootAttaches_ThenTheBeforeHandlerReceivesRootBeforeDescendants`.

Three tests change from passing to failing:

- `CallbackSupportGetterContinuationTests.WhenUnpublishedRootGetterWritesTheStableBoxedSegment_ThenSeedingTerminates`: a real termination regression. Its trigger uses public context and incoming-count state, remains exercised after root-first publication, and exceeds its bounded 16-read guard. With the root now owned, `WriteProperty` invokes the authoritative getter in its `finally`, and that getter writes the same property and enters the authoritative reread again. The repeated stack is `ReadChildren -> Children setter -> WriteProperty -> ReadChildren`. There is no equality skip in the terminal. Without the test's bound this sequence has no terminating step.
- `ReentrantStructuralWriteTests.WhenAUserEnumerableWritesTheRootWhileTheAttachSeedsIt_ThenTheWritePassesThroughAndTheAttachCompletes`: its private-state phase guard no longer fires because this root is owned before enumeration. This is an obsolete-phase characterization failure, not evidence that the new public-state enumerable test is still inconsistent.
- `LifecycleInterceptorTests.WhenAddingInterceptorCollection_ThenAllChildrenAreAlsoAttached`: the explicit-root notification ordering change described above.

`CallbackSupportGetterRemainingLimitTests.WhenUnpublishedRootEnumerableRewritesItsProperty_ThenOwnershipMatchesTheStoredValue` remains failing, but now its private-state trigger assertion fails because the phase was eliminated. Its former stale-ownership symptom is independently covered by the new passing public-state probe. All other outcome statuses are unchanged from the combined baseline, including inherited rollback and callback exception-contract failures. The simple new rollback probe does not establish correctness for provisional-anchor adoption or callback-failure rollback.

## Cost and design limit

The variant adds no data structure, allocation, scan, retry loop, or ordinary-path asymptotic work. It changes when the existing root ownership record and ordered notifications are created. Root-first seeding still uses the getter candidate's linear property/occurrence traversal and constant-time ownership/baseline revision checks.

Handling a getter that writes its own property needs an explicit getter-evaluation/reconciliation protocol. Blindly trusting the setter's proposed value would break normalizing terminals; recursively rereading the authoritative getter breaks this stable getter. This experiment deliberately adds neither shortcut. A supported root-first design may still be useful, but these two production edits are insufficient on their own.
