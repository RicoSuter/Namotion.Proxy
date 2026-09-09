using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Consolidated per-property data for derived property tracking.
/// Stored once per (subject, property) in Subject.Data under a short key
/// to minimize dictionary lookups (one lookup instead of separate lookups
/// for UsedByProperties, RequiredProperties, and LastKnownValue).
/// </summary>
internal sealed class DerivedPropertyData
{
    /// <summary>
    /// Dependencies: Which properties this derived property depends on.
    /// Always read/written under lock(this) — no volatile or CAS needed.
    /// Null until first recalculation; buffer may be larger than count for reuse.
    /// </summary>
    private PropertyReference[]? _requiredProperties;

    /// <summary>
    /// Number of valid entries in _requiredProperties (may be less than array length).
    /// Always read/written under lock(this).
    /// </summary>
    private int _requiredPropertyCount;

    /// <summary>
    /// Used-by properties: Which derived properties depend on this property.
    /// Initialized lazily via Interlocked.CompareExchange for thread safety.
    /// </summary>
    private PropertyReferenceCollection? _usedByProperties;

    /// <summary>
    /// Cached last known value for change detection.
    /// Only used for derived properties.
    /// </summary>
    internal object? LastKnownValue;

    /// <summary>
    /// Reentrancy guard for RecalculateDerivedProperty.
    /// Prevents infinite recursion when a derived-with-setter property's
    /// SetPropertyValueWithInterception re-enters WriteProperty.
    /// Only read/written inside lock(this), so no volatile needed.
    /// </summary>
    internal bool IsRecalculating;

    /// <summary>
    /// Sequence counter incremented under lock(this) each time RecalculateDerivedProperty
    /// computes a new value. Read via Volatile.Read outside the lock to detect stale
    /// notifications — if the sequence has advanced, a newer recalculation is already completed
    /// and the current notification should be skipped.
    /// </summary>
    internal long RecalculationSequence;

    /// <summary>
    /// Set under lock(this) when a state change occurs while IsRecalculating is true:
    /// concurrent RecalculateDerivedProperty (bails), DetachProperty, or AttachProperty.
    /// Checked by RecalculateDerivedProperty before committing — if set, the evaluation
    /// result is discarded and the getter is re-evaluated with a fresh state.
    /// </summary>
    internal bool RecalculationNeeded;

    /// <summary>
    /// Lifecycle flag cleared during DetachProperty under lock(this).
    /// Checked by RecalculateDerivedProperty to prevent zombie used-by property resurrection.
    /// Set by AttachProperty to support re-attachment.
    /// Defaults to true because properties are assumed to be attached until explicitly detached.
    /// </summary>
    internal bool IsAttached = true;

    /// <summary>
    /// True if this data belongs to a derived property (vs. a source property whose data exists only
    /// because a derived depends on it). Lets WriteProperty identify derived-with-setter writes
    /// without touching metadata on the hot path.
    /// </summary>
    /// <remarks>
    /// Written via <c>Volatile.Write</c> in AttachProperty, read via <c>Volatile.Read</c> in
    /// WriteProperty. Write-once; never reset (metadata.IsDerived is immutable).
    /// </remarks>
    internal bool IsDerived;

