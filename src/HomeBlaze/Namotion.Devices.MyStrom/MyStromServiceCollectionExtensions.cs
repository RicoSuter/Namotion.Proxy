using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.MyStrom;

public static class MyStromServiceCollectionExtensions
{
    public static IServiceCollection AddMyStromSwitch(
        this IServiceCollection services,
        Action<MyStromSwitch>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddSubject(configure, contextResolver);
}
