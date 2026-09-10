using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry;

public static class InterceptorSubjectContextExtensions
{
    /// <summary>
    /// Adds the registry which tracks and extends subjects.
    /// </summary>
    /// <remarks>
    /// Establishes the lifecycle before publishing the registry, so a lifecycle conflict throws
    /// with no registry left behind on the context.
    /// </remarks>
    /// <param name="context">The context.</param>
    /// <returns>The context.</returns>
    public static IInterceptorSubjectContext WithRegistry(this IInterceptorSubjectContext context)
    {
        context.WithLifecycle();
        context.TryAddService(() => new SubjectRegistry(), _ => true);

        return context;
    }
}