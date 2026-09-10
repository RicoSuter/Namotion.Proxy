using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Opc.Ua.Client;
using static Namotion.Interceptor.OpcUa.Tests.Client.ClientSourceTestFactory;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class ReconnectionMetricsTests
{
    [Fact]
    public void WhenCreated_ThenAllCountersAreZero()
    {
        // Arrange & Act
        var metrics = new ReconnectionMetrics();

        // Assert
        Assert.Equal(0, metrics.TotalAttempts);
        Assert.Equal(0, metrics.Successful);
        Assert.Equal(0, metrics.Failed);
        Assert.Null(metrics.LastConnectedAt);
    }

    [Fact]
    public void WhenRecordAttemptStart_ThenTotalAttemptsIncrements()
    {
        // Arrange
        var metrics = new ReconnectionMetrics();

        // Act
        metrics.RecordAttemptStart();
        metrics.RecordAttemptStart();

        // Assert
        Assert.Equal(2, metrics.TotalAttempts);
    }

    [Fact]
    public void WhenRecordSuccess_ThenSuccessfulIncrementsAndLastConnectedAtIsSet()
    {
        // Arrange
        var metrics = new ReconnectionMetrics();
        var before = DateTimeOffset.UtcNow;

        // Act
        metrics.RecordSuccess();

        // Assert
        Assert.Equal(1, metrics.Successful);
        Assert.NotNull(metrics.LastConnectedAt);
        Assert.True(metrics.LastConnectedAt >= before);
    }

    [Fact]
    public void WhenRecordFailure_ThenFailedIncrements()
    {
        // Arrange
        var metrics = new ReconnectionMetrics();

        // Act
        metrics.RecordFailure();
        metrics.RecordFailure();
        metrics.RecordFailure();

        // Assert
        Assert.Equal(3, metrics.Failed);
    }

    [Fact]
    public async Task WhenConcurrentAccess_ThenCountersAreCorrect()
    {
        // Arrange
        var metrics = new ReconnectionMetrics();
        const int threadCount = 10;
        const int opsPerThread = 100;

        // Act
        var tasks = Enumerable.Range(0, threadCount)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < opsPerThread; i++)
                {
                    metrics.RecordAttemptStart();
                    metrics.RecordSuccess();
                    metrics.RecordFailure();
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert
        var expected = threadCount * opsPerThread;
        Assert.Equal(expected, metrics.TotalAttempts);
        Assert.Equal(expected, metrics.Successful);
        Assert.Equal(expected, metrics.Failed);
    }

    [Fact]
    public async Task WhenDisposedWithPendingSdkReconnect_ThenAttemptIsAbandoned()
    {
        // Arrange
        var configuration = CreateConfigurationWithDisabledSubcomponents();
        await using var source = CreateClientSource(configuration: configuration);
        await using var sessionManager = CreateSessionManager(source, configuration);
        MarkSdkReconnectPending(sessionManager, source.ReconnectionMetrics);

        // Act
        await sessionManager.DisposeAsync();

        // Assert
        Assert.Equal(1, source.ReconnectionMetrics.TotalAttempts);
        Assert.Equal(0, source.ReconnectionMetrics.Successful);
        Assert.Equal(0, source.ReconnectionMetrics.Failed);
        Assert.Equal(1, source.ReconnectionMetrics.Abandoned);
        Assert.Equal(
            source.ReconnectionMetrics.TotalAttempts,
            source.ReconnectionMetrics.Successful +
            source.ReconnectionMetrics.Failed +
            source.ReconnectionMetrics.Abandoned);
    }

    [Fact]
    public async Task WhenSdkReconnectCompletesAfterDisposal_ThenAttemptIsNotClassifiedAgain()
    {
        // Arrange
        var configuration = CreateConfigurationWithDisabledSubcomponents();
        await using var source = CreateClientSource(configuration: configuration);
        await using var sessionManager = CreateSessionManager(source, configuration);
        MarkSdkReconnectPending(sessionManager, source.ReconnectionMetrics);
        var reconnectHandler = GetReconnectHandler(sessionManager);
        await sessionManager.DisposeAsync();

        // Act
        InvokeReconnectComplete(sessionManager, reconnectHandler);

        // Assert
        Assert.Equal(1, source.ReconnectionMetrics.TotalAttempts);
        Assert.Equal(0, source.ReconnectionMetrics.Successful);
        Assert.Equal(0, source.ReconnectionMetrics.Failed);
        Assert.Equal(1, source.ReconnectionMetrics.Abandoned);
        Assert.Equal(
            source.ReconnectionMetrics.TotalAttempts,
            source.ReconnectionMetrics.Successful +
            source.ReconnectionMetrics.Failed +
            source.ReconnectionMetrics.Abandoned);
    }

    private static OpcUaClientConfiguration CreateConfigurationWithDisabledSubcomponents()
    {
        var configuration = CreateConfiguration();
        configuration.EnablePollingFallback = false;
        configuration.EnableReadAfterWrite = false;
        return configuration;
    }

    private static SessionManager CreateSessionManager(
        OpcUaSubjectClientSource source,
        OpcUaClientConfiguration configuration)
    {
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        return new SessionManager(
            source,
            propertyWriter,
            configuration,
            source.PollingMetrics,
            source.ReadAfterWriteMetrics,
            NullLogger.Instance);
    }

    private static void MarkSdkReconnectPending(SessionManager sessionManager, ReconnectionMetrics metrics)
    {
        // Mirrors the two accounting writes OnKeepAlive makes after the SDK accepts a reconnect,
        // without involving a network connection or timer.
        metrics.RecordAttemptStart();
        GetField("_pendingSdkReconnection").SetValue(sessionManager, 1);
    }

    private static SessionReconnectHandler GetReconnectHandler(SessionManager sessionManager) =>
        Assert.IsType<SessionReconnectHandler>(GetField("_reconnectHandler").GetValue(sessionManager));

    private static void InvokeReconnectComplete(SessionManager sessionManager, SessionReconnectHandler reconnectHandler) =>
        typeof(SessionManager)
            .GetMethod("OnReconnectComplete", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sessionManager, [reconnectHandler, EventArgs.Empty]);

    private static FieldInfo GetField(string name) =>
        typeof(SessionManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
}
