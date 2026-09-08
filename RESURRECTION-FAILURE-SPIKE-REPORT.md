# Explicit resurrection failure cleanup

This bounded throwaway probe is based on combined candidate `83510bc9cebd301dd3bab1fdf9fcaa8e825866b0`, on `codex/pr494-support-resurrection-failure-v2`. No original test was changed. No benchmark, push, or GitHub edit was performed.

## Defect and fix

Explicit AttachToContext from a departing subject's callback took an early branch that promoted the retained claim and directly seeded the root. With root-first publication, a throwing getter left the new root ownership published without the common attach rollback. Old queued teardown could unregister the subject while final claim release retained it because it was still graph-owned.

The early branch still returns for provisional requests and for ordinary promotion of already owned subjects. An unowned, releasing explicit resurrection now falls through the existing common attach transaction. It receives the same consumed-anchor tracking and RollbackRejectedAttach cleanup as fresh attachment. No new rollback algorithm or public API was added. Production increment: **10 added / 9 removed lines** in LifecycleInterceptor.

Two new tests failed on the frozen candidate with retained context after failure. The first throws from the resurrected root's getter. The second throws from a later descendant's getter after earlier edges have already attached. Both now preserve the original getter exception and finish with no context, Registry registration, ownership, reference count, or pending release marker for the rejected root and tested descendants. Historical detach callbacks still resolve GetContext. Prior successful root resurrection and release-identity tests continue to pass.

## Validation and scope

Focused resurrection/release/failure tests: **9 passed / 0 failed**. Full Tracking nonintegration suite: **784 passed / 20 failed / 804 total**, compared with frozen candidate **782 passed / 20 failed / 802 total**. The exact 20 failing test names are unchanged. `git diff --check` passes.

Red log: `/private/tmp/resurrection-failure-v2-red.log`. Focused green log: `/private/tmp/resurrection-failure-v2-green.log`. Full log: `/private/tmp/resurrection-failure-v2-full.log`. Results: `src/Namotion.Interceptor.Tracking.Tests/TestResults/resurrection-failure-v2-full.trx`.

The focused test command compiled with `dotnet test src/Namotion.Interceptor.Tracking.Tests -m:1 -p:UseSharedCompilation=false`, filtering CallbackResurrectionFailureSpikeTests, CallbackReleaseEpochSpikeTests, and SupportFailureBoundaryV2Tests. The full run reused that successful build with `--no-build --no-restore --filter 'Category!=Integration' --logger 'trx;LogFileName=resurrection-failure-v2-full.trx'`.

This closes the reproduced explicit-resurrection failure path within the spike. It does not establish full callback/getter correctness or repair the separately identified ordinary-reconciliation partial-edge defect. The common rollback's existing limitations remain; this change reuses that path rather than strengthening every exceptional topology transition.
