# Callback support contract migration

This isolated test migration starts at `801f7c47`. Production sources are unchanged. The original assertions remain available at that commit; this branch makes the changed contract and its positive settlement evidence independently reviewable.

The previous full Tracking result was 784 passing and 20 failing tests. The migrated suite passes all 804 tests. This does not resolve or incorporate the separate ordinary getter reconciliation defects: the 12-case `CallbackGraphOracleTests` commit `40043663` remains separate, and six of those cases fail against the unchanged production baseline.

## Contract decisions covered

| Previous failure group | Cases | Replacement evidence |
| --- | ---: | --- |
| Same-context callback structural writes, attach, and detach expected rejection | 6 | The callback runs and returns without rejection; actual storage, ownership, incoming occurrences, Registry, and independent root teardown agree. |
| Attach callback failures expected veto rollback or nested callback failures expected to escape the nested operation | 7 | The component remains committed, provisional anchors stay consumed, independently supported incoming edges retain their exact counts, and later explicit detach cleans up. Nested attach returns before its callback failure escapes the outer drain. A real callback promotion survives parent removal. Attach and detach callback exceptions belong to their separate operations. |
| Metadata admission callback failure expected the published child's claim to be released | 1 | Metadata, baseline, child ownership, and Registry remain committed after the exception; explicit root teardown removes the complete graph. |
| Nested metadata admission expected the host's obsolete incoming count and transient adoption events | 1 | A delayed detach callback sees host count zero. The admitted metadata remains on the released host without a baseline or child edge; the referenced provisional root keeps its anchor. |
| Notification snapshots expected the previous interleaving | 2 | Root ownership notification precedes descendant notifications. A property admitted during an attach callback is notified after already queued properties, exactly once. |
| Getter probes depended on a claimed but unowned root phase | 2 | Public context attachment arms each one-shot write. Both probes assert that the nested assignment actually executed, that the stored replacement owns the final edge, and that the stale value and all teardown state are released. |
| Contention test inferred overlap from elapsed time | 1 | A structural getter keeps the gate holder runnable. Synchronization observes the contender waiting before releasing the holder, then verifies successful ordered completion and complete graph cleanup. There are no sleeps, duration thresholds, or large-tree workload assumptions. |

The deterministic contention replacement proves actual overlap and serialization. It does not prove that a holder remained runnable longer than the deadlock conviction threshold; the old elapsed-time assertion did not reliably establish that coverage either.

The root snapshot intentionally changes from `Mother2, Mother3, Mother1` to `Mother1, Mother2, Mother3`. No production handler ordering changes were made here. Root ownership now exists before seeding, and external notification preserves the current declared order around descent. The `FooBar` snapshot intentionally moves its single property notification after the previously queued built-in properties.

`SupportContractAssertions` traverses the actual settled storage from the supplied anchors and compares exact incoming occurrences, parent indices and counts, both Registry edge directions, complete Registry membership, and structural baselines. Released subjects must have no executor context, ownership, anchor, release marker, parent edge, Registry entry, or baseline. Existing graph assertions were replaced with stronger positive committed-state assertions, not removed to suppress errors. The separate getter oracle is not weakened.

## Verification

Initial targeted migration run: 118 passed, zero failed. After strengthening baseline checks and narrowing fixture changes, the complete non-integration Tracking suite passed 804 tests with zero failures or skips, in 10 seconds of reported test duration.

Run the full suite from `/private/tmp/pr494-support-contract-migration-v3`:

```sh
dotnet test src/Namotion.Interceptor.Tracking.Tests -m:1 -p:UseSharedCompilation=false --filter 'Category!=Integration' --logger 'trx;LogFileName=contract-migration-full.trx'
```

Full log: `/private/tmp/pr494-contract-migration-full.log`. TRX: `src/Namotion.Interceptor.Tracking.Tests/TestResults/contract-migration-full.trx`. No production edits, benchmarks, original-baseline edits, or pushes were made.

Follow-up verification rejects every subject absent from the declared fixture universe instead of assigning unknown subjects a shared identity. No fixture omissions were found: the full suite again passed 804 tests with zero failures or skips. Log: `/private/tmp/pr494-contract-universe-full.log`; TRX: `src/Namotion.Interceptor.Tracking.Tests/TestResults/contract-universe-full.trx`.
