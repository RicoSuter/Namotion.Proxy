# Source reconciliation spike

This opt-in experiment explores prevention of stale scalar notifications and convergence after application writes stop. It does not resolve issue #373 or propose a merge-ready API. Existing connector behavior remains the default.

## Behavior

After writing `100`, a delayed notification containing `90` requests an authoritative read instead of overwriting the local value. If the source now reports `100`, the model stays at `100`. If it reports a correction, that correction can apply regardless of the notification's timestamp. A notification arriving after that read still requests verification; completing one read does not make later notifications trustworthy.

Source writes also schedule verification, so convergence does not require a write echo or another application write. Failed or incomplete reads retry. A result is rejected when a local write, source operation, notification, reconnect, or ownership change overtakes it. The final check runs under the subject lock immediately before the property commit. Transaction coordination remains active through local confirmation and compensation.

This gives quiescent convergence in the tested scalar cases, assuming the source becomes readable and returns its settled value. Ordinary OPC UA polling requests another refresh without invalidating an active read. Continuously arriving ambiguous notifications can still postpone application indefinitely.

## Integration points

| Layer | Experimental change |
| --- | --- |
| Core | `IPropertyWriteGuard` provides conditional admission at the setter terminal. A writer-only check leaves a race before the setter commits. |
| Tracking | A guarded source setter and `ITransactionWriteCoordinator` extend protection across local transaction application. |
| Connectors | `ISourcePropertyReader`, a shared reconciler, scalar `SubjectPropertyWriter.WriteValue`, and source-operation scopes. Ownership and connection generations invalidate obsolete reads. |
| OPC UA | `EnableExperimentalSourceReconciliation = true` enables batched `Read` calls with `maxAge = 0`, reusing node mappings and value conversion. The experiment replaces the existing read-after-write path and routes polling through the coordinator. |
| MQTT | `ExperimentalReconciliationReader` accepts an application-provided authoritative reader. Notifications and write completion then use the same coordinator. |

MQTT does not gain a generic read protocol here. Its live test supplies a reader over the test broker's model, while writes and notifications use real MQTT connections. It proves reuse of the coordination mechanism; an actual MQTT deployment still needs a request/response or authoritative state contract.

## Evidence

Fourteen deterministic shared tests cover delayed notifications before and after verification, absent echoes, local writes overtaking reads, commit-time races, notification and polling overlap, read failure retries, reconnect, transaction success and failure, operations without local commits, and ownership release/reclaim. A live OPC UA test verifies a transaction, injects a stale subscription callback, reads the server, and follows an external server change. A live MQTT test exercises the equivalent callback path with the supplied reader.

All five affected project suites passed: core 157, tracking 571, connectors 805, OPC UA 357, and MQTT 117, totaling 2,007 tests. Intentional experimental public API additions are recorded in their snapshots. Independent review identified and helped close lifecycle and ordering gaps; its verdict is suitability for an experimental spike, not production approval.

## Decisions before production work

- Decide whether these public interfaces are the right permanent contract. Enable reconciliation before property ownership is claimed and before initial state loading. Existing claims are not registered by late enablement; the built-in integrations use the required order.
- Reduce and measure costs: conservative notification-triggered reads, tracked-property scans, allocations, and one dedicated observation thread per source.
- Define graph/collection reconciliation and behavior with continuous notifications. This experiment handles scalars only; structural observations routed through the opted-in scalar writer are currently ignored without a legacy fallback.
- Define an awaitable settlement contract. `HasPendingReconciliation` is a diagnostic snapshot, and existing source synchronization status is not a verification barrier.
- Extend failure and protocol coverage, including real device freshness and an actual MQTT read contract. OPC UA currently discards already-collected batch results if a later mapping, conversion, or read fails, so one property can delay healthy properties. Guarding the commit does not undo custom interceptor side effects that execute before the terminal.
- Agree and run the long Connector Tester and performance measurements before treating this as a production fix. Neither has run for this spike.
