using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CallbackReleaseEpochSpikeTests
{
    [Fact]
    public void WhenDetachCallbackReattachesAndRemovesSameSubject_ThenEveryHistoricalCallbackRetainsContext()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person();
        var root = new Person(context) { Father = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var events = new List<string>();
        var detachCount = 0;
        lifecycle.SubjectDetaching += change =>
        {
            if (!ReferenceEquals(change.Subject, child)) return;
            Assert.Same(context, child.GetContext());
            Assert.True(lifecycle.Graph.IsReleasing(child));
            events.Add($"detach-{++detachCount}");
            if (detachCount == 1)
            {
                root.Father = child;
                root.Father = null;
                events.Add("nested-setters-returned");
            }
        };
        lifecycle.SubjectAttached += change =>
        {
            if (!ReferenceEquals(change.Subject, child)) return;
            Assert.Same(context, child.GetContext());
            events.Add("reattach");
        };

        // Act
        root.Father = null;

        // Assert
        Assert.Equal(["detach-1", "nested-setters-returned", "reattach", "detach-2"], events);
        Assert.Null(root.Father);
        Assert.Null(child.TryGetContext());
        Assert.Null(child.TryGetRegisteredSubject());
        Assert.Equal(0, child.GetReferenceCount());
        Assert.False(lifecycle.Graph.IsOwned(child));
        Assert.False(lifecycle.Graph.IsReleasing(child));
    }
}
