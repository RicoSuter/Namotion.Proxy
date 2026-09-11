using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CallbackGetterEpochReviewTests
{
    [Fact]
    public void WhenGetterReattachesItsOwnerAndThenWrites_ThenTheNewOwnershipReconcilesTheWrite()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new CallbackSpikeNode(context);
        var stale = new CallbackSpikeNode();
        var replacement = new CallbackSpikeNode();
        var wrapper = new CallbackSpikeSegmentWrapper { Children = new([stale]) };
        wrapper.OnRead = () =>
        {
            if (wrapper.GetReferenceCount() > 0)
            {
                wrapper.OnRead = null;
                root.Payload = null;
                root.Payload = wrapper;
                wrapper.Children = new([replacement]);
            }
        };

        // Act
        root.Payload = wrapper;

        // Assert
        Assert.Same(wrapper, root.Payload);
        Assert.Same(replacement, Assert.Single(wrapper.Children));
        Assert.Same(context, replacement.TryGetContext());
        Assert.Equal(1, replacement.GetReferenceCount());
        Assert.Null(stale.TryGetContext());
    }
}
