using System.Collections.Concurrent;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceMonitorTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithLifecycle()
            .WithSourceMonitoring();

    [Fact]
    public async Task WhenASourceRegisters_ThenSubscribersReceiveSourceRegistered()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));
        var source = new TestStateSource(new Person(context));

        // Act
        monitor.Register(source);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.SourceRegistered));
        Assert.Contains(source, monitor.Sources);
    }

    [Fact]
    public async Task WhenARegisteredSourceTransitions_ThenTheMonitorForwardsStateChanged()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var source = new TestStateSource(new Person(context));
        monitor.Register(source);
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        source.ReportSynchronized();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() =>
            received.Any(e => e.Kind == SourceEventKind.StateChanged && e.NewState == SourceState.Synchronized));
    }

    [Fact]
    public async Task WhenASourceUnregisters_ThenItsLaterTransitionsAreNotForwarded()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var source = new TestStateSource(new Person(context));
        monitor.Register(source);
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        monitor.Unregister(source);
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.SourceUnregistered));
        source.ReportSynchronized();

        // Assert
        // Delivery is asynchronous, so an empty-of-StateChanged queue proves nothing on its own right
        // here: a wrongly forwarded event may simply not have been drained yet. Register a sentinel
        // afterwards and wait for ITS delivered event; once that has arrived, anything the
        // (supposedly disconnected) source's ReportSynchronized wrongly published would already have
        // been delivered too, since delivery per subscription is FIFO.
        await SettleDeliveryAsync(monitor, context, received);
        Assert.DoesNotContain(received, e => e.Kind == SourceEventKind.StateChanged);
        Assert.DoesNotContain(source, monitor.Sources);
    }

    [Fact]
    public async Task WhenRegisteringTwice_ThenTheSecondRegistrationEmitsNothing()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var source = new TestStateSource(new Person(context));
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        monitor.Register(source);
        monitor.Register(source);

        // Assert
        Assert.Single(monitor.Sources);
        // A sentinel settles delivery without a blind sleep: once its SourceRegistered has arrived,
        // any event the second, idempotent Register call wrongly published would already have been
        // delivered too, so exactly one SourceRegistered for `source` proves the second call emitted
        // nothing, not just that Sources stayed a single element.
        await SettleDeliveryAsync(monitor, context, received);
        Assert.Single(received, e => e.Kind == SourceEventKind.SourceRegistered && ReferenceEquals(e.Source, source));
    }

    [Fact]
    public async Task WhenUnregisteringAnUnknownSource_ThenNothingHappens()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var source = new TestStateSource(new Person(context));
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        monitor.Unregister(source);

        // Assert
        // Sources staying empty is true whether or not the "is it actually registered" guard ran:
        // removing an absent item from an ImmutableArray is already a no-op. Without the guard,
        // Unregister would still publish a spurious SourceUnregistered for a source nobody ever
        // registered - that is the part only a subscriber can catch.
        Assert.Empty(monitor.Sources);
        await SettleDeliveryAsync(monitor, context, received);
        Assert.DoesNotContain(received, e => e.Kind == SourceEventKind.SourceUnregistered);
    }

    [Fact]
    public async Task WhenOneHandlerThrows_ThenOtherSubscribersStillReceiveEvents()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();
        using var throwing = monitor.Subscribe(_ => throw new InvalidOperationException("subscriber is buggy"));
        using var healthy = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        monitor.Register(new TestStateSource(new Person(context)));

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.SourceRegistered));
    }

    [Fact]
    public async Task WhenOneHandlerIsSlow_ThenAnotherSubscriberIsNotDelayed()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var release = new ManualResetEventSlim(false);
        var fastReceived = new ManualResetEventSlim(false);
        using var slow = monitor.Subscribe(_ => release.Wait(TimeSpan.FromSeconds(30)));
        using var fast = monitor.Subscribe(_ => fastReceived.Set());

        // Act
        monitor.Register(new TestStateSource(new Person(context)));

        // Assert
        Assert.True(fastReceived.Wait(TimeSpan.FromSeconds(10)));
        release.Set();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WhenSubscribing_ThenTheSnapshotPlusDeliveredEventsSeeEachChangeOnce()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var before = new TestStateSource(new Person(context));
        monitor.Register(before);
        var received = new ConcurrentQueue<SourceEvent>();

        // Act
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));
        var after = new TestStateSource(new Person(context));
        monitor.Register(after);

        // Assert
        Assert.Contains(before, subscription.Sources);
        Assert.DoesNotContain(after, subscription.Sources);
        await AsyncTestHelpers.WaitUntilAsync(() =>
            received.Any(e => e.Kind == SourceEventKind.SourceRegistered && ReferenceEquals(e.Source, after)));
        Assert.DoesNotContain(received, e => ReferenceEquals(e.Source, before));
    }

    [Fact]
    public async Task WhenRegisterAndSubscribeRaceConcurrently_ThenTheSourceIsObservedExactlyOnce()
    {
        // Arrange
        const int iterations = 10;
        var outcomes = new List<(bool WasInSnapshot, bool WasDelivered)>();

        // Act
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            outcomes.Add(await RaceRegisterAgainstSubscribeOnceAsync());
        }

        // Assert
        Assert.All(outcomes, outcome => Assert.True(
            outcome.WasInSnapshot ^ outcome.WasDelivered,
            $"snapshot={outcome.WasInSnapshot}, delivered={outcome.WasDelivered}; the source must appear " +
            "exactly once, in the snapshot or as a delivered event, never both, never neither."));
    }

    /// <summary>
    /// Manufactures, on one fresh monitor, the exact race described in the review finding: Register is
    /// paused right where it reads the source's State to build the SourceRegistered event (pre-fix that
    /// read happens after the monitor's lock is released; post-fix it happens while the lock is still
    /// held). Subscribe is started concurrently and given every chance to complete before Register is
    /// allowed to resume, which is what lets a pre-fix run see the source both in its snapshot and as a
    /// delivered event.
    /// </summary>
    private static Task<(bool WasInSnapshot, bool WasDelivered)> RaceRegisterAgainstSubscribeOnceAsync() =>
        RaceActionAgainstSubscribeOnceAsync(
            (monitor, source) => monitor.Register(source),
            SourceEventKind.SourceRegistered);

    [Fact]
    public async Task WhenUnregisterAndSubscribeRaceConcurrently_ThenSnapshotPresenceAgreesWithDelivery()
    {
        // Arrange
        const int iterations = 10;
        var outcomes = new List<(bool WasInSnapshot, bool WasDelivered)>();

        // Act
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            outcomes.Add(await RaceUnregisterAgainstSubscribeOnceAsync());
        }

        // Assert
        // Unlike Register, Unregister is not a change from the racing subscriber's point of view unless
        // the subscriber already knew about the source. So the two facts must AGREE, not be exclusive:
        // seen in the baseline snapshot implies exactly one delivered SourceUnregistered, and absent from
        // the snapshot implies no delivered event, because a subscriber that never learned the source
        // existed must not be told it left.
        Assert.All(outcomes, outcome => Assert.True(
            outcome.WasInSnapshot == outcome.WasDelivered,
            $"snapshot={outcome.WasInSnapshot}, delivered={outcome.WasDelivered}; a source that was already " +
            "registered before the race is prior state, not a change, so a racing subscriber must either see " +
            "it in the snapshot AND receive exactly one SourceUnregistered for it, or see neither."));
    }

    /// <summary>
    /// Manufactures, on one fresh monitor, the race between a source that was already registered before the
    /// race starts and a Subscribe call that competes with the Unregister of that source. Mirrors the
    /// Register race above, but registration itself happens first and untimed, because Unregister racing a
    /// new subscriber is only interesting once the source is prior state rather than a fresh change.
    /// Unregister is paused right where it reads the source's State to build the SourceUnregistered event
    /// (pre-fix that read happens after the monitor's lock is released; post-fix it happens while the lock
    /// is still held). Subscribe is started concurrently and given every chance to complete before Unregister
    /// is allowed to resume. Against the pre-fix code the lock is already released by the time Subscribe
    /// runs, so Subscribe races ahead: it captures a snapshot from which the source has already been removed
    /// (WasInSnapshot false) but is added to the subscriber list in time to still receive the delivered
    /// SourceUnregistered event once Unregister's paused Publish resumes (WasDelivered true), breaking the
    /// agreement. Against the fixed code, Unregister still holds the lock while Subscribe is blocked, so
    /// Subscribe cannot observe or register until after Publish has already run without it, keeping both
    /// facts false and in agreement.
    /// </summary>
    private static Task<(bool WasInSnapshot, bool WasDelivered)> RaceUnregisterAgainstSubscribeOnceAsync() =>
        RaceActionAgainstSubscribeOnceAsync(
            (monitor, source) => monitor.Unregister(source),
            SourceEventKind.SourceUnregistered,
            // Register a source with a gated State property, but keep the gate open so this initial
            // registration, which is not part of the race, completes immediately without blocking.
            preArrange: (monitor, source) => monitor.Register(source));

    /// <summary>
    /// Shared scaffolding for the Register-vs-Subscribe and Unregister-vs-Subscribe races above: builds a
    /// fresh monitor and a source whose State getter can be paused mid-read, pauses <paramref name="act"/>
    /// right there, gives a concurrent Subscribe every chance to race ahead before <paramref name="act"/>
    /// is allowed to resume, then drains with a sentinel so delivery can be settled without a blind sleep.
    /// </summary>
    /// <param name="act">The monitor operation to race against Subscribe (Register or Unregister).</param>
    /// <param name="expectedEventKind">The event kind that would prove delivery for <paramref name="act"/>.</param>
    /// <param name="preArrange">
    /// Optional setup that runs before the race's gates are engaged, e.g. pre-registering the source so
    /// Unregister has something to remove.
    /// </param>
    private static async Task<(bool WasInSnapshot, bool WasDelivered)> RaceActionAgainstSubscribeOnceAsync(
        Action<SourceMonitor, GatedStateRaisingSource> act,
        SourceEventKind expectedEventKind,
        Action<SourceMonitor, GatedStateRaisingSource>? preArrange = null)
    {
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();

        // Keep the gate open for preArrange so any State read it triggers completes immediately
        // without blocking; reset both gates afterwards so the race below starts from a clean pause point.
        var reachedStateRead = new ManualResetEventSlim(false);
        var releaseStateRead = new ManualResetEventSlim(true);
        var source = new GatedStateRaisingSource(new Person(context), reachedStateRead, releaseStateRead);

        preArrange?.Invoke(monitor, source);

        reachedStateRead.Reset();
        releaseStateRead.Reset();

        // Both race participants park on locks and gates, so they run on dedicated threads rather than
        // the pool: the full assembly runs its collections in parallel, and a queued work item can then
        // wait seconds before it is ever scheduled. That starvation is what makes the State read time out
        // below, and it also lets the blocked-Subscribe assertion pass for the wrong reason, since a
        // Subscribe that was never scheduled has not completed either.
        var actTask = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => act(monitor, source));
        Assert.True(reachedStateRead.Wait(TimeSpan.FromSeconds(10)),
            "The racing action should have reached the State read before the timeout.");

        var subscribeEntered = new ManualResetEventSlim(false);
        var subscribeTask = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            subscribeEntered.Set();
            return monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));
        });

        Assert.True(subscribeEntered.Wait(TimeSpan.FromSeconds(10)),
            "The racing Subscribe should have started before the timeout.");

        // Subscribe must be BLOCKED here, on the lock the paused action still holds. Asserted
        // directly rather than by catching a timeout as the pass path: under load that catch fires
        // for both the fixed and the broken code, silently turning the whole race harness into a
        // no-op that still reports green.
        var settledFirst = await Task.WhenAny(subscribeTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(subscribeTask, settledFirst);

        releaseStateRead.Set();

        using var subscription = await subscribeTask.WaitAsync(TimeSpan.FromSeconds(10));
        await actTask.WaitAsync(TimeSpan.FromSeconds(10));

        var wasInSnapshot = subscription.Sources.Contains(source);

        // A subscriber's queue is FIFO and single-drained, so once a sentinel registered strictly after
        // the race above has resolved is observed, any event for `source` that was ever enqueued for this
        // subscription has already been delivered. This settles delivery without a blind sleep.
        var sentinel = new TestStateSource(new Person(context));
        monitor.Register(sentinel);
        await AsyncTestHelpers.WaitUntilAsync(
            () => received.Any(e => e.Kind == SourceEventKind.SourceRegistered && ReferenceEquals(e.Source, sentinel)),
            pollInterval: TimeSpan.FromMilliseconds(2));

        var wasDelivered = received.Any(
            e => e.Kind == expectedEventKind && ReferenceEquals(e.Source, source));

        return (wasInSnapshot, wasDelivered);
    }

    [Fact]
    public void WhenNoMonitorIsConfigured_ThenGetSourceMonitorThrowsWithGuidance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => context.GetSourceMonitor());
        Assert.Contains("WithSourceMonitoring", exception.Message);
    }

    [Fact]
    public void WhenTwoMonitorsAreReachable_ThenGetSourceMonitorThrows()
    {
        // Arrange
        var parent = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithLifecycle().WithSourceMonitoring();
        var child = InterceptorSubjectContext.Create().WithSourceMonitoring();
        child.AddFallbackContext(parent);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => child.GetSourceMonitor());
        Assert.Contains("GetServices", exception.Message);
    }

    [Fact]
    public async Task WhenAStoppedSourceIsStartedAgain_ThenThePumpDoesNotRun()
    {
        // Arrange
        var context = CreateContext();
        var source = new TestStateSource(new Person(context));
        await source.StartAsync(CancellationToken.None);
        await source.StopAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.State == SourceState.Stopped);

        // Act
        await source.StartAsync(CancellationToken.None);

        // Assert
        // BackgroundService.StartAsync builds a FRESH linked CancellationTokenSource on every call
        // and StopAsync cancels only the previous one, so without the guard ExecuteAsync would run
        // a second time against an uncancelled token while State stayed pinned at Stopped.
        Assert.Equal(SourceState.Stopped, source.State);
        Assert.Equal(1, source.ExecuteCount);
    }

    [Fact]
    public async Task WhenDisposeRacesStartAsync_ThenTheSourceDoesNotStayRegisteredForever()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var reachedRootRead = new ManualResetEventSlim(false);
        var releaseRootRead = new ManualResetEventSlim(false);
        var source = new GatedRootSubjectSource(new Person(context), reachedRootRead, releaseRootRead);

        // Act - pause StartAsync exactly where it reads RootSubject to resolve which monitors to
        // register with, so Dispose can run to completion (transitioning to Stopped and finding
        // nothing yet in _registeredMonitors to unregister) before StartAsync ever registers.
        var startTask = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(
            () => source.StartAsync(CancellationToken.None));
        Assert.True(reachedRootRead.Wait(TimeSpan.FromSeconds(10)));

        source.Dispose();
        Assert.Equal(SourceState.Stopped, source.State);

        releaseRootRead.Set();
        await startTask;

        // Assert - without StartAsync's post-registration re-check, this already-disposed, Stopped
        // source would register successfully (Register does not check State) and stay registered
        // forever, since Dispose already ran and will not run again.
        Assert.DoesNotContain(source, monitor.Sources);
    }

    [Fact]
    public async Task WhenDisposeLandsInsideStartAsyncsRegistrationLoop_ThenNoStoppedSourceStaysRegistered()
    {
        // Arrange
        // A second interleaving, narrower than the RootSubject-gated one above and with no seam to
        // pin it on: Dispose has to land between StartAsync's assignment of _registeredMonitors and
        // its registration loop. Dispose then unregisters nothing (the source is not in _sources
        // yet) and blanks the field, so a post-registration re-check that re-READS the field finds
        // an empty array and strands the registration it just made. Driven as a bounded race rather
        // than a gate, since nothing between those two statements can be paused from a test.
        const int iterations = 4000;
        var leaked = 0;

        // Act
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var context = CreateContext();
            var monitor = context.GetSourceMonitor();
            var source = new TestStateSource(new Person(context));

            using var bothReady = new Barrier(2);
            // A Barrier both sides must reach, so a participant the pool never schedules hangs the
            // test outright rather than failing it.
            var startTask = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
            {
                bothReady.SignalAndWait();
                return source.StartAsync(CancellationToken.None);
            });
            var disposeTask = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
            {
                bothReady.SignalAndWait();
                source.Dispose();
            });

            await Task.WhenAll(startTask, disposeTask);
            source.Dispose();

            if (monitor.Sources.Contains(source))
            {
                leaked++;
            }
        }

        // Assert - a leaked source stays in Sources with a live StateChanged subscription, holding
        // the source and its root subject for the lifetime of the monitor. Dispose has already run
        // and will not run again, so nothing ever removes it.
        Assert.Equal(0, leaked);
    }

    [Fact]
    public async Task WhenASourceTransitionsWhileItIsRegistering_ThenSourceRegisteredIsStillDeliveredFirst()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        using var reachedStateRead = new ManualResetEventSlim(false);
        using var releaseStateRead = new ManualResetEventSlim(false);
        var source = new GatedStateRaisingSource(new Person(context), reachedStateRead, releaseStateRead);

        // Act - Register attaches the StateChanged forwarder BEFORE it publishes SourceRegistered,
        // and is pinned here on the State read it performs to build that event, still holding the
        // monitor lock.
        var register = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => monitor.Register(source));
        Assert.True(reachedStateRead.Wait(TimeSpan.FromSeconds(10)));

        using var transitionStarted = new ManualResetEventSlim(false);
        var transition = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            transitionStarted.Set();
            source.RaiseStateChanged(SourceState.Synchronizing, SourceState.Synchronized);
        });
        Assert.True(transitionStarted.Wait(TimeSpan.FromSeconds(10)));

        // The forwarder publishes under the monitor lock, so this transition cannot finish while
        // Register holds it. A bounded negative wait is the assertion here precisely because the
        // property under test is that something does NOT happen: with a lock-free publish the
        // transition returns at once and its event overtakes SourceRegistered in the queue.
        var settledFirst = await Task.WhenAny(transition, Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(transition, settledFirst);

        releaseStateRead.Set();
        await register;
        await transition;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Count >= 2);
        Assert.Equal(SourceEventKind.SourceRegistered, received.First().Kind);
        Assert.Equal(SourceEventKind.StateChanged, received.Skip(1).First().Kind);
    }

    [Fact]
    public async Task WhenATransitionWasAlreadyInFlightWhenTheSourceUnregistered_ThenItIsNotPublished()
    {
        // Arrange
        // TransitionTo captures the handler list before invoking it, so a transition that started
        // before Unregister ran its -= still reaches the monitor afterwards. Publishing it would
        // hand a consumer a state for a source it has already seen unregistered.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();
        using var reached = new ManualResetEventSlim(true);
        using var release = new ManualResetEventSlim(true);
        var source = new GatedStateRaisingSource(new Person(context), reached, release);
        monitor.Register(source);
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // The in-flight transition: handlers captured while the source is still registered.
        var inFlight = source.CaptureStateChangedHandlers();

        // Act
        monitor.Unregister(source);
        inFlight?.Invoke(source, new SourceEvent(
            SourceEventKind.StateChanged, source, null,
            SourceState.Synchronizing, SourceState.Synchronized, DateTimeOffset.UtcNow));

        // Assert
        // A sentinel settles delivery: once its SourceRegistered arrives, anything the in-flight
        // transition wrongly published would already have been delivered on this FIFO queue.
        await SettleDeliveryAsync(monitor, context, received);
        Assert.DoesNotContain(received, e => e.Kind == SourceEventKind.StateChanged);
    }

    [Fact]
    public void WhenASubjectIsAttachedToASecondTree_ThenOnlyTheFirstTreesMonitorIsReachable()
    {
        // Arrange
        var firstTree = CreateContext();
        var secondTree = CreateContext();
        var firstRoot = new Person(firstTree);
        var secondRoot = new Person(secondTree);
        var shared = new Person();
        firstRoot.Mother = shared;

        // Act
        secondRoot.Mother = shared;

        // Assert
        // Characterization, not aspiration: ContextInheritanceHandler adds a parent fallback only on
        // the FIRST attach ({ ReferenceCount: 1, IsContextAttach: true }), so the second tree's
        // monitor never becomes reachable and this design claims no multi-tree coverage. If context
        // inheritance ever starts tracking every parent, this test fails and the limitation, the
        // topology-aware CurrentState, and the docs all need revisiting together.
        var reachable = ((IInterceptorSubject)shared).Context.GetServices<SourceMonitor>();
        Assert.Single(reachable);
        Assert.Same(firstTree.GetSourceMonitor(), reachable[0]);
    }

    [Fact]
    public async Task WhenNoSubjectEverAttaches_ThenTheMonitorStillWorks()
    {
        // Arrange
        // Note: a monitor-bearing context ALWAYS has the lifecycle interceptor, because
        // WithSourceMonitoring implies WithParents and WithParents returns WithLifecycle. So the
        // reachable robustness case is a tree where nothing attaches, not one with no interceptor.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithSourceMonitoring();
        var monitor = context.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        monitor.Register(new TestStateSource(new Person(context)));

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.SourceRegistered));
    }

    /// <summary>
    /// Settles asynchronous delivery so an absence can be asserted: registers a sentinel source and
    /// waits for its event. Delivery per subscription is FIFO, so once that arrives, anything
    /// wrongly published earlier would already have arrived too.
    /// </summary>
    private static async Task SettleDeliveryAsync(
        SourceMonitor monitor, IInterceptorSubjectContext context, ConcurrentQueue<SourceEvent> received)
    {
        var sentinel = new TestStateSource(new Person(context));
        monitor.Register(sentinel);
        await AsyncTestHelpers.WaitUntilAsync(
            () => received.Any(e => e.Kind == SourceEventKind.SourceRegistered && ReferenceEquals(e.Source, sentinel)));
    }

}

