using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Registry.Tests.GraphBehavior;

/// <summary>
/// Tests for cyclic graph scenarios, including release of orphaned cycles.
/// </summary>
public class CycleTests
{
    [Fact]
    public Task WhenCreatingSelfReference_ThenSubjectStaysAttached()
    {
        // Arrange
        var helper = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => helper);

        // Act - Create self-reference
        var person = new Person(context) { FirstName = "Narcissus" };
        person.Father = person;

        // Assert
        return Verify(helper.GetEvents());
    }

    [Fact]
    public Task WhenCreatingDirectCycle_ThenBothAttached()
    {
        // Arrange
        var helper = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => helper);

        // Act - Create bidirectional cycle: Alice <-> Bob
        var alice = new Person(context) { FirstName = "Alice" };
        var bob = new Person(context) { FirstName = "Bob" };

        alice.Mother = bob;
        bob.Mother = alice;

        // Assert
        return Verify(helper.GetEvents());
    }

    [Fact]
    public Task WhenBreakingCycle_ThenAdoptedSubjectDetachesAndConstructorRootStays()
    {
        // Arrange
        var helper = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => helper);

        var alice = new Person(context) { FirstName = "Alice" };
        var bob = new Person(context) { FirstName = "Bob" };

        alice.Mother = bob;
        bob.Mother = alice;

        helper.Clear();

        // Act - Break the cycle
        alice.Mother = null;

        // Assert - Bob detaches (his only support was Alice's edge). Alice keeps her
        // constructor anchor: Bob's back-reference had no independent support, so it never
        // consumed her provisional root status, and losing it cannot detach her.
        return Verify(helper.GetEvents());
    }

    [Fact]
    public Task WhenCycleProtectedByExternalRef_ThenCycleStays()
    {
        // Arrange
        var helper = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => helper);

        // Setup: Root.Children = [A, B], A <-> B cycle
        var nodeA = new Person { FirstName = "A" };
        var nodeB = new Person { FirstName = "B" };
        nodeA.Mother = nodeB;
        nodeB.Mother = nodeA;

        var root = new Person(context)
        {
            FirstName = "Root",
            Children = [nodeA, nodeB]
        };

        helper.Clear();

        // Act - Break the internal cycle
        nodeA.Mother = null;

        // Assert - A and B stay attached (both still referenced from Root.Children)
        return Verify(helper.GetEvents());
    }

    [Fact]
    public Task WhenInternalCycleOrphaned_ThenCycleIsReleased()
    {
        // Arrange
        var helper = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => helper);

        // Setup: Root -> A -> B <-> C (internal cycle)
        var nodeC = new Person { FirstName = "C" };
        var nodeB = new Person { FirstName = "B", Mother = nodeC };
        nodeC.Mother = nodeB; // Create B <-> C cycle

        var nodeA = new Person { FirstName = "A", Mother = nodeB };

        var root = new Person(context)
        {
            FirstName = "Root",
            Father = nodeA
        };

        helper.Clear();

        // Act - Remove A from root (orphans the B <-> C cycle)
        root.Father = null;

        // Assert - A detaches and the orphaned B <-> C cycle is released as well: the
        // reachability scan proves the cycle unreachable from any root. The registry projection
        // detaches completely with it; keeping orphaned cycles registered was a limitation of the
        // reference-count model this projection no longer has.
        var registry = context.GetService<ISubjectRegistry>();
        Assert.Equal([root], registry.KnownSubjects.Keys);
        return Verify(helper.GetEvents());
    }
}
