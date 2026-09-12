using Microsoft.Extensions.Logging;
using Moq;
using Namotion.Interceptor.Ads.Client;
using TwinCAT;
using TwinCAT.Ads;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests.Client;

public class AdsConnectionManagerTests
{
    private static AdsConnectionManager CreateManager(Mock<ILogger>? mockLogger = null)
    {
        return new AdsConnectionManager(
            TestHelpers.CreateConfiguration(),
            (mockLogger ?? new Mock<ILogger>()).Object);
    }

    [Fact]
    public void Constructor_WithNullConfiguration_ShouldThrow()
    {
        // Arrange
        var logger = new Mock<ILogger>().Object;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new AdsConnectionManager(null!, logger));
    }

    [Fact]
    public void Constructor_WithNullLogger_ShouldThrow()
    {
        // Arrange
        var configuration = TestHelpers.CreateConfiguration();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new AdsConnectionManager(configuration, null!));
    }

    [Fact]
    public void InitialState_AllPropertiesHaveDefaultValues()
    {
        // Arrange & Act
        var manager = CreateManager();

        // Assert
        Assert.NotNull(manager);
        Assert.Null(manager.Connection);
        Assert.Null(manager.SymbolLoader);
        Assert.Null(manager.CurrentAdsState);
        Assert.False(manager.IsConnected);
        Assert.Equal(0, manager.TotalReconnectionAttempts);
        Assert.Equal(0, manager.SuccessfulReconnections);
        Assert.Equal(0, manager.FailedReconnections);
        Assert.Null(manager.LastConnectedAt);
        Assert.False(manager.IsCircuitBreakerOpen);
        Assert.Equal(0, manager.CircuitBreakerTripCount);
    }

    [Fact]
    public async Task RecreateSymbolLoaderAsync_WhenNoSession_ShouldNotThrow()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        await manager.RecreateSymbolLoaderAsync(CancellationToken.None);

        // Assert
        Assert.Null(manager.SymbolLoader);
    }

    [Fact]
    public async Task RecreateSymbolLoaderAsync_WhenNoSession_ShouldKeepSymbolLoaderNull()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        await manager.RecreateSymbolLoaderAsync(CancellationToken.None);
        await manager.RecreateSymbolLoaderAsync(CancellationToken.None);

        // Assert
        Assert.Null(manager.SymbolLoader);
    }

    [Fact]
    public async Task DisposeAsync_ShouldComplete()
    {
        // Arrange
        var manager = CreateManager();

        // Act & Assert
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        // Arrange
        var manager = CreateManager();

        // Act & Assert
        await manager.DisposeAsync();
        await manager.DisposeAsync();
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_PropertiesStillAccessible()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        await manager.DisposeAsync();

        // Assert
        Assert.Null(manager.Connection);
        Assert.Null(manager.SymbolLoader);
        Assert.Null(manager.CurrentAdsState);
        Assert.False(manager.IsConnected);
    }

    [Fact]
    public void LogFirstOccurrence_FirstCall_LogsWarning()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        // Act
        manager.LogFirstOccurrence("TestCategory", null, "Test message");

        // Assert
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogFirstOccurrence_SecondCall_LogsDebug()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        // Act
        manager.LogFirstOccurrence("TestCategory", null, "Test message");
        manager.LogFirstOccurrence("TestCategory", null, "Test message");

        // Assert
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogFirstOccurrence_ThirdCall_StillLogsDebug()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        // Act
        manager.LogFirstOccurrence("TestCategory", null, "Test message");
        manager.LogFirstOccurrence("TestCategory", null, "Test message");
        manager.LogFirstOccurrence("TestCategory", null, "Test message");

        // Assert
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));
    }

    [Fact]
    public void ClearFirstOccurrenceLog_ResetsToWarning()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        // Act
        manager.LogFirstOccurrence("TestCategory", null, "Test message");
        manager.ClearFirstOccurrenceLog("TestCategory");
        manager.LogFirstOccurrence("TestCategory", null, "Test message");

        // Assert - should have 2 warnings (first call + after clear) and 0 debug
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));
    }

    [Fact]
    public void ClearFirstOccurrenceLog_NonExistentCategory_DoesNotThrow()
    {
        // Arrange
        var manager = CreateManager();

        // Act & Assert
        manager.ClearFirstOccurrenceLog("NonExistentCategory");
    }

    [Fact]
    public void LogFirstOccurrence_WithException_FirstCall_LogsWarning()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);
        var exception = new InvalidOperationException("Test error");

        // Act
        manager.LogFirstOccurrence("TestCategory", exception, "Error occurred");

        // Assert
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogFirstOccurrence_WithException_SecondCall_LogsDebug()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);
        var exception1 = new InvalidOperationException("First error");
        var exception2 = new InvalidOperationException("Second error");

        // Act
        manager.LogFirstOccurrence("TestCategory", exception1, "Error occurred");
        manager.LogFirstOccurrence("TestCategory", exception2, "Error occurred");

        // Assert
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                exception1,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                exception2,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogFirstOccurrence_WithException_AfterClear_LogsWarningAgain()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);
        var exception = new InvalidOperationException("Test error");

        // Act
        manager.LogFirstOccurrence("TestCategory", exception, "Error occurred");
        manager.ClearFirstOccurrenceLog("TestCategory");
        manager.LogFirstOccurrence("TestCategory", exception, "Error occurred");

        // Assert - two warnings, zero debug
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));
    }

    [Fact]
    public void LogFirstOccurrence_DifferentCategories_TrackedIndependently()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        // Act
        manager.LogFirstOccurrence("Connection", null, "Connection error");
        manager.LogFirstOccurrence("BatchPoll", null, "Polling error");
        manager.LogFirstOccurrence("HealthCheck", null, "Health check error");

        // Assert - each category gets its own first-occurrence warning
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(3));
    }

    [Fact]
    public void LogFirstOccurrence_DifferentCategories_SecondCallPerCategory_LogsDebug()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        // Act
        manager.LogFirstOccurrence("Connection", null, "Connection error");
        manager.LogFirstOccurrence("BatchPoll", null, "Polling error");
        manager.LogFirstOccurrence("Connection", null, "Connection error");
        manager.LogFirstOccurrence("BatchPoll", null, "Polling error");

        // Assert
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));
    }

    [Fact]
    public void ClearFirstOccurrenceLog_OnlyAffectsSpecifiedCategory()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        // Act
        manager.LogFirstOccurrence("Connection", null, "Connection error");
        manager.LogFirstOccurrence("BatchPoll", null, "Polling error");
        manager.ClearFirstOccurrenceLog("Connection");
        manager.LogFirstOccurrence("Connection", null, "Connection error");
        manager.LogFirstOccurrence("BatchPoll", null, "Polling error");

        // Assert - 3 warnings: Connection(1st), BatchPoll(1st), Connection(after clear)
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(3));

        // 1 debug: BatchPoll(2nd)
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ConnectWithRetryAsync_WhenCancelledImmediately_ShouldReturn()
    {
        // Arrange
        var manager = CreateManager();
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        // Act
        await manager.ConnectWithRetryAsync(cancellationTokenSource.Token);

        // Assert
        Assert.Equal(0, manager.TotalReconnectionAttempts);
    }

    [Fact]
    public void OnConnectionStateChanged_ToConnected_FiresConnectionRestored()
    {
        // Arrange
        var manager = CreateManager();
        var fired = false;
        manager.ConnectionRestored += () => fired = true;
        var eventArgs = new ConnectionStateChangedEventArgs(
            ConnectionStateChangedReason.Established,
            ConnectionState.Connected,
            ConnectionState.Disconnected);

        // Act
        manager.OnConnectionStateChanged(null, eventArgs);

        // Assert
        Assert.True(fired);
    }

    [Fact]
    public void OnConnectionStateChanged_ToConnected_IncrementsSuccessfulReconnections()
    {
        // Arrange
        var manager = CreateManager();
        var eventArgs = new ConnectionStateChangedEventArgs(
            ConnectionStateChangedReason.Established,
            ConnectionState.Connected,
            ConnectionState.Disconnected);

        // Act
        manager.OnConnectionStateChanged(null, eventArgs);

        // Assert
        Assert.Equal(1, manager.SuccessfulReconnections);
    }

    [Fact]
    public void OnConnectionStateChanged_ToDisconnected_FiresConnectionLost()
    {
        // Arrange
        var manager = CreateManager();
        var fired = false;
        manager.ConnectionLost += () => fired = true;
        var eventArgs = new ConnectionStateChangedEventArgs(
            ConnectionStateChangedReason.Lost,
            ConnectionState.Disconnected,
            ConnectionState.Connected);

        // Act
        manager.OnConnectionStateChanged(null, eventArgs);

        // Assert
        Assert.True(fired);
    }

    [Fact]
    public void OnConnectionStateChanged_ToDisconnected_ClearsConnectionLog()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        // Log a connection error first (uses Warning)
        manager.LogFirstOccurrence("Connection", null, "Error");

        var eventArgs = new ConnectionStateChangedEventArgs(
            ConnectionStateChangedReason.Lost,
            ConnectionState.Disconnected,
            ConnectionState.Connected);

        // Act
        manager.OnConnectionStateChanged(null, eventArgs);

        // Log again after disconnect cleared the log - should be Warning again
        manager.LogFirstOccurrence("Connection", null, "Error");

        // Assert - two warnings (first + after clear), zero debug
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));
    }

    [Fact]
    public void OnConnectionStateChanged_ConnectedToConnected_DoesNotFireConnectionRestored()
    {
        // Arrange
        var manager = CreateManager();
        var fired = false;
        manager.ConnectionRestored += () => fired = true;
        var eventArgs = new ConnectionStateChangedEventArgs(
            ConnectionStateChangedReason.Established,
            ConnectionState.Connected,
            ConnectionState.Connected);

        // Act
        manager.OnConnectionStateChanged(null, eventArgs);

        // Assert - no transition, so event should not fire
        Assert.False(fired);
    }

    [Fact]
    public void OnAdsStateChanged_ToRun_FiresAdsStateEnteredRun()
    {
        // Arrange
        var manager = CreateManager();
        var fired = false;
        manager.AdsStateEnteredRun += () => fired = true;
        var stateInfo = new StateInfo(AdsState.Run, 0);
        var eventArgs = new AdsStateChangedEventArgs(stateInfo);

        // Act
        manager.OnAdsStateChanged(null, eventArgs);

        // Assert
        Assert.True(fired);
        Assert.Equal(AdsState.Run, manager.CurrentAdsState);
    }

    [Fact]
    public void OnAdsStateChanged_FromRunToRun_DoesNotFireAdsStateEnteredRun()
    {
        // Arrange
        var manager = CreateManager();

        // First transition to Run
        manager.OnAdsStateChanged(null, new AdsStateChangedEventArgs(new StateInfo(AdsState.Run, 0)));

        var fired = false;
        manager.AdsStateEnteredRun += () => fired = true;

        // Act - Run to Run transition
        manager.OnAdsStateChanged(null, new AdsStateChangedEventArgs(new StateInfo(AdsState.Run, 0)));

        // Assert
        Assert.False(fired);
    }

    [Fact]
    public void OnAdsStateChanged_ToStop_UpdatesCurrentAdsState()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        manager.OnAdsStateChanged(null, new AdsStateChangedEventArgs(new StateInfo(AdsState.Stop, 0)));

        // Assert
        Assert.Equal(AdsState.Stop, manager.CurrentAdsState);
    }

    [Fact]
    public void OnSymbolVersionChanged_FiresSymbolVersionChanged()
    {
        // Arrange
        var manager = CreateManager();
        var fired = false;
        manager.SymbolVersionChanged += () => fired = true;

        // Act
        manager.OnSymbolVersionChanged(null, new AdsSymbolVersionChangedEventArgs(1));

        // Assert
        Assert.True(fired);
    }
}
