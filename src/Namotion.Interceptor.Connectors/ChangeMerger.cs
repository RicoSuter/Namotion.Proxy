using System.Buffers;
using System.Runtime.InteropServices;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Collapses a flush batch to a single change per property, keeping the oldest commit's old value and
/// the newest commit's new value. Note that the old value is only as good as what the write pipeline
/// captured: the generated setter reads it outside the subject lock, so picking by revision decides
/// WHICH change's old value survives, not that it is the value committed at the preceding revision.
/// Both ends are picked by <see cref="SubjectPropertyChange.Revision"/> (commit
/// order) rather than by arrival position, because a change is enqueued after its commit and outside the
/// subject lock, so concurrent writers to one property can enqueue in the opposite order they committed.
/// Owns the pooled scratch buffers so that a flush allocates nothing per batch.
/// Not thread-safe: the caller must serialize all calls, which <see cref="ChangeQueueProcessor"/> does
/// by holding its flush gate for the whole merge, write and reset cycle.
/// </summary>
internal sealed class ChangeMerger : IDisposable
{
    private const int BufferMinimumSize = 256;
    private const int BufferMaximumSize = 1024;

    // Mirrors the buffer policy above. The minimum is deliberately both the pre-size cap and the shrink
    // floor: if they differed, a narrow burst would grow to the cap, shrink to the floor on reset and
    // regrow on the next batch, allocating forever.
    private const int PropertyIndexMinimumCapacity = 256;
    private const int PropertyIndexMaximumCapacity = 1024;

    // Releasing either high-water mark is cheap once and expensive repeatedly, and neither the index nor
    // the buffer can tell a burst from a working set by looking at one batch. Flush widths vary constantly
    // under load, so releasing on the first narrow batch makes a wide one regrow it immediately.
    // Requiring the narrow condition to persist distinguishes "the load went quiet" from "this flush
    // happened to be small". The +17% allocation regression that produced this constant was measured for
    // the index trim only; the buffer shrink was ungated then, so its share was never measured.
    private const int NarrowBatchesBeforeTrim = 4;

    // Per property: the slot of its surviving change, the arrival index that seeded that slot, and the
    // revision bounds seen so far. Bounds are running, not global, which is what lets one pass do the
    // work: the walk goes backwards, so the last extension on each side is the batch extremum.
    // Revisions are only comparable within one subject, which holds here because the key pins the
    // collapse to a single property. A committed write never takes revision 0, so a zero lower bound
    // doubles as the "this property has an unordered change" flag the merge falls back on. Zero is
    // read as a sentinel rather than as an ordinary minimum, so a change carrying a negative revision
    // (constructible through the public factory, never produced by the write path) is outside the
    // contract and orders arbitrarily.
    private readonly Dictionary<PropertyReference, (int Index, int SeedIndex, long LowestRevision, long HighestRevision)> _propertyIndices
        = new(PropertyReference.Comparer);

    // Reusable buffer for merged changes (rented from ArrayPool to avoid allocations on resize)
    private SubjectPropertyChange[] _buffer = RentClearedBuffer(BufferMinimumSize);
    private int _count;
    private int _consecutiveNarrowBatches;

    /// <summary>
    /// Rents a buffer and clears it once. <see cref="ArrayPool{T}"/> hands out whatever the previous
    /// renter left behind and a <see cref="SubjectPropertyChange"/> carries object references, so this is
    /// what establishes the invariant the flush path relies on: nothing outside <c>[0, _count)</c> holds a
    /// reference, which is why releasing a batch only has to clear that prefix.
    /// </summary>
    private static SubjectPropertyChange[] RentClearedBuffer(int minimumLength)
    {
        var buffer = ArrayPool<SubjectPropertyChange>.Shared.Rent(minimumLength);
        Array.Clear(buffer, 0, buffer.Length);
        return buffer;
    }

