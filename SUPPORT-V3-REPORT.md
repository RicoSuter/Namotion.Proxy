# Callback support v3

The candidate is isolated on `codex/pr494-support-integration-v3`. The measured production baseline is `ffdaf2f4`; the subsequent correctness pass is integrated through `562691d1` and has not been benchmarked again. The PR branch and original review workspace have not been modified or pushed. This report supersedes the earlier spike reports for the integrated candidate; their failed-test counts and limitations describe historical intermediate commits.

## Design selected

Capture a property's desired occurrences at structural commit, and retain a temporary journal of occurrences actually installed while seeding or reconciliation is incomplete. Release and reachability use those captures without re-enumerating consumer collections. This removes the unstable release/adoption queries instead of adding retry loops or replaying callback history. Immutable GC-owned arrays keep enclosing snapshots valid across nested overwrites; null and direct subject values use no occurrence array. See the canonical [lifecycle design](docs/design/tracking-lifecycle.md) for the contract.

The integrated candidate fixes the reproduced nested getter/callback ownership defects, duplicate occurrence indices, failed child-seed retry, queued detach claim lifetime, and metadata admission from unowned subjects or an active seeding getter. Registry now handles object-typed positional collections and retained duplicate occurrences, preserving dictionary keys. Independent checks compare actual storage, ownership, baselines, both Registry directions, anchor state and complete teardown.

The release-retry alternative was rejected. It passed an intermediate suite but still produced obsolete Registry additions or consumed an unsupported provisional anchor when membership enumeration removed the edge being published. The retained-capture design removes that enumeration surface. The independent acceptance tests fail on the old implementation and pass on the integrated candidate; they do not require incidental old-value enumerations to occur.

## Performance work

The common direct-child path avoids the general reconciler's two occurrence lists and two subject counters. Getter frames, a single active journal and one journal occurrence stay inline. A releasing ownership record doubles as its cleanup identity token, removing a separate dictionary and lock. Queued notifications use a smaller record and avoid repeated public extension/service/gate dispatch. Registry duplicate/keyed matching uses an indexed slot chain rather than searching the child list from the beginning for each subject.

The captured baseline adds one reference to each baseline entry and one immutable array per nonempty collection capture. It also removes repeated enumeration and index boxing during old-side reconciliation and release. Steady-state allocations and retained live memory are different measurements; the benchmark allocation column alone cannot establish the latter. No array pooling lifetime protocol was added.

Registry's unique positional path remains linear. The duplicate path still sorts ordinal entries, and updating many occurrences of the same subject can scan its parent list repeatedly. Dense aliasing can therefore remain quadratic; this candidate does not claim linear cost for every graph shape or arbitrary nested workload. User callbacks that repeatedly rewrite a growing collection also necessarily cause repeated requested work.

## Correctness pass after benchmarking

Six bounded fixes are integrated after the measured baseline:

1. **Rejected attach with outside support:** remove the failed root anchor and use normal reachability release. Do not drain installed child edges from a root that still has independent support. Restored provisional anchors and interrupted-property retry retain their existing recovery roles.
2. **Stale attach rollback:** scope rollback to the root attachment revision belonging to the failed operation. A getter that detaches and explicitly reattaches the root must not have its newer anchor removed by the older failed attach, including when an outside edge keeps the same ownership record alive.
3. **Gate wait identity:** reset the blocked-holder observation window for each outer transaction, even if the same thread immediately reacquires the gate. The timeout values and heuristic remain unchanged.
4. **Constructor declaration order:** inspect all eligible nonobsolete constructors, including partial declarations. Preserve the established public context-only overload when a parameterless constructor exists, and respect an exact handwritten context constructor. The first-constructor selection problem was inherited; the PR's fallback additionally exposed accessibility differences.
5. **Disconnected storage method discovery:** turn the private throwing storage client getter into a method so it is not registered as a subject property. This fixes the concrete inherited browser circuit failure without narrowing the public method-discovery contract.
6. **Browser startup readiness:** distinguish early root publication from completed loading, attachment and service registration. The browser awaits completion before resolving its Registry and cancels its own wait on disposal. This covers the PR's incomplete-Registry window and the inherited root-null startup window. The new public `RootManager.LoadingCompleted` task reports success, failure or cancellation; `IsLoaded` now means successful completion.

