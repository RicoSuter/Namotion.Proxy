using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mqtt.Tests.Client;

/// <summary>
/// Pins that the claimed-property gauge is pointed at the client's own ownership manager, and that a
/// client which has never connected says so. The liveness transitions need a broker and are covered
/// by <see cref="MqttClientLivenessTests"/>.
/// </summary>
[Collection(MqttNetworkIntegrationCollection.Name)]
public class MqttClientDiagnosticsTests
{
    /// <summary>
    /// A compile-level pin of the member tree plus the defaults a fresh <c>SourceMetrics</c> reports.
    /// Its value is that the two throughput rates stay <c>null</c> rather than being wired to a
    /// counter this connector does not feed, which would report a misleading zero.
    /// </summary>
    [Fact]
    public async Task WhenNeverConnected_ThenTheSourceReportsUnavailableLivenessAndNoThroughput()
    {
        // Arrange
        await using var source = CreateClientSource();

        // Act
        var diagnostics = source.Diagnostics;

        // Assert
        Assert.Null(diagnostics.IsOperational);
        Assert.Null(diagnostics.OperationalChangeTime);
        Assert.Null(diagnostics.StartTime);
        Assert.Null(diagnostics.LastError);
        Assert.Equal(0, diagnostics.ClaimedPropertyCount);

        // Null rather than 0: the client measures neither direction.
        Assert.Null(diagnostics.Throughput.IncomingPerSecond);
        Assert.Null(diagnostics.Throughput.OutgoingPerSecond);
    }

    [Fact]
    public async Task WhenPropertiesAreClaimed_ThenClaimedPropertyCountFollowsTheOwnershipManager()
    {
        // Arrange
        await using var source = CreateClientSource();
        var property = ((DeliveryRuleTestRoot)source.RootSubject)
            .GetPropertyReference(nameof(DeliveryRuleTestRoot.Name));

        // Act
        var claimed = source.Ownership.ClaimSource(property);
        var whileClaimed = source.Diagnostics.ClaimedPropertyCount;
        source.Ownership.ReleaseSource(property);
        var afterRelease = source.Diagnostics.ClaimedPropertyCount;

        // Assert
        Assert.True(claimed);
        Assert.Equal(1, whileClaimed);
        Assert.Equal(0, afterRelease);
    }

    [Fact]
    public async Task WhenDisposedWhileHostedExecutionIsActive_ThenCleanupWaitsForExecutionToExit()
    {
        // Arrange
        await using var source = CreateClientSource();
        var property = ((DeliveryRuleTestRoot)source.RootSubject)
            .GetPropertyReference(nameof(DeliveryRuleTestRoot.Name));
        Assert.True(source.Ownership.ClaimSource(property));

        await using var executionGate = HostedExecutionGate.Install(source);
        await executionGate.Started.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        var disposal = source.DisposeAsync().AsTask();
        try
        {
            await executionGate.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.False(disposal.IsCompleted);
            Assert.Equal(1, source.Ownership.Count);
        }
        finally
        {
            executionGate.AllowExit();
            await disposal;
        }

        Assert.Equal(0, source.Ownership.Count);
        Assert.Null(source.Diagnostics.LastError);
    }

    /// <summary>
    /// The connection monitor runs inside the listen lifetime, outside the try in
    /// <c>SubjectSourceBase.RunAsync</c> that records per-attempt failures, so the monitor has to
    /// report these itself.
    /// </summary>
    [Trait("Category", "Integration")]
    [Fact]
    public async Task WhenTheBrokerStaysDownAfterAConnection_ThenTheFailedReconnectReachesLastError()
    {
        // Arrange - connected first, so the failure under test is the reconnect rather than the
        // initial connect, which the base class would report on its own.
        var brokerPort = GetFreeTcpPort();
        await using var broker = CreateBroker(brokerPort);
        await using var source = CreateClientSource(brokerPort);

        await broker.StartAsync(CancellationToken.None);
        await source.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == true,
                message: "The client should report operational once it has connected to the broker.");
            Assert.Null(source.Diagnostics.LastError);

            // Act
            await broker.StopAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.LastError is not null,
                message: "A client that cannot reconnect should report the failure.");
            Assert.False(source.Diagnostics.IsOperational);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    private static MqttSubjectServer CreateBroker(int brokerPort)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

        return new MqttSubjectServer(
            new DeliveryRuleTestRoot(context),
            new MqttServerConfiguration { BrokerPort = brokerPort },
            NullLogger<MqttSubjectServer>.Instance);
    }

    private static MqttSubjectClientSource CreateClientSource(int? brokerPort = null)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

        return new MqttSubjectClientSource(
            new DeliveryRuleTestRoot(context),
            new MqttClientConfiguration
            {
                // The broker binds IPv4 only, so dialling it by name would let the client spend its
                // connect timeout on the IPv6 loopback first.
                BrokerHost = "127.0.0.1",
                BrokerPort = brokerPort ?? 1883,
                ReconnectDelay = TimeSpan.FromMilliseconds(200),
                MaximumReconnectDelay = TimeSpan.FromSeconds(2),
                HealthCheckInterval = TimeSpan.FromSeconds(1)
            },
            NullLogger<MqttSubjectClientSource>.Instance);
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