    /// <summary>
    /// Collapses the batch to one change per property. The result is ordered by the arrival position of
    /// each property's last occurrence.
    /// </summary>
    /// <param name="changes">The batch to collapse, in arrival order.</param>
    /// <returns>The merged changes. The memory points into the pooled buffer and stays valid until
    /// the next <see cref="Merge"/>, <see cref="Reset"/> or <see cref="Dispose"/> call, so the caller
    /// can await a write handler on it before resetting. Empty once <see cref="Dispose"/> has run.</returns>
    /// <param name="deliveryRule">When set, drops survivors the model has already moved past under
    /// that rule, which is what makes delivery converge across flushes rather than only within one. Null
    /// by default so the batch collapse can be exercised on its own.</param>
    /// <param name="admissionHandler">Optional terminal admission invoked with the exact survivor count
    /// after delivery suppression. Returning false hides the rejected batch from the caller.</param>
    public ReadOnlyMemory<SubjectPropertyChange> Merge(
        ReadOnlySpan<SubjectPropertyChange> changes,
        ChangeDeliveryRule? deliveryRule = null,
        Func<int, bool>? admissionHandler = null)
    {
        if (_buffer is null)
        {
            // Merge is empty after explicit disposal; Reset and Dispose are idempotent too.
            return ReadOnlyMemory<SubjectPropertyChange>.Empty;
        }

        _propertyIndices.Clear();

        // Release the previous batch here rather than only trusting Reset to have done it. A missed
        // Reset would otherwise hand a dirty array back to the shared pool on the growth path below,
        // keeping its subjects and boxed values alive, or leave references stranded past a smaller
        // new count where no later Reset would ever clear them. Free on the normal path, where the
        // count is already zero.
        if (_count > 0)
        {
            Array.Clear(_buffer, 0, _count);
            _count = 0;
        }

        if (changes.Length == 0)
        {
            return ReadOnlyMemory<SubjectPropertyChange>.Empty;
        }

        // Capped because the index is keyed by property and the batch length says nothing about how many
        // distinct ones it carries. Above the cap it grows organically and keeps that capacity.
        _propertyIndices.EnsureCapacity(Math.Min(changes.Length, PropertyIndexMinimumCapacity));

        // Ensure the buffer is large enough (rent from pool to avoid allocations). Returning without
        // clearing is safe because the release above leaves the whole array clear.
        if (_buffer.Length < changes.Length)
        {
            ArrayPool<SubjectPropertyChange>.Shared.Return(_buffer);
            _buffer = RentClearedBuffer(changes.Length);
        }

        // One backward pass. Keeps the lowest revision's old value and the highest revision's new value,
        // or, for a property that has an unordered change, the first arrival's old value and the last
        // arrival's new value. Backward iteration finds last occurrences first, which both preserves
        // last-occurrence emit order and makes the running bounds sufficient: each side is only ever
        // extended, so the final extension is the batch extremum and no prior pass is needed to know it.
        //
        // Relies on two changes to one property never sharing a nonzero revision. That holds by
        // construction: a committed write takes a strictly incremented per-subject revision under the
        // subject's lock, and the dictionary key pins the collapse to one property of one subject.
        for (var i = changes.Length - 1; i >= 0; i--)
        {
            // By reference: a by-value copy of this struct is a 144 byte block move that the JIT emits a
            // bulk write barrier for, because the struct carries object fields.
            ref readonly var change = ref changes[i];

            // Single lookup per change: the ref is only read and written before the next add.
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_propertyIndices, change.Property, out var propertyAlreadySeen);
            if (!propertyAlreadySeen)
            {
                // The property's last arrival, which seeds the survivor with its old and new value.
                entry = (_count, i, change.Revision, change.Revision);
                _buffer[_count++] = change;
                continue;
            }

            var survivingChange = _buffer[entry.Index];
            if (entry.LowestRevision == 0)
            {
                // Already falling back to arrival position: every earlier arrival overwrites the old
                // value, so the first arrival's wins, and no new value is ever promoted by revision.
                _buffer[entry.Index] = change.MergeWithNewer(survivingChange);
            }
            else if (change.Revision == 0)
            {
                // A change constructed outside a terminal write, which orders against nothing, so the
                // whole property falls back to arrival position from here on. Anything a revision
                // promoted earlier in this pass has to be discarded, which is what the seed index is
                // for: it restarts the survivor from the last arrival, still live in the input span.
                // Revision dropped with it: the survivor is now assembled by arrival position, so
                // carrying the seed's revision would let a later commit supersede a value that nothing
                // else in this batch still holds.
                _buffer[entry.Index] = change.MergeWithNewer(changes[entry.SeedIndex]).WithoutRevision();
                entry.LowestRevision = 0;
            }
            else if (change.Revision < entry.LowestRevision)
            {
                // The oldest commit seen so far, which supplies the old value.
                _buffer[entry.Index] = change.MergeWithNewer(survivingChange);
                entry.LowestRevision = change.Revision;
            }
            else if (change.Revision > entry.HighestRevision)
            {
                // Committed after everything seen so far but enqueued before it, so its new value is
                // the current state.
                _buffer[entry.Index] = survivingChange.MergeWithNewer(change);
                entry.HighestRevision = change.Revision;
            }

            // Any other revision lies inside the bounds, so it is neither the baseline nor the newest
            // state and contributes nothing to the survivor.
        }

