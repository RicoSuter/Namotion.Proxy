using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Tests that verify recursive discovery of descendants when subjects enter the graph.
/// Discovery happens through the lifecycle's own descent slot, which seeds the property baselines
/// and recursively attaches children; context inheritance is intrinsic to WithLifecycle().
/// </summary>
public class RecursiveAttachTests
{
    // ──────────────────────────────────────────────
    // Context inheritance discovers all descendants
    // ──────────────────────────────────────────────

    [Fact]
    public void WhenChildWithPrePopulatedGrandchildIsAttached_ThenGrandchildIsAlsoAttached()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var attached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(change.Subject);

        var parent = new Person(context) { FirstName = "Parent" };

        var grandmother = new Person { FirstName = "Grandmother" };
        var mother = new Person { FirstName = "Mother", Mother = grandmother };

        // Act
        parent.Mother = mother;

        // Assert: both Mother and Grandmother must be attached
        Assert.Contains(mother, attached);
        Assert.Contains(grandmother, attached);
    }

    [Fact]
    public void WhenChildWithPrePopulatedCollectionIsAttached_ThenCollectionChildrenAreAlsoAttached()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var attached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(change.Subject);

        var parent = new Person(context) { FirstName = "Parent" };

        var grandchild1 = new Person { FirstName = "Grandchild1" };
        var grandchild2 = new Person { FirstName = "Grandchild2" };
        var mother = new Person { FirstName = "Mother", Children = [grandchild1, grandchild2] };

        // Act
        parent.Mother = mother;

        // Assert
        Assert.Contains(grandchild1, attached);
        Assert.Contains(grandchild2, attached);
    }

    [Fact]
    public void WhenDeepHierarchyIsAttached_ThenAllLevelsAreAttached()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var attached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(change.Subject);

        var parent = new Person(context) { FirstName = "Parent" };

        var greatGrandmother = new Person { FirstName = "GreatGrandmother" };
        var grandmother = new Person { FirstName = "Grandmother", Mother = greatGrandmother };
        var mother = new Person { FirstName = "Mother", Mother = grandmother };

        // Act
        parent.Mother = mother;

        // Assert: all three levels must be attached
        Assert.Contains(mother, attached);
        Assert.Contains(grandmother, attached);
        Assert.Contains(greatGrandmother, attached);
    }

    // ──────────────────────────────────────────────
    // Detach cascade cleans up discovered hierarchy
    // ──────────────────────────────────────────────

    [Fact]
    public void WhenParentOfDiscoveredHierarchyIsDetached_ThenAllLevelsAreDetached()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;

        var parent = new Person(context) { FirstName = "Parent" };

        var grandmother = new Person { FirstName = "Grandmother" };
        var mother = new Person { FirstName = "Mother", Mother = grandmother };
        parent.Mother = mother;

        var detached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectDetaching += change =>
        {
            if (change.IsContextDetach)
                detached.Add(change.Subject);
        };

        // Act
        parent.Mother = null;

        // Assert: both Mother and Grandmother must be detached
        Assert.Contains(mother, detached);
        Assert.Contains(grandmother, detached);
    }

    [Fact]
    public void WhenCollectionIsCleared_ThenDiscoveredGrandchildrenAreDetached()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;

        var parent = new Person(context) { FirstName = "Parent" };

        var grandchild = new Person { FirstName = "Grandchild" };
        var child = new Person { FirstName = "Child", Children = [grandchild] };
        parent.Children = [child];

        var detached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectDetaching += change =>
        {
            if (change.IsContextDetach)
                detached.Add(change.Subject);
        };

        // Act
        parent.Children = [];

        // Assert
        Assert.Contains(child, detached);
        Assert.Contains(grandchild, detached);
    }

    // ──────────────────────────────────────────────
    // Subsequent writes diff correctly against seeded baseline
    // ──────────────────────────────────────────────

    [Fact]
    public void WhenGrandchildIsReplacedAfterDiscovery_ThenOldIsDetachedAndNewIsAttached()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;

        var parent = new Person(context) { FirstName = "Parent" };

        var grandmother = new Person { FirstName = "Grandmother" };
        var mother = new Person { FirstName = "Mother", Mother = grandmother };
        parent.Mother = mother;

        var attached = new List<IInterceptorSubject>();
        var detached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(change.Subject);
        lifecycleInterceptor.SubjectDetaching += change =>
        {
            if (change.IsContextDetach)
                detached.Add(change.Subject);
        };

        // Act: replace grandmother with a new one
        var newGrandmother = new Person { FirstName = "NewGrandmother" };
        mother.Mother = newGrandmother;

        // Assert
        Assert.Contains(grandmother, detached);
        Assert.Contains(newGrandmother, attached);
    }

    // ──────────────────────────────────────────────
    // Cycles must not cause infinite recursion
    // ──────────────────────────────────────────────

    [Fact]
    public void WhenCyclicGraphIsAttached_ThenAllSubjectsAreAttachedWithoutInfiniteRecursion()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var attached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(change.Subject);

        var parent = new Person(context) { FirstName = "Parent" };

        // Create cycle: A.Mother = B, B.Mother = A
        var nodeA = new Person { FirstName = "NodeA" };
        var nodeB = new Person { FirstName = "NodeB" };
        nodeA.Mother = nodeB;
        nodeB.Mother = nodeA;

        // Act: attach the cycle
        parent.Mother = nodeA;

        // Assert: both nodes attached, no stack overflow
        Assert.Contains(nodeA, attached);
        Assert.Contains(nodeB, attached);
    }

    // ──────────────────────────────────────────────
    // WithLifecycle() alone carries the full descent
    // ──────────────────────────────────────────────

    [Fact]
    public void WhenUsingLifecycleAlone_ThenTheWholeSubtreeIsAttached()
    {
        // Arrange: WithLifecycle() alone is the full graph lifecycle: the descent that used to
        // require the separate context-inheritance handler is intrinsic.
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var attached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(change.Subject);

        var parent = new Person(context) { FirstName = "Parent" };

        var grandmother = new Person { FirstName = "Grandmother" };
        var mother = new Person { FirstName = "Mother", Mother = grandmother };

        // Act
        parent.Mother = mother;

        // Assert
        Assert.Contains(mother, attached);
        Assert.Contains(grandmother, attached);
    }

    [Fact]
    public void WhenAnEdgeRemovalIsSurvived_ThenNoAttachIsPublished()
    {
        // Arrange: no attach transition may be published from inside a removal, and whether a
        // subject's component is owned must not depend on it having survived an edge removal.
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;

        var root = new Person(context) { FirstName = "Root" };
        var child = new Person { FirstName = "Child" };

        root.Father = child;
        root.Mother = child;

        var attached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(change.Subject);

        // Act: the child survives, because the second edge still holds it.
        root.Father = null;

        // Assert
        Assert.Empty(attached);
        Assert.Equal(1, child.GetReferenceCount());
        Assert.Same(context, child.TryGetContext());
    }

    // ──────────────────────────────────────────────
    // Null baseline: re-attached child without re-seeding
    // ──────────────────────────────────────────────

    [Fact]
    public void WhenAReleasedChildIsReattached_ThenItsSubtreeIsRediscovered()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person(context) { FirstName = "Child" };
        var grandmother = new Person(context) { FirstName = "Grandmother" };

        // Build hierarchy: parent → child → grandmother
        child.Mother = grandmother;
        parent.Mother = child;

        // Release the child, which takes its baselines and the grandmother with it.
        parent.Mother = null;
        Assert.Null(child.TryGetContext());
        Assert.Null(grandmother.TryGetContext());

        var attached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(change.Subject);

        // Act: re-attach the child. Its backing store still holds the grandmother while the
        // lifecycle holds no baseline for child.Mother, so the descent must re-seed the child's
        // properties and rediscover the stored subtree rather than trust a stale baseline.
        parent.Mother = child;

        // Assert
        Assert.Contains(grandmother, attached);
        Assert.Same(context, grandmother.TryGetContext());
        Assert.Equal(1, grandmother.GetReferenceCount());
    }
}
