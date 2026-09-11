using System.Collections;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CommittedOccurrenceCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenReleasingCommittedOccurrences_ThenTheUserEnumerableIsNotReadAgain(bool hasCycle)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new CallbackSpikeNode();
        var child = new CallbackSpikeNode();
        var value = new ArmedEnumerable([child, child]);
        root.Payload = value;
        if (hasCycle) child.Child = root;
        root.AttachToContext(context);
        value.IsArmed = true;

        // Act
        var exception = Record.Exception(() => root.DetachFromContext(context));

        // Assert
        Assert.Null(exception);
        Assert.Equal(0, value.ArmedReads);
        value.IsArmed = false;
        SupportContractAssertions.Settled(context, [], root, child);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void WhenReplacingACommittedEnumerable_ThenOldOccurrencesDoNotRequireAnotherRead(int replacementKind)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var child = new CallbackSpikeNode();
        var replacement = new CallbackSpikeNode();
        var value = new ArmedEnumerable([child, child]);
        var root = new CallbackSpikeNode { Payload = value };
        root.AttachToContext(context);
        value.IsArmed = true;

        // Act
        var exception = Record.Exception(() => root.Payload = replacementKind switch
        {
            0 => null,
            1 => replacement,
            _ => new[] { child, replacement, child }
        });

        // Assert
        Assert.Null(exception);
        Assert.Equal(0, value.ArmedReads);
        value.IsArmed = false;
        SupportContractAssertions.Settled(context, [root], root, child, replacement);
        root.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], root, child, replacement);
    }

    [Fact]
    public void WhenAdoptionChecksCommittedOutsideSupport_ThenReachabilityDoesNotReadTheUserEnumerableAgain()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var provisional = new CallbackSpikeNode(context);
        var owner = new CallbackSpikeNode();
        provisional.Child = owner;
        var value = new ArmedEnumerable([owner]);
        var outside = new CallbackSpikeNode { Payload = value };
        outside.AttachToContext(context);
        value.IsArmed = true;

        // Act
        var exception = Record.Exception(() => owner.Child = provisional);

        // Assert
        Assert.Null(exception);
        Assert.Equal(0, value.ArmedReads);
        value.IsArmed = false;
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)provisional).Executor.AttachmentAnchor);
        SupportContractAssertions.Settled(context, [outside], provisional, owner, outside);
        outside.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], provisional, owner, outside);
    }

    private sealed class ArmedEnumerable(CallbackSpikeNode[] subjects) : IEnumerable<CallbackSpikeNode>
    {
        public bool IsArmed { get; set; }
        public int ArmedReads { get; private set; }

        public IEnumerator<CallbackSpikeNode> GetEnumerator()
        {
            if (IsArmed)
            {
                ArmedReads++;
                throw new InvalidOperationException("a committed user enumerable was read again");
            }
            return ((IEnumerable<CallbackSpikeNode>)subjects).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
