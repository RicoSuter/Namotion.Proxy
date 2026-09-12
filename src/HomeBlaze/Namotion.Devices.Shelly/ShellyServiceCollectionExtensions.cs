using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.Shelly;

public static class ShellyServiceCollectionExtensions
{
    public static IServiceCollection AddShellyDevice(
        this IServiceCollection services,
        Action<ShellyDevice>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddSubject(configure, contextResolver);
}
