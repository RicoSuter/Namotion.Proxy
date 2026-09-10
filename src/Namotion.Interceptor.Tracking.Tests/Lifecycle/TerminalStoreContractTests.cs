using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The write protocol claims and validates the proposed component before the terminal runs, and the
/// value the terminal actually stored after it runs, so a terminal that stores a different graph
/// stores subjects the second claim covers. These pin which terminal rewrites stay legal, which must
/// be rejected, and how much of a rejected write can be taken back.
/// </summary>
public class TerminalStoreContractTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    private static OwnershipGraph GetGraph(IInterceptorSubjectContext context)
    {
        return ((LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!).Graph;
    }

    /// <summary>
    /// Pins the terminal-store contract and the one boundary it has. A terminal that stores a
    /// subject the write never proposed is a contract violation, and the rejection now comes from
    /// the claim taken over the stored value, which runs before the reconcile commits a baseline or
    /// records any ownership. So the graph is left exactly as it was, asserted per kind of state
    /// rather than through one probe.
    ///
    /// The backing field is the boundary, and the last assertion pins it deliberately rather than
    /// leaving it unchecked: the field still holds what the terminal stored, and a future change
    /// that restores it would be a contract move that should be seen rather than absorbed. The
    /// reason it cannot be restored is that the only store the framework has is the terminal this
    /// subject handed it, and this terminal ignores the value it is given, so replaying it with the
    /// pre-write value re-stores the same foreign subject and fails again. Going through the
    /// property setter is worse, being full chain re-entry with the same substitution and therefore
    /// unbounded recursion. The model is deliberately badly behaved; a terminal that is a function
    /// of its argument never reaches this boundary, because it stores only subjects the write
    /// proposed and the claim taken before the terminal already covers those.
    /// </summary>
    [Fact]
    public void WhenATerminalStoresASubjectOwnedByAnotherContext_ThenTheRejectedWriteLeavesTheGraphUnchanged()
    {
        // Arrange: the terminal substitutes a subject owned by a second context, which the claim
        // taken before the terminal never covered.
        var context = CreateContext();
        var otherContext = CreateContext();
        var parent = new SubstitutingDevice();
        ((IInterceptorSubject)parent).AttachToContext(context);
        var foreign = new SubstitutingDevice();
        ((IInterceptorSubject)foreign).AttachToContext(otherContext);
        parent.Substitute = foreign;

        // Act
        var exception = Record.Exception(() => parent.Child = new SubstitutingDevice());

        // Assert: the write is rejected.
        Assert.NotNull(exception);

        // Every kind of graph state the write would have published is absent, which is what makes
        // the rejection one that arrived before the reconcile rather than during it.
        var graph = GetGraph(context);
        var property = new PropertyReference(parent, nameof(SubstitutingDevice.Child));
        Assert.True(graph.GetBaseline(property) is null,
            "the rejected write committed the terminal's value as the property baseline");
        Assert.False(graph.IsOwned(foreign),
            "the rejected write recorded ownership for a subject owned by another context");
        Assert.Same(otherContext, ((IInterceptorSubject)foreign).TryGetContext());
        Assert.Empty(((IInterceptorSubject)foreign).GetParents());

        // The documented boundary, asserted rather than omitted so that moving it is visible.
        Assert.True(ReferenceEquals(parent.Child, foreign),
            "the backing field no longer holds what the terminal stored, so the terminal-store " +
            "contract has moved: the documented guarantee is that a rejected write leaves no " +
            "baseline, no ownership and no claim, and explicitly not that it restores the field, " +
            "because the only store the framework has is the terminal the subject handed it and a " +
            "terminal that ignores the value it is given cannot be replayed to restore anything");
    }

    /// <summary>
    /// Parity guard, not a repro: a normalizing setter that stores a reordered subset of the
    /// proposed subjects is legal and must keep working. This is also the only case that runs the
    /// stored-value claim on a value that has to pass.
    /// </summary>
    [Fact]
    public void WhenATerminalReordersAndDropsTheProposedSubjects_ThenTheWriteSucceeds()
    {
        // Arrange: the stored list is a different instance from the proposed one, so a
        // reference-equality short circuit cannot hide the rewrite.
        var context = CreateContext();
        var parent = new ReorderingDevice();
        ((IInterceptorSubject)parent).AttachToContext(context);
        var kept = new ReorderingDevice { Name = "b" };
        var alsoKept = new ReorderingDevice { Name = "a" };
        var dropped = new ReorderingDevice { Name = "dropped" };

        // Act
        parent.Children = [kept, alsoKept, dropped];

        // Assert: the kept subjects are owned in the terminal's order, not the caller's, and the
        // dropped one's provisional claim was handed back rather than becoming ownership.
        Assert.Equal(["a", "b"], parent.Children!.Select(child => child.Name));
        Assert.Same(context, ((IInterceptorSubject)kept).TryGetContext());
        Assert.Same(context, ((IInterceptorSubject)alsoKept).TryGetContext());
        Assert.Null(((IInterceptorSubject)dropped).TryGetContext());
        Assert.Empty(((IInterceptorSubject)dropped).GetParents());
    }

    /// <summary>
    /// The stored-value claim is skipped when the terminal stored what it was given, and that has to
    /// hold for an immutable array too. It does not follow from the reference-typed case: the
    /// authoritative getter boxes a struct afresh on every call, so comparing the stored value to
    /// the proposed one by reference answers false for every such write and scans the value a second
    /// time. Asserted against the identical reference-typed shape on the same subject, so the number
    /// is a comparison rather than a magic constant, and a change in how often the protocol passes
    /// over a value moves both together.
    /// </summary>
    [Fact]
    public void WhenATerminalStoresTheProposedImmutableArray_ThenItIsNotScannedAgain()
    {
        // Arrange
        var context = CreateContext();
        var device = new StoredValueClaimDevice();
        ((IInterceptorSubject)device).AttachToContext(context);
        var immutableChild = new ExecutorCountingSubject();
        var listChild = new ExecutorCountingSubject();

        // Act: the same shape assigned twice, once value-typed and once reference-typed.
        device.ImmutableChildren = [immutableChild];
        device.ListChildren = new List<ExecutorCountingSubject> { listChild };

        // Assert: every pass over a value asks each subject it holds for its attachment, so an extra
        // pass shows up as extra accesses on the child it passed over.
        Assert.Equal(listChild.ExecutorAccessCount, immutableChild.ExecutorAccessCount);
    }

    /// <summary>
    /// A value type whose equality answers about a version stamp rather than about storage must not
    /// be allowed to decide whether the stored value still needs claiming. Two such values compare
    /// equal while holding entirely different subjects, so trusting that equality would skip the
    /// claim on a graph nothing validated and commit it, which is the same hole an overridden
    /// <c>Equals</c> would open on the reference side. Such a type is claimed a second time instead,
    /// which costs one scan and cannot be wrong.
    /// </summary>
    [Fact]
    public void WhenATerminalStoresAnEquallyStampedValueHoldingAForeignSubject_ThenTheRejectedWriteLeavesTheGraphUnchanged()
    {
        // Arrange: the substitute carries the same stamp as the proposed value and a foreign child.
        var context = CreateContext();
        var otherContext = CreateContext();
        var device = new StoredValueClaimDevice();
        ((IInterceptorSubject)device).AttachToContext(context);
        var foreign = new Person { FirstName = "F" };
        ((IInterceptorSubject)foreign).AttachToContext(otherContext);
        device.StampedSubstitute = new StampedChildren(7, [foreign]);

        // Act
        var exception = Record.Exception(() => device.Stamped = new StampedChildren(7, []));

        // Assert
        Assert.NotNull(exception);
        var graph = GetGraph(context);
        Assert.False(graph.CommitsEdgeTo(new PropertyReference(device, nameof(StoredValueClaimDevice.Stamped)), foreign),
            "the rejected write committed a value the claim never validated as the property baseline");
        Assert.False(graph.IsOwned(foreign),
            "the rejected write recorded ownership for a subject owned by another context");
        Assert.Same(otherContext, ((IInterceptorSubject)foreign).TryGetContext());
        Assert.Empty(((IInterceptorSubject)foreign).GetParents());
    }
}
