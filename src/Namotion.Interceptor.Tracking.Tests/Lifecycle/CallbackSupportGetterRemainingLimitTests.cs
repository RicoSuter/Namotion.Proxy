using System.Collections;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CallbackSupportGetterRemainingLimitTests
{
    [Fact]
    public void WhenUnpublishedRootEnumerableRewritesItsProperty_ThenOwnershipMatchesTheStoredValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new CallbackSpikeNode();
        var stale = new CallbackSpikeNode();
        var replacement = new CallbackSpikeNode();
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        var reentered = false;
        root.Payload = new HookEnumerable([stale], () =>
        {
            if (!reentered && root.TryGetContext() is not null && !lifecycle.Graph.IsOwned(root))
            {
                reentered = true;
                root.Payload = replacement;
            }
        });

        // Act
        root.AttachToContext(context);

        // Assert
        Assert.True(reentered);
        Assert.Same(replacement, root.Payload);
        Assert.Same(context, replacement.TryGetContext());
        Assert.Equal(1, replacement.GetReferenceCount());
        Assert.Null(stale.TryGetContext());
    }

    [Fact]
    public void WhenGetterReplacesAndRestoresTheSamePropertyValue_ThenOccurrencesAreNotDuplicated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new CallbackSpikeNode(context);
        var stale = new CallbackSpikeNode();
        var replacement = new CallbackSpikeNode();
        var trigger = new CallbackSpikeSegmentWrapper();
        var branch = new CallbackSpikeNode { Payload = trigger };
        var wrapper = new CallbackSpikeNode { Payload = new[] { branch, stale } };
        var originalValue = wrapper.Payload;
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() > 0)
            {
                trigger.OnRead = null;
                wrapper.Payload = replacement;
                wrapper.Payload = originalValue;
            }
        };

        // Act
        root.Payload = wrapper;

        // Assert
        Assert.Same(originalValue, wrapper.Payload);
        Assert.Equal(1, branch.GetReferenceCount());
        Assert.Equal(1, stale.GetReferenceCount());
        Assert.Null(replacement.TryGetContext());
    }

    private sealed class HookEnumerable(IEnumerable<CallbackSpikeNode> children, Action onEnumeration) : IEnumerable<CallbackSpikeNode>
    {
        public IEnumerator<CallbackSpikeNode> GetEnumerator()
        {
            onEnumeration();
            return children.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
