using System.Collections;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CapturedBaselineTests
{
    [Fact]
    public void WhenACommittedEnumerableRefusesAnotherRead_ThenReplacementUsesItsCapturedOldOccurrences()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new CallbackSpikeNode(context);
        var old = new CallbackSpikeNode();
        var replacement = new CallbackSpikeNode();
        var sequence = new CaptureOnlySequence(old);
        root.Payload = sequence;
        var reads = sequence.Reads;
        sequence.RejectReads = true;

        // Act
        var exception = Record.Exception(() => root.Payload = replacement);

        // Assert
        Assert.Null(exception);
        Assert.Equal(reads, sequence.Reads);
        Assert.Null(old.TryGetContext());
        Assert.Equal(1, replacement.GetReferenceCount());
        root.Payload = null;
        Assert.Null(replacement.TryGetContext());
    }

    [Fact]
    public void WhenACommittedEnumerableRefusesAnotherRead_ThenReachabilityUsesCapturedDesiredMembership()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new CallbackSpikeNode(context);
        var otherRoot = new CallbackSpikeNode(context);
        var shared = new CallbackSpikeNode();
        var sequence = new CaptureOnlySequence(shared);
        root.Payload = sequence;
        otherRoot.Child = shared;
        var reads = sequence.Reads;
        sequence.RejectReads = true;

        // Act
        var exception = Record.Exception(() => otherRoot.Child = null);

        // Assert
        Assert.Null(exception);
        Assert.Equal(reads, sequence.Reads);
        Assert.Same(context, shared.TryGetContext());
        Assert.Equal(1, shared.GetReferenceCount());
        root.Payload = null;
        Assert.Null(shared.TryGetContext());
    }

    [Fact]
    public void WhenParentRetryResumesAFailedSeed_ThenItsCommittedCollectionIsNotCapturedAgain()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new CallbackSpikeNode(context);
        var leaf = new CallbackSpikeNode();
        var pending = new CallbackSpikeNode();
        var trigger = new CallbackSpikeSegmentWrapper { Children = new([leaf]) };
        var sequence = new CaptureOnlySequence(trigger, pending);
        var branch = new CallbackSpikeNode { Payload = sequence };
        var failure = new InvalidOperationException("descendant seeding failed");
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() == 0) return;
            trigger.OnRead = null;
            sequence.RejectReads = true;
            throw failure;
        };

        // Act
        var exception = Record.Exception(() => root.Payload = branch);
        var reads = sequence.Reads;
        root.Payload = branch;

        // Assert
        Assert.Same(failure, exception);
        Assert.Equal(reads, sequence.Reads);
        Assert.Equal(1, leaf.GetReferenceCount());
        Assert.Equal(1, pending.GetReferenceCount());
        root.Payload = null;
        Assert.Null(branch.TryGetContext());
        Assert.Null(trigger.TryGetContext());
        Assert.Null(leaf.TryGetContext());
        Assert.Null(pending.TryGetContext());
    }

    private sealed class CaptureOnlySequence(params IInterceptorSubject[] subjects) : IEnumerable<IInterceptorSubject>
    {
        public int Reads { get; private set; }
        public bool RejectReads { get; set; }

        public IEnumerator<IInterceptorSubject> GetEnumerator()
        {
            Reads++;
            if (RejectReads) throw new InvalidOperationException("committed storage was enumerated again");
            return ((IEnumerable<IInterceptorSubject>)subjects).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
