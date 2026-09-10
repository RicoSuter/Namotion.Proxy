using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectPropertyWriterTests
{
    [Fact]
    public async Task WhenAfterInit_ThenUpdatesAreAppliedImmediately()
    {
        // Arrange
        var writer = new SubjectPropertyWriter(CreateSource(), NullLogger.Instance);
        var updates = new List<string>();

        writer.StartBuffering();
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Act - write after initialization
        writer.Write(updates, u => u.Add("Immediate"));

        // Assert - applied immediately
        Assert.Single(updates);
        Assert.Equal("Immediate", updates[0]);
    }

    [Fact]
    public async Task WhenInitialStateProvided_ThenOrderIsInitialStateThenBuffered()
    {
        // Arrange
        var order = new List<string>();
        var writer = new SubjectPropertyWriter(
            CreateSource(() => order.Add("InitialState")), NullLogger.Instance);

        // Act
        writer.StartBuffering();
        writer.Write(order, o => o.Add("BufferedUpdate"));
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Assert - order: initial state first, then buffered
        Assert.Equal(2, order.Count);
        Assert.Equal("InitialState", order[0]);
        Assert.Equal("BufferedUpdate", order[1]);
    }

    [Fact]
    public async Task WhenUpdateThrows_ThenErrorIsLoggedAndOtherUpdatesApplied()
    {
        // Arrange
        var writer = new SubjectPropertyWriter(CreateSource(), NullLogger.Instance);
        var updates = new List<string>();

        // Act
        writer.StartBuffering();
        writer.Write(updates, u => u.Add("Update1"));
        writer.Write(updates, _ => throw new Exception("Test error"));
        writer.Write(updates, u => u.Add("Update3"));

        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Assert - first and third updates applied, second error logged (not thrown)
        Assert.Equal(2, updates.Count);
        Assert.Equal("Update1", updates[0]);
        Assert.Equal("Update3", updates[1]);
    }

    [Fact]
    public async Task WhenImmediateUpdateThrows_ThenErrorIsLoggedNotThrown()
    {
        // Arrange
        var writer = new SubjectPropertyWriter(CreateSource(), NullLogger.Instance);

        writer.StartBuffering();
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Act & Assert - should not throw
        writer.Write(0, _ => throw new Exception("Test error"));
    }

    [Fact]
    public async Task WhenStartBufferingCalledMultipleTimes_ThenOnlyLatestBufferIsReplayed()
    {
        // Arrange
        var writer = new SubjectPropertyWriter(CreateSource(), NullLogger.Instance);
        var updates = new List<string>();

        // Act
        writer.StartBuffering();
        writer.Write(updates, u => u.Add("First"));

        writer.StartBuffering(); // Reset buffer
        writer.Write(updates, u => u.Add("Second"));

        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Assert - only "Second" replayed
        Assert.Single(updates);
        Assert.Equal("Second", updates[0]);
    }

    [Fact]
    public async Task WhenLoadInitialStateAndResumeCalledTwice_ThenSecondCallSkipsReplay()
    {
        // Arrange
        var loadCount = 0;
        var replayCount = 0;
        var writer = new SubjectPropertyWriter(
            CreateSource(() => loadCount++), NullLogger.Instance);

        // Act
        writer.StartBuffering();
        writer.Write(replayCount, _ => replayCount++);
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None); // Second call

        // Assert
        // LoadInitialStateAsync called twice (before null check), but replay only happens once
        Assert.Equal(2, loadCount);
        Assert.Equal(1, replayCount);
    }

    [Fact]
    public async Task WhenNotClientSource_ThenNoInitialStateLoaded()
    {
        // Arrange - using ISubjectSource (not ISubjectSource)
        var writer = new SubjectPropertyWriter(CreateSource(), NullLogger.Instance);
        var updates = new List<string>();

        // Act
        writer.StartBuffering();
        writer.Write(updates, u => u.Add("Update"));
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Assert - update replayed without LoadInitialStateAsync call
        Assert.Single(updates);
        Assert.Equal("Update", updates[0]);
    }

    [Fact]
    public void WhenNoStartBufferingCalled_ThenUpdatesAreBuffered()
    {
        // Arrange - _updates starts as empty list (buffering by default)
        var writer = new SubjectPropertyWriter(CreateSource(), NullLogger.Instance);
        var updates = new List<string>();

        // Act
        writer.Write(updates, u => u.Add("Update"));

        // Assert - buffered because _updates starts as [] (not null)
        Assert.Empty(updates);
    }

    [Fact]
    public async Task WhenAStaleLoadCompletesAfterANewerCycleHasStartedBuffering_ThenTheStaleCycleIsDiscarded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);

        var staleLoadEntered = new TaskCompletionSource();
        var releaseStaleLoad = new TaskCompletionSource();
        var staleApplied = false;

        var source = new TestSubjectSource(person, context, NullLogger.Instance)
        {
            LoadInitialStateOverride = async _ =>
            {
                staleLoadEntered.TrySetResult();
                await releaseStaleLoad.Task;
                return (Action?)(() => staleApplied = true);
            },
        };

        // A standalone writer, separate from the source's own internal pump (never started here):
        // ReportSynchronizing/ReportSynchronized still drive the same source's real state machine,
        // since TransitionTo operates on the source instance, not on any one writer.
        var writer = new SubjectPropertyWriter(source, NullLogger.Instance);

        // Act
        writer.StartBuffering();                                       // cycle A (stale), generation 1
        var staleTask = writer.LoadInitialStateAndResumeAsync(CancellationToken.None);
        await staleLoadEntered.Task;                                   // cycle A is now blocked inside LoadInitialStateAsync

        writer.StartBuffering();                                       // cycle B supersedes A, generation 2

        releaseStaleLoad.SetResult();
        await staleTask;

        // Assert
        Assert.False(staleApplied, "The superseded cycle's stale snapshot must never be applied.");
        Assert.Equal(SourceState.Synchronizing, source.State);
    }

    [Fact]
    public async Task WhenLoadInitialStateAndResumeCompletesNonSuperseded_ThenTheSynchronizedReportRunsWhileTheWriterLockIsHeld()
    {
        // Arrange
        // The bug this pins is a narrow window (demonstrated only under a dedicated stress harness,
        // not reliably within a unit test's runtime) between the generation check passing and the
        // Synchronized report actually firing: a StartBuffering landing in that gap left the report
        // unguarded by the very check that was supposed to suppress it for a superseded cycle. The
        // fix closes the gap structurally by moving the report inside the same lock as the check, so
        // rather than trying to force the race itself, this test verifies the structural invariant
        // the fix establishes directly: StateChanged for Synchronized must fire while this writer's
        // own _lock is held. A background thread's non-blocking TryEnter on that same Lock instance
        // fails if and only if the reporting thread (running this handler, reentrant on _stateLock
        // per the documented StateChanged contract) still holds it.
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var source = new TestSubjectSource(person, context, NullLogger.Instance)
        {
            LoadInitialStateOverride = _ => Task.FromResult<Action?>(null),
        };
        var writer = new SubjectPropertyWriter(source, NullLogger.Instance);

        var lockField = typeof(SubjectPropertyWriter).GetField("_lock", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var writerLock = (Lock)lockField.GetValue(writer)!;

        bool? lockWasHeldDuringReport = null;
        source.StateChanged += (_, sourceEvent) =>
        {
            if (sourceEvent.NewState != SourceState.Synchronized)
            {
                return;
            }

            // Probe from another thread: this thread already owns _stateLock (reentrant) and, if the
            // fix is in place, _lock too, so a same-thread TryEnter would prove nothing either way.
            var acquiredByOtherThread = false;
            var probe = new Thread(() => acquiredByOtherThread = writerLock.TryEnter());
            probe.Start();
            probe.Join();
            if (acquiredByOtherThread)
            {
                writerLock.Exit();
            }

            lockWasHeldDuringReport = !acquiredByOtherThread;
        };

        // Act
        writer.StartBuffering();
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Assert
        Assert.True(lockWasHeldDuringReport,
            "Expected the Synchronized report to run while the writer's own _lock is held, " +
            "atomically with the generation check that decides whether to report at all.");
    }

    [Fact]
    public async Task WhenConnectionLossIsReportedWhileALoadIsInFlight_ThenTheInFlightLoadIsDiscarded()
    {
        // Arrange
        // ReportConnectionLost fires ahead of the reconnect's own StartBuffering (which is what
        // would otherwise bump the generation). Without invalidating the generation here too, a load
        // already in flight when the connection drops has no way to learn that, applies pre-outage
        // data once it completes, and certifies Synchronized - a false state that would then persist
        // until the next reconnect cycle actually calls StartBuffering.
        //
        // This must drive the SOURCE's own internal writer (reached via reflection), not a
        // standalone one: ReportConnectionLost calls SubjectSourceBase's own _propertyWriter field
        // directly, so a separate writer instance would never observe the invalidation and the test
        // would pass for the wrong reason.
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);

        var loadEntered = new TaskCompletionSource();
        var releaseLoad = new TaskCompletionSource();
        var applied = false;

        var source = new TestSubjectSource(person, context, NullLogger.Instance)
        {
            LoadInitialStateOverride = async _ =>
            {
                loadEntered.TrySetResult();
                await releaseLoad.Task;
                return (Action?)(() => applied = true);
            },
        };

        var writerField = typeof(SubjectSourceBase).GetField("_propertyWriter", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var writer = (SubjectPropertyWriter)writerField.GetValue(source)!;

        // Act
        writer.StartBuffering();                                    // generation 1
        var loadTask = writer.LoadInitialStateAndResumeAsync(CancellationToken.None);
        await loadEntered.Task;                                     // load is now blocked mid-flight

        source.SimulateConnectionLost();                             // does NOT call StartBuffering

        releaseLoad.SetResult();
        await loadTask;

        // Assert
        Assert.False(applied, "A load that completes after a reported connection loss must not apply pre-outage data.");
        Assert.Equal(SourceState.Synchronizing, source.State);
    }

    [Fact]
    public void WhenBuffering_ThenBufferedUpdateCountTracksTheBuffer()
    {
        // Arrange
        var writer = new SubjectPropertyWriter(CreateSource(), NullLogger.Instance);
        writer.StartBuffering();

        // Act
        writer.Write(0, static _ => { });
        writer.Write(1, static _ => { });

        // Assert
        Assert.Equal(2, writer.BufferedUpdateCount);
    }

    [Fact]
    public void WhenBufferingRestarts_ThenBufferedUpdateCountResets()
    {
        // Arrange
        var writer = new SubjectPropertyWriter(CreateSource(), NullLogger.Instance);
        writer.StartBuffering();
        writer.Write(0, static _ => { });
        writer.Write(1, static _ => { });

        // Act
        writer.StartBuffering();

        // Assert
        Assert.Equal(0, writer.BufferedUpdateCount);
    }

    [Fact]
    public async Task WhenTheBufferIsReplayed_ThenBufferedUpdateCountReturnsToZero()
    {
        // Arrange
        var writer = new SubjectPropertyWriter(CreateSource(), NullLogger.Instance);
        writer.StartBuffering();
        writer.Write(0, static _ => { });

        // Act
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, writer.BufferedUpdateCount);
    }

    [Fact]
    public async Task WhenASupersededLoadIsDiscarded_ThenTheSurvivingBufferStaysCounted()
    {
        // Arrange: the superseded load returns before touching the buffer the newer cycle filled.
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);

        var staleLoadEntered = new TaskCompletionSource();
        var releaseStaleLoad = new TaskCompletionSource();

        var source = new TestSubjectSource(person, context, NullLogger.Instance)
        {
            LoadInitialStateOverride = async _ =>
            {
                staleLoadEntered.TrySetResult();
                await releaseStaleLoad.Task;
                return (Action?)null;
            },
        };

        var writer = new SubjectPropertyWriter(source, NullLogger.Instance);

        // Act
        writer.StartBuffering();
        var staleTask = writer.LoadInitialStateAndResumeAsync(CancellationToken.None);
        await staleLoadEntered.Task;

        writer.StartBuffering();
        writer.Write(0, static _ => { });

        releaseStaleLoad.SetResult();
        await staleTask;

        // Assert
        Assert.Equal(1, writer.BufferedUpdateCount);
    }

    /// <summary>
    /// A minimal started-nowhere source for writer tests. The writer takes SubjectSourceBase rather
    /// than ISubjectSource because it drives the source's state transitions, so these cannot use a
    /// bare interface mock.
    /// </summary>
    private static TestSubjectSource CreateSource(Action? onLoadInitialState = null)
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        return new TestSubjectSource(new Person(context), context, NullLogger.Instance, writeRetryQueueSize: 0)
        {
            LoadInitialStateOverride = _ => Task.FromResult(onLoadInitialState)
        };
    }
}
