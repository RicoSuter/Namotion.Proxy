using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Hosting.Tests;

public class ContextConfigurationCompositionTests
{
    [Fact]
    public void WhenHostedServiceContextsAreConfiguredBeforeComposition_ThenResolutionThrows()
    {
        // Arrange
        var parent = InterceptorSubjectContext.Create()
            .WithHostedServices(new ServiceCollection());
        var child = InterceptorSubjectContext.Create()
            .WithHostedServices(new ServiceCollection());
        child.AddFallbackContext(parent);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => child.GetServices<object>());

        // Assert
        Assert.Contains(typeof(ILifecycleInterceptor).FullName!, exception.Message);
    }
}
