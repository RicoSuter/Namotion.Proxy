using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
using Namotion.Interceptor.Testing;
using static Namotion.Interceptor.OpcUa.Tests.Client.ClientSourceTestFactory;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Covers the counters the client source owns on behalf of components that are rebuilt on every
/// connect attempt, so their totals measure the source's run rather than its latest attempt.
/// </summary>
public class HoistedMetricsTests
{
    [Fact]
    public void WhenPollingMetricsAreReset_ThenEveryCumulativeCounterReturnsToZero()
    {
        // Arrange
        var metrics = new PollingMetrics();
        metrics.RecordRead();
        metrics.RecordFailedRead();
        metrics.RecordValueChange();
        metrics.RecordSlowPoll();
        metrics.RecordCircuitBreakerTrip();

        // Act
        metrics.Reset();

        // Assert
        Assert.Equal(0, metrics.TotalReads);
        Assert.Equal(0, metrics.FailedReads);
        Assert.Equal(0, metrics.ValueChanges);
        Assert.Equal(0, metrics.SlowPolls);
        Assert.Equal(0, metrics.CircuitBreakerTrips);
    }

    [Fact]
    public void WhenReadAfterWriteMetricsAreReset_ThenEveryCumulativeCounterReturnsToZero()
    {
        // Arrange
        var metrics = new ReadAfterWriteMetrics();
        metrics.RecordScheduled();
        metrics.RecordExecuted(2);
        metrics.RecordCoalesced();
        metrics.RecordFailed(1);

        // Act
        metrics.Reset();

        // Assert
        Assert.Equal(0, metrics.Scheduled);
        Assert.Equal(0, metrics.Executed);
        Assert.Equal(0, metrics.Coalesced);
        Assert.Equal(0, metrics.Failed);
    }

    [Fact]
    public void WhenReconnectionMetricsAreReset_ThenCountersClearButTheLastConnectionSurvives()
    {
        // Arrange
        var metrics = new ReconnectionMetrics();
        metrics.RecordAttemptStart();
        metrics.RecordSuccess();
        metrics.RecordFailure();
        metrics.RecordAbandoned();
        var lastConnected = metrics.LastConnectedAt;
        Assert.NotNull(lastConnected);

        // Act
        metrics.Reset();

        // Assert
        Assert.Equal(0, metrics.TotalAttempts);
        Assert.Equal(0, metrics.Successful);
        Assert.Equal(0, metrics.Failed);
        Assert.Equal(0, metrics.Abandoned);
        Assert.Equal(lastConnected, metrics.LastConnectedAt);
    }

    [Fact]
    public async Task WhenTheSessionManagerIsRecreated_ThenTheSubComponentCountersAreNotRebased()
    {
        // Arrange
        await using var source = CreateClientSource();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using (await CreateSessionManagerAsync(source, propertyWriter))
        {
            source.PollingMetrics.RecordRead();
            source.PollingMetrics.RecordCircuitBreakerTrip();
            source.ReadAfterWriteMetrics.RecordScheduled();
        }

        // Act
        await using var recreated = await CreateSessionManagerAsync(source, propertyWriter);

        // Assert
        Assert.Equal(1, recreated.PollingDiagnostics!.TotalSuccessfulReads);
        Assert.Equal(1, recreated.PollingDiagnostics!.TotalCircuitBreakerTrips);
        Assert.Equal(1, recreated.ReadAfterWriteDiagnostics!.TotalScheduledReads);
    }

    [Fact]
    public async Task WhenTheConnectorStartsANewEpoch_ThenTheHoistedCountersAreReset()
    {
        // Arrange - without property tracking the pump fails its configuration guard immediately,
        // which starts an epoch without needing a server.
        await using var source = CreateClientSource(withPropertyTracking: false);
        source.PollingMetrics.RecordRead();
        source.ReadAfterWriteMetrics.RecordScheduled();
        source.ReconnectionMetrics.RecordAttemptStart();

        // Act
        await StartAndIgnoreTheConfigurationFailureAsync(source);

        // Assert
        Assert.Equal(0, source.PollingMetrics.TotalReads);
        Assert.Equal(0, source.ReadAfterWriteMetrics.Scheduled);
        Assert.Equal(0, source.ReconnectionMetrics.TotalAttempts);
    }

    private static async Task<SessionManager> CreateSessionManagerAsync(
        OpcUaSubjectClientSource source, SubjectPropertyWriter propertyWriter)
    {
        var sessionManager = new SessionManager(
            source,
            propertyWriter,
            CreateConfiguration(),
            source.PollingMetrics,
            source.ReadAfterWriteMetrics,
            NullLogger.Instance);

        // The polling loop is spawned by the constructor; wait for it so disposal has a task to join
        // rather than racing its own cancellation.
        await AsyncTestHelpers.WaitUntilAsync(
            () => sessionManager.PollingManager!.IsRunning,
            message: "The polling manager should start with the session manager");

        return sessionManager;
    }

    private static async Task StartAndIgnoreTheConfigurationFailureAsync(OpcUaSubjectClientSource source)
    {
        try
        {
            await source.StartAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // The pump surfaces its own configuration guard when it faults synchronously.
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.StartTime is not null,
            message: "The connector should have stamped a start epoch");
    }
}