        // Reverse to restore chronological order of last occurrences
        if (_count > 1)
        {
            Array.Reverse(_buffer, 0, _count);
        }

        if (deliveryRule is { } rule && _count > 0)
        {
            SuppressSupersededChanges(rule);
        }

        if (_count > 0 && admissionHandler is not null && !admissionHandler(_count))
        {
            return ReadOnlyMemory<SubjectPropertyChange>.Empty;
        }

        return new ReadOnlyMemory<SubjectPropertyChange>(_buffer, 0, _count);
    }

    /// <summary>
    /// Drops survivors the model has already moved past, compacting what remains.
    /// </summary>
    private void SuppressSupersededChanges(ChangeDeliveryRule rule)
    {
        var kept = 0;
        for (var i = 0; i < _count; i++)
        {
            if (!ChangeDeliveryFilter.TryAcceptForDelivery(in _buffer[i], rule))
            {
                continue;
            }

            if (kept != i)
            {
                _buffer[kept] = _buffer[i];
            }

            kept++;
        }

        if (kept == _count)
        {
            return;
        }

        // The slots past the compacted prefix still hold the dropped changes, and therefore their
        // subjects and boxed values. Reset only clears [0, _count), so shrinking the count without
        // clearing here would strand those references in the pooled buffer.
        Array.Clear(_buffer, kept, _count - kept);
        _count = kept;
    }

    /// <summary>
    /// Releases the batch state after the write handler has consumed the result, invalidating the memory
    /// returned by <see cref="Merge"/>. Must be called after every batch, because it is what keeps
    /// the pooled buffer free of stale references. A no-op once <see cref="Dispose"/> has run.
    /// </summary>
    public void Reset()
    {
        if (_buffer is null)
        {
            return;
        }

        var distinctPropertyCount = _propertyIndices.Count;
        _propertyIndices.Clear();

        // Both marks are judged here and released together behind one hysteresis. They are not the same
        // measurement: the buffer compares survivors against a capacity sized by batch length, the index
        // compares distinct properties against its own, so they can disagree. Sharing the counter
        // therefore means "the flush stream has been narrow for four batches" rather than "this mark has
        // been". Nothing is starved by that: whatever is oversized when the threshold lands is released.
        // Read after the Clear: TrimExcess throws when the requested capacity is below Count, and the
        // guard does not bound Count by the floor, so trimming first would throw for exactly the wide
        // batch this exists for. Dictionary.Clear leaves Capacity alone, so the guard still sees the
        // pre-clear value.
        var indexIsOversized = _propertyIndices.Capacity >= PropertyIndexMaximumCapacity &&
                               distinctPropertyCount < _propertyIndices.Capacity / 4;
        var bufferIsOversized = _buffer.Length >= BufferMaximumSize && _count < _buffer.Length / 4;

        // Only the prefix Merge filled can hold object references (subjects, boxed values): every
        // buffer is cleared once when it is rented and every batch is released here, so the rest of the
        // array is already clear. Clearing the whole rental instead would make a small batch pay for the
        // 256 slot minimum. Before any return to the pool, so no references leave with it.
        Array.Clear(_buffer, 0, _count);

        if (indexIsOversized || bufferIsOversized)
        {
            if (++_consecutiveNarrowBatches >= NarrowBatchesBeforeTrim)
            {
                if (indexIsOversized)
                {
                    _propertyIndices.TrimExcess(PropertyIndexMinimumCapacity);
                }

                if (bufferIsOversized)
                {
                    ArrayPool<SubjectPropertyChange>.Shared.Return(_buffer);
                    _buffer = RentClearedBuffer(BufferMinimumSize);
                }

                _consecutiveNarrowBatches = 0;
            }
        }
        else
        {
            _consecutiveNarrowBatches = 0;
        }

        // No batch is held any more, and a shrink can leave a buffer shorter than the old count.
        _count = 0;
    }

    /// <summary>
    /// Clears and returns the pooled buffer, invalidating the memory returned by
    /// <see cref="Merge"/>. Idempotent, but not safe to call while a batch is in flight.
    /// </summary>
    public void Dispose()
    {
        if (_buffer is null)
        {
            return;
        }

        _propertyIndices.Clear();

        // Clear exactly the prefix Merge filled before returning the rental.
        Array.Clear(_buffer, 0, _count);
        ArrayPool<SubjectPropertyChange>.Shared.Return(_buffer);
        _buffer = null!;
        _count = 0;
    }
}
