# Per-property divergence reporting

**Status: design spec, approved for planning.** Supersedes `2026-08-30-property-sync-state-design.md` (the pruned review material), whose surviving decisions and falsified claims are folded in below. This file is scaffolding and is deleted before merge; durable documentation lands in `docs/` per the Documentation edits section.

Prerequisite: PR #500 (merged), whose per-property catch in `SubjectUpdateApplier.ApplyPropertyUpdate` is one of the detection sites.

Related issues: this is the observability layer #342 defers to in its question 3. It builds no repair; #340 and #349 own that. It does not detect silent source-side changes (#342 question 4).

## Goal

A consumer can ask, per property, whether the local model currently agrees with the source that owns it, and be told the moment the framework detects that it stops agreeing. Both directions are in scope: values the source sent that the model refused or transformed, and local writes that were dropped before reaching the source. Both property classes are in scope: data properties and subject-based (structural) properties, with different record kinds.

## Contract

A property has **diverged** when the model knows it holds a different value than its source, and knows nothing will fix it on its own.

**Knows.** Only detected disagreement is reported. The model cannot discover that a source changed a value behind its back, so this is not a consistency checker.

**Nothing will fix it.** Only terminal outcomes count. A write in flight, or parked in the retry queue while the transport is down, has not diverged; reporting it would flicker on every local write. Hence no `InFlight` state, and hence the outbound staleness rule below: a dropped write superseded by a newer queued write of the same property is not divergence.

`Diverged` is a floor, not a ceiling. Its absence means nothing was detected, not that the two agree.

## States and precedence (survived three review rounds, unchanged)

`SourceState` gains a `Diverged` member. It is property-level only, like `Unclaimed`; a source itself never reports it (`SourceMonitor.cs:367` gates branch waits on `state == SourceState.Synchronized` exactly, so a source-level `Diverged` would hang every containing wait).

`GetSourceState()` (`SourceMonitoringExtensions.cs:25`) reports `Diverged` only while the owning source is `Synchronized`; any other source state dominates. This loses nothing for the decision the state serves: `docs/connectors-monitoring.md:204` defines `State` as "can I trust these values" and everything other than `Synchronized` already means no. `TryGetDivergence()` answers why and stays readable in any source state, so an operator diagnosing an incident during a reconnect reads it directly. The two can disagree and that is documented.

## The divergence record

One record per property, stored in property data under a short key (same convention as `WriteStateKey`, `PropertyReference.cs:87`). Fields:

| Field | Type | Content |
|---|---|---|
| `Kind` | enum `DivergenceKind` | `Value` or `Structure` |
| `Reason` | enum `DivergenceReason` | `Refused`, `Transformed`, `DroppedWrite` |
| `Direction` | computed property | Derived from `Reason`: `Refused` and `Transformed` are inbound, `DroppedWrite` is outbound. Not a stored field. |
| `Value` | `object?` | `Kind == Value` only. Inbound: the value the source sent. Outbound: the value that never arrived. Always a data value (scalar, string, enum, array, DTO), never anything graph-attached; the classification gate below is the exact complement of the lifecycle's own attach gate (`CanContainSubjects`), so nothing the graph tracks can reach it. `null` is a legitimate sent value, which is why `Kind` is a separate field. |
| `Message` | `string?` | The exception message for `Refused`, never the `Exception` object (no stack or inner-exception graph is pinned). |
| `Timestamp` | `DateTimeOffset` | When the divergence was detected (`UtcNow` at record time). |

The record pins no source, no session, and no subject. Its lifetime is bounded by the claim (see Record lifecycle), it is overwritten in place on re-detection (last writer wins, both writers describe real disagreements), and it dies with `Subject.Data` when the subject is collected.

## Staleness

Divergence is computed at read time, never latched, for `Kind == Value`:

- **Inbound record** (`Refused`, `Transformed`): diverged if and only if the current stored value differs from `record.Value`. The model holds something other than what the source sent.
- **Outbound record** (`DroppedWrite`): diverged if and only if the current stored value equals `record.Value`. The very value we hold never reached the source. If the value has moved on, a newer change is in the outbound pipeline and supersedes the drop, which the contract classifies as in flight, not diverged. This rule is what makes ring-buffer eviction honest without scanning the queue at drop time: the oldest write of a property is evicted first (`WriteRetryQueue.cs:81`, `:229`), and when a newer write of the same property is still queued the predicate reports agreement-pending rather than divergence.

