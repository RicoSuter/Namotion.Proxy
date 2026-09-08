# Transaction divergence repair

- Date: 2026-06-13
- Status: approved design, implementation pending
- Issues: implements #340 (epic #347, row 5). The observability and control layer (pending-divergence tracking, events, repair policy, manual converge) and the consistency contract live in #342, not here.
- PR: #349 (this branch; implementation follows here)
- Prerequisite: #343 via PR #344 (source-marked commit applies). Implementation lands on top of PR #344 for code-shape reasons. Note: unlike the original #340 proposal, the repair mechanism here does not depend on source-marked applies, because repair never applies values to the local model directly (see section 4).

## 1. Context and scope

When a transaction commit fails after writing to external sources, the local model and the sources can end up persistently diverged: a revert write can fail, a transport error can leave the server state unknown, and the commit timeout path currently exits without any repair. Today the thrown `SubjectTransactionException` is the only record; nothing repairs the divergence afterwards.

This design adds a repair mechanism that runs in the commit failure path and converges the local model to the source by triggering a source-wins resynchronization. It is deliberately minimal: classify the failure, request a resync when divergence is possible, and report it in the commit exception. It supersedes three statements in issue #340:

- the guess-only proposal ("apply the new values of the revert-failed changes to the local model"): this design applies no guess locally; it triggers an authoritative source-wins resync instead;
- the "no public API drift" constraint: this design deliberately extends `ISubjectSource`, `WriteResult`, and `ITransactionWriter`;
- the "no connector-specific changes" constraint: the transport connectors get a resync override.

Everything beyond the repair itself is out of scope and owned by #342: the pending-divergence tracker, the detected/resolved events, the automatic-versus-application-handled repair policy, the manual converge API, the connector state machine, and the consistency contract wording. This PR always repairs automatically, which is the safe default for a rare failure path; #342 layers the controls and observability on top. The `ChangeOrigin` discriminator and the #338 commit-isolation decision also remain in #342. Success-path races are out of scope: #346 (stale queued write) and #338 (commit window isolation) have their own rows in epic #347.

### Why resync-only

