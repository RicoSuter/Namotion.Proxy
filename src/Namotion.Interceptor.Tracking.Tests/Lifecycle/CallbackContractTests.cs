using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CallbackContractTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    [Fact]
    public void WhenAPropertyCallbackWritesStructuralPropertyAtTopLevel_ThenBothRootsSettle()
    {
        // Arrange
        // Construction also publishes callbacks before the local receives the constructed subject.
        Exception? callbackException = null;
        var attempted = false;
        Person? stranger = null;
        var handler = new DelegatePropertyAttachHandler(change =>
        {
            if (attempted || stranger is null)
            {
                return;
            }

            attempted = true;
            callbackException = Record.Exception(() => stranger.Father = new Person());
        });

        var context = CreateContext().WithRegistry().WithService(() => handler, _ => false);
        stranger = new Person(context) { FirstName = "S" };

        // Act
        var root = new Person(context) { FirstName = "R" };

        // Assert
        Assert.True(attempted);
        Assert.Null(callbackException);
        var child = Assert.IsType<Person>(stranger.Father);
        SupportContractAssertions.Settled(context, [stranger, root], stranger, root, child);
        stranger.AttachToContext(context);
        root.AttachToContext(context);
        stranger.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [root], stranger, root, child);
        root.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], stranger, root, child);
    }

    [Fact]
    public void WhenAPropertyCallbackWritesStructuralPropertyBelowTheFirstLevel_ThenBothSubtreesSettle()
    {
        // Arrange
        Exception? deepException = null;
        var attempted = false;
        Person? stranger = null;
        var handler = new DelegatePropertyAttachHandler(change =>
        {
            if (attempted || stranger is null || change.Subject is not Person { FirstName: "leaf" })
            {
                return;
            }

            attempted = true;
            deepException = Record.Exception(() => stranger.Father = new Person());
        });

        var context = CreateContext().WithRegistry().WithService(() => handler, _ => false);
        stranger = new Person(context) { FirstName = "S" };

        var top = new Person(context) { FirstName = "top" };
        var mid = new Person { FirstName = "mid" };
        var leaf = new Person { FirstName = "leaf" };
        mid.Father = leaf;

        // Act
        top.Father = mid;

        // Assert
        Assert.True(attempted);
        Assert.Null(deepException);
        var child = Assert.IsType<Person>(stranger.Father);
        SupportContractAssertions.Settled(context, [stranger, top], stranger, top, mid, leaf, child);
        stranger.AttachToContext(context);
        top.AttachToContext(context);
        stranger.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [top], stranger, top, mid, leaf, child);
        top.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], stranger, top, mid, leaf, child);
    }

    [Fact]
    public void WhenALifecycleCallbackAttachesASubject_ThenBothRootsRemainIndependentlyDetachable()
    {
        // Arrange
        // Nested attachment queues further callbacks, so the handler runs only once.
        Exception? callbackException = null;
        var attempted = false;
        var introduced = new Person { FirstName = "X" };
        var context = CreateContext().WithRegistry()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (attempted)
                {
                    return;
                }

                attempted = true;
                callbackException = Record.Exception(
                    () => introduced.AttachToContext(change.Subject.GetContext()));
            }), _ => false);

        // Act
        var root = new Person(context) { FirstName = "R" };

        // Assert
        Assert.True(attempted);
        Assert.Null(callbackException);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)introduced).Executor.AttachmentAnchor);
        SupportContractAssertions.Settled(context, [root, introduced], root, introduced);
        introduced.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [root], root, introduced);
        root.AttachToContext(context);
        root.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], root, introduced);
    }

    [Fact]
    public void WhenALifecycleCallbackDetachesASubject_ThenTheExplicitRootIsReleased()
    {
        // Arrange
        Exception? callbackException = null;
        Person? pinned = null;
        var attempted = false;
        var context = CreateContext().WithRegistry()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (attempted || pinned is null || ReferenceEquals(change.Subject, pinned))
                {
                    return;
                }

                attempted = true;
                callbackException = Record.Exception(() => pinned.DetachFromContext(pinned.GetContext()));
            }), _ => false);

        // Explicit attach, not a context constructor: a constructed subject carries a Provisional
        // anchor, and ValidateDetach already rejects detaching one with a plain
        // InvalidOperationException, so the test would pass pre-fix for the wrong reason.
        pinned = new Person { FirstName = "P" };
        pinned.AttachToContext(context);

        // Act
        var root = new Person(context) { FirstName = "R" };

        // Assert
        Assert.True(attempted);
        Assert.Null(callbackException);
        SupportContractAssertions.Settled(context, [root], root, pinned);
        root.AttachToContext(context);
        root.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], root, pinned);
    }

    [Fact]
    public void WhenTwoLifecyclesAttachIntoEachOtherFromCallbacks_ThenNeitherDeadlocks()
    {
        // Arrange: the reproduction of the cross-lifecycle gate deadlock. Each thread holds its
        // own gate inside a callback and reaches for the other's. The contract must reject the
        // attach before either gate is requested, so both threads finish.
        var first = CreateContext();
        var second = CreateContext();
        var ready = new CountdownEvent(2);

        void Body(IInterceptorSubjectContext own, IInterceptorSubjectContext other)
        {
            own.WithService(() => new DelegateLifecycleHandler(_ =>
            {
                ready.Signal();
                ready.Wait(TimeSpan.FromSeconds(5));
                Record.Exception(() => new Person { FirstName = "X" }.AttachToContext(other));
            }), _ => false);

            _ = new Person(own) { FirstName = "R" };
        }

        // Act
        var a = new Thread(() => Body(first, second)) { IsBackground = true };
        var b = new Thread(() => Body(second, first)) { IsBackground = true };
        a.Start();
        b.Start();

        // Assert: a bounded join, so a regression fails the test instead of hanging the suite.
        Assert.True(a.Join(TimeSpan.FromSeconds(10)), "thread a did not finish, the gates deadlocked");
        Assert.True(b.Join(TimeSpan.FromSeconds(10)), "thread b did not finish, the gates deadlocked");
    }

    /// <summary>
    /// Derived getters are only evaluated when DerivedPropertyChangeHandler is registered, which
    /// WithLifecycle() alone does not do. Without this the tests below pass vacuously.
    /// </summary>
    private static IInterceptorSubjectContext CreateDerivedContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithDerivedPropertyChangeDetection();
    }

    [Fact]
    public void WhenADerivedPropertyExposesAnUnattachedSubject_ThenItRemainsAProjection()
    {
        // Arrange
        var context = CreateDerivedContext();

        // Act
        var subject = new LazyDerivedSubject(context);

        // Assert
        Assert.Same(context, subject.TryGetContext());
        Assert.Null(subject.Current.TryGetContext());
        Assert.Equal(0, subject.Current.GetReferenceCount());
    }

    [Fact]
    public void WhenTheAttachEvaluationReturnsAProjection_ThenItsValueIsRecordedWithoutOwnership()
    {
        // Arrange
        var context = CreateDerivedContext();
        var subject = new LazyDerivedSubject();

        // Act
        subject.AttachToContext(context);

        // Assert
        var data = new PropertyReference(subject, nameof(LazyDerivedSubject.Current)).TryGetDerivedPropertyData();
        Assert.NotNull(data);
        Assert.Same(subject.Current, data.LastKnownValue);
        Assert.Null(subject.Current.TryGetContext());
    }

    [Fact]
    public void WhenADerivedPropertyProjectsAnAttachedSubject_ThenItDoesNotThrow()
    {
        // Arrange
        var context = CreateDerivedContext();

        // Act: FirstChild projects a subject already owned through the stored Children edge.
        var subject = new ProjectingDerivedSubject(context);
        subject.Children = [new Person { FirstName = "C" }];

        // Assert
        Assert.NotNull(subject.FirstChild);
        Assert.NotNull(subject.FirstChild!.TryGetContext());
    }

    [Fact]
    public void WhenAnObjectDeclaredDerivedPropertyReturnsAString_ThenItDoesNotThrow()
    {
        // Arrange
        var context = CreateDerivedContext();

        // Act
        var subject = new ObjectDerivedStringSubject(context);
        subject.Name = "world";

        // Assert
        Assert.Equal("Hello, world", subject.Value);
    }

    [Fact]
    public void WhenAnObjectDeclaredDerivedPropertyReturnsASubject_ThenItRemainsUnowned()
    {
        // Arrange
        var context = CreateDerivedContext();

        // Act
        var subject = new ObjectDerivedLazySubject(context);

        // Assert
        var projected = Assert.IsType<Person>(subject.Value);
        Assert.Null(projected.TryGetContext());
        Assert.Equal(0, projected.GetReferenceCount());
    }

    [Fact]
    public void WhenAComputedProjectionReturnsADetachedSubjectOnce_ThenThatValueCanPublish()
    {
        // Arrange
        var context = CreateDerivedContext();
        var subject = new TransientOrphanDerivedSubject(context);
        subject.ReturnUnattachedSubjectOnce = true;

        // Act
        subject.Name = "x";

        // Assert
        var data = new PropertyReference(subject, nameof(TransientOrphanDerivedSubject.Current)).GetDerivedPropertyData();
        var projected = Assert.IsType<Person>(data.LastKnownValue);
        Assert.Null(projected.TryGetContext());
        Assert.Equal(0, projected.GetReferenceCount());
        Assert.Null(subject.Current);
    }

    [Fact]
    public void WhenAProjectionKeepsAReleasedChild_ThenRecalculationPreservesTheValueWithoutAnEdge()
    {
        // Arrange
        var context = CreateDerivedContext();
        var subject = new CachingOrphanDerivedSubject(context);
        var child = new Person { FirstName = "C" };
        subject.Stored = child;
        Assert.Same(child, subject.Current);

        // Act
        subject.Stored = null;

        // Assert
        Assert.Same(child, subject.Current);
        Assert.Null(child.TryGetContext());
        Assert.Equal(0, child.GetReferenceCount());
    }

    [Fact]
    public void WhenADerivedPropertyWithABackingFieldStoresASubject_ThenItOwnsTheSubject()
    {
        // Arrange: a derived property with a generator-emitted backing field is the sole store of
        // whatever is assigned, so it carries an ownership edge like any other stored property.
        var context = CreateDerivedContext();
        var subject = new StoringDerivedSubject(context);
        var child = new Person { FirstName = "Child" };

        // Act
        var exception = Record.Exception(() => subject.Current = child);

        // Assert
        Assert.Null(exception);
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenADerivedPropertyWithABackingFieldReleasesASubject_ThenTheSubjectIsDetached()
    {
        // Arrange: clearing the store runs the derived-with-setter recalculation and the release
        // descent over the same write, so the edge must come off exactly once.
        var context = CreateDerivedContext();
        var subject = new StoringDerivedSubject(context);
        var child = new Person { FirstName = "Child" };
        subject.Current = child;

        // Act
        subject.Current = null;

        // Assert
        Assert.Null(child.TryGetContext());
        Assert.Equal(0, child.GetReferenceCount());
    }
}
