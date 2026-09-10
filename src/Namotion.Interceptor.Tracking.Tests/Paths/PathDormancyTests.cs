using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Paths;
using Namotion.Interceptor.Tracking.Tests.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

// Dormancy is inherited from the per-property primitive: a subject not in a notifying context does not
// dispatch, so a path rooted in a detached subtree delivers nothing until it re-attaches. The revalidating
// walk heals the chain on the next delivered callback, Current is a fresh side-effect-free walk, and a
// traversal failure (throwing getter or container) resolves as unresolved rather than propagating. These
// tests verify those emergent behaviors; no production change is expected.
[Collection(PerPropertySubscriptionCollection.Name)]
public class PathDormancyTests
{
    public PathDormancyTests() => PropertyChangeSubscriptions.ResetForTests();

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // --- Scenario 1: exclusively-owned subtree dormancy -----------------------------------------------

    [Fact]
    public void WhenRootDetached_ThenNoDeliveryWhileDormantAndCurrentStaysFreshAndReattachResumes()
    {
        // Arrange: outer holds root as an exclusively-owned subtree (root -> childA). Assigning outer.Child
        // attaches the whole subtree via context inheritance; nulling it detaches (dormancy).
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var outer = new Node(context) { Name = "outer" };
        var childA = new Node { Name = "A0" };
        var root = new Node { Name = "root", Child = childA };
        outer.Child = root; // attach: root and childA inherit the notifying context

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        // Sanity: while attached, a leaf write delivers.
        childA.Name = "A1";
        Assert.Single(events);
        Assert.Equal("A1", subscription.Current.GetValueOrDefault());

        // Act: detach the root subtree -> dormant.
        outer.Child = null;
        childA.Name = "A2";

        // Assert: no delivery while dormant, but Current is a fresh walk that reflects the real graph.
        Assert.Single(events);
        Assert.Equal("A2", subscription.Current.GetValueOrDefault());

        // Act: re-attach -> delivery resumes on the still-installed chain (it follows the subject instance).
        outer.Child = root;
        childA.Name = "A3";

        // Assert: the write after re-attach delivers. Old is the last delivered value ("A1"): the dormant
        // "A2" was missed, so the observer converges rather than replaying every skipped write.
        Assert.Equal(2, events.Count);
        Assert.Equal(SubjectPathChangeKind.ValueChange, events[1].Kind);
        Assert.Equal("A1", events[1].OldState.GetValueOrDefault());
        Assert.Equal("A3", events[1].NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenStructuralChangeWhileDormant_ThenMissedAndChainResyncsOnNextDeliveredCallback()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var outer = new Node(context) { Name = "outer" };
        var childA = new Node { Name = "A0" };
        var root = new Node { Name = "root", Child = childA };
        outer.Child = root;

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        // Act: detach, then replace the watched intermediate while dormant.
        outer.Child = null;
        var childB = new Node { Name = "B0" };
        root.Child = childB; // structural change, missed (no dispatch while dormant)

        // Assert: missed at the time it happens. No event; the subscribed leaf listener is still on the stale
        // childA (never learned about childB); Current is nonetheless fresh and reflects childB.
        Assert.Empty(events);
        Assert.True(HasListener(childA, nameof(Node.Name)));
        Assert.False(HasListener(childB, nameof(Node.Name)));
        Assert.Equal("B0", subscription.Current.GetValueOrDefault());

        // Act: re-attach. Re-attach alone delivers nothing (no watched write occurred).
        outer.Child = root;
        Assert.Empty(events);
        Assert.Equal("B0", subscription.Current.GetValueOrDefault());

        // Act: the next delivered callback is a watched upper-segment write; its revalidating walk re-syncs
        // the stale chain straight onto current truth (childC), skipping the transient childB.
        var childC = new Node { Name = "C0" };
        root.Child = childC;

        // Assert: re-synced. One PathChange from the stale baseline to the true current leaf; the leaf
        // listener moved off childA onto childC.
        var resync = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.PathChange, resync.Kind);
        Assert.Equal("A0", resync.OldState.GetValueOrDefault());
        Assert.Equal("C0", resync.NewState.GetValueOrDefault());
        Assert.False(HasListener(childA, nameof(Node.Name)));
        Assert.True(HasListener(childC, nameof(Node.Name)));

        // Act & Assert: writes to the new suffix deliver; writes to the (detached, off-path) old subject do not.
        childC.Name = "C1";
        Assert.Equal(2, events.Count);
        Assert.Equal("C1", events[1].NewState.GetValueOrDefault());

        childA.Name = "A9";
        Assert.Equal(2, events.Count);
    }