For `Kind == Structure` the record is latched: its presence means diverged, cleared by the next successful structural apply of that property (and by the lifecycle clears). Value comparison is undefined for structure and the latching failure modes that the review rounds punished for values (clamping hooks, no-op equality landings, concurrent same-property applies) do not transfer: hooks do not clamp subject references, and structural applies are serialized per connection (`WebSocketSubjectHandler`'s `_applyUpdateLock`, the single WebSocket receive loop, the OPC UA loader's browse sequence).

The value comparison mirrors the terminal's semantics: `object.Equals` with null handled explicitly, plus the boxed-enum underlying-type coercion of `SentValueEqualsAfterUnbox` (`IWriteInterceptor.cs:321-338`), because OPC UA delivers enums as boxed integers. Reference types compare by reference, which is correct here because the stored reference on a faithful landing is the sent instance itself, and the applier already preserves that instance identity deliberately (`SubjectUpdateApplier.cs:107-124`).

Why the stored value exists at all (settled during the brainstorm): the detection-time decisions compare the incoming sent value against the current stored value, both in hand at the call site, and never consult the record. The record's value has exactly three jobs. First, outbound supersession per the contract, as above; a payload-free latched record would report the forbidden flicker on every superseded ring drop, and the only latched alternative is an O(dropped x remaining) queue scan under the queue lock. Second, read-time self-healing when no detection event fires: a same-value race (two delivery paths carry the same value, one hits a transient error and records after the other landed) and a local write that sets exactly the refused value both read as agreement. Third, diagnostics: `Transformed` has no exception, so without the value the record carries no evidence of what the source wanted.

Constraints from the review rounds, and how this satisfies them: no producing revision exists on the exception path and none is needed (no revisions anywhere in this design); a record written by a losing same-value race cannot permanently mark a converged property (the predicate reads agreement); a clear leaves no tombstone and a late record wins against nothing (clears are compare-and-remove of the observed record, `PropertyReference.cs:80`); ownership changes are handled by the claim-scoped lifecycle rather than by tokens.

## Detection

Detection lives entirely in `Namotion.Interceptor.Connectors`. Nothing changes in `Namotion.Interceptor` core or in `Namotion.Interceptor.Tracking`; the `PendingOrigin` stamp mechanism and its five invariants are untouched and remain dedicated to echo suppression. Detection never consults the stamp's outcome, which is what closes the re-entrant clamp hole by construction (see Interactions).

### Shared gates

Checked before any read-back or record work, so excluded paths pay almost nothing:

1. The write's origin source is an `ISubjectSource` (`Origin.Source is ISubjectSource`). Servers fail this structurally: `ISubjectSource : ISubjectConnector` (`ISubjectSource.cs:25`) and servers derive `SubjectConnectorBase : ISubjectConnector` without implementing `ISubjectSource`; the WebSocket server applies with a connection object (`WebSocketSubjectHandler.cs:217`) and the MQTT server stamps a per-instance sentinel (`MqttSubjectServer.cs:48`). A server's refused value is diverged with respect to one client and fine for the others, a per-(property, client) matrix that is a different data model; servers stay excluded.
2. That source currently owns the property (`TryGetSource` plus reference equality). Belt and braces over gate 1, and it scopes divergence to the owning relationship.
3. Not `Metadata.IsDerived`. A derived property's getter recomputes the stored value, so a sent value never survives literally and every complete update carries derived properties (`SubjectUpdateFactory.cs:83`), which would otherwise mark them permanently.
4. Inbound only: the property has a setter (`Metadata.SetValue is not null`). `SetValueFromOrigin` writes through `SetValue?.Invoke` (`SubjectChangeContextExtensions.cs:51`), so a missing setter is a silent no-op; without this gate the read-back would see stored differs from sent and mislabel every read-only property in a welcome snapshot as `Transformed`. Outbound drop sites do not apply this gate: a dropped change is a real loss regardless of the setter, and the predicate only needs the getter.
5. Kind selection: properties classified `IsSubjectReference`, `IsSubjectCollection`, or `IsSubjectDictionary` (`RegisteredSubjectProperty.cs:120-138`, the same predicates `SubjectUpdateFactory` uses) take the `Structure` path; everything else takes the `Value` path. This classification, not the update's `Kind`, is what guarantees `record.Value` never holds a subject: null subject assignments travel as `Kind = Value` updates (`SubjectUpdateFactory.cs:147`, `:159`, `:173`), and the OPC UA loader's structural writes bind to the instrumented Connectors overload because they hold `RegisteredSubjectProperty` (`OpcUaSubjectLoader.cs:268`, `:292`, `:333`, `:378`).

