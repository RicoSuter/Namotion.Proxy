using Namotion.Interceptor.Tracking;

using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Registry;

public static class InterceptorSubjectContextExtensions
{
    /// <summary>
    /// Adds the registry which tracks and extends subjects.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The context.</returns>
    public static IInterceptorSubjectContext WithRegistry(this IInterceptorSubjectContext context)
    {
        context
            .TryAddService<ISubjectRegistry>(() => new SubjectRegistry(), _ => true);

        return context
            .WithContextInheritance();
    }
}
