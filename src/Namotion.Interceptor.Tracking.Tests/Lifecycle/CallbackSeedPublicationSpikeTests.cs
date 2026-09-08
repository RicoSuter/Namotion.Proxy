using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Change;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

[Collection(PerPropertySubscriptionCollection.Name)]
public class CallbackSeedPublicationSpikeTests
{
    public CallbackSeedPublicationSpikeTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenSeedingGetterWritesItsOwnProperty_ThenObserverRunsAfterChildRegistration()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new CallbackSpikeNode();
        var root = new CallbackSpikeSegmentWrapper();
        var events = new List<string>();
        using var subscription = new PropertyReference(root, nameof(CallbackSpikeSegmentWrapper.Children))
            .SubscribeInline((in SubjectPropertyChange change) =>
            {
                Assert.Same(child, Assert.Single(change.GetNewValue<ArraySegment<CallbackSpikeNode>>()));
                Assert.NotNull(child.TryGetRegisteredSubject());
                Assert.Equal(1, child.GetReferenceCount());
                events.Add("property-observer");
            });
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, child)) events.Add("child-attached");
        };
        root.OnRead = () =>
        {
            if (root.TryGetContext() is null) return;
            root.OnRead = null;
            root.Children = new([child]);
            events.Add("setter-returned");
        };

        // Act
        root.AttachToContext(context);

        // Assert
        Assert.Equal(["setter-returned", "child-attached", "property-observer"], events);
    }

    [Fact]
    public void WhenObserversQueueMoreLifecycleWork_ThenEachPropertyPublicationWaitsForItAndKeepsWriteOrder()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new CallbackSpikeNode();
        var grandchild = new CallbackSpikeNode();
        var replacement = new CallbackSpikeNode();
        var root = new CallbackSpikeSegmentWrapper();
        var events = new List<string>();
        using var rootSubscription = new PropertyReference(root, nameof(CallbackSpikeSegmentWrapper.Children))
            .SubscribeInline((in SubjectPropertyChange _) =>
            {
                Assert.NotNull(child.TryGetRegisteredSubject());
                Assert.NotNull(grandchild.TryGetRegisteredSubject());
                events.Add("root-observer");
                child.Child = replacement;
            });
        using var childSubscription = new PropertyReference(child, nameof(CallbackSpikeNode.Child))
            .SubscribeInline((in SubjectPropertyChange change) =>
            {
                Assert.Same(replacement, child.Child);
                Assert.NotNull(replacement.TryGetRegisteredSubject());
                Assert.Null(grandchild.TryGetRegisteredSubject());
                events.Add(ReferenceEquals(change.GetNewValue<CallbackSpikeNode>(), grandchild)
                    ? "first-child-write" : "second-child-write");
            });
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        lifecycle.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, child))
            {
                events.Add("child-attached");
                child.Child = grandchild;
            }
            if (ReferenceEquals(change.Subject, grandchild)) events.Add("grandchild-attached");
            if (ReferenceEquals(change.Subject, replacement)) events.Add("replacement-attached");
        };
        lifecycle.SubjectDetaching += change =>
        {
            if (ReferenceEquals(change.Subject, grandchild)) events.Add("grandchild-detached");
        };
        root.OnRead = () =>
        {
            if (root.TryGetContext() is null) return;
            root.OnRead = null;
            root.Children = new([child]);
        };

        // Act
        root.AttachToContext(context);

        // Assert
        Assert.Equal(["child-attached", "grandchild-attached", "root-observer", "grandchild-detached", "replacement-attached", "first-child-write", "second-child-write"], events);
    }
    [Fact]
    public void WhenSeedingAndBothPublicationStreamsFail_ThenEveryFailureSurvivesInDeliveryOrder()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new CallbackSpikeNode();
        var root = new CallbackSpikeSegmentWrapper();
        var seedFailure = new InvalidOperationException("seeding getter");
        var lifecycleFailure = new InvalidOperationException("detach callback");
        var propertyFailure = new InvalidOperationException("property observer");
        using var subscription = new PropertyReference(root, nameof(CallbackSpikeSegmentWrapper.Children))
            .SubscribeInline((in SubjectPropertyChange _) => throw propertyFailure);
        context.TryGetLifecycleInterceptor()!.SubjectDetaching += change =>
        {
            if (ReferenceEquals(change.Subject, root)) throw lifecycleFailure;
        };
        root.OnRead = () =>
        {
            if (root.TryGetContext() is null) return;
            root.OnRead = null;
            root.Children = new([child]);
            throw seedFailure;
        };

        // Act
        var exception = Record.Exception(() => root.AttachToContext(context));

        // Assert
        Assert.Collection(Assert.IsType<AggregateException>(exception).InnerExceptions,
            failure => Assert.Same(seedFailure, failure),
            failure => Assert.Same(lifecycleFailure, failure),
            failure => Assert.Same(propertyFailure, failure));
        Assert.Null(root.TryGetContext());
        Assert.Null(child.TryGetContext());
        Assert.Null(root.TryGetRegisteredSubject());
        Assert.Null(child.TryGetRegisteredSubject());
    }

}
