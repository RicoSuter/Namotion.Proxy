using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Connectors.Tests.Diagnostics;

public class ConnectorMetricsTests
{
    [Fact]
    public void WhenLivenessIsNeverReported_ThenOperationalStateAndTimestampAreNull()
    {
        // Arrange
        var metrics = new ConnectorMetrics();

        // Act
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.MarkStarted();

        // Assert
        Assert.Null(diagnostics.IsOperational);
        Assert.Null(diagnostics.OperationalChangeTime);
        Assert.NotNull(diagnostics.StartTime);
    }

    [Fact]
    public void WhenMarkedOperational_ThenFlagAndTimestampMoveTogether()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.MarkStarted();

        // Act
        metrics.MarkOperational();

        // Assert
        Assert.True(diagnostics.IsOperational);
        Assert.NotNull(diagnostics.OperationalChangeTime);
    }

    [Fact]
    public void WhenMarkedNotOperationalFirst_ThenFalseAndTimestampArePublished()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);

        // Act
        metrics.MarkNotOperational();

        // Assert
        Assert.False(diagnostics.IsOperational);
        Assert.NotNull(diagnostics.OperationalChangeTime);
    }

    [Fact]
    public void WhenMarkedOperationalTwice_ThenTheTimestampDoesNotMove()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.MarkOperational();
        var first = diagnostics.OperationalChangeTime;

        // Act
        // Without the tick both calls sample the same coarse clock value, so this would pass even
        // without the no-op guard.
        ClockTestHelpers.WaitForClockTick();
        metrics.MarkOperational();

        // Assert
        Assert.Equal(first, diagnostics.OperationalChangeTime);
    }

    [Fact]
    public void WhenMarkedNotOperational_ThenTheTimestampMovesOnTheDownTransition()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.MarkOperational();
        var whenUp = diagnostics.OperationalChangeTime;

        // Act
        ClockTestHelpers.WaitForClockTick();
        metrics.MarkNotOperational();

        // Assert
        Assert.False(diagnostics.IsOperational);
        Assert.NotNull(whenUp);
        Assert.True(diagnostics.OperationalChangeTime > whenUp);
    }

    [Fact]
    public void WhenStopped_ThenLaterMarkOperationalIsIgnored()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.MarkOperational();

        // Act
        metrics.MarkStopped();
        metrics.MarkOperational();

        // Assert
        Assert.False(diagnostics.IsOperational);
    }

    [Fact]
    public void WhenRestartedAfterStopping_ThenTheLatchIsReleasedAndLivenessCanMoveAgain()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.MarkOperational();
        metrics.MarkStopped();

        // Act
        metrics.MarkStarted();
        metrics.MarkOperational();

        // Assert
        Assert.True(diagnostics.IsOperational);
    }

    [Fact]
    public void WhenStoppedTwice_ThenTheSecondCallLeavesStateAndTimestampUnchanged()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.MarkOperational();
        metrics.MarkStopped();
        var afterFirstStop = diagnostics.OperationalChangeTime;

        // Act
        ClockTestHelpers.WaitForClockTick();
        metrics.MarkStopped();

        // Assert
        Assert.False(diagnostics.IsOperational);
        Assert.Equal(afterFirstStop, diagnostics.OperationalChangeTime);
    }

    [Fact]
    public void WhenUnmonitoredConnectorIsStopped_ThenLivenessBecomesFalseAndLateReportsAreIgnored()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);

        // Act
        metrics.MarkStopped();
        metrics.MarkOperational();

        // Assert: a stopped connector is known not to be serving even though it never measured that.
        Assert.False(diagnostics.IsOperational);
        Assert.NotNull(diagnostics.OperationalChangeTime);
    }

    [Fact]
    public void WhenMonitoredConnectorIsRestarted_ThenLivenessReturnsToUnavailable()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.MarkOperational();
        metrics.MarkStopped();

        // Act
        metrics.MarkStarted();

        // Assert: the new epoch reports nothing observed rather than the previous epoch's value and a
        // timestamp from before it began.
        Assert.Null(diagnostics.IsOperational);
        Assert.Null(diagnostics.OperationalChangeTime);
    }

    [Fact]
    public void WhenUnmonitoredConnectorIsRestarted_ThenLivenessRemainsNullUntilExplicitlyReported()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.MarkStopped();

        // Act
        metrics.MarkStarted();

        // Assert
        Assert.Null(diagnostics.IsOperational);
        Assert.Null(diagnostics.OperationalChangeTime);
    }

    [Fact]
    public void WhenErrorIsReported_ThenItIsStickyAcrossRecovery()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        var error = new InvalidOperationException("boom");

        // Act
        metrics.ReportError(error);
        metrics.MarkOperational();

        // Assert
        Assert.Same(error, diagnostics.LastError);
    }

    [Fact]
    public void WhenRestarted_ThenLastErrorIsClearedThoughRecoveryAloneDoesNotClearIt()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.ReportError(new InvalidOperationException("boom"));

        // Act
        metrics.MarkOperational();
        var afterRecovery = diagnostics.LastError;
        metrics.MarkStarted();

        // Assert
        Assert.NotNull(afterRecovery);
        Assert.Null(diagnostics.LastError);
    }

    [Fact]
    public void WhenRestarted_ThenStartTimeMovesAndEveryTotalResets()
    {
        // Arrange
        var metrics = new SourceMetrics();
        var diagnostics = new SourceDiagnostics(metrics);
        var hoisted = new CountingResettable();
        metrics.RegisterResettable(hoisted);

        metrics.MarkStarted();
        var firstStart = diagnostics.StartTime;
        metrics.OutboundChanges.AddDropped(3);
        metrics.OutboundRetries.AddDropped(4);
        metrics.InboundBuffer.AddDropped(5);

        // Act
        ClockTestHelpers.WaitForClockTick();
        metrics.MarkStarted();

        // Assert
        Assert.NotEqual(firstStart, diagnostics.StartTime);
        Assert.Null(diagnostics.IsOperational);
        Assert.Equal(0, diagnostics.OutboundChanges.TotalDropped);
        Assert.Equal(0, diagnostics.OutboundRetries.TotalDropped);
        Assert.Equal(0, diagnostics.InboundBuffer.TotalDropped);

        // Once per MarkStarted, including the one in the arrange: the epoch reset is deliberately not
        // idempotent.
        Assert.Equal(2, hoisted.ResetCount);
    }

    [Fact]
    public async Task WhenRestartResetIsStillRunning_ThenTheNewEpochIsNotYetVisible()
    {
        // Arrange
        var metrics = new SourceMetrics();
        var diagnostics = new SourceDiagnostics(metrics);
        metrics.MarkStarted();
        var firstStart = diagnostics.StartTime;
        metrics.OutboundRetries.AddDropped(3);

        using var resettable = new BlockingResettable();
        metrics.RegisterResettable(resettable);
        ClockTestHelpers.WaitForClockTick();

        // Act
        // Off the thread pool: the restart parks inside the resettable for as long as this test holds
        // it, and a pool work item can wait seconds to be scheduled at all while the rest of the
        // assembly runs, which times out the wait below for a reason the test does not cover.
        var restart = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(metrics.MarkStarted, "restart reset");

        try
        {
            await resettable.Entered.WaitAsync(TimeSpan.FromSeconds(30));

            // Assert
            Assert.Equal(firstStart, diagnostics.StartTime);
        }
        finally
        {
            resettable.AllowReset();
            await restart;
        }

        Assert.NotEqual(firstStart, diagnostics.StartTime);
        Assert.Equal(0, diagnostics.OutboundRetries.TotalDropped);
    }

    [Fact]
    public void WhenNoThroughputCountersArePassed_ThenBothRatesAreNull()
    {
        // Arrange
        var metrics = new ConnectorMetrics();

        // Act
        var diagnostics = new ConnectorDiagnostics(metrics);

        // Assert
        Assert.Null(diagnostics.Throughput.IncomingPerSecond);
        Assert.Null(diagnostics.Throughput.OutgoingPerSecond);
    }

    [Fact]
    public void WhenOnlyIncomingIsInstrumented_ThenOutgoingStaysNullAndIncomingReportsZeroWhenIdle()
    {
        // Arrange
        var metrics = new ConnectorMetrics(incoming: new ThroughputCounter());

        // Act
        var diagnostics = new ConnectorDiagnostics(metrics);

        // Assert
        Assert.Equal(0.0, diagnostics.Throughput.IncomingPerSecond);
        Assert.Null(diagnostics.Throughput.OutgoingPerSecond);
    }

    [Fact]
    public void WhenNoClaimedPropertyProviderIsRegistered_ThenCountIsUnavailable()
    {
        // Arrange
        var metrics = new SourceMetrics();

        // Act
        var diagnostics = new SourceDiagnostics(metrics);

        // Assert
        Assert.Null(diagnostics.ClaimedPropertyCount);
    }

    [Fact]
    public void WhenClaimedPropertyProviderIsRegistered_ThenCountFollowsIt()
    {
        // Arrange
        var metrics = new SourceMetrics();
        var count = 0;
        metrics.RegisterClaimedProperties(() => count);
        var diagnostics = new SourceDiagnostics(metrics);

        // Act
        count = 42;

        // Assert
        Assert.Equal(42, diagnostics.ClaimedPropertyCount);
    }

    [Fact]
    public void WhenClaimedPropertyProviderThrows_ThenCountIsUnavailableInsteadOfThrowing()
    {
        // Arrange
        var metrics = new SourceMetrics();
        metrics.RegisterClaimedProperties(() => throw new InvalidOperationException("boom"));
        var diagnostics = new SourceDiagnostics(metrics);

        // Act
        var count = diagnostics.ClaimedPropertyCount;

        // Assert
        Assert.Null(count);
    }

    [Fact]
    public void WhenNullIsRegisteredOrReported_ThenTheGuardNamesTheParameter()
    {
        // Arrange
        var metrics = new SourceMetrics();

        // Act & Assert
        Assert.Equal("metrics", Assert.Throws<ArgumentNullException>(() => metrics.RegisterResettable(null!)).ParamName);
        Assert.Equal("error", Assert.Throws<ArgumentNullException>(() => metrics.ReportError(null!)).ParamName);
        Assert.Equal("count", Assert.Throws<ArgumentNullException>(() => metrics.RegisterClaimedProperties(null!)).ParamName);
    }

    [Fact]
    public async Task WhenLivenessIsFlippedConcurrently_ThenTheChangeTimestampNeverMovesBackwards()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        var stop = false;
        var movedBackwards = false;
        metrics.MarkOperational();

        // Act
        // Both threads flip the liveness: only a writer that lost the race to another writer can
        // stamp a stale timestamp.
        var flipper = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                metrics.MarkNotOperational();
                metrics.MarkOperational();
            }
        });

        var previous = DateTimeOffset.MinValue;
        var stampedReads = 0;
        for (var i = 0; i < 100_000; i++)
        {
            metrics.MarkNotOperational();
            metrics.MarkOperational();

            // The guarantee is per read: each read is internally consistent and the timestamp only
            // moves forward. IsOperational and OperationalChangeTime are two separate snapshots, so
            // reading both never yields one coherent pair.
            var changeTime = diagnostics.OperationalChangeTime;
            if (changeTime < previous)
            {
                movedBackwards = true;
            }

            if (changeTime is not null)
            {
                stampedReads++;
            }

            previous = changeTime ?? previous;
        }

        Volatile.Write(ref stop, true);
        await flipper;

        // Assert
        Assert.False(movedBackwards);

        // Backstops, so the assertion above cannot pass vacuously: a null timestamp compares false
        // against everything, so a getter that stopped stamping would leave the flag false too.
        Assert.Equal(100_000, stampedReads);
        Assert.True(diagnostics.IsOperational);
    }

    private sealed class CountingResettable : IResettableMetrics
    {
        public int ResetCount { get; private set; }

        public void Reset() => ResetCount++;
    }

    private sealed class BlockingResettable : IResettableMetrics, IDisposable
    {
        private readonly ManualResetEventSlim _allowReset = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal void AllowReset() => _allowReset.Set();

        public void Reset()
        {
            _entered.TrySetResult();
            _allowReset.Wait();
        }

        public void Dispose() => _allowReset.Dispose();
    }
}
