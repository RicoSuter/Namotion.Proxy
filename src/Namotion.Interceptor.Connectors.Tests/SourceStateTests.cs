using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceStateTests
{
    [Fact]
    public void WhenReadingTheEnum_ThenUnclaimedIsTheDefault()
    {
        // Arrange & Act
        var state = default(SourceState);

        // Assert
        Assert.Equal(SourceState.Unclaimed, state);
    }

    [Fact]
    public void WhenNoSourceClaimedTheProperty_ThenGetSourceStateReturnsUnclaimed()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithLifecycle();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var state = property.GetSourceState();

        // Assert
        Assert.Equal(SourceState.Unclaimed, state);
    }

    [Fact]
    public void WhenSourceClaimedTheProperty_ThenGetSourceStateReturnsTheSourcesState()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithLifecycle();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source = new TestStateSource(person);
        property.SetSource(source);

        // Act
        var state = property.GetSourceState();

        // Assert
        Assert.Equal(SourceState.Synchronizing, state);
    }

    [Fact]
    public void WhenTransitioningToTheSameState_ThenNoEventIsRaised()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        var raised = 0;
        source.StateChanged += (_, _) => Interlocked.Increment(ref raised);

        // Act
        source.ReportSynchronizing();

        // Assert
        Assert.Equal(SourceState.Synchronizing, source.State);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void WhenNeverTransitioned_ThenStateChangeTimeIsStampedAtConstruction()
    {
        // Arrange
        var before = DateTimeOffset.UtcNow;

        // Act
        var source = new TestStateSource(new Person());

        // Assert
        Assert.Equal(SourceState.Synchronizing, source.State);
        Assert.InRange(source.StateChangeTime, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void WhenTransitioningToSynchronized_ThenStateChangeTimeIsSetBeforeTheEventIsRaised()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        DateTimeOffset? observedInHandler = null;
        source.StateChanged += (_, _) => observedInHandler = source.StateChangeTime;

        // Act
        source.ReportSynchronized();

        // Assert
        Assert.Equal(SourceState.Synchronized, source.State);
        Assert.Equal(source.StateChangeTime, observedInHandler);
    }

    [Fact]
    public void WhenSynchronizationIsLost_ThenStateChangeTimeMovesToTheLoss()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        source.ReportSynchronized();
        var synchronizedAt = source.StateChangeTime;

        // Act
        ClockTestHelpers.WaitForClockTick();
        source.ReportSynchronizing();

        // Assert
        Assert.Equal(SourceState.Synchronizing, source.State);
        Assert.True(source.StateChangeTime > synchronizedAt,
            "The timestamp must move to the moment synchronization was lost, not stay at the last good one.");
    }

    [Fact]
    public void WhenTheTransitionIsANoOp_ThenStateChangeTimeDoesNotMove()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        source.ReportSynchronized();
        var first = source.StateChangeTime;

        // Act
        ClockTestHelpers.WaitForClockTick();
        source.ReportSynchronized();

        // Assert
        Assert.Equal(first, source.StateChangeTime);
    }

    [Fact]
    public void WhenStopped_ThenNoFurtherTransitionSucceeds()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        source.ReportSynchronized();
        source.ReportStopped();
        var eventsAfterStop = 0;
        source.StateChanged += (_, _) => Interlocked.Increment(ref eventsAfterStop);
        var timestampAtStop = source.StateChangeTime;

        // Act
        ClockTestHelpers.WaitForClockTick();
        source.ReportSynchronizing();
        source.ReportSynchronized();

        // Assert
        Assert.Equal(SourceState.Stopped, source.State);
        Assert.Equal(0, eventsAfterStop);
        Assert.Equal(timestampAtStop, source.StateChangeTime);
    }

    [Fact]
    public void WhenSynchronizedThenStopped_ThenLastSynchronizedAtIsNotCleared()
    {
        // Arrange
        // Branch waits tell "stopped having delivered its initial load" from "stopped having never
        // delivered anything" solely by this timestamp surviving the stop. The assertion in
        // WhenStopped_ThenNoFurtherTransitionSucceeds compares two post-stop reads, so it would pass
        // just as happily against an implementation that cleared the field on Stopped.
        var source = new TestStateSource(new Person());
        source.ReportSynchronized();
        var timestampWhileSynchronized = source.LastSynchronizedAt;
        Assert.NotNull(timestampWhileSynchronized);

        // Act
        source.ReportStopped();

        // Assert
        Assert.NotNull(source.LastSynchronizedAt);
        Assert.Equal(timestampWhileSynchronized, source.LastSynchronizedAt);
    }

    [Fact]
    public async Task WhenSynchronizedIsHammeredConcurrentlyWithStopped_ThenSynchronizedIsNeverPublishedAfterStoppedAndBothTimestampsFreeze()
    {
        // Arrange
        // TransitionTo serializes the state change, the StateChangeTime write and the event raise
        // inside one lock (see its own remarks), so this race is deterministic BY CONSTRUCTION given
        // that lock. There is therefore no way to force the two orderings this test forbids without
        // weakening the lock itself - this is a stress loop, not a test that hits a narrow timing
        // window, and its job is to catch a regression that removes or narrows that lock, not to
        // prove a race exists today. Many iterations and many hammering transitions per iteration
        // maximize the chance that a weakened lock would show a lost update or an out-of-order event.
        const int iterations = 200;
        const int hammerCountPerIteration = 500;

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var source = new TestStateSource(new Person());
            var events = new ConcurrentQueue<SourceEvent>();
            DateTimeOffset? stateChangeTimeWhenStopped = null;
            DateTimeOffset? lastSynchronizedAtWhenStopped = null;
            source.StateChanged += (_, sourceEvent) =>
            {
                events.Enqueue(sourceEvent);
                if (sourceEvent.NewState == SourceState.Stopped)
                {
                    stateChangeTimeWhenStopped = source.StateChangeTime;
                    lastSynchronizedAtWhenStopped = source.LastSynchronizedAt;
                }
            };

            using var barrier = new Barrier(2);
            var hammerTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < hammerCountPerIteration; i++)
                {
                    source.ReportSynchronizing();
                    source.ReportSynchronized();
                }
            });
            var stopTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                source.ReportStopped();
            });

            // Act
            await Task.WhenAll(hammerTask, stopTask);

            // Assert
            Assert.Equal(SourceState.Stopped, source.State);
            var ordered = events.ToArray();
            var stoppedIndex = Array.FindIndex(ordered, e => e.NewState == SourceState.Stopped);
            Assert.True(stoppedIndex >= 0, "Expected a Stopped transition to have been published.");
            for (var afterStop = stoppedIndex + 1; afterStop < ordered.Length; afterStop++)
            {
                Assert.NotEqual(SourceState.Synchronized, ordered[afterStop].NewState);
            }

            // Stopped is terminal, so both timestamps observed inside the handler at the moment
            // Stopped was published must equal their values after every racing thread has finished.
            // Not asserted non-null: the stopping thread can win the barrier before the hammer ever
            // reaches Synchronized, so a null here is a legal outcome of the race.
            Assert.Equal(stateChangeTimeWhenStopped, source.StateChangeTime);
            Assert.Equal(lastSynchronizedAtWhenStopped, source.LastSynchronizedAt);
        }
    }

    [Fact]
    public void WhenAThrowingHandlerIsSubscribed_ThenTheTransitionStillCompletes()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        source.StateChanged += (_, _) => throw new InvalidOperationException("handler is buggy");

        // Act
        source.ReportSynchronized();

        // Assert
        Assert.Equal(SourceState.Synchronized, source.State);
    }

    [Fact]
    public void WhenAThrowingHandlerIsSubscribedBeforeAnotherHandler_ThenTheOtherHandlerStillObservesTheTransition()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        var laterHandlerRaised = 0;
        source.StateChanged += (_, _) => throw new InvalidOperationException("handler is buggy");
        source.StateChanged += (_, _) => Interlocked.Increment(ref laterHandlerRaised);

        // Act
        source.ReportSynchronized();

        // Assert
        Assert.Equal(SourceState.Synchronized, source.State);
        Assert.Equal(1, laterHandlerRaised);
    }

    [Fact]
    public void WhenSourceTransitionsAgainAfterEventCapture_ThenCurrentStateReflectsTheLatestStateWhileNewStateStaysFrozen()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        SourceEvent? capturedEvent = null;
        source.StateChanged += (_, sourceEvent) => capturedEvent ??= sourceEvent;
        source.ReportSynchronized();

        // Act
        source.ReportStopped();

        // Assert
        Assert.NotNull(capturedEvent);
        Assert.Equal(SourceState.Synchronized, capturedEvent.Value.NewState);
        Assert.Equal(SourceState.Stopped, capturedEvent.Value.CurrentState);
    }

    [Fact]
    public void WhenBufferingStartsOutsideThePump_ThenTheSourceReportsSynchronizing()
    {
        // Arrange
        var person = new Person();
        var source = new TestStateSource(person);
        var writer = new SubjectPropertyWriter(source, NullLogger.Instance);
        source.ReportSynchronized();

        // Act
        writer.StartBuffering();

        // Assert
        Assert.Equal(SourceState.Synchronizing, source.State);
    }

    [Fact]
    public async Task WhenTheInitialLoadCompletesOutsideThePump_ThenTheSourceReportsSynchronized()
    {
        // Arrange
        var person = new Person();
        var source = new TestStateSource(person);
        var writer = new SubjectPropertyWriter(source, NullLogger.Instance);
        writer.StartBuffering();

        // Act
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Assert
        Assert.Equal(SourceState.Synchronized, source.State);
    }

    [Fact]
    public async Task WhenTheInitialLoadThrows_ThenTheSourceDoesNotReportSynchronized()
    {
        // Arrange
        var person = new Person();
        var source = new ThrowingLoadSource(person);
        var writer = new SubjectPropertyWriter(source, NullLogger.Instance);
        writer.StartBuffering();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.LoadInitialStateAndResumeAsync(CancellationToken.None));
        Assert.Equal(SourceState.Synchronizing, source.State);
    }

    [Fact]
    public void WhenAConnectorDetectsLossBeforeBuffering_ThenReportConnectionLostTransitions()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        source.ReportSynchronized();

        // Act
        source.SimulateConnectionLost();

        // Assert
        Assert.Equal(SourceState.Synchronizing, source.State);
    }
}