    // --- Scenario 2: dormant-divergence heal via the stale branch -------------------------------------

    [Fact]
    public void WhenDivergedOldSuffixMovedIntoAnotherNotifyingContextAndWritten_ThenWalkRetracksToTrueStateNotOffPathValue()
    {
        // Arrange: the root path (root -> childA -> Name) plus a separate attached keeper root that will
        // adopt the OLD suffix subject so a write to it dispatches again while the chain is diverged.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var outer = new Node(context) { Name = "outer" };
        var keeper = new Node(context) { Name = "keeper" };
        var childA = new Node { Name = "A0" };
        var root = new Node { Name = "root", Child = childA };
        outer.Child = root;

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        // Act: replace the intermediate while dormant (missed), then move the old suffix childA into the
        // attached keeper so childA becomes notifying again although it is off the true path.
        outer.Child = null;
        var childB = new Node { Name = "B0" };
        root.Child = childB;
        outer.Child = root; // re-attach: the true path is now root -> childB
        keeper.Child = childA; // childA is attached again, but off-path

        Assert.Empty(events);
        Assert.True(HasListener(childA, nameof(Node.Name))); // stale leaf listener still on childA

        // Act: write the OLD subject's leaf. The stale listener fires, but the revalidating walk detects the
        // divergence (true child is childB) and must NOT deliver the off-path "A-offpath".
        childA.Name = "A-offpath";

        // Assert: the delivered value reflects the true observed state (childB.Name), retracked onto childB.
        var change = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.PathChange, change.Kind);
        Assert.Equal("A0", change.OldState.GetValueOrDefault());
        Assert.Equal("B0", change.NewState.GetValueOrDefault());
        Assert.False(HasListener(childA, nameof(Node.Name))); // retracked off the old subject
        Assert.True(HasListener(childB, nameof(Node.Name)));

        // Act & Assert: writes to the new suffix deliver; writes to the old subject do not.
        childB.Name = "B1";
        Assert.Equal(2, events.Count);
        Assert.Equal("B1", events[1].NewState.GetValueOrDefault());

