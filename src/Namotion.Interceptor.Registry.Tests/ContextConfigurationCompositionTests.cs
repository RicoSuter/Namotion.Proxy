using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests;

public class ContextConfigurationCompositionTests
{
    [Fact]
    public void WhenRegistryIsConfiguredAfterCompositionWithCustomAuthorities_ThenItReusesBothAuthorities()
    {
        // Arrange
        var registry = new CustomSubjectRegistry();
        var lifecycle = new CustomLifecycleInterceptor();
        var parent = InterceptorSubjectContext.Create();
        parent.AddService<ISubjectRegistry>(registry);
        parent.AddService<ILifecycleInterceptor>(lifecycle);
        var child = InterceptorSubjectContext.Create();
        child.AddFallbackContext(parent);

        // Act
        child.WithRegistry();

        // Assert
        Assert.Same(registry, child.GetService<ISubjectRegistry>());
        Assert.Single(child.GetServices<ISubjectRegistry>());
        Assert.Same(lifecycle, child.GetService<ILifecycleInterceptor>());
        Assert.Single(child.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenRegistryContextsShareRegistryBeforeComposition_ThenLifecycleResolutionThrows()
    {
        // Arrange
        var registry = new SubjectRegistry();
        var parent = InterceptorSubjectContext.Create();
        var child = InterceptorSubjectContext.Create();
        parent.AddService(registry);
        child.AddService(registry);
        parent.WithRegistry();
        child.WithRegistry();
        child.AddFallbackContext(parent);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => child.GetServices<object>());

        // Assert
        Assert.Contains(typeof(ILifecycleInterceptor).FullName!, exception.Message);
    }

    [Fact]
    public void WhenRegistryContextsShareLifecycleBeforeComposition_ThenRegistryResolutionThrows()
    {
        // Arrange
        var lifecycle = new LifecycleInterceptor();
        var parent = InterceptorSubjectContext.Create();
        var child = InterceptorSubjectContext.Create();
        parent.AddService(lifecycle);
        child.AddService(lifecycle);
        parent.WithRegistry();
        child.WithRegistry();
        child.AddFallbackContext(parent);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => child.GetServices<object>());

        // Assert
        Assert.Contains(typeof(ISubjectRegistry).FullName!, exception.Message);
    }

    private sealed class CustomSubjectRegistry : ISubjectRegistry
    {
        public IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> KnownSubjects { get; } =
            new Dictionary<IInterceptorSubject, RegisteredSubject>();

        public RegisteredSubject? TryGetRegisteredSubject(IInterceptorSubject subject)
        {
            return null;
        }
    }

    private sealed class CustomLifecycleInterceptor : ILifecycleInterceptor
    {
        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
        }
    }
}
