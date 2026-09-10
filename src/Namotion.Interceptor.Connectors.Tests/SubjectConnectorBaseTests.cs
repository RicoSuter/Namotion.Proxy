using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectConnectorBaseTests
{
    [Fact]
    public async Task WhenStarted_ThenStartTimeIsStamped()
    {
        // Arrange
        using var connector = new TestConnector();

        // Act
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => connector.Diagnostics.StartTime is not null);

        // Assert
        Assert.NotNull(connector.Diagnostics.StartTime);
    }

    [Fact]
    public async Task WhenRunAsyncFaults_ThenTheErrorIsRecordedAndTheConnectorIsNotOperational()
    {
        // Arrange
        var error = new InvalidOperationException("run failed");
        using var connector = new TestConnector { Fault = error };
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        connector.MarkOperational();
        Assert.True(connector.Diagnostics.IsOperational);

        // Act
        connector.Release();

        // Awaited rather than polled: the finally has run once the execute task completes, so the
        // liveness asserted below is settled.
        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.ExecuteTask!);

        // Assert
        Assert.Same(error, connector.Diagnostics.LastError);
        Assert.False(connector.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenStoppedWhileBackingOffFromAFault_ThenTheFaultIsStillTheReportedError()
    {
        // Arrange
        var error = new InvalidOperationException("connect failed");
        using var connector = new TestConnector { FaultBeforeBackoff = error };
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => connector.Diagnostics.LastError is not null);

        // Act
        await ((IHostedService)connector).StopAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connector.ExecuteTask!);

        // Assert
        Assert.Same(error, connector.Diagnostics.LastError);
    }

    [Fact]
    public async Task WhenStoppedAfterBeingOperational_ThenItReportsNotOperational()
    {
        // Arrange
        using var connector = new TestConnector();
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        connector.MarkOperational();
        await AsyncTestHelpers.WaitUntilAsync(() => connector.Diagnostics.IsOperational == true);

        // Act
        connector.Release();
        await ((IHostedService)connector).StopAsync(CancellationToken.None);

        // Assert
        Assert.False(connector.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenDisposedWithoutStopping_ThenItReportsNotOperational()
    {
        // Arrange
        var connector = new TestConnector();
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        connector.MarkOperational();
        await AsyncTestHelpers.WaitUntilAsync(() => connector.Diagnostics.IsOperational == true);

        // Act
        connector.Dispose();

        // Assert
        Assert.False(connector.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenReEntered_ThenTheEpochMovesAndTotalsReset()
    {
        // Arrange
        using var connector = new TestConnector();
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => connector.Diagnostics.StartTime is not null);
        var firstStart = connector.Diagnostics.StartTime;
        connector.AddOutboundDrop(5);
        connector.Release();
        await ((IHostedService)connector).StopAsync(CancellationToken.None);

        // Act
        ClockTestHelpers.WaitForClockTick();
        connector.Reopen();
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => connector.Diagnostics.StartTime != firstStart);

        // Assert
        Assert.NotEqual(firstStart, connector.Diagnostics.StartTime);
        Assert.Equal(0, connector.Diagnostics.OutboundChanges.TotalDropped);
    }

    [Fact]
    public async Task WhenReEnteredAfterStopping_ThenItCanReportOperationalAgain()
    {
        // Arrange
        using var connector = new TestConnector();
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        connector.MarkOperational();
        connector.Release();
        await ((IHostedService)connector).StopAsync(CancellationToken.None);
        Assert.False(connector.Diagnostics.IsOperational);

        // Act
        connector.Reopen();
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        connector.MarkOperational();

        // Assert
        Assert.True(connector.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenStartedWhilePreviousExecutionIsStillActive_ThenSecondStartIsRejected()
    {
        // Arrange
        using var connector = new TestConnector { IgnoreCancellation = true };
        var hostedService = (IHostedService)connector;
        await hostedService.StartAsync(CancellationToken.None);
        var firstExecution = connector.ExecuteTask!;

        using var cancelledStop = new CancellationTokenSource();
        await cancelledStop.CancelAsync();
        await hostedService.StopAsync(cancelledStop.Token);
        Assert.False(firstExecution.IsCompleted);

        try
        {
            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => hostedService.StartAsync(CancellationToken.None));
            Assert.Equal(1, connector.ExecutionCount);
        }
        finally
        {
            var latestExecution = connector.ExecuteTask;
            connector.Release();
            await firstExecution;
            if (!ReferenceEquals(firstExecution, latestExecution))
            {
                await latestExecution!;
            }
        }
    }

    [Fact]
    public async Task WhenARegisteredResettableThrowsOnStart_ThenTheErrorIsRecordedAndTheConnectorIsNotOperational()
    {
        // Arrange: RegisterResettable and IResettableMetrics are public, so a third-party Reset that
        // throws is reachable.
        var failure = new InvalidOperationException("reset failed");
        using var connector = new TestConnector();
        connector.RegisterResettable(new ThrowingResettableMetrics(failure));
        connector.MarkOperational();
        Assert.True(connector.Diagnostics.IsOperational);

        // Act
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ((IHostedService)connector).StartAsync(CancellationToken.None));

        // Assert
        Assert.Same(failure, thrown);
        Assert.Same(failure, connector.Diagnostics.LastError);
        Assert.False(connector.Diagnostics.IsOperational);
    }

    [Fact]
    public void WhenReadThroughTheConnectorInterface_ThenItIsTheConnectorsOwnDiagnostics()
    {
        // Arrange
        using var connector = new TestConnector();

        // Act
        var throughInterface = ((ISubjectConnector)connector).Diagnostics;

        // Assert
        // Derived connectors narrow the member with a covariant override, and the interface has to
        // land on that same override rather than on a view reading metrics nobody writes to.
        Assert.Same(connector.Diagnostics, throughInterface);
    }

    private sealed class ThrowingResettableMetrics(Exception failure) : IResettableMetrics
    {
        public void Reset() => throw failure;
    }

    private sealed class TestConnector : SubjectConnectorBase
    {
        private readonly ConnectorMetrics _metrics;
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _executionCount;

        public TestConnector()
            : this(new ConnectorMetrics())
        {
        }

        private TestConnector(ConnectorMetrics metrics)
            : base(metrics)
        {
            _metrics = metrics;
            Diagnostics = new ConnectorDiagnostics(metrics);
        }

        public Exception? Fault { get; init; }

        public Exception? FaultBeforeBackoff { get; init; }

        public bool IgnoreCancellation { get; init; }

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public override IInterceptorSubject RootSubject => throw new NotSupportedException();

        public override ConnectorDiagnostics Diagnostics { get; }

        public void MarkOperational() => _metrics.MarkOperational();

        public void RegisterResettable(IResettableMetrics metrics) => _metrics.RegisterResettable(metrics);

        public void AddOutboundDrop(long count) => _metrics.OutboundChanges.AddDropped(count);

        public void Release() => _gate.TrySetResult();

        public void Reopen() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task RunAsync(CancellationToken stoppingToken)
        {
            Interlocked.Increment(ref _executionCount);

            if (FaultBeforeBackoff is not null)
            {
                // Mirrors a connector that records its connect failure and then backs off inside its
                // own catch block. The backoff never elapses, so the only way out is the stopping token.
                _metrics.ReportError(FaultBeforeBackoff);
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }

            await using (stoppingToken.Register(() =>
            {
                if (!IgnoreCancellation)
                {
                    _gate.TrySetResult();
                }
            }))
            {
                await _gate.Task;
            }

            // Faulting only once the gate opens keeps the failure asynchronous: BackgroundService
            // surfaces an execute task that has already faulted from StartAsync instead, which would
            // move the throw away from the running connector.
            if (Fault is not null)
            {
                throw Fault;
            }
        }
    }
}