/// <summary>
/// A source that exposes the transition seam directly, so state machine behaviour can be tested
/// without a pump, a network, or a hosted service lifecycle.
/// </summary>
internal class TestStateSource : SubjectSourceBase
{
    // An unattached root gets an inert empty context, which resolves the same nothing its empty
    // executor view did; these state tests never start the source.
    public TestStateSource(IInterceptorSubject rootSubject)
        : base(rootSubject.TryGetContext() ?? InterceptorSubjectContext.Create(), NullLogger.Instance)
    {
        RootSubject = rootSubject;
    }

    public override IInterceptorSubject RootSubject { get; }

    // Test-only aliases for the transition seam: production code drives these two through
    // SubjectPropertyWriter's direct TransitionTo calls now (see SubjectPropertyWriter.StartBuffering
    // and LoadInitialStateAndResumeAsync), but the test suite still drives state machine behaviour
    // through named, state-specific entry points rather than a bare TransitionTo call.
    public void ReportSynchronizing() => TransitionStateTo(SourceState.Synchronizing);

    public void ReportSynchronized() => TransitionStateTo(SourceState.Synchronized);

    public void ReportStopped() => TransitionStateTo(SourceState.Stopped);

    /// <summary>Exposes the now-protected ReportConnectionLost seam for tests outside the type hierarchy.</summary>
    public void SimulateConnectionLost() => ReportConnectionLost();

    /// <summary>How many times the pump body has been entered. Used to prove the terminal guard works.</summary>
    public int ExecuteCount;

    protected override Task<IAsyncDisposable?> StartListeningAsync(
        SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ExecuteCount);
        return Task.FromResult<IAsyncDisposable?>(null);
    }

    public override Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        => Task.FromResult<Action?>(null);

    public override ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        => new(WriteResult.Success);
}

internal sealed class ThrowingLoadSource : TestStateSource
{
    public ThrowingLoadSource(IInterceptorSubject rootSubject) : base(rootSubject) { }

    public override Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        => throw new InvalidOperationException("load failed");
}
