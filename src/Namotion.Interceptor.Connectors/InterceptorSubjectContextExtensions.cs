using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Extension methods for configuring <see cref="IInterceptorSubjectContext"/> with source-related features.
/// </summary>
public static class InterceptorSubjectContextExtensions
{
    /// <summary>
    /// Enables external source write support for transactions.
    /// Registers an <see cref="ITransactionWriter"/> that writes changes to external sources.
    /// Automatically registers WithTransactions() if not already registered.
    /// </summary>
    /// <remarks>
    /// Idempotent, and first writer wins: a custom <see cref="ITransactionWriter"/> already on the
    /// context keeps the slot and no default writer is constructed, so composing this call with a
    /// hand-written writer stays a working configuration.
    /// </remarks>
    /// <param name="context">The interceptor subject context to configure.</param>
    /// <returns>The same context instance for method chaining.</returns>
    public static IInterceptorSubjectContext WithSourceTransactions(this IInterceptorSubjectContext context)
    {
        context
            .WithTransactions()
            .TryAddService<ITransactionWriter>(
                () => new SourceTransactionWriter(),
                _ => true);

        return context;
    }

    /// <summary>
    /// Adds source monitoring to this context, the one context every subject of the tree is
    /// attached to. Implies WithLifecycle, whose parent tracking the branch-scoped wait needs.
    /// </summary>
    public static IInterceptorSubjectContext WithSourceMonitoring(this IInterceptorSubjectContext context)
    {
        context.WithLifecycle();

        // One registration serves every role (SourceMonitor and ILifecycleHandler) through
        // assignability-based resolution.
        context.TryAddService(() =>
        {
            // Lazy logger: the context is configured before any logging provider exists. This is the
            // same Func<ILogger?> idiom HostedServiceHandler uses. Without it every warning the wait
            // engine emits is a silent no-op, and those warnings are the only thing distinguishing a
            // vacuous completion from a live tree.
            return new SourceMonitor(() =>
                context.TryGetService<ILoggerFactory>()?.CreateLogger<SourceMonitor>());
        }, _ => true);

        return context;
    }

    /// <summary>
    /// Adds source monitoring and registers a hosted service that completes source registration when
    /// IHostApplicationLifetime.ApplicationStarted fires. Use this when every source is a
    /// DI-registered hosted service. Applications that create sources at runtime use the
    /// parameterless overload and call CompleteSourceRegistration themselves.
    /// </summary>
    public static IInterceptorSubjectContext WithSourceMonitoring(
        this IInterceptorSubjectContext context, IServiceCollection services)
    {
        context.WithSourceMonitoring();

        // Bridges the host's logging provider into the context, the same way WithHostedServices
        // wires HostedServiceHandler's logger from DI: the context is configured before the host is
        // built, so without this the monitor's lazy logger resolver (see WithSourceMonitoring())
        // never finds an ILoggerFactory and every wait-engine warning is a silent no-op.
        services.AddHostedService(serviceProvider =>
        {
            context.TryAddService(serviceProvider.GetRequiredService<ILoggerFactory>, _ => true);
            return new SourceRegistrationGate(
                context, serviceProvider.GetRequiredService<IHostApplicationLifetime>());
        });

        return context;
    }
}
