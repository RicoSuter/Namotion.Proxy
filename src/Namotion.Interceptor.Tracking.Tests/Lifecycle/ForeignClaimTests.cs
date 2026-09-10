using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Tests that a subject claim which collides with another context cannot leave the graph half
/// built: the losing side fails at its claim step, before any lifecycle state was mutated.
/// </summary>
public class ForeignClaimTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    [Fact]
    public void WhenAnotherContextClaimsSubjectDuringWrite_ThenCompetingClaimFailsAndWriteCompletes()
    {
        // Arrange: a handler reacting to the removal of the previous child tries to claim the
        // incoming child for a second context, exactly between the write's validation and the
        // point where the reconcile attaches the child.
        Exception? competingClaimException = null;
        var claimAttempted = false;
        var context2 = CreateContext();
        var candidate = new Person { FirstName = "C" };
        var previous = new Person { FirstName = "O" };

        var context1 = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (claimAttempted || !change.IsContextDetach || !ReferenceEquals(change.Subject, previous))
                {
                    return;
                }

                claimAttempted = true;
                try
                {
                    candidate.AttachToContext(context2);
                }
                catch (InvalidOperationException exception)
                {
                    competingClaimException = exception;
                }
            }), _ => false);

        var parent = new Person(context1) { FirstName = "P" };
        parent.Children = [previous];

        // Act
        var writeException = Record.Exception(() => parent.Children = new[] { candidate });

        // Assert: the write wins and completes; the competing claim is the one that fails; every
        // committed child carries a matching incoming edge and nothing hangs off the loser.
        Assert.Null(writeException);
        Assert.True(claimAttempted);
        Assert.NotNull(competingClaimException);
        Assert.Same(context1, candidate.TryGetContext());
        Assert.Equal(1, candidate.GetReferenceCount());
        Assert.Single(candidate.GetParents());
        Assert.Equal(0, previous.GetReferenceCount());
        Assert.Null(previous.TryGetContext());
    }

    [Fact]
    public void WhenAssignedSubtreeReferencesSubjectOfAnotherContext_ThenWriteIsRejectedBeforeCommit()
    {
        // Arrange: the candidate is unattached, so its own writes are not intercepted and it can
        // freely reference a subject that another context owns.
        var context1 = CreateContext();
        var context2 = CreateContext();
        var parent = new Person(context1) { FirstName = "P" };
        var foreign = new Person(context2) { FirstName = "F" };
        var candidate = new Person { FirstName = "C" };
        candidate.Father = foreign;

        // Act & Assert: the message is pinned because several InvalidOperationExceptions can fire
        // on this path and only this one means the cross-context discovery rejection.
        var exception = Assert.Throws<InvalidOperationException>(() => parent.Father = candidate);
        Assert.Contains("owned by a different context", exception.Message);
        Assert.Null(parent.Father);
        Assert.Null(candidate.TryGetContext());
        Assert.Equal(0, candidate.GetReferenceCount());
        Assert.Empty(candidate.GetParents());
        Assert.Same(context2, foreign.TryGetContext());
    }

    [Fact]
    public void WhenAttachedSubjectSubtreeReferencesSubjectOfAnotherContext_ThenAttachFailsWithoutResidue()
    {
        // Arrange
        var context1 = CreateContext();
        var context2 = CreateContext();
        var foreign = new Person(context2) { FirstName = "F" };
        var subject = new Person { FirstName = "S" };
        subject.Father = foreign;

        // Act & Assert: the attach is rejected without claiming the subject or its subtree.
        Assert.Throws<InvalidOperationException>(() => subject.AttachToContext(context1));
        Assert.Null(subject.TryGetContext());
        Assert.Equal(0, subject.GetReferenceCount());
        Assert.Same(context2, foreign.TryGetContext());
    }

    [Fact]
    public void WhenAFailedAttachIsRetriedWithoutTheForeignSubject_ThenItSucceeds()
    {
        // Arrange: a rejected attach must leave no residue behind on the subject it refused.
        var context1 = CreateContext();
        var context2 = CreateContext();
        var foreign = new Person(context2) { FirstName = "F" };
        var subject = new Person { FirstName = "S" };
        subject.Father = foreign;
        Assert.Throws<InvalidOperationException>(() => subject.AttachToContext(context1));

        // Act
        subject.Father = null;
        subject.AttachToContext(context1);

        // Assert
        Assert.Same(context1, subject.TryGetContext());
    }
}
