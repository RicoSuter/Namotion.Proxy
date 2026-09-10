namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// The lifecycle's outbound notification surface: the two subject events and the ordered handler
/// fan-out, for one context.
/// </summary>
/// <remarks>
/// Separate from <see cref="LifecycleInterceptor"/> so that what the publishing classes are allowed
/// to do is a property of a constructor signature: handing them the interceptor would put the write
/// protocol and both attach entry points within reach of code running under the topology lock.
///
/// Every publication marks the thread through <see cref="CallbackReentrancyGuard.EnterScope"/>,
/// which is what lets the structural write protocol reject a callback that writes a structural
/// property.
/// </remarks>
internal sealed class LifecycleNotifier(IInterceptorSubjectContext context)
{
    public event Action<SubjectLifecycleChange>? SubjectAttached;

    public event Action<SubjectLifecycleChange>? SubjectDetaching;

    public void RaiseSubjectAttached(SubjectLifecycleChange change)
    {
        using var scope = CallbackReentrancyGuard.EnterScope();
        SubjectAttached?.Invoke(change);
    }

    public void RaiseSubjectDetaching(SubjectLifecycleChange change)
    {
        using var scope = CallbackReentrancyGuard.EnterScope();
        SubjectDetaching?.Invoke(change);
    }

    /// <summary>Publishes an edge removal that did not release the subject.</summary>
    public void PublishEdgeRemoved(IInterceptorSubject subject, PropertyReference property, object? index, int referenceCount)
    {
        InvokeRemovedLifecycleHandlers(subject, new SubjectLifecycleChange
        {
            Subject = subject,
            Property = property,
            Index = index,
            ReferenceCount = referenceCount,
            IsPropertyReferenceRemoved = true
        });
    }

    public void InvokeAddedLifecycleHandlers(IInterceptorSubject subject, SubjectLifecycleChange change)
    {
        using var scope = CallbackReentrancyGuard.EnterScope();
        var handlers = context.GetServices<ILifecycleHandler>();
        for (var index = 0; index < handlers.Length; index++)
        {
            handlers[index].HandleLifecycleChange(change);
        }

        if (subject is ILifecycleHandler subjectHandler)
        {
            subjectHandler.HandleLifecycleChange(change);
        }
    }

    public void InvokeRemovedLifecycleHandlers(IInterceptorSubject subject, SubjectLifecycleChange change)
    {
        using var scope = CallbackReentrancyGuard.EnterScope();
        if (subject is ILifecycleHandler subjectHandler)
        {
            subjectHandler.HandleLifecycleChange(change);
        }

        var handlers = context.GetServices<ILifecycleHandler>();
        for (var index = 0; index < handlers.Length; index++)
        {
            handlers[index].HandleLifecycleChange(change);
        }
    }

    public void RefreshCollectionProperty(PropertyReference property, object? value)
    {
        using var scope = CallbackReentrancyGuard.EnterScope();
        var handlers = context.GetServices<IPropertyLifecycleHandler>();
        for (var index = 0; index < handlers.Length; index++)
        {
            handlers[index].RefreshCollectionProperty(property, value);
        }
    }
}
