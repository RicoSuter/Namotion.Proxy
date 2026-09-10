using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Connectors.Tests.Diagnostics;

public class QueueMetricsTests
{
    [Fact]
    public void WhenNothingIsRegistered_ThenDepthIsZeroAndCapacityIsNull()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));

        // Act
        var diagnostics = new QueueDiagnostics(metrics);

        // Assert
        Assert.Equal(0, diagnostics.Depth);
        Assert.Null(diagnostics.Capacity);
        Assert.Equal(0, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenProviderIsRegistered_ThenDepthAndCapacityComeFromIt()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var depth = 7;
        var diagnostics = new QueueDiagnostics(metrics);

        // Act
        using var registration = metrics.Register(() => depth, capacity: 100);

        // Assert
        Assert.Equal(7, diagnostics.Depth);
        Assert.Equal(100, diagnostics.Capacity);
    }

    [Fact]
    public void WhenRegistrationHandleIsDisposed_ThenDepthReturnsToZeroButCapacityStays()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var registration = metrics.Register(() => 7, capacity: 100);
        var diagnostics = new QueueDiagnostics(metrics);

        // Act
        registration.Dispose();

        // Assert
        Assert.Equal(0, diagnostics.Depth);
        Assert.Equal(100, diagnostics.Capacity);
    }

    [Fact]
    public void WhenDropsAreReported_ThenTotalAdvancesDuringTheBurst()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        using var registration = metrics.Register(() => 0, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);

        // Act
        metrics.AddDropped(5);

        // Assert
        Assert.Equal(5, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenRegistrationIsHandedOver_ThenTotalNeitherDecreasesNorDoubleCounts()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var firstRegistration = metrics.Register(() => 0, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);
        metrics.AddDropped(5);
        Assert.Equal(5, diagnostics.TotalDropped);

        // Act
        firstRegistration.Dispose();
        var afterFirstRegistrationIsDisposed = diagnostics.TotalDropped;

        using var secondRegistration = metrics.Register(() => 0, capacity: 10);
        var afterSecondRegistration = diagnostics.TotalDropped;
        metrics.AddDropped(3);

        // Assert
        Assert.Equal(5, afterFirstRegistrationIsDisposed);
        Assert.Equal(5, afterSecondRegistration);
        Assert.Equal(8, diagnostics.TotalDropped);
    }

    [Fact]
    public async Task WhenDropReportIsGatedAcrossRegistrationRelease_ThenFinalTotalDoesNotDecreaseOrLoseTheDrop()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var diagnostics = new QueueDiagnostics(metrics);
        var registration = metrics.Register(() => 0, capacity: 10);
        metrics.AddDropped(1);
        Assert.Equal(1, diagnostics.TotalDropped);
        var dropReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueDrop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportDrop = Task.Run(async () =>
        {
            dropReady.SetResult();
            await continueDrop.Task;
            metrics.AddDropped(1);
        });

        // Act
        await dropReady.Task;
        var observedBeforeRelease = diagnostics.TotalDropped;
        registration.Dispose();
        continueDrop.SetResult();
        await reportDrop;

        // Assert
        Assert.Equal(1, observedBeforeRelease);
        Assert.Equal(2, diagnostics.TotalDropped);
    }

    [Fact]
    public async Task WhenAddDroppedRacesWithRegistrationDisposal_ThenNoIncrementIsLost()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var diagnostics = new QueueDiagnostics(metrics);
        const int iterations = 10_000;

        // Act
        var adder = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                metrics.AddDropped(1);
            }
        });

        var churner = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                metrics.Register(() => 0, capacity: 10).Dispose();
            }
        });

        await Task.WhenAll(adder, churner);

        // Assert
        Assert.Equal(iterations, diagnostics.TotalDropped);
    }

    [Fact]
    public async Task WhenTotalIsReadRepeatedlyDuringChurn_ThenItNeverDecreases()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var diagnostics = new QueueDiagnostics(metrics);
        var stop = false;
        var observed = 0L;
        var decreased = false;

        // Act
        var reader = Task.Run(() =>
        {
            // Only this task writes observed and decreased, and the await below establishes the
            // happens-before ordering, so plain reads and writes are enough here.
            while (!Volatile.Read(ref stop))
            {
                var current = diagnostics.TotalDropped;
                if (current < observed)
                {
                    decreased = true;
                }

                observed = current;
            }
        });

        for (var i = 0; i < 500; i++)
        {
            var registration = metrics.Register(() => 0, capacity: 10);

            // Stepping instead of adding the final count in one operation widens the window in which
            // a broken handover shows up as a decrease.
            for (var step = 1; step <= 4; step++)
            {
                metrics.AddDropped(1);
            }

            registration.Dispose();
        }

        Volatile.Write(ref stop, true);
        await reader;

        // Assert
        Assert.False(decreased);
        Assert.Equal(2000, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenReset_ThenTotalDroppedReturnsToZeroAndRegistrationSurvives()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        metrics.AddDropped(9);
        using var registration = metrics.Register(() => 4, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);

        // Act
        metrics.Reset();

        // Assert
        Assert.Equal(0, diagnostics.TotalDropped);
        Assert.Equal(4, diagnostics.Depth);
        Assert.Equal(10, diagnostics.Capacity);
    }

    [Fact]
    public void WhenAReporterBelongsToThePreviousEpoch_ThenItsLateDropsAreIgnored()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(SourceMetrics.OutboundChanges));
        var diagnostics = new QueueDiagnostics(metrics);
        var oldReporter = metrics.CreateDropReporter();
        oldReporter(1);

        // Act
        metrics.Reset();
        var currentReporter = metrics.CreateDropReporter();
        oldReporter(2);
        currentReporter(3);

        // Assert
        Assert.Equal(3, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenRegistrationIsReplacedAfterReset_ThenTotalDroppedKeepsLaterDrops()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var firstRegistration = metrics.Register(() => 0, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);
        metrics.AddDropped(5);
        metrics.Reset();
        Assert.Equal(0, diagnostics.TotalDropped);

        // Act
        metrics.AddDropped(3);
        firstRegistration.Dispose();
        using var secondRegistration = metrics.Register(() => 0, capacity: 10);

        // Assert
        Assert.Equal(3, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenRegisterIsCalledWhileARegistrationIsLive_ThenItThrowsAndHandleDisposalAllowsRegisteringAgain()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var firstRegistration = metrics.Register(() => 0, capacity: 10);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => metrics.Register(() => 0, capacity: 20));

        // Act
        firstRegistration.Dispose();
        using var secondRegistration = metrics.Register(() => 3, capacity: 30);
        var diagnostics = new QueueDiagnostics(metrics);

        // Assert
        Assert.Equal(3, diagnostics.Depth);
        Assert.Equal(30, diagnostics.Capacity);
    }

    [Fact]
    public void WhenRegisterIsCalledWhileARegistrationIsLive_ThenTheMessageNamesTheBuffer()
    {
        // Arrange: the failure surfaces from inside a connector's retry loop, which catches it and
        // tries again, so the message is all an operator gets.
        var metrics = new SourceMetrics();
        using var registration = metrics.OutboundRetries.Register(() => 0, capacity: 10);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(
            () => metrics.OutboundRetries.Register(() => 0, capacity: 20));

        Assert.Contains(nameof(SourceMetrics.OutboundRetries), exception.Message);
    }

    [Fact]
    public void WhenTheHandleIsDeclaredAfterTheBuffer_ThenTheRegistrationIsReleasedBeforeTheBufferGoesAway()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var diagnostics = new QueueDiagnostics(metrics);
        var depthSeenByTheBuffer = -1;

        // Act
        RunScope();

        // Assert
        Assert.Equal(0, depthSeenByTheBuffer);

        void RunScope()
        {
            using var buffer = new CallbackDisposable(() => depthSeenByTheBuffer = diagnostics.Depth);
            using var registration = metrics.Register(() => 7, capacity: null);

            Assert.Equal(7, diagnostics.Depth);
        }
    }

    [Fact]
    public void WhenDisposedHandleIsDisposedAgainAfterReplacement_ThenReplacementRemainsRegistered()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var diagnostics = new QueueDiagnostics(metrics);
        var firstRegistration = metrics.Register(() => 7, capacity: 10);
        firstRegistration.Dispose();
        using var secondRegistration = metrics.Register(() => 3, capacity: 20);

        // Act
        firstRegistration.Dispose();

        // Assert
        Assert.Equal(3, diagnostics.Depth);
        Assert.Equal(20, diagnostics.Capacity);
    }

    [Fact]
    public void WhenRegisterThrows_ThenTheLiveRegistrationIsUntouched()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        using var registration = metrics.Register(() => 7, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => metrics.Register(() => 3, capacity: 20));

        Assert.Equal(7, diagnostics.Depth);
        Assert.Equal(10, diagnostics.Capacity);
    }

    [Fact]
    public void WhenTheHandleIsDisposedTwice_ThenProviderIsReleasedAndDropsRemainCountedOnce()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var diagnostics = new QueueDiagnostics(metrics);
        var registration = metrics.Register(() => 7, capacity: 10);
        metrics.AddDropped(4);

        // Act
        registration.Dispose();
        var totalDroppedAfterFirstDisposal = diagnostics.TotalDropped;
        registration.Dispose();

        // Assert
        Assert.Equal(0, diagnostics.Depth);
        Assert.Equal(4, totalDroppedAfterFirstDisposal);
        Assert.Equal(4, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenDepthProviderThrows_ThenDiagnosticsReportZeroDepth()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        using var registration = metrics.Register(
            () => throw new InvalidOperationException("boom"),
            capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);

        // Act
        var depth = diagnostics.Depth;
        var totalDropped = diagnostics.TotalDropped;

        // Assert
        Assert.Equal(0, depth);
        Assert.Equal(0, totalDropped);
    }

    [Fact]
    public async Task WhenRegistrationsRace_ThenExactlyOneRegistrationSucceeds()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        const int registrationCount = 16;
        using var barrier = new Barrier(registrationCount + 1);
        var registrations = new System.Collections.Concurrent.ConcurrentBag<IDisposable>();
        var failures = new System.Collections.Concurrent.ConcurrentBag<InvalidOperationException>();

        var attempts = Enumerable.Range(0, registrationCount)
            .Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();

                try
                {
                    registrations.Add(metrics.Register(() => 0, capacity: 10));
                }
                catch (InvalidOperationException exception)
                {
                    failures.Add(exception);
                }
            }))
            .ToArray();

        // Act
        barrier.SignalAndWait();
        await Task.WhenAll(attempts);

        // Assert
        Assert.Single(registrations);
        Assert.Equal(registrationCount - 1, failures.Count);

        // Cleanup
        registrations.Single().Dispose();
    }

    private sealed class CallbackDisposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
