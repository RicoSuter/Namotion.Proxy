using Namotion.Interceptor.Connectors.Reconciliation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

public static class SubjectSourceExtensions
{
    // TODO: ConditionalWeakTable pattern means SemaphoreSlim is not explicitly disposed when source is GC'd.
    // This relies on SemaphoreSlim's finalizer for cleanup. For long-lived sources (typical), this is acceptable.
    // If short-lived sources become common, consider having sources own their write lock via IDisposable.
    private static readonly ConditionalWeakTable<ISubjectSource, SourceWriteLock> WriteLocks = new();

    /// <summary>
    /// Writes changes to the source in batches, respecting the source's maximum batch size.
    /// Returns a <see cref="WriteResult"/> containing which changes failed.
    /// Never throws for write failures, errors are reported in the result.
    /// </summary>
    /// <remarks>
    /// This method automatically synchronizes write operations unless the source implements
    /// <see cref="ISupportsConcurrentWrites"/>. Callers should always use this method
    /// instead of calling <see cref="ISubjectSource.WriteChangesAsync"/> directly.
    /// </remarks>
    /// <returns>A <see cref="WriteResult"/> containing failed changes and any error.</returns>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<WriteResult> WriteChangesInBatchesAsync(
        this ISubjectSource source,
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        var count = changes.Length;
        if (count == 0)
        {
            return WriteResult.Success;
        }

        using var reconciliation = SourceWriteCoordinationScope.RetainOrReturn(
            (source as SubjectSourceBase)?.PropertyWriter.Reconciler?.BeginOperation(changes));

        // Skip synchronization for sources that handle their own concurrency
        if (source is ISupportsConcurrentWrites)
        {
            return await WriteChangesInBatchesCoreAsync(source, changes, cancellationToken).ConfigureAwait(false);
        }

        var writeLock = WriteLocks.GetValue(source, static _ => new SourceWriteLock());
        try
        {
            await writeLock.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // Return failure instead of throwing - let caller handle cancellation uniformly
            return WriteResult.Failure(changes, ex);
        }

        try
        {
            return await WriteChangesInBatchesCoreAsync(source, changes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Semaphore.Release();
        }
    }

    private static async ValueTask<WriteResult> WriteChangesInBatchesCoreAsync(
        ISubjectSource source,
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        var confirmedCount = 0;
        try
        {
            var count = changes.Length;
            var batchSize = source.WriteBatchSize;

            if (batchSize <= 0 || count <= batchSize)
            {
                // Single batch - delegate directly to source (zero allocation on success)
                var result = await source.WriteChangesAsync(changes, cancellationToken).ConfigureAwait(false);

                // Normalize the unenumerated-failure shorthand once, at the choke point all callers use.
                return result.Error is not null && result.FailedChanges.IsEmpty
                    ? WriteResult.Failure(changes, result.Error)
                    : result;
            }

            // Multi-batch: process sequentially, stop on first failure
            for (var i = 0; i < count; i += batchSize)
            {
                var currentBatchSize = Math.Min(batchSize, count - i);
                var batch = changes.Slice(i, currentBatchSize);

                var batchResult = await source.WriteChangesAsync(batch, cancellationToken).ConfigureAwait(false);
                if (batchResult.Error is not null)
                {
                    // The batch's failed changes (the whole batch when unenumerated) plus the unprocessed
                    // remainder, matched by identity since any subset of a batch can fail.
                    var batchFailed = batchResult.FailedChanges.IsEmpty
                        ? batch
                        : batchResult.FailedChanges.AsMemory();
                    var remaining = changes.Slice(i + currentBatchSize);
                    if (remaining.IsEmpty)
                    {
                        return WriteResult.PartialFailure(batchFailed, batchResult.Error);
                    }

                    // The array never escapes, so the ImmutableArray takes ownership without a second copy.
                    var failedChanges = new SubjectPropertyChange[batchFailed.Length + remaining.Length];
                    batchFailed.CopyTo(failedChanges);
                    remaining.CopyTo(failedChanges.AsMemory(batchFailed.Length));
                    return WriteResult.PartialFailure(
                        ImmutableCollectionsMarshal.AsImmutableArray(failedChanges), batchResult.Error);
                }

                confirmedCount = i + currentBatchSize;
            }

            // All batches succeeded (zero allocation)
            return WriteResult.Success;
        }
        catch (Exception ex)
        {
            // Batches confirmed before the throw are written and must not be condemned; only the
            // throwing batch (outcome unknown) and the unprocessed remainder are unconfirmed.
            return confirmedCount == 0
                ? WriteResult.Failure(changes, ex)
                : WriteResult.PartialFailure(changes.Slice(confirmedCount), ex);
        }
    }

    /// <summary>
    /// Internal lock holder for per-source write synchronization.
    /// </summary>
    internal sealed class SourceWriteLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
    }
}
