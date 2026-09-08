# Callback support v2 investigation

This is an isolated experiment on `codex/pr494-support-integration-v2`, continuing the support approach with three focused reviewers. The PR branch remains unchanged at `2f896cccc47fc6b66442d6c54b54ab6bd687e5ae`. The original workspace is clean. This candidate is not ready to merge: a confirmed nested reconciliation defect still violates quiescent consistency.

## Integrated improvements

- Preserve ordered lifecycle handler delivery around internal graph descent, including handlers ordered after descent.
- Queue property publications and finish pending lifecycle maintenance before each publication group, so self-writing seeding getters do not notify observers before child Registry admission.
- Retain departing claims through detach delivery; identify each deferred release by its ownership record so an old release cannot clear a newer release after reattachment.
- Support explicit reattachment of a departing subject by restoring root ownership and its component. Failed resurrection reuses the common attach rollback path, covering both root getter failure and failure after some descendants have attached.
- Validate ownership identity and property baseline revisions across seeding getters and enumerable callbacks, preventing stale seeding continuations and replacement/restoration double edges.
- Publish root ownership before seeding, and defer a setter invoked by its own active seeding getter until that getter returns. Active getter state belongs to the exact ownership record, so an old getter cannot defer writes on a reattached owner.
- Preserve a single callback exception's identity, preserve primary write/seed exceptions alongside notification failures, continue notification cleanup, and reject derived exposure of subjects pending release.
- Remove unused inline callback scope bookkeeping. No whole-graph repair scan or per-callback closure was added.

## Remaining blocker

The ordinary reconciler commits its desired property baseline before all corresponding incoming edges are installed. A child getter can reenter that property during this interval.

Example: an outer assignment installs `[branch, stale]`. While attaching `branch`, a descendant getter replaces the property with `replacement`. The nested assignment releases branch and attaches replacement. The outer loop then resumes and attaches stale, even though the final property no longer references it.

A revision-abort guard is insufficient. If the nested assignment instead retains the outer assignment's not-yet-installed second child, nested reconciliation assumes that child's edge already exists. Aborting the outer loop then leaves that retained child unowned. Two probes demonstrate this trade: the original algorithm fails the stale-child case; the temporary guard fails the retained-child case. The temporary guard was discarded.

The evidence is preserved separately in commit `ef6db5cfcda07374a89b3f0220bce48c39ebbb2d`, with [the detailed report](/private/tmp/pr494-support-root-first-v2/RECONCILIATION-REVIEW-BLOCKER.md). A safe next design needs an affected-property record of installed and pending occurrences, or must stage incoming-edge publication before executing getters. Revisions alone cannot provide the missing information. This need not require whole-graph scans, but no linear-cost implementation or small production diff has yet been demonstrated.

## Observable contract changes

This experiment supports same-context structural callback writes. It does not promise the master callback stack or all master observation timing.

- Detach transitions precede the corresponding attach transitions. Lifecycle notifications retain their queued order, while nested work joins the queue; callbacks can therefore describe historical transitions and observe a newer graph.
- Nested callback setters settle graph ownership before returning, but their Registry and derived maintenance wait for queued delivery. An own-property setter inside its active seeding getter is the narrower exception: graph settlement follows the getter's return.
- Pending lifecycle work precedes each property publication group. Property publications remain FIFO within their own stream. There is no global FIFO across the two streams and no settlement boundary between individual subscribers within one publication group.
- Callback failures are post-commit notification failures, not an attach veto. Nested notification failures reach the outer drain rather than the nested setter's catch. Graph-operation failures still take the existing compensation path.
- Explicit-root handlers ordered before descent now observe the root before descendants. Dynamic admission ordering and snapshots inside nested callbacks also differ from original tests.
- Cross-context nested topology operations remain prohibited. The existing gate contention heuristic and other PR limitations were not redesigned.
- Nonterminating user callback mutation can keep the drain running; the candidate does not bound arbitrary user work.

## Verification

Original tests were left unchanged. At production candidate `4af76369`, Tracking completed with **784 passed, 20 failed, 804 total**. The 20 existing failures are classified below; they are not presented as a green suite. Independent reproduction tests additionally establish the unresolved reconciliation defect above.

| Count | Failure interpretation |
| --- | --- |
| 6 | Original assertions require structural callback writes to throw. |
| 7 | Original callback-veto rollback or exception-scope expectations. |
| 1 | Failed admission callback now leaves committed child ownership. |
| 1 | Nested admission observes the host after ownership removal. |
| 2 | Dynamic admission and explicit-root ordering snapshots. |
| 2 | Old tests require the claimed-but-unowned root window that root-first removes; replacement tests exercise the surviving public trigger. |
| 1 | Contention test explicitly reports that it failed to establish overlapping work. |

