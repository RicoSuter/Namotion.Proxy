using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests;

/// <summary>
/// Verifies that the client source reports an outage rather than staying Synchronized while
/// disconnected. Unlike OPC UA, MQTT already buffers at loss detection (see
/// MqttSubjectClientSource.StartListeningAsync's onDisconnected callback), so this test is a
/// regression guard, not a bug reproduction: it exists to stop a future connector change from
/// silently reintroducing the OPC UA defect this feature closes.
/// </summary>
/// <remarks>
/// There is no existing round-trip integration fixture in this project to copy (the other tests
/// here are mock- or mapper-level). This test builds the smallest one that exercises a real
/// broker: MqttSubjectServer hosts the embedded broker, MqttSubjectClientSource connects to it,
/// both constructed directly (not through DI) so the client can be held as its concrete type
/// for IFaultInjectable and ISubjectSource.StateChanged.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(MqttNetworkIntegrationCollection.Name)]
public partial class OutageStateTests
{
    [InterceptorSubject]
    public partial class TestRoot
    {
        [Path("mqtt", "Name")]
        public partial string Name { get; set; }

        public TestRoot()
        {
            Name = "";
        }
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task WhenTheConnectionIsLost_ThenTheSourceReportsSynchronizingUntilItRecovers()
    {
        // Arrange
        var brokerPort = GetFreeTcpPort();
        var mapper = new MqttCompositeMapper(
            new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),
            new MqttAttributeMapper("mqtt"));

        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();
        var serverRoot = new TestRoot(serverContext) { Name = "Initial" };

        await using var server = new MqttSubjectServer(
            serverRoot,
            new MqttServerConfiguration
            {
                BrokerPort = brokerPort,
                Mapper = mapper
            },
            NullLogger<MqttSubjectServer>.Instance);

        var clientContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithSourceTransactions()
            .WithSourceMonitoring();
        var clientRoot = new TestRoot(clientContext);

        await using var source = new MqttSubjectClientSource(
            clientRoot,
            new MqttClientConfiguration
            {
                BrokerHost = "localhost",
                BrokerPort = brokerPort,
                Mapper = mapper,
                ReconnectDelay = TimeSpan.FromMilliseconds(200),
                MaximumReconnectDelay = TimeSpan.FromSeconds(2),
                HealthCheckInterval = TimeSpan.FromSeconds(1)
            },
            NullLogger<MqttSubjectClientSource>.Instance);

        // Subscribed before anything can transition the source: the outage is asserted from the
        // recorded transitions, because the Synchronizing window between the disconnect and the
        // reconnect can be shorter than the interval at which a test can sample the current state.
        var stateRecorder = SourceStateRecorder.SubscribeTo(source);

        try
        {
            await server.StartAsync(CancellationToken.None);
            await source.StartAsync(CancellationToken.None);

            await stateRecorder.WaitForStatesAsync(
                TimeSpan.FromSeconds(30),
                "Initial subscription should complete.",
                SourceState.Synchronized);

            // Act - Disconnect is the soft fault: it breaks the broker connection without
            // stopping the connector, matching a real network blip.
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);

            // Assert - MQTT's Synchronized means subscriptions are (re-)established, not that
            // retained values have been received: MQTT provides no end-of-retained signal, so
            // asserting anything about received property values here would be dishonest.
            await stateRecorder.WaitForStatesAsync(
                TimeSpan.FromSeconds(15),
                "The disconnect should have been reported as an outage instead of the source staying Synchronized.",
                SourceState.Synchronized, SourceState.Synchronizing);

            await stateRecorder.WaitForStatesAsync(
                TimeSpan.FromSeconds(30),
                "Source should recover to Synchronized after reconnecting.",
                SourceState.Synchronized, SourceState.Synchronizing, SourceState.Synchronized);
        }
        finally
        {
            stateRecorder.Dispose();
            await source.StopAsync(CancellationToken.None);
            await server.StopAsync(CancellationToken.None);
        }
    }
}
