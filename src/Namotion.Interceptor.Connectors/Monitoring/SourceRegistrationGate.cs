using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Connectors.Monitoring;

/// <summary>
/// Completes source registration once host startup has finished, so every DI-registered source has
/// been started and registered. Its only job is releasing the initial hold; it takes none of its own.
/// </summary>
internal sealed class SourceRegistrationGate(
    IInterceptorSubjectContext context,
    IHostApplicationLifetime lifetime) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Awaiting ApplicationStarted here would deadlock the host, because it fires only after
        // every StartAsync has returned. Register a callback instead.
        lifetime.ApplicationStarted.Register(context.CompleteSourceRegistration);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
