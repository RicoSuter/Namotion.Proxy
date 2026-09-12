using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.Philips.Hue;

public static class HueServiceCollectionExtensions
{
    public static IServiceCollection AddPhilipsHue(
        this IServiceCollection services,
        Action<HueBridge>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddSubject(configure, contextResolver);
}
