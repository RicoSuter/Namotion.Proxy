using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

public static class SubjectChangeContextExtensions
{
    /// <summary>Attempts a source apply with admission validated at the property commit boundary.</summary>
    public static void SetValueFromSource(this PropertyReference property, object source,
        DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp, object? value, IPropertyWriteGuard guard)
    {
        using (SubjectChangeContext.WithTimestamps(changedTimestamp, receivedTimestamp))
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(source), value, guard))
            property.Metadata.SetValue?.Invoke(property.Subject, value);
    }

    /// <summary>
    /// Sets the value of the property, arming the pending origin so the resulting write carries the
    /// given <paramref name="origin"/> instead of scoping an ambient source. This is the intent-level
    /// entry point for connectors and appliers that cannot reach the internal pending-origin slot.
    /// When <paramref name="receivedTimestamp"/> is null the ambient received timestamp is preserved
    /// (see <see cref="SubjectChangeContext.WithTimestamps"/>); only a non-null value replaces it.
    /// The written value doubles as the origin's sent-value evidence; use the overload taking a
    /// separate <c>sentValue</c> when the applied value was locally transformed after the source sent it.
    /// </summary>
    /// <param name="property">The property to mutate.</param>
    /// <param name="origin">The origin to stamp on the resulting change.</param>
    /// <param name="changedTimestamp">The changed timestamp.</param>
    /// <param name="receivedTimestamp">The received timestamp, or null to preserve the ambient value.</param>
    /// <param name="value">The value to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetValueFromOrigin(
        this PropertyReference property,
        ChangeOrigin origin, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp,
        object? value) =>
        property.SetValueFromOrigin(origin, changedTimestamp, receivedTimestamp, value, value);

    /// <summary>
    /// Sets the value of the property, arming the pending origin with <paramref name="sentValue"/> as
    /// the origin's sent-value evidence while writing <paramref name="value"/>. Use this when the
    /// applied value was locally transformed after the source sent it: the survival check at the
    /// terminal write demotes the origin to <see cref="ChangeOriginKind.Local"/> when the stored value
    /// differs from <paramref name="sentValue"/>, so a locally corrected value is not echo-suppressed
    /// back to the source. Timestamp behavior matches the sibling overload.
    /// </summary>
    /// <param name="property">The property to mutate.</param>
    /// <param name="origin">The origin to stamp on the resulting change.</param>
    /// <param name="changedTimestamp">The changed timestamp.</param>
    /// <param name="receivedTimestamp">The received timestamp, or null to preserve the ambient value.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="sentValue">The value the source semantically sent, used as the origin's survival evidence.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetValueFromOrigin(
        this PropertyReference property,
        ChangeOrigin origin, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp,
        object? value, object? sentValue)
    {
        using (SubjectChangeContext.WithTimestamps(changedTimestamp, receivedTimestamp))
        using (PendingOrigin.Set(property, origin, sentValue))
        {
            property.Metadata.SetValue?.Invoke(property.Subject, value);
        }
    }

    /// <summary>
    /// Sets the value of the property from the given source, changed and received timestamp.
    /// Forwards to <see cref="SetValueFromOrigin(PropertyReference, ChangeOrigin, DateTimeOffset?, DateTimeOffset?, object?)"/>
    /// with a <see cref="ChangeOrigin.FromSource"/> origin.
    /// </summary>
    /// <param name="property">The property to mutate.</param>
    /// <param name="source">The source.</param>
    /// <param name="changedTimestamp">The changed timestamp.</param>
    /// <param name="receivedTimestamp">The received timestamp.</param>
    /// <param name="valueFromSource">The value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetValueFromSource(
        this PropertyReference property,
        object source, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp,
        object? valueFromSource) =>
        property.SetValueFromOrigin(ChangeOrigin.FromSource(source), changedTimestamp, receivedTimestamp, valueFromSource);
}