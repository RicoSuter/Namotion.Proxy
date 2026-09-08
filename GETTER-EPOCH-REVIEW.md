# Getter marker ownership-epoch review

Review target: integration commit `9a283c0aa55b499684500835ff3667ede0feb10d`. The integration worktree was inspected read-only. The reproduction and fix were developed in `/private/tmp/pr494-support-root-first-v2` from `fca771ee9f550bef8b18b701da44b06ef864711d`. The relevant integrated getter code matches that candidate. No change was made to exception reconciliation or notification publication.

## Fixed finding

An active seeding getter could release its owner, reattach that same subject, and then write its own property. The old property-only marker still matched, so the new ownership's setter deferred reconciliation to the old getter frame. When that frame returned, the existing ownership-identity guard correctly discarded the obsolete seed. Nothing then reconciled the newer field: the stored replacement remained unowned and the previous child remained attached.

`CallbackGetterEpochReviewTests.WhenGetterReattachesItsOwnerAndThenWrites_ThenTheNewOwnershipReconcilesTheWrite` reproduces this through public setters. Its wrapper getter clears `root.Payload`, restores the same wrapper, then changes `wrapper.Children`. Before the fix, the latest child has context null after the whole operation completes. `/private/tmp/ownership-review-red.log` records the failure.

Active-getter counts are now keyed by `(PropertyReference, SubjectOwnership record)`. A deferred write must belong to the same ownership record as the enclosing seeding getter. An old frame cannot defer writes after reattachment. The counted-key cleanup still handles nesting and runs in `finally`; the empty-count fast path remains. The composite key is a value tuple, and both components use the intended identity semantics: PropertyReference supplies subject-identity/property-name equality, and SubjectOwnership retains reference equality.

Production diff: one file, **8 added / 6 removed lines, net +2**. Membership remains expected O(1), with one extra ownership-map lookup only while active getters exist. Each retained dictionary key grows by one ownership-record reference; no per-evaluation object is allocated after dictionary capacity warmup.

## Verification

Final command: `dotnet test src/Namotion.Interceptor.Tracking.Tests --no-restore --filter 'Category!=Integration' -m:1 -p:UseSharedCompilation=false --logger 'trx;LogFileName=getter-epoch-full.trx'`.

Result: **750 passed, 26 failed, 776 total**. Log: `/private/tmp/getter-epoch-full.log`. TRX: `src/Namotion.Interceptor.Tracking.Tests/TestResults/getter-epoch-full.trx`. The earlier candidate was 749 passed, 26 failed, 775 total; the new regression passes and existing outcome counts are unchanged. `git diff --check` passes. No benchmark, integration test, push, or GitHub write was performed.

## Separate unresolved reconciliation finding

An ordinary write of `root.Payload = [branch, stale]` can attach `branch`, invoke its descendant getter, and have that getter replace `root.Payload` with a different subject. The nested write reconciles its new value, but the outer addition loop checks only that root is still owned, then attaches `stale` to the obsolete baseline. This leaves an attached orphan. It is reproduced independently in `/private/tmp/ownership-review-red.log`; the getter-marker fix does not address it.

Simply aborting the outer loop when its baseline revision changes is insufficient. If the nested write retains an occurrence that the outer loop has not yet published, nested reconciliation currently assumes that occurrence already exists because it appears in the committed baseline. Aborting the outer loop then leaves the retained child unowned. These reconciliation probes are kept separate from the isolated marker-fix test, and no broad transaction redesign is included in this commit.