Independent reviews found no remaining blocker in these fixes. The four library fixes remove five net production lines; the storage and browser fixes add 25, for **20 net production lines** in this pass. Regression tests exercise supported descendants, provisional cycles, replacement root anchors, successive transactions, constructor order, disconnected storage, pre-Registry attachment, startup failure/cancellation and interactive browser startup. Failing-before/passing-after checks were recorded; controls intentionally already passed before the relevant fix.

## Verification

Library commands use Release builds, `Category!=Integration`, `-m:1` and `-p:UseSharedCompilation=false`. The final library state at `71abea0c` passed eight complete relevant suites:

| Project | Passed | Failed |
| --- | ---: | ---: |
| Tracking | 864 | 0 |
| Generator | 289 | 0 |
| Core | 154 | 0 |
| Registry | 192 | 0 |
| Connectors | 797 | 0 |
| Dynamic | 10 | 0 |
| Validation | 8 | 0 |
| Hosting | 12 | 0 |
| Library total | 2326 | 0 |

Each project's final TRX is `TestResults/correctness-final.trx`; logs are `/private/tmp/pr494-correctness-final-<project>.log`. The subsequent storage and browser changes do not alter those library sources. Integrated HomeBlaze Storage passed 51/51 and Services passed 225/225. The five targeted startup/navigation browser tests also passed on the integrated branch, for **2,607 passed, zero failed and zero skipped** across these eleven selected suites/runs. The full browser suite was not run. No connector integration or long-running connector tester was run: this spike changes lifecycle/Registry behavior, not connector implementations. This is not a claim that the entire solution's test suite ran.

The 20 former callback-contract/ordering/trigger failures were migrated explicitly to the approved support semantics, with positive ownership and cleanup assertions rather than skipped tests. One reentrancy test that depended on old-collection re-enumeration now triggers its nested write during new-value capture; separate tests require committed graph reads not to re-enumerate. The callback support implementation adds no library public API; interface edits describe changed behavior. The HomeBlaze readiness task is an additive application service API.

### HomeBlaze dependency resolution

A fresh integrated restore encountered NU1103 after the existing floating `Microsoft.Extensions.Hosting.Abstractions` `10.*` references selected 10.0.12 with unavailable transitive dependencies. Services validation used an external temporary MSBuild override selecting the previously successful 10.0.11, with no repository package edits. Its log is `/private/tmp/pr494-correctness-final-HomeBlaze.Services.Tests-pinned.log`.

For the browser build, an isolated local feed was assembled from the reviewed worktree's package archives plus cached compiler/reference-pack dependencies, then restored into an isolated package directory. The final E2E package identity/version set matches the previously successful reviewed E2E assets exactly. This avoids depending on incomplete newly selected floating packages; it does not fix the repository's unrestricted fresh restore. The existing NU1603 in the separate `Namotion.NuGet.Plugins` graph remains a visible warning through `-p:WarningsNotAsErrors=NU1603`, with DI.Abstractions 10.0.0 selected there. No package declarations changed.

The integrated browser command uses Release, `-m:1`, `-p:UseSharedCompilation=false`, `--filter 'FullyQualifiedName~BrowserStartupTests|FullyQualifiedName~NavigationTests'`, `-p:WarningsNotAsErrors=NU1603`, `-p:RestoreSources=/private/tmp/pr494-reviewed-nuget-feed`, and `-p:RestorePackagesPath=/private/tmp/pr494-reviewed-nuget-packages`. Its log is `/private/tmp/pr494-correctness-final-HomeBlaze.E2E.Tests-reviewed-feed.log`. Regression mutation evidence is `/private/tmp/pr494-browser-e2e-final-red.log`: restoring only the old browser component makes the exact default-storage startup test fail while the new readiness task and storage helper remain.