An earlier revision of this design used a three-rung ladder: presume the source value (no IO), confirm with a targeted read-back (an opt-in `ISupportsPropertyReadBack` capability), then escalate to resync. It was dropped in favor of a single guaranteed action because both upper rungs are only optimizations over resync, which is strictly more correct (it reads the source's actual state rather than guessing, and reads the whole source rather than one property), and the path they optimize almost never runs:

- In Rollback mode the local apply happens only after the source write succeeds, so a full failure (session down, whole batch refused, transport drop before sending) leaves both sides at the old value: consistent, no repair. Permanently rejected items (not writable, wrong type) land in the failed set, are never applied locally, and likewise leave both sides consistent.
- Divergence therefore arises only from partial acceptance followed by a revert of the accepted items that also fails (a connection that accepted some writes then dropped before the revert landed), or from an indeterminate write (lost response or commit timeout mid-write). Both are connection-timing accidents, rare per connection.

For such a rare cold path, one guaranteed action is the better complexity trade. Targeted read-back (`ISupportsPropertyReadBack`) can be reintroduced later as a pure optimization, without changing the contract, if whole-source resync ever proves too heavy in practice (tracked in #342 question 4). The success path is untouched either way: a clean one-source, one-batch, all-accepted commit never enters any repair code.

## 2. The guarantee

After `CommitAsync` returns or throws, every source-bound property satisfies exactly one of:

1. **Consistent**: the local value equals the source value (clean success, clean failure, or repaired by a completed resync).
2. **Resync in flight**: a source-wins resynchronization has been requested and will converge, retrying until it completes and escalating to a transport reconnect when needed. The failure is reported in the thrown `SubjectTransactionException`. (Making this window queryable and event-observable is #342's job; this PR guarantees the resync is requested and converges, not that it is observable mid-flight.)
3. **Named residual**: one of the documented residuals in section 7, all reported loudly in the thrown exception.

Silent steady-state divergence is eliminated for the built-in `SourceTransactionWriter`, including the commit-timeout path. The guarantee is eventual where case 2 applies.

## 3. Failure classification

Repair must know whether divergence is even possible, and whether a clean revert already restored consistency. `WriteResult` (Connectors) cannot express that today, so it gains a classification. The enum is defined once in `Namotion.Interceptor.Tracking` (namespace `Namotion.Interceptor.Tracking.Change`, next to `SubjectPropertyChange`) so both layers share it:

```csharp
public enum WriteFailureKind
{
    None,          // success
    NothingSent,   // failure before transmission: no divergence possible
    Rejected,      // server answered per item: accepted subset known exactly
    Indeterminate  // transport error mid-call: server state unknown
}
```

- `WriteResult.Failure(...)` defaults to `Indeterminate` (conservative: triggers repair). A new overload accepts an explicit kind for `NothingSent`.
- `WriteResult.PartialFailure(...)` implies `Rejected` (a response with per-item status codes was received).
- `SourceWriteResult` (Tracking) gains a `FailureKind` field mapped by `SourceTransactionWriter` from each `WriteResult`. Multi-source results merge conservatively: `Indeterminate` wins over `Rejected` wins over `NothingSent`.

Call-site mapping in the OPC UA client (`OutboundWriter`):

- "session is not connected" pre-check: `NothingSent`;
- semaphore-cancellation failure in `SubjectSourceExtensions.WriteChangesInBatchesAsync`: `NothingSent`;
- `ProcessWriteResults` outcomes, including the all-items-rejected case: `Rejected`;
- the `catch` around `session.WriteAsync`: `Indeterminate`.

Multi-batch simplification: when a middle batch fails, the not-yet-sent tail batches share the overall result's classification. Treating never-sent changes as in-doubt only costs a redundant resync request that the source-wins snapshot reconciles anyway. This keeps `WriteResult` a single flat result instead of a per-batch report.

All three kinds earn their keep even though both repair-triggering kinds take the same action: `NothingSent` lets repair be skipped entirely, `Rejected` lets it be skipped when the revert of the known-accepted set succeeded, and `Indeterminate` always repairs because the outcome is unknown.

Classification drives the repair decision:

| Outcome | Repair |
|---|---|
| `NothingSent` | none (no divergence) |
| `Rejected`, revert succeeded | none (state restored) |
| `Rejected`, revert failed | resync the affected source |
| `Indeterminate` | resync the affected source |
| Commit timeout during source IO | resync the affected source (all source-bound changes, outcome unknown) |

## 4. Repair orchestration in the commit

### Hook points

All repair runs inside `SubjectTransaction.ReconcileWithWriterAsync`. Its four divergence-capable exits get wired to repair:

1. Rollback branch, source-write failure: repair runs after `RevertSourceWritesSafelyAsync` when the revert reported failures or the write was `Indeterminate`.
2. Rollback branch, local-apply failure: same treatment after its source revert.
3. BestEffort branches: same treatment for their targeted reverts.
4. Commit timeout: the write and revert calls get a catch for the commit-timeout cancellation. All source-bound changes are classified in-doubt, repair runs, then the original timeout exception rethrows. This supersedes the failure-matrix line "commit timeout during source revert: no convergence".

Retryability is unchanged: the timeout path still resets the transaction for retry. Because repair applies nothing to the local model (see below), a retry simply re-asserts the intent; a `FailOnConflict` retry behaves exactly as today.

### Why repair runs in the background

The `_isCommitting` flag makes `CaptureChange` reject tracked writes during commit, so neither the writer nor the transaction can apply source values to the local model inside the commit flow. Resync sidesteps this entirely: it converges the local model on a detached flow through the normal inbound path (`StartBuffering` plus `LoadInitialStateAndResumeAsync`), which is already echo-correct independent of this design.

Consequently the repair action does not apply anything to the local model during the commit. It only triggers a background resync, then the commit throws. This is why, unlike the original #340 proposal, repair does not depend on #343's source-marked applies: there are no in-commit applies to suppress.

### The writer seam

One method on `ITransactionWriter` with a default implementation (`Namotion.Interceptor.Tracking` targets net9.0), so custom writers keep compiling and keep today's no-repair behavior:

```csharp
// ITransactionWriter (Tracking); default implementation returns SourceRepairResult.None
ValueTask<SourceRepairResult> RepairDivergenceAsync(
    SourceRepairRequest request,
    CancellationToken cancellationToken);
```

- `SourceRepairRequest`: the affected changes (revert-failed subset or in-doubt set), the `WriteFailureKind`, the revert outcome, and the opaque revert state (which already carries the per-source grouping).
- `SourceRepairResult`: the sources for which a resync was requested and any `Errors`. No values flow back to the transaction; it only reports them.

The built-in `SourceTransactionWriter` implements it as: for each affected source, call `RequestResynchronization`; return the list. No policy and no tracker here, both belong to #342.

### Latency

`RequestResynchronization` is fire-and-forget: it schedules background work and returns immediately. Repair therefore adds no blocking IO to the commit path, so commit-failure latency is essentially unchanged and there is no repair timeout to configure. The commit-timeout exit likewise just schedules the resync and rethrows; it needs no separate clock.

### Reporting surface

`SubjectTransactionException` keeps its shape: `FailedChanges` still means "the transaction intent failed". The repair outcome arrives as a `SourceDivergenceException` (a Connectors type) appended to `Errors`, carrying the affected properties, the failure kind, the sources a resync was requested for, and any repair errors. This is the in-band report; the out-of-band tracker and events are added in #342.

## 5. The repair action: resync

The single repair action is a source-wins resynchronization, requested per involved source via `RequestResynchronization` (section 6). It reconciles the whole source through the inbound path, which is authoritative and already handles echo semantics and buffering. Multi-source transactions request a resync per source group, reusing the grouping carried in the revert state.

Cost: a resync reconciles the entire source, not just the diverged properties. This is accepted because the path is a rare cold path (section 1) and resync is the guaranteed backstop every source must support anyway. If profiling later shows whole-source resync is too heavy for a real workload, a targeted read-back capability (`ISupportsPropertyReadBack`, returning per-property source values applied through the inbound path) can be added as a strictly-optional fast path ahead of resync, without changing the section 2 guarantee (tracked in #342 question 4).

## 6. Source interface change

### `ISubjectSource.RequestResynchronization` (breaking change)

```csharp
/// Ensures a full source-wins resynchronization eventually completes,
/// cycling the transport if one exists. Fire-and-forget; the implementation
/// retries until the resync lands and coalesces concurrent requests.
void RequestResynchronization(string reason);
```

- `SubjectSourceBase` provides the default implementation: schedule its existing `StartBuffering` plus `LoadInitialStateAndResumeAsync` choreography on its background lifetime. The existing behavior that a failed load propagates and triggers reconnection provides the retry guarantee.
- OPC UA overrides to route into `SessionManager` supervision; MQTT and WebSocket route into their reconnection handling likewise.
- In-memory and test sources inherit the base default and are correct for free; for them the resync request is the full reload.
- This is a breaking interface change for implementors that do not derive from `SubjectSourceBase`. Accepted: the library is in its 0.0.x breaking phase, and the member is required for the guarantee in section 2 (a last resort that is optional is not a last resort).

## 7. Edge cases and residual divergence

Handled:

- **In-doubt window**: between the failed commit and resync completion the local model holds the pre-commit value, reported via the exception. The resync converges it to the source truth; the caller already knows the commit failed.
- **Multi-source partial failure**: per-source resync, conservative kind merging.
- **Concurrent third-party writes during resync**: resync applies a source-wins snapshot through the inbound path; a later third-party write flows in the same way. This is quiescent consistency, not a defect.

Residual (documented, reported, no repair possible):

1. **A local property setter that deterministically throws** during the resync's inbound apply. The resync fetches the correct source value, but applying it re-runs the throwing setter. Reported via the inbound apply error handling; local-side divergence remains until the setter stops throwing (a later inbound apply retries it naturally).
2. **A custom `ITransactionWriter` that throws** instead of reporting. It returns neither a written set nor revert state, so nothing can be classified or repaired. Already documented as terminal; the built-in writer never takes this path.
3. **Reverts can clobber concurrent third-party writes and expose transient values to other clients.** OPC UA has no server-side transactions; the revert writes captured old values, not compare-and-swap. This does not break local/source agreement (both sides end at the old value); it overwrites another client's intent. Documented protocol limit, owned by the #342 contract.

## 8. Out of scope

Owned by #342 (observability, control, and contract):

- pending-divergence tracking (`SourceConsistencyTracker`), `DivergenceDetected`/`DivergenceResolved` events, and the green/degraded aggregate;
- the repair policy (automatic versus application-handled, `WithSourceTransactions` configuration) and the manual converge API;
- the connector state machine (`Stopped`/`Connecting`/`Synchronizing`/`Synchronized`);
- the consistency contract wording, the `ChangeOrigin` discriminator, and the divergence taxonomy;
- targeted read-back (`ISupportsPropertyReadBack`): a deferred optimization, only if whole-source resync proves too heavy in practice.

Owned by other epic rows:

- #346: stale queued non-transactional write overwriting a committed value (flush-time echo filter, row 3).
- #338: commit window isolation against concurrent local writes and inbound updates (decided by the #342 design, row 6).

## 9. Documentation updates

- `docs/tracking-transactions.md` failure matrix: the revert-failure and in-doubt rows change from "diverged, reported" to "resync requested, converges in background"; the commit-timeout row changes from "no convergence" to "resync requested, then retryable"; the clean-failure rows (full rejection, permanent per-item rejection) documented as consistent-at-old.
- Sources documentation: `RequestResynchronization` semantics (including coalescing and retry-until-landed).
- Issue #340: closing notes referencing this spec and the superseded statements.

## 10. Testing

- **Unit, repair orchestration** (Tracking/Connectors tests, fake sources via the existing `IFaultInjectable` pattern): each failure kind requests resync or not as the table in section 3 specifies; full rejection and permanent per-item rejection leave both sides consistent and request no resync; revert-failure and indeterminate request resync; the timeout path requests resync and stays retryable; `SourceDivergenceException` contents pinned; repair adds no local apply (no spurious change notifications).
- **Unit, base class**: `SubjectSourceBase.RequestResynchronization` default runs the buffer/load/replay choreography, retries on load failure, and coalesces concurrent requests.
- **Integration, OPC UA** (`Namotion.Interceptor.OpcUa.Tests`, run targeted per repo convention): resync request routes into session supervision; an end-to-end partial-acceptance-with-failed-revert scenario converges against the server fixture.
- **Public API snapshots**: Connectors and Tracking verified files change; accept new snapshots in the implementation PR.
- Conventions: `When<Condition>_Then<ExpectedBehavior>` naming, explicit Arrange/Act/Assert, no hardcoded waits (`AsyncTestHelpers.WaitUntilAsync` for eventual paths).

## 11. Components touched

| Component | Change |
|---|---|
| `Namotion.Interceptor.Tracking` | `WriteFailureKind`, `SourceWriteResult.FailureKind`, `ITransactionWriter.RepairDivergenceAsync` (default impl), `SourceRepairRequest`/`SourceRepairResult`, `ReconcileWithWriterAsync` hook points incl. timeout amendment |
| `Namotion.Interceptor.Connectors` | `WriteResult` classification, `SourceTransactionWriter` repair (request resync per affected source), `SourceDivergenceException`, `ISubjectSource.RequestResynchronization`, `SubjectSourceBase` default |
| `Namotion.Interceptor.OpcUa` | `OutboundWriter` failure kinds, `RequestResynchronization` override into `SessionManager` |
| `Namotion.Interceptor.Mqtt` / `WebSocket` | `RequestResynchronization` overrides into their reconnection handling, failure-kind mapping where determinable |
| Docs | `docs/tracking-transactions.md` failure matrix, sources docs |

## 12. Planning notes

- **Code-base assumption.** Every code reference in this spec (the `Memory<SubjectPropertyChange>` snapshot, `ReconcileWithWriterAsync` exits, the `SourceWriteResult` shape) describes the codebase **after PR #344 is merged**. This branch is currently based on master, which predates #344 and has materially different shapes in `SubjectTransaction`, `ITransactionWriter`, and `SourceTransactionWriter`. Do not plan or implement against the pre-#344 code: first merge master into this branch once #344 has landed, then verify the referenced shapes match. (The repair mechanism itself no longer depends on #343's source-marked applies, since repair applies nothing locally; the dependency is only the shared code shape.)
- **Suggested implementation order**, each step independently buildable and testable: (1) `WriteFailureKind` and the `WriteResult`/`SourceWriteResult` plumbing with call-site mapping; (2) the `ITransactionWriter.RepairDivergenceAsync` seam and the `ReconcileWithWriterAsync` hook points including the timeout catch, with a no-op default writer behavior; (3) `ISubjectSource.RequestResynchronization` with the `SubjectSourceBase` default; (4) wire `SourceTransactionWriter` to request a resync per affected source and report via `SourceDivergenceException`; (5) connector resync overrides (OPC UA into `SessionManager`, MQTT/WebSocket) and failure-kind mapping; (6) docs and public API snapshot updates.
- **Verification.** Build and unit tests per repository convention (`dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`); run `Namotion.Interceptor.OpcUa.Tests` targeted when the OPC UA changes land. The public API snapshot tests for Tracking and Connectors will fail until their `.verified.txt` files are updated, which is expected and part of step 6.
