using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class DerivedProjectionOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAComputedProjectionReturnsASubject_ThenItsExistingOwnershipIsPreserved(bool foreign)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var otherContext = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var projected = new Person();
        if (foreign) projected.AttachToContext(otherContext);
        var subject = new ScalarTriggeredOrphanSubject { Orphan = projected };

        // Act
        subject.AttachToContext(context);
        subject.Name = "recalculate";

        // Assert
        var data = new PropertyReference(subject, nameof(ScalarTriggeredOrphanSubject.Current)).GetDerivedPropertyData();
        Assert.Same(projected, data.LastKnownValue);
        Assert.Same(projected, subject.Current);
        Assert.Same(foreign ? otherContext : null, projected.TryGetContext());
        Assert.Equal(0, projected.GetReferenceCount());
        subject.DetachFromContext(context);
        Assert.Same(foreign ? otherContext : null, projected.TryGetContext());
        if (foreign) projected.DetachFromContext(otherContext);
    }

    [Fact]
    public void WhenAStoredEdgeIsRemoved_ThenACachedProjectionDoesNotKeepTheChildOwned()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var subject = new CachingOrphanDerivedSubject(context);
        var child = new Person();
        subject.Stored = child;
        Assert.Same(child, subject.Current);
        Assert.Equal(1, child.GetReferenceCount());

        // Act
        subject.Stored = null;

        // Assert
        Assert.Same(child, subject.Current);
        Assert.Null(child.TryGetContext());
        Assert.Equal(0, child.GetReferenceCount());
        Assert.Empty(child.GetParents());
        var data = new PropertyReference(subject, nameof(CachingOrphanDerivedSubject.Current)).GetDerivedPropertyData();
        Assert.Same(child, data.LastKnownValue);
    }
}
