using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class SupportFailureBoundaryV2Tests
{
    [Fact]
    public void WhenOnlyOneQueuedCallbackFails_ThenOriginalExceptionIsPreservedAfterSettlement()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var oldChild = new Person();
        var parent = new Person(context) { Father = oldChild };
        var newChild = new Person();
        var failure = new InvalidOperationException("detach callback");
        context.TryGetLifecycleInterceptor()!.SubjectDetaching += change =>
        {
            if (ReferenceEquals(change.Subject, oldChild)) throw failure;
        };

        // Act
        var exception = Record.Exception(() => parent.Father = newChild);

        // Assert
        Assert.Same(failure, exception);
        Assert.Same(newChild, parent.Father);
        Assert.Same(context, newChild.TryGetContext());
        Assert.Null(oldChild.TryGetContext());
    }

    [Fact]
    public void WhenTheWriteAndQueuedCallbackFail_ThenNeitherExceptionIsLost()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var oldChild = new Person();
        var parent = new Person(context) { Father = oldChild };
        var newChild = new Person();
        var writeFailure = new InvalidOperationException("downstream write");
        var callbackFailure = new InvalidOperationException("detach callback");
        context.WithService(() => new FailAfterCommit(writeFailure));
        context.TryGetLifecycleInterceptor()!.SubjectDetaching += change =>
        {
            if (ReferenceEquals(change.Subject, oldChild)) throw callbackFailure;
        };

        // Act
        var exception = Record.Exception(() => parent.Father = newChild);

        // Assert
        var failures = Assert.IsType<AggregateException>(exception).Flatten().InnerExceptions;
        Assert.Collection(failures,
            error => Assert.Same(writeFailure, error),
            error => Assert.Same(callbackFailure, error));
        Assert.Same(newChild, parent.Father);
        Assert.Same(context, newChild.TryGetContext());
        Assert.Null(oldChild.TryGetContext());
    }

    [Fact]
    public void WhenSeedingAndQueuedCallbackFail_ThenBothErrorsEscapeAndRejectedGraphIsReleased()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var child = new CallbackSpikeNode();
        var failingChild = new CallbackSpikeSegmentWrapper();
        var root = new CallbackSpikeNode { Payload = new object[] { child, failingChild } };
        var seedFailure = new InvalidOperationException("seeding getter");
        var callbackFailure = new InvalidOperationException("queued attach callback");
        failingChild.OnRead = () =>
        {
            if (failingChild.TryGetContext() is not null) throw seedFailure;
        };
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, child)) throw callbackFailure;
        };

        // Act
        var exception = Record.Exception(() => root.AttachToContext(context));
        failingChild.OnRead = null;

        // Assert
        var failures = Assert.IsType<AggregateException>(exception).Flatten().InnerExceptions;
        Assert.Collection(failures,
            error => Assert.Same(seedFailure, error),
            error => Assert.Same(callbackFailure, error));
        Assert.Null(root.TryGetContext());
        Assert.Null(child.TryGetContext());
        Assert.Null(failingChild.TryGetContext());
    }

    [Fact]
    public void WhenAClaimedChildIsProjectedBeforeReconciliation_ThenItIsNotRejectedAsDeparting()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var subject = new SupportProjectionV2Node(context);
        var child = new Person();
        context.WithService(() => new AfterTargetCommit(subject, nameof(SupportProjectionV2Node.Child),
            () => subject.Refresh++));

        // Act
        var exception = Record.Exception(() => subject.Child = child);

        // Assert
        Assert.Null(exception);
        Assert.Same(child, subject.Projected);
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenWriteReconciliationAndCallbackAllFail_ThenThePrimaryFailureGroupStaysFirst()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var subject = new CallbackSpikeSegmentWrapper();
        subject.AttachToContext(context);
        var otherRoot = new CallbackSpikeNode(context);
        var callbackChild = new CallbackSpikeNode();
        var writeFailure = new InvalidOperationException("downstream write");
        var reconciliationFailure = new InvalidOperationException("authoritative getter");
        var callbackFailure = new InvalidOperationException("queued callback");
        context.WithService(() => new AfterTargetCommit(subject, nameof(CallbackSpikeSegmentWrapper.Children), () =>
        {
            otherRoot.Child = callbackChild;
            subject.OnRead = () => throw reconciliationFailure;
            throw writeFailure;
        }));
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, callbackChild)) throw callbackFailure;
        };

        // Act
        var exception = Record.Exception(() => subject.Children = new([new CallbackSpikeNode()]));
        subject.OnRead = null;

        // Assert
        var aggregate = Assert.IsType<AggregateException>(exception);
        Assert.Equal(2, aggregate.InnerExceptions.Count);
        var primary = Assert.IsType<AggregateException>(aggregate.InnerExceptions[0]);
        Assert.Collection(primary.InnerExceptions,
            error => Assert.Same(writeFailure, error),
            error => Assert.Same(reconciliationFailure, error));
        Assert.Same(callbackFailure, aggregate.InnerExceptions[1]);
        Assert.Same(context, callbackChild.TryGetContext());
    }

    [RunsAfter(typeof(LifecycleInterceptor))]
    private sealed class AfterTargetCommit(IInterceptorSubject subject, string propertyName, Action continuation) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            if (ReferenceEquals(context.Property.Subject, subject) && context.Property.Name == propertyName) continuation();
        }
    }

    [RunsAfter(typeof(LifecycleInterceptor))]
    private sealed class FailAfterCommit(Exception failure) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            throw failure;
        }
    }
}

[InterceptorSubject]
public partial class SupportProjectionV2Node
{
    public partial Person? Child { get; set; }
    public partial int Refresh { get; set; }

    [Derived]
    public Person? Projected
    {
        get
        {
            _ = Refresh;
            return Child;
        }
    }
}
