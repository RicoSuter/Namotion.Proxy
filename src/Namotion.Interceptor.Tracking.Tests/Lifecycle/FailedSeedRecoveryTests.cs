using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class FailedSeedRecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void WhenAChildSeedFailsAfterItsIncomingEdge_ThenParentRetrySeedsTheRetainedDescendants(bool sameValue, bool nested)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new CallbackSpikeNode(context);
        var leaf = new CallbackSpikeNode();
        var pending = new CallbackSpikeNode();
        var completed = new CallbackSpikeNode();
        var attached = new List<IInterceptorSubject>();
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change => attached.Add(change.Subject);
        var trigger = new CallbackSpikeSegmentWrapper { Children = new([leaf]) };
        object retained = nested ? new CallbackSpikeNode { Payload = trigger } : trigger;
        if (nested) leaf.Child = (CallbackSpikeNode)retained;
        var value = new[] { completed, retained, pending };
        var failure = new InvalidOperationException("authoritative child getter failed");
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() > 0) throw failure;
        };

        // Act
        var exception = Record.Exception(() => root.Payload = value);
        trigger.OnRead = null;
        root.Payload = sameValue ? value : new[] { completed, retained, pending };

        // Assert
        Assert.Same(failure, exception);
        Assert.Same(context, leaf.TryGetContext());
        Assert.Equal(1, leaf.GetReferenceCount());
        Assert.Equal(1, pending.GetReferenceCount());
        Assert.Equal(1, trigger.GetReferenceCount());
        Assert.Single(attached, subject => ReferenceEquals(subject, completed));
        Assert.Single(attached, subject => ReferenceEquals(subject, trigger));
        Assert.Single(attached, subject => ReferenceEquals(subject, leaf));
        Assert.NotNull(context.GetService<ISubjectRegistry>().TryGetRegisteredSubject(leaf));
        root.DetachFromContext(context);
        Assert.Null(leaf.TryGetContext());
        Assert.Null(pending.TryGetContext());
        Assert.Null(trigger.TryGetContext());
    }
    [Fact]
    public void WhenOriginalFailedAttachEdgeIsRemoved_ThenRetryUsesOnlyTheIndependentSupport()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var originalRoot = new CallbackSpikeNode(context);
        var retainedRoot = new CallbackSpikeNode(context);
        var leaf = new CallbackSpikeNode();
        var trigger = new CallbackSpikeSegmentWrapper { Children = new([leaf]) };
        var failure = new InvalidOperationException("repeated seed failure");
        trigger.OnRead = () => { if (trigger.GetReferenceCount() > 0) throw failure; };
        var attaches = 0;
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, trigger)) attaches++;
        };

        // Act
        var first = Record.Exception(() => originalRoot.Payload = trigger);
        var second = Record.Exception(() => retainedRoot.Payload = trigger);
        originalRoot.Payload = null;
        trigger.OnRead = null;
        retainedRoot.Payload = trigger;

        // Assert
        Assert.Same(failure, first);
        Assert.Same(failure, second);
        Assert.Equal(1, attaches);
        Assert.Same(retainedRoot, Assert.Single(trigger.GetParents()).Property.Subject);
        var registered = context.GetService<ISubjectRegistry>().TryGetRegisteredSubject(trigger)!;
        Assert.Same(retainedRoot, Assert.Single(registered.Parents).Property.Reference.Subject);
        Assert.Equal(1, leaf.GetReferenceCount());
        retainedRoot.Payload = null;
        Assert.Null(trigger.TryGetContext());
        Assert.Null(leaf.TryGetContext());
    }

    [Fact]
    public void WhenASeedGetterThrows_ThenItsOriginalHandlerAndPropertyHistoryCompletesOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new CallbackSpikeNode(context);
        var trigger = new CallbackSpikeSegmentWrapper { Children = new([new CallbackSpikeNode()]) };
        var calls = new List<string>();
        context.AddService<ILifecycleHandler>(new AfterSeedProbe(trigger, calls));
        context.AddService<ILifecycleHandler>(new BeforeSeedProbe(trigger, calls));
        context.AddService<IPropertyLifecycleHandler>(new SeedPropertyProbe(trigger, calls));
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, trigger)) calls.Add("event");
        };
        var failure = new InvalidOperationException("seed getter failure");
        trigger.OnRead = () => { if (trigger.GetReferenceCount() > 0) throw failure; };

        // Act
        var exception = Record.Exception(() => root.Payload = trigger);

        // Assert
        Assert.Same(failure, exception);
        Assert.Equal(new[] { "before", "after", "event", "property" }, calls);

        // Act
        trigger.OnRead = null;
        root.Payload = trigger;

        // Assert
        Assert.Equal(new[] { "before", "after", "event", "property" }, calls);
        Assert.Equal(1, trigger.Children[0].GetReferenceCount());
        root.Payload = null;
        Assert.Null(trigger.TryGetContext());
    }

    [RunsBefore(typeof(LifecycleInterceptor))]
    private sealed class BeforeSeedProbe(IInterceptorSubject target, List<string> calls) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach && ReferenceEquals(change.Subject, target)) calls.Add("before");
        }
    }

    [RunsAfter(typeof(LifecycleInterceptor))]
    private sealed class AfterSeedProbe(IInterceptorSubject target, List<string> calls) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach && ReferenceEquals(change.Subject, target)) calls.Add("after");
        }
    }

    private sealed class SeedPropertyProbe(IInterceptorSubject target, List<string> calls) : IPropertyLifecycleHandler
    {
        public void AttachProperty(SubjectPropertyLifecycleChange change)
        {
            if (ReferenceEquals(change.Subject, target)) calls.Add("property");
        }
        public void DetachProperty(SubjectPropertyLifecycleChange change) { }
    }

}
