using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Tests;

public class ContextConfigurationCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenSourceMonitoringContextsAreConfiguredBeforeComposition_ThenResolutionThrows(
        bool useHostedOverload)
    {
        // Arrange
        var parent = InterceptorSubjectContext.Create();
        var child = InterceptorSubjectContext.Create();
        ConfigureSourceMonitoring(parent, useHostedOverload);
        ConfigureSourceMonitoring(child, useHostedOverload);
        child.AddFallbackContext(parent);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => child.GetServices<object>());

        // Assert
        Assert.Contains(typeof(ILifecycleInterceptor).FullName!, exception.Message);
    }

    [Fact]
    public void WhenSourceTransactionContextsAreConfiguredBeforeComposition_ThenResolutionThrows()
    {
        // Arrange
        var parent = InterceptorSubjectContext.Create().WithSourceTransactions();
        var child = InterceptorSubjectContext.Create().WithSourceTransactions();
        child.AddFallbackContext(parent);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => child.GetServices<object>());

        // Assert
        Assert.Contains(typeof(SubjectTransactionInterceptor).FullName!, exception.Message);
    }

    private static void ConfigureSourceMonitoring(
        IInterceptorSubjectContext context,
        bool useHostedOverload)
    {
        if (useHostedOverload)
        {
            context.WithSourceMonitoring(new ServiceCollection());
        }
        else
        {
            context.WithSourceMonitoring();
        }
    }
}
