using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// Extension methods for registering hosted subjects with dependency injection.
/// </summary>
public static class HostedSubjectServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IHostedService"/> as a singleton and hosted service.
    /// </summary>
    /// <typeparam name="T">The hosted service type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure the instance after creation.</param>
    /// <param name="contextResolver">
    /// Optional resolver for the <see cref="IInterceptorSubjectContext"/>.
    /// If null, attempts to resolve from DI; if not registered in DI, no context is used.
    /// A non-null result is passed explicitly when the subject has a context-taking constructor.
    /// A null result leaves constructor argument resolution to dependency injection.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHostedSubject<T>(
        this IServiceCollection services,
        Action<T>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        where T : class, IHostedService
    {
        services.TryAddSingleton<T>(serviceProvider =>
        {
            var context = HasContextConstructor<T>()
                ? contextResolver != null
                    ? contextResolver(serviceProvider)
                    : serviceProvider.GetService<IInterceptorSubjectContext>()
                : null;
            using var startup = (context ?? serviceProvider.GetService<IInterceptorSubjectContext>())?.DeferHostedServiceStartup();
            var instance = context is not null
                ? ActivatorUtilities.CreateInstance<T>(serviceProvider, context)
                : ActivatorUtilities.CreateInstance<T>(serviceProvider);

            configure?.Invoke(instance);
            startup?.Complete();
            return instance;
        });

        services.AddHostedService<T>(serviceProvider => serviceProvider.GetRequiredService<T>());

        return services;
    }

    private static bool HasContextConstructor<T>()
    {
        return typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Any(constructor => constructor.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(IInterceptorSubjectContext)));
    }
}
