namespace Namotion.Interceptor.Tracking.Lifecycle;

public static class LifecycleInterceptorExtensions
{
    /// <summary>
    /// Gets the lifecycle interceptor from the context, if configured.
    /// </summary>
    public static LifecycleInterceptor? TryGetLifecycleInterceptor(this IInterceptorSubjectContext context)
    {
        return context.TryGetService<LifecycleInterceptor>();
    }

    /// <summary>
    /// Gets the number of active incoming structural edge occurrences of the subject. A subject
    /// listed twice in one collection counts two. Returns 0 for an unattached subject and for an
    /// anchored root that no edge points at, so this is not an ownership predicate: use
    /// <see cref="InterceptorSubjectExtensions.TryGetContext"/> to test attachment and a non-None
    /// <see cref="Interceptors.IInterceptorExecutor.AttachmentAnchor"/> to test root-ness.
    /// </summary>
    public static int GetReferenceCount(this IInterceptorSubject subject)
    {
        // Zero is the answer in both fallbacks rather than a stand-in for one. No edge can point at
        // an unattached subject, because an attached parent would have pulled it into the context.
        // On a context with no lifecycle, the only subject that can be attached is one anchored to
        // that context directly: nothing propagates the context along an edge, and a lifecycle
        // cannot be registered behind an attach, so such a subject is a root and has no edge either.
        return subject.TryGetContext()?.TryGetLifecycleInterceptor()?.GetReferenceCount(subject) ?? 0;
    }

    /// <summary>
    /// Runs the property attach callbacks for one property. The subject must be attached: every
    /// caller runs inside an attach descent or a property admission, where the attachment is
    /// already established.
    /// </summary>
    public static void AttachSubjectProperty(this IInterceptorSubject subject, PropertyReference property)
    {
        using var scope = CallbackReentrancyGuard.EnterPropertyCallbackScope();
        var change = new SubjectPropertyLifecycleChange(subject, property);

        foreach (var handler in subject.GetContext().GetServices<IPropertyLifecycleHandler>())
        {
            handler.AttachProperty(change);
        }

        if (subject is IPropertyLifecycleHandler lifecycleHandler)
        {
            lifecycleHandler.AttachProperty(change);
        }
    }

    /// <summary>
    /// Runs the property detach callbacks for one property. The subject must be attached: the
    /// release descent runs these before the claim is released.
    /// </summary>
    public static void DetachSubjectProperty(this IInterceptorSubject subject, PropertyReference property)
    {
        using var scope = CallbackReentrancyGuard.EnterPropertyCallbackScope();
        var change = new SubjectPropertyLifecycleChange(subject, property);

        foreach (var handler in subject.GetContext().GetServices<IPropertyLifecycleHandler>())
        {
            handler.DetachProperty(change);
        }

        if (subject is IPropertyLifecycleHandler lifecycleHandler)
        {
            lifecycleHandler.DetachProperty(change);
        }
    }
}
