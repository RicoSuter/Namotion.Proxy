using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Transactions;

/// <summary>
/// Handler that writes transaction changes to their associated external sources.
/// Only writes changes that have an associated source; local (no-source) changes are
/// skipped and left for the transaction to apply.
/// </summary>
internal sealed class SourceTransactionWriter : ITransactionWriter, ITransactionWriteCoordinator
{
    public IDisposable BeginCoordination() => new Reconciliation.SourceWriteCoordinationScope();

    /// <summary>
    /// One source's changes with their snapshot indices in lockstep, so accepted slots can be marked.
    /// Also serves as the multi-source revert state.
    /// </summary>
    private sealed record SourceGroup(List<SubjectPropertyChange> Changes, List<int> Indices);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<SourceWriteResult> WriteToSourcesAsync(
        Memory<SubjectPropertyChange> changes,
        TransactionRequirement requirement,
        CancellationToken cancellationToken)
    {
        // Single pass reading each property's source exactly once (a concurrent SetSource/RemoveSource
        // cannot be observed inconsistently), classifying single- vs multi-source with lazy allocation.
        // Snapshot indices are tracked so accepted slots can be marked after the write succeeds.

        ISubjectSource? singleSource = null;
        Dictionary<ISubjectSource, SourceGroup>? changesBySource = null;

        SubjectPropertyChange firstChange = default;   // held until single vs multi is known
        var firstIndex = 0;
        SubjectPropertyChange[]? buffer = null;        // allocated once a 2nd same-source change confirms single-source
        int[]? indices = null;                         // parallel to buffer

        var count = 0;
        var span = changes.Span;
        for (var snapshotIndex = 0; snapshotIndex < span.Length; snapshotIndex++)
        {
            var change = span[snapshotIndex];
            if (!change.Property.TryGetSource(out var source))
            {
                continue;
            }

            if (changesBySource is not null)
            {
                AddToSourceGroup(changesBySource, source, change, snapshotIndex);
            }
            else if (singleSource is null)
            {
                singleSource = source;
                firstChange = change;
                firstIndex = snapshotIndex;
            }
            else if (ReferenceEquals(source, singleSource))
            {
                if (buffer is null)
                {
                    buffer = new SubjectPropertyChange[changes.Length];
                    indices = new int[changes.Length];
                    buffer[count] = firstChange;
                    indices[count++] = firstIndex;
                }
                buffer[count] = change;
                indices![count++] = snapshotIndex;
            }
            else
            {
                // Second distinct source: switch to multi-source, seeding the dict with the prefix
                // collected so far (all of it belonged to singleSource).
                var prefixChanges = new List<SubjectPropertyChange>(buffer is null ? 1 : count);
                var prefixIndices = new List<int>(buffer is null ? 1 : count);
                if (buffer is null)
                {
                    prefixChanges.Add(firstChange);
                    prefixIndices.Add(firstIndex);
                }
                else
                {
                    for (var i = 0; i < count; i++)
                    {
                        prefixChanges.Add(buffer[i]);
                        prefixIndices.Add(indices![i]);
                    }
                }
                changesBySource = new Dictionary<ISubjectSource, SourceGroup>
                {
                    [singleSource] = new SourceGroup(prefixChanges, prefixIndices)
                };
                AddToSourceGroup(changesBySource, source, change, snapshotIndex);
            }
        }

        if (singleSource is null)
        {
            return new SourceWriteResult([], [], [], RevertState: null);
        }

        if (changesBySource is not null)
        {
            return await WriteToMultipleSourcesAsync(changes, changesBySource, requirement, cancellationToken).ConfigureAwait(false);
        }

        if (buffer is null)
        {
            buffer = [firstChange];
            indices = [firstIndex];
            count = 1;
        }

        return await WriteToSingleSourceAsync(changes, singleSource, buffer, indices!, count, requirement, cancellationToken).ConfigureAwait(false);
    }

    private static void AddToSourceGroup(Dictionary<ISubjectSource, SourceGroup> changesBySource, ISubjectSource source, SubjectPropertyChange change, int snapshotIndex)
    {
        if (!changesBySource.TryGetValue(source, out var group))
        {
            group = new SourceGroup([], []);
            changesBySource[source] = group;
        }
        group.Changes.Add(change);
        group.Indices.Add(snapshotIndex);
    }

