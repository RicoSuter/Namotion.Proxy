using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class PropertyEdgeJournalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenInitialSeedingReplacesPendingDuplicates_ThenOnlyTheLatestOccurrencesAreOwned(bool retainPending)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var pending = new CallbackSpikeNode();
        var replacement = new CallbackSpikeNode();
        var trigger = new CallbackSpikeSegmentWrapper();
        var branch = new CallbackSpikeNode { Payload = trigger };
        var root = new CallbackSpikeNode { Payload = new[] { branch, pending, pending } };
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() == 0) return;
            trigger.OnRead = null;
            root.Payload = retainPending ? new[] { pending, pending } : replacement;
        };

        // Act
        root.AttachToContext(context);

        // Assert
        Assert.Null(branch.TryGetContext());
        Assert.Null(trigger.TryGetContext());
        Assert.Equal(retainPending ? 2 : 0, pending.GetReferenceCount());
        Assert.Equal(retainPending ? 0 : 1, replacement.GetReferenceCount());
        if (retainPending) Assert.Equal(new object?[] { 0, 1 }, pending.GetParents().Select(parent => parent.Index));
        root.DetachFromContext(context);
        Assert.Null(pending.TryGetContext());
        Assert.Null(replacement.TryGetContext());
        AssertNoJournal(context, root);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenPublicationFailsBeforePendingEdges_ThenRetryOrClearConsumesThePartialJournal(bool retry)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new CallbackSpikeNode(context);
        var pending = new CallbackSpikeNode();
        var trigger = new CallbackSpikeSegmentWrapper();
        var value = new object[] { trigger, pending, pending };
        var failure = new InvalidOperationException("seed failed after incoming publication");
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() > 0) throw failure;
        };

        // Act
        var exception = Record.Exception(() => root.Payload = value);
        trigger.OnRead = null;
        root.Payload = retry ? value : null;

        // Assert
        Assert.Same(failure, exception);
        Assert.Equal(retry ? 2 : 0, pending.GetReferenceCount());
        if (retry) Assert.Equal(new object?[] { 1, 2 }, pending.GetParents().Select(parent => parent.Index));
        AssertNoJournal(context, root);
        root.DetachFromContext(context);
        Assert.Null(trigger.TryGetContext());
        Assert.Null(pending.TryGetContext());
        AssertNoJournal(context, root);
    }

    [Fact]
    public void WhenAGetterReattachesItsWritingOwner_ThenTheOlderJournalCannotMutateTheNewEpoch()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new CallbackSpikeNode();
        root.AttachToContext(context);
        var pending = new CallbackSpikeNode();
        var replacement = new CallbackSpikeNode();
        var trigger = new CallbackSpikeSegmentWrapper();
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() == 0) return;
            trigger.OnRead = null;
            root.DetachFromContext(context);
            root.Payload = replacement;
            root.AttachToContext(context);
        };

        // Act
        root.Payload = new object[] { trigger, pending, pending };

        // Assert
        Assert.Same(replacement, root.Payload);
        Assert.Equal(1, replacement.GetReferenceCount());
        Assert.Null(trigger.TryGetContext());
        Assert.Null(pending.TryGetContext());
        AssertNoJournal(context, root);
        root.Payload = pending;
        Assert.Null(replacement.TryGetContext());
        Assert.Equal(1, pending.GetReferenceCount());
        root.DetachFromContext(context);
        Assert.Null(pending.TryGetContext());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenTwoNestedGettersSupersedePendingDuplicates_ThenTheLastOccurrenceOrderSurvives(bool throwAfterNested)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new CallbackSpikeNode(context);
        var pending = new CallbackSpikeNode();
        var first = new CallbackSpikeSegmentWrapper();
        var second = new CallbackSpikeSegmentWrapper();
        var failure = new InvalidOperationException("outer getter failed after both nested writes");
        second.OnRead = () =>
        {
            if (second.GetReferenceCount() == 0) return;
            second.OnRead = null;
            root.Payload = new[] { pending, pending };
        };
        first.OnRead = () =>
        {
            if (first.GetReferenceCount() == 0) return;
            first.OnRead = null;
            root.Payload = new object[] { pending, second, pending };
            if (throwAfterNested) throw failure;
        };

        // Act
        var exception = Record.Exception(() => root.Payload = new object[] { first, pending, pending });

        // Assert
        Assert.Same(throwAfterNested ? failure : null, exception);
        Assert.Equal(2, pending.GetReferenceCount());
        Assert.Equal(new object?[] { 0, 1 }, pending.GetParents().Select(parent => parent.Index));
        Assert.Null(first.TryGetContext());
        Assert.Null(second.TryGetContext());
        AssertNoJournal(context, root);
        root.DetachFromContext(context);
        Assert.Null(pending.TryGetContext());
    }

    private static void AssertNoJournal(IInterceptorSubjectContext context, CallbackSpikeNode root)
    {
        var graph = context.TryGetLifecycleInterceptor()!.Graph;
        Assert.Null(graph.GetPropertyJournal(new PropertyReference(root, nameof(root.Payload)), graph.TryGetOwnership(root)));
    }
}
