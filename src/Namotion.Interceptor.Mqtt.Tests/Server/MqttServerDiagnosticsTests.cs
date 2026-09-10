using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mqtt.Tests.Server;

/// <summary>
/// Pins that the diagnostics read the metrics the connector itself writes to, and that a broker which
/// has never listened says so. The liveness transitions need a bound port and are covered by
/// <see cref="MqttServerLivenessTests"/>.
/// </summary>
public class MqttServerDiagnosticsTests
{
    [Fact]
    public async Task WhenNeverStarted_ThenTheServerReportsUnavailableLivenessAndNoThroughput()
    {
        // Arrange
        await using var server = CreateServer(new MqttServerConfiguration());

        // Act
        var diagnostics = server.Diagnostics;

        // Assert
        Assert.Null(diagnostics.IsOperational);
        Assert.Null(diagnostics.OperationalChangeTime);
        Assert.Null(diagnostics.StartTime);
        Assert.Null(diagnostics.LastError);
        Assert.Equal(0, diagnostics.ConnectedClientCount);

        // Null rather than 0: the broker measures neither direction.
        Assert.Null(diagnostics.Throughput.IncomingPerSecond);
        Assert.Null(diagnostics.Throughput.OutgoingPerSecond);
    }

    [Fact]
    public async Task WhenTheBindAddressCannotBeParsed_ThenTheFailureReachesTheServerDiagnostics()
    {
        // Arrange: the address is parsed inside the pump, before the broker binds anything.
        await using var server = CreateServer(new MqttServerConfiguration { BrokerHost = "not-an-ip-address" });

        // Act
        await Assert.ThrowsAsync<FormatException>(() => server.StartAsync(CancellationToken.None));

        // Assert
        Assert.IsType<FormatException>(server.Diagnostics.LastError);
        Assert.NotNull(server.Diagnostics.StartTime);
        Assert.False(server.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenDisposedWhileHostedExecutionIsActive_ThenCleanupWaitsForExecutionToExit()
    {
        // Arrange
        await using var server = CreateServer(new MqttServerConfiguration());
        var publishSemaphore = (SemaphoreSlim)typeof(MqttSubjectServer)
            .GetField("_publishSemaphore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(server)!;

        await using var executionGate = HostedExecutionGate.Install(server);
        await executionGate.Started.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        var disposal = server.DisposeAsync().AsTask();
        try
        {
            await executionGate.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.False(disposal.IsCompleted);
            Assert.True(publishSemaphore.Wait(0));
            publishSemaphore.Release();
        }
        finally
        {
            executionGate.AllowExit();
            await disposal;
        }
        Assert.Null(server.Diagnostics.LastError);
    }

    private static MqttSubjectServer CreateServer(MqttServerConfiguration configuration)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        return new MqttSubjectServer(
            new DeliveryRuleTestRoot(context), configuration, NullLogger<MqttSubjectServer>.Instance);
    }
}
