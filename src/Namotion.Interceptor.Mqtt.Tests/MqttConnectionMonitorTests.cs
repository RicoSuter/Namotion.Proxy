using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MQTTnet;
using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests;

/// <summary>
/// Tests for <see cref="MqttConnectionMonitor"/> resilience behaviors:
/// health checks, reconnection, circuit breaker, exponential backoff, and stale signal handling.
/// </summary>
/// <remarks>
/// TryPingAsync is an extension method (not mockable). It wraps PingAsync in a try/catch:
/// PingAsync succeeds → TryPingAsync returns true; PingAsync throws → TryPingAsync returns false.
/// All tests mock PingAsync accordingly.
///
/// Every test drives the monitoring loop until the mock observes the behavior under test and then cancels,
/// so no test paces itself with a wall clock. The cancellation sources are fail-fast watchdogs: they are
/// reached only when the monitor never produces the expected observation, and a passing test never waits
/// for them.
/// </remarks>
public class MqttConnectionMonitorTests
{
    /// <summary>Budget for an observation the monitor produces within milliseconds. Reached only on a genuine failure.</summary>
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Hard stop for the monitoring loop so a hang fails the run instead of blocking the test host.</summary>
    private static readonly TimeSpan MonitorWatchdogTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Poll interval for observations. The monitor reacts within a few milliseconds when it is configured that way.</summary>
    private static readonly TimeSpan ObservationPollInterval = TimeSpan.FromMilliseconds(2);

    /// <summary>Log fragment for the stale-signal branch, asserted present in one test and absent in another.</summary>
    private const string StaleSignalIgnoredMessage = "Stale disconnect signal ignored";

    private static MqttClientConfiguration CreateConfiguration(
        TimeSpan? healthCheckInterval = null,
        TimeSpan? reconnectDelay = null,
        TimeSpan? maximumReconnectDelay = null,
        int circuitBreakerFailureThreshold = 0,
        TimeSpan? circuitBreakerCooldown = null,
        int reconnectStallThreshold = 0)
    {
        return new MqttClientConfiguration
        {
            BrokerHost = "localhost",
            Mapper = new MqttPathProviderMapper(new AttributeBasedPathProvider("test", '/')),
            HealthCheckInterval = healthCheckInterval ?? TimeSpan.FromMilliseconds(5),
            ReconnectDelay = reconnectDelay ?? TimeSpan.FromMilliseconds(5),
            MaximumReconnectDelay = maximumReconnectDelay ?? TimeSpan.FromSeconds(1),
            CircuitBreakerFailureThreshold = circuitBreakerFailureThreshold,
            CircuitBreakerCooldown = circuitBreakerCooldown ?? TimeSpan.FromMilliseconds(500),
            ReconnectStallThreshold = reconnectStallThreshold,
        };
    }

    private static MqttClientOptions CreateOptions() => new MqttClientOptionsBuilder()
        .WithTcpServer("localhost")
        .Build();

    /// <summary>Helper: configure mock so PingAsync succeeds (→ TryPingAsync returns true).</summary>
    private static void SetupPingHealthy(Mock<IMqttClient> client, Action? onPing = null)
    {
        client.Setup(c => c.PingAsync(It.IsAny<CancellationToken>()))
            .Callback(() => onPing?.Invoke())
            .Returns(Task.CompletedTask);
    }

    /// <summary>Helper: configure mock so PingAsync throws (→ TryPingAsync returns false).</summary>
    private static void SetupPingUnhealthy(Mock<IMqttClient> client, Action? onPing = null)
    {
        client.Setup(c => c.PingAsync(It.IsAny<CancellationToken>()))
            .Callback(() => onPing?.Invoke())
            .ThrowsAsync(new Exception("Ping failed"));
    }

