using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CallbackReconciliationReviewTests
{
    [Fact]
    public void WhenChildGetterReplacesTheWritingProperty_ThenNoRemainingOldOccurrenceIsAttached()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new CallbackSpikeNode(context);
        var stale = new CallbackSpikeNode();
        var replacement = new CallbackSpikeNode();
        var trigger = new CallbackSpikeSegmentWrapper();
        var branch = new CallbackSpikeNode { Payload = trigger };
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() > 0)
            {
                trigger.OnRead = null;
                root.Payload = replacement;
            }
        };

        // Act
        root.Payload = new[] { branch, stale };

        // Assert
        Assert.Same(replacement, root.Payload);
        Assert.Same(context, replacement.TryGetContext());
        Assert.Equal(1, replacement.GetReferenceCount());
        Assert.Null(branch.TryGetContext());
        Assert.Null(trigger.TryGetContext());
        Assert.Null(stale.TryGetContext());
    }

    [Fact]
    public void WhenChildGetterRetainsAnUnpublishedOccurrence_ThenThatOccurrenceStillAttaches()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new CallbackSpikeNode(context);
        var retained = new CallbackSpikeNode();
        var trigger = new CallbackSpikeSegmentWrapper();
        var branch = new CallbackSpikeNode { Payload = trigger };
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() > 0)
            {
                trigger.OnRead = null;
                root.Payload = new[] { retained };
            }
        };

        // Act
        root.Payload = new[] { branch, retained };

        // Assert
        Assert.Same(retained, Assert.Single((CallbackSpikeNode[])root.Payload!));
        Assert.Same(context, retained.TryGetContext());
        Assert.Equal(1, retained.GetReferenceCount());
        Assert.Null(branch.TryGetContext());
        Assert.Null(trigger.TryGetContext());
    }

}
