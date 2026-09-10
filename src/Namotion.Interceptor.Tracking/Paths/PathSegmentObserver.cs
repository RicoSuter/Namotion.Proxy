using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>
/// A per-position property listener. Its identity is the slot-identity token
/// (<see cref="Position"/>): the subscription matches a callback against the observer currently
/// recorded for that position to reject deliveries from a torn-down build.
/// </summary>
internal sealed class PathSegmentObserver<TValue>(SubjectPathSubscription<TValue> coordinator) : IPropertyChangeObserver
{
    public required int Position { get; init; }

    public void OnChange(in SubjectPropertyChange change) => coordinator.ProcessSegmentCallback(this, in change);
}
