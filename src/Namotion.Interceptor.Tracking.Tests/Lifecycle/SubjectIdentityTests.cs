using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Graph membership is identity: a subject that overrides Equals/GetHashCode must still be
/// tracked as its own node. The lifecycle's subject-keyed collections use reference equality
/// explicitly, so instances that compare equal (or whose hash mutates) cannot merge or strand.
/// </summary>
public class SubjectIdentityTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        // Deliberately registry-free: the identity guarantee holds for the lifecycle's own state,
        // but the Registry still keys subjects with default equality, so under WithRegistry()
        // these models would merge registry entries. Aligning the consumers that key subjects by
        // default equality is a recorded follow-up, not part of this stage.
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    [Fact]
    public void WhenAllInstancesCompareEqual_ThenEachSubjectIsTrackedAsItsOwnNode()
    {
        // Arrange
        var context = CreateContext();
        var root = new EqualityOverridingPerson(context) { Name = "Root" };
        var a = new EqualityOverridingPerson { Name = "A" };
        var b = new EqualityOverridingPerson { Name = "B" };

        // Act
        root.Friends = [a, b];

        // Assert: two distinct nodes, one incoming edge each.
        Assert.Same(context, a.TryGetContext());
        Assert.Same(context, b.TryGetContext());
        Assert.Equal(1, a.GetReferenceCount());
        Assert.Equal(1, b.GetReferenceCount());
    }

    [Fact]
    public void WhenOneOfTwoEqualComparingSubjectsIsRemoved_ThenOnlyThatSubjectIsReleased()
    {
        // Arrange
        var context = CreateContext();
        var root = new EqualityOverridingPerson(context) { Name = "Root" };
        var a = new EqualityOverridingPerson { Name = "A" };
        var b = new EqualityOverridingPerson { Name = "B" };
        root.Friends = [a, b];

        // Act: the ordinal reconcile counts occurrences per subject; merged counting under
        // default equality would treat a and b as one subject with two occurrences.
        root.Friends = [a];

        // Assert
        Assert.Same(context, a.TryGetContext());
        Assert.Equal(1, a.GetReferenceCount());
        Assert.Single(a.GetParents());
        Assert.Null(b.TryGetContext());
        Assert.Equal(0, b.GetReferenceCount());
    }

    [Fact]
    public void WhenEqualComparingSubjectsFormAChain_ThenReleaseDescendsThroughEachNode()
    {
        // Arrange: discovery and release both walk with subject-keyed visited state, which under
        // default equality would stop at the first "already visited" false positive.
        var context = CreateContext();
        var root = new EqualityOverridingPerson(context) { Name = "Root" };
        var middle = new EqualityOverridingPerson { Name = "M" };
        var leaf = new EqualityOverridingPerson { Name = "L" };
        middle.Partner = leaf;
        root.Partner = middle;
        Assert.Same(context, leaf.TryGetContext());

        // Act
        root.Partner = null;

        // Assert: both levels released.
        Assert.Null(middle.TryGetContext());
        Assert.Null(leaf.TryGetContext());
        Assert.Same(context, root.TryGetContext());
    }
}
