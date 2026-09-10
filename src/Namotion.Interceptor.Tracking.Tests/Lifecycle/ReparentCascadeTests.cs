using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Reparenting a subject that this context already owns. The capture taken before the terminal runs
/// skips a subject that is already attached here, so the incoming value contributes nothing to it,
/// and the removal pass then cascades through the edge being replaced and reaches that same subject
/// and everything below it. What re-attaches the subtree afterwards is therefore not the capture.
/// </summary>
public class ReparentCascadeTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    /// <summary>
    /// Passes on the current branch and is a regression guard rather than a repro. The subtree below
    /// the reparent target survives only because the descent that runs after the new edge is
    /// published re-reads the target's own structural getters, which rediscovers a grandchild that
    /// nothing captured and nothing else re-attaches.
    ///
    /// The precondition that makes this shape reachable is asserted rather than assumed: the target
    /// is already owned by this context before the write, which is exactly why the capture skips it.
    /// A design that replaced the post-publication descent with a replay of the captured set would
    /// replay nothing here, and the grandchild would be left detached while still referenced by a
    /// committed property.
    /// </summary>
    [Fact]
    public void WhenAReparentTargetIsAlreadyOwned_ThenTheSubtreeBelowItSurvivesTheCascade()
    {
        // Arrange: a three level chain hanging off the edge that is about to be replaced.
        var context = CreateContext();
        var root = new Person { FirstName = "R" };
        ((IInterceptorSubject)root).AttachToContext(context);
        var stepchild = new Person { FirstName = "S" };
        var child = new Person { FirstName = "C" };
        var grandchild = new Person { FirstName = "G" };

        root.Mother = stepchild;
        stepchild.Father = child;
        child.Father = grandchild;

        // The reparent target is already owned here, so the capture taken before the terminal runs
        // contributes nothing for it or for anything below it.
        Assert.Same(context, ((IInterceptorSubject)child).TryGetContext());
        Assert.Same(context, ((IInterceptorSubject)grandchild).TryGetContext());

        // Act: replace the edge that holds the whole chain with an edge to its middle.
        root.Mother = child;

        // Assert: the subject that lost its support is gone, and everything the committed graph
        // still reaches is still owned and still counted.
        Assert.Null(((IInterceptorSubject)stepchild).TryGetContext());
        Assert.Same(context, ((IInterceptorSubject)child).TryGetContext());
        Assert.Equal(1, ((IInterceptorSubject)child).GetReferenceCount());

        // The grandchild is the one this test exists for: it is referenced only through the target's
        // own property, so nothing outside the target's subtree can re-establish it.
        Assert.Same(grandchild, child.Father);
        Assert.True(((IInterceptorSubject)grandchild).TryGetContext() is not null,
            "the cascade released the subtree below the reparent target and nothing re-attached it, " +
            "so a committed property still references a subject this context no longer owns");
        Assert.Equal(1, ((IInterceptorSubject)grandchild).GetReferenceCount());
    }
}
