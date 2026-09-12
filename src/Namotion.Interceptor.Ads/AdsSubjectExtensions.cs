using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Ads;
using Namotion.Interceptor.Ads.Client;
using Namotion.Interceptor.Ads.Mapping;
using TwinCAT.Ads;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering TwinCAT ADS client sources in the dependency injection container.
/// </summary>
public static class AdsSubjectExtensions
{
    /// <summary>
    /// Registers a TwinCAT ADS client source that connects to a PLC by IP or hostname using an in-process AMS
    /// router (embedded mode). No system TwinCAT router is required, so this works cross-platform. The PLC must
    /// trust this client (a static route on the PLC, or ADS-Secure). Only one embedded router runs per process.
    /// </summary>
    /// <typeparam name="TSubject">The subject type to synchronize with the PLC.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="host">The PLC IP or hostname; the embedded router routes to it.</param>
    /// <param name="amsPort">The AMS port (default: 851 for the TwinCAT3 PLC runtime).</param>
    /// <param name="amsNetId">The target AMS Net ID. When null and <paramref name="host"/> is an IP, it defaults to <c>{host}.1.1</c>; required when <paramref name="host"/> is a hostname.</param>
    /// <param name="connectorName">The connector name used for attribute-based symbol mapping (default: "ads").</param>
    public static IServiceCollection AddAdsSubjectClientSource<TSubject>(
        this IServiceCollection services,
        string host,
        int amsPort = 851,
        AmsNetId? amsNetId = null,
        string connectorName = AdsConstants.DefaultConnectorName)
        where TSubject : IInterceptorSubject
    {
        return services.AddAdsSubjectClientSource(
            serviceProvider => serviceProvider.GetRequiredService<TSubject>(),
            _ => new AdsClientConfiguration
            {
                Host = host,
                AmsNetId = amsNetId,
                AmsPort = amsPort,
                Mapper = AdsCompositeMapper.CreateDefault(connectorName)
            });
    }

    /// <summary>
    /// Registers a TwinCAT ADS client source that connects through the machine's existing AMS router by AMS Net
    /// ID (system-router mode). Use <see cref="AmsNetId.Local"/> for an in-process loopback connection, or a
    /// remote PLC's net id when the system router already has a route to it. No embedded router is started.
    /// </summary>
    /// <typeparam name="TSubject">The subject type to synchronize with the PLC.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="amsNetId">The target AMS Net ID.</param>
    /// <param name="amsPort">The AMS port (default: 851 for the TwinCAT3 PLC runtime).</param>
    /// <param name="connectorName">The connector name used for attribute-based symbol mapping (default: "ads").</param>
    public static IServiceCollection AddAdsSubjectClientSource<TSubject>(
        this IServiceCollection services,
        AmsNetId amsNetId,
        int amsPort = 851,
        string connectorName = AdsConstants.DefaultConnectorName)
        where TSubject : IInterceptorSubject
    {
        return services.AddAdsSubjectClientSource(
            serviceProvider => serviceProvider.GetRequiredService<TSubject>(),
            _ => new AdsClientConfiguration
            {
                AmsNetId = amsNetId,
                AmsPort = amsPort,
                Mapper = AdsCompositeMapper.CreateDefault(connectorName)
            });
    }

    /// <summary>
    /// Registers a TwinCAT ADS client source with full configuration control.
    /// Uses the keyed-services pattern to support multiple registrations in the same container,
    /// and also exposes each registration as an unkeyed <see cref="AdsSubjectClientSource"/> alias.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="subjectSelector">A factory that resolves the root subject from the service provider.</param>
    /// <param name="configurationProvider">A factory that creates the ADS client configuration from the service provider.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAdsSubjectClientSource(
        this IServiceCollection services,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, AdsClientConfiguration> configurationProvider)
    {
        var key = Guid.NewGuid().ToString();
        return services
            .AddKeyedSingleton(key, (serviceProvider, _) => configurationProvider(serviceProvider))
            .AddKeyedSingleton(key, (serviceProvider, _) => subjectSelector(serviceProvider))
            .AddKeyedSingleton(key, (serviceProvider, _) =>
            {
                var subject = serviceProvider.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new AdsSubjectClientSource(
                    subject,
                    serviceProvider.GetRequiredKeyedService<AdsClientConfiguration>(key),
                    serviceProvider.GetRequiredService<ILogger<AdsSubjectClientSource>>());
            })
            .AddSingleton(serviceProvider =>
                serviceProvider.GetRequiredKeyedService<AdsSubjectClientSource>(key))
            .AddSingleton<IHostedService>(serviceProvider =>
                serviceProvider.GetRequiredKeyedService<AdsSubjectClientSource>(key));
    }
}
