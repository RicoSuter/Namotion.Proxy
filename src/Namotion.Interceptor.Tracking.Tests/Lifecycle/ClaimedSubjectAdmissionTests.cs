using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class ClaimedSubjectAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAGetterAddsAPropertyToAnUnpublishedClaim_ThenOnlyPublishedOwnersAttachItsChild(bool retainOwner)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new CallbackSpikeNode(context);
        var trigger = new CallbackSpikeSegmentWrapper();
        var pending = new Tire();
        var child = new CallbackSpikeNode();
        var ran = false;
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() == 0) return;
            trigger.OnRead = null;
            ran = true;
            Assert.Same(context, pending.TryGetContext());
            Assert.Equal(0, pending.GetReferenceCount());
            ((IInterceptorSubject)pending).AddProperties(new SubjectPropertyMetadata(
                "Extra", typeof(CallbackSpikeNode), [], _ => child, null,
                isIntercepted: true, isDynamic: true));
            root.Payload = retainOwner ? pending : null;
        };

        // Act
        root.Payload = new object[] { trigger, pending };

        // Assert
        Assert.True(ran);
        Assert.True(((IInterceptorSubject)pending).Properties.ContainsKey("Extra"));
        Assert.Same(retainOwner ? context : null, child.TryGetContext());
        Assert.Equal(retainOwner ? 1 : 0, child.GetReferenceCount());
        var registry = context.GetService<ISubjectRegistry>();
        Assert.Equal(retainOwner, registry.TryGetRegisteredSubject(child) is not null);
        root.Payload = null;
        Assert.Null(child.TryGetContext());
        Assert.Null(pending.TryGetContext());
        Assert.Null(registry.TryGetRegisteredSubject(child));

        // Act
        root.Payload = pending;

        // Assert
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
        root.DetachFromContext(context);
        Assert.Null(child.TryGetContext());
        Assert.Null(pending.TryGetContext());
    }
}
