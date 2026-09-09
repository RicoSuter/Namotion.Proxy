using System.Collections;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CallbackGraphOracleTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void WhenAChildGetterReassignsThePublishingProperty_ThenStorageAndAllIncomingOccurrencesAgree(
        bool retainPendingOccurrences, bool hasIndependentSupport, bool throwAfterNestedWrite)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new CallbackSpikeNode();
        root.AttachToContext(context);
        var pending = new CallbackSpikeNode();
        var shared = new CallbackSpikeNode { Child = pending };
        pending.Child = shared;
        if (hasIndependentSupport) root.Child = shared;
        var trigger = new CallbackSpikeSegmentWrapper();
        var branch = new CallbackSpikeNode { Payload = trigger };
        var replacement = new CallbackSpikeNode();
        object finalValue = retainPendingOccurrences ? new object[] { pending, pending, shared } : replacement;
        var failure = new InvalidOperationException("getter failed after the nested write committed");
        var reentered = false;
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() == 0) return;
            trigger.OnRead = null;
            reentered = true;
            root.Payload = finalValue;
            if (throwAfterNestedWrite) throw failure;
        };
        IInterceptorSubject[] subjects = [root, pending, shared, trigger, branch, replacement];

        // Act
        var exception = Record.Exception(() => root.Payload = new object[] { branch, pending, pending });
        trigger.OnRead = null;

        // Assert
        Assert.True(reentered);
        Assert.Same(throwAfterNestedWrite ? failure : null, exception);
        Assert.Same(finalValue, root.Payload);
        var errors = SupportContractAssertions.CompareStorageGraph(context, [root], subjects, "after replacement");

        // Act
        root.DetachFromContext(context);

        // Assert
        errors.AddRange(SupportContractAssertions.CompareStorageGraph(context, [], subjects, "after teardown"));
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void WhenADetachCallbackReassignsTheSameProperty_ThenSharedCyclesAndReattachmentEpochsSettle(
        bool reattachOldSubject, bool throwAfterNestedWrite)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var shared = new CallbackSpikeNode();
        var cycleFirst = new CallbackSpikeNode();
        var cycleSecond = new CallbackSpikeNode { Child = cycleFirst };
        cycleFirst.Child = cycleSecond;
        var old = new CallbackSpikeNode { Payload = new object[] { shared, cycleFirst, shared } };
        var intermediate = new CallbackSpikeNode { Payload = new object[] { shared, cycleSecond } };
        var replacement = new CallbackSpikeNode { Payload = new object[] { shared, shared, cycleFirst } };
        var final = reattachOldSubject ? old : replacement;
        var root = new CallbackSpikeNode { Child = shared, Payload = old };
        root.AttachToContext(context);
        var failure = new InvalidOperationException("detach callback failed after the nested write committed");
        var reentered = false;
        var transitions = new List<(IInterceptorSubject Subject, bool Attached)>();
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        lifecycle.SubjectAttached += change => transitions.Add((change.Subject, true));
        lifecycle.SubjectDetaching += change =>
        {
            transitions.Add((change.Subject, false));
            if (reentered || !ReferenceEquals(change.Subject, old)) return;
            reentered = true;
            root.Payload = final;
            if (throwAfterNestedWrite) throw failure;
        };
        IInterceptorSubject[] subjects = [root, old, intermediate, replacement, shared, cycleFirst, cycleSecond];

        // Act
        var exception = Record.Exception(() => root.Payload = intermediate);

        // Assert
        Assert.True(reentered);
        Assert.Same(throwAfterNestedWrite ? failure : null, exception);
        Assert.Same(final, root.Payload);
        var replacementTransitions = transitions.Where(change =>
            ReferenceEquals(change.Subject, old) || ReferenceEquals(change.Subject, intermediate) ||
            ReferenceEquals(change.Subject, replacement)).ToArray();
        Assert.Equal(new (IInterceptorSubject Subject, bool Attached)[]
        {
            (old, false), (intermediate, true), (intermediate, false), (final, true)
        }, replacementTransitions);
        var errors = SupportContractAssertions.CompareStorageGraph(context, [root], subjects, "after callback replacement");

        // Act
        root.DetachFromContext(context);

        // Assert
        errors.AddRange(SupportContractAssertions.CompareStorageGraph(context, [], subjects, "after teardown"));
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

}