    /// <summary>
    /// Read-only access to used-by properties for public API.
    /// </summary>
    internal PropertyReferenceCollection? UsedByDependencies
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _usedByProperties);
    }

    /// <summary>
    /// Returns the current dependencies as a read-only span.
    /// Returns an empty span if no dependencies exist (allocation-free).
    /// Safe for lock-free reads (clamps count to array length to handle torn reads).
    /// </summary>
    internal ReadOnlySpan<PropertyReference> RequiredPropertiesSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var array = _requiredProperties;
            if (array is null)
            {
                return ReadOnlySpan<PropertyReference>.Empty;
            }

            return array.AsSpan(0, Math.Min(_requiredPropertyCount, array.Length));
        }
    }

    /// <summary>
    /// Returns used-by properties as a read-only span (allocation-free).
    /// Performs a single Volatile.Read of the collection and returns its Items span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<PropertyReference> GetUsedByProperties()
    {
        var usedBy = Volatile.Read(ref _usedByProperties);
        if (usedBy is null)
        {
            return ReadOnlySpan<PropertyReference>.Empty;
        }

        return usedBy.Items;
    }

    /// <summary>
    /// Detaches this property: sets IsAttached=false, cleans dependencies (if derived),
    /// snapshots, and clears used-by properties. Called under lock(this).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PropertyReference[] DetachAndSnapshotUsedBy(in PropertyReference property)
    {
        IsAttached = false;

        // Case 1 (derived only): remove this property from each dependency's UsedByProperties.
        if (property.Metadata.IsDerived)
        {
            var requiredProperties = RequiredPropertiesSpan;
            for (var i = 0; i < requiredProperties.Length; i++)
            {
                requiredProperties[i]
                    .TryGetDerivedPropertyData()?
                    .RemoveUsedByProperty(property);
            }

            ClearRequiredProperties();
            LastKnownValue = null;
        }

        // Case 2: snapshot used-by properties, then clear. Snapshot is stable (copy-on-write).
        var snapshot = _usedByProperties?.ItemsArray ?? [];
        _usedByProperties = null;
        return snapshot;
    }

    /// <summary>
    /// Updates dependencies (RequiredProperties) and used-by properties (UsedByProperties).
    /// Called under lock(this). Returns true if dependency set changed (caller should re-evaluate).
    /// </summary>
    internal bool UpdateDependencies(
        in PropertyReference derivedProperty,
        ReadOnlySpan<PropertyReference> recordedDependencies,
        DerivedPropertyRecorder recorder)
    {
        var previousRequiredProperties = RequiredPropertiesSpan;

        // Fast path: deps unchanged — no allocation.
        if (previousRequiredProperties.SequenceEqual(recordedDependencies))
        {
            recorder.ClearLastRecording();
            return false;
        }

        RemoveStaleUsedByProperties(derivedProperty, previousRequiredProperties, recordedDependencies);

        if (TryAddNewUsedByProperties(derivedProperty, previousRequiredProperties, recordedDependencies))
        {
            SetRequiredProperties(recordedDependencies);
            recorder.ClearLastRecording();
            return true;
        }

        // Rare: a dependency was detaching concurrently — reconcile under locks.
        return ReconcileSkippedDependencies(
            derivedProperty, previousRequiredProperties, recordedDependencies, recorder);
    }

    /// <summary>
    /// Removes used-by properties for dependencies that are no longer in the recorded set.
    /// Called under lock(this). Both spans must be valid (array not yet modified, recorder not yet cleared).
    /// </summary>
    private static void RemoveStaleUsedByProperties(
        in PropertyReference derivedProperty,
        ReadOnlySpan<PropertyReference> previousRequiredProperties,
        ReadOnlySpan<PropertyReference> recordedDependencies)
    {
        foreach (ref readonly var oldDependency in previousRequiredProperties)
        {
            if (!recordedDependencies.Contains(oldDependency))
            {
                oldDependency.TryGetDerivedPropertyData()?.RemoveUsedByProperty(derivedProperty);
            }
        }
    }

    /// <summary>
    /// Adds used-by properties for newly recorded dependencies. Locks each dependency's data
    /// to serialize with DetachProperty's IsAttached check.
    /// Returns true if all used-by properties were added; false if any were skipped (concurrent detach).
    /// </summary>
    private static bool TryAddNewUsedByProperties(
        in PropertyReference derivedProperty,
        ReadOnlySpan<PropertyReference> previousRequiredProperties,
        ReadOnlySpan<PropertyReference> recordedDependencies)
    {
        var allAdded = true;
        foreach (ref readonly var newDependency in recordedDependencies)
        {
            if (!previousRequiredProperties.Contains(newDependency))
            {
                var dependencyData = newDependency.GetDerivedPropertyData();
                lock (dependencyData)
                {
                    if (dependencyData.IsAttached)
                    {
                        dependencyData.AddUsedByProperty(derivedProperty);
                    }
                    else
                    {
                        allAdded = false;
                    }
                }
            }
        }

        return allAdded;
    }

    /// <summary>
    /// Handles the rare case where a dependency was detaching concurrently during used-by property registration.
    /// Re-checks each dependency under lock, filters out still-detached deps, updates RequiredProperties.
    /// Called under lock(this).
    /// </summary>
    private bool ReconcileSkippedDependencies(
        in PropertyReference derivedProperty,
        ReadOnlySpan<PropertyReference> previousRequiredProperties,
        ReadOnlySpan<PropertyReference> recordedDependencies,
        DerivedPropertyRecorder recorder)
    {
        // Allocate owned copy to preserve previousRequiredProperties span for the final SequenceEqual check.
        var newItems = recordedDependencies.ToArray();
        recorder.ClearLastRecording();

        var liveCount = 0;
        for (var i = 0; i < newItems.Length; i++)
        {
            var dependencyData = newItems[i].TryGetDerivedPropertyData();
            if (dependencyData is null)
            {
                newItems[liveCount++] = newItems[i];
                continue;
            }

            lock (dependencyData)
            {
                if (dependencyData.IsAttached)
                {
                    dependencyData.AddUsedByProperty(derivedProperty);
                    newItems[liveCount++] = newItems[i];
                }
                else
                {
                    dependencyData.RemoveUsedByProperty(derivedProperty);
                }
            }
        }

        SetRequiredProperties(newItems.AsSpan(0, liveCount));

        // If a filtered result matches previous deps, nothing effectively changed.
        // Return false to prevent infinite stabilization loop.
        // the previousRequiredProperties span is still valid (points to old array, replaced above).
        return !RequiredPropertiesSpan.SequenceEqual(previousRequiredProperties);
    }

    /// <summary>
    /// Updates dependencies using buffer reuse when possible.
    /// Reuses existing array if capacity is sufficient; allocates only when growing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetRequiredProperties(ReadOnlySpan<PropertyReference> properties)
    {
        var newCount = properties.Length;
        var currentArray = _requiredProperties;
        if (currentArray is not null && currentArray.Length >= newCount)
        {
            // Reuse existing buffer — zero allocation.
            properties.CopyTo(currentArray);
            if (currentArray.Length > newCount)
            {
                currentArray.AsSpan(newCount).Clear();
            }
        }
        else
        {
            // Must grow: allocate new array.
            _requiredProperties = properties.ToArray();
        }

        _requiredPropertyCount = newCount;
    }

    /// <summary>
    /// Clears all dependencies.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearRequiredProperties()
    {
        _requiredProperties = null;
        _requiredPropertyCount = 0;
    }

    /// <summary>
    /// Removes a dependency using swap-remove (no array allocation).
    /// Nulls out array when the last item is removed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool RemoveRequiredProperty(in PropertyReference property)
    {
        var required = _requiredProperties;
        var requiredCount = _requiredPropertyCount;
        if (required is null || requiredCount == 0)
        {
            return false;
        }

        var index = Array.IndexOf(required, property, 0, requiredCount);
        if (index < 0)
        {
            return false;
        }

        // Swap-remove: move the last item into the removed slot, decrement count.
        // No array allocation. Safe under lock.
        var lastIndex = requiredCount - 1;
        if (index < lastIndex)
        {
            required[index] = required[lastIndex];
        }

        required[lastIndex] = default;
        _requiredPropertyCount = lastIndex;

        if (lastIndex == 0)
        {
            _requiredProperties = null;
        }

        return true;
    }

    /// <summary>
    /// Adds a used-by property. Creates PropertyReferenceCollection on first call
    /// with the item pre-populated, avoiding a separate CAS Add allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddUsedByProperty(in PropertyReference dependentProperty)
    {
        var usedByProperty = Volatile.Read(ref _usedByProperties);
        if (usedByProperty is not null)
        {
            usedByProperty.Add(dependentProperty);
            return;
        }

        // First used-by property: Create collection with item already included (one allocation instead of two).
        var created = new PropertyReferenceCollection(dependentProperty);
        var existing = Interlocked.CompareExchange(ref _usedByProperties, created, null);

        // Another thread created it first, add to theirs instead.
        existing?.Add(dependentProperty);
    }

    /// <summary>
    /// Removes a used-by property if the collection exists.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RemoveUsedByProperty(in PropertyReference dependent)
    {
        _usedByProperties?.Remove(dependent);
    }
}
