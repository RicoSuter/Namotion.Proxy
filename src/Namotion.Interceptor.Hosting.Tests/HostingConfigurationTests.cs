using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting.Tests;

/// <summary>
/// <c>WithHostedServices</c> is idempotent for its handler, and establishes the lifecycle before
/// publishing it, so a lifecycle conflict throws with neither a context handler nor a DI hosted
/// service left behind.
/// </summary>
public class HostingConfigurationTests
{
    [Fact]
    public void WhenHostedServicesAreConfiguredRepeatedly_ThenOneHandlerIsRegistered()
    {
        // Arrange
        var serviceCollection = new ServiceCollection();

        // Act
        var context = InterceptorSubjectContext
            .Create()
            .WithHostedServices(serviceCollection)
            .WithHostedServices(serviceCollection);

        // Assert: one handler on the context and one hosted service in the collection.
        Assert.Single(context.GetServices<ILifecycleHandler>(), handler => handler is IHostedService);
        Assert.Single(serviceCollection, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void WhenACustomLifecycleIsRegistered_ThenWithHostedServicesPublishesNothing()
    {
        // Arrange
        var serviceCollection = new ServiceCollection();
        var context = InterceptorSubjectContext.Create();
        context.AddService<ILifecycleInterceptor>(new CustomLifecycle());

        // Act & Assert: the conflict must fire before the handler is created, so the failed
        // configuration leaves neither a context handler nor a DI hosted service behind.
        Assert.Throws<InvalidOperationException>(() => context.WithHostedServices(serviceCollection));
        Assert.Empty(context.GetServices<ILifecycleHandler>());
        Assert.DoesNotContain(serviceCollection, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    private sealed class CustomLifecycle : ILifecycleInterceptor
    {
        public void EnterStructuralWriteGate()
        {
        }

        public void ExitStructuralWriteGate()
        {
        }

        public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAttachmentAnchorKind anchor)
            => throw new NotSupportedException();

        public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
            => throw new NotSupportedException();

        public bool TryAddProperties(SubjectPropertyRegistration registration)
            => throw new NotSupportedException();

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
            => next(ref context);
    }
}
