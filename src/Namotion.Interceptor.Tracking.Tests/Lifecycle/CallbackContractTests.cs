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
    public void WhenAPropertyCallbackWritesStructuralPropertyAtTopLevel_ThenItThrows()
    {
        // Arrange
        // The one-shot flag is load-bearing twice over. The handler also fires during stranger's
        // OWN construction, while the local is still null, which would record a
        // NullReferenceException and gate out every later invocation. And pre-fix the write
        // succeeds, so without the flag each attempt publishes another attach and recurses.
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

        var context = CreateContext().WithService(() => handler, _ => false);
        stranger = new Person(context) { FirstName = "S" };

        // Act
        var root = new Person(context) { FirstName = "R" };

        // Assert
        Assert.IsType<LifecycleContractViolationException>(callbackException);
        Assert.NotNull(root);
    }

    [Fact]
    public void WhenAPropertyCallbackWritesStructuralPropertyBelowTheFirstLevel_ThenItThrows()
    {
        // Arrange: three levels, so the callback for the deepest subject runs inside the
        // descent's own callback scope. This is the case a single-level test cannot see.
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

        var context = CreateContext().WithService(() => handler, _ => false);
        stranger = new Person(context) { FirstName = "S" };

        var top = new Person(context) { FirstName = "top" };
        var mid = new Person { FirstName = "mid" };
        var leaf = new Person { FirstName = "leaf" };
        mid.Father = leaf;

        // Act
        top.Father = mid;

        // Assert
        Assert.IsType<LifecycleContractViolationException>(deepException);
    }

    [Fact]
    public void WhenALifecycleCallbackAttachesASubject_ThenItThrows()
    {
        // Arrange
        // The flag must be set BEFORE the attempt. Pre-fix the attach succeeds and publishes
        // another attach, which re-enters this handler before callbackException is assigned, and
        // the recursion ends in a stack overflow that kills the whole assembly rather than
        // failing one test.
        Exception? callbackException = null;
        var attempted = false;
        var context = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (attempted)
                {
                    return;
                }

                attempted = true;
                callbackException = Record.Exception(
                    () => new Person { FirstName = "X" }.AttachToContext(change.Subject.GetContext()));
            }), _ => false);

        // Act
        _ = new Person(context) { FirstName = "R" };

        // Assert
        Assert.IsType<LifecycleContractViolationException>(callbackException);
    }

    [Fact]
    public void WhenALifecycleCallbackDetachesASubject_ThenItThrows()
    {
        // Arrange
        Exception? callbackException = null;
        Person? pinned = null;
        var context = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (callbackException is not null || pinned is null || ReferenceEquals(change.Subject, pinned))
                {
                    return;
                }

                callbackException = Record.Exception(() => pinned.DetachFromContext(pinned.GetContext()));
            }), _ => false);

        // Explicit attach, not a context constructor: a constructed subject carries a Provisional
        // anchor, and ValidateDetach already rejects detaching one with a plain
        // InvalidOperationException, so the test would pass pre-fix for the wrong reason.
        pinned = new Person { FirstName = "P" };
        pinned.AttachToContext(context);

        // Act
        _ = new Person(context) { FirstName = "R" };

        // Assert
        Assert.IsType<LifecycleContractViolationException>(callbackException);
        Assert.NotNull(pinned.TryGetContext());
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
    public void WhenADerivedPropertyExposesAnUnattachedSubject_ThenItThrows()
    {
        // Arrange
        var context = CreateDerivedContext();

        // Act & Assert: the lazily created child is owned by nothing, so it would never be
        // tracked. Attach-time evaluation of the derived getter is where that surfaces.
        var exception = Record.Exception(() => new LazyDerivedSubject(context));

        Assert.IsType<LifecycleContractViolationException>(exception);
        Assert.Contains("derived", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhenTheAttachEvaluationExposesAnUnattachedSubject_ThenNoValueIsCommitted()
    {
        // Arrange
        var context = CreateDerivedContext();
        var subject = new LazyDerivedSubject();

        // Act
        var exception = Record.Exception(() => subject.AttachToContext(context));

        // Assert: the rejected value must never become LastKnownValue, matching the
        // recalculation path, which checks before committing.
        Assert.IsType<LifecycleContractViolationException>(exception);
        var data = new PropertyReference(subject, nameof(LazyDerivedSubject.Current)).TryGetDerivedPropertyData();
        Assert.NotNull(data);
        Assert.Null(data.LastKnownValue);
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
        // Arrange: the declared type object cannot exclude the property from the untracked-subject
        // check, so the runtime type of the returned value must.
        var context = CreateDerivedContext();

        // Act
        var subject = new ObjectDerivedStringSubject(context);
        subject.Name = "world";

        // Assert
        Assert.Equal("Hello, world", subject.Value);
    }

    [Fact]
    public void WhenAnObjectDeclaredDerivedPropertyExposesAnUnattachedSubject_ThenItThrows()
    {
        // Arrange: the runtime-type fast path must stay fail-closed for a real subject hiding
        // behind an object declaration.
        var context = CreateDerivedContext();

        // Act & Assert
        var exception = Record.Exception(() => new ObjectDerivedLazySubject(context));

        Assert.IsType<LifecycleContractViolationException>(exception);
    }

    [Fact]
    public void WhenADerivedValueExposesAnUnattachedSubjectTransiently_ThenTheRecalculationRetriesAndConverges()
    {
        // Arrange: derived evaluation runs outside lock(data), so a concurrent structural write
        // can detach a projected subject after evaluation but before its cascade marks the data
        // stale. The one-shot flag reproduces that window deterministically: one evaluation
        // returns an unattached subject, the re-evaluation is clean.
        var context = CreateDerivedContext();
        var subject = new TransientOrphanDerivedSubject(context);
        subject.ReturnUnattachedSubjectOnce = true;

        // Act: the triggering write is innocent and must not observe a spurious throw.
        subject.Name = "x";

        // Assert
        Assert.Null(subject.Current);
    }

    [Fact]
    public void WhenADerivedValueKeepsExposingAnUnattachedSubject_ThenTheRecalculationThrowsAfterTheRetryBound()
    {
        // Arrange: attach passes because the getter projects nothing yet; the projection is then
        // cached in a plain field, so clearing the stored edge turns every re-evaluation into the
        // same genuine orphan that no retry converges away.
        var context = CreateDerivedContext();
        var subject = new CachingOrphanDerivedSubject(context);
        subject.Stored = new Person { FirstName = "C" };
        var evaluationsBeforeDetach = subject.EvaluationCount;

        // Act
        var exception = Record.Exception(() => subject.Stored = null);

        // Assert: the throw must come out of the bounded retry loop, not the first detection.
        Assert.IsType<LifecycleContractViolationException>(exception);
        Assert.True(
            subject.EvaluationCount - evaluationsBeforeDetach >= DerivedPropertyChangeHandler.MaxStabilizationIterations,
            "the recalculation must re-evaluate up to the retry bound before declaring a genuine orphan");
    }

    [Fact]
    public void WhenADerivedPropertyWithABackingFieldStoresASubject_ThenTheUntrackedSubjectCheckAccepts()
    {
        // Arrange: a derived property with a generator-emitted backing field is the sole store of
        // whatever is assigned, so it carries an ownership edge like any other stored property.
        // The untracked-subject check must then find the subject owned rather than reject it.
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
