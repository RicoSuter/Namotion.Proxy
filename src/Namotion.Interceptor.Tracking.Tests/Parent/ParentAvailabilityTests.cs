using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Parent;

/// <summary>
/// Covers what the parent projection and the reference count answer when the lifecycle that
/// publishes them is absent. Empty and zero are the true answers there rather than a stand-in for
/// an unavailable one, because nothing can give such a subject a parent.
/// </summary>
public class ParentAvailabilityTests
{
    [Fact]
    public void WhenSubjectIsUnattached_ThenGetParentsReturnsEmpty()
    {
        // Arrange
        var subject = new Simulation { Name = "Detached" };

        // Act
        var parents = subject.GetParents();

        // Assert: no edge can point at an unattached subject, so empty is the answer itself
        Assert.Empty(parents);
    }

    [Fact]
    public void WhenSubjectIsUnattached_ThenGetReferenceCountReturnsZero()
    {
        // Arrange
        var subject = new Simulation { Name = "Detached" };

        // Act
        var referenceCount = subject.GetReferenceCount();

        // Assert
        Assert.Equal(0, referenceCount);
    }

    [Fact]
    public void WhenAttachedToAContextWithoutLifecycle_ThenGetParentsReturnsEmptyAndGetReferenceCountReturnsZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new Simulation(context) { Name = "Root" };

        // Act
        var parents = subject.GetParents();
        var referenceCount = subject.GetReferenceCount();

        // Assert
        Assert.Empty(parents);
        Assert.Equal(0, referenceCount);
    }

    [Fact]
    public void WhenAContextHasNoLifecycle_ThenOnlyAnchoredRootsCanBeAttachedToIt()
    {
        // Arrange: this is why empty and zero are the answers above and not a stand-in for one.
        var context = InterceptorSubjectContext.Create();
        var simulation = new Simulation(context) { Name = "Root" };
        var component = new Component { Name = "Child" };

        // Act: nothing propagates the context along the edge, so the child never attaches, and a
        // lifecycle can no longer be registered behind the root's attach either.
        simulation.Component = component;

        // Assert
        Assert.Null(component.TryGetContext());
        Assert.Throws<InvalidOperationException>(() => context.WithLifecycle());
    }

    [Fact]
    public void WhenLifecycleIsRegisteredWithoutRegistry_ThenParentsAreStillAvailable()
    {
        // Arrange: parents come from the lifecycle, so the registry is not required
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var simulation = new Simulation(context) { Name = "Root" };
        var component = new Component { Name = "Child" };

        // Act
        simulation.Component = component;

        // Assert
        Assert.Single(component.GetParents());
        Assert.Equal(1, component.GetReferenceCount());
    }
}
