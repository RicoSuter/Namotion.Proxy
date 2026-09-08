namespace Namotion.Interceptor.Connectors.Monitoring;

/// <summary>The kind of source metadata change a <see cref="SourceEvent"/> reports.</summary>
public enum SourceEventKind
{
    /// <summary>A source registered with the monitor, which happens when it starts.</summary>
    SourceRegistered,

    /// <summary>A source unregistered, which happens when it is disposed.</summary>
    SourceUnregistered,

    /// <summary>A source's own state changed.</summary>
    StateChanged,

    /// <summary>A source took ownership of a property.</summary>
    PropertyClaimed,

    /// <summary>A source gave up ownership of a property.</summary>
    PropertyReleased
}

/// <summary>
/// A change to source metadata: registration, state, or ownership.
/// </summary>
/// <remarks>
/// Events for one property can be enqueued out of order, because the ownership compare-and-set and
/// the enqueue are not atomic, so <see cref="OldState"/> and <see cref="NewState"/> describe one
/// transition and must not be applied blindly. Use <see cref="CurrentState"/>.
/// </remarks>
public readonly record struct SourceEvent(
    SourceEventKind Kind,
    ISubjectSource Source,
    PropertyReference? Property,
    SourceState OldState,
    SourceState NewState,
    DateTimeOffset Timestamp)
{
    /// <summary>
    /// The authoritative state for this event's subject, resolved fresh rather than the value
    /// captured when the event was created. This is what a consumer maintaining a derived view
    /// should apply.
    /// </summary>
    /// <remarks>
    /// For <see cref="SourceEventKind.StateChanged"/> this is the source's state, not any one
    /// property's. Reports ownership only, never graph membership: ask
    /// <c>ISubjectRegistry.TryGetRegisteredSubject</c> for that. Not cached.
    /// </remarks>
    public SourceState CurrentState =>
        Property is null ? Source.State : Property.Value.GetSourceState();
}
