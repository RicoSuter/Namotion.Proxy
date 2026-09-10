using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceWaitTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithSourceMonitoring();

    [Fact]
    public void WhenTheMonitorIsCreated_ThenRegistrationIsIncomplete()
    {
        // Arrange & Act
        var monitor = CreateContext().GetSourceMonitor();

        // Assert
        Assert.False(monitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenRegistrationIsCompleted_ThenTheFlagFlipsAndIsIdempotent()
    {
        // Arrange
        var monitor = CreateContext().GetSourceMonitor();

        // Act
        monitor.CompleteSourceRegistration();
        monitor.CompleteSourceRegistration();

        // Assert
        Assert.True(monitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenACompletionHoldIsTaken_ThenRegistrationIsIncompleteUntilItIsReleased()
    {
        // Arrange
        var monitor = CreateContext().GetSourceMonitor();
        monitor.CompleteSourceRegistration();

        // Act
        var hold = monitor.DeferWaitCompletion();

        // Assert
        Assert.False(monitor.IsRegistrationComplete);
        hold.Dispose();
        Assert.True(monitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenHoldsAreNested_ThenRegistrationCompletesOnlyAfterTheLastRelease()
    {
        // Arrange
        var monitor = CreateContext().GetSourceMonitor();
        monitor.CompleteSourceRegistration();

        // Act
        var outer = monitor.DeferWaitCompletion();
        var inner = monitor.DeferWaitCompletion();
        inner.Dispose();

        // Assert
        Assert.False(monitor.IsRegistrationComplete);
        outer.Dispose();
        Assert.True(monitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenCompleteSourceRegistrationIsCalledConcurrentlyFromManyThreads_ThenTheInitialHoldIsReleasedExactlyOnce()
    {
        // Arrange
        // The idempotency guard (RegistrationHold.Dispose's own Interlocked.Exchange on _disposed,
        // via the initial hold that CompleteSourceRegistration disposes) is what must survive many
        // threads racing into CompleteSourceRegistration at once: if it let more than one thread
        // through, _registrationHolds would be decremented past zero and IsRegistrationComplete
        // would get stuck false, since nothing else would ever bring the count back up.
        var monitor = CreateContext().GetSourceMonitor();
        const int threadCount = 64;
        using var barrier = new Barrier(threadCount);

        // Raw threads, not the thread pool: Parallel.For/Task.Run schedule onto the pool, whose
        // throttled thread-injection heuristic can take many seconds to grow to threadCount
        // concurrent workers when they all immediately block on the barrier, making the test slow
        // without exercising any more concurrency than a handful of real threads would.
        var threads = new Thread[threadCount];
        for (var i = 0; i < threadCount; i++)
        {
            threads[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                monitor.CompleteSourceRegistration();
            });
        }

        // Act
        foreach (var thread in threads)
        {
            thread.Start();
        }
        foreach (var thread in threads)
        {
            thread.Join();
        }

        // Assert
        Assert.True(monitor.IsRegistrationComplete);
        Assert.Equal(0, GetRegistrationHolds(monitor));
    }

    [Fact]
    public void WhenManyDeferWaitCompletionHoldsAreTakenAndDisposedConcurrently_ThenTheCountReturnsToZero()
    {
        // Arrange
        var monitor = CreateContext().GetSourceMonitor();
        monitor.CompleteSourceRegistration(); // release the initial hold so the baseline is zero
        const int threadCount = 32;
        const int perThreadIterations = 500;
        using var barrier = new Barrier(threadCount);

        // Raw threads: see the comment in the test above for why the thread pool is avoided here.
        var threads = new Thread[threadCount];
        for (var i = 0; i < threadCount; i++)
        {
            threads[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var iteration = 0; iteration < perThreadIterations; iteration++)
                {
                    using var hold = monitor.DeferWaitCompletion();
                }
            });
        }

        // Act
        foreach (var thread in threads)
        {
            thread.Start();
        }
        foreach (var thread in threads)
        {
            thread.Join();
        }

        // Assert
        // The count must land back at exactly zero: never negative (a lost double-release) and
        // never stuck positive (a leaked hold that was never disposed).
        Assert.True(monitor.IsRegistrationComplete);
        Assert.Equal(0, GetRegistrationHolds(monitor));
    }

    private static int GetPendingWaitCount(SourceMonitor monitor)
    {
        var field = typeof(SourceMonitor).GetField("_waits", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var waits = field.GetValue(monitor)!;
        return (int)waits.GetType().GetProperty("Length")!.GetValue(waits)!;
    }

    [Fact]
    public async Task WhenAWaitCompletes_ThenItIsRemovedFromThePendingList()
    {
        // Arrange
        // A wait that is never unregistered stays in _waits for the monitor's lifetime, pinning its
        // anchor subject and being re-evaluated on every property-reference add/remove tree-wide -
        // the documented hot path. Nothing else in the suite observes the list itself.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        monitor.Register(source);
        monitor.CompleteSourceRegistration();

        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);
        Assert.Equal(1, GetPendingWaitCount(monitor));

        // Act
        source.ReportSynchronized();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => GetPendingWaitCount(monitor) == 0);
    }

    [Fact]
    public async Task WhenAWaitIsCancelled_ThenItIsRemovedFromThePendingList()
    {
        // Arrange
        // Companion to the test above for the cancellation path: the existing cancellation test
        // asserts only that the token propagates, so the cleanup it is named for went unpinned.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        monitor.Register(new TestStateSource(root));
        monitor.CompleteSourceRegistration();

        using var cancellation = new CancellationTokenSource();
        var wait = root.WaitForSynchronizationAsync(cancellation.Token);
        Assert.Equal(1, GetPendingWaitCount(monitor));

        // Act
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => GetPendingWaitCount(monitor) == 0);
    }

    private static int GetRegistrationHolds(SourceMonitor monitor)
    {
        var field = typeof(SourceMonitor).GetField("_registrationHolds", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (int)field.GetValue(monitor)!;
    }

    [Fact]
    public void WhenTheContextExtensionIsUsed_ThenEveryReachableMonitorIsSignalled()
    {
        // Arrange
        var context = CreateContext();

        // Act
        context.CompleteSourceRegistration();

        // Assert
        Assert.True(context.GetSourceMonitor().IsRegistrationComplete);
    }

    [Fact]
    public void WhenTwoMonitorsAreReachable_ThenCompleteSourceRegistrationSignalsAll()
    {
        // Arrange
        var lifecycle = new LifecycleInterceptor();
        var parent = InterceptorSubjectContext.Create();
        var child = InterceptorSubjectContext.Create();
        parent.AddService(lifecycle);
        child.AddService(lifecycle);
        parent.WithFullPropertyTracking().WithSourceMonitoring();
        child.WithSourceMonitoring();
        child.AddFallbackContext(parent);

        var parentMonitor = parent.GetSourceMonitor();
        var childMonitor = child.GetServices<SourceMonitor>()[0];

        // Act
        child.CompleteSourceRegistration();

        // Assert
        Assert.True(parentMonitor.IsRegistrationComplete);
        Assert.True(childMonitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenDeferWaitCompletionIsCalledWithTwoMonitors_ThenBothHoldsAreReleased()
    {
        // Arrange
        var lifecycle = new LifecycleInterceptor();
        var parent = InterceptorSubjectContext.Create();
        var child = InterceptorSubjectContext.Create();
        parent.AddService(lifecycle);
        child.AddService(lifecycle);
        parent.WithFullPropertyTracking().WithSourceMonitoring();
        child.WithSourceMonitoring();
        child.AddFallbackContext(parent);

        var parentMonitor = parent.GetSourceMonitor();
        var childMonitor = child.GetServices<SourceMonitor>()[0];

        parentMonitor.CompleteSourceRegistration();
        childMonitor.CompleteSourceRegistration();

        // Act
        var hold = child.DeferWaitCompletion();

        // Assert - both should be incomplete while holds are out
        Assert.False(parentMonitor.IsRegistrationComplete);
        Assert.False(childMonitor.IsRegistrationComplete);

        // Act - release the holds
        hold.Dispose();

        // Assert - both should be complete
        Assert.True(parentMonitor.IsRegistrationComplete);
        Assert.True(childMonitor.IsRegistrationComplete);
    }

    [Fact]
    public async Task WhenRegistrationIsIncomplete_ThenTheWaitDoesNotComplete()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        monitor.Register(source);
        source.ReportSynchronized();

        // Act
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        Assert.False(wait.IsCompleted);
        monitor.CompleteSourceRegistration();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WhenAnInScopeSourceIsSynchronizing_ThenTheWaitBlocksUntilItSynchronizes()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        monitor.Register(source);
        monitor.CompleteSourceRegistration();

        // Act
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);
        source.ReportSynchronized();

        // Assert
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WhenASiblingBranchSourceNeverSynchronizes_ThenAScopedWaitStillCompletes()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var left = new Person();
        var right = new Person();
        root.Mother = left;
        root.Father = right;
        var healthy = new TestStateSource(left);
        var broken = new TestStateSource(right);
        monitor.Register(healthy);
        monitor.Register(broken);
        monitor.CompleteSourceRegistration();
        healthy.ReportSynchronized();

        // Act
        await left.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(SourceState.Synchronizing, broken.State);
    }

    [Fact]
    public async Task WhenNoInScopeSourceIsRegistered_ThenTheWaitCompletesVacuously()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        monitor.CompleteSourceRegistration();

        // Act - once registration is complete, an empty scope is no longer ambiguous between "no
        // source yet" and "no source ever": it definitively means this branch is local-only, so the
        // wait completes immediately instead of blocking forever (consistent with the all-Stopped
        // rule, which already completes vacuously rather than hanging).
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WhenNoInScopeSourceIsRegisteredAndRegistrationIsIncomplete_ThenTheWaitStillBlocks()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        // Registration is deliberately left incomplete here: only the post-signal case changes to
        // vacuous completion. Before the signal, an empty scope is still ambiguous between "no
        // source yet" and "no source ever", so it must keep blocking - this is the startup
        // protection the empty-scope rule cannot give up.

        // Act
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        await Task.Yield();
        Assert.False(wait.IsCompleted);
        monitor.CompleteSourceRegistration();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WhenOneWaitsReEvaluationThrows_ThenOtherPendingWaitsAreStillReEvaluated()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();

        var healthyRoot = new Person(context);
        var healthySource = new TestStateSource(healthyRoot);
        monitor.Register(healthySource);

        // A hold keeps registration incomplete while both waits below are created, so their
        // fast-path IsBranchSynchronized check short-circuits on IsRegistrationComplete before walking any
        // scope, so the poison wait cannot throw until both waits are in the list.
        var hold = monitor.DeferWaitCompletion();
        monitor.CompleteSourceRegistration();

        // Added to _waits before the healthy wait below, so a loop with no per-wait isolation
        // reaches this one first. The throw comes from the ANCHOR's own scope walk, so it recurs on
        // every re-evaluation pass. An earlier version of this test drove the throw from a
        // one-shot diagnostic warning instead, which meant nothing threw on the second pass and
        // the test passed with the per-wait isolation removed.
        var poisonWait = new PoisonAnchor(context).WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(poisonWait.IsCompleted);

        // A second, later wait whose own re-evaluation never touches the poison anchor: its scope
        // walk only ever visits healthyRoot.
        var healthyWait = healthyRoot.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(healthyWait.IsCompleted);

        // Releasing the hold makes registration complete and triggers a re-evaluation pass over
        // every pending wait, including the poison one, whose scope walk throws.
        var releaseException = Assert.Throws<InvalidOperationException>(() => hold.Dispose());
        Assert.Equal("scope walk is broken", releaseException.Message);

        // Act - transitioning healthySource triggers a further re-evaluation pass over every
        // pending wait. Without per-wait isolation, the throw while re-evaluating poisonWait
        // (first in the list) would abort the pass before healthyWait (second) is ever looked at
        // again, which would leave it pending forever - a lost wakeup.
        healthySource.ReportSynchronized();

        // Assert
        await healthyWait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WhenASourceRegistersWhileAWaitIsPending_ThenTheWaitIsReEvaluated()
    {
        // Arrange
        // A source arriving while a wait is already pending must be taken into account: the wait
        // must not complete when only the original source synchronizes. Note this does NOT pin
        // Register's own trailing OnWaitConditionChanged() - registering can only ever add
        // constraints, never satisfy a wait, so that call is defensive rather than load-bearing
        // (see the remark on Register).
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var first = new TestStateSource(root);
        monitor.Register(first);
        monitor.CompleteSourceRegistration();

        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        // Act - a second in-scope source arrives while the wait is still pending, then both settle.
        var second = new TestStateSource(root);
        monitor.Register(second);
        first.ReportSynchronized();
        Assert.False(wait.IsCompleted);
        second.ReportSynchronized();

        // Assert
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void WhenAWaitReEvaluationThrowsDuringAttach_ThenTheAttachStillCompletes()
    {
        // Arrange
        // OnWaitConditionChanged runs from inside LifecycleInterceptor's attach lock. Letting an
        // exception out of it leaves the graph half-attached: later handlers are skipped and child
        // properties never attach. The poison anchor throws on every re-evaluation, standing in for
        // the realistic causes (a throwing logger, a custom subject with a throwing Data getter).
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        monitor.Register(new TestStateSource(root));
        monitor.CompleteSourceRegistration();

        var hold = monitor.DeferWaitCompletion();
        var poisonWait = new PoisonAnchor(context).WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(poisonWait.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => hold.Dispose());

        // Act - attaching fires a property reference add, which re-evaluates the poison wait.
        var child = new Person();
        var exception = Record.Exception(() => root.Mother = child);

        // Assert
        Assert.Null(exception);
        Assert.Same(child, root.Mother);
        Assert.NotEmpty(child.GetParents());
    }

    [Fact]
    public async Task WhenASourceIsRegisteredMidWait_ThenItIsIncludedAndReBlocksTheWait()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var first = new TestStateSource(root);
        monitor.Register(first);
        monitor.CompleteSourceRegistration();
        first.ReportSynchronized();
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        var second = new TestStateSource(root);
        monitor.Register(second);
        var secondWait = root.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        Assert.False(secondWait.IsCompleted);
        second.ReportSynchronized();
        await secondWait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WhenEveryInScopeSourceIsStopped_ThenTheWaitCompletesVacuously()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        monitor.Register(source);
        monitor.CompleteSourceRegistration();

        // Act
        source.ReportStopped();

        // Assert
        await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WhenCancelled_ThenTheWaitPropagatesCancellation()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        // An in-scope, unsynchronized source keeps the wait genuinely pending: an empty scope would
        // instead complete vacuously once registration is complete (see IsBranchSynchronized), leaving
        // nothing for cancellation to interrupt.
        monitor.Register(new TestStateSource(root));
        monitor.CompleteSourceRegistration();
        using var cancellation = new CancellationTokenSource();

        // Act
        var wait = root.WaitForSynchronizationAsync(cancellation.Token);
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task WhenTheAnchorIsReparentedWithinTheTree_ThenAPendingWaitIsReEvaluated()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var left = new Person(context);
        var right = new Person(context);
        var moving = new Person();

        // moving hangs under BOTH parents, so the single write in Act removes one parent link
        // without ever emptying the anchor's scope. That matters: an empty scope completes a wait
        // vacuously (see IsBranchSynchronized), which would mask exactly the staleness this test exists to
        // catch - an earlier version of this test let the scope go empty and therefore passed with
        // the handler ordering inverted, and passed with the Act removed entirely.
        left.Mother = moving;
        right.Mother = moving;

        // Rooted at left, so it leaves moving's scope when the left link is cut. Never
        // synchronizes, so while it IS in scope the wait cannot complete.
        var blocking = new TestStateSource(left);
        monitor.Register(blocking);

        // Rooted at right, so it stays in scope throughout and is already satisfied.
        var healthy = new TestStateSource(right);
        monitor.Register(healthy);

        monitor.CompleteSourceRegistration();
        healthy.ReportSynchronized();

        var wait = moving.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        // Act - one parent link removed. moving stays in the tree through right, so this fires
        // neither IsContextAttach nor IsContextDetach, only a property reference removal.
        left.Mother = null;

        // Assert
        // This is the test that catches handler-ordering defects. If the monitor ran before
        // ParentTrackingHandler it would re-evaluate against moving's stale parent set, still find
        // blocking in scope, and never look again - the wait would hang.
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WhenASourceIsDisposedWithoutBeingStopped_ThenStoppedPrecedesUnregistered()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var source = new TestStateSource(new Person(context));
        await source.StartAsync(CancellationToken.None);
        var received = new System.Collections.Concurrent.ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        source.Dispose();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.SourceUnregistered));
        var kinds = received.Select(e => e.Kind).ToList();
        var stoppedIndex = kinds.FindIndex(k => k == SourceEventKind.StateChanged);
        var unregisteredIndex = kinds.FindIndex(k => k == SourceEventKind.SourceUnregistered);
        Assert.True(stoppedIndex >= 0 && stoppedIndex < unregisteredIndex);
    }

    [Fact]
    public async Task WhenASourceRegistersAndImmediatelyTransitions_ThenSourceRegisteredPrecedesStateChanged()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));
        var source = new TestStateSource(new Person(context));

        // Act
        monitor.Register(source);
        source.ReportSynchronized();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.StateChanged));
        var kinds = received.Select(e => e.Kind).ToList();
        var registeredIndex = kinds.FindIndex(k => k == SourceEventKind.SourceRegistered);
        var stateChangedIndex = kinds.FindIndex(k => k == SourceEventKind.StateChanged);
        Assert.True(registeredIndex >= 0 && registeredIndex < stateChangedIndex);
    }

    [Fact]
    public async Task WhenAHoldIsTakenWhileABranchIsAlreadySynchronized_ThenANewWaitBlocksUntilTheHoldIsDisposed()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        monitor.Register(source);
        monitor.CompleteSourceRegistration();
        source.ReportSynchronized();

        // Act - DeferWaitCompletion re-arms IsRegistrationComplete even though the branch itself has
        // nothing left to synchronize: a wait created while the hold is outstanding must still block
        // purely on the hold, and only unblock once it is disposed.
        var hold = monitor.DeferWaitCompletion();
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        Assert.False(wait.IsCompleted);
        hold.Dispose();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WhenASourceIsConstructedButNeverStarted_ThenItDoesNotAffectAWait()
    {
        // Arrange
        // neverStarted is rooted at the SAME subject as started, so it would be squarely in scope if
        // registration were what put a source there. It never registers, so it must not count - and
        // because it stays Synchronizing forever, a wait that did count it could never complete. An
        // earlier version of this test let neverStarted stay Synchronizing but asserted only that it
        // was absent from Sources, which holds trivially whether or not the wait consults it.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var started = new TestStateSource(root);
        var neverStarted = new TestStateSource(root);
        monitor.Register(started);
        monitor.CompleteSourceRegistration();

        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        // Act
        started.ReportSynchronized();

        // Assert - completes despite neverStarted being in scope and never synchronizing.
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(neverStarted, monitor.Sources);
        Assert.Equal(SourceState.Synchronizing, neverStarted.State);
    }

    [Fact]
    public async Task WhenNoMonitorIsReachable_ThenTheWaitThrowsWithGuidance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root = new Person(context);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => root.WaitForSynchronizationAsync(CancellationToken.None));
        Assert.Contains("WithSourceMonitoring", exception.Message);
    }

    [Fact]
    public async Task WhenAWaitRacesASourceTransitionToSynchronized_ThenTheWaitAlwaysCompletes()
    {
        // Arrange
        // Reproduces a lost wakeup: OnWaitConditionChanged used to gate itself on a lock-free
        // "_waits is empty" read. That read could observe the not-yet-published wait list while
        // WaitForSynchronizationAsync's own check-and-add was still in flight under _lock, so the
        // signal would return without ever completing the wait it just missed - and nothing later
        // would re-evaluate it. A barrier lines up the two racing operations on every iteration so
        // the narrow window gets exercised repeatedly instead of relying on incidental timing.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        monitor.Register(source);
        monitor.CompleteSourceRegistration();

        const int iterations = 20_000;
        using var barrier = new Barrier(2);

        var transitionThread = new Thread(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30)))
                {
                    return;
                }

                source.ReportSynchronized();
            }
        })
        {
            IsBackground = true
        };
        transitionThread.Start();

        // Act & Assert
        for (var i = 0; i < iterations; i++)
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(30)));

            var wait = root.WaitForSynchronizationAsync(CancellationToken.None);

            // A bounded timeout so a regression fails this test instead of wedging the run.
            await wait.WaitAsync(TimeSpan.FromSeconds(5));

            // Reset for the next iteration. TransitionTo allows Synchronized -> Synchronizing, and
            // wait's removal from the monitor's pending-wait list happens before the awaited task
            // above completes (see PendingWait.AwaitAsync), so the monitor has no leftover wait
            // state carried into the next iteration.
            source.ReportSynchronizing();
        }

        transitionThread.Join(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void WhenOneMonitorsReleaseThrows_ThenTheOtherMonitorIsStillReleased()
    {
        // Arrange
        var lifecycle = new LifecycleInterceptor();
        var parent = InterceptorSubjectContext.Create();
        var child = InterceptorSubjectContext.Create();
        parent.AddService(lifecycle);
        child.AddService(lifecycle);
        parent.WithFullPropertyTracking().WithSourceMonitoring();
        child.WithSourceMonitoring();
        child.AddFallbackContext(parent);

        var parentMonitor = parent.GetSourceMonitor();
        var childMonitor = child.GetServices<SourceMonitor>()[0];

        parentMonitor.CompleteSourceRegistration();
        childMonitor.CompleteSourceRegistration();

        var root = new Person(child);
        var throwing = new ThrowingScopeSource(root);
        childMonitor.Register(throwing);

        // Re-arms both monitors and gives each a pending wait, so the release below has something
        // to re-evaluate: it is that re-evaluation, not the release itself, that throws.
        var hold = child.DeferWaitCompletion();
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => hold.Dispose());

        // Assert - the throwing monitor's own count still dropped, and the other monitor's hold was
        // not stranded by the first one's exception.
        Assert.Equal("scope check failed", exception.Message);
        Assert.True(parentMonitor.IsRegistrationComplete);
        Assert.True(childMonitor.IsRegistrationComplete);
    }
}