## Compatibility boundaries

Same-context structural callback writes are supported, and replacement delivers detach before attach. Queued events describe historical transitions; nested setters settle ownership before their queued Registry and other callbacks run. Lifecycle work drains before each FIFO property publication group, with no extra boundary between observers inside that group. Explicit-root handlers before descent now receive the root before descendants.

Committed collection membership is a snapshot until a later effective structural assignment. Release, adoption reachability, old-side diff and internal seed recovery no longer repeat committed-enumerator side effects or exceptions. New-value discovery/capture and consumer refresh callbacks may still enumerate. A settled same-reference assignment keeps its existing no-op behavior; raw collection mutation without an intercepted assignment is not tracked.

Callback errors are post-commit notification failures, not vetoes: remaining queued handlers and cleanup are attempted, one error preserves its identity, and multiple errors include the primary operation failure first. An arbitrary throwing structural getter can leave an interrupted seed until retry or detach, with a reported exception; this is recovery support, not strong rollback of arbitrary consumer code. A hand-written terminal that mutates storage without reporting commit remains outside the normal write recovery guarantee. Cross-context nested topology is still rejected, and the topology-gate contention heuristic retains its thresholds but now distinguishes successive transactions by revision.

## Scope relative to PR

Against PR head `2f896cccc47fc6b66442d6c54b54ab6bd687e5ae`, production sources contain **1,151 added and 681 removed lines, net 470**, across 27 files. This includes production comments and XML documentation, excludes tests, benchmarks and standalone documentation, and includes the Registry and HomeBlaze corrections. It is a substantive support change. The latest correctness pass adds 20 net production lines relative to `78f17a15`. Retained capture itself previously removed 76 net production lines; the two obsolete admission shortcuts removed a further 100.

## Matched benchmark

All numbers in this section describe `ffdaf2f4`, before the six correctness fixes above. No benchmark was rerun during the correctness pass.

The identical harness compared pinned master `5e4da907`, PR `2f896ccc`, pre-capture support `380a51d0` with the Registry correctness fix, and final support production `ffdaf2f4`. The quiet rerun recorded candidate HEAD `6b6ad686`, which adds only this report to the frozen production commit. Five ordinary rows ran on all four arms; a duplicate Registry reorder ran only on the two corrected support arms. These rows measure ordinary operations, not structural writes inside callbacks.

The quieter rerun completed on September 8 in 480.8 seconds. Three rotating fresh-process repetitions used six warmups and fifteen measured 200 ms iterations. Production diffs, shared benchmark sources and top-level input binaries matched the earlier run and remained unchanged during the rerun. BenchmarkDotNet generated and built its own harness before each measurement run; the measured generated binaries were not proved byte-identical across runs. Unrelated builds and tests were paused. Tiered compilation, PGO and ReadyToRun were disabled, so these results describe that JIT configuration.

| Workload | Master median | PR median | Final median | Final vs master | Final vs PR | Allocation, PR → final |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Attached scalar write | 90.13 ns | 84.73 ns | 79.30 ns | -12.0% | -6.4% | 0 → 0 B |
| Replace one child | 1.424 µs | 1.571 µs | 1.847 µs | +29.7% | +17.6% | 488 → 488 B |
| Attach and release 15-node subtree | 20.76 µs | 18.45 µs | 24.45 µs | +17.8% | +32.5% | 9,000 → 8,832 B |
| AddLotsOfPreviousCars | 29.92 ms | 26.64 ms | 30.75 ms | +2.8% | +15.4% | 18,616,668 → 18,568,650 B |
| Unchanged service-order control | 1.186 µs | 1.194 µs | 1.191 µs | +0.4% | -0.3% | 3,720 → 3,720 B |

