using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tests.Context;

public class ContextServiceResolutionTests
{
    [Fact]
    public void WhenAddingSingleService_ThenItCanBeRetrieved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();

        // Act
        context.AddService(1);

        // Assert
        Assert.Equal(1, context.GetService<int>());
    }
    
    [Fact]
    public void WhenAddingTwoServices_ThenListCanBeRetrieved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();

        // Act
        context.AddService(1);
        context.AddService(2);

        // Assert
        var services = context
            .GetServices<int>()
            .ToArray();
        
        Assert.Contains(1, services);
        Assert.Contains(2, services);
        Assert.Equal(2, services.Length);
        
        Assert.Throws<InvalidOperationException>(() => context.GetService<int>());
    }
    
    [Fact]
    public void WhenTheSameInstanceIsRegisteredTwice_ThenItResolvesOnce()
    {
        // Arrange: registration keeps insertion order and tolerates duplicate references, so the
        // dedup lives in service resolution, keeping the first occurrence.
        var context = InterceptorSubjectContext.Create();
        var service = new DuplicateOrderedService();

        context.AddService(service);
        context.AddService(service);

        // Act
        var services = context.GetServices<IOrderedTestService>();

        // Assert
        Assert.Same(service, Assert.Single(services));
    }

    [Fact]
    public void WhenTwoInstancesOfSameServiceTypeAreRegistered_ThenOrderingAttributeBindsAgainstAllInstances()
    {
        // Arrange: the first duplicate enumerates before the constrainer, so last-index binding
        // would leave it unordered.
        var context = InterceptorSubjectContext.Create();

        var duplicate0 = new DuplicateOrderedService();
        var constrainer = new ConstrainerOrderedService();
        var duplicate1 = new DuplicateOrderedService();

        context.AddService(duplicate0);
        context.AddService(constrainer);
        context.AddService(duplicate1);

        // Act
        var services = context.GetServices<IOrderedTestService>();

        // Assert: the constrainer precedes both duplicate instances
        Assert.Equal(3, services.Length);
        Assert.Same(constrainer, services[0]);
        Assert.Same(duplicate0, services[1]);
        Assert.Same(duplicate1, services[2]);
    }

    private interface IOrderedTestService { }

    private class DuplicateOrderedService : IOrderedTestService { }

    [RunsBefore(typeof(DuplicateOrderedService))]
    private class ConstrainerOrderedService : IOrderedTestService { }
}
