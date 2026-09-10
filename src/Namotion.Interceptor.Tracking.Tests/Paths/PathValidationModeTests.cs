using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Paths;
using Namotion.Interceptor.Tracking.Tests.Change;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

// Pins the difference between the two SubjectPathValidation modes on the dormant-divergence scenario
// (an intermediate replaced while dormant, the old suffix subject re-homed into another notifying
// context): Full heals on the stale leaf write; LeafOnly delivers the off-path leaf value on the leaf
// write (the carve-out) and heals only on the next structural write. Current is a fresh full walk in
// both modes.
[Collection(PerPropertySubscriptionCollection.Name)]
public class PathValidationModeTests
{
    public PathValidationModeTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenFullValidationAndDivergedOldSuffixLeafWritten_ThenWalkRetracksToTrueStateNotOffPathValue()
    {
        // Arrange: root -> childA is watched; keeper will adopt the old suffix so it dispatches again.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var outer = new Node(context) { Name = "outer" };
        var keeper = new Node(context) { Name = "keeper" };
        var childA = new Node { Name = "A0" };
        var root = new Node { Name = "root", Child = childA };
        outer.Child = root;

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Name,
            (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        // Act: replace the intermediate while dormant (missed), re-attach, re-home the old suffix, then
        // write the OLD subject's leaf.
        outer.Child = null;
        var childB = new Node { Name = "B0" };
        root.Child = childB;
        outer.Child = root;
        keeper.Child = childA;
        childA.Name = "A-offpath";

        // Assert: the revalidating walk detects the divergence, retracks onto childB, and delivers the
        // true observed state instead of the off-path value.
        var change = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.PathChange, change.Kind);
        Assert.Equal("A0", change.OldState.GetValueOrDefault());
        Assert.Equal("B0", change.NewState.GetValueOrDefault());
        Assert.False(HasListener(childA, nameof(Node.Name)));
        Assert.True(HasListener(childB, nameof(Node.Name)));
    }

    [Fact]
    public void WhenLeafOnlyValidationAndDivergedOldSuffixLeafWritten_ThenOffPathValueDeliversAndStructuralWriteHeals()
    {
        // Arrange: same dormant-divergence setup as the Full test, but with leaf-only validation.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var outer = new Node(context) { Name = "outer" };
        var keeper = new Node(context) { Name = "keeper" };
        var childA = new Node { Name = "A0" };
        var root = new Node { Name = "root", Child = childA };
        outer.Child = root;

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Name,
            (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.LeafOnly);

        outer.Child = null;
        var childB = new Node { Name = "B0" };
        root.Child = childB;
        outer.Child = root;
        keeper.Child = childA;

        // Act: write the OLD subject's leaf. The stale leaf listener fires and the leaf-only re-read
        // skips the from-root walk, so the divergence is NOT healed and the off-path value delivers.
        childA.Name = "A-offpath";

        // Assert: the carve-out. The off-path leaf value is delivered as a ValueChange, the stale
        // listener stays installed, and Current (always a fresh full walk) still reflects the true path.
        var offPath = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.ValueChange, offPath.Kind);
        Assert.Equal("A0", offPath.OldState.GetValueOrDefault());
        Assert.Equal("A-offpath", offPath.NewState.GetValueOrDefault());
        Assert.True(HasListener(childA, nameof(Node.Name)));
        Assert.False(HasListener(childB, nameof(Node.Name)));
        Assert.Equal("B0", subscription.Current.GetValueOrDefault());

        // Act: a STRUCTURAL write runs the full validating walk in both modes and heals the divergence.
        var childC = new Node { Name = "C0" };
        root.Child = childC;

        // Assert: retracked onto the true path; the stale listener is gone.
        Assert.Equal(2, events.Count);
        Assert.Equal(SubjectPathChangeKind.PathChange, events[1].Kind);
        Assert.Equal("A-offpath", events[1].OldState.GetValueOrDefault());
        Assert.Equal("C0", events[1].NewState.GetValueOrDefault());
        Assert.False(HasListener(childA, nameof(Node.Name)));
        Assert.True(HasListener(childC, nameof(Node.Name)));

        // Act & Assert: after the heal, writes to the healed suffix deliver and the old subject is inert.
        childC.Name = "C1";
        Assert.Equal(3, events.Count);
        Assert.Equal("C1", events[2].NewState.GetValueOrDefault());

        childA.Name = "A-again";
        Assert.Equal(3, events.Count);
    }

    [Fact]
    public void WhenLeafOnlyValidationOnResolvedPath_ThenLeafWriteDeliversValueChangeAndStructuralWriteRetracks()
    {
        // Arrange: an intact attached path with leaf-only validation.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root = new Node(context) { Name = "root" };
        var childA = new Node { Name = "A0" };
        root.Child = childA;

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Name,
            (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.LeafOnly);

        // Act: a leaf write takes the leaf-only re-read.
        childA.Name = "A1";

        // Assert: a ValueChange with the correct observed transition.
        var leafChange = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.ValueChange, leafChange.Kind);
        Assert.Equal("A0", leafChange.OldState.GetValueOrDefault());
        Assert.Equal("A1", leafChange.NewState.GetValueOrDefault());
        Assert.Equal("A1", subscription.Current.GetValueOrDefault());

        // Act: a structural write still runs the full walk and retracks the suffix.
        var childB = new Node { Name = "B0" };
        root.Child = childB;

        // Assert: retracked; the listener moved to the new suffix.
        Assert.Equal(2, events.Count);
        Assert.Equal(SubjectPathChangeKind.PathChange, events[1].Kind);
        Assert.Equal("A1", events[1].OldState.GetValueOrDefault());
        Assert.Equal("B0", events[1].NewState.GetValueOrDefault());
        Assert.False(HasListener(childA, nameof(Node.Name)));
        Assert.True(HasListener(childB, nameof(Node.Name)));

        // Act & Assert: leaf writes on the retracked suffix deliver; the replaced subject is inert.
        childB.Name = "B1";
        Assert.Equal(3, events.Count);
        Assert.Equal(SubjectPathChangeKind.ValueChange, events[2].Kind);
        Assert.Equal("B0", events[2].OldState.GetValueOrDefault());
        Assert.Equal("B1", events[2].NewState.GetValueOrDefault());

        childA.Name = "A9";
        Assert.Equal(3, events.Count);
    }

    private static bool HasListener(IInterceptorSubject subject, string propertyName) =>
        new PropertyReference(subject, propertyName)
            .TryGetPropertyData(PropertyChangeSubscription.ListenersKey, out _);
}