The candidate is still slower than the PR for the three structural workloads. `AddLotsOfPreviousCars` is 2.8% above the pinned master median; its repeated ranges were master 29.85–30.04 ms, PR 26.63–26.72 ms, and final 30.33–30.89 ms. These ranges do not overlap, although three runs on an unpinned desktop do not establish a universal regression percentage. Cars allocates about 5.6% less than master (19,672,655 → 18,568,650 B) and 0.26% less than the PR. The child/subtree CPU cost remains substantial. Conversely, the scalar improvement is not an isolated proof of a particular optimization: code placement also changes between binaries.

Against the immediate pre-capture support candidate, the new design changes direct-child time by -1.5%, subtree time by -2.0%, and Cars by -0.9%. Treat those small timing differences cautiously; capture is chosen for correctness and simpler maintenance, not a demonstrated large ordinary-workload speedup. The duplicate Registry reorder is a stronger measured improvement: **525.99 → 307.85 µs (-41.5%)**, with **98,528 → 90,320 B** allocated. That comparison includes both retained capture and indexed Registry slot matching. Master/PR were excluded from this extra row because their duplicate projection is incorrect.

The earlier run with sustained Rider activity reported Cars at master 32.16 ms, PR 27.33 ms and final 33.74 ms, corresponding to +4.9% versus master and +23.5% versus PR. The quieter rerun reduces the estimated Cars overhead but confirms the structural overhead and allocation conclusions. The original results remain preserved in [the earlier benchmark report](/private/tmp/pr494-support-v3-bdn-results/REPORT.md) and [manifest](/private/tmp/pr494-support-v3-bdn-results/manifest.json).

CPU frequency remains unpinned and desktop services remain active. The before snapshot showed 89.2% idle CPU without the sustained Rider processes. The after snapshot caught MSBuild workers that started at 19:23:16, after the final measurement log and completed manifest were written at 19:23:00; that observed burst did not overlap measurements. Before/after snapshots do not continuously certify idle conditions throughout the run. All three repetitions are retained; BenchmarkDotNet applies its default iteration outlier policy. Reported percentages are ratios of median run means, not significance tests. Three rotations do not fully balance all four arm positions, and pre-capture always precedes captured support. Nothing here proves globally maximal performance or linear cost for every aliasing shape.

An independent read-only review found no reason to invalidate the observational comparison and identified the generated-binary qualification above. Full samples, ranges, source/input hashes, logs and the completed manifest are in [the rerun benchmark report](/private/tmp/pr494-support-v3-bdn-quiet-results/REPORT.md) and [manifest](/private/tmp/pr494-support-v3-bdn-quiet-results/manifest.json). The harness is `/private/tmp/pr494-support-v3-bdn`; it is outside the repository so all arms use the same sources without changing production or benchmark code on the PR.

## Recommendation

Use retained committed captures with installed-occurrence journals and ordered notification delivery. The alternative of re-enumerating committed collections and retrying unstable graph decisions remained incorrect in finite consumer scenarios. The chosen candidate removes those windows, passes the verified suites listed above, lowers measured allocations in the collection/subtree workloads, and retains the explicit callback/error contract. Its remaining ordinary structural CPU overhead is a real tradeoff for review. The spike is ready to review as a concrete integration candidate; it has not been merged into or pushed to PR #494.

## Remaining separate follow-ups

Primary-constructor handling, MQTT negative-cache behavior, arbitrary throwing getters in general method discovery, and storage configuration versus hosted-service startup ordering remain inherited issues outside these bounded fixes. The browser startup test requires root Status and Create visibility; its artificial constructor pause can leave storage disconnected, so it does not establish file-content readiness. Cross-context nested topology restrictions and callbacks running under the topology gate remain documented design boundaries. This pass does not claim full behavioral equivalence to master or resolution of every inherited defect.
