using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Interceptors;
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
    [Fact]
    public void WhenDetachCallbackExplicitlyAttachesDepartingSubject_ThenItOwnsItsDescendantsAsARoot()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var descendant = new Person();
        var child = new Person { Father = descendant };
        var parent = new Person(context) { Father = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var resurrected = false;
        lifecycle.SubjectDetaching += change =>
        {
            if (!ReferenceEquals(change.Subject, child) || resurrected) return;
            resurrected = true;
            child.AttachToContext(context);
            Assert.True(lifecycle.Graph.IsOwned(child));
            Assert.True(lifecycle.Graph.IsOwned(descendant));
            Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)child).Executor.AttachmentAnchor);
        };

        // Act
        parent.Father = null;

        // Assert
        Assert.True(resurrected);
        Assert.Null(parent.Father);
        Assert.Same(context, child.GetContext());
        Assert.Same(context, descendant.GetContext());
        Assert.NotNull(child.TryGetRegisteredSubject());
        Assert.NotNull(descendant.TryGetRegisteredSubject());
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)child).Executor.AttachmentAnchor);
        Assert.Equal(1, descendant.GetReferenceCount());
        Assert.False(lifecycle.Graph.IsReleasing(child));
        Assert.False(lifecycle.Graph.IsReleasing(descendant));

        // Act
        child.DetachFromContext(context);

        // Assert
        Assert.Null(child.TryGetContext());
        Assert.Null(descendant.TryGetContext());
        Assert.Null(child.TryGetRegisteredSubject());
        Assert.Null(descendant.TryGetRegisteredSubject());
        Assert.False(lifecycle.Graph.IsOwned(child));
        Assert.False(lifecycle.Graph.IsOwned(descendant));
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)child).Executor.AttachmentAnchor);
    }

}