Ten positive commit-then-notify acceptance/boundary cases verify back edges, provisional adoption, outside support, actual callback-time promotion, nested attach failure delivery, failed admission, releasing-host metadata admission, throwing-detach cleanup, and successful later operations. Five failure-boundary cases verify exception identity and grouping, cleanup, and legitimate claimed-child derived reads. These pass, but do not establish correctness beyond the tested cases.

Other affected unit suites passed on the preceding combined candidate: Core 154, Registry 185, Connectors 797, Dynamic 10, Validation 8. The subsequent changes were focused getter, notification ordering, resurrection cleanup, and test additions. Registry was rerun after the getter and notification boundary changes at `83510bc9`: 185 passed. The other projects were not rerun after those changes. Integration/connector-chaos tests were not run for this isolated lifecycle experiment.

Full Tracking log: `/private/tmp/pr494-final-v2-tests.log`. Machine-readable failure details: `/private/tmp/pr494-final-v2-failures.json`.

## Size and performance

Against PR `2f896ccc`, production changes span 11 files: **409 lines added, 174 removed, net +235**. Those affected files grow from 3,478 to 3,713 lines, **6.8%**; this is not a percentage of the whole library. Relative to the original support candidate, the same files grow by 176 lines. The diff includes comments and declarations as well as executable code. Added tests are separate from these production counts. This is no longer a surgical guard-only fix.

A focused matched comparison used PR `2f896ccc`, original support `2e0ee43b`, and improved support `4af76369`, with identical sources and settings: three rotating fresh-process repetitions per arm, 6 warmups, 15 measured 200 ms iterations, tiering/PGO/ReadyToRun disabled, no competing task builds/tests. All nine runs completed in 356 seconds; source and binary identities were verified unchanged. CPU frequency was not pinned, so these are observational results.

| Ordinary operation | PR median | Original support median | Improved support median | Improved vs PR | Steady-state bytes PR / improved |
| --- | ---: | ---: | ---: | ---: | ---: |
| Scalar write | 94.94 ns | 94.77 ns | 86.98 ns | -8.4% | 0 / 0 |
| Replace one child | 1.831 µs | 2.056 µs | 2.672 µs | +45.9% | 488 / 488 |
| Attach/release 15-node subtree | 20.841 µs | 23.639 µs | 31.593 µs | +51.6% | 9,000 / 9,000 |
| AddLotsOfPreviousCars | 28.020 ms | 36.160 ms | 36.291 ms | +29.5% | 18,616,599 / 18,616,553 |
| Unaffected service-order control | 1.366 µs | 1.320 µs | 1.332 µs | -2.5% | 3,720 / 3,720 |

Do not interpret the scalar decrease as a proven optimization: within-arm timing ranges overlap and code placement/frequency were uncontrolled. The third original-support repetition was conspicuously noisy: its control measured 2.147 µs rather than roughly 1.3 µs, and Cars measured 52.49 ms rather than roughly 35–36 ms. All samples are retained in the report; no run was silently discarded. The +0.4% improved-versus-original-support Cars median difference is not meaningful. Improved-support child replacement and subtree costs were about 30% and 34% above the original-support medians, respectively.

Cars includes constructing and replacing 1,000 cars with four tires each in an already-populated context. These are ordinary operations, not structural writes inside callbacks. The small byte difference on Cars is process accounting variation. Equal measured steady-state allocation does not establish equal cold-start allocation or retained memory: the new dictionaries and queue buffers retain capacity, and benchmark warmup/setup can absorb their growth.

The improved candidate's structural CPU cost is material in these workloads; it also still has the correctness blocker. These numbers therefore support keeping it experimental, not merging it as a complete or cheap fix. No new comparison against master or benchmark of the unresolved nested-write path was run in this iteration.

[Full benchmark table and commit identities](/private/tmp/pr494-support-v2-bdn-results/REPORT.md), [all sample ranges](/private/tmp/pr494-support-v2-bdn-results/summary.json), [run manifest](/private/tmp/pr494-support-v2-bdn-results/manifest.json).

## Recommendation

Keep the support work isolated. The bounded changes improve known failure modes, but merging while the nested reconciliation defect exists would violate the requirement that state must never silently settle inconsistently. The next support experiment should target installed versus pending occurrences for one property, using both failing scenarios as acceptance tests, before adding more guards to the current reconciler. The rejection-contract spike remains a fallback and is not selected by this work. A [read-only design comparison](/private/tmp/pr494-support-root-first-v2/PARTIAL-EDGE-NEXT-DESIGN.md) favors a temporary affected-property installed/pending journal over staging all incoming mutations before descent, primarily to preserve current ordering. It states the assumptions needed for proportional work and does not claim an implemented linear-cost solution.
