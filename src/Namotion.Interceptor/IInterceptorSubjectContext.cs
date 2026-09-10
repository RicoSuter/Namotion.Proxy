using System.Collections.Immutable;

namespace Namotion.Interceptor;

/// <summary>
/// The central context for interceptor subjects: service registration and retrieval. Execution of
/// intercepted operations is an internal concern of the implementation.
/// </summary>
/// <remarks>
/// Not independently implementable: interceptor chains compile inside the built-in
/// implementation, so contexts come from <see cref="InterceptorSubjectContext.Create"/> and
/// attaching a foreign implementation of this interface is rejected by the executor. The
/// interface exists as the consumer-facing surface, not as an extension point.
/// </remarks>
public interface IInterceptorSubjectContext
{
    /// <summary>
    /// Registers a service instance directly with the context.
    /// </summary>
    /// <remarks>
    /// A service implementing <see cref="ISingletonContextService{TContract}"/> reserves that
    /// contract on this context: registering a second service implementing the same contract
    /// throws, even when it is the same instance or a different <typeparamref name="TService"/>
    /// is used. A rejected registration leaves the context unchanged.
    /// </remarks>
    /// <typeparam name="TService">The type of service to register.</typeparam>
    /// <param name="service">The service instance to add.</param>
    /// <exception cref="InvalidOperationException">A singleton contract of the service is already reserved on this context.</exception>
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
    /// A created service implementing <see cref="ISingletonContextService{TContract}"/> is
    /// validated like in <see cref="AddService{TService}"/>, against the state after the factory
    /// ran, so it also conflicts with a service the factory itself registered reentrantly. A
    /// conflict throws rather than returning false and leaves the context unchanged, but any
    /// registration the factory already published stays.
    /// </remarks>
    /// <typeparam name="TService">The type of service to register.</typeparam>
    /// <param name="factory">Factory function to create the service instance.</param>
    /// <param name="exists">Predicate to check against existing services. If any existing service matches (returns true), the factory is not invoked.</param>
    /// <returns>True if the service was added, false if a matching service already exists.</returns>
    /// <exception cref="InvalidOperationException">A singleton contract of the created service is already reserved on this context.</exception>
    bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists);

    /// <summary>
    /// Retrieves a service of the specified type, or null if not registered.
    /// </summary>
    /// <typeparam name="TInterface">The type of service to retrieve.</typeparam>
    /// <returns>The service instance, or null if not found.</returns>
    TInterface? TryGetService<TInterface>();

    /// <summary>
    /// Retrieves all registered services of the specified type.
    /// </summary>
    /// <typeparam name="TInterface">The type of services to retrieve.</typeparam>
    /// <returns>An immutable array of all matching services.</returns>
    ImmutableArray<TInterface> GetServices<TInterface>();
}
