using Namotion.Interceptor.Tracking.Tests.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// What a lifecycle handler sees on the anchor of a subject that is being released, which is the
/// invariant <see cref="Namotion.Interceptor.Tracking.Parent.ParentsHandlerExtensions.GetParents"/> documents to consumers deciding root-ness
/// from inside a callback. Nothing else asserts the anchor from within a callback: the ownership
/// oracle compares post-state only.
/// </summary>
public class DetachAnchorVisibilityTests
{
    [Fact]
    public void WhenAReleasedChildIsObservedFromItsDetachCallback_ThenItCarriesNoAnchor()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var simulation = new Simulation(context) { Name = "Root" };
        var component = new Component { Name = "Child" };
        simulation.Component = component;

        SubjectAttachmentAnchorKind? anchorDuringDetach = null;
        context.AddService(new DelegateLifecycleHandler(change =>
        {
            if (ReferenceEquals(change.Subject, component) && !change.IsContextAttach)
            {
                anchorDuringDetach = change.Subject.Executor.AttachmentAnchor;
            }
        }));

        // Act
        simulation.Component = null;

        // Assert: ownership is dropped before any detach callback runs, but the anchor is what
        // decides root-ness, and an edge-held child never had one.
        Assert.Equal(SubjectAttachmentAnchorKind.None, anchorDuringDetach);
    }

    [Fact]
    public void WhenAnExplicitRootIsObservedFromItsDetachCallback_ThenItCarriesNoAnchor()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var simulation = new Simulation { Name = "Root" };
        simulation.AttachToContext(context);

        SubjectAttachmentAnchorKind? anchorDuringDetach = null;
        context.AddService(new DelegateLifecycleHandler(change =>
        {
            if (ReferenceEquals(change.Subject, simulation) && !change.IsContextAttach)
            {
                anchorDuringDetach = change.Subject.Executor.AttachmentAnchor;
            }
        }));

        // Act
        simulation.DetachFromContext(context);

        // Assert: the detach clears the anchor before releasing, so a handler never sees a departing
        // root reported as one, and it stays cleared once the detach returns.
        Assert.Equal(SubjectAttachmentAnchorKind.None, anchorDuringDetach);
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)simulation).Executor.AttachmentAnchor);
    }
}
