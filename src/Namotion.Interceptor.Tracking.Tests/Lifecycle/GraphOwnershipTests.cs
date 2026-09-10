using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Tests for the graph ownership model: occurrence-aware edges, exact-context authority,
/// provisional anchors, and reachability-based release including cycles.
/// </summary>
public class GraphOwnershipTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    [Fact]
    public void WhenSubjectAppearsTwiceInCollection_ThenReferenceCountIsTwo()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var a = new Person { FirstName = "A" };
        var b = new Person { FirstName = "B" };

        // Act
        parent.Children = [a, a, b];

        // Assert
        Assert.Equal(2, a.GetReferenceCount());
        Assert.Equal(1, b.GetReferenceCount());
    }

    [Fact]
    public void WhenSubjectAppearsTwiceInCollection_ThenItHasTwoParentEntries()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var a = new Person { FirstName = "A" };

        // Act
        parent.Children = [a, a];

        // Assert
        var parents = a.GetParents();
        Assert.Equal(2, parents.Length);
        Assert.Contains(parents, p => Equals(p.Index, 0));
        Assert.Contains(parents, p => Equals(p.Index, 1));
    }

    [Fact]
    public void WhenDuplicateOccurrencesAreRemoved_ThenSubjectDetachesWithNoLeakedParentEntry()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var a = new Person { FirstName = "A" };
        var b = new Person { FirstName = "B" };
        parent.Children = [a, a, b];

        // Act
        parent.Children = [b];

        // Assert
        Assert.Equal(0, a.GetReferenceCount());
        Assert.Empty(a.GetParents());
        Assert.Null(a.TryGetContext());
        Assert.Equal(1, b.GetReferenceCount());
        Assert.Single(b.GetParents());
    }

    [Fact]
    public void WhenCollectionIsReordered_ThenParentIndicesAreRefreshed()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var a = new Person { FirstName = "A" };
        var b = new Person { FirstName = "B" };
        parent.Children = [a, b];

        // Act
        parent.Children = [b, a];

        // Assert
        var parentsOfA = a.GetParents();
        var parentsOfB = b.GetParents();
        Assert.Single(parentsOfA);
        Assert.Single(parentsOfB);
        Assert.Equal(1, parentsOfA[0].Index);
        Assert.Equal(0, parentsOfB[0].Index);
    }

    [Fact]
    public void WhenReorderedDuplicatesShrink_ThenRemainingParentIndicesAreRefreshed()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var a = new Person { FirstName = "A" };
        var b = new Person { FirstName = "B" };
        parent.Children = [a, a, b];

        // Act
        parent.Children = [b, a];

        // Assert
        Assert.Equal(1, a.GetReferenceCount());
        var parentsOfA = a.GetParents();
        Assert.Single(parentsOfA);
        Assert.Equal(1, parentsOfA[0].Index);
        Assert.Equal(0, b.GetParents()[0].Index);
    }

    [Fact]
    public void WhenChildHoldsBackEdgeToConstructorRoot_ThenReplacingTheChildKeepsTheRoot()
    {
        // Arrange: child.Mother = root is a back-edge; it must not consume the root's
        // provisional anchor, or replacing the child would release the whole tree.
        var context = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var child = new Person { FirstName = "C" };
        root.Father = child;
        child.Mother = root;

        // Act
        root.Father = new Person { FirstName = "N" };

        // Assert
        Assert.Same(context, root.TryGetContext());
        Assert.Equal(0, child.GetReferenceCount());
        Assert.Null(child.TryGetContext());
    }

    [Fact]
    public void WhenOrphanedSelfCycle_ThenSubjectIsReleased()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var child = new Person { FirstName = "C" };
        parent.Father = child;
        child.Father = child;

        // Act
        parent.Father = null;

        // Assert
        Assert.Equal(0, child.GetReferenceCount());
        Assert.Null(child.TryGetContext());
    }

    [Fact]
    public void WhenInternalCycleIsOrphaned_ThenCycleIsReleased()
    {
        // Arrange: parent -> a -> b <-> c
        var context = CreateContext();
        var c = new Person { FirstName = "C" };
        var b = new Person { FirstName = "B", Mother = c };
        c.Mother = b;
        var a = new Person { FirstName = "A", Mother = b };
        var parent = new Person(context) { FirstName = "P", Father = a };

        var detached = new List<IInterceptorSubject>();
        context.TryGetLifecycleInterceptor()!.SubjectDetaching += change =>
        {
            if (change.IsContextDetach)
            {
                detached.Add(change.Subject);
            }
        };

        // Act
        parent.Father = null;

        // Assert: a, b and c are all released, including the b <-> c cycle
        Assert.Contains(a, detached);
        Assert.Contains(b, detached);
        Assert.Contains(c, detached);
        Assert.Equal(0, b.GetReferenceCount());
        Assert.Equal(0, c.GetReferenceCount());
        Assert.Null(b.TryGetContext());
        Assert.Null(c.TryGetContext());
    }

    /// <summary>parent1 -> b, b &lt;-&gt; c, parent2 -> c.</summary>
    private static (Person Parent1, Person Parent2, Person B, Person C) CreateCycleUnderTwoRoots(
        IInterceptorSubjectContext context)
    {
        var c = new Person { FirstName = "C" };
        var b = new Person { FirstName = "B", Mother = c };
        c.Mother = b;
        var parent1 = new Person(context) { FirstName = "P1", Father = b };
        var parent2 = new Person(context) { FirstName = "P2", Father = c };
        return (parent1, parent2, b, c);
    }

    [Fact]
    public void WhenCycleLosesOneOfTwoRoots_ThenItIsRetained()
    {
        // Arrange
        var context = CreateContext();
        var (parent1, _, b, c) = CreateCycleUnderTwoRoots(context);

        // Act: parent1 lets go, parent2 still reaches the cycle through c
        parent1.Father = null;

        // Assert
        Assert.NotNull(b.TryGetContext());
        Assert.NotNull(c.TryGetContext());
        Assert.Equal(1, b.GetReferenceCount());
        Assert.Equal(2, c.GetReferenceCount());
    }

    [Fact]
    public void WhenCycleLosesBothRoots_ThenItIsReleased()
    {
        // Arrange
        var context = CreateContext();
        var (parent1, parent2, b, c) = CreateCycleUnderTwoRoots(context);
        parent1.Father = null;

        // Act
        parent2.Father = null;

        // Assert
        Assert.Null(b.TryGetContext());
        Assert.Null(c.TryGetContext());
        Assert.Equal(0, b.GetReferenceCount());
        Assert.Equal(0, c.GetReferenceCount());
    }

    /// <summary>root -> left -> shared and root -> right -> shared.</summary>
    private static (Person Left, Person Right, Person Shared) CreateSharedDagChild(
        IInterceptorSubjectContext context)
    {
        var shared = new Person { FirstName = "S" };
        var left = new Person { FirstName = "L", Father = shared };
        var right = new Person { FirstName = "R", Father = shared };
        _ = new Person(context) { FirstName = "Root", Mother = left, Father = right };
        return (left, right, shared);
    }

    [Fact]
    public void WhenSharedDagChildLosesOneParent_ThenItIsRetained()
    {
        // Arrange
        var context = CreateContext();
        var (left, _, shared) = CreateSharedDagChild(context);

        // Act
        left.Father = null;

        // Assert
        Assert.Equal(1, shared.GetReferenceCount());
        Assert.NotNull(shared.TryGetContext());
    }

    [Fact]
    public void WhenSharedDagChildLosesBothParents_ThenItIsReleased()
    {
        // Arrange
        var context = CreateContext();
        var (left, right, shared) = CreateSharedDagChild(context);
        left.Father = null;

        // Act
        right.Father = null;

        // Assert
        Assert.Equal(0, shared.GetReferenceCount());
        Assert.Null(shared.TryGetContext());
    }

    [Fact]
    public void WhenSubtreeIsSharedByTwoRoots_ThenDetachingOneRootRetainsIt()
    {
        // Arrange
        var context = CreateContext();
        var shared = new Person { FirstName = "S" };
        var root1 = new Person(context) { FirstName = "R1", Father = shared };
        var root2 = new Person(context) { FirstName = "R2", Father = shared };
        Assert.Equal(2, shared.GetReferenceCount());

        // Act
        root1.Father = null;

        // Assert
        Assert.NotNull(shared.TryGetContext());
        Assert.Equal(1, shared.GetReferenceCount());
    }

    [Fact]
    public void WhenSubjectIsAttached_ThenTryGetContextReturnsTheExactContext()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var child = new Person { FirstName = "C" };

        // Act
        parent.Father = child;

        // Assert
        Assert.Same(context, parent.TryGetContext());
        Assert.Same(context, child.TryGetContext());
    }

    [Fact]
    public void WhenConstructorAttachedSubjectGainsAndLosesEdge_ThenItDetaches()
    {
        // Arrange: the constructor anchor is provisional and is consumed by the first edge
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var child = new Person(context) { FirstName = "C" };
        Assert.Same(context, child.TryGetContext());

        // Act
        parent.Father = child;
        parent.Father = null;

        // Assert
        Assert.Null(child.TryGetContext());
        Assert.Equal(0, child.GetReferenceCount());
    }

    [Fact]
    public void WhenAnExplicitAttachConsumesAProvisionalAnchor_ThenHandlersObserveTheEdgeWithTheAnchorConsumed()
    {
        // Arrange: the anchor is what a handler reads to tell a root from a held subject, so it is
        // settled before the edge is published, on the attach path exactly as on the write path.
        var context = CreateContext();
        var provisional = new Person(context) { FirstName = "P" };
        var root = new Person { FirstName = "R", Father = provisional };
        SubjectAttachmentAnchorKind? anchorSeenByHandler = null;
        context.AddService<ILifecycleHandler>(new DelegateLifecycleHandler(change =>
        {
            if (ReferenceEquals(change.Subject, provisional) && change.IsPropertyReferenceAdded)
            {
                anchorSeenByHandler = change.Subject.Executor.AttachmentAnchor;
            }
        }));

        // Act
        root.AttachToContext(context);

        // Assert
        Assert.Equal(SubjectAttachmentAnchorKind.None, anchorSeenByHandler);
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)provisional).Executor.AttachmentAnchor);
    }

    [Fact]
    public void WhenAProvisionalRootIsAttachedWithABackReference_ThenTheBackEdgeDoesNotConsumeItsAnchor()
    {
        // Arrange: the everyday back reference child.Parent = root, seeded by an explicit attach
        // with a provisional anchor rather than written afterwards. The back edge's parent reaches
        // no anchor other than the root's own, so it must not consume that anchor.
        var context = CreateContext();
        var root = new Person { FirstName = "R" };
        var child = new Person { FirstName = "C", Father = root };
        root.Father = child;

        // Act
        root.AttachToContext(context, SubjectAttachmentAnchorKind.Provisional);
        root.Father = null;

        // Assert: the root outlives the child it no longer references
        Assert.Same(context, root.TryGetContext());
        Assert.Equal(SubjectAttachmentAnchorKind.Provisional, ((IInterceptorSubject)root).Executor.AttachmentAnchor);
        Assert.Null(child.TryGetContext());
    }

    [Fact]
    public void WhenExplicitlyAttachedSubjectLosesItsEdge_ThenItStaysAttached()
    {
        // Arrange: an explicit anchor is never cleared by edge changes
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var child = new Person { FirstName = "C" };
        child.AttachToContext(context);
        Assert.Same(context, child.TryGetContext());

        // Act
        parent.Father = child;
        parent.Father = null;

        // Assert
        Assert.Same(context, child.TryGetContext());
    }

    [Fact]
    public void WhenConstructorAttachedSubjectIsPromotedToExplicit_ThenItSurvivesEdgeRemoval()
    {
        // Arrange: promoting a subject that is already in the graph sets an explicit anchor without
        // repeating its attach callbacks, and the explicit anchor outlives the edge that adopted it
        // where the provisional one would not have.
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var child = new Person(context) { FirstName = "C" };
        child.AttachToContext(context);

        // Act
        parent.Father = child;
        parent.Father = null;

        // Assert: the explicit anchor keeps the subject attached
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(0, child.GetReferenceCount());
    }

    [Fact]
    public void WhenPromotedSubjectHoldsASubtree_ThenTheSubtreeIsRetained()
    {
        // Arrange: a subject promoted to an explicit root after it entered the graph keeps its
        // own children reachable even when an unrelated removal runs the reachability scan.
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var promoted = new Person { FirstName = "X" };
        var held = new Person { FirstName = "H" };
        parent.Father = promoted;
        promoted.Father = held;
        promoted.AttachToContext(context);

        // Act: the promoted subject loses its edge from the parent; its explicit anchor must
        // keep both it and its child attached.
        parent.Father = null;

        // Assert
        Assert.Same(context, promoted.TryGetContext());
        Assert.Same(context, held.TryGetContext());
        Assert.Equal(1, held.GetReferenceCount());
    }

    [Fact]
    public void WhenSubjectFromAnotherContextIsAssigned_ThenWriteIsRejectedBeforeCommit()
    {
        // Arrange
        var context1 = CreateContext();
        var context2 = CreateContext();
        var parent = new Person(context1) { FirstName = "P" };
        var foreign = new Person(context2) { FirstName = "F" };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => parent.Father = foreign);
        Assert.Null(parent.Father);
    }

    [Fact]
    public void WhenAddedOccurrenceCollidesWithRetainedIndex_ThenBothOccurrencesAreTracked()
    {
        // Arrange: after [b, a] the subject holds the stale index 1; the addition in [a, a] is
        // also attached at index 1, so the two must not be conflated into one occurrence.
        var referenceAddedEvents = new List<IInterceptorSubject>();
        var context = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (change.IsPropertyReferenceAdded)
                {
                    referenceAddedEvents.Add(change.Subject);
                }
            }), _ => false);

        var parent = new Person(context) { FirstName = "P" };
        var a = new Person { FirstName = "A" };
        var b = new Person { FirstName = "B" };

        // Act
        parent.Children = [b, a];
        parent.Children = [a, a];

        // Assert
        Assert.Equal(2, a.GetReferenceCount());
        Assert.Equal(2, referenceAddedEvents.Count(subject => ReferenceEquals(subject, a)));
        var parents = a.GetParents();
        Assert.Equal(2, parents.Length);
        Assert.Contains(parents, p => Equals(p.Index, 0));
        Assert.Contains(parents, p => Equals(p.Index, 1));
    }

    [Fact]
    public void WhenCollidedDuplicateShrinksBackToOne_ThenSubjectStaysAttached()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var a = new Person { FirstName = "A" };
        var b = new Person { FirstName = "B" };
        parent.Children = [b, a];
        parent.Children = [a, a];

        // Act
        parent.Children = [a];

        // Assert: one live occurrence remains, so the subject must stay attached.
        Assert.Equal(1, a.GetReferenceCount());
        Assert.NotNull(a.TryGetContext());
        Assert.Single(a.GetParents());
    }

    [Fact]
    public void WhenAddedOccurrenceCollidesWithinTripleOccurrence_ThenAllOccurrencesAreTracked()
    {
        // Arrange: the subject already occupies indices 0 and 2; the addition lands on index 2.
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var a = new Person { FirstName = "A" };
        var b = new Person { FirstName = "B" };
        parent.Children = [a, b, a];

        // Act
        parent.Children = [a, a, a];

        // Assert
        Assert.Equal(3, a.GetReferenceCount());
        var parents = a.GetParents();
        Assert.Equal(3, parents.Length);
        Assert.Contains(parents, p => Equals(p.Index, 0));
        Assert.Contains(parents, p => Equals(p.Index, 1));
        Assert.Contains(parents, p => Equals(p.Index, 2));
        Assert.Equal(0, b.GetReferenceCount());
        Assert.Null(b.TryGetContext());
    }

    [Fact]
    public void WhenLifecycleCallbackWritesStructuralProperty_ThenTheGuardRejectsIt()
    {
        // Arrange: a handler reacting to b's removal writes another structural property of the
        // same graph. Subject lifecycle callbacks must not write structural properties, and the
        // guard is live in every build, so the write is rejected before its backing writer runs
        // rather than re-entering the reconciler on half-updated state.
        var callbackObserved = false;
        Exception? callbackException = null;
        Person? root = null;
        Person? b = null;
        var context = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (callbackObserved || !change.IsPropertyReferenceRemoved || !ReferenceEquals(change.Subject, b))
                {
                    return;
                }

                callbackObserved = true;
                callbackException = Record.Exception(() => root!.Father = null);
            }), _ => false);

        root = new Person(context) { FirstName = "R" };
        var parent = new Person { FirstName = "P" };
        b = new Person { FirstName = "B" };
        var a = new Person { FirstName = "A" };
        root.Father = parent;
        parent.Children = [b, a];

        // Act: the removal of b publishes the callback; the callback's write is rejected, so the
        // outer write itself completes normally.
        parent.Children = [a];

        // Assert
        Assert.True(callbackObserved);
        Assert.IsType<LifecycleContractViolationException>(callbackException);
        Assert.Contains("lifecycle callback must not change graph topology", callbackException.Message);
        Assert.Same(context, root.TryGetContext());
        Assert.Same(context, parent.TryGetContext());
        Assert.Equal(1, a.GetReferenceCount());
        Assert.Equal(0, b.GetReferenceCount());
        Assert.Null(b.TryGetContext());
    }

    [Fact]
    public void WhenPropertyDetachCallbackReleasesTheWritingParent_ThenTheGuardRejectsIt()
    {
        // Arrange: a detach property callback reacting to b's release tries to release the
        // writing parent itself. Property lifecycle callbacks are not exempt from the callback
        // contract, so the write is rejected mid-reconcile and the outer write completes on a
        // consistent graph instead of descending from a released parent.
        var callbackObserved = false;
        Exception? callbackException = null;
        Person? root = null;
        Person? b = null;
        var context = CreateContext()
            .WithService(() => new DelegatePropertyDetachHandler(change =>
            {
                if (!callbackObserved && ReferenceEquals(change.Subject, b))
                {
                    callbackObserved = true;
                    callbackException = Record.Exception(() => root!.Father = null);
                }
            }), _ => false);

        root = new Person(context) { FirstName = "R" };
        var parent = new Person { FirstName = "P" };
        b = new Person { FirstName = "B" };
        var a = new Person { FirstName = "A" };
        root.Father = parent;
        parent.Children = [b, a];

        // Act
        parent.Children = [a];

        // Assert: the reentrant release was rejected, so the subtree stays attached and settled.
        Assert.True(callbackObserved);
        Assert.IsType<LifecycleContractViolationException>(callbackException);
        Assert.Same(parent, root.Father);
        Assert.Same(context, parent.TryGetContext());
        Assert.Same(context, a.TryGetContext());
        Assert.Equal(1, a.GetReferenceCount());
        Assert.Single(a.GetParents());
        Assert.Equal(0, b.GetReferenceCount());
        Assert.Null(b.TryGetContext());
    }

    [Fact]
    public void WhenStoredIncomingIndexLagsTheCommittedValue_ThenSamePropertyFallbackDrainsTheEdge()
    {
        // Arrange: a reconcile commits the property's new value before it refreshes the retained
        // edges' stored indices, so inside that window a release descent can drain a committed
        // edge whose new index the subject has not adopted yet. The callback contract forbids
        // the reentrant graph shape that used to cover this end to end, so the fallback is
        // pinned directly here until its stored-index-lag justification is independently
        // retired.
        var parent = new Person { FirstName = "P" };
        var property = new PropertyReference(parent, nameof(Person.Children));
        var ownership = new SubjectOwnership();
        ownership.AddIncoming(property, 1);

        // Act: the committed value holds the subject at index 0, the stored edge still says 1.
        var removed = ownership.RemoveIncoming(property, 0);

        // Assert
        Assert.True(removed);
        Assert.Equal(0, ownership.IncomingCount);
    }

    [Fact]
    public void WhenRemovingAnEdgeOfAnotherProperty_ThenTheSamePropertyFallbackDoesNotApply()
    {
        // Arrange: the fallback is scoped to occurrences of the same property; an edge of a
        // different property must never be drained in its place.
        var parent = new Person { FirstName = "P" };
        var ownership = new SubjectOwnership();
        ownership.AddIncoming(new PropertyReference(parent, nameof(Person.Children)), 0);

        // Act
        var removed = ownership.RemoveIncoming(new PropertyReference(parent, nameof(Person.Father)), 0);

        // Assert
        Assert.False(removed);
        Assert.Equal(1, ownership.IncomingCount);
    }

    [Fact]
    public void WhenTheLaggingEdgeIsNotTheFirstSlot_ThenSamePropertyFallbackStillDrainsIt()
    {
        // Arrange: the lagging edge lives in the additional-edges list because another property
        // occupies the first slot, so the fallback must find it there.
        var parent = new Person { FirstName = "P" };
        var property = new PropertyReference(parent, nameof(Person.Children));
        var ownership = new SubjectOwnership();
        ownership.AddIncoming(new PropertyReference(parent, nameof(Person.Father)), null);
        ownership.AddIncoming(property, 2);

        // Act: the committed value holds the subject at index 0, the stored edge still says 2.
        var removed = ownership.RemoveIncoming(property, 0);

        // Assert
        Assert.True(removed);
        Assert.Equal(1, ownership.IncomingCount);
    }

    [Fact]
    public void WhenLifecycleCallbackWritesScalarProperty_ThenTheWriteIsAllowed()
    {
        // Arrange: scalar writes from callbacks stay supported; only structural writes are the
        // contract violation.
        Person? root = null;
        var context = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                // The root's own construction-time attach fires before the local is assigned.
                if (change.IsContextAttach && root is not null)
                {
                    root.LastName = "attached";
                }
            }), _ => false);

        root = new Person(context) { FirstName = "R" };

        // Act
        root.Father = new Person { FirstName = "F" };

        // Assert
        Assert.Equal("attached", root.LastName);
    }

    [Fact]
    public void WhenRootIsDetachedWhileStillReferenced_ThenItStaysAttachedThroughTheEdge()
    {
        // Arrange: an explicitly attached subject that is also referenced by a live parent
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var child = new Person { FirstName = "C" };
        child.AttachToContext(context);
        parent.Father = child;

        // Act: remove the explicit anchor; the structural edge still holds the subject
        child.DetachFromContext(context);

        // Assert
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenDetachedRootLosesItsLastEdge_ThenItIsReleased()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var child = new Person { FirstName = "C" };
        child.AttachToContext(context);
        parent.Father = child;
        child.DetachFromContext(context);

        // Act
        parent.Father = null;

        // Assert
        Assert.Null(child.TryGetContext());
    }

    [Fact]
    public void WhenADerivedPartialPropertyStoresASubject_ThenTheSubjectIsOwned()
    {
        // Arrange: a [Derived] partial property is generated with a backing field, so it is the
        // only thing holding whatever is assigned to it. Nothing else can own that subject.
        var context = CreateContext();
        var holder = new StoringDerivedSubject(context);
        var child = new Person { FirstName = "C" };

        // Act
        holder.Current = child;

        // Assert
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
        var parent = Assert.Single(child.GetParents());
        Assert.Equal(nameof(StoringDerivedSubject.Current), parent.Property.Name);
    }

    [Fact]
    public void WhenADerivedPartialPropertyStoringASubjectIsCleared_ThenTheSubjectIsReleased()
    {
        // Arrange
        var context = CreateContext();
        var holder = new StoringDerivedSubject(context);
        var child = new Person { FirstName = "C" };
        holder.Current = child;

        // Act
        holder.Current = null;

        // Assert
        Assert.Null(child.TryGetContext());
        Assert.Equal(0, child.GetReferenceCount());
        Assert.Empty(child.GetParents());
    }
}
