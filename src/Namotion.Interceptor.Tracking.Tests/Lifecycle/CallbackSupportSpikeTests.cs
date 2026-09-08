using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CallbackSupportSpikeTests
{
    [Fact]
    public void WhenAttachCallbackInitializesStructuralProperty_ThenChildIsOwnedBeforeNestedSetterReturns()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new Person(context);
        var child = new Person();
        var grandchild = new Person();
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var nestedReturned = false;
        lifecycle.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, child))
            {
                child.Father = grandchild;
                Assert.Same(context, grandchild.TryGetContext());
                Assert.Equal(1, grandchild.GetReferenceCount());
                nestedReturned = true;
            }
        };

        // Act
        root.Father = child;

        // Assert
        Assert.True(nestedReturned);
        Assert.Same(grandchild, child.Father);
        Assert.Equal(1, grandchild.GetReferenceCount());
    }

    [Fact]
    public void WhenDetachCallbackReplacesSameProperty_ThenTransitionsAreFifoAndFinalEdgesAgree()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var oldChild = new Person();
        var root = new Person(context) { Father = oldChild };
        var intermediate = new Person();
        var final = new Person();
        var events = new List<string>();
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        lifecycle.SubjectDetaching += change =>
        {
            if (ReferenceEquals(change.Subject, oldChild))
            {
                events.Add("old-detach");
                Assert.Same(intermediate, root.Father);
                Assert.Equal(1, intermediate.GetReferenceCount());
                root.Father = final;
                Assert.Same(final, root.Father);
                Assert.Equal(1, final.GetReferenceCount());
            }
            else if (ReferenceEquals(change.Subject, intermediate)) events.Add("intermediate-detach");
        };
        lifecycle.SubjectAttached += change => events.Add(ReferenceEquals(change.Subject, intermediate) ? "intermediate-attach" : "final-attach");

        // Act
        root.Father = intermediate;

        // Assert
        Assert.Equal(new[] { "old-detach", "intermediate-attach", "intermediate-detach", "final-attach" }, events);
        Assert.Same(final, root.Father);
        Assert.Null(oldChild.TryGetContext());
        Assert.Null(intermediate.TryGetContext());
        Assert.Equal(1, final.GetReferenceCount());
        Assert.False(lifecycle.Graph.IsOwned(intermediate));
    }

    [Fact]
    public void WhenDetachCallbackThrows_ThenReplacementAndRemainingDetachStillSettle()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var descendant = new Person();
        var oldChild = new Person { Father = descendant };
        var root = new Person(context) { Father = oldChild };
        var replacement = new Person();
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var replacementNotified = false;
        lifecycle.SubjectDetaching += change =>
        {
            if (ReferenceEquals(change.Subject, oldChild)) throw new InvalidOperationException("callback failure");
        };
        lifecycle.SubjectAttached += change => replacementNotified |= ReferenceEquals(change.Subject, replacement);

        // Act
        var exception = Record.Exception(() => root.Father = replacement);

        // Assert
        Assert.NotNull(exception);
        Assert.Same(replacement, root.Father);
        Assert.Same(context, replacement.TryGetContext());
        Assert.Equal(1, replacement.GetReferenceCount());
        Assert.Null(oldChild.TryGetContext());
        Assert.Null(descendant.TryGetContext());
        Assert.False(lifecycle.Graph.IsOwned(descendant));
        Assert.True(replacementNotified);
    }
    [Fact]
    public void WhenAttachCallbackReplacesItself_ThenDetachAndAttachAreDeliveredAfterCurrentCallback()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new Person(context);
        var first = new Person();
        var second = new Person();
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var events = new List<string>();
        lifecycle.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, first))
            {
                events.Add("first-start");
                root.Father = second;
                Assert.Equal(1, second.GetReferenceCount());
                events.Add("first-end");
            }
            else if (ReferenceEquals(change.Subject, second)) events.Add("second-attach");
        };
        lifecycle.SubjectDetaching += change => { if (ReferenceEquals(change.Subject, first)) events.Add("first-detach"); };

        // Act
        root.Father = first;

        // Assert
        Assert.Equal(new[] { "first-start", "first-end", "first-detach", "second-attach" }, events);
        Assert.Null(first.TryGetContext());
        Assert.Same(second, root.Father);
    }

    [Fact]
    public void WhenDetachCallbackReattachesSameSubject_ThenDeferredReleaseDoesNotDropNewOwnership()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var child = new Person();
        var root = new Person(context) { Father = child };
        context.TryGetLifecycleInterceptor()!.SubjectDetaching += change =>
        {
            if (ReferenceEquals(change.Subject, child))
            {
                Assert.Same(context, child.GetContext());
                root.Father = child;
            }
        };

        // Act
        root.Father = null;

        // Assert
        Assert.Same(child, root.Father);
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenNestedInterceptorAssignsForeignChild_ThenItRejectsBeforeChangingField()
    {
        // Arrange
        var interceptor = new SpikeNestedInterceptor();
        var context = InterceptorSubjectContext.Create().WithLifecycle().WithService(() => interceptor);
        var foreignContext = InterceptorSubjectContext.Create().WithLifecycle();
        var outer = new Person(context);
        var destination = new Person(context);
        var wrapper = new Person();
        var foreign = new Person(foreignContext);
        Exception? nestedException = null;
        interceptor.Action = () =>
        {
            destination.Father = wrapper;
            nestedException = Record.Exception(() => wrapper.Father = foreign);
            Assert.Null(wrapper.Father);
        };

        // Act
        outer.Father = new Person();

        // Assert
        Assert.NotNull(nestedException);
        Assert.Same(context, wrapper.TryGetContext());
        Assert.Equal(1, wrapper.GetReferenceCount());
        Assert.Same(foreignContext, foreign.TryGetContext());
    }

    [Fact]
    public void WhenCallbackWritesOtherContext_ThenItRejectsBeforeChangingFieldAndScalarWriteStillWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var foreignContext = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new Person(context);
        var foreign = new Person(foreignContext);
        Exception? rejected = null;
        context.TryGetLifecycleInterceptor()!.SubjectAttached += _ =>
        {
            foreign.FirstName = "ok";
            rejected = Record.Exception(() => foreign.Father = new Person());
        };

        // Act
        root.Father = new Person();

        // Assert
        Assert.IsType<LifecycleContractViolationException>(rejected);
        Assert.Null(foreign.Father);
        Assert.Equal("ok", foreign.FirstName);
    }

    [Fact]
    public void WhenDownstreamInterceptorThrowsAfterTerminal_ThenCommittedFieldAndEdgesAgree()
    {
        // Arrange
        var interceptor = new SpikeThrowAfterInterceptor();
        var context = InterceptorSubjectContext.Create().WithLifecycle().WithService(() => interceptor);
        var oldChild = new Person();
        var root = new Person(context) { Father = oldChild };
        var replacement = new Person();
        interceptor.Throw = true;

        // Act
        var exception = Record.Exception(() => root.Father = replacement);

        // Assert
        Assert.NotNull(exception);
        Assert.Same(replacement, root.Father);
        Assert.Same(context, replacement.TryGetContext());
        Assert.Equal(1, replacement.GetReferenceCount());
        Assert.Null(oldChild.TryGetContext());
    }

}


[RunsBefore(typeof(LifecycleInterceptor))]
public sealed class SpikeNestedInterceptor : IWriteInterceptor
{
    public Action? Action { get; set; }
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var action = Action;
        Action = null;
        action?.Invoke();
        next(ref context);
    }
}

[RunsAfter(typeof(LifecycleInterceptor))]
public sealed class SpikeThrowAfterInterceptor : IWriteInterceptor
{
    public bool Throw { get; set; }
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        next(ref context);
        if (Throw) throw new InvalidOperationException("after terminal");
    }
}
