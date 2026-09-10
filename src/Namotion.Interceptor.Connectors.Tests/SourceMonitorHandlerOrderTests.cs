using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Proves the monitor's <c>[RunsAfter(typeof(LifecycleInterceptor))]</c> binds against the merged
/// ordering seam. Registration order is deliberately adversarial: unordered resolution preserves
/// registration order, so with an unbound constraint the monitor registered first would resolve
/// ahead of the lifecycle, and the fan-out would run it before the descent.
/// </summary>
public class SourceMonitorHandlerOrderTests
{
    [Fact]
    public void WhenTheMonitorIsRegisteredBeforeTheLifecycle_ThenItStillResolvesBehindIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService<ILifecycleHandler>(new SourceMonitor());
        context.WithLifecycle();

        // Act
        var handlers = context.GetServices<ILifecycleHandler>()
            .Select(handler => handler.GetType().Name)
            .ToArray();

        // Assert
        Assert.Equal([nameof(LifecycleInterceptor), nameof(SourceMonitor)], handlers);
    }

    [Fact]
    public void WhenSourceMonitoringIsConfigured_ThenTheMonitorResolvesBehindTheLifecycle()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithSourceMonitoring();

        // Act
        var handlers = context.GetServices<ILifecycleHandler>()
            .Select(handler => handler.GetType().Name)
            .ToArray();

        // Assert
        Assert.Equal([nameof(LifecycleInterceptor), nameof(SourceMonitor)], handlers);
    }
}
