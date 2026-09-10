using System.Collections.Immutable;

namespace Namotion.Interceptor;

/// <summary>
/// The central context for interceptor subjects: service registration, retrieval, and fallback-context
/// composition. Execution of intercepted operations is an internal concern of the implementation.
/// </summary>
public interface IInterceptorSubjectContext
{
    /// <summary>
    /// Registers a service instance directly with the context.
    /// </summary>
    /// <typeparam name="TService">The type of service to register.</typeparam>
    /// <param name="service">The service instance to add.</param>
    void AddService<TService>(TService service);

    /// <summary>
    /// Conditionally registers a service using a factory function.
    /// The factory is only invoked if the <paramref name="exists"/> predicate returns false for all existing services of this type.
    /// </summary>
    /// <remarks>
    /// Both delegates run while this context is locked for mutation. They may read any context and
    /// may mutate this one, but they must not mutate a different context: the calling thread then
    /// holds the mutation locks of two contexts, and two threads that each register into one context
    /// from a factory mutating the other acquire those locks in opposite orders and deadlock. Create
    /// the instance in the factory and register it into any other context after this call returns.
    ///
    /// Unlike service resolution, this operation can enter a fallback graph that consists only of a
    /// delegation cycle. If no matching service is found, registering one on this context breaks the
    /// cycle and makes subsequent service resolution possible.
    /// </remarks>
    /// <typeparam name="TService">The type of service to register.</typeparam>
    /// <param name="factory">Factory function to create the service instance.</param>
    /// <param name="exists">Predicate to check against existing services. If any existing service matches (returns true), the factory is not invoked.</param>
    /// <returns>True if the service was added, false if a matching service already exists.</returns>
    bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists);

    /// <summary>
    /// Retrieves a service of the specified type, or null if not registered.
    /// </summary>
    /// <remarks>
    /// The <c>Try</c> prefix describes the zero-service result only; it is not a no-throw guarantee.
    /// </remarks>
    /// <typeparam name="TInterface">The type of service to retrieve.</typeparam>
    /// <returns>The service instance, or null if not found.</returns>
    /// <exception cref="InvalidOperationException">
    /// The requested singular service has several matches, the delegation chain is cyclic, or an
    /// unrelated unique authority in the resolved context cone has several distinct instances.
    /// </exception>
    TInterface? TryGetService<TInterface>();

    /// <summary>
    /// Retrieves all registered services of the specified type.
    /// </summary>
    /// <remarks>
    /// A context with no own service and exactly one fallback context resolves everything through
    /// that fallback context. When following those references leads back to a context already
    /// visited, nothing can be resolved and this raises. A cycle containing at least one context
    /// that has a service of its own resolves normally, so this only affects a chain that
    /// delegates all the way round. Intercepted property and method access resolve the same way
    /// and raise the same exception, so it can surface from an ordinary property getter or setter.
    /// </remarks>
    /// <typeparam name="TInterface">The type of services to retrieve.</typeparam>
    /// <returns>An immutable array of all matching services.</returns>
    /// <exception cref="InvalidOperationException">
    /// The delegation chain is cyclic, or a unique authority in the resolved context cone has
    /// several distinct instances, including an authority unrelated to <typeparamref name="TInterface"/>.
    /// </exception>
    ImmutableArray<TInterface> GetServices<TInterface>();

    /// <summary>
    /// Adds a fallback context for service resolution.
    /// Services not found in this context will be looked up in fallback contexts.
    /// </summary>
    /// <param name="context">The fallback context to add.</param>
    /// <returns>True if the fallback context was added, false if it was already present.</returns>
    bool AddFallbackContext(IInterceptorSubjectContext context);

    /// <summary>
    /// Removes a previously added fallback context.
    /// </summary>
    /// <param name="context">The fallback context to remove.</param>
    /// <returns>True if the fallback context was removed, false if it was not present.</returns>
    bool RemoveFallbackContext(IInterceptorSubjectContext context);
}
