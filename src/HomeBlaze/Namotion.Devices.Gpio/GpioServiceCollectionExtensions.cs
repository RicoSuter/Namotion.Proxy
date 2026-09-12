using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.Gpio;

/// <summary>
/// Extension methods for registering GPIO services with dependency injection.
/// </summary>
public static class GpioServiceCollectionExtensions
{
    /// <summary>
    /// Adds GPIO support as a hosted service.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure the GPIO subject.</param>
    /// <param name="contextResolver">
    /// Optional resolver for the <see cref="IInterceptorSubjectContext"/>.
    /// If null, attempts to resolve from DI; if not registered in DI, no context is used.
    /// If provided, uses the resolver's return value (which may be null for explicitly no context).
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddGpio(
        this IServiceCollection services,
        Action<GpioSubject>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddSubject(configure, contextResolver);
}