/// <summary>
/// A source whose State getter blocks on first access until released, with a raisable StateChanged
/// event. Pins Register at the exact point where it reads State to build the SourceRegistered
/// event, so a test controls what happens while the monitor lock is held.
/// </summary>
internal sealed class GatedStateRaisingSource : ISubjectSource
{
    private readonly ManualResetEventSlim _reachedStateRead;
    private readonly ManualResetEventSlim _releaseStateRead;

    public GatedStateRaisingSource(
        IInterceptorSubject rootSubject, ManualResetEventSlim reachedStateRead, ManualResetEventSlim releaseStateRead)
    {
        RootSubject = rootSubject;
        _reachedStateRead = reachedStateRead;
        _releaseStateRead = releaseStateRead;
    }

    public IInterceptorSubject RootSubject { get; }

    public int WriteBatchSize => 0;

    public SourceState State
    {
        get
        {
            _reachedStateRead.Set();
            _releaseStateRead.Wait(TimeSpan.FromSeconds(10));
            return SourceState.Synchronizing;
        }
    }

    public DateTimeOffset StateChangeTime { get; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastSynchronizedAt => null;

    public SourceDiagnostics Diagnostics { get; } = new(new SourceMetrics());

    ConnectorDiagnostics ISubjectConnector.Diagnostics => Diagnostics;

    public event EventHandler<SourceEvent>? StateChanged;

    /// <summary>
    /// Captures the handler list the way SubjectSourceBase.TransitionTo does before it invokes it,
    /// so a test can unregister in between and then deliver the already-in-flight transition.
    /// </summary>
    public EventHandler<SourceEvent>? CaptureStateChangedHandlers() => StateChanged;

    /// <summary>Raises StateChanged the way a source's own transition would.</summary>
    public void RaiseStateChanged(SourceState oldState, SourceState newState) =>
        StateChanged?.Invoke(this, new SourceEvent(
            SourceEventKind.StateChanged, this, null, oldState, newState, DateTimeOffset.UtcNow));

    public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken) => Task.FromResult<Action?>(null);

    public ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        => new(WriteResult.Success);
}

/// <summary>
/// A source whose RootSubject getter blocks until released. Used to pin StartAsync at the exact
/// point where it reads RootSubject to resolve which monitors to register with, so a test can make
/// Dispose race the registration loop.
/// </summary>
internal sealed class GatedRootSubjectSource : TestStateSource
{
    private readonly ManualResetEventSlim _reachedRootRead;
    private readonly ManualResetEventSlim _releaseRootRead;
    private readonly IInterceptorSubject _rootSubject;

    public GatedRootSubjectSource(
        IInterceptorSubject rootSubject, ManualResetEventSlim reachedRootRead, ManualResetEventSlim releaseRootRead)
        : base(rootSubject)
    {
        _rootSubject = rootSubject;
        _reachedRootRead = reachedRootRead;
        _releaseRootRead = releaseRootRead;
    }

    public override IInterceptorSubject RootSubject
    {
        get
        {
            _reachedRootRead.Set();
            _releaseRootRead.Wait(TimeSpan.FromSeconds(10));
            return _rootSubject;
        }
    }
}
