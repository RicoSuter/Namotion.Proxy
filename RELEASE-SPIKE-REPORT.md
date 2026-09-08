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

## Adjacent unresolved question

Code inspection suggests that explicit AttachToContext from a departing subject's callback may only promote its retained executor claim without restoring graph ownership, because the attached-context branch returns early. Final release still tests graph ownership. This probe verifies parent-edge reattachment only; explicit-root resurrection is not tested or fixed here and was reported to the coordinating investigation.
