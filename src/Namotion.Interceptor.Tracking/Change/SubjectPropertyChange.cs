using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Change.Performance;

namespace Namotion.Interceptor.Tracking.Change;

public readonly struct SubjectPropertyChange : IEquatable<SubjectPropertyChange>
{
    // Discriminated union: either inline storage OR boxed holder (per value)
    private readonly InlineValueStorage _oldValueStorage;
    private readonly InlineValueStorage _newValueStorage;
    private readonly object? _oldBoxedHolder; // IBoxedValueHolder or null
    private readonly object? _newBoxedHolder; // IBoxedValueHolder or null

    private SubjectPropertyChange(
        PropertyReference property,
        ChangeOrigin origin,
        DateTimeOffset changedTimestamp,
        DateTimeOffset? receivedTimestamp,
        InlineValueStorage oldValueStorage,
        InlineValueStorage newValueStorage,
        object? oldBoxedHolder,
        object? newBoxedHolder,
        long revision)
    {
        Property = property;
        Origin = origin;
        ChangedTimestamp = changedTimestamp;
        ReceivedTimestamp = receivedTimestamp;
        _oldValueStorage = oldValueStorage;
        _newValueStorage = newValueStorage;
        _oldBoxedHolder = oldBoxedHolder;
        _newBoxedHolder = newBoxedHolder;
        Revision = revision;
    }

    public PropertyReference Property { get; }

    public ChangeOrigin Origin { get; }

    public DateTimeOffset ChangedTimestamp { get; }

    public DateTimeOffset? ReceivedTimestamp { get; }

    /// <summary>
    /// The writing subject's commit revision: monotonic per subject over committed writes, so two
    /// changes to the same subject are ordered by comparing it. Revisions of different subjects are
    /// NOT comparable. 0 means the change was constructed outside a terminal write.
    /// </summary>
    public long Revision { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectPropertyChange Create<TValue>(
        PropertyReference property,
        ChangeOrigin origin,
        DateTimeOffset changedTimestamp,
        DateTimeOffset? receivedTimestamp,
        TValue oldValue,
        TValue newValue,
        long revision = 0)
    {
        // Fast path: small ref-free value types stored inline - zero allocations. Structs containing
        // references (e.g. ImmutableArray<T>) must be excluded: inline storage is not GC-scanned, so a
        // contained reference could dangle. All checks fold to JIT-time constants (zero-cost guard).
        if (typeof(TValue).IsValueType &&
            !RuntimeHelpers.IsReferenceOrContainsReferences<TValue>() &&
            Unsafe.SizeOf<TValue>() <= InlineValueStorage.MaxSize)
        {
            return new SubjectPropertyChange(
                property,
                origin,
                changedTimestamp,
                receivedTimestamp,
                InlineValueStorage.Create(oldValue),
                InlineValueStorage.Create(newValue),
                null,
                null,
                revision);
        }

        // Fast path: strings - store directly without wrapper (ZERO allocations)
        if (typeof(TValue) == typeof(string))
        {
            return new SubjectPropertyChange(
                property,
                origin,
                changedTimestamp,
                receivedTimestamp,
                default,
                default,
                oldValue,
                newValue,
                revision);
        }

        // Slow path: other reference types or large value types - TWO allocations (one per value)
        return new SubjectPropertyChange(
            property,
            origin,
            changedTimestamp,
            receivedTimestamp,
            default,
            default,
            new BoxedValueHolder<TValue>(oldValue),
            new BoxedValueHolder<TValue>(newValue),
            revision);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetOldValue<TValue>() =>
        TryGetValue(_oldValueStorage, _oldBoxedHolder, out TValue value)
            ? value
            : throw new InvalidCastException($"Old value of property '{Property.Name}' is of type '{_oldValueStorage.StoredType?.FullName ?? _oldBoxedHolder?.GetType().FullName ?? "null"}' and cannot be cast to '{typeof(TValue).FullName}'.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetNewValue<TValue>() =>
        TryGetValue(_newValueStorage, _newBoxedHolder, out TValue value)
            ? value
            : throw new InvalidCastException($"New value of property '{Property.Name}' is of type '{_newValueStorage.StoredType?.FullName ?? _newBoxedHolder?.GetType().FullName ?? "null"}' and cannot be cast to '{typeof(TValue).FullName}'.");

    /// <summary>
    /// Reads the property's value now, rather than the value captured when this change was created.
    /// Deliveries can arrive out of commit order under concurrent writes to the same property, so a
    /// consumer maintaining a derived view must use this instead of <see cref="GetNewValue{TValue}"/>,
    /// which describes one commit and can be superseded by the time it is delivered.
    /// </summary>
    /// <exception cref="InvalidCastException">The current value is not assignable to <typeparamref name="TValue"/>.</exception>
    public TValue GetCurrentValue<TValue>()
    {
        var value = Property.Metadata.GetValue?.Invoke(Property.Subject);
        if (value is TValue typed)
        {
            return typed;
        }

        if (value is null)
        {
            if (default(TValue) is null)
            {
                return default!;
            }

            throw new InvalidCastException(
                $"Current value of property '{Property.Name}' is null and cannot be cast to non-nullable '{typeof(TValue).FullName}'.");
        }

        throw new InvalidCastException(
            $"Current value of property '{Property.Name}' is of type '{value.GetType().FullName}' " +
            $"and cannot be cast to '{typeof(TValue).FullName}'.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetOldValue<TValue>(out TValue value) =>
        TryGetValue(_oldValueStorage, _oldBoxedHolder, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetNewValue<TValue>(out TValue value) =>
        TryGetValue(_newValueStorage, _newBoxedHolder, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetValue<TValue>(InlineValueStorage storage, object? boxedHolder, out TValue value)
    {
        // Fast path: inline storage (zero allocation retrieval)
        if (boxedHolder == null)
        {
            if (storage.TryGetValue(out value))
            {
                return true;
            }

            // Support casting to object (will box the value)
            if (typeof(TValue) == typeof(object))
            {
                // If no inline storage was used, this was a null string/reference
                if (storage.StoredType == null)
                {
                    value = default!;
                    return true;
                }
                value = (TValue)storage.GetValueBoxed()!;
                return true;
            }

            // Handle null strings: boxedHolder is null AND no inline storage was used
            if (typeof(TValue) == typeof(string) && storage.StoredType == null)
            {
                value = default!;
                return true;
            }

            value = default!;
            return false;
        }

        // Fast path: direct string retrieval (strings stored without wrapper)
        if (typeof(TValue) == typeof(string) && boxedHolder is string)
        {
            value = (TValue)boxedHolder;
            return true;
        }

        // Fast path: boxed holder with interface dispatch (no reflection)
        if (boxedHolder is IBoxedValueHolder holder)
        {
            // Fast path: try direct type match first
            if (holder.TryGetValue(out value))
            {
                return true;
            }

            // Fallback: box for object cast (supports custom structs)
            if (typeof(TValue) == typeof(object))
            {
                value = (TValue)holder.GetValueBoxed()!;
                return true;
            }
        }

        // Support casting stored strings to object
        if (typeof(TValue) == typeof(object) && boxedHolder is string)
        {
            value = (TValue)boxedHolder;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Merges this (earlier) change with a newer change to the same property.
    /// Keeps this change's old value and takes the newer change's new value,
    /// origin, and timestamps. Used during flush merging to preserve the correct
    /// diff baseline while reflecting the latest state. Copies the newer change's full
    /// <see cref="Origin"/> so a merged change keeps its kind and source. The newer change's
    /// <see cref="Revision"/> carries over as well, so a merged survivor stays comparable against
    /// further changes to the same subject.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SubjectPropertyChange MergeWithNewer(SubjectPropertyChange newerChange)
    {
        return new SubjectPropertyChange(
            Property,
            newerChange.Origin,
            newerChange.ChangedTimestamp,
            newerChange.ReceivedTimestamp,
            _oldValueStorage,
            newerChange._newValueStorage,
            _oldBoxedHolder,
            newerChange._newBoxedHolder,
            newerChange.Revision);
    }

    /// <summary>
    /// Copies this change carrying no revision, without re-boxing the values. A batch collapse uses this
    /// when it falls back to arrival position: the survivor is then chosen by arrival rather than by
    /// revision, so ranking a later commit against the revision it happens to carry could drop the very
    /// value the fallback selected, with nothing left in the batch still holding it.
    /// </summary>
    /// <remarks>
    /// For anyone collapsing a batch themselves rather than through the built-in processor. Use it
    /// whenever arrival order, not commit order, decided which change survived.
    /// <para>
    /// Scope it to changes on their way out to a sink. There a change carrying no revision is never
    /// dropped as superseded, so using it where it was not needed costs a redundant delivery rather than
    /// a value. Do <b>not</b> use it on a change that will be parked and later re-applied locally, for
    /// example into a retry queue: the same supersession check is what stops an older parked write from
    /// being restored over a newer local one, and a missing revision makes that check pass
    /// unconditionally, which loses the newer write instead.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SubjectPropertyChange WithoutRevision() =>
        new(Property,
            Origin,
            ChangedTimestamp,
            ReceivedTimestamp,
            _oldValueStorage,
            _newValueStorage,
            _oldBoxedHolder,
            _newBoxedHolder,
            revision: 0);

    /// <summary>
    /// Copies this change with a different origin, without re-boxing the values. A transaction writer
    /// uses this to mark an accepted change with the origin that confirmed it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SubjectPropertyChange WithOrigin(ChangeOrigin origin) =>
        new(Property,
            origin,
            ChangedTimestamp,
            ReceivedTimestamp,
            _oldValueStorage,
            _newValueStorage,
            _oldBoxedHolder,
            _newBoxedHolder,
            Revision);

    /// <summary>
    /// Copies this change with its old and new values swapped, without re-boxing them, so applying the
    /// result undoes this change. Values keep the type they were stored with, so typed reads behave as
    /// they do on this change.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="WithOrigin"/> and <see cref="MergeWithNewer"/>, the result carries no
    /// <see cref="Revision"/>: it is a write to perform, not a commit that happened. Applying it commits
    /// with a revision of its own.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SubjectPropertyChange ToRollbackChange() =>
        new(Property,
            Origin,
            ChangedTimestamp,
            ReceivedTimestamp,
            _newValueStorage,
            _oldValueStorage,
            _newBoxedHolder,
            _oldBoxedHolder,
            revision: 0);

    /// <summary>
    /// Equality based on PropertyReference only for efficient HashSet/Dictionary usage.
    /// </summary>
    public bool Equals(SubjectPropertyChange other) => Property.Equals(other.Property);

    public override bool Equals(object? obj) => obj is SubjectPropertyChange other && Equals(other);

    public override int GetHashCode() => Property.GetHashCode();

    public static bool operator ==(SubjectPropertyChange left, SubjectPropertyChange right) => left.Equals(right);

    public static bool operator !=(SubjectPropertyChange left, SubjectPropertyChange right) => !left.Equals(right);
}
