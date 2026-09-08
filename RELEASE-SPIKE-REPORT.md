# Release identity follow-up spike

This is throwaway work on `codex/pr494-support-release-v2`, based on publication prototype `2838fcda07b11c958a44cfd41fb3c93f64177586`. It does not include the root integration branch's newer PR exception or getter changes. No original test was changed. No benchmark, push, or GitHub edit was performed.

## Reproduction and result

A child's first detach callback reassigns that exact child to its parent and removes it again before returning. The first queued release then sees the child unowned by the newest transition and drops its context before the pending historical reattach and second detach callbacks. The red probe failed with ten AggregateException inner failures, including Registry property admission and the historical callbacks' GetContext calls.

The candidate associates each release with the existing SubjectOwnership instance that was removed. The releasing marker stores that instance by subject identity, and the final-release notification carries the same reference in its existing Value slot. A release only drops a claim and clears the marker if that reference is still current. The older release is skipped when a newer ownership lifetime has already released the subject.

The regression now observes exactly `detach-1`, `nested-setters-returned`, `reattach`, `detach-2`. Every historical callback resolves the context, both detach callbacks see the releasing marker, and final state has no parent property, context, Registry registration, graph ownership, reference count, or releasing marker.

## Locking and allocation

All releasing-marker reads and writes take one leaf Lock. Writers still hold the lifecycle topology gate, so the identity check and later release cannot race another graph writer. The executor claim release runs outside the marker lock; no user or executor code runs under that leaf lock. Compare-by-identity cleanup also protects failure cleanup from removing a newer marker.

The marker changes from HashSet<subject> to Dictionary<subject, SubjectOwnership>. It retains an existing ownership record rather than allocating an epoch object, and the queued record reuses its existing object slot. The dictionary stores an additional reference per entry and the graph adds one lock; no allocation or throughput measurements were run. This is not a public lifecycle-event epoch contract, since only internal release cleanup carries the identity.

## Validation

Focused callback support, publication, and repeated-release tests: **11 passed / 0 failed**. Full Tracking nonintegration suite: **736 passed / 24 failed / 760 total**. Original tests remain **724/747 passed**; spike tests are **12/13 passed**. The exact 24 failure names are unchanged from the publication prototype. See PUBLICATION-SPIKE-REPORT.md for their categories and the inherited limitations; the separate getter orphan still fails.

Red log: `/private/tmp/release-v2-red.log`. Focused green log: `/private/tmp/release-v2-green.log`. Full log: `/private/tmp/release-v2-full.log`. Full results: `src/Namotion.Interceptor.Tracking.Tests/TestResults/release-v2-full.trx`.

The focused command compiled with `dotnet test src/Namotion.Interceptor.Tracking.Tests -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~CallbackReleaseEpochSpikeTests|FullyQualifiedName~CallbackPublicationSpikeTests|FullyQualifiedName~CallbackSupportSpikeTests'`. The full command reused that build with `--no-build --no-restore --filter 'Category!=Integration' --logger 'trx;LogFileName=release-v2-full.trx'`. `git diff --check` passes.

Production increment: **41 added / 14 removed lines** across OwnershipGraph, ReleaseTraversal, and LifecycleNotifier. The new regression is CallbackReleaseEpochSpikeTests.cs.

## Explicit root resurrection follow-up

A second probe confirmed that explicit AttachToContext from the departing subject's callback promoted its retained claim but returned with no graph ownership. The old final release therefore dropped that promoted context claim. The red test failed its immediate post-AttachToContext ownership assertion.

The promotion branch now calls the existing SeedAndAttachComponent helper when the subject is both unowned and releasing, after setting the explicit anchor. This restores the component using the existing attach policy, including descendant seeding and back-edge handling. An already owned subject still only promotes its anchor. Provisional attach requests retain their documented already-attached no-op behavior. Fresh-root ordering policy is unchanged.

The new test confirms root and descendant ownership before the nested call returns, explicit anchor and Registry registration after the outer setter returns, and complete context/Registry/ownership cleanup after a later explicit DetachFromContext. The final-release identity check preserves the restored ownership. This does not remove the prototype's existing getter/discovery or callback-failure limitations.

Follow-up production increment: **6 added / 2 removed lines** in LifecycleInterceptor. Test increment: 49 lines. Focused callback/publication/release tests: **12 passed / 0 failed**. Full Tracking: **737 passed / 24 failed / 761 total**. Original tests remain **724/747 passed**, and all 24 failure names are unchanged. Added spike tests are **13/14 passed**, with the known getter orphan the remaining failure.

Follow-up logs: `/private/tmp/release-root-v2-red.log`, `/private/tmp/release-root-v2-green.log`, `/private/tmp/release-root-v2-full.log`. Full results: `src/Namotion.Interceptor.Tracking.Tests/TestResults/release-root-v2-full.trx`. Commands match the earlier release probe with the additional test included by CallbackReleaseEpochSpikeTests. The final full run reused the successfully compiled focused build. `git diff --check` passes.
