using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

public class AttachmentTransitionTests
{
    private static IInterceptorExecutor CreateExecutor()
    {
        return ((IInterceptorSubject)new Car()).Executor;
    }

    [Fact]
    public void WhenExecutorIsFresh_ThenAttachmentStateIsEmpty()
    {
        // Arrange & Act
        var executor = CreateExecutor();

        // Assert
        Assert.Null(executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.None, executor.AttachmentAnchor);
        Assert.Equal(0, executor.AttachmentRevision);
    }

    [Fact]
    public void WhenExpectedRevisionMatches_ThenTransitionSucceedsAndBumpsRevision()
    {
        // Arrange
        var executor = CreateExecutor();
        var context = InterceptorSubjectContext.Create();
        var revisionBefore = executor.AttachmentRevision;

        // Act
        var success = executor.TryUpdateAttachment(revisionBefore, context, SubjectAttachmentAnchorKind.Provisional, out var currentRevision);

        // Assert
        Assert.True(success);
        Assert.Equal(revisionBefore + 1, currentRevision);
        Assert.Equal(currentRevision, executor.AttachmentRevision);
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.Provisional, executor.AttachmentAnchor);
    }

    [Fact]
    public void WhenExpectedRevisionIsStale_ThenTransitionFailsAndReportsCurrentRevision()
    {
        // Arrange
        var executor = CreateExecutor();
        var context = InterceptorSubjectContext.Create();
        var staleRevision = executor.AttachmentRevision;
        Assert.True(executor.TryUpdateAttachment(staleRevision, context, SubjectAttachmentAnchorKind.Explicit, out _));

        // Act
        var success = executor.TryUpdateAttachment(staleRevision, null, SubjectAttachmentAnchorKind.None, out var currentRevision);

        // Assert
        Assert.False(success);
        Assert.Equal(executor.AttachmentRevision, currentRevision);
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, executor.AttachmentAnchor);
    }

    [Theory]
    [InlineData(SubjectAttachmentAnchorKind.Provisional)]
    [InlineData(SubjectAttachmentAnchorKind.Explicit)]
    public void WhenContextIsNullWithAnAnchor_ThenTransitionThrowsBeforeAnyStateChange(SubjectAttachmentAnchorKind anchor)
    {
        // Arrange
        var executor = CreateExecutor();
        var revisionBefore = executor.AttachmentRevision;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => executor.TryUpdateAttachment(revisionBefore, null, anchor, out _));
        Assert.Equal(revisionBefore, executor.AttachmentRevision);
        Assert.Null(executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.None, executor.AttachmentAnchor);
    }

    [Fact]
    public void WhenSwappingDirectlyToADifferentContext_ThenTransitionThrowsBeforeAnyStateChange()
    {
        // Arrange
        var executor = CreateExecutor();
        var firstContext = InterceptorSubjectContext.Create();
        var secondContext = InterceptorSubjectContext.Create();
        Assert.True(executor.TryUpdateAttachment(executor.AttachmentRevision, firstContext, SubjectAttachmentAnchorKind.Explicit, out _));
        var revisionBefore = executor.AttachmentRevision;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => executor.TryUpdateAttachment(revisionBefore, secondContext, SubjectAttachmentAnchorKind.Explicit, out _));
        Assert.Equal(revisionBefore, executor.AttachmentRevision);
        Assert.Same(firstContext, executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, executor.AttachmentAnchor);
    }

    [Fact]
    public void WhenDetachingToNullAndReattaching_ThenRevisionIsMonotonicAndNeverResets()
    {
        // Arrange
        var executor = CreateExecutor();
        var firstContext = InterceptorSubjectContext.Create();
        var secondContext = InterceptorSubjectContext.Create();

        // Act & Assert: every successful transition bumps the revision, including across
        // detach and reattach, so revisions from before a detach stay comparable.
        Assert.True(executor.TryUpdateAttachment(0, firstContext, SubjectAttachmentAnchorKind.Explicit, out var afterAttach));
        Assert.Equal(1, afterAttach);

        Assert.True(executor.TryUpdateAttachment(afterAttach, null, SubjectAttachmentAnchorKind.None, out var afterDetach));
        Assert.Equal(2, afterDetach);

        Assert.True(executor.TryUpdateAttachment(afterDetach, secondContext, SubjectAttachmentAnchorKind.Provisional, out var afterReattach));
        Assert.Equal(3, afterReattach);

        Assert.Equal(3, executor.AttachmentRevision);
        Assert.Same(secondContext, executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.Provisional, executor.AttachmentAnchor);
    }

    [Fact]
    public void WhenTransitionAppliesIdenticalValues_ThenRevisionStillBumps()
    {
        // Arrange: a successful compare-and-swap always bumps, even without a field change,
        // so callers can rely on "revision unchanged" meaning "no transition attempt succeeded".
        var executor = CreateExecutor();
        var context = InterceptorSubjectContext.Create();
        Assert.True(executor.TryUpdateAttachment(0, context, SubjectAttachmentAnchorKind.None, out var firstRevision));

        // Act
        var success = executor.TryUpdateAttachment(firstRevision, context, SubjectAttachmentAnchorKind.None, out var secondRevision);

        // Assert
        Assert.True(success);
        Assert.Equal(firstRevision + 1, secondRevision);
    }

    [Fact]
    public void WhenSnapshotIsTakenBeforeAndAfterAttaching_ThenItIsCoherent()
    {
        // Arrange
        var executor = CreateExecutor();
        var context = InterceptorSubjectContext.Create();

        // Act
        var attachedBefore = executor.TryGetAttachment(out var contextBefore, out var anchorBefore, out var revisionBefore);
        Assert.True(executor.TryUpdateAttachment(revisionBefore, context, SubjectAttachmentAnchorKind.Provisional, out _));
        var attachedAfter = executor.TryGetAttachment(out var contextAfter, out var anchorAfter, out var revisionAfter);

        // Assert: the triple is read under one lock, so all three values belong together in
        // both snapshots.
        Assert.False(attachedBefore);
        Assert.Null(contextBefore);
        Assert.Equal(SubjectAttachmentAnchorKind.None, anchorBefore);
        Assert.Equal(0, revisionBefore);

        Assert.True(attachedAfter);
        Assert.Same(context, contextAfter);
        Assert.Equal(SubjectAttachmentAnchorKind.Provisional, anchorAfter);
        Assert.Equal(revisionBefore + 1, revisionAfter);
    }

    [Fact]
    public void WhenAttachmentIsUpdatedThroughTheSeam_ThenSubjectExtensionsObserveIt()
    {
        // Arrange: the seam is public interface surface, usable by an out-of-assembly
        // lifecycle implementation without reflection or friend access.
        var subject = new Car();
        var executor = ((IInterceptorSubject)subject).Executor;
        var context = InterceptorSubjectContext.Create();

        // Act
        Assert.True(executor.TryUpdateAttachment(executor.AttachmentRevision, context, SubjectAttachmentAnchorKind.Provisional, out _));

        // Assert
        Assert.Same(context, subject.TryGetContext());
        Assert.Same(context, subject.GetContext());
    }
}
