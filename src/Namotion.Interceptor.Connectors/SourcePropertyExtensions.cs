using System.Diagnostics.CodeAnalysis;

using Namotion.Interceptor.Connectors.Monitoring;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Extension methods for associating properties with their external data sources.
/// </summary>
/// <remarks>
/// These extensions use the property data storage mechanism to maintain a mapping between
/// properties and their corresponding <see cref="ISubjectSource"/> instances. This association
/// is used by <see cref="SubjectSourceBase"/> derivatives to dispatch changes only to the
/// owning source, and by transactions to determine which source to write changes to during commit.
/// Each property can have at most one source (single owner model).
/// </remarks>
public static class SourcePropertyExtensions
{
    private const string SourceKey = "Namotion.Interceptor.Sources.Source";

    /// <summary>
    /// Associates a property with its external data source.
    /// Returns false if already owned by a different source.
    /// Idempotent - returns true if already owned by the same source.
    /// </summary>
    /// <param name="property">The property reference to associate with a source.</param>
    /// <param name="source">The external data source that provides and synchronizes this property's value.</param>
    /// <returns>
    /// <c>true</c> if the source was set or already owned by the same source;
    /// <c>false</c> if the property is already owned by a different source.
    /// </returns>
    public static bool SetSource(this PropertyReference property, ISubjectSource source)
    {
        // Add-if-absent, not GetOrSet: only this distinguishes a fresh claim from a re-claim.
        if (property.TryAddPropertyData(SourceKey, source))
        {
            PublishOwnershipChange(property, source, SourceEventKind.PropertyClaimed);
            return true;
        }

        // Check-then-act: the result reflects ownership at this read, not a lasting guarantee.
        return property.TryGetPropertyData(SourceKey, out var existing) && ReferenceEquals(existing, source);
    }

    /// <summary>
    /// Gets the external data source associated with a property, if any.
    /// </summary>
    /// <param name="property">The property reference to query.</param>
    /// <param name="source">
    /// When this method returns, contains the associated source if found; otherwise, <c>null</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> if the property has an associated source; otherwise, <c>false</c>.
    /// </returns>
    public static bool TryGetSource(this PropertyReference property, [NotNullWhen(true)] out ISubjectSource? source)
    {
        if (property.TryGetPropertyData(SourceKey, out var data) && data is ISubjectSource s)
        {
            source = s;
            return true;
        }
        source = null;
        return false;
    }

    /// <summary>
    /// Removes the source association from a property, but only if the current source matches the expected source.
    /// This prevents accidentally removing another source's ownership.
    /// </summary>
    /// <param name="property">The property reference to disassociate from its source.</param>
    /// <param name="expectedSource">The source that should currently own this property.</param>
    /// <returns><c>true</c> if the source was removed; <c>false</c> if the property had no source or a different source.</returns>
    public static bool RemoveSource(this PropertyReference property, ISubjectSource expectedSource)
    {
        if (!property.TryRemovePropertyData(SourceKey, expectedSource))
        {
            return false;
        }

        PublishOwnershipChange(property, expectedSource, SourceEventKind.PropertyReleased);
        return true;
    }

    private static void PublishOwnershipChange(
        PropertyReference property, ISubjectSource source, SourceEventKind kind)
    {
        // Usually length 0 or 1 and cached on the context's copy-on-write state snapshot, so a tree
        // without monitoring pays one array check per claim and nothing else.
        var monitors = property.Subject.Context.GetSourceMonitors();
        if (monitors.IsEmpty)
        {
            return;
        }

        // With no subscribers this skips the clock, the event and the monitor's lock entirely,
        // which is the common shape. The event is identical per monitor, so build it at most once.
        SourceEvent? sourceEvent = null;
        foreach (var monitor in monitors)
        {
            if (!monitor.HasSubscribers)
            {
                continue;
            }

            sourceEvent ??= kind == SourceEventKind.PropertyClaimed
                ? new SourceEvent(kind, source, property, SourceState.Unclaimed, source.State, DateTimeOffset.UtcNow)
                : new SourceEvent(kind, source, property, source.State, SourceState.Unclaimed, DateTimeOffset.UtcNow);

            monitor.PublishUnderLock(sourceEvent.Value);
        }
    }
}