    /// <summary>
    /// Fast path when all source-bound changes target one source. <paramref name="buffer"/> holds them in its
    /// first <paramref name="count"/> slots (privately owned, not the commit snapshot), <paramref name="indices"/>
    /// their snapshot indices in lockstep. Accepted snapshot slots are marked; the returned written set keeps
    /// the unmarked copies.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<SourceWriteResult> WriteToSingleSourceAsync(
        Memory<SubjectPropertyChange> snapshot,
        ISubjectSource source,
        SubjectPropertyChange[] buffer,
        int[] indices,
        int count,
        TransactionRequirement requirement,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SubjectPropertyChange> sourceChanges =
            count == buffer.Length ? buffer : new ArraySegment<SubjectPropertyChange>(buffer, 0, count);

        if (requirement == TransactionRequirement.SingleWrite)
        {
            var batchSize = source.WriteBatchSize;
            if (batchSize > 0 && count > batchSize)
            {
                // Nothing was written yet, so there is no revert state.
                return new SourceWriteResult(
                    [],
                    sourceChanges,
                    [new InvalidOperationException(
                        $"SingleWrite requirement violated: Transaction contains {count} changes for source '{source.GetType().Name}', but WriteBatchSize is {batchSize}.")],
                    RevertState: null);
            }
        }

        // FailedChanges is complete (see WriteChangesInBatchesAsync), so everything else reached the
        // source. RevertState is the source itself: every written change belongs to it.
        var writeResult = await source.WriteChangesInBatchesAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        if (writeResult.Error is null)
        {
            MarkSnapshotSlots(snapshot.Span, indices, count, source);
            return new SourceWriteResult(sourceChanges, [], [], RevertState: source);
        }

