using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.Wallbox;

public static class WallboxServiceCollectionExtensions
{
    public static IServiceCollection AddWallboxCharger(
        this IServiceCollection services,
        Action<WallboxCharger>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddSubject(configure, contextResolver);
}
