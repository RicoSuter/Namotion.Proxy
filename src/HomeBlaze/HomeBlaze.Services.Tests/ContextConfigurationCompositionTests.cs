using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;

namespace HomeBlaze.Services.Tests;

public class ContextConfigurationCompositionTests
{
    [Fact]
    public void WhenPathResolverContextsAreConfiguredBeforeComposition_ThenResolutionThrows()
    {
        // Arrange
        var parent = InterceptorSubjectContext.Create();
        var child = InterceptorSubjectContext.Create();
        AddRootManager(parent);
        AddRootManager(child);
        parent.WithPathResolver();
        child.WithPathResolver();
        child.AddFallbackContext(parent);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => child.GetServices<object>());

        // Assert
        Assert.Contains(typeof(ILifecycleInterceptor).FullName!, exception.Message);
    }

    private static void AddRootManager(IInterceptorSubjectContext context)
    {
        var typeProvider = new TypeProvider();
        var typeRegistry = new SubjectTypeRegistry(typeProvider);
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var serializer = new ConfigurableSubjectSerializer(typeProvider, serviceProvider);
        _ = new RootManager(typeRegistry, serializer, context);
    }
}
