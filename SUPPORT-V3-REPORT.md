# Callback support v3

The candidate is isolated on `codex/pr494-support-integration-v3`. Production is frozen at `ffdaf2f4`. The PR branch and original review workspace have not been modified or pushed. This report supersedes the earlier spike reports for the integrated candidate; their failed-test counts and limitations describe historical intermediate commits.

## Design selected

Capture a property's desired occurrences at structural commit, and retain a temporary journal of occurrences actually installed while seeding or reconciliation is incomplete. Release and reachability use those captures without re-enumerating consumer collections. This removes the unstable release/adoption queries instead of adding retry loops or replaying callback history. Immutable GC-owned arrays keep enclosing snapshots valid across nested overwrites; null and direct subject values use no occurrence array. See the canonical [lifecycle design](docs/design/tracking-lifecycle.md) for the contract.

The integrated candidate fixes the reproduced nested getter/callback ownership defects, duplicate occurrence indices, failed child-seed retry, queued detach claim lifetime, and metadata admission from unowned subjects or an active seeding getter. Registry now handles object-typed positional collections and retained duplicate occurrences, preserving dictionary keys. Independent checks compare actual storage, ownership, baselines, both Registry directions, anchor state and complete teardown.

The release-retry alternative was rejected. It passed an intermediate suite but still produced obsolete Registry additions or consumed an unsupported provisional anchor when membership enumeration removed the edge being published. The retained-capture design removes that enumeration surface. The independent acceptance tests fail on the old implementation and pass on the integrated candidate; they do not require incidental old-value enumerations to occur.

## Performance work

The common direct-child path avoids the general reconciler's two occurrence lists and two subject counters. Getter frames, a single active journal and one journal occurrence stay inline. A releasing ownership record doubles as its cleanup identity token, removing a separate dictionary and lock. Queued notifications use a smaller record and avoid repeated public extension/service/gate dispatch. Registry duplicate/keyed matching uses an indexed slot chain rather than searching the child list from the beginning for each subject.

The captured baseline adds one reference to each baseline entry and one immutable array per nonempty collection capture. It also removes repeated enumeration and index boxing during old-side reconciliation and release. Steady-state allocations and retained live memory are different measurements; the benchmark allocation column alone cannot establish the latter. No array pooling lifetime protocol was added.

Registry's unique positional path remains linear. The duplicate path still sorts ordinal entries, and updating many occurrences of the same subject can scan its parent list repeatedly. Dense aliasing can therefore remain quadratic; this candidate does not claim linear cost for every graph shape or arbitrary nested workload. User callbacks that repeatedly rewrite a growing collection also necessarily cause repeated requested work.

## Verification

Final commands use Release builds, `Category!=Integration`, `-m:1` and `-p:UseSharedCompilation=false`. All seven relevant project suites passed on the frozen integrated candidate:

| Project | Passed | Failed |
| --- | ---: | ---: |
| Tracking | 855 | 0 |
| Core | 154 | 0 |
| Registry | 192 | 0 |
| Connectors | 797 | 0 |
| Dynamic | 10 | 0 |
| Validation | 8 | 0 |
| Hosting | 12 | 0 |
| Total | 2028 | 0 |

Each project's final TRX is `TestResults/v3-final.trx`; logs are `/private/tmp/pr494-v3-final-<project>.log`. No connector integration or long-running connector tester was run: this spike changes lifecycle/Registry behavior, not connector implementations. This is not a claim that the entire solution's test suite ran.

The 20 former callback-contract/ordering/trigger failures were migrated explicitly to the approved support semantics, with positive ownership and cleanup assertions rather than skipped tests. One reentrancy test that depended on old-collection re-enumeration now triggers its nested write during new-value capture; separate tests require committed graph reads not to re-enumerate. Public API changes are not introduced by this candidate; interface edits describe its changed behavior.

## Compatibility boundaries

Same-context structural callback writes are supported, and replacement delivers detach before attach. Queued events describe historical transitions; nested setters settle ownership before their queued Registry and other callbacks run. Lifecycle work drains before each FIFO property publication group, with no extra boundary between observers inside that group. Explicit-root handlers before descent now receive the root before descendants.

Committed collection membership is a snapshot until a later effective structural assignment. Release, adoption reachability, old-side diff and internal seed recovery no longer repeat committed-enumerator side effects or exceptions. New-value discovery/capture and consumer refresh callbacks may still enumerate. A settled same-reference assignment keeps its existing no-op behavior; raw collection mutation without an intercepted assignment is not tracked.

