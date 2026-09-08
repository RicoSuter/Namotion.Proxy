# Publication performance follow-up

This is a bounded performance candidate on `codex/pr494-support-performance-v3`, based on `801f7c47a6dccae1d2433e3376fcc992eb6713bf`. It preserves the support spike's existing publication contract and known correctness limits. No original test was changed. No timed benchmark, push, or GitHub edit was performed.

## Evidence and selected work

Read docs/benchmarking.md and the previous matched-run report at `/private/tmp/pr494-support-v2-bdn-results/REPORT.md`. The structural benchmark contexts use full tracking without Registry: their context lifecycle-handler list contains only the synchronous descent handler. Consequently per-handler notification records are not the principal added cost in those rows. Broad handler-fanout batching was not introduced.

A nontimed Release console probe drove 1,000 alternating child assignments with tiering, PGO, and ReadyToRun disabled, and captured ARM64 JIT output. The original notification record measured **80 bytes** via Unsafe.SizeOf. The original attach and detach property extensions each compiled to **708 bytes**; the hot traversal routed through those extensions even though it already held the notifier and topology gate. The original IsReleasing and IsCurrentRelease helpers compiled to **228** and **252 bytes**, including their leaf-lock paths.

## Changes

- Internal attach and release property loops now call the notifier directly. Public extension behavior is unchanged; it still routes callers that do not already own this internal boundary. This removes per-property context/lifecycle service lookup and gate routing from the internal loops.
- Property notifications reuse SubjectLifecycleChange.Subject and .Property instead of carrying a second PropertyReference field. The private notification record is now **64 bytes**, a **20% smaller element** for queued copies and retained list capacity. Dispatch reads the payload only for property notification kinds.
- Topology-gate callers use a direct releasing-marker read with a zero-count fast path, and the release drain compares its ownership token without reacquiring the leaf lock. Every marker mutation still takes the leaf lock and topology gate; external derived readers retain the locked IsReleasing method. Other threads can only read the dictionary while these gate-protected queries execute. ABA release identity and compare-before-clear behavior are unchanged.
- An empty drain returns before entering delivery scope or clearing empty lists.
- Lifecycle event fanout uses .NET 9 Delegate.EnumerateInvocationList instead of allocating GetInvocationList arrays. The installed .NET 9 reference XML explicitly describes this enumeration as allocation-free. It still enumerates the delegate snapshot captured for the current event and catches failures per target.

The candidate nontimed JIT probe confirms the 64-byte record. The public property routing helpers and the release queries no longer receive separate compiled bodies in that exercised path; the direct queue/query work inlines. AttachTraversal.Publish itself grows from **796 to 932 bytes**, so this is not a blanket code-size reduction. Less lookup/locking/copy work and removal of delegate arrays are concrete code changes, but their net throughput effect remains unmeasured. No claim is made that these changes recover the previously observed structural slowdown.

## Verification

The existing focused publication, ordering, release-identity, resurrection, and failure tests passed before the change (19/19). The candidate additionally passes a delegate-snapshot regression: a first subscriber removes the second, adds a third, and throws; the original second still receives the current event, the third first receives the next event, and the original exception propagates from both operations after graph settlement. Focused candidate result: **20/20 passed**.

Full Tracking nonintegration result: **785 passed / 20 failed / 805 total**. The exact 20 failing names are unchanged from the base support candidate. Registry nonintegration result: **185/185 passed**. After removing an unnecessary nullable payload check, the final Tracking suite was rerun. `git diff --check` passes.

Commands used `dotnet test ... -m:1 -p:UseSharedCompilation=false` and `Category!=Integration` for full suites. Logs: `/private/tmp/publication-perf-baseline-tests.log`, `/private/tmp/publication-perf-candidate-tests.log`, `/private/tmp/publication-perf-v3-full.log`, `/private/tmp/publication-perf-v3-registry.log`. Tracking results: `src/Namotion.Interceptor.Tracking.Tests/TestResults/publication-perf-v3-full.trx`.

Nontimed probe project: `/private/tmp/pr494-publication-perf-probe`. JIT/layout outputs: `/private/tmp/publication-perf-baseline-jit.log` and `/private/tmp/publication-perf-candidate-jit.log`. Environment: `DOTNET_TieredCompilation=0 DOTNET_TieredPGO=0 DOTNET_ReadyToRun=0 DOTNET_JitDisasmDiffable=1`; method filters included Publish, QueueProperty, IsCurrentRelease, IsReleasing, IsReleasingUnderGate, AttachSubjectProperty, and DetachSubjectProperty.

Production increment: **24 added / 17 removed lines**, six files. Shared-file edits were coordinated with the getter/accounting investigation and do not touch baseline or installed-occurrence logic. Performance benchmarking remains for a coordinated, quiet comparison after integration. The separately identified ordinary-reconciliation partial-edge defect is not fixed by this work.
