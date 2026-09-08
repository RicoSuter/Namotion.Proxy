using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class RejectedAttachOutsideSupportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenASeedFailureLeavesTheRootSupportedElsewhere_ThenItsInstalledChildrenRemainOwned(bool retry)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var outside = new CallbackSpikeNode(context);
        var completed = new CallbackSpikeNode();
        var pending = new CallbackSpikeNode();
        var trigger = new CallbackSpikeSegmentWrapper { Children = new([pending]) };
        var root = new CallbackSpikeNode { Payload = new object[] { completed, trigger } };
        var failure = new InvalidOperationException("seed failed after gaining outside support");
        var triggered = false;
        trigger.OnRead = () =>
        {
            if (triggered || trigger.GetReferenceCount() == 0) return;
            triggered = true;
            outside.Payload = root;
            throw failure;
        };

        // Act
        var exception = Record.Exception(() => root.AttachToContext(context));
        trigger.OnRead = null;
        if (retry) root.Payload = root.Payload;

        // Assert
        Assert.True(triggered);
        Assert.Same(failure, exception);
        Assert.Same(context, completed.TryGetContext());
        Assert.Same(context, trigger.TryGetContext());
        Assert.Equal(1, completed.GetReferenceCount());
        Assert.Equal(1, trigger.GetReferenceCount());
        Assert.Same(root, Assert.Single(completed.GetParents()).Property.Subject);
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)root).Executor.AttachmentAnchor);
        var registry = context.GetService<ISubjectRegistry>();
        Assert.NotNull(registry.TryGetRegisteredSubject(completed));
        Assert.NotNull(registry.TryGetRegisteredSubject(trigger));
        var registeredChildren = registry.TryGetRegisteredSubject(root)!.TryGetProperty(nameof(root.Payload))!.Children;
        Assert.Equal(new IInterceptorSubject[] { completed, trigger }, registeredChildren.Select(child => child.Subject));
        if (retry)
        {
            SupportContractAssertions.Settled(context, [outside], outside, root, completed, trigger, pending);
        }

        outside.Payload = null;
        SupportContractAssertions.Settled(context, [outside], outside, root, completed, trigger, pending);
        outside.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], outside, root, completed, trigger, pending);
    }

    [Fact]
    public void WhenARejectedRootHasABackEdgeFromARestoredProvisionalRoot_ThenItsInstalledChildrenRemainOwned()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var provisional = new CallbackSpikeNode(context);
        var trigger = new CallbackSpikeSegmentWrapper();
        var root = new CallbackSpikeNode { Payload = new object[] { provisional, trigger } };
        var failure = new InvalidOperationException("seed failed after adding a back edge");
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() == 0) return;
            trigger.OnRead = null;
            provisional.Child = root;
            throw failure;
        };

        // Act
        var exception = Record.Exception(() => root.AttachToContext(context));

        // Assert
        Assert.Same(failure, exception);
        Assert.Equal(SubjectAttachmentAnchorKind.Provisional, ((IInterceptorSubject)provisional).Executor.AttachmentAnchor);
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)root).Executor.AttachmentAnchor);
        Assert.Same(context, trigger.TryGetContext());
        Assert.Equal(1, provisional.GetReferenceCount());
        Assert.Equal(1, root.GetReferenceCount());
        Assert.Equal(1, trigger.GetReferenceCount());

        root.Payload = root.Payload;
        SupportContractAssertions.Settled(context, [provisional], provisional, root, trigger);
        provisional.Child = null;
        SupportContractAssertions.Settled(context, [provisional], provisional, root, trigger);
        provisional.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], provisional, root, trigger);
    }
}
