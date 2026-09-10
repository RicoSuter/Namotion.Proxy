using System;
using System.Collections.Generic;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Paths;
using Namotion.Interceptor.Tracking.Tests.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

[Collection(PerPropertySubscriptionCollection.Name)]
public class PathConcurrencyTests
{
    public PathConcurrencyTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenCallbackWritesAnotherWatchedSegment_ThenNestedEventDeliveredAfterCurrentCallbackAndTotallyOrdered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var father = new Person { FirstName = "Joe" };
        var person = new Person(context) { Father = father };

        // The callback records its own entry and exit around a single nested write to a watched segment.
        // Inline (re-entrant) delivery would interleave as enter:Jack, enter:Zed, exit:Zed, exit:Jack;
        // flattened delivery keeps each callback whole and runs the nested event after the current returns.
        var log = new List<string>();
        var nestedWriteDone = false;
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> change) =>
        {
            var value = change.NewState.GetValueOrDefault();
            log.Add("enter:" + value);
            if (!nestedWriteDone)
            {
                nestedWriteDone = true;
                father.FirstName = "Zed"; // nested write to the watched leaf during the active drain
            }

            log.Add("exit:" + value);
        }, SubjectPathValidation.Full);

        // Act
        father.FirstName = "Jack";

        // Assert: the nested event is flattened (delivered after the current callback returns, not inline)
        // and the total delivery order is the FIFO of the writes.
        Assert.Equal(new[] { "enter:Jack", "exit:Jack", "enter:Zed", "exit:Zed" }, log);
    }

    [Fact]
    public void WhenCallbackThrows_ThenQueuedBacklogStrandedAndDeliveredOnNextWriteWithChainedTransitions()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var father = new Person { FirstName = "Joe" };
        var person = new Person(context) { Father = father };

        var delivered = new List<SubjectPathChange<string?>>();
        var nestedWriteDone = false;
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> change) =>
        {
            delivered.Add(change);
            if (change.NewState.GetValueOrDefault() == "Jack")
            {
                if (!nestedWriteDone)
                {
                    nestedWriteDone = true;
                    father.FirstName = "Zed"; // enqueues Jack->Zed into the drain while this callback is active
                }

                throw new InvalidOperationException("boom");
            }
        }, SubjectPathValidation.Full);

        // Act & Assert: the throwing callback propagates through the triggering write and abandons the drain.
        Assert.Throws<InvalidOperationException>(() => father.FirstName = "Jack");

        // Only the first event was delivered; the nested Jack->Zed stays stranded in the queue.
        Assert.Single(delivered);
        Assert.Equal("Jack", delivered[0].NewState.GetValueOrDefault());

        // Act: a later write recovers the stranded backlog first, then delivers its own event.
        father.FirstName = "Max";

        // Assert: the stranded Jack->Zed drains first, then Zed->Max, with chained transitions (N+1.OldState == N.NewState).
        Assert.Equal(3, delivered.Count);
        Assert.Equal("Jack", delivered[1].OldState.GetValueOrDefault());
        Assert.Equal("Zed", delivered[1].NewState.GetValueOrDefault());
        Assert.Equal("Zed", delivered[2].OldState.GetValueOrDefault());
        Assert.Equal("Max", delivered[2].NewState.GetValueOrDefault());
    }

    [Fact]
    public async Task WhenGetterWritesWatchedSegmentDuringWalk_ThenReentrancyIsDeferredAndDeliveredInTotalOrderWithoutDeadlock()
    {
        // Arrange: a two-segment path (x => x.Father!.FirstName) so both the intermediate and the leaf are
        // watched segments. A read interceptor performs, exactly once and only after arming, a single write
        // to the watched leaf DURING the leaf read of the tracker's own walk. That write dispatches
        // synchronously on the walking thread and re-enters ProcessSegmentCallback while the thread already
        // holds the subscription lock. Without the thread-affine guard the re-entrant call computes a nested
        // event under the held lock and corrupts the outer walk: the outer walk's already-captured leaf value
        // ("Jack") is delivered LAST, so the final delivered value disagrees with the settled graph
        // ("SideEffect"). With the guard the re-entrant write is deferred and delivered as a fresh, chained
        // event after the outer walk, keeping delivery totally ordered and quiescently consistent.
        var father = new Person { FirstName = "Joe" };
        var sideEffect = new SideEffectReadInterceptor(father, nameof(Person.FirstName), "SideEffect");
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => sideEffect, _ => false);
        var person = new Person(context) { Father = father };

        var deliveredLock = new object();
        var delivered = new List<SubjectPathChange<string?>>();
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> change) =>
        {
            lock (deliveredLock)
            {
                delivered.Add(change);
            }
        }, SubjectPathValidation.Full);

        // Act: arm the one-shot side effect, then trigger the outer walk with a watched-leaf write. The write
        // runs on a worker thread bounded by a timeout: a regression that reintroduces the AB-BA deadlock
        // makes WaitAsync throw TimeoutException and fails the test fast instead of hanging the whole suite.
        // On the happy path the write completes synchronously in microseconds.
        sideEffect.Arm();
        await Task.Run(() => father.FirstName = "Jack").WaitAsync(TimeSpan.FromSeconds(10));

        SubjectPathChange<string?>[] observed;
        lock (deliveredLock)
        {
            observed = delivered.ToArray();
        }

        // Assert
        // Quiescent consistency: once writes settle, the graph, the subscription's Current and the last
        // delivered New all agree on the side-effect value.
        Assert.Equal("SideEffect", father.FirstName);
        Assert.Equal("SideEffect", subscription.Current.GetValueOrDefault());
        Assert.NotEmpty(observed);
        Assert.Equal("SideEffect", observed[^1].NewState.GetValueOrDefault());

        // Total order: transitions are chained from the seed value (each event's Old is the prior event's
        // New), so the deferred side-effect event is delivered AFTER the triggering event, never inline.
        Assert.Equal("Joe", observed[0].OldState.GetValueOrDefault());
        for (var i = 1; i < observed.Length; i++)
        {
            Assert.Equal(observed[i - 1].NewState.GetValueOrDefault(), observed[i].OldState.GetValueOrDefault());
        }
    }

    [Fact]
    public async Task WhenLeafOnlyGetterReentrantlyReplacesUpperSegmentDuringWalk_ThenDeferredPassRetracks()
    {
        // Arrange: a two-segment LeafOnly path (x => x.Father!.FirstName). A read interceptor, once after
        // arming, replaces the WATCHED INTERMEDIATE (person.Father) while the leaf is being read during the
        // tracker's leaf-only re-read. That structural write dispatches synchronously and re-enters the
        // subscription on the walking (lock-holding) thread, so the reentrancy guard defers it WITHOUT
        // recording its position. The deferred re-pass must therefore run the full from-root walk rather than
        // the leaf-only fast path, so the divergence is detected and the chain retracks onto the new father;
        // otherwise the chain strands on the old father and the last delivered value disagrees with Current.
        var oldFather = new Person { FirstName = "Old" };
        var newFather = new Person { FirstName = "New" };
        var replaceFather = new UpperSegmentSideEffectReadInterceptor(oldFather, nameof(Person.FirstName), newFather);
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => replaceFather, _ => false);
        var person = new Person(context) { Father = oldFather };
        replaceFather.Bind(person);

        var deliveredLock = new object();
        var delivered = new List<SubjectPathChange<string?>>();
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> change) =>
        {
            lock (deliveredLock)
            {
                delivered.Add(change);
            }
        }, SubjectPathValidation.LeafOnly);

        // Act: arm, then a watched-leaf write triggers the leaf-only re-read; the interceptor swaps the father
        // mid-read. Bounded by a timeout so a deadlock regression fails fast instead of hanging the suite.
        replaceFather.Arm();
        await Task.Run(() => oldFather.FirstName = "Old2").WaitAsync(TimeSpan.FromSeconds(10));

        SubjectPathChange<string?>[] observed;
        lock (deliveredLock)
        {
            observed = delivered.ToArray();
        }

        // Assert: the deferred full walk retracked onto the new father. Quiescent consistency holds (the last
        // delivered New equals Current equals the new father's leaf), and the leaf listener moved off the old
        // father onto the new one.
        Assert.Equal("New", person.Father!.FirstName);
        Assert.Equal("New", subscription.Current.GetValueOrDefault());
        Assert.NotEmpty(observed);
        Assert.Equal("New", observed[^1].NewState.GetValueOrDefault());
        Assert.Equal(0, ListenerCount(oldFather, nameof(Person.FirstName)));
        Assert.Equal(1, ListenerCount(newFather, nameof(Person.FirstName)));
    }

    [Fact]
    public async Task WhenLeafWriteFollowsRetrackInstall_ThenListenerLookupFindsItAndDelivers()
    {
        // Arrange: interleaving (a). The structural retrack installs the suffix listener FIRST; the racing
        // leaf write then runs entirely after the install, so its post-commit listener lookup finds the
        // installed listener (serialized after the install by the tracker lock).
        var (root, targetLeaf, blocking) = BuildRetrackRaceGraph();
        var deliveredLock = new object();
        var delivered = new List<SubjectPathChange<string?>>();
        using var subscription = root.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> change) =>
        {
            lock (deliveredLock)
            {
                delivered.Add(change);
            }
        }, SubjectPathValidation.Full);

        blocking.ProceedWithCommit.Reset();
        blocking.EnteredInnerChain.Reset();

        // Act: retrack first (installs the leaf listener, reads "Old", delivers the path change), then the
        // racing write parked on a worker thread; releasing it commits after the install.
        root.Father = targetLeaf;

        var writer = Task.Run(() => targetLeaf.FirstName = "New");
        Assert.True(blocking.EnteredInnerChain.Wait(TimeSpan.FromSeconds(10)), "the racing write never entered the inner chain");
        blocking.ProceedWithCommit.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: quiescent consistency. The settled graph, Current and the last delivered New all agree on
        // the racing value; it was never silently dropped.
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

    [Fact]
    public async Task WhenLeafWriteEntersBeforeRetrackInstallAndCommitsAfterRead_ThenPostCommitResolutionDeliversIt()
    {
        // Arrange: interleaving (b). The racing leaf write passes the pre-commit gate and parks BEFORE the
        // retrack installs the suffix listener, then commits AFTER the tracker's read. Only the primitive's
        // post-commit listener resolution can close this window; a lost write would fail the settled-state
        // assertion below.
        var (root, targetLeaf, blocking) = BuildRetrackRaceGraph();
        var deliveredLock = new object();
        var delivered = new List<SubjectPathChange<string?>>();
        using var subscription = root.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> change) =>
        {
            lock (deliveredLock)
            {
                delivered.Add(change);
            }
        }, SubjectPathValidation.Full);

        blocking.ProceedWithCommit.Reset();
        blocking.EnteredInnerChain.Reset();

        // Act: the racing write enters the chain (past the pre-commit gate) and parks pre-commit, before the
        // install.
        var writer = Task.Run(() => targetLeaf.FirstName = "New");
        Assert.True(blocking.EnteredInnerChain.Wait(TimeSpan.FromSeconds(10)), "the racing write never entered the inner chain");

        // The structural retrack runs on the unblocked root context while the writer is parked: it installs
        // the leaf listener and reads the pre-race value ("Old"); the racing "New" has not committed yet.
        root.Father = targetLeaf;

        // Release the parked writer: it commits "New" after the retrack's read, and post-commit listener
        // resolution finds the just-installed listener, so the racing write is delivered, not lost.
        blocking.ProceedWithCommit.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
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

    [Fact]
    public async Task WhenLeafWriteRacesInitialSubscribeBuild_ThenLateDispatchDeliversItToTheFreshBuild()
    {
        // Arrange: a single context carrying the blocking interceptor. Arrange-time writes proceed (gate
        // open); the gate is then armed so the racing write can be parked while SubscribeToPath builds the
        // chain. This pins the subscribe-before-read guarantee for the FIRST build, not only retracks.
        var blocking = new BlockingWriteInterceptor();
        blocking.ProceedWithCommit.Set();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithService(() => blocking);
        var father = new Person(context) { FirstName = "Joe" };
        var person = new Person(context) { Father = father };

        blocking.ProceedWithCommit.Reset();
        blocking.EnteredInnerChain.Reset();

        var deliveredLock = new object();
        var delivered = new List<SubjectPathChange<string?>>();

        // Act: the racing leaf write parks pre-commit. At gate-read time no subscription exists, so it takes
        // the idle-entry path, whose late dispatch must still reach a listener installed while it is in flight.
        var writer = Task.Run(() => father.FirstName = "Jon");
        Assert.True(blocking.EnteredInnerChain.Wait(TimeSpan.FromSeconds(10)), "the racing write never entered the inner chain");

        // The initial subscribe-time build installs the leaf listener and seeds from a read that sees "Joe"
        // (the racing write has not committed). SubscribeToPath performs no write, so it is not blocked.
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> change) =>
        {
            lock (deliveredLock)
            {
                delivered.Add(change);
            }
        }, SubjectPathValidation.Full);

        // Release: the write commits "Jon" and its post-commit late dispatch resolves the freshly installed
        // listener, so the write racing the initial build is delivered, not lost.
        blocking.ProceedWithCommit.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        SubjectPathChange<string?>[] observed;
        lock (deliveredLock)
        {
            observed = delivered.ToArray();
        }

        Assert.Equal("Jon", father.FirstName);
        Assert.Equal("Jon", subscription.Current.GetValueOrDefault());
        Assert.NotEmpty(observed);
        Assert.Equal("Jon", observed[^1].NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenCyclicPathStructuralWriteFansOutToTwoListeners_ThenDeliveredExactlyOnceAndChainInvariantHolds()
    {
        // Arrange: a self-referencing node (node.Child == node) watched through x => x.Child.Child.Name. The
        // decomposed chain reads Child on node (position 0), Child on node again (position 1) and Name on
        // node (position 2), so the doubly-appearing subject's Child property carries TWO install-order
        // listeners and its Name one: the process-wide count equals the chain length (three).
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var node = new Node(context) { Name = "N" };
        node.Child = node;

        var other = new Node(context) { Name = "Y" };
        other.Child = other; // the retrack target is itself cyclic, so the rebuilt chain is also length three

        var delivered = new List<SubjectPathChange<string>>();
        using var subscription = node.SubscribeToPath(x => x.Child!.Child!.Name, (in SubjectPathChange<string> change) => delivered.Add(change), SubjectPathValidation.Full);

        Assert.Equal(3, PropertyChangeSubscriptions.ReadSubscriptionCount());
        Assert.Equal(2, ListenerCount(node, nameof(Node.Child))); // the doubly-appearing subject holds two listeners
        Assert.Equal(1, ListenerCount(node, nameof(Node.Name)));

        // Act: one structural write to the doubly-listened property. Dispatch fans out over the install-order
        // snapshot [position 0, position 1]; position 0 fires first and its retrack (divergence at position 1)
        // disposes position 1's listener, whose in-snapshot dispatch is then skipped by the primitive's
        // cleared-observer guard.
        node.Child = other;

        // Assert: exactly one delivery for the single write, and the rebuilt chain preserves the invariant
        // (process-wide count equals the new chain length; no listener leaked).
        var change = Assert.Single(delivered);
        Assert.Equal("N", change.OldState.GetValueOrDefault());
        Assert.Equal("Y", change.NewState.GetValueOrDefault());
        Assert.Equal(3, PropertyChangeSubscriptions.ReadSubscriptionCount());
        Assert.Equal("Y", subscription.Current.GetValueOrDefault());
    }

    [Fact]
    public async Task WhenCallbacksCrossWriteEachOthersPathConcurrently_ThenNoDeadlock()
    {
        // Arrange: two independent path subscriptions. A's callback writes the leaf of path B and B's
        // callback writes the leaf of path A. If callbacks ran under the per-subscription lock, the two
        // cross-writes would form an AB-BA cycle and deadlock; they must run OUTSIDE the lock.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var fatherA = new Person { FirstName = "A0" };
        var personA = new Person(context) { Father = fatherA };
        var fatherB = new Person { FirstName = "B0" };
        var personB = new Person(context) { Father = fatherB };

        var timeout = TimeSpan.FromSeconds(10);
        using var enteredA = new ManualResetEventSlim(false);
        using var enteredB = new ManualResetEventSlim(false);
        var aBarrierDone = 0;
        var bBarrierDone = 0;
        var aCrossWrote = 0;
        var bCrossWrote = 0;
        Exception? barrierFailure = null;

        using var subscriptionA = personA.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) =>
        {
            if (Interlocked.Exchange(ref aBarrierDone, 1) == 0)
            {
                enteredA.Set();
                if (!enteredB.Wait(timeout))
                {
                    Volatile.Write(ref barrierFailure, new TimeoutException("path B callback never entered"));
                    return;
                }
            }

            if (Interlocked.Exchange(ref aCrossWrote, 1) == 0)
            {
                fatherB.FirstName = "fromA"; // cross-write into path B while (correctly) holding no lock
            }
        }, SubjectPathValidation.Full);

        using var subscriptionB = personB.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) =>
        {
            if (Interlocked.Exchange(ref bBarrierDone, 1) == 0)
            {
                enteredB.Set();
                if (!enteredA.Wait(timeout))
                {
                    Volatile.Write(ref barrierFailure, new TimeoutException("path A callback never entered"));
                    return;
                }
            }

            if (Interlocked.Exchange(ref bCrossWrote, 1) == 0)
            {
                fatherA.FirstName = "fromB"; // cross-write into path A while (correctly) holding no lock
            }
        }, SubjectPathValidation.Full);

        // Act: two threads trigger the two callbacks concurrently. The barrier makes both callbacks
        // in-flight simultaneously, so a lock held across a callback would surface as an AB-BA deadlock.
        // The bounded join turns a hang into a fast test failure instead of hanging the suite.
        var writerA = Task.Run(() => fatherA.FirstName = "goA");
        var writerB = Task.Run(() => fatherB.FirstName = "goB");
        await Task.WhenAll(writerA, writerB).WaitAsync(timeout);

        // Assert: no deadlock (reached here within the timeout), neither barrier wait timed out, and both
        // cross-writes actually ran (so the test is not vacuously satisfied by callbacks never firing).
        Assert.Null(Volatile.Read(ref barrierFailure));
        Assert.Equal(1, Volatile.Read(ref aCrossWrote));
        Assert.Equal(1, Volatile.Read(ref bCrossWrote));
    }

    [Fact]
    public async Task WhenManyConcurrentLeafAndStructuralWrites_ThenSubscriptionConvergesToTheGraphState()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var fathers = new[]
        {
            new Person { FirstName = "f0" },
            new Person { FirstName = "f1" },
            new Person { FirstName = "f2" },
        };
        var person = new Person(context) { Father = fathers[0] };

        var lastDeliveredLock = new object();
        SubjectPathChange<string?>? lastDelivered = null;
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> change) =>
        {
            lock (lastDeliveredLock)
            {
                lastDelivered = change;
            }
        }, SubjectPathValidation.Full);

        using var start = new ManualResetEventSlim(false);
        const int iterations = 2000;

        // Act: a structural writer swaps person.Father among a small set while leaf writers mutate those
        // fathers' names, all concurrently. The interleaving is nondeterministic; the assertion is on the
        // settled state only (converge-then-assert).
        var structural = Task.Run(() =>
        {
            start.Wait(TimeSpan.FromSeconds(10));
            for (var i = 0; i < iterations; i++)
            {
                person.Father = fathers[i % fathers.Length];
            }
        });

        var leafWriters = new List<Task>();
        foreach (var father in fathers)
        {
            var target = father;
            leafWriters.Add(Task.Run(() =>
            {
                start.Wait(TimeSpan.FromSeconds(10));
                for (var i = 0; i < iterations; i++)
                {
                    target.FirstName = "v" + (i % 8);
                }
            }));
        }

        start.Set();
        await Task.WhenAll(leafWriters.Append(structural)).WaitAsync(TimeSpan.FromSeconds(30));

        // Settle to a unique final state on a single thread; these writes deliver synchronously.
        var settleFather = new Person { FirstName = "init" };
        person.Father = settleFather;
        settleFather.FirstName = "End";

        // Assert: quiescent consistency. The real graph, the subscription's Current and the last delivered
        // New all agree on the settled value, and the chain has not leaked listeners.
        SubjectPathChange<string?>? last;
        lock (lastDeliveredLock)
        {
            last = lastDelivered;
        }

        Assert.Equal("End", person.Father!.FirstName);
        Assert.Equal("End", subscription.Current.GetValueOrDefault());
        Assert.NotNull(last);
        Assert.Equal("End", last!.Value.NewState.GetValueOrDefault());
        Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount()); // Father + current leaf, no leak
    }

    /// <summary>
    /// Builds a two-context graph for the retrack missed-write race. The root and its initial leaf live in a
    /// plain full-tracking context; the retrack target leaf lives in its own context carrying the
    /// <see cref="BlockingWriteInterceptor"/>, so a write to THAT leaf can be parked pre-commit while the
    /// (unblocked) structural retrack on the root runs. A keeper reference (root.Mother) attaches the target
    /// leaf before the race so its context is stable: the retrack's root.Father assignment only bumps the
    /// target's reference count and adds no fallback context under the parked write.
    /// </summary>
    private static (Person Root, Person TargetLeaf, BlockingWriteInterceptor Blocking) BuildRetrackRaceGraph()
    {
        var blocking = new BlockingWriteInterceptor();
        blocking.ProceedWithCommit.Set(); // let arrange-time writes through; reset before the race

        var rootContext = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var targetContext = InterceptorSubjectContext.Create().WithService(() => blocking);

        var root = new Person(rootContext);
        var initialLeaf = new Person { FirstName = "Joe" };
        root.Father = initialLeaf; // inherits rootContext

        var targetLeaf = new Person(targetContext) { FirstName = "Old" };
        root.Mother = targetLeaf; // keeper: attaches targetLeaf, adds rootContext as a stable fallback

        return (root, targetLeaf, blocking);
    }

    private static int ListenerCount(IInterceptorSubject subject, string propertyName)
        => new PropertyReference(subject, propertyName).TryGetPropertyData(PropertyChangeSubscription.ListenersKey, out var value)
            && value is PropertyChangeSubscription[] listeners
            ? listeners.Length
            : 0;

    /// <summary>
    /// A read interceptor that, exactly once and only after <see cref="Arm"/>, writes a watched leaf while it
    /// is being read. Registered so its write lands DURING the tracker's own event walk, re-entering the path
    /// subscription on the walking (lock-holding) thread. Reads before arming (the seed walk) and reads after
    /// firing (later re-walks, derived recalculations) do nothing, so the side effect fires exactly once.
    /// </summary>
    private sealed class SideEffectReadInterceptor : IReadInterceptor
    {
        private readonly IInterceptorSubject _target;
        private readonly string _triggerPropertyName;
        private readonly string? _sideEffectValue;
        private int _armed;
        private int _fired;

        public SideEffectReadInterceptor(Person target, string triggerPropertyName, string? sideEffectValue)
        {
            _target = target;
            _triggerPropertyName = triggerPropertyName;
            _sideEffectValue = sideEffectValue;
        }

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            var result = next(ref context);

            if (Volatile.Read(ref _armed) == 1
                && ReferenceEquals(context.Property.Subject, _target)
                && context.Property.Name == _triggerPropertyName
                && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
            {
                // Write the watched leaf mid-read so the write dispatches synchronously and re-enters the
                // path subscription on this same lock-holding thread. Setting the value AFTER next() returned
                // means the outer walk already captured the pre-side-effect value, so the deferred revalidation
                // has a genuinely distinct value to deliver.
                ((Person)_target).FirstName = _sideEffectValue;
            }

            return result;
        }
    }

    /// <summary>
    /// A read interceptor that, exactly once and only after <see cref="Arm"/>, replaces a WATCHED INTERMEDIATE
    /// (an upper segment) while the leaf is being read, so the structural write re-enters the path subscription
    /// on the walking (lock-holding) thread. Used to prove a deferred revalidation runs the full walk even
    /// under leaf-only validation, where the first pass would otherwise take the leaf-only fast path.
    /// </summary>
    private sealed class UpperSegmentSideEffectReadInterceptor : IReadInterceptor
    {
        private readonly IInterceptorSubject _leafSubject;
        private readonly string _leafPropertyName;
        private readonly Person _replacementFather;
        private Person? _parent;
        private int _armed;
        private int _fired;

        public UpperSegmentSideEffectReadInterceptor(IInterceptorSubject leafSubject, string leafPropertyName, Person replacementFather)
        {
            _leafSubject = leafSubject;
            _leafPropertyName = leafPropertyName;
            _replacementFather = replacementFather;
        }

        public void Bind(Person parent) => _parent = parent;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            var result = next(ref context);

            if (Volatile.Read(ref _armed) == 1
                && ReferenceEquals(context.Property.Subject, _leafSubject)
                && context.Property.Name == _leafPropertyName
                && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
            {
                // Replace the watched intermediate mid-read: a structural (upper-segment) change that
                // dispatches synchronously and re-enters on this lock-holding thread, deferring a revalidation.
                _parent!.Father = _replacementFather;
            }

            return result;
        }
    }
}
