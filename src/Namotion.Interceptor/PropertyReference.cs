using System.Runtime.CompilerServices;

namespace Namotion.Interceptor;

public readonly struct PropertyReference : IEquatable<PropertyReference>
{
    public static readonly PropertyReferenceComparer Comparer = new();

    public PropertyReference(IInterceptorSubject subject, string name)
    {
        Subject = subject;
        Name = name;
    }

    public IInterceptorSubject Subject { get; }

    public string Name { get; }

    /// <summary>
    /// Looks up the property metadata in the subject's property table on each access; the result is not
    /// cached, because PropertyReference is a value type copied throughout the codebase and an embedded
    /// cache would bloat every copy and force the struct to be mutable (see the readonly-struct
    /// declaration). The lookup is cheap, but hoist it to a local if you read it more than once in a hot path.
    /// </summary>
    public SubjectPropertyMetadata Metadata =>
        Subject.Properties.TryGetValue(Name, out var metadata) ? metadata :
            throw new InvalidOperationException(
                $"No metadata found for property '{Name}' on {Subject.GetType().Name}. " +
                $"Available properties ({Subject.Properties.Count}): [{string.Join(", ", Subject.Properties.Keys)}]");

    public void SetPropertyData(string key, object? value)
    {
        Subject.Data[(Name, key)] = value;
    }

    public void RemovePropertyData(string key)
    {
        Subject.Data.TryRemove((Name, key), out _);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetPropertyData(string key, out object? value)
    {
        return Subject.Data.TryGetValue((Name, key), out value);
    }

    /// <summary>
    /// Gets an existing value for the specified key, or adds the value if the key doesn't exist.
    /// This operation is atomic and thread-safe.
    /// </summary>
    /// <param name="key">The key to look up or add.</param>
    /// <param name="value">The value to add if the key doesn't exist.</param>
    /// <returns>The existing value if found, or the newly added value.</returns>
    public object? GetOrSetPropertyData(string key, object? value)
    {
        return Subject.Data.GetOrAdd((Name, key), value);
    }

    /// <summary>
    /// Adds the property data for the specified key only if the key is not already present.
    /// This operation is atomic and thread-safe, and is the add-if-absent counterpart to
    /// <see cref="TryRemovePropertyData"/>. Use it when the caller must distinguish a first
    /// write from a subsequent one, which <see cref="GetOrSetPropertyData"/> cannot express.
    /// </summary>
    /// <param name="key">The key to add.</param>
    /// <param name="value">The value to store when the key is absent.</param>
    /// <returns><c>true</c> if the value was stored; <c>false</c> if a value was already present, which is left untouched.</returns>
    public bool TryAddPropertyData(string key, object? value)
    {
        return Subject.Data.TryAdd((Name, key), value);
    }

    /// <summary>
    /// Removes the property data for the specified key only if it matches the expected value.
    /// This operation is atomic and thread-safe.
    /// </summary>
    /// <param name="key">The key to remove.</param>
    /// <param name="expectedValue">The value that must match for removal to succeed.</param>
    /// <returns><c>true</c> if the key-value pair was removed; <c>false</c> if the key didn't exist or the value didn't match.</returns>
    public bool TryRemovePropertyData(string key, object? expectedValue)
    {
        return ((ICollection<KeyValuePair<(string?, string), object?>>)Subject.Data)
            .Remove(new KeyValuePair<(string?, string), object?>((Name, key), expectedValue));
    }

    // Short deliberately: hashed on every delivered change, and string hash codes are not cached.
    private const string WriteStateKey = "ni.wstate";

    /// <summary>
    /// Gets the write timestamp, or null if no timestamp has been set.
    /// </summary>
    public DateTimeOffset? TryGetWriteTimestamp()
    {
        if (TryGetWriteState(out var state))
        {
            var ticks = Interlocked.Read(ref state.TimestampTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }

        return null;
    }

    /// <summary>
    /// Gets the revision of the last write to this property that reached a write terminal, and whether any
    /// sink has published its value. Returns false when no write state has been recorded at all, which is
    /// not the same as never written: marking a property published records state for it too, so a
    /// never-written property can return true with a commit revision of 0.
    /// </summary>
    /// <param name="includeSourceCommitsInRevision">Whether <paramref name="commitRevision"/> also counts
    /// commits applied from a source. It governs that value alone; <paramref name="publishedToAnySource"/>
    /// is independent of it.
    /// <para>
    /// Pass true only where an applied value has provably already reached its destination by the time it
    /// is applied, which holds for a server serving the store its clients write into. It does not hold for
    /// anything reached over a wire, whose value was produced before it saw our write and therefore cannot
    /// rank against our commits. Both mistakes are silent and permanent: true where it does not hold drops
    /// local writes that nothing then redelivers, false where it does hold keeps serving a value the model
    /// has moved past.
    /// </para></param>
    /// <param name="commitRevision">The revision of the last qualifying commit, or 0 if there is none.</param>
    /// <param name="publishedToAnySource">Whether <em>some</em> sink has published this property's value.
    /// Deliberately not per source, so a connector cannot read this as "I published it".</param>
    /// <remarks>
    /// A revision is only comparable against a change to the same property: revisions are per subject,
    /// and two properties of one subject draw from the same counter. The caller states which commits
    /// count rather than receiving both markers, so the wrong one cannot be selected by argument
    /// position, and only the slot that is needed is read.
    /// <para>
    /// The revision and the published flag are returned together despite being unrelated because the
    /// delivery filter needs both per delivered change, and splitting them would hash the property key
    /// twice on that path. Callers needing only one may ignore the other.
    /// </para>
    /// </remarks>
    public bool TryGetWriteState(bool includeSourceCommitsInRevision, out long commitRevision, out bool publishedToAnySource)
    {
        if (TryGetWriteState(out var state))
        {
            var nonSourceCommitRevision = Interlocked.Read(ref state.LastNonSourceCommitRevision);

            // Each commit advances exactly one of the two, so the last of any kind is their maximum, and
            // the source slot is read only when it can count. A stale read of either can only lower the
            // result, which delivers a redundant change rather than dropping a live one.
            commitRevision = includeSourceCommitsInRevision
                ? Math.Max(nonSourceCommitRevision, Interlocked.Read(ref state.LastSourceCommitRevision))
                : nonSourceCommitRevision;

            publishedToAnySource = state.PublishedToAnySource;
            return true;
        }

        commitRevision = 0;
        publishedToAnySource = false;
        return false;
    }

    /// <summary>
    /// Records that the calling sink has published this property's value to its source. One-way: the flag
    /// is never cleared, so calling this again on the same property has no effect.
    /// </summary>
    /// <remarks>
    /// Takes no source, and the state it sets is not per source: it is read back as
    /// <c>publishedToAnySource</c> from <see cref="TryGetWriteState(bool, out long, out bool)"/>, which
    /// every sink sees. That is the whole design rather than a simplification. It decides only whether a
    /// transaction confirmation is written back, and a confirmation carries the current value, so another
    /// sink's mark costs one redundant write rather than a wrong value. Keeping it a bare flag is what
    /// lets it hold no source reference to release when a subject detaches.
    /// </remarks>
    public void MarkAsPublishedToSource()
    {
        GetOrAddWriteState().PublishedToAnySource = true;
    }

    /// <summary>
    /// Records what the terminal just committed: the write timestamp and the commit revision, the first
    /// as raw UTC ticks; see <see cref="PropertyWriteState.TimestampTicks"/>. The revision goes to whichever
    /// of the two slots the origin selects, never both, so a commit costs one revision store on top of the
    /// timestamp store rather than two; see <see cref="PropertyWriteState.LastSourceCommitRevision"/>. The
    /// timestamp is recorded either way, preserving what <see cref="TryGetWriteTimestamp"/> reports.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetWriteState(long timestamp, long revision, bool isFromSource)
    {
        var state = GetOrAddWriteState();
        Interlocked.Exchange(ref state.TimestampTicks, timestamp);

        if (isFromSource)
        {
            Interlocked.Exchange(ref state.LastSourceCommitRevision, revision);
        }
        else
        {
            Interlocked.Exchange(ref state.LastNonSourceCommitRevision, revision);
        }
    }

    /// <summary>
    /// Sets the write timestamp alone, for the paths that produce a change without committing a write
    /// through a terminal and therefore have no revision to stamp.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetWriteTimestamp(long timestamp)
    {
        Interlocked.Exchange(ref GetOrAddWriteState().TimestampTicks, timestamp);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetWriteState(out PropertyWriteState state)
    {
        if (Subject.Data.TryGetValue((Name, WriteStateKey), out var value) && value is PropertyWriteState existing)
        {
            state = existing;
            return true;
        }

        state = null!;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PropertyWriteState GetOrAddWriteState()
    {
        return (PropertyWriteState)Subject.Data.GetOrAdd((Name, WriteStateKey), static _ => new PropertyWriteState())!;
    }

    #region Equality

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(PropertyReference other)
    {
        return Comparer.Equals(this, other);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? obj)
    {
        return obj is PropertyReference other && Comparer.Equals(this, other);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        return Comparer.GetHashCode(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(PropertyReference left, PropertyReference right)
    {
        return left.Equals(right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(PropertyReference left, PropertyReference right)
    {
        return !left.Equals(right);
    }

    public sealed class PropertyReferenceComparer : IEqualityComparer<PropertyReference>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(PropertyReference x, PropertyReference y)
        {
            return ReferenceEquals(x.Subject, y.Subject) && string.Equals(x.Name, y.Name, StringComparison.Ordinal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetHashCode(PropertyReference obj)
        {
            var subject = obj.Subject;
            var name = obj.Name;
            // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            var h1 = subject is null ? 0 : RuntimeHelpers.GetHashCode(subject);
            var h2 = name is null ? 0 : StringComparer.Ordinal.GetHashCode(name);
            // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            return (h1 * 397) ^ h2;
        }
    }    

    #endregion
}