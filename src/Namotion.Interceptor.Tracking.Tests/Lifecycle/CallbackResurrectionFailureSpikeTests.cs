using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CallbackResurrectionFailureSpikeTests
{
    [Fact]
    public void WhenExplicitResurrectionGetterThrows_ThenRejectedRootAndDescendantsAreReleased()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var descendant = new CallbackSpikeNode();
        var child = new CallbackSpikeSegmentWrapper { Children = new([descendant]) };
        var parent = new CallbackSpikeNode(context) { Payload = child };
        var failure = new InvalidOperationException("resurrection getter");
        var attempted = false;
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        lifecycle.SubjectDetaching += change =>
        {
            if (!ReferenceEquals(change.Subject, child)) return;
            Assert.Same(context, child.GetContext());
            if (attempted) return;
            attempted = true;
            child.OnRead = () => throw failure;
            child.AttachToContext(context);
        };

        // Act
        var exception = Record.Exception(() => parent.Payload = null);
        child.OnRead = null;

        // Assert
        Assert.Same(failure, exception);
        Assert.True(attempted);
        Assert.Null(child.TryGetContext());
        Assert.Null(descendant.TryGetContext());
        Assert.Null(child.TryGetRegisteredSubject());
        Assert.Null(descendant.TryGetRegisteredSubject());
        Assert.False(lifecycle.Graph.IsOwned(child));
        Assert.False(lifecycle.Graph.IsOwned(descendant));
        Assert.False(lifecycle.Graph.IsReleasing(child));
        Assert.Equal(SubjectAttachmentAnchorKind.None, child.Executor.AttachmentAnchor);
    }
    [Fact]
    public void WhenResurrectionDescendantThrowsAfterEarlierEdgesAttach_ThenEveryPublishedEdgeIsRemoved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var oldDescendant = new CallbackSpikeNode();
        var child = new CallbackSpikeSegmentWrapper { Children = new([oldDescendant]) };
        var parent = new CallbackSpikeNode(context) { Payload = child };
        var accepted = new CallbackSpikeNode();
        var failingDescendant = new CallbackSpikeSegmentWrapper();
        var newDescendant = new CallbackSpikeNode { Payload = failingDescendant };
        var failure = new InvalidOperationException("descendant getter");
        var attempted = false;
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        lifecycle.SubjectDetaching += change =>
        {
            Assert.Same(context, change.Subject.GetContext());
            if (!ReferenceEquals(change.Subject, child) || attempted) return;
            attempted = true;
            child.Children = new([accepted, newDescendant]);
            failingDescendant.OnRead = () => throw failure;
            child.AttachToContext(context);
        };

        // Act
        var exception = Record.Exception(() => parent.Payload = null);
        failingDescendant.OnRead = null;

        // Assert
        Assert.Same(failure, exception);
        Assert.True(attempted);
        foreach (var subject in new IInterceptorSubject[] { child, oldDescendant, accepted, newDescendant, failingDescendant })
        {
            Assert.Null(subject.TryGetContext());
            Assert.Null(subject.TryGetRegisteredSubject());
            Assert.False(lifecycle.Graph.IsOwned(subject));
            Assert.False(lifecycle.Graph.IsReleasing(subject));
            Assert.Equal(0, subject.GetReferenceCount());
            Assert.Equal(SubjectAttachmentAnchorKind.None, subject.Executor.AttachmentAnchor);
        }
    }

}
