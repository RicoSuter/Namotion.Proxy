using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Covers what a completed wait reports, as opposed to when it completes, which is SourceWaitTests.
/// </summary>
public class SourceWaitResultTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithSourceMonitoring();

    [Fact]
    public async Task WhenEveryInScopeSourceIsSynchronized_ThenTheResultIsSynchronized()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var first = new TestStateSource(root);
        var second = new TestStateSource(root);
        monitor.Register(first);
        monitor.Register(second);
        monitor.CompleteSourceRegistration();

        // Act
        first.ReportSynchronized();
        second.ReportSynchronized();
        var result = await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(SourceSynchronizationResult.Synchronized, result);
    }

    [Fact]
    public async Task WhenNoSourceIsInScope_ThenTheResultIsSynchronized()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        monitor.CompleteSourceRegistration();

        // Act
        var result = await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // Assert - vacuous, and the reason Synchronized is not proof that a source exists.
        Assert.Equal(SourceSynchronizationResult.Synchronized, result);
    }

    [Fact]
    public async Task WhenEverySourceStoppedAfterSynchronizing_ThenTheResultIsStale()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        monitor.Register(source);
        monitor.CompleteSourceRegistration();

        // Act - the initial load completed, so the values it delivered are real but no longer live.
        source.ReportSynchronized();
        source.ReportStopped();
        var result = await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(SourceSynchronizationResult.Stale, result);
    }

    [Fact]
    public async Task WhenOneSourceStoppedAfterSynchronizingBesideALiveOne_ThenTheResultIsStale()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var live = new TestStateSource(root);
        var dropped = new TestStateSource(root);
        monitor.Register(live);
        monitor.Register(dropped);
        monitor.CompleteSourceRegistration();

        // Act
        live.ReportSynchronized();
        dropped.ReportSynchronized();
        dropped.ReportStopped();
        var result = await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(SourceSynchronizationResult.Stale, result);
    }

    [Fact]
    public async Task WhenOneSourceStoppedWithoutSynchronizingBesideALiveOne_ThenTheResultIsIncomplete()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var live = new TestStateSource(root);
        var neverDelivered = new TestStateSource(root);
        monitor.Register(live);
        monitor.Register(neverDelivered);
        monitor.CompleteSourceRegistration();

        // Act
        live.ReportSynchronized();
        neverDelivered.ReportStopped();
        var result = await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(SourceSynchronizationResult.Incomplete, result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenBothStopKindsAreInScope_ThenTheResultIsIncompleteInEitherOrder(bool neverDeliveredFirst)
    {
        // Arrange
        // Registration order decides walk order, and only one of the two orders pins the worst-wins
        // comparison: registered second, an unconditional assignment would also land on Incomplete.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var neverDelivered = new TestStateSource(root);
        var dropped = new TestStateSource(root);

        if (neverDeliveredFirst)
        {
            monitor.Register(neverDelivered);
            monitor.Register(dropped);
        }
        else
        {
            monitor.Register(dropped);
            monitor.Register(neverDelivered);
        }

        monitor.CompleteSourceRegistration();

        // Act
        dropped.ReportSynchronized();
        dropped.ReportStopped();
        neverDelivered.ReportStopped();
        var result = await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(SourceSynchronizationResult.Incomplete, result);
    }

    [Fact]
    public void WhenASourceIsStillSynchronizingBesideAStoppedOne_ThenTheWaitStaysPending()
    {
        // Arrange
        // The stopped source is registered first on purpose: in the opposite order the walk
        // short-circuits before reaching it, and this would pass against an early exit on Incomplete.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var neverDelivered = new TestStateSource(root);
        var stillLoading = new TestStateSource(root);
        monitor.Register(neverDelivered);
        monitor.Register(stillLoading);
        monitor.CompleteSourceRegistration();

        // Act
        neverDelivered.ReportStopped();
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        Assert.False(wait.IsCompleted);
    }

    [Fact]
    public async Task WhenASourceStopsWithoutSynchronizingWhileAWaitIsPending_ThenTheWaitResolvesToIncomplete()
    {
        // Arrange
        // The only path carrying the verdict through OnWaitConditionChanged rather than the
        // already-satisfied fast path.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        monitor.Register(source);
        monitor.CompleteSourceRegistration();

        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        // Act
        source.ReportStopped();

        // Assert
        Assert.Equal(SourceSynchronizationResult.Incomplete, await wait.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WhenAFailedSourceIsDisposedWhileAWaitIsPending_ThenTheWaitResolvesToIncomplete()
    {
        // Arrange
        // Dispose publishes Stopped while still registered and only then unregisters. The other
        // order would shrink the scope to empty first and report a vacuous Synchronized.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new NeverLoadingSource(root);
        await source.StartAsync(CancellationToken.None);
        monitor.CompleteSourceRegistration();

        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        // Act
        source.Dispose();

        // Assert
        Assert.Equal(SourceSynchronizationResult.Incomplete, await wait.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WhenAStoppedSourceIsUnregistered_ThenALaterWaitReportsSynchronized()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        monitor.Register(source);
        monitor.CompleteSourceRegistration();
        source.ReportStopped();

        Assert.Equal(
            SourceSynchronizationResult.Incomplete,
            await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        // Act - unregistering removes it from every scope, so the branch has nothing left to report on.
        monitor.Unregister(source);
        var result = await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // Assert - a failure that is cleaned up stops being visible, so Synchronized is not proof
        // the branch was ever loaded.
        Assert.Equal(SourceSynchronizationResult.Synchronized, result);
    }

    [Fact]
    public async Task WhenASourceReportsSynchronizedWithoutATimestamp_ThenTheResultIsSynchronized()
    {
        // Arrange
        // Only a stopped source's timestamp carries the "never synchronized" meaning, so a missing
        // one while synchronized must not be read as a failure.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        monitor.Register(new TimestamplessSource(root));
        monitor.CompleteSourceRegistration();

        // Act
        var result = await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(SourceSynchronizationResult.Synchronized, result);
    }

    [Fact]
    public async Task WhenTheResultIsIncomplete_ThenReAwaitingReturnsIncompleteAgain()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        monitor.Register(source);
        monitor.CompleteSourceRegistration();
        source.ReportStopped();

        // Act - Stopped is terminal and the source stays registered, so waiting again cannot
        // improve the answer.
        var first = await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var second = root.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(SourceSynchronizationResult.Incomplete, first);
        Assert.True(second.IsCompleted);
        Assert.Equal(SourceSynchronizationResult.Incomplete, await second);
    }

    [Fact]
    public void WhenTheWaitIsAlreadySatisfied_ThenTheCompletedTaskIsCached()
    {
        // Arrange - the documented usage is re-awaiting per operation, so the already-satisfied
        // path must not allocate a task per call.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        monitor.CompleteSourceRegistration();

        // Act
        var first = root.WaitForSynchronizationAsync(CancellationToken.None);
        var second = root.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        Assert.Same(first, second);
    }

    [Fact]
    public async Task WhenDisposeRacesAFailingRegistration_ThenTheSourceIsNeverLeftRegistered()
    {
        // Arrange
        // If Dispose wins the race before Register has added the source, its unregister no-ops and
        // the registration failure is the only thing left that can clean up. However the race lands,
        // a stranded source is both a leak and a branch pinned to Incomplete for good. The
        // throwing-Register counterpart of
        // SourceMonitorTests.WhenDisposeLandsInsideStartAsyncsRegistrationLoop_ThenNoStoppedSourceStaysRegistered,
        // with fewer iterations because each one arms and retires a poison wait.
        var leaked = 0;
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var context = CreateContext();
            var monitor = context.GetSourceMonitor();
            using var cancellation = new CancellationTokenSource();
            var poisonWait = ArmFailingRegistration(context, monitor, cancellation.Token);
            var source = new TestStateSource(new Person(context));

            using var ready = new Barrier(2);
            var disposal = Task.Run(() =>
            {
                ready.SignalAndWait();

                // Unregistering re-evaluates the armed poison wait, which throws after the
                // unregister itself has happened. That is the fixture, not a disposal defect.
                return Record.Exception(() => source.Dispose());
            });

            // Act - the start may also find the source already stopped and return without ever
            // registering, so the failure is recorded rather than asserted on.
            ready.SignalAndWait();
            await Record.ExceptionAsync(() => source.StartAsync(CancellationToken.None));
            await disposal;

            if (monitor.Sources.Contains(source))
            {
                leaked++;
            }

            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poisonWait);
        }

        // Assert - a leaked source keeps a live StateChanged subscription and holds its root subject
        // for the monitor's lifetime, with nothing left that would ever remove it.
        Assert.Equal(0, leaked);
    }

    [Fact]
    public async Task WhenARegistrationFails_ThenTheSourceIsReportedStoppedAndTheBranchIsIncomplete()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var poisonWait = ArmFailingRegistration(context, monitor);
        var source = new TestStateSource(root);

        // Act - Register adds the source under the lock and only then re-evaluates waits, so the
        // throw arrives with it already registered.
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.StartAsync(CancellationToken.None));

        // Assert
        Assert.Equal(SourceState.Stopped, source.State);
        Assert.Contains(source, monitor.Sources);
        Assert.Equal(
            SourceSynchronizationResult.Incomplete,
            await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.False(poisonWait.IsCompleted);
    }

    [Fact]
    public async Task WhenARegistrationFails_ThenTheOriginalExceptionPropagates()
    {
        // Arrange
        // Reporting the failure re-enters the same wait re-evaluation that just threw.
        // TransitionStateTo isolates each handler, so the caller must still see the original.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        _ = ArmFailingRegistration(context, monitor);
        var source = new TestStateSource(root);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.StartAsync(CancellationToken.None));

        // Assert
        Assert.Equal("scope walk is broken", exception.Message);
    }

    [Fact]
    public async Task WhenASourceWithAFailedRegistrationIsDisposed_ThenItUnregistersCleanly()
    {
        // Arrange - the failure path keeps the monitor list populated so Dispose can still unwind it.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        using var cancellation = new CancellationTokenSource();
        var poisonWait = ArmFailingRegistration(context, monitor, cancellation.Token);
        var source = new TestStateSource(root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.StartAsync(CancellationToken.None));

        // Cancelling retires the poison wait, so the unregistration below has nothing left to
        // re-evaluate and its scope walk cannot surface instead.
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poisonWait);

        // Act
        var exception = Record.Exception(() => source.Dispose());

        // Assert
        Assert.Null(exception);
        Assert.DoesNotContain(source, monitor.Sources);
    }

    /// <summary>
    /// Leaves the monitor in a state where any further wait re-evaluation throws, which is what makes
    /// <c>SourceMonitor.Register</c> throw. Returns the poison wait, which stays pending throughout.
    /// </summary>
    /// <remarks>
    /// The hold keeps registration incomplete while the poison wait is created, so its own fast-path
    /// check short-circuits before walking any scope. Releasing it triggers the first re-evaluation.
    /// </remarks>
    private static Task<SourceSynchronizationResult> ArmFailingRegistration(
        IInterceptorSubjectContext context, SourceMonitor monitor, CancellationToken cancellationToken = default)
    {
        // The walk runs per source, so with none registered the poison anchor never throws and the
        // wait completes vacuously. This sentinel is rooted on an unrelated subject, so it is out of
        // scope for every other anchor here.
        monitor.Register(new TestStateSource(new Person(context)));

        var hold = monitor.DeferWaitCompletion();
        monitor.CompleteSourceRegistration();

        var poisonWait = monitor.WaitForSynchronizationAsync(new PoisonAnchor(), cancellationToken);
        Assert.False(poisonWait.IsCompleted);

        Assert.Throws<InvalidOperationException>(() => hold.Dispose());
        return poisonWait;
    }

    /// <summary>
    /// A source whose initial load never completes, so it stays Synchronizing until it is stopped.
    /// </summary>
    /// <remarks><see cref="TestStateSource"/> reaches Synchronized as soon as its pump runs.</remarks>
    private sealed class NeverLoadingSource(IInterceptorSubject rootSubject) : TestStateSource(rootSubject)
    {
        public override async Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
    }

    /// <summary>
    /// An <see cref="ISubjectSource"/> implemented directly that reports Synchronized while never
    /// stamping <see cref="ISubjectSource.LastSynchronizedAt"/>, unlike <see cref="SubjectSourceBase"/>.
    /// </summary>
    private sealed class TimestamplessSource(IInterceptorSubject rootSubject) : ISubjectSource
    {
        public IInterceptorSubject RootSubject { get; } = rootSubject;

        public int WriteBatchSize => 0;

        public SourceState State => SourceState.Synchronized;

        public DateTimeOffset? LastSynchronizedAt => null;

        public DateTimeOffset StateChangeTime { get; } = DateTimeOffset.UtcNow;

        public SourceDiagnostics Diagnostics { get; } = new(new SourceMetrics());

        ConnectorDiagnostics ISubjectConnector.Diagnostics => Diagnostics;

        public event EventHandler<SourceEvent>? StateChanged
        {
            add { }
            remove { }
        }

        public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
            => Task.FromResult<Action?>(null);

        public ValueTask<WriteResult> WriteChangesAsync(
            ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
            => new(WriteResult.Success);
    }
}
