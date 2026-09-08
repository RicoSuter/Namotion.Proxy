using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceScopeTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithLifecycle()
            .WithParents()
            .WithSourceMonitoring();

    [Fact]
    public void WhenTheSourceIsRootedAtAnAncestor_ThenItIsInScope()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context);
        var child = new Person();
        root.Mother = child;
        var source = new TestStateSource(root);

        // Act
        var inScope = IsInScope(source, child);

        // Assert
        Assert.True(inScope);
    }

    [Fact]
    public void WhenTheSourceIsRootedAtADescendant_ThenItIsInScope()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context);
        var child = new Person();
        root.Mother = child;
        var source = new TestStateSource(child);

        // Act
        var inScope = IsInScope(source, root);

        // Assert
        Assert.True(inScope);
    }

    [Fact]
    public void WhenTheSourceIsRootedOnASiblingBranch_ThenItIsNotInScope()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context);
        var left = new Person();
        var right = new Person();
        root.Mother = left;
        root.Father = right;
        var source = new TestStateSource(right);

        // Act
        var inScope = IsInScope(source, left);

        // Assert
        Assert.False(inScope);
    }

    [Fact]
    public void WhenTheAnchorIsTheSourceRootItself_ThenItIsInScopeWithoutAnyParentWalk()
    {
        // Arrange
        var context = CreateContext();
        var detached = new Person(context);
        var source = new TestStateSource(detached);

        // Act
        var inScope = IsInScope(source, detached);

        // Assert
        Assert.True(inScope);
    }

    [Fact]
    public void WhenTheSubjectHasTwoParents_ThenSourcesOnEitherPathAreInScope()
    {
        // Arrange
        var context = CreateContext();
        var firstRoot = new Person(context);
        var secondRoot = new Person(context);
        var shared = new Person();
        firstRoot.Mother = shared;
        secondRoot.Mother = shared;

        // Act & Assert
        Assert.True(IsInScope(new TestStateSource(firstRoot), shared));
        Assert.True(IsInScope(new TestStateSource(secondRoot), shared));
    }

    [Fact(Timeout = 10_000)]
    public async Task WhenTheParentGraphHasACycle_ThenIsInScopeReturnsPromptlyInsteadOfHanging()
    {
        // Arrange
        // A same-tree reparent gives every node exactly one parent while still forming a cycle:
        // nothing in ParentTrackingHandler rejects it. An unguarded single-parent walk that starts
        // from a node in the cycle and never finds its (unrelated) candidate would ping-pong between
        // the two nodes forever instead of terminating.
        var context = CreateContext();
        var first = new Person(context);
        var second = new Person();
        first.Mother = second;
        second.Mother = first;
        var unrelated = new Person();
        var source = new TestStateSource(unrelated);

        // Act
        var inScope = await Task.Run(() => IsInScope(source, first));

        // Assert
        Assert.False(inScope);
    }

    /// <summary>
    /// The production overload takes caller-supplied scratch collections, since the wait engine
    /// reuses them across every walk. These tests allocate a fresh pair per call.
    /// </summary>
    private static bool IsInScope(ISubjectSource source, IInterceptorSubject anchor) =>
        SourceScope.IsInScope(
            source, anchor,
            new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance),
            new Stack<IInterceptorSubject>());
}
