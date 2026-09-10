using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Regression tests for a defect shipped in 0.9.x and earlier: the context composed onto a subject
/// when it joined the graph could outlive its detach, because it was composed from the parent of the
/// first attach and decomposed against the parent of the last detach. For a subject with more than
/// one parent subject those are different contexts, so the decomposition matched nothing.
/// </summary>
public class ContextInheritanceCycleTests
{
    [Fact]
    public void WhenASharedSubjectLosesItsFirstParentBeforeItsLast_ThenItStopsResolvingTheGraphsServices()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var root = new Person(context) { FirstName = "Root" };
        var first = new Person { FirstName = "1st" };
        var shared = new Person { FirstName = "Shd" };

        root.Mother = first;
        first.Mother = shared;
        root.Father = shared;

        // Act: the parent that pulled the subject in is removed first, the other one last.
        first.Mother = null;
        root.Father = null;

        // Assert
        Assert.Equal(0, shared.GetReferenceCount());
        Assert.Null(((IInterceptorSubject)shared).TryGetContext());
        Assert.Empty(((IInterceptorSubject)shared).TryGetContext()?.GetServices<ILifecycleHandler>() ?? []);
    }

    [Fact]
    public void WhenASharedSubjectLosesItsFirstParentLast_ThenItStopsResolvingTheGraphsServices()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var root = new Person(context) { FirstName = "Root" };
        var first = new Person { FirstName = "1st" };
        var shared = new Person { FirstName = "Shd" };

        root.Mother = first;
        first.Mother = shared;
        root.Father = shared;

        // Act: the same graph, taken apart in the other order.
        root.Father = null;
        first.Mother = null;

        // Assert: the outcome must not depend on the removal order.
        Assert.Equal(0, shared.GetReferenceCount());
        Assert.Null(((IInterceptorSubject)shared).TryGetContext());
        Assert.Empty(((IInterceptorSubject)shared).TryGetContext()?.GetServices<ILifecycleHandler>() ?? []);
    }

    [Fact]
    public void WhenADetachedSubjectIsLaterAttachedBelowItsOwnFormerChild_ThenReadsAndWritesStillWork()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var root = new Person(context) { FirstName = "Root" };
        var first = new Person { FirstName = "1st" };
        var shared = new Person { FirstName = "Shd" };

        root.Mother = first;
        first.Mother = shared;
        root.Father = shared;
        first.Mother = null;
        root.Father = null;
        root.Mother = null;

        // Act: the reverse edge attaches the former parent below its own former child.
        shared.Mother = first;
        root.Mother = shared;
        root.Father = first;
        root.Mother = null;

        // Assert: a leaked composition used to make this a closed resolution loop, so every read
        // and every write on the subject threw.
        Assert.Equal("1st", first.FirstName);
        first.LastName = "X";
        Assert.Equal("X", first.LastName);
        Assert.NotEmpty(((IInterceptorSubject)first).TryGetContext()?.GetServices<ILifecycleHandler>() ?? []);
    }

    [Fact]
    public void WhenADetachedSubjectIsLaterAttachedBelowItsOwnFormerChild_ThenTheGraphStaysRepairable()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var root = new Person(context) { FirstName = "Root" };
        var first = new Person { FirstName = "1st" };
        var shared = new Person { FirstName = "Shd" };

        root.Mother = first;
        first.Mother = shared;
        root.Father = shared;
        first.Mother = null;
        root.Father = null;
        root.Mother = null;
        shared.Mother = first;
        root.Mother = shared;
        root.Father = first;
        root.Mother = null;

        // Act: the object model used to be unable to take this graph apart again.
        root.Father = null;

        // Assert
        Assert.Equal(0, first.GetReferenceCount());
        Assert.Null(((IInterceptorSubject)first).TryGetContext());
    }

    [Fact]
    public void WhenASubjectReferencesItself_ThenNothingThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var root = new Person(context) { FirstName = "Root" };
        var self = new Person { FirstName = "Slf" };

        // Act
        self.Mother = self;
        root.Mother = self;

        // Assert
        Assert.Equal("Slf", self.FirstName);
    }

    [Fact]
    public void WhenTwoSubjectsReferenceEachOther_ThenNothingThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var first = new Person(context) { FirstName = "1st" };
        var second = new Person { FirstName = "2nd" };

        // Act
        first.Mother = second;
        second.Mother = first;

        // Assert
        Assert.Equal("1st", first.FirstName);
        Assert.Equal("2nd", second.FirstName);
    }
}
