using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Tests.Change;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

[Collection(PerPropertySubscriptionCollection.Name)]
public class CallbackPublicationSpikeTests
{
    public CallbackPublicationSpikeTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenAttachCallbackWritesNewChildBeforePropertyAdmission_ThenObserverRunsAfterRegistration()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new Person(context);
        var child = new Person();
        var grandchild = new Person();
        var events = new List<string>();
        using var subscription = new PropertyReference(grandchild, nameof(Person.FirstName))
            .SubscribeInline((in SubjectPropertyChange change) =>
            {
                Assert.NotNull(grandchild.TryGetRegisteredSubject());
                Assert.Same(context, grandchild.GetContext());
                Assert.Equal("initialized", change.GetNewValue<string>());
                events.Add("scalar-observer");
            });
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, child))
            {
                events.Add("child-callback-start");
                child.Father = grandchild;
                grandchild.FirstName = "initialized";
                events.Add("child-callback-end");
            }
            if (ReferenceEquals(change.Subject, grandchild)) events.Add("grandchild-attached");
        };

        // Act
        root.Father = child;

        // Assert
        Assert.Equal(["child-callback-start", "child-callback-end", "grandchild-attached", "scalar-observer"], events);
    }

    [Fact]
    public void WhenAttachCallbackRemovesChild_ThenPropertyObserverRunsAfterDepartureAndDoesNotTrackScalarWrite()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var departing = new Person();
        var root = new Person(context) { Father = departing };
        var arriving = new Person();
        var events = new List<string>();
        using var scalarSubscription = new PropertyReference(departing, nameof(Person.FirstName))
            .SubscribeInline((in SubjectPropertyChange _) => events.Add("departing-scalar"));
        using var structuralSubscription = new PropertyReference(root, nameof(Person.Father))
            .SubscribeInline((in SubjectPropertyChange change) =>
            {
                Assert.Null(departing.TryGetContext());
                Assert.Null(departing.TryGetRegisteredSubject());
                departing.FirstName = "departed";
                events.Add("structural-observer");
            });
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, arriving))
            {
                events.Add("attach-callback-start");
                root.Father = null;
                events.Add("attach-callback-end");
            }
        };
        context.TryGetLifecycleInterceptor()!.SubjectDetaching += change =>
        {
            if (ReferenceEquals(change.Subject, departing))
            {
                Assert.Same(context, departing.GetContext());
                events.Add("departing-detach");
            }
        };

        // Act
        root.Mother = arriving;

        // Assert
        Assert.Equal(["attach-callback-start", "attach-callback-end", "departing-detach", "structural-observer"], events);
        Assert.Equal("departed", departing.FirstName);
    }
}
