using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.Ecowitt;

public static class EcowittServiceCollectionExtensions
{
    public static IServiceCollection AddEcowittGateway(
        this IServiceCollection services,
        Action<EcowittGateway>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddSubject(configure, contextResolver);
}