        childA.Name = "A-again";
        Assert.Equal(2, events.Count);
    }

    // --- Scenario 3: divergent-retrack freshness (orchestrated) ---------------------------------------

    [Fact]
    public async Task WhenLeafWriteRacesRetrackWindow_ThenDeliveredNewReflectsRetrackReadNotPreRetrackWalk()
    {
        // Arrange: root and its initial leaf live in a full-tracking context; the retrack target leaf lives
        // in a context carrying the blocking interceptor, so a write to THAT leaf can be parked pre-commit
        // while the (unblocked) structural retrack on the root runs. A keeper reference stabilizes the
        // target's context before the race.
        var blocking = new BlockingWriteInterceptor();
        blocking.ProceedWithCommit.Set();

        var rootContext = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var targetContext = InterceptorSubjectContext.Create().WithService(() => blocking);

        var root = new Person(rootContext);
        root.Father = new Person { FirstName = "Joe" };
        var targetLeaf = new Person(targetContext) { FirstName = "Old" };
        root.Mother = targetLeaf; // keeper: stabilizes targetLeaf's context

        var deliveredLock = new object();
        var delivered = new List<SubjectPathChange<string?>>();
        using var subscription = root.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> c) =>
        {
            lock (deliveredLock)
            {
                delivered.Add(c);
            }
        }, SubjectPathValidation.Full);

        blocking.ProceedWithCommit.Reset();
        blocking.EnteredInnerChain.Reset();

        // Act: the racing write to the new suffix enters and parks BEFORE the retrack installs the suffix
        // listener; the structural retrack then reads the pre-race value ("Old"); releasing the writer commits
        // "New" AFTER the retrack's read.
        var writer = Task.Run(() => targetLeaf.FirstName = "New");
        Assert.True(blocking.EnteredInnerChain.Wait(Timeout), "the racing write never entered the inner chain");

        root.Father = targetLeaf; // retrack: subscribe-before-read installs the listener, reads "Old"

        blocking.ProceedWithCommit.Set();
        await writer.WaitAsync(Timeout);

        // Assert: quiescent consistency. Because the retrack subscribes before it reads, the racing write is
        // observed by the freshly installed listener; the settled graph, Current and the last delivered New
        // all agree on "New" rather than the stale pre-retrack "Old".
        SubjectPathChange<string?>[] observed;
        lock (deliveredLock)
        {
            observed = delivered.ToArray();
        }

        Assert.Equal("New", targetLeaf.FirstName);
        Assert.Equal("New", subscription.Current.GetValueOrDefault());
        Assert.NotEmpty(observed);
        Assert.Equal("New", observed[^1].NewState.GetValueOrDefault());
    }

    // --- Scenario 4: context-inheritance prerequisite ------------------------------------------------

    [Fact]
    public void WhenBareSubscriptionsAndChildContextFree_ThenSegmentZeroFiresButDeeperSegmentsInert()
    {
        // Arrange: a bare notifications context has no context inheritance, so a context-free child assigned
        // into the root never becomes notifying.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var root = new Node(context) { Name = "root" };
        var childA = new Node { Name = "A0" }; // context-free
        root.Child = childA;

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        // Act & Assert: the leaf below the root is inert (childA never dispatches).
        childA.Name = "A1";
        Assert.Empty(events);

        // Act & Assert: the root segment still fires; a structural write to root.Child dispatches and delivers.
        var childB = new Node { Name = "B0" };
        root.Child = childB;
        var structural = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.PathChange, structural.Kind);
        Assert.Equal("B0", structural.NewState.GetValueOrDefault());

        // Act & Assert: the new deeper segment is inert too (childB is also context-free).
        childB.Name = "B1";
        Assert.Single(events);
    }

    [Fact]
    public void WhenFullTrackingAndChildContextFree_ThenDeeperSegmentDelivers()
    {
        // Arrange: full tracking includes context inheritance, so the context-free child inherits the root's
        // notifying context on assignment.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root = new Node(context) { Name = "root" };
        var childA = new Node { Name = "A0" };
        root.Child = childA;

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        // Act
        childA.Name = "A1";

        // Assert: the same path that was inert under bare subscriptions now delivers.
        var change = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.ValueChange, change.Kind);
        Assert.Equal("A1", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenBareSubscriptionsButChildCarriesOwnNotifyingContext_ThenDeeperSegmentDelivers()
    {
        // Arrange: no context inheritance, but the child is constructed with its OWN notifying context, so it
        // dispatches independently of the root.
        var rootContext = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var childContext = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var root = new Node(rootContext) { Name = "root" };
        var childA = new Node(childContext) { Name = "A0" };
        root.Child = childA;

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        // Act
        childA.Name = "A1";

        // Assert: delivery depends only on the child being in a notifying context, whether inherited or its own.
        var change = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.ValueChange, change.Kind);
        Assert.Equal("A1", change.NewState.GetValueOrDefault());
    }

    // --- Scenario 5: newly-assigned child (dispatch-after-lifecycle prerequisite) ---------------------

    [Fact]
    public void WhenCallbackWritesJustAssignedChild_ThenThatWriteIsInterceptedAndDelivered()
    {
        // Arrange: the path is initially unresolved (Child null). The callback reacts to the structural
        // appearance by writing a property of the just-assigned, previously context-free child.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root = new Node(context) { Name = "root" };
        var child = new Node { Name = "init" }; // context-free until assigned

        var events = new List<SubjectPathChange<string>>();
        var reacted = false;
        using var subscription = root.SubscribeToPath(x => x.Child!.Name, (in SubjectPathChange<string> c) =>
        {
            events.Add(c);
            if (c.NewState.GetValueOrDefault() == "init" && !reacted)
            {
                reacted = true;
                child.Name = "reacted"; // write the freshly attached child from inside the callback
            }
        }, SubjectPathValidation.Full);

        // Act
        root.Child = child;

        // Assert: dispatch runs after lifecycle reconciliation, so the child is already attached when the
        // callback writes it; that write is intercepted and delivered as its own (flattened) event.
        Assert.Equal(2, events.Count);
        Assert.Equal(SubjectPathChangeKind.PathChange, events[0].Kind);
        Assert.Equal("init", events[0].NewState.GetValueOrDefault());
        Assert.Equal("init", events[1].OldState.GetValueOrDefault());
        Assert.Equal("reacted", events[1].NewState.GetValueOrDefault());
        Assert.Equal("reacted", child.Name);
    }

    // --- Scenario 6: derived-intermediate exclusive-hold ----------------------------------------------

    [Fact]
    public void WhenDerivedExclusiveChildWrittenBeforeRecalc_ThenNoDelivery()
    {
        // Arrange: the derived intermediate resolves to alpha, which is held only via the derived property,
        // so context inheritance never attaches it (its walk only visits intercepted properties).
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var holder = new DerivedExclusiveHolder(context);

        var events = new List<SubjectPathChange<string>>();
        using var subscription = holder.SubscribeToPath(x => x.Exclusive.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        // White-box: alpha is watched but dormant (never attached), so its leaf write does not dispatch.
        Assert.True(HasListener(holder.Alpha, nameof(EqualityNode.Name)));

        // Act
        holder.Alpha.Name = "A1";

        // Assert: no delivery before any recalculation.
        Assert.Empty(events);
        Assert.Equal("A1", subscription.Current.GetValueOrDefault());
    }

    [Fact]
    public void WhenDerivedInputForcesDistinctRecalc_ThenSuffixAttachesHealsAndDelivers()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var holder = new DerivedExclusiveHolder(context);

        var events = new List<SubjectPathChange<string>>();
        using var subscription = holder.SubscribeToPath(x => x.Exclusive.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        holder.Alpha.Name = "A1"; // dormant, no delivery
        Assert.Empty(events);

        // Act: change the derived input so Exclusive recomputes to beta (Equals-distinct from alpha). The
        // derived write reconciles through the lifecycle interceptor, attaching beta, and the path retracks.
        holder.Selector = 1;

        // Assert: the suffix attaches and heals onto beta with a PathChange to its leaf.
        var heal = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.PathChange, heal.Kind);
        Assert.Equal("B0", heal.NewState.GetValueOrDefault());
        Assert.True(HasListener(holder.Beta, nameof(EqualityNode.Name)));
        Assert.False(HasListener(holder.Alpha, nameof(EqualityNode.Name)));

        // Act & Assert: beta is now attached (notifying), so a write to its leaf subsequently delivers.
        holder.Beta.Name = "B1";
        Assert.Equal(2, events.Count);
        Assert.Equal(SubjectPathChangeKind.ValueChange, events[1].Kind);
        Assert.Equal("B0", events[1].OldState.GetValueOrDefault());
        Assert.Equal("B1", events[1].NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenDerivedRecomputesToDistinctButEqualInstance_ThenNoAttachOrHeal()
    {
        // Arrange: bring the chain live on beta first (distinct recalc).
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var holder = new DerivedExclusiveHolder(context);

        var events = new List<SubjectPathChange<string>>();
        using var subscription = holder.SubscribeToPath(x => x.Exclusive.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        holder.Selector = 1; // heal onto beta
        Assert.Single(events);
        events.Clear();

        // Act: recompute to betaClone, a distinct instance that is Equals-equal to beta. The equality-check
        // handler suppresses the derived write, so nothing dispatches.
        holder.Selector = 2;

        // Assert: the equal recompute did not attach or heal. It delivered no event and the subscribed chain
        // was not retracked (still on beta, betaClone never wired into the chain). A distinct recompute would
        // instead have dispatched, retracked and delivered (verified by the distinct-recalc test above).
        Assert.Empty(events);
        Assert.True(HasListener(holder.Beta, nameof(EqualityNode.Name)));
        Assert.False(HasListener(holder.BetaClone, nameof(EqualityNode.Name)));

        // Current is a fresh walk, so it does reflect the getter's genuinely distinct betaClone: the
        // suppression is at the event/heal level, not because the instances are reference-equal.
        Assert.Equal("C0", subscription.Current.GetValueOrDefault());
    }

    [Fact]
    public void WhenDerivedIntermediateAliasesInterceptedChild_ThenDeliversImmediately()
    {
        // Arrange: contrast to the exclusive-hold. Thrower aliases the intercepted Backing, so the child is
        // attached via that alias and is notifying from the start (no recalculation needed).
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var holder = new GetterThrowHolder(context);
        var child = new Node { Name = "C0" };
        holder.Backing = child;

        var events = new List<SubjectPathChange<string>>();
        using var subscription = holder.SubscribeToPath(x => x.Thrower!.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        // Act
        child.Name = "C1";

        // Assert
        var change = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.ValueChange, change.Kind);
        Assert.Equal("C1", change.NewState.GetValueOrDefault());
    }

    // --- Scenario 7: slot-identity guard --------------------------------------------------------------

    [Fact]
    public void WhenLowerSuffixRebuilt_ThenSurvivingUpperObserverStillDeliversAndRebuiltPositionReplaced()
    {
        // Arrange: a three-segment path root -> M -> L -> Name. Only the leaf's subject (L) diverges on the
        // first structural write, so the suffix at position 2 is rebuilt while positions 0 and 1 survive.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var leafL = new Node { Name = "L0" };
        var midM = new Node { Name = "M", Child = leafL };
        var root = new Node(context) { Name = "root", Child = midM };

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Child!.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        var observersBefore = GetSegmentObservers(subscription);
        var upper0 = observersBefore.GetValue(0);
        var upper1 = observersBefore.GetValue(1);
        var leaf2 = observersBefore.GetValue(2);

        // Act: replace M.Child (divergence at position 2) -> only the suffix at position 2 is rebuilt.
        var leafL2 = new Node { Name = "L2v" };
        midM.Child = leafL2;

        // Assert: per-position slot identity. The upper observers survive; only position 2 was replaced.
        var observersAfter = GetSegmentObservers(subscription);
        Assert.Same(upper0, observersAfter.GetValue(0));
        Assert.Same(upper1, observersAfter.GetValue(1));
        Assert.NotSame(leaf2, observersAfter.GetValue(2));
        Assert.Equal("L2v", Assert.Single(events).NewState.GetValueOrDefault());

        // Act: a legitimate callback from the SURVIVING upper segment (position 1, M.Child) must not be
        // rejected. A generation-counter guard bumped by the position-2 rebuild would wrongly ignore it.
        var leafL3 = new Node { Name = "L3v" };
        midM.Child = leafL3;

        // Assert
        Assert.Equal(2, events.Count);
        Assert.Equal("L2v", events[1].OldState.GetValueOrDefault());
        Assert.Equal("L3v", events[1].NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenSharedSubjectStructuralWriteFansOut_ThenDeliveredOnceAndChainLengthPreserved()
    {
        // Arrange: a self-referencing node watched through x => x.Child.Child.Name. The subject appears at
        // positions 0 and 1, so its Child property carries two install-order listeners.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var node = new Node(context) { Name = "N" };
        node.Child = node;
        var other = new Node(context) { Name = "Y" };
        other.Child = other;

        var events = new List<SubjectPathChange<string>>();
        using var subscription = node.SubscribeToPath(x => x.Child!.Child!.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        Assert.Equal(3, PropertyChangeSubscriptions.ReadSubscriptionCount());
        var position1Before = GetSegmentObservers(subscription).GetValue(1);

        // Act: one structural write fans out over the install-order snapshot [position 0, position 1].
        // Position 0 fires first and its retrack disposes position 1's listener; the trailing in-snapshot
        // dispatch of that now-disposed subscription is skipped by the primitive's own cleared-observer guard
        // (PropertyChangeSubscription.Dispatch reads a nulled observer). This test therefore pins exactly-once
        // delivery and no listener leak under fan-out; the path-level slot-identity guard is exercised
        // separately, under concurrency, by WhenLateCallbackArrivesForRetrackedObserver... below.
        node.Child = other;

        // Assert: exactly one delivery for the single write, and position 1's observer was replaced, with the
        // chain length preserved (no leak).
        var change = Assert.Single(events);
        Assert.Equal("N", change.OldState.GetValueOrDefault());
        Assert.Equal("Y", change.NewState.GetValueOrDefault());
        Assert.NotSame(position1Before, GetSegmentObservers(subscription).GetValue(1));
        Assert.Equal(3, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public async Task WhenLateCallbackArrivesForRetrackedObserver_ThenSlotIdentityGuardDropsStaleInvocation()
    {
        // The path-level slot-identity guard is a concurrency guard: it drops a callback whose observer was
        // captured live by the primitive's dispatch (into a stack local) but was then torn down and replaced
        // by a concurrent retrack before the observer ran. The synchronous fan-out case is already handled by
        // the primitive's cleared-observer guard; only a cross-thread capture-then-retrack reaches this guard.
        //
        // Construction: a structural retrack (T2) parks mid-walk holding the subscription lock, with the OLD
        // suffix listener still live (a keeper reference keeps the old child attached so writes to it still
        // dispatch). A late leaf write (T1) then captures that live observer and blocks on the subscription
        // lock. T2 is released, disposes the old observer and retracks, then T1 proceeds: its observer is now
        // stale. A read gate armed thread-affine to T1 makes T1's revalidating walk resolve UNRESOLVED, so a
        // dropped stale invocation (guard present) delivers nothing while a processed one (guard removed)
        // delivers a spurious observed->unresolved PathChange. Exactly one delivered event pins the guard.

        // Arrange
        var newChild = new Node { Name = "new" };
        var gate = new WalkGateInterceptor(newChild, nameof(Node.Name));
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithService(() => gate, _ => false);
        var oldChild = new Node { Name = "old" };
        var root = new Node(context) { Name = "root", Child = oldChild };
        var keeper = new Node(context) { Name = "keeper", Child = oldChild }; // keeps oldChild attached after the retrack

        var eventsLock = new object();
        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Name, (in SubjectPathChange<string> c) =>
        {
            lock (eventsLock)
            {
                events.Add(c);
            }
        }, SubjectPathValidation.Full);

        // Act: T2 retracks root.Child to newChild and parks inside its walk (holding the subscription lock,
        // before it tears down the old suffix listener).
        var structural = Task.Run(() => root.Child = newChild);
        Assert.True(gate.Parked.Wait(Timeout), "the structural retrack never parked in the walk");

        // T1 writes the OLD child's leaf. Because oldChild is still attached (keeper) and the old suffix
        // listener is still live (T2 has not yet torn it down), the primitive captures that observer and T1
        // blocks on the subscription lock.
        var t1 = new Thread(() => oldChild.Name = "race") { IsBackground = true };
        t1.Start();
        await AsyncTestHelpers.WaitUntilAsync(
            () => oldChild.Name == "race" && t1.ThreadState.HasFlag(ThreadState.WaitSleepJoin),
            Timeout, TimeSpan.FromMilliseconds(5),
            "the late leaf write never captured the observer and blocked on the subscription lock");

        // Arm the stale walk to throw (thread-affine to T1 only, so T2's own walks are unaffected), then
        // release the retrack. T2 disposes the old observer, retracks, and delivers old->new; T1 then runs its
        // now-stale invocation.
        gate.ThrowThread = t1;
        gate.Release.Set();

        await structural.WaitAsync(Timeout);
        Assert.True(t1.Join(Timeout), "the late leaf write never completed");
        gate.ThrowThread = null;

        // Assert: exactly one delivered event (the structural old->new). The stale late invocation was dropped
        // by the slot-identity guard. Without the guard, T1's stale walk throws (unresolved != last observed)
        // and delivers a spurious old->unresolved PathChange, making this Assert.Single fail.
        lock (eventsLock)
        {
            var change = Assert.Single(events);
            Assert.Equal(SubjectPathChangeKind.PathChange, change.Kind);
            Assert.Equal("old", change.OldState.GetValueOrDefault());
            Assert.Equal("new", change.NewState.GetValueOrDefault());
        }

        // The chain is intact after the dropped stale invocation.
        Assert.Equal("new", subscription.Current.GetValueOrDefault());
    }

    // --- Scenario 8: Current is side-effect free ------------------------------------------------------

    [Fact]
    public void WhenReadingCurrentWhileDiverged_ThenSubscribedChainIsUnchanged()
    {
        // Arrange: diverge the chain via a dormant intermediate replacement so the subscribed leaf listener
        // is on the stale childA while the true path is on childB.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var outer = new Node(context) { Name = "outer" };
        var childA = new Node { Name = "A0" };
        var root = new Node { Name = "root", Child = childA };
        outer.Child = root;

        using var subscription = root.SubscribeToPath(x => x.Child!.Name, (in SubjectPathChange<string> _) => { }, SubjectPathValidation.Full);

        outer.Child = null;
        var childB = new Node { Name = "B0" };
        root.Child = childB; // diverged: chain stale on childA, truth on childB

        var countBefore = PropertyChangeSubscriptions.ReadSubscriptionCount();
        Assert.True(HasListener(childA, nameof(Node.Name)));
        Assert.False(HasListener(childB, nameof(Node.Name)));

        // Act: read Current repeatedly. Current is a pure fresh walk; it must neither retrack nor heal.
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal("B0", subscription.Current.GetValueOrDefault());
        }

        // Assert: the subscribed chain is unchanged by the reads (still stale on childA, count preserved).
        Assert.Equal(countBefore, PropertyChangeSubscriptions.ReadSubscriptionCount());
        Assert.True(HasListener(childA, nameof(Node.Name)));
        Assert.False(HasListener(childB, nameof(Node.Name)));
    }

    // --- Scenario 9: traversal-failure carve-out (getter and container) -------------------------------

    [Fact]
    public void WhenGetterThrowsDuringWalk_ThenPublishesUnresolvedAndClearingDoesNotHealButWatchedWriteDoes()
    {
        // Arrange: Thrower aliases the intercepted Backing (so the child is attached and delivering), unless
        // the untracked ThrowOnGet toggle is set, in which case its getter throws mid-walk.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var holder = new GetterThrowHolder(context);
        var child = new Node { Name = "C0" };
        holder.Backing = child;

        var events = new List<SubjectPathChange<string>>();
        using var subscription = holder.SubscribeToPath(x => x.Thrower!.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        child.Name = "C1"; // sanity delivery
        Assert.Single(events);

        // Act: arm the getter throw in place (untracked, dispatches nothing), then trigger a walk with a
        // watched leaf write.
        holder.ThrowOnGet = true;
        child.Name = "C2";

        // Assert: the walk fails and publishes unresolved; the leaf listener is torn off the stale suffix.
        Assert.Equal(2, events.Count);
        Assert.True(events[1].OldState.IsResolved);
        Assert.False(events[1].NewState.IsResolved);
        Assert.False(HasListener(child, nameof(Node.Name)));

        // Act: clear the failing condition in place. Current now reflects truth again, but merely clearing
        // does not heal the event stream (no watched segment dispatched).
        holder.ThrowOnGet = false;
        Assert.Equal(2, events.Count);
        Assert.True(subscription.Current.IsResolved);
        Assert.Equal("C2", subscription.Current.GetValueOrDefault());

        // Act: a subsequent watched-segment callback (the derived intermediate recomputing) re-walks and heals.
        var healChild = new Node { Name = "H0" };
        holder.Backing = healChild;

        // Assert
        Assert.Equal(3, events.Count);
        Assert.False(events[2].OldState.IsResolved);
        Assert.Equal("H0", events[2].NewState.GetValueOrDefault());

        healChild.Name = "H1";
        Assert.Equal(4, events.Count);
        Assert.Equal("H1", events[3].NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenContainerComparerThrowsDuringWalk_ThenPublishesUnresolvedAndClearingDoesNotHealButWatchedWriteDoes()
    {
        // Arrange: a dictionary with a hostile comparer. The walk's TryGetValue honors the comparer, so an
        // armed comparer throws mid-walk. The toggle lives on the comparer, not on any watched property.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var comparer = new ThrowingStringComparer();
        var element = new Node { Name = "E0" };
        var root = new Node(context) { Name = "root", ByName = new Dictionary<string, Node>(comparer) { ["key"] = element } };

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.ByName["key"].Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        element.Name = "E1"; // sanity delivery
        Assert.Single(events);

        // Act: arm the comparer in place, then trigger a walk with a watched leaf write.
        comparer.Throw = true;
        element.Name = "E2";

        // Assert: publishes unresolved and tears the leaf listener off the stale suffix.
        Assert.Equal(2, events.Count);
        Assert.True(events[1].OldState.IsResolved);
        Assert.False(events[1].NewState.IsResolved);
        Assert.False(HasListener(element, nameof(Node.Name)));

        // Act: clear in place. Current reflects truth again, but clearing alone does not heal.
        comparer.Throw = false;
        Assert.Equal(2, events.Count);
        Assert.True(subscription.Current.IsResolved);
        Assert.Equal("E2", subscription.Current.GetValueOrDefault());

        // Act: a subsequent watched-segment callback (reassigning the dictionary property) re-walks and heals.
        root.ByName = new Dictionary<string, Node>(StringComparer.Ordinal) { ["key"] = element };

        // Assert
        Assert.Equal(3, events.Count);
        Assert.False(events[2].OldState.IsResolved);
        Assert.Equal("E2", events[2].NewState.GetValueOrDefault());

        element.Name = "E3";
        Assert.Equal(4, events.Count);
        Assert.Equal("E3", events[3].NewState.GetValueOrDefault());
    }

    // --- Scenario 10: traversal-failure carve-outs (non-propagation, hostile container) ---------------

    [Fact]
    public void WhenGetterThrowsDuringWalk_ThenPathUnresolvedAndWriterChainUnaffected()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var holder = new GetterThrowHolder(context);
        var child = new Node { Name = "C0" };
        holder.Backing = child;
        using var subscription = holder.SubscribeToPath(x => x.Thrower!.Name, (in SubjectPathChange<string> _) => { }, SubjectPathValidation.Full);

        holder.ThrowOnGet = true;

        // Act & Assert: the watched write triggers a walk in which the getter throws. The exception is caught
        // by the walk boundary and does NOT propagate into the writer's chain: the write itself completes.
        child.Name = "C-written";
        Assert.Equal("C-written", child.Name);
        Assert.False(subscription.Current.IsResolved);
    }

    [Fact]
    public void WhenHostileContainerMembersThrow_ThenSegmentResolvesUnresolved()
    {
        // Arrange: a hostile reference collection whose Count and indexer throw once armed. The walk resolves
        // the collection segment as unresolved rather than propagating the throw.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var element = new Node { Name = "X0" };
        var holder = new HostileContainerHolder(context) { Items = new HostileList<Node>(new[] { element }) { Throw = true } };

        // Act: subscribing must not throw (the build's child resolution swallows the container failure).
        using var subscription = holder.SubscribeToPath(x => x.Items[0].Name, (in SubjectPathChange<string> _) => { }, SubjectPathValidation.Full);

        // Assert: the segment resolves unresolved.
        Assert.False(subscription.Current.IsResolved);

        // Act & Assert: clearing the hostile condition lets a fresh walk resolve the element again.
        holder.Items.Throw = false;
        Assert.True(subscription.Current.IsResolved);
        Assert.Equal("X0", subscription.Current.GetValueOrDefault());
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private static bool HasListener(IInterceptorSubject subject, string propertyName) =>
        new PropertyReference(subject, propertyName)
            .TryGetPropertyData(PropertyChangeSubscription.ListenersKey, out _);

    private static Array GetSegmentObservers<TValue>(SubjectPathSubscription<TValue> subscription)
    {
        var chain = typeof(SubjectPathSubscription<TValue>)
            .GetField("_chain", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(subscription)!;
        return (Array)chain.GetType()
            .GetField("_segmentObservers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(chain)!;
    }

    // A read interceptor for the slot-identity concurrency test. It parks the FIRST read of one specific
    // property (the retracking walk, holding the subscription lock) so a late writer can capture the still-live
    // suffix observer, and it throws for reads on one designated thread so the late writer's stale revalidating
    // walk resolves unresolved. Thread-affinity keeps the retracking thread's own walks unaffected.
    private sealed class WalkGateInterceptor : IReadInterceptor
    {
        private readonly IInterceptorSubject _target;
        private readonly string _propertyName;
        private int _parkedOnce;

        public WalkGateInterceptor(IInterceptorSubject target, string propertyName)
        {
            _target = target;
            _propertyName = propertyName;
        }

        public ManualResetEventSlim Parked { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public volatile Thread? ThrowThread;

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            if (ReferenceEquals(context.Property.Subject, _target) && context.Property.Name == _propertyName)
            {
                if (ThrowThread is not null && ReferenceEquals(Thread.CurrentThread, ThrowThread))
                {
                    throw new InvalidOperationException("stale walk boom");
                }

                if (Interlocked.Exchange(ref _parkedOnce, 1) == 0)
                {
                    Parked.Set();
                    if (!Release.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("the walk gate was never released");
                    }
                }
            }

            return next(ref context);
        }
    }
}
