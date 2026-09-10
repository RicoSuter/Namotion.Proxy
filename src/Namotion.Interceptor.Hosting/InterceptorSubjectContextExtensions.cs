using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting;

public static class InterceptorSubjectContextExtensions
{
    /// <summary>
    /// Defers hosted-service starts attached in the current execution flow until the scope and its
    /// enclosing scopes are disposed.
    /// Returns null when hosted services are not configured on this context.
    /// </summary>
    public static HostedServiceStartupScope? DeferHostedServiceStartup(this IInterceptorSubjectContext context)
        => context.TryGetService<HostedServiceHandler>()?.DeferStartup();

    public static IInterceptorSubjectContext WithHostedServices(this IInterceptorSubjectContext context, IServiceCollection serviceCollection)
    {
        context
            .TryAddService(() =>
            {
                var handler = new HostedServiceHandler();

                // A plain Add, not AddHostedService: AddHostedService routes through TryAddEnumerable,
                // which dedupes on the implementation type, so a second context on the same collection
                // would silently lose its handler and never start any of its subjects.
                serviceCollection.AddSingleton<IHostedService>(serviceProvider =>
                {
                    // Deferred to here because the handler is built before any provider exists: the
                    // context creates it, and only the host can resolve a logger for it.
                    handler.SetLogger(serviceProvider.GetRequiredService<ILogger<HostedServiceHandler>>());
                    return handler;
                });

                return handler;
            }, _ => true);

        return context
            .WithLifecycle();
    }
}