    /// <summary>
    /// Runs the monitoring loop until the observation holds, then cancels it and waits for it to unwind so that
    /// every mock callback has completed before the caller reads its counters.
    /// </summary>
    private static async Task RunMonitorUntilAsync(
        MqttConnectionMonitor monitor,
        Func<bool> observation,
        string observationDescription)
    {
        using var cancellation = new CancellationTokenSource();
        var monitorTask = monitor.MonitorConnectionAsync(cancellation.Token);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                observation, ObservationTimeout, ObservationPollInterval, observationDescription);
        }
        finally
        {
            // Unwind even when the observation never landed, otherwise the loop keeps running against a
            // cancellation source that this method is about to dispose.
            await cancellation.CancelAsync();
            try
            {
                // Bounded join: a monitor that ignores its token fails the test at the watchdog
                // instead of hanging the class.
                await monitorTask.WaitAsync(MonitorWatchdogTimeout);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task HealthyConnection_DoesNotTriggerReconnection()
    {
        // Arrange
        var client = new Mock<IMqttClient>();
        client.Setup(c => c.IsConnected).Returns(true);

        var healthCheckCount = 0;
        SetupPingHealthy(client, () => Interlocked.Increment(ref healthCheckCount));

        var reconnectedCount = 0;
        var disconnectedCount = 0;

        var monitor = new MqttConnectionMonitor(
            client.Object,
            CreateConfiguration(),
            CreateOptions,
            onReconnected: _ => { Interlocked.Increment(ref reconnectedCount); return Task.CompletedTask; },
            onDisconnected: () => { Interlocked.Increment(ref disconnectedCount); return Task.CompletedTask; },
            onError: _ => { },
            NullLogger.Instance);

        // Act: let the monitor complete several periodic health checks
        await RunMonitorUntilAsync(
            monitor,
            () => Volatile.Read(ref healthCheckCount) >= 3,
            "monitor did not complete three health checks");

        // Assert: healthy connection should never trigger reconnection or disconnect handlers
        Assert.Equal(0, reconnectedCount);
        Assert.Equal(0, disconnectedCount);
    }

    [Fact]
    public async Task HealthCheckFailure_TriggersReconnection()
    {
        // Arrange: client starts disconnected, reconnects after ConnectAsync
        var client = new Mock<IMqttClient>();
        client.Setup(c => c.IsConnected).Returns(false);
        SetupPingUnhealthy(client);
        client.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                client.Setup(c => c.IsConnected).Returns(true);
                SetupPingHealthy(client);
            })
            .ReturnsAsync(new MqttClientConnectResult());

        var reconnectedCount = 0;

        var monitor = new MqttConnectionMonitor(
            client.Object,
            CreateConfiguration(),
            CreateOptions,
            onReconnected: _ => { Interlocked.Increment(ref reconnectedCount); return Task.CompletedTask; },
            onDisconnected: () => Task.CompletedTask,
            onError: _ => { },
            NullLogger.Instance);

        // Act
        await RunMonitorUntilAsync(
            monitor,
            () => Volatile.Read(ref reconnectedCount) >= 1,
            "failed health check did not lead to a reconnection");

        // Assert
        Assert.True(reconnectedCount >= 1, $"Expected at least 1 reconnection but got {reconnectedCount}");
        client.Verify(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task WhenPeriodicPingFailsWhileClientReportsConnected_ThenTransportIsTerminatedAndReconnected()
    {
        // Arrange
        var client = new Mock<IMqttClient>();
        var isConnected = true;
        var isOperational = true;
        var isBuffering = false;
        var transportTerminated = false;
        var reconnectAttempted = false;

        client.Setup(c => c.IsConnected).Returns(() => isConnected);
        SetupPingUnhealthy(client);
        client
            .Setup(c => c.DisconnectAsync(
                It.IsAny<MqttClientDisconnectOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                transportTerminated = true;
                isConnected = false;
            })
            .Returns(Task.CompletedTask);
        client
            .Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                reconnectAttempted = true;
                isConnected = true;
                SetupPingHealthy(client);
            })
            .ReturnsAsync(new MqttClientConnectResult());

        var reconnectedCount = 0;

        var monitor = new MqttConnectionMonitor(
            client.Object,
            CreateConfiguration(
                healthCheckInterval: TimeSpan.FromMilliseconds(10),
                reconnectDelay: TimeSpan.Zero),
            CreateOptions,
            onReconnected: _ => { Interlocked.Increment(ref reconnectedCount); return Task.CompletedTask; },
            onDisconnected: () =>
            {
                isOperational = false;
                isBuffering = true;
                return Task.CompletedTask;
            },
            onError: _ => { },
            NullLogger.Instance);

        // Act
        await RunMonitorUntilAsync(
            monitor,
            () => Volatile.Read(ref reconnectedCount) >= 1,
            "failed ping on a connected client did not lead to a reconnection");

        // Assert
        Assert.False(isOperational);
        Assert.True(isBuffering);
        Assert.True(transportTerminated);
        Assert.True(reconnectAttempted);
    }

    [Fact]
    public async Task WhenTransportTerminationFailsDuringShutdown_ThenTheFailureIsNotReported()
    {
        // Arrange: the transport termination cancels the monitor and then fails, which is what a shutdown looks like
        var client = new Mock<IMqttClient>();
        using var watchdog = new CancellationTokenSource(MonitorWatchdogTimeout);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
        var errorCount = 0;
        var transportTerminationAttempted = false;

        client.Setup(c => c.IsConnected).Returns(true);
        SetupPingUnhealthy(client);
        client
            .Setup(c => c.DisconnectAsync(
                It.IsAny<MqttClientDisconnectOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                transportTerminationAttempted = true;
                cancellation.Cancel();
                return Task.FromException(new InvalidOperationException("transport stopped"));
            });

        var monitor = new MqttConnectionMonitor(
            client.Object,
            // Long health check interval so the pending signal, not the periodic check, drives the single iteration
            CreateConfiguration(healthCheckInterval: TimeSpan.FromSeconds(30)),
            CreateOptions,
            onReconnected: _ => Task.CompletedTask,
            onDisconnected: () => Task.CompletedTask,
            onError: _ => Interlocked.Increment(ref errorCount),
            NullLogger.Instance);
        monitor.SignalReconnectNeeded();

        // Act
        try
        {
            await monitor.MonitorConnectionAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        // Assert
        Assert.True(transportTerminationAttempted, "monitor never attempted to terminate the unhealthy transport");
        Assert.Equal(0, errorCount);
    }

    [Fact]
    public async Task DisconnectSignal_TriggersReconnection()
    {
        // Arrange
        var client = new Mock<IMqttClient>();
        var connectCallCount = 0;

        // Start disconnected, become connected after first ConnectAsync
        client.Setup(c => c.IsConnected).Returns(false);
        SetupPingUnhealthy(client);
        client.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                Interlocked.Increment(ref connectCallCount);
                client.Setup(c => c.IsConnected).Returns(true);
                SetupPingHealthy(client);
            })
            .ReturnsAsync(new MqttClientConnectResult());

        var disconnectedCalled = false;

        var monitor = new MqttConnectionMonitor(
            client.Object,
            // Long health check interval so the signal (not the periodic check) triggers reconnection
            CreateConfiguration(healthCheckInterval: TimeSpan.FromSeconds(30)),
            CreateOptions,
            onReconnected: _ => Task.CompletedTask,
            onDisconnected: () => { Volatile.Write(ref disconnectedCalled, true); return Task.CompletedTask; },
            onError: _ => { },
            NullLogger.Instance);

        // The signal is a SemaphoreSlim(0, 1) that the monitoring loop waits on as its first statement, so
        // releasing it before the loop starts is equivalent to releasing it once the loop is parked there.
        monitor.SignalReconnectNeeded();

        // Act
        await RunMonitorUntilAsync(
            monitor,
            () => Volatile.Read(ref disconnectedCalled) && Volatile.Read(ref connectCallCount) >= 1,
            "disconnect signal did not lead to a reconnection");

        // Assert
        Assert.True(disconnectedCalled, "Expected onDisconnected to be called");
        Assert.True(connectCallCount >= 1, $"Expected at least 1 connect call but got {connectCallCount}");
    }

    [Fact]
    public async Task StaleDisconnectSignal_IgnoredWhenClientHealthy()
    {
        // Arrange: client is connected and ping succeeds
        var client = new Mock<IMqttClient>();
        client.Setup(c => c.IsConnected).Returns(true);

        // The long health check interval means the only ping in this test is the staleness verification of
        // the signal, so a completed ping proves the monitor processed the signal.
        var stalenessVerificationCount = 0;
        SetupPingHealthy(client, () => Interlocked.Increment(ref stalenessVerificationCount));

        var disconnectedCount = 0;

        // The sibling test asserts this log fragment is absent, which rots into a vacuous pass if the
        // message is reworded. Recording it here, where the branch actually fires, makes a reword fail loudly.
        var logger = new RecordingLogger();

        var monitor = new MqttConnectionMonitor(
            client.Object,
            // Long health check interval so the signal (not the periodic check) is what gets processed
            CreateConfiguration(healthCheckInterval: TimeSpan.FromSeconds(30)),
            CreateOptions,
            onReconnected: _ => Task.CompletedTask,
            onDisconnected: () => { Interlocked.Increment(ref disconnectedCount); return Task.CompletedTask; },
            onError: _ => { },
            logger);

        // This is a "stale" signal because the client is actually healthy
        monitor.SignalReconnectNeeded();

        // Act: run until the signal has been received and verified via ping
        await RunMonitorUntilAsync(
            monitor,
            () => Volatile.Read(ref stalenessVerificationCount) >= 1,
            "monitor did not verify the disconnect signal with a ping");

        // Assert: disconnect handler should NOT be called because ping confirmed healthy
        Assert.True(logger.ContainsMessage(StaleSignalIgnoredMessage),
            "the stale signal was not reported as ignored");
        Assert.Equal(0, disconnectedCount);
        client.Verify(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CircuitBreaker_TripsAfterThresholdFailures()
    {
        // Arrange: ConnectAsync always fails
        var client = new Mock<IMqttClient>();
        client.Setup(c => c.IsConnected).Returns(false);
        SetupPingUnhealthy(client);

        var failureCount = 0;
        client.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref failureCount))
            .ThrowsAsync(new InvalidOperationException("Connection refused"));

        // An open circuit breaker blocks the attempt before it reaches the client, so the log is the only
        // place where the block becomes observable from outside the monitor.
        var logger = new RecordingLogger();

        var monitor = new MqttConnectionMonitor(
            client.Object,
            CreateConfiguration(
                circuitBreakerFailureThreshold: 3,
                // Long cooldown: once tripped, no more retries within the test window
                circuitBreakerCooldown: TimeSpan.FromSeconds(60)),
            CreateOptions,
            onReconnected: _ => Task.CompletedTask,
            onDisconnected: () => Task.CompletedTask,
            onError: _ => { },
            logger);

        // Act: run until the breaker refuses an attempt
        await RunMonitorUntilAsync(
            monitor,
            () => logger.ContainsMessage("Circuit breaker open"),
            "circuit breaker never blocked a reconnect attempt");

        // Assert: the threshold number of attempts reaches the client, everything after it is blocked
        Assert.Equal(3, failureCount);
    }

    [Fact]
    public async Task ExponentialBackoff_LimitsRetryRate()
    {
        // Arrange: ConnectAsync always fails, so the delay between attempts is observable through their timestamps
        var client = new Mock<IMqttClient>();
        client.Setup(c => c.IsConnected).Returns(false);
        SetupPingUnhealthy(client);

        var attemptTimestamps = new ConcurrentQueue<long>();
        client.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => attemptTimestamps.Enqueue(Stopwatch.GetTimestamp()))
            .ThrowsAsync(new Exception("Connection refused"));

        var monitor = new MqttConnectionMonitor(
            client.Object,
            CreateConfiguration(
                reconnectDelay: TimeSpan.FromMilliseconds(5),
                maximumReconnectDelay: TimeSpan.FromSeconds(1)),
            CreateOptions,
            onReconnected: _ => Task.CompletedTask,
            onDisconnected: () => Task.CompletedTask,
            onError: _ => { },
            NullLogger.Instance);

        // Act
        await RunMonitorUntilAsync(
            monitor,
            () => attemptTimestamps.Count >= 5,
            "monitor did not make five reconnect attempts");

        // Assert: with the 5ms base delay doubling per failure, attempts 2 to 5 wait 10 + 20 + 40 + 80 = 150ms
        // in total, against 20ms for a fixed delay. This is a lower bound on elapsed time and a delay never
        // expires early, so a slow agent can only push the measurement further above the bound.
        var timestamps = attemptTimestamps.ToArray();
        var elapsedOverFourBackoffs = Stopwatch.GetElapsedTime(timestamps[0], timestamps[4]);
        Assert.True(elapsedOverFourBackoffs >= TimeSpan.FromMilliseconds(100),
            $"Expected exponential backoff to spread five attempts over at least 100ms but they took {elapsedOverFourBackoffs.TotalMilliseconds:F1}ms");
    }

    [Fact]
    public async Task ReconnectSuccess_DrainsStaleSignals()
    {
        // Arrange
        var client = new Mock<IMqttClient>();
        MqttConnectionMonitor? monitorRef = null;
        var healthCheckAfterReconnectCount = 0;

        // First health check: disconnected. After reconnect: connected, so every healthy ping is a
        // health check that follows the reconnection.
        client.Setup(c => c.IsConnected).Returns(false);
        SetupPingUnhealthy(client);
        client.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                client.Setup(c => c.IsConnected).Returns(true);
                SetupPingHealthy(client, () => Interlocked.Increment(ref healthCheckAfterReconnectCount));
            })
            .ReturnsAsync(new MqttClientConnectResult());

        var disconnectedCount = 0;

        // A drained signal and a signal that survived the drain both end in a ping against a healthy client,
        // so the debug log is the only place where the difference becomes observable.
        var logger = new RecordingLogger();

        var monitor = new MqttConnectionMonitor(
            client.Object,
            CreateConfiguration(),
            CreateOptions,
            onReconnected: _ =>
            {
                // Simulate: a stale disconnect signal arrives right after reconnection.
                // The monitor should drain this signal immediately after onReconnected returns.
                monitorRef?.SignalReconnectNeeded();
                return Task.CompletedTask;
            },
            onDisconnected: () => { Interlocked.Increment(ref disconnectedCount); return Task.CompletedTask; },
            onError: _ => { },
            logger);

        monitorRef = monitor;

        // Act: run until the monitoring loop has taken its first decision after the reconnection
        await RunMonitorUntilAsync(
            monitor,
            () => Volatile.Read(ref healthCheckAfterReconnectCount) >= 1,
            "monitor did not complete a health check after reconnecting");

        // Assert: the stale signal was drained, so the loop went back to periodic health checks instead of
        // processing a signal it had raised itself, and the client never saw a second disconnect.
        Assert.False(logger.ContainsMessage(StaleSignalIgnoredMessage),
            "the disconnect signal raised during reconnection was not drained");
        Assert.Equal(1, disconnectedCount);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        // Arrange
        var client = new Mock<IMqttClient>();
        var monitor = new MqttConnectionMonitor(
            client.Object,
            CreateConfiguration(),
            CreateOptions,
            onReconnected: _ => Task.CompletedTask,
            onDisconnected: () => Task.CompletedTask,
            onError: _ => { },
            NullLogger.Instance);

        // Act & Assert: no exception on double dispose
        await monitor.DisposeAsync();
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task SignalReconnectNeeded_AfterDispose_DoesNotThrow()
    {
        // Arrange
        var client = new Mock<IMqttClient>();
        var monitor = new MqttConnectionMonitor(
            client.Object,
            CreateConfiguration(),
            CreateOptions,
            onReconnected: _ => Task.CompletedTask,
            onDisconnected: () => Task.CompletedTask,
            onError: _ => { },
            NullLogger.Instance);

        await monitor.DisposeAsync();

        // Act & Assert: no exception
        monitor.SignalReconnectNeeded();
    }

    [Fact]
    public void Constructor_ThrowsOnNullArguments()
    {
        var client = new Mock<IMqttClient>();
        var configuration = CreateConfiguration();
        Func<MqttClientOptions> optionsBuilder = CreateOptions;
        Func<CancellationToken, Task> onReconnected = _ => Task.CompletedTask;
        Func<Task> onDisconnected = () => Task.CompletedTask;
        Action<Exception> onError = _ => { };

        Assert.Throws<ArgumentNullException>(() => new MqttConnectionMonitor(
            null!, configuration, optionsBuilder, onReconnected, onDisconnected, onError, NullLogger.Instance));

        Assert.Throws<ArgumentNullException>(() => new MqttConnectionMonitor(
            client.Object, null!, optionsBuilder, onReconnected, onDisconnected, onError, NullLogger.Instance));

        Assert.Throws<ArgumentNullException>(() => new MqttConnectionMonitor(
            client.Object, configuration, null!, onReconnected, onDisconnected, onError, NullLogger.Instance));

        Assert.Throws<ArgumentNullException>(() => new MqttConnectionMonitor(
            client.Object, configuration, optionsBuilder, null!, onDisconnected, onError, NullLogger.Instance));

        Assert.Throws<ArgumentNullException>(() => new MqttConnectionMonitor(
            client.Object, configuration, optionsBuilder, onReconnected, null!, onError, NullLogger.Instance));

        Assert.Throws<ArgumentNullException>(() => new MqttConnectionMonitor(
            client.Object, configuration, optionsBuilder, onReconnected, onDisconnected, null!, NullLogger.Instance));

        Assert.Throws<ArgumentNullException>(() => new MqttConnectionMonitor(
            client.Object, configuration, optionsBuilder, onReconnected, onDisconnected, onError, null!));
    }

    /// <summary>
    /// Captures log messages so a test can observe a monitor decision that leaves no trace on the mocked client.
    /// </summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _messages.Enqueue(formatter(state, exception));
        }

        public bool ContainsMessage(string fragment)
            => _messages.Any(message => message.Contains(fragment, StringComparison.Ordinal));
    }
}
