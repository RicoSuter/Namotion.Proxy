namespace Namotion.Interceptor;

/// <summary>
/// Extension methods for <see cref="IInterceptorSubjectContext"/> to register and retrieve services.
/// </summary>
public static class InterceptorSubjectContextExtensions
{
    /// <summary>
    /// Registers a service with the context using a factory function.
    /// The service is only added if no service of this type is already registered.
    /// </summary>
    /// <typeparam name="TService">The type of service to register.</typeparam>
    /// <param name="context">The subject context.</param>
    /// <param name="factory">Factory function to create the service instance. Only invoked if no service of this type exists.</param>
    /// <returns>The context for fluent chaining.</returns>
    public static IInterceptorSubjectContext WithService<TService>(this IInterceptorSubjectContext context, Func<TService> factory)
    {
        context.TryAddService(factory, _ => true);
        return context;
    }

    /// <summary>
    /// Registers a service with the context using a factory function.
    /// The factory is only invoked if the <paramref name="exists"/> predicate returns false for all existing services of this type.
    /// </summary>
    /// <typeparam name="TService">The type of service to register.</typeparam>
    /// <param name="context">The subject context.</param>
    /// <param name="factory">Factory function to create the service instance. Only invoked if no matching service exists.</param>
    /// <param name="exists">Predicate to check against existing services. If any existing service matches (returns true), the factory is not invoked.</param>
    /// <returns>The context for fluent chaining.</returns>
    public static IInterceptorSubjectContext WithService<TService>(this IInterceptorSubjectContext context,
        Func<TService> factory, Func<TService, bool> exists)
    {
        context.TryAddService(factory, exists);
        return context;
    }

    /// <summary>
    /// Retrieves a registered service from the context.
    /// </summary>
    /// <typeparam name="TService">The type of service to retrieve.</typeparam>
    /// <param name="context">The subject context.</param>
    /// <returns>The service instance.</returns>
    /// <exception cref="InvalidOperationException">
    /// The service is not registered, the requested singular service has several matches, the
    /// delegation chain is cyclic, or an unrelated unique authority in the resolved context cone
    /// has several distinct instances.
    /// </exception>
    public static TService GetService<TService>(this IInterceptorSubjectContext context)
    {
        return context.TryGetService<TService>()
            ?? throw new InvalidOperationException($"Service type '{typeof(TService).FullName}' not found.");
    }
}
