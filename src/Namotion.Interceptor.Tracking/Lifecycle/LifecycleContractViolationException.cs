namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Thrown for a lifecycle contract violation, such as nested topology work in another context,
/// or when waiting for the topology gate exceeds a contention threshold.
/// </summary>
/// <remarks>
/// Its own type matters. <see cref="Change.DerivedPropertyChangeHandler"/> absorbs exceptions from derived
/// getters on purpose, keeping the last known value and recomputing on the next dependency write,
/// and a contract violation must not disappear into that. Both absorbing handlers filter on this
/// type, so a violation propagates while an ordinary getter fault still degrades gracefully. It
/// derives from <see cref="InvalidOperationException"/> so existing consumer catches keep working.
/// </remarks>
public sealed class LifecycleContractViolationException : InvalidOperationException
{
    public LifecycleContractViolationException(string message)
        : base(message)
    {
    }
}