### Inbound: the shared helper

An internal helper in Connectors wraps the write attempt for both inbound sites:

- **Exception path**: catch everything except `OperationCanceledException`, record `Refused` (`Value` kind with the sent value, `Structure` kind payload-free), and rethrow. Never swallow: for WebSocket the initial-load throw is the retry mechanism, and swallowing it would turn a transient load failure into a silently missing property (reversed decision from the review rounds, do not re-propose catching at `SubjectPropertyWriter.cs:143`).
- **Success path, `Value` kind**: read the property back and compare with the sent value using the comparison above. Equal: compare-and-remove any existing record (this heals inbound and outbound records alike, since the source just proved its value equals ours). Different: record `Transformed`.
- **Success path, `Structure` kind**: compare-and-remove any existing record.

Sites:

1. `RegisteredSubjectPropertyExtensions.SetValueFromSource` (`RegisteredSubjectPropertyExtensions.cs:16`). All direct client call sites route through this overload; per the review rounds exactly three sites currently bind to the `PropertyReference`-typed Tracking overload and change binding (extension binding follows the receiver's static type, not namespace imports; several OPC UA sites already bind here because they hold a `RegisteredSubjectProperty`). The Tracking overload itself stays untouched and undetected, which is correct because non-connector callers have no owning source.
2. The applier: `SubjectUpdateApplyContext.SetPropertyValue` (`SubjectUpdateApplyContext.cs:52-64`) wraps its non-Local branch with the helper (Local origins keep the unarmed path and are excluded by gate 1 anyway), and #500's per-property catch (`SubjectUpdateApplier.cs:153-158`) records `Refused` for the failed property before `RecordFailure` aggregates it. The two recorders overlap for a Value-kind failure, so the precedence is fixed: the helper's record wins when it wrote one (it holds the converted sent value), and the catch records only when the helper did not reach its own recording step. That case is real, because `ConvertValue` runs inside the try (`SubjectUpdateApplier.cs:119-128`) and can throw before the write is attempted; such a record carries no value (`Kind == Value`, value absent, treated as never-agreeing) rather than the raw `JsonElement`, which could never compare equal to a stored value and would defeat the local-write self-heal. A later landing clears it normally. A successful structural walk of a property (`Object`, `Collection`, `Dictionary` update kinds completing without a throw for that property) clears a `Structure` record.

The applier's transform arm passes distinct written and sent values (`SubjectUpdateApplier.cs:107-124`); the read-back compares against the sent value, so a deliberate local correction records `Transformed` until the source converges, which is the intended reading of the contract.

WebSocket coverage comes through site 2 (it applies everything through the applier), which is what killed the typed-entry-point design in review round three.

### Outbound: record at drops, clear at deliveries

Record `{DroppedWrite, change value}` at every live drop site, beside the existing `Metrics.OutboundRetries.AddDropped` calls, applying gates 2 to 5. Gate 2 (ownership) must be re-checked at the drop and clear sites themselves, not inherited from capture: the delivery filter checks ownership when the change is captured (`SubjectSourceBase.cs:308`), but eviction and flush happen arbitrarily later and ownership can change in between. Without the re-check, a queue belonging to a released source can write a `DroppedWrite` record against a property another source has since claimed (reported as `Diverged` on a healthy property), or its later successful flush can clear a record the new owner's detection legitimately wrote. Drops are cold and clears already cost one probe, so the re-check is effectively free. With it, the residual window shrinks to the same microsecond race the inbound sites have, which the Record lifecycle section describes.

| Site | What drops |
|---|---|
| `WriteRetryQueue.cs:81` | Ring eviction on enqueue over capacity |
| `WriteRetryQueue.cs:229` | Ring eviction on requeue after a failed flush |
| `SubjectSourceBase.cs:366` | Write failure with no retry queue configured, from `result.FailedChanges` |
| `SubjectSourceBase.cs:381` | Defensive catch, documented unreachable; recording there is one line and costs nothing |
| `SubjectSourceBase.cs:614` | Reconnect reconciliation, property without a setter (mostly derived recalcs, which gate 3 skips; the residue is recorded) |
| `SubjectSourceBase.cs:626` | Reconnect reconciliation failure |

Inside `WriteRetryQueue`, evicted changes are collected under the lock and recorded after releasing it, keeping the lock footprint unchanged.

Clear on confirmed delivery: wherever a `WriteResult` is interpreted, every change the writer **attempted** and did not list in `FailedChanges` counts as written and clears its property's record by compare-and-remove. This applies to every flush batch whether or not `Error` is set, since in the error path the non-failed remainder of the batch did reach the source; clearing only on fully successful batches would leave stale records whenever a partial failure occurs. Sites: the flush loop (`WriteRetryQueue.cs:160-183`, both arms), the direct no-queue path (`SubjectSourceBase.cs:363`), the normal send path's result handling, and the three transaction interpretation sites in `SourceTransactionWriter` (`:164`, `:375`, `:416`), which already compute the written set (`:176`, `:381`). The transaction sites are not optional: a commit writes `ChangeOrigin.Confirmed` (`SourceTransactionWriter.cs:306`, `:325`) whose re-delivery is suppressed (`ChangeDeliveryFilter.cs:82`), so no other clear site ever observes a transaction delivery, and an operator retrying a dropped write through a transaction would otherwise leave the property reporting `Diverged` indefinitely. The probe is one dictionary miss per delivered change in the healthy case. An inbound faithful landing also clears outbound records, per the helper above.

**Attempted, not "everything unlisted".** `WriteResult.cs:13-16` documents unlisted changes as written, but two shipping writers violate that contract by skipping changes silently: OPC UA drops changes with no writable node (`OutboundWriter.cs:96-97`, `:149-152`) and returns `WriteResult.Success` when every change was skipped (`:46-49`); MQTT skips unregistered or subject-containing properties, properties with no topic, and **serialization failures** (`MqttSubjectClientSource.cs:425-445`), logging the last and continuing. Consuming the documented contract naively would both miss terminal losses (a serialization failure is exactly the per-property terminal loss this feature exists to report) and actively erase true records, because a skipped change would clear the very record describing its own non-delivery. Resolution: extend `WriteResult` with a `SkippedChanges` list (new property defaulting to empty, source-compatible), populate it at the five skip sites above, clear on `attempted minus FailedChanges minus SkippedChanges`, and record `DroppedWrite` for skipped changes whose skip is terminal (missing writable node, serialization failure) rather than a mapping gap (no topic, not registered, subject-containing). This also repairs a latent pre-existing defect: `SourceTransactionWriter`'s `written` set (`:176`, `:381`) currently counts silently-skipped changes as written, which feeds commit and revert decisions.

## Record lifecycle (claim scoping)

No weak references and no identity tokens. The record's validity is scoped to the property-source claim, enforced at the two universal choke points every claim and release flows through:

- `RemoveSource` (`SourcePropertyExtensions.cs:73-82`) removes the record, but only once the ownership removal itself succeeded: the function returns early on an ownership mismatch (`:75-78`), and clearing in that branch would delete the current owner's record. Every release path funnels here: explicit release (`SourceOwnershipManager.cs:113`), subject detach (`:134`), dispose (`:156`). A record is a statement about one property-source relationship; when the relationship ends, the statement goes with it, and nothing pins a replaced or disposed source's data.
- `SetSource` (`SourcePropertyExtensions.cs:32-43`) removes any leftover record, but only on the fresh-claim branch (`TryAddPropertyData` succeeded). An idempotent re-claim by the same instance, which is what an OPC UA rebrowse after reconnect performs, deliberately does not clear, so records survive reconnects and `TryGetDivergence()` keeps answering during an outage.
- `TryGetDivergence()` returns false when the property has no current owner, consistent with divergence being defined against an owning source.

The one hole is a drop or detection callback racing a release and writing a record just after the clear. The orphan is invisible (`GetSourceState` reports `Unclaimed`, which dominates; `TryGetDivergence` is owner-gated) and the next fresh claim sweeps it.

## Reporting API

In `Namotion.Interceptor.Connectors.Monitoring`:

- `SourceState.Diverged` (new enum member, property-level only).
- `PropertyDivergence` (public readonly type with the record fields above).
- `bool TryGetDivergence(this PropertyReference property, out PropertyDivergence divergence)`: owner-gated, returns the record regardless of the owner's state and regardless of whether the predicate currently evaluates to diverged; consumers wanting the point-in-time verdict use `GetSourceState()`.
- `GetSourceState()` gains the branch: owner `Synchronized` and the staleness predicate holds, report `Diverged`. Cost when not diverged: one dictionary probe. The `Value`-kind predicate invokes the property getter once (never a derived getter, gate 3 guarantees no derived records exist).

Detection internals (the helper, the record storage key) stay internal.

## Events

New `SourceEventKind` members `PropertyDiverged` and `PropertyDivergenceCleared`, published with `Property` set, following the `PublishOwnershipChange` pattern (`SourcePropertyExtensions.cs:84-111`): skip entirely without subscribers, build the event at most once, publish through the monitor which only enqueues (verified, no consumer callback under `SyncRoot`, no ABBA cycle). `PropertyDivergenceCleared` fires only when a compare-and-remove actually removed a record. The lifecycle clears in `SetSource`/`RemoveSource` emit no divergence events; the claim and release events already describe those transitions, and a released property reports `Unclaimed` anyway.

Honest limitation, documented: events fire on detection actions. A predicate flip caused by a local write (an operator manually setting the refused value) changes what `GetSourceState()` returns but emits no event; polling sees it, the stream does not.

## Accepted transients and limitations (documented, not defects)

- A record written by a losing race with a different value over-reports until the next landing or delivery of that property heals it. Transports serialize per-connection delivery, so the window requires cross-path concurrency (OPC UA subscription against polling) and closes on the next message including a no-op landing.
- The read-back races a concurrent local write outside `SyncRoot` (`WriteInterceptorFactory.cs:19`, `:50` scope the lock to the store itself), which can record a transient divergence that is real at that instant (the model did just move away from what the source sent) and heals the same way.
- While the owning source is not `Synchronized`, `GetSourceState()` reports the source's state and a diverged property reads `Synchronizing` for the duration of a reconnect. The record survives and `TryGetDivergence()` still returns it.
- Ring eviction is not perfectly superseding for A-B-A value sequences. If the queue holds P=1, P=2, P=1 (newest) and capacity evicts the oldest P=1, the record is `{DroppedWrite, 1}` while the stored value is also 1, so the predicate reports `Diverged` even though two newer writes of P are still queued. It heals on the next successful flush of P. A true fix needs the queue scan the design rejects on cost, so this is accepted rather than solved.
- MQTT without retained messages re-applies nothing on reconnect (`LoadInitialStateAsync` returns null), so records persist across reconnects there until a live message arrives, which is correct: the disagreement persists too.

## What is not covered

| Case | Why, and where it is tracked |
|---|---|
| A source changing a value without telling us | No read-back or periodic compare while connected, so nothing detects it. #342 question 4. |
| A write the source rejects permanently | The retry queue keeps retrying rather than giving up, so the change is never dropped and never marked, although it never lands either. #342 row 3. |
| A transaction commit whose source write fails and whose revert also fails | Repair designed but not built. #340. |
| A write landing inside a transaction commit window | Silently overwritten, documented best effort. #338. |
| An inbound update naming a property the model does not have | No property exists to mark. |
| A value sent for a property with no setter | Gate 4 skips it; a welcome snapshot carries read-only properties and marking them would report `Diverged` permanently. |
| A property no source has claimed | Divergence is defined against an owning source. Reports `Unclaimed`. |
| Structural transform detection | Only refusals are detectable for structure; nobody clamps a subject reference in a hook. |

## Interactions verified against code

- **Re-entrant clamp, closed by construction.** The generated `OnXChanging` hook runs before the write context consumes the pending stamp (`SubjectCodeGenerator.cs:369-376`, `IWriteInterceptor.cs:133`), so a hook that "clamps" by re-entering its own property makes the inner write consume the stamp and the outer write then stores the original sent value over the clamp. Probe results (throwaway test, written and run 2026-08-31): faithful write stores 50 with the origin surviving; a ref-parameter clamp stores 100 with the origin demoting (a true transform); a re-entrant clamp stores 150, exactly what the source sent, while the stamp outcome claims demotion. Read-back compares stored against sent and reports agreement, so no false record exists. The stamp outcome is wrong in that case and is never consulted. Separately, the re-entrant pattern is a pre-existing silent footgun for every write kind (the clamp itself does not clamp); decision: keep runtime behavior, add one sentence to `docs/subject-guidelines.md`'s hook section (mutate `newValue`; assigning the property from its own `OnXChanging` is unsupported and will be overwritten). No runtime guard, no analyzer for now.
- **Equality short-circuit.** `PropertyValueEqualityCheckHandler.cs:16-19` short-circuits a write whose value equals the current one, so a source re-sending the value we hold produces no write at all. The helper's read-back runs at the call site, outside the chain, sees stored equals sent, and clears; the exact message that should heal a record does, with no write occurring.
- **Transaction capture.** `SubjectTransactionInterceptor.cs:106-140` captures only under an ambient transaction bound to the context; inbound connector threads never carry one, so captured inbound writes are practically nonexistent (#338 governs the commit-window residue).
- **Echo suppression.** A faithful landing keeps `FromSource` and is skipped for the owning source, so the healthy path produces no outbound echo and no spurious clear traffic. A transform demotes to Local and flows back to the owner as the correction write, which is the existing, intended behavior this design observes rather than changes.

## Public API and compatibility

All changes are in `Namotion.Interceptor.Connectors` (new enum member, new type, new extension, new event kinds). Core and Tracking are untouched, so `Namotion.Interceptor.Tests` and the Tracking API snapshot stay green by construction; the Connectors API snapshot (`VerifyChecksTests.PublicApi`) changes intentionally and gets the received-file accept. Adding an enum member to `SourceState` is source-compatible; exhaustive switches in consumer code gain a case, which the release notes must mention.

## Performance

- Local writes: zero cost. Nothing is added to the write interceptor chain.
- Inbound value applies: the gates (an ownership probe plus metadata reads), one getter invoke for the read-back, one record probe. Structural applies add one probe on success.
- Outbound: one record probe per delivered change (a dictionary miss in the healthy case); drops are cold.
- `GetSourceState()`: one probe when `Synchronized`, plus one getter invoke only when a record exists.
- Claim and release: one unconditional dictionary remove each, on paths that already lock and mutate dictionaries.

Benchmark gate before merge: the inbound apply path and the outbound batch path, compared against master per `docs/benchmarking.md`.

## Verification plan

- Unit tests in `Connectors.Tests`: the helper matrix (faithful, refused, transformed, no-op equal landing, derived skip, no-setter skip, structural refusal and clear, server-origin skip, unclaimed skip), record lifecycle (fresh claim clears, idempotent re-claim preserves, release clears), outbound drop and delivery clear paths including ring eviction and supersession, `GetSourceState` precedence, `TryGetDivergence` during `Synchronizing`, event emission including the cleared-only-when-removed rule.
- A test pinning the re-entrant clamp scenario producing no record (the probe, promoted).
- Transaction commit clearing an outbound record (the B2 workflow: drop, retry via transaction, source accepts, record clears), and a writer-skip test proving a skipped change neither clears a record nor counts as written.
- Connector suites locally: OPC UA, MQTT, WebSocket (CI path filters skip them for shared-library changes; run the OPC UA suite alone, it cannot run concurrently).
- Both API snapshot tests; accept the Connectors snapshot change.
- Benchmark gate as above.
- Connector Tester run recommended: this touches `SubjectSourceBase` and `WriteRetryQueue` on the outbound pipeline of every connector. Agreed during design per the planning rule; confirm before finalizing the PR.

## Documentation edits

`docs/connectors-monitoring.md`:

1. "Reading Per-Property State": reword the opening (state is no longer derived with no per-property storage), append the `TryGetDivergence()` example and the rule that `Diverged` describes one property's relationship with its source.
2. "The State Model": add `Diverged` to the enum block; name both property-only members where `Unclaimed` is described.
3. "The Event Stream" table: add rows for the divergence event kinds with `Property` set.
4. "Diagnostics and State answer different questions": note that `Diverged` sits on the `State` side even though a dropped outbound write can cause it, because it answers "can I trust this value"; a queued write says nothing, a dropped one never arrives.
5. New section "What Diverged Does Not Cover": the floor-not-ceiling framing, the covered table (inbound refusal, inbound transform, structural refusal, outbound drop), and the not-covered table above.

Elsewhere: `docs/connectors.md`'s "Inbound Update Error Handling" points at `Diverged` for how a dropped update becomes observable; `docs/validation.md` notes that a rejected inbound value is now reported rather than only logged; `docs/subject-guidelines.md` gains the hook sentence from the Interactions section.

## Decisions log (this brainstorm, 2026-08-31)

1. Staleness by value predicate with eager clear on landing; no revisions, no tombstones. The revision machinery stays dead (see falsified claims).
2. Detection at two Connectors sites sharing a helper, read-back based; the write-interceptor and core-outcome-return alternatives were rejected (core API surgery, hot-path cost, and the interceptor would trust the stamp outcome the probe falsified).
3. Outbound in scope, per-property at every live drop site, with the inverted value predicate providing supersession semantics.
4. Structural properties in scope for refusals both directions, as payload-free latched records; structural transform detection excluded.
5. No weak references; claim-scoped record lifetime via `SetSource`/`RemoveSource`.
6. `record.Value` holds data values only, guaranteed by the registry classification gate, and stores the exception message rather than the exception.
7. Re-entrant clamp: pre-existing footgun, behavior kept, one documentation sentence, closed for divergence by construction.

Amended after adversarial review (2026-08-31, findings verified independently against code):

8. Ownership is re-checked at the outbound drop and clear sites rather than inherited from capture-time filtering.
9. The three `SourceTransactionWriter` interpretation sites join the clear inventory; without them a transaction-confirmed delivery never clears.
10. `WriteResult` gains `SkippedChanges`, because two shipping writers silently skip changes and the documented "unlisted counts as written" contract is therefore false today. **This is the one amendment that widens scope beyond Connectors** (it edits the OPC UA and MQTT writers) and needs explicit approval; the alternative is to narrow the clear rule and document the resulting hole, at the cost of erasing true records on a skip.
11. A-B-A ring eviction is an accepted transient rather than a solved case.

## Falsified claims from the review rounds (do not repeat)

- The call-site count is three, not twelve and not five; extension binding is decided by the receiver's static type.
- Cycle-boundary clearing in `SubjectPropertyWriter.StartBuffering` is unimplementable (ownership manager internal to connector assemblies, empty set at first call, fires on retries, misses OPC UA apply paths).
- The server exclusion is not a compile-time fact via the applier; it is enforced by the runtime gates (and the interface hierarchy happens to exclude servers today).
- `AspNetCore` does reference Validation.
- MQTT never re-applies a snapshot: `LoadInitialStateAsync` returns null.
- `WriteRetryQueue`'s buffering-disabled drop site is dead code in production (`maxQueueSize > 0` always); the live drops are the sites tabled above.
- `TryGetWriteState` returns the property's last commit from any thread, not this write's, so it cannot supply a producing revision.
- Outbound changes routinely carry `Revision == 0` (`CollapsePerProperty` and `ChangeMerger` call `WithoutRevision()`).
- `PropertyReference` exposes no compare-and-replace; it does expose compare-and-remove (`TryRemovePropertyData`), which is what the clears use.

## Untouched invariants

The `PendingFrame` stamp mechanism and its five invariants (`PendingOrigin.cs`) are not modified: `Set` assigns a whole new frame; scope capture stays by value before overwrite; the stamp write is gated on a non-Local attempted origin as a correctness requirement; the reader stays inside the `using`; every non-Local producer goes through `Set`. The stamp remains positive (absence means not applied). This design adds no reader of the stamp and no new producer.
