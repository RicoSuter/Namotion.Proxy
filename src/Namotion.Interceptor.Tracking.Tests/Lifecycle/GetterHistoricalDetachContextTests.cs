using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class GetterHistoricalDetachContextTests
{
    [Fact]
    public void WhenASeedingGetterReplacesItsPublishingProperty_ThenHistoricalDetachStillResolvesContext()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new CallbackSpikeNode(context);
        var trigger = new CallbackSpikeSegmentWrapper();
        var pending = new CallbackSpikeNode();
        var replacement = new CallbackSpikeNode();
        var detachCount = 0;
        context.TryGetLifecycleInterceptor()!.SubjectDetaching += change =>
        {
            if (!ReferenceEquals(change.Subject, trigger)) return;
            detachCount++;
            Assert.Same(context, trigger.GetContext());
        };
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() == 0) return;
            trigger.OnRead = null;
            root.Payload = replacement;
        };

        // Act
        root.Payload = new object[] { trigger, pending };

        // Assert
        Assert.Equal(1, detachCount);
        Assert.Same(replacement, root.Payload);
        Assert.Equal(1, replacement.GetReferenceCount());
        Assert.Null(trigger.TryGetContext());
        Assert.Null(pending.TryGetContext());
    }
}
