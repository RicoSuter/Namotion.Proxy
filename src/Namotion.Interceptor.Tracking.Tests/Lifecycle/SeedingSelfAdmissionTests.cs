using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class SeedingSelfAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenTheFirstSeedingGetterAdmitsAnotherStructuralProperty_ThenItsEdgesCommitAndRelease(bool duplicateChildren)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new CallbackSpikeNode(context);
        var host = new Tire();
        var child = new CallbackSpikeNode();
        var admitted = false;
        object extraValue = duplicateChildren ? new[] { child, child } : child;
        ((IInterceptorSubject)host).AddProperties(new SubjectPropertyMetadata(
            "Trigger", typeof(CallbackSpikeNode), [], _ =>
            {
                if (!admitted && host.GetReferenceCount() > 0)
                {
                    admitted = true;
                    ((IInterceptorSubject)host).AddProperties(new SubjectPropertyMetadata(
                        "Extra", typeof(object), [], _ => extraValue, null, isIntercepted: true, isDynamic: true));
                }

                return null;
            }, null, isIntercepted: true, isDynamic: true));

        // Act
        root.Payload = host;

        // Assert
        Assert.True(admitted);
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(duplicateChildren ? 2 : 1, child.GetReferenceCount());
        SupportContractAssertions.Settled(context, [root], root, host, child);

        // Act
        root.Payload = null;

        // Assert
        SupportContractAssertions.Settled(context, [root], root, host, child);
        root.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], root, host, child);
    }
}