        IReadOnlyList<int> sourceIndices =
            count == indices.Length ? indices : new ArraySegment<int>(indices, 0, count);
        var failedSet = ToPropertySet(writeResult.FailedChanges);
        MarkSnapshotSlotsExcept(snapshot.Span, sourceChanges, sourceIndices, failedSet, source);
        var written = ExcludeFailed(sourceChanges, failedSet);
        return new SourceWriteResult(
            written,
            [.. writeResult.FailedChanges],
            [new SourceTransactionWriteException(source, [.. writeResult.FailedChanges], writeResult.Error)],
            RevertState: source);
    }

    /// <summary>
    /// Writes source-bound changes grouped per source, dispatched in parallel. Each source's accepted
    /// snapshot slots are marked after its write succeeds.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<SourceWriteResult> WriteToMultipleSourcesAsync(
        Memory<SubjectPropertyChange> snapshot,
        Dictionary<ISubjectSource, SourceGroup> changesBySource,
        TransactionRequirement requirement,
        CancellationToken cancellationToken)
    {
        if (requirement == TransactionRequirement.SingleWrite)
        {
            var validationError = ValidateSingleWriteRequirement(changesBySource);
            if (validationError != null)
            {
                // Nothing was written yet, so there is no revert state.
                var validationFailed = FlattenChanges(changesBySource);
                return new SourceWriteResult([], validationFailed, [validationError], RevertState: null);
            }
        }

        if (changesBySource.Count == 0)
        {
            return new SourceWriteResult([], [], [], RevertState: null);
        }

        // RevertState reuses the grouping: revert resolves each written change to its original source
        // from this map instead of re-deriving (the mapping may have changed since).
        var (written, failed, errors) = await WriteToExternalSourcesAsync(snapshot, changesBySource, cancellationToken).ConfigureAwait(false);
        return new SourceWriteResult(written, failed, errors, RevertState: changesBySource);
    }

    public async ValueTask<SourceRevertResult> RevertSourceWritesAsync(
        IReadOnlyList<SubjectPropertyChange> written,
        object? revertState,
        CancellationToken cancellationToken)
    {
        if (written.Count == 0 || revertState is null)
        {
            return new SourceRevertResult([], []);
        }

        var failed = new List<SubjectPropertyChange>();
        var errors = new List<Exception>();

        if (revertState is ISubjectSource source)
        {
            // Single-source write path: every written change belongs to this exact source.
            var single = new Dictionary<ISubjectSource, List<SubjectPropertyChange>>(1)
            {
                [source] = [.. written]
            };
            await TryRevertSourceWritesAsync(single, failed, errors, cancellationToken).ConfigureAwait(false);
            return new SourceRevertResult(failed, errors);
        }

        if (revertState is Dictionary<ISubjectSource, SourceGroup> groups)
        {
            // Revert only the passed 'written' subset, to the original sources recorded at write time.
            var writtenSet = new HashSet<PropertyReference>(written.Count, PropertyReference.Comparer);
            foreach (var change in written)
            {
                writtenSet.Add(change.Property);
            }

            var toRevert = new Dictionary<ISubjectSource, List<SubjectPropertyChange>>(groups.Count);
            foreach (var (groupSource, group) in groups)
            {
                List<SubjectPropertyChange>? subset = null;
                foreach (var change in group.Changes)
                {
                    if (writtenSet.Contains(change.Property))
                    {
                        (subset ??= []).Add(change);
                    }
                }
                if (subset is not null)
                {
                    toRevert[groupSource] = subset;
                }
            }

            await TryRevertSourceWritesAsync(toRevert, failed, errors, cancellationToken).ConfigureAwait(false);
        }

        return new SourceRevertResult(failed, errors);
    }

    private static HashSet<PropertyReference> ToPropertySet(IReadOnlyList<SubjectPropertyChange> changes)
    {
        var set = new HashSet<PropertyReference>(changes.Count, PropertyReference.Comparer);
        foreach (var change in changes)
        {
            set.Add(change.Property);
        }
        return set;
    }

    private static List<SubjectPropertyChange> ExcludeFailed(
        IReadOnlyList<SubjectPropertyChange> changes, HashSet<PropertyReference> failed)
    {
        // Math.Max: a contract-violating source can report failures for properties it was never given.
        var written = new List<SubjectPropertyChange>(Math.Max(0, changes.Count - failed.Count));
        foreach (var change in changes)
        {
            if (!failed.Contains(change.Property))
            {
                written.Add(change);
            }
        }
        return written;
    }

    /// <summary>
    /// Marks accepted snapshot slots with their source so the commit's local apply is recognized as an
    /// echo by the outbound connector queue. Must run only after a successful write.
    /// </summary>
    private static void MarkSnapshotSlots(Span<SubjectPropertyChange> snapshot, int[] indices, int count, ISubjectSource source)
    {
        for (var i = 0; i < count; i++)
        {
            var slot = indices[i];
            snapshot[slot] = snapshot[slot].WithOrigin(ChangeOrigin.Confirmed(source));
        }
    }

    /// <summary>
    /// Marks the slots of the non-failed changes, leaving failed slots untouched.
    /// </summary>
    private static void MarkSnapshotSlotsExcept(
        Span<SubjectPropertyChange> snapshot,
        IReadOnlyList<SubjectPropertyChange> changes,
        IReadOnlyList<int> indices,
        HashSet<PropertyReference> failed,
        ISubjectSource source)
    {
        for (var i = 0; i < changes.Count; i++)
        {
            if (!failed.Contains(changes[i].Property))
            {
                var slot = indices[i];
                snapshot[slot] = snapshot[slot].WithOrigin(ChangeOrigin.Confirmed(source));
            }
        }
    }

    private static async Task<(List<SubjectPropertyChange> Written, List<SubjectPropertyChange> Failed, List<Exception> Errors)>
        WriteToExternalSourcesAsync(
            Memory<SubjectPropertyChange> snapshot,
            Dictionary<ISubjectSource, SourceGroup> changesBySource,
            CancellationToken cancellationToken)
    {
        var written = new List<SubjectPropertyChange>();
        var failed = new List<SubjectPropertyChange>();
        var errors = new List<Exception>();

        var taskIndex = 0;
        var writeTasks = new Task<(List<SubjectPropertyChange> Written, List<SubjectPropertyChange> Failed, Exception? Error)>[changesBySource.Count];
        foreach (var kvp in changesBySource)
        {
            writeTasks[taskIndex++] = TryWriteToSourceAsync(snapshot, kvp.Key, kvp.Value, cancellationToken);
        }

        var results = await Task.WhenAll(writeTasks).ConfigureAwait(false);

        foreach (var (writtenList, failList, error) in results)
        {
            written.AddRange(writtenList);
            failed.AddRange(failList);
            if (error != null)
            {
                errors.Add(error);
            }
        }

        return (written, failed, errors);
    }

    /// <summary>
    /// Writes one source's changes and marks its accepted snapshot slots. Safe to run in parallel:
    /// one source per property makes the groups disjoint, so concurrent marking never overlaps.
    /// </summary>
    private static async Task<(List<SubjectPropertyChange> Written, List<SubjectPropertyChange> Failed, Exception? Error)>
        TryWriteToSourceAsync(
            Memory<SubjectPropertyChange> snapshot,
            ISubjectSource source,
            SourceGroup group,
            CancellationToken cancellationToken)
    {
        var sourceChanges = group.Changes;
        var memory = new ReadOnlyMemory<SubjectPropertyChange>(sourceChanges.ToArray());
        var result = await source.WriteChangesInBatchesAsync(memory, cancellationToken).ConfigureAwait(false);

        if (result.Error is not null)
        {
            var failedSet = ToPropertySet(result.FailedChanges);
            MarkSnapshotSlotsExcept(snapshot.Span, sourceChanges, group.Indices, failedSet, source);
            var written = ExcludeFailed(sourceChanges, failedSet);
            var error = new SourceTransactionWriteException(source, [.. result.FailedChanges], result.Error);
            return (written, [.. result.FailedChanges], error);
        }

        MarkSnapshotSlots(snapshot.Span, group.Indices, source);
        return (sourceChanges, [], null);
    }

    private static void MarkSnapshotSlots(Span<SubjectPropertyChange> snapshot, List<int> indices, ISubjectSource source)
    {
        foreach (var slot in indices)
        {
            snapshot[slot] = snapshot[slot].WithOrigin(ChangeOrigin.Confirmed(source));
        }
    }

    /// <summary>
    /// Attempts to revert successful external source writes.
    /// </summary>
    private static async Task TryRevertSourceWritesAsync(
        Dictionary<ISubjectSource, List<SubjectPropertyChange>> successfulWrites,
        List<SubjectPropertyChange> failedChanges,
        List<Exception> errors,
        CancellationToken cancellationToken)
    {
        foreach (var (source, originalChanges) in successfulWrites)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rollbackChanges = Enumerable.Reverse(originalChanges)
                .Select(c => c.ToRollbackChange())
                .ToArray();

            var memory = new ReadOnlyMemory<SubjectPropertyChange>(rollbackChanges);
            var result = await source.WriteChangesInBatchesAsync(memory, cancellationToken).ConfigureAwait(false);

            if (result.Error is not null)
            {
                var rollbackException = new SourceTransactionWriteException(
                    source,
                    originalChanges,
                    new InvalidOperationException($"Failed to rollback changes to source {source.GetType().Name}", result.Error));

                failedChanges.AddRange(originalChanges);
                errors.Add(rollbackException);
            }
        }
    }

    /// <summary>
    /// Validates that the SingleWrite requirement is satisfied.
    /// </summary>
    private static Exception? ValidateSingleWriteRequirement(
        Dictionary<ISubjectSource, SourceGroup> changesBySource)
    {
        if (changesBySource.Count == 0)
            return null;

        if (changesBySource.Count > 1)
        {
            var sourceNames = new string[changesBySource.Count];
            var nameIndex = 0;
            foreach (var kvp in changesBySource)
            {
                sourceNames[nameIndex++] = kvp.Key.GetType().Name;
            }
            return new InvalidOperationException(
                $"SingleWrite requirement violated: Transaction spans {changesBySource.Count} sources ({string.Join(", ", sourceNames)}), but only 1 is allowed.");
        }

        ISubjectSource? source = null;
        SourceGroup? group = null;
        foreach (var kvp in changesBySource)
        {
            source = kvp.Key;
            group = kvp.Value;
            break;
        }

        // source is always non-null here since we checked Count == 0 above
        var batchSize = source!.WriteBatchSize;
        if (batchSize > 0 && group!.Changes.Count > batchSize)
        {
            return new InvalidOperationException(
                $"SingleWrite requirement violated: Transaction contains {group.Changes.Count} changes for source '{source.GetType().Name}', " +
                $"but WriteBatchSize is {batchSize}.");
        }

        return null;
    }

    private static List<SubjectPropertyChange> FlattenChanges(
        Dictionary<ISubjectSource, SourceGroup> changesBySource)
    {
        var totalCount = 0;
        foreach (var group in changesBySource.Values)
        {
            totalCount += group.Changes.Count;
        }

        var changes = new List<SubjectPropertyChange>(totalCount);
        foreach (var group in changesBySource.Values)
        {
            changes.AddRange(group.Changes);
        }

        return changes;
    }
}
