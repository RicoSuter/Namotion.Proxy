using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>Runs a delegate on every lifecycle change, for tests that react from inside a callback.</summary>
internal sealed class DelegateLifecycleHandler(Action<SubjectLifecycleChange> onChange) : ILifecycleHandler
{
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        onChange(change);
    }
}

/// <summary>Runs a delegate on every property attach, for tests that react from inside a
/// property lifecycle callback.</summary>
internal sealed class DelegatePropertyAttachHandler(Action<SubjectPropertyLifecycleChange> onAttach) : IPropertyLifecycleHandler
{
    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        onAttach(change);
    }

    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
    }
}

/// <summary>Runs a delegate on every property detach, for tests that react from inside a
/// property lifecycle callback.</summary>
internal sealed class DelegatePropertyDetachHandler(Action<SubjectPropertyLifecycleChange> onDetach) : IPropertyLifecycleHandler
{
    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
    }

    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
        onDetach(change);
    }
}