Callback errors are post-commit notification failures, not vetoes: remaining queued handlers and cleanup are attempted, one error preserves its identity, and multiple errors include the primary operation failure first. An arbitrary throwing structural getter can leave an interrupted seed until retry or detach, with a reported exception; this is recovery support, not strong rollback of arbitrary consumer code. A hand-written terminal that mutates storage without reporting commit remains outside the normal write recovery guarantee. Cross-context nested topology is still rejected, and the existing topology-gate contention heuristic is unchanged.

## Scope relative to PR

Against PR head `2f896cccc47fc6b66442d6c54b54ab6bd687e5ae`, production sources contain **1,028 added and 578 removed lines, net 450**, across 21 files. This includes production comments and XML documentation, excludes tests, benchmarks and standalone documentation, and includes the Registry corrections. It is a substantive support change, not a one-line patch. Retained capture itself removed 76 net production lines; the two obsolete admission shortcuts removed a further 100.

## Matched benchmark

The identical harness compared pinned master `5e4da907`, PR `2f896ccc`, pre-capture support `380a51d0` with the Registry correctness fix, and final support `ffdaf2f4`. Five ordinary rows ran on all four arms; a duplicate Registry reorder ran only on the two corrected support arms. Three rotating fresh-process repetitions used six warmups and fifteen measured 200 ms iterations. The comparison completed in 494.6 seconds, with source and binary identities verified unchanged. Builds and tests were stopped before timing.

| Workload | Master median | PR median | Final median | Final vs PR | Allocation, PR → final |
| --- | ---: | ---: | ---: | ---: | ---: |
| Attached scalar write | 94.33 ns | 88.60 ns | 82.55 ns | -6.8% | 0 → 0 B |
| Replace one child | 1.543 µs | 1.679 µs | 1.929 µs | +14.9% | 488 → 488 B |
| Attach and release 15-node subtree | 21.75 µs | 19.33 µs | 25.17 µs | +30.2% | 9,000 → 8,832 B |
| AddLotsOfPreviousCars | 32.16 ms | 27.33 ms | 33.74 ms | +23.5% | 18,616,854 → 18,568,647 B |
| Unchanged service-order control | 1.237 µs | 1.231 µs | 1.232 µs | +0.1% | 3,720 → 3,720 B |

The candidate is still slower than the PR for the three structural workloads. `AddLotsOfPreviousCars` is 4.9% above the pinned master median. The repeated ranges for that row were master 32.01–34.21 ms, PR 27.10–27.67 ms, and final 33.69–33.97 ms. The structural CPU cost cannot be dismissed as a tiny control-row fluctuation. Conversely, the scalar improvement is not an isolated proof of a particular optimization: code placement also changes between binaries.

Against the immediate pre-capture support candidate, the new design changes direct-child time by -1.1%, subtree time by -3.1%, and Cars by -0.3%. Treat those small timing differences cautiously; capture is chosen for correctness and simpler maintenance, not a demonstrated large ordinary-workload speedup. The duplicate Registry reorder is a stronger measured improvement: **553.17 → 321.38 µs (-41.9%)**, with **98,528 → 90,320 B** allocated. That comparison includes both retained capture and indexed Registry slot matching. Master/PR were excluded from this extra row because their duplicate projection is incorrect.

CPU frequency was unpinned, and two sustained background Rider processes consumed about a core each, with additional desktop activity. All timings are therefore observational, not a quiet-machine performance certification. Allocation results are less sensitive to that activity. All repetitions, including the higher first master/control samples, are retained. Nothing here proves globally maximal performance or linear cost for every aliasing shape.

Full samples, ranges, binary/source hashes, logs and the completed manifest are in [the benchmark report](/private/tmp/pr494-support-v3-bdn-results/REPORT.md) and [manifest](/private/tmp/pr494-support-v3-bdn-results/manifest.json). The harness is `/private/tmp/pr494-support-v3-bdn`; it is outside the repository so all arms use the same sources without changing production or benchmark code on the PR.

## Recommendation

Use retained committed captures with installed-occurrence journals and ordered notification delivery. The alternative of re-enumerating committed collections and retrying unstable graph decisions remained incorrect in finite consumer scenarios. The chosen candidate removes those windows, passes all 2,028 relevant tests, lowers measured allocations in the collection/subtree workloads, and retains the explicit callback/error contract. Its remaining ordinary structural CPU overhead is a real tradeoff for review. The spike is ready to review as a concrete integration candidate; it has not been merged into or pushed to PR #494.
