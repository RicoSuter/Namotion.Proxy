using HomeBlaze.Host.Services;
using HomeBlaze.Host.TimeZones;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace HomeBlaze.Host;

/// <summary>
/// Extension methods for registering HomeBlaze.Host services in dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds HomeBlaze host UI services to the service collection.
    /// This includes MudBlazor and cascades to all underlying services.
    /// </summary>
    public static IServiceCollection AddHomeBlazeHost(this IServiceCollection services)
    {
        // Include Host.Services (which includes Services)
        services.AddHomeBlazeHostServices();

        // MudBlazor services
        services.AddMudServices();

        services.AddHttpContextAccessor();
        services.AddScoped<TimeZoneInterop>();

        return services;
    }
}
