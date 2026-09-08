# Getter ownership continuation spike

This is a throwaway continuation of `2e0ee43bb861b9ce79e59528443cc528adec4b99`, on `codex/pr494-support-getter-v2` in `/private/tmp/pr494-support-getter-v2`. It fixes the demonstrated attach-time owner-replacement leak in the frozen callback-support spike. It is not a complete callback-support implementation. No original test was edited, removed, or weakened. No push, GitHub write, integration test, or benchmark was performed.

## Cause and change

`CollectStructuralChildren(seed: true)` invoked user getters and enumerators, then unconditionally wrote a baseline. If that user code removed the owner, seeding recreated the baseline after release had removed it. `SeedAndAttachChildren` subsequently published its captured children for the released owner. A nested release deeper in the graph could also invalidate the remaining captured siblings.

Seeding now captures the existing ownership record, checks that identity after user code, and checks it again before publishing each captured edge. An unpublished anchored root is permitted to seed, retaining the existing back-edge behavior. Baselines are committed only after enumeration and after verifying that the owner and property have not changed. Captured children carry the baseline revision, and publication skips occurrences from an obsolete revision.

A reference comparison alone was insufficient: a new probe replaced a property and restored the exact array instance while descendant seeding ran. The old continuation then duplicated an occurrence, reporting reference count two for one stored occurrence. Baselines now store `(Value, Revision)` inline in the existing dictionary, with a fresh revision for each commit. The revision distinguishes this replacement-and-restoration case without scanning the property again.

Claimed but unpublished subjects now validate the proposed child component before their structural terminal runs. They still skip the authoritative getter reread and reconciliation, preventing recursion when an unpublished root's getter writes its own property. Released, still-claimed subjects retain the existing pass-through behavior. LifecycleNotifier and the publication queue are unchanged.

Production diff against the frozen support head: five files, **81 added / 24 removed lines, net +57**. Two new test files add eight test cases including theory rows.

## Verification

The original getter regression failed before the change, with the wrapper detached but its child still attached. `/private/tmp/getter-v2-red.log` records that failure. The first new batch failed in all four added cases before the implementation: release at depths zero and three, same-property replacement during descendant seeding, and a foreign write from a claimed root. `/private/tmp/getter-v2-red2.log` records those failures. The replacement-and-restoration probe subsequently failed with an incoming count of two before baseline revisions were added; `/private/tmp/getter-v2-full.log` records it.

Final command: `dotnet test src/Namotion.Interceptor.Tracking.Tests --no-restore --filter 'Category!=Integration' -m:1 -p:UseSharedCompilation=false --logger 'trx;LogFileName=getter-v2-full-final.trx'`.

Final result: **737 passed, 28 failed, 765 total**. Log: `/private/tmp/getter-v2-full-final.log`. Results: `src/Namotion.Interceptor.Tracking.Tests/TestResults/getter-v2-full-final.trx`. Comparing failing names against `SPIKE-REPORT.md` confirms the only resolved old failure is `CallbackSupportGetterSpikeTests.WhenGetterReplacesOwnerPropertyDuringAttach_ThenFinalGraphMatchesTheLatestWrite`; all other 27 prior failures remain. The only added failure is the explicit unpublished-root enumerable consistency probe described below. `git diff --check` passes.

Nine of ten getter cases pass: both frozen getter cases; ancestor release at depths zero and three; same-property replacement during descendant descent; release and reattachment of the same owner; claimed-root stable boxed ArraySegment self-write with an explicit trigger assertion; claimed-root foreign-child rejection before storage; and replacement then restoration of the exact property array.

## Remaining defect and limits

`CallbackSupportGetterRemainingLimitTests.WhenUnpublishedRootEnumerableRewritesItsProperty_ThenOwnershipMatchesTheStoredValue` remains red. An enumerable runs while an explicit root is claimed but has no ownership record, then writes a newer value to the root property. That write passes its terminal without reconciling. The outer seed still owns the old child, while the latest stored child remains unowned. The original `ReentrantStructuralWriteTests.WhenAUserEnumerableWritesTheRootWhileTheAttachSeedsIt_ThenTheWritePassesThroughAndTheAttachCompletes` remains untouched and passes because it explicitly characterizes this inconsistent state as expected. The new probe asserts graph consistency independently and fails with replacement context null.

Solving that case requires a protocol that records writes while an unpublished root is being seeded and captures their authoritative stored results. An unconditional getter retry risks nontermination for a stable self-writing getter; treating the proposed value as authoritative is unsound for normalizing terminals. This continuation does not invent either shortcut. It also does not add a general mutation-epoch contract to `StructuralReconciler`, redesign exception settlement, or establish correctness for arbitrary side-effecting metadata, collection keys, or enumerators. The inherited notification-ordering and exception-contract failures remain as documented in `SPIKE-REPORT.md`.

## Cost

The new ordinary seeding work remains linear in structural properties and captured child occurrences. Each freshness check is an expected constant-time dictionary or ownership-map lookup; there is no whole-graph scan, per-edge collection rescan, or retry loop. Existing graph algorithms and their costs are otherwise unchanged.

Each retained baseline dictionary value grows by eight bytes for its revision on a 64-bit runtime. Each pooled child tuple grows by eight bytes for its captured revision, including the shared release scratch list. There is no new per-baseline or per-edge heap object. Baseline commits increment one per-context counter. Claimed-but-unpublished structural writes now pay the proposed-component validation and temporary claim bookkeeping they previously bypassed. Scratch buffers remain pooled and retain peak capacity. These are structural cost observations, not benchmark results.
