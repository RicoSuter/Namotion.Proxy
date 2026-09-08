# Ownership tombstone performance spike

This isolated prototype is based on `f6039fba401316e8c04c09726397695df9ef68b9`. It replaces the separate pending-release dictionary and leaf lock with a tombstone in the existing concurrent ownership map. It does not establish full callback-support correctness or a throughput improvement.

Each ownership lifetime has a one-way volatile releasing flag. Marking it hides that record from `TryGetOwnership` and `IsOwned`; the record remains available as the queued release identity. Reattachment replaces the map entry with a fresh ownership record. A queued release can release the context claim and remove the entry only when its token still matches the current tombstone. This preserves the reattach/remove ABA protection without a second dictionary.

All ownership-map writes and release identity comparisons remain under the topology gate. Readers outside that gate use the concurrent map and volatile flag. Marking happens before baseline removal, which preserves the epoch behavior expected by the separate getter-journal work. A cleanup reached before marking cannot remove a live ownership record. A cleanup reached after marking removes only the matching tombstone, preserving the existing failure-path behavior. The known release-enumerator reentry issue is separate work and is not fixed here.

## Validation

The focused release-epoch, resurrection-failure, failure-boundary, seed-publication, ownership-oracle, and attach-residue run passed 22 of 23 tests. The failure was the existing `AttachResidueTests.WhenARollbackCallbackThrows_ThenTheAttachExceptionIsTheOneThatEscapes` exception-selection limitation.

The full nonintegration Tracking suite passed 785 tests and failed the same 20 tests as the base, with no new or restored failure names. Registry passed all 185 tests. Original tests were unchanged. The final run also checks the simplification that a current releasing tombstone necessarily is not owned.

## Allocation evidence

A standalone, untimed .NET 9 Release probe measured per-thread allocated bytes after 4,096 warmup operations, followed by 4,096 measured operations. Ownership instances were created through a generated constructor delegate outside production code; lifecycle contexts used `WithLifecycle`. The reattach/remove case detached a child whose first detach callback reattached and removed that same child again.

| Operation | Base | Tombstone | Difference |
| --- | ---: | ---: | ---: |
| Ownership object | 64 B | 64 B | 0 B |
| Warmed lifecycle context | 2,232 B | 2,088 B | -144 B |
| Reattach/remove cycle | 856 B | 808 B | -48 B |

The flag fits the current object layout padding. The cycle saves a concurrent-map node because replacing the retained tombstone can reuse its entry. These are allocation observations, not timing results. Probe files and logs are under `/private/tmp/pr494-tombstone-perf-probe` and `/private/tmp/tombstone-v3-*-allocation.log`.

## Performance tradeoffs and scope

The change removes a dictionary, a leaf lock, and repeated release-state lookups. Active ownership queries now read a volatile flag. An ordinary write also replaces the previous empty-release-dictionary shortcut with a concurrent-map lookup. Tombstones retain ownership-map entries until queued release delivery, increasing transient map occupancy during large detach batches. Only a coordinated matched benchmark can establish the net CPU effect.

This commit changes internal storage and query paths only. Property/lifecycle publication order, pending-release admission, historical detach context access, resurrection rollback, and public APIs retain their existing contracts and limitations. Deeper ordinary-reconciliation partial-edge behavior remains outside this optimization.
