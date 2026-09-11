using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CallbackSupportGetterContinuationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void WhenGetterReleasesAncestorDuringAttach_ThenNoDescendantRemainsOwned(int depth)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new CallbackSpikeNode(context);
        var target = new CallbackSpikeNode();
        var wrapper = new CallbackSpikeSegmentWrapper { Children = new([target]) };
        var nodes = new List<CallbackSpikeNode>();
        object component = wrapper;
        for (var index = 0; index < depth; index++)
        {
            var node = new CallbackSpikeNode { Payload = component };
            nodes.Add(node);
            component = node;
        }
        wrapper.OnRead = () =>
        {
            if (root.Payload is not null)
            {
                wrapper.OnRead = null;
                root.Payload = null;
            }
        };

        // Act
        root.Payload = component;

        // Assert
        Assert.Null(root.Payload);
        Assert.Null(wrapper.TryGetContext());
        Assert.Null(target.TryGetContext());
        Assert.All(nodes, node => Assert.Null(node.TryGetContext()));
    }

    [Fact]
    public void WhenDescendantGetterRewritesTheSeedingProperty_ThenOnlyTheLatestChildrenAreOwned()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new CallbackSpikeNode(context);
        var stale = new CallbackSpikeNode();
        var replacement = new CallbackSpikeNode();
        var trigger = new CallbackSpikeSegmentWrapper();
        var branch = new CallbackSpikeNode { Payload = trigger };
        var wrapper = new CallbackSpikeSegmentWrapper { Children = new([branch, stale]) };
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() > 0)
            {
                trigger.OnRead = null;
                wrapper.Children = new([replacement]);
            }
        };

        // Act
        root.Payload = wrapper;

        // Assert
        Assert.Same(replacement, Assert.Single(wrapper.Children));
        Assert.Same(context, replacement.TryGetContext());
        Assert.Equal(1, replacement.GetReferenceCount());
        Assert.Null(branch.TryGetContext());
        Assert.Null(trigger.TryGetContext());
        Assert.Null(stale.TryGetContext());
    }

    [Fact]
    public void WhenGetterReleasesAndReattachesTheSameOwner_ThenItsChildrenAttachOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new CallbackSpikeNode(context);
        var target = new CallbackSpikeNode();
        var wrapper = new CallbackSpikeSegmentWrapper { Children = new([target]) };
        wrapper.OnRead = () =>
        {
            if (ReferenceEquals(root.Payload, wrapper))
            {
                wrapper.OnRead = null;
                root.Payload = null;
                root.Payload = wrapper;
            }
        };

        // Act
        root.Payload = wrapper;

        // Assert
        Assert.Same(wrapper, root.Payload);
        Assert.Same(context, wrapper.TryGetContext());
        Assert.Same(context, target.TryGetContext());
        Assert.Equal(1, wrapper.GetReferenceCount());
        Assert.Equal(1, target.GetReferenceCount());
    }

    [Fact]
    public void WhenUnpublishedRootGetterWritesTheStableBoxedSegment_ThenSeedingTerminates()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var target = new CallbackSpikeNode();
        var stable = new ArraySegment<CallbackSpikeNode>([target]);
        var root = new CallbackSpikeSegmentWrapper { Children = stable };
        var reads = 0;
        root.OnRead = () =>
        {
            if (root.TryGetContext() is not null && root.GetReferenceCount() == 0)
            {
                if (++reads > 16) throw new InvalidOperationException("unbounded getter retry");
                root.Children = stable;
            }
        };

        // Act
        var exception = Record.Exception(() => root.AttachToContext(context));
        root.OnRead = null;

        // Assert
        Assert.Null(exception);
        Assert.True(reads > 0);
        Assert.Same(target, Assert.Single(root.Children));
        Assert.Same(context, target.TryGetContext());
        Assert.Equal(1, target.GetReferenceCount());
    }

    [Fact]
    public void WhenClaimedRootGetterAssignsForeignChild_ThenTheWriteIsRejectedBeforeStorage()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var foreignContext = InterceptorSubjectContext.Create().WithLifecycle();
        var foreign = new CallbackSpikeNode(foreignContext);
        var target = new CallbackSpikeNode();
        var root = new CallbackSpikeSegmentWrapper { Children = new([target]) };
        Exception? writeException = null;
        root.OnRead = () =>
        {
            if (root.TryGetContext() is not null)
            {
                root.OnRead = null;
                writeException = Record.Exception(() => root.Children = new([foreign]));
            }
        };

        // Act
        var attachException = Record.Exception(() => root.AttachToContext(context));

        // Assert
        Assert.Null(attachException);
        Assert.IsType<InvalidOperationException>(writeException);
        Assert.Same(target, Assert.Single(root.Children));
        Assert.Same(context, target.TryGetContext());
        Assert.Equal(1, target.GetReferenceCount());
        Assert.Same(foreignContext, foreign.TryGetContext());
    }
}