/// <summary>A source whose RootSubject getter throws, to exercise exception-safety in wait re-evaluation.</summary>
internal sealed class ThrowingScopeSource : TestStateSource
{
    public ThrowingScopeSource(IInterceptorSubject rootSubject) : base(rootSubject)
    {
    }

    public override IInterceptorSubject RootSubject => throw new InvalidOperationException("scope check failed");
}

/// <summary>Captures warning and error messages logged through it, to assert on diagnostics.</summary>
internal sealed class RecordingLogger : ILogger
{
    public List<string> Warnings { get; } = [];

    public List<string> Errors { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
        {
            lock (Warnings)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
        else if (logLevel == LogLevel.Error)
        {
            lock (Errors)
            {
                Errors.Add(formatter(state, exception));
            }
        }
    }
}

/// <summary>Always resolves to the same <see cref="RecordingLogger"/>, regardless of category.</summary>
internal sealed class RecordingLoggerFactory(RecordingLogger logger) : ILoggerFactory
{
    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => logger;

    public void Dispose()
    {
    }
}

/// <summary>
/// A subject whose <see cref="Data"/> getter throws, so any parent walk that reaches it fails.
/// </summary>
/// <remarks>
/// Used as a wait anchor to make one wait's re-evaluation throw on every pass while leaving every
/// other wait unaffected. GetParents() reads Data, and SourceScope's walk starts from the anchor, so
/// the throw lands inside that wait's own IsBranchSynchronized and nowhere else. A throwing source would not
/// work here: IsBranchSynchronized iterates the shared source list for every wait, so one poison source makes
/// every wait's evaluation throw.
/// </remarks>
internal sealed class PoisonAnchor(IInterceptorSubjectContext context) : IInterceptorSubject
{
    public object SyncRoot { get; } = new();

    public IInterceptorSubjectContext Context { get; } = context;

    public ConcurrentDictionary<(string? property, string key), object?> Data =>
        throw new InvalidOperationException("scope walk is broken");

    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; } =
        new Dictionary<string, SubjectPropertyMetadata>();

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
        throw new NotSupportedException();
}
