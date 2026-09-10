using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting.Tests;

/// <summary>
/// Pins the resolved fan-out for <c>WithHostedServices</c>: the handler resolves behind the
/// lifecycle. Registration is lifecycle-first since the extension establishes its dependency
/// before publishing the handler, so this order alone no longer distinguishes a bound constraint
/// from registration order; the adversarial binding proof for the seam lives in
/// <c>LifecycleHandlerOrderTests</c>.
/// </summary>
public class HostedServiceHandlerOrderTests
{
    [Fact]
    public void WhenHostedServicesAreConfigured_ThenTheHandlerResolvesBehindTheLifecycle()
    {
        // Arrange
        var services = new ServiceCollection();
        var context = InterceptorSubjectContext
            .Create()
            .WithHostedServices(services);

        // Act
        var handlers = context.GetServices<ILifecycleHandler>()
            .Select(handler => handler.GetType().Name)
            .ToArray();

        // Assert
        Assert.Equal([nameof(LifecycleInterceptor), "HostedServiceHandler"], handlers);
    }
}
