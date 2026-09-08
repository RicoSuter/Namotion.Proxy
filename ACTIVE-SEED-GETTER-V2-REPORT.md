# Active seeding getter continuation

This bounded follow-up starts from tiny root-first commit `1bf90667` on `codex/pr494-support-root-first-v2`, in `/private/tmp/pr494-support-root-first-v2`. The branch already includes getter candidate `b63d80fe` and publication candidate `2838fcda` cherry-picked as `506c4e81`. No original or earlier-spike test was changed, removed, or weakened. No benchmark, integration test, push, or GitHub write was performed.

## Result and contract

The stable boxed ArraySegment root getter now terminates, while the root-first public-state enumerable consistency test continues to pass. The enclosing seeding getter supplies the authoritative stored value after its nested same-property setter returns. A normalizing terminal's actual subset, rather than the proposed collection, determines the final attached children in the new probe.

**A setter invoked from its own active seeding getter returns with that property's graph reconciliation still pending.** The setter validates its proposed component and runs its terminal, but graph publication waits for the enclosing getter to return. A probe observes reference count zero immediately after the nested setter and reference count one after attach completes. This is an explicit nested-call contract consequence, not synchronous settlement at every setter return.

Only the exact active property receives this treatment. A different property's setter continues through ordinary reconciliation before returning. Enumeration begins after the getter marker has been removed, so enumeration-phase writes also reconcile normally. There is no equality shortcut, arbitrary retry bound, blanket suspension of structural writes, or assumption that the proposed value is the terminal's stored result.

This remains a callback-support experiment with inherited correctness and contract failures. The passing probes are evidence for this narrow protocol, not a proof for arbitrary user getters, terminal side effects, metadata implementations, or all rollback paths.

## Implementation

`OwnershipGraph` keeps a counted dictionary of active seeding getter properties, using `PropertyReference.Comparer`. It increments the count immediately before invoking a seeding getter and decrements or removes it in `finally`. Counts preserve nested evaluation of the same property, including a release and reattach while an older getter frame remains active. `WriteProperty` checks this marker after validating and claiming the proposed component, then defers its own authoritative reread and reconciliation only for that property.

The other-property probe exposed a companion issue: its nested setter correctly published its property immediately, but the later outer seed enumerated that already-committed property again and duplicated the incoming occurrence. Seeding now skips a property that already has a committed baseline, since the nested normal write already published those edges.

Follow-up production diff relative to `1bf90667`: two files, **31 added / 4 removed lines, net +27**. Combined tiny root-first plus this follow-up relative to `506c4e81`: two files, **33 added / 13 removed lines, net +20**. Three new tests are added in `CallbackActiveSeedGetterSpikeTests.cs`.

## Verification

Before this change, `/private/tmp/root-first-active-red.log` records three focused failures: the stable self-writing root getter exceeded its 16-read termination guard; the normalized-subset probe observed immediate reconciliation instead of the intended deferred phase; and the other-property probe reported incoming count two for a single stored occurrence. Its nested setter had correctly reported count one before the outer seed duplicated it. The exception cleanup probe passed before the change and remains regression coverage for the newly introduced marker.

The first focused run after the fix passed **9/9**, including the five root-first probes, stable boxed root getter, normalized subset, exception cleanup, and immediate reconciliation of another property. A first full run with list-based membership passed 749 tests and failed 26. The list was then replaced before freezing with the counted dictionary to avoid lookup cost growing linearly with getter nesting depth.

Final command: `dotnet test src/Namotion.Interceptor.Tracking.Tests --no-restore --filter 'Category!=Integration' -m:1 -p:UseSharedCompilation=false --logger 'trx;LogFileName=root-first-active-dictionary-full.trx'`.

Final result: **749 passed, 26 failed, 775 total**. Log: `/private/tmp/root-first-active-dictionary-full.log`. TRX: `src/Namotion.Interceptor.Tracking.Tests/TestResults/root-first-active-dictionary-full.trx`. `git diff --check` passes.

Relative to the frozen tiny root-first run (745 passed, 27 failed, 772 total), the only changed existing outcome is that `WhenUnpublishedRootGetterWritesTheStableBoxedSegment_ThenSeedingTerminates` now passes. All three added tests pass, and no previously passing test changes to failing. The new tests cover:

- Normalizing a proposed two-child segment to the actual one-child subset, asserting both the deferred nested phase and final ownership.
- Throwing after a deferred write, then reattaching and performing a normal write to the same property. This verifies marker cleanup and proves subsequent writes are not accidentally deferred.
- Writing another property from the seeding getter, observing its ownership immediately after its setter, and checking that the final seed does not publish it twice.

The public-state root enumeration probe remains passing. The two earlier probes that require private `!Graph.IsOwned(root)` state remain failing because root-first publication removes their triggering phase. The root-before-descendant ordering change and its snapshot failure remain as documented in `ROOT-FIRST-V2-REPORT.md`. The other inherited failures are unchanged.

## Cost and limits

Active-property membership is expected O(1), including nested getter writes. Ordinary structural writes with no active getter take the dictionary count fast path and perform no property hash lookup for this check. Each structural seeding getter performs counted dictionary insertion and finally cleanup. The dictionary retains peak capacity and adds a per-context object plus backing storage on first use; there is no per-evaluation heap allocation after capacity warmup. The maximum retained entry count is the number of distinct simultaneously active seeding properties, not graph size. Nested evaluations of one property share one counted entry.

The existing property/occurrence traversal remains linear with expected constant-time ownership, baseline-revision, and active-property checks. This follow-up introduces no graph-wide scan, per-edge collection rescan, or retry loop. These are structural complexity observations, not measured performance results.

A getter that returns a stale captured value after mutating its own backing store is still responsible for the mismatch between its returned value and the store; this experiment treats the seeding getter's return as authoritative, matching the surrounding protocol. It does not attempt to discover arbitrary hidden terminal effects. Exceptions can still interact with the inherited commit/publication and rollback contracts; only the new simple marker-cleanup and existing narrow rollback probes are demonstrated here.
