using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Client;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

/// <summary>
/// Verifies that the client source reports an outage rather than staying Synchronized while
/// disconnected. Unlike OPC UA, WebSocket already buffers at loss detection (see
/// WebSocketSubjectClientSource.RunMonitorLoopAsync's StartBuffering call), so this test is a
/// regression guard, not a bug reproduction: it exists to stop a future connector change from
/// silently reintroducing the OPC UA defect this feature closes.
/// </summary>
[Trait("Category", "Integration")]
public class OutageStateTests
{
    private readonly ITestOutputHelper _output;

    public OutageStateTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenTheConnectionIsLost_ThenTheSourceReportsSynchronizingUntilItRecovers()
    {
        // Arrange - server fixture copied from WebSocketServerClientTests.
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port);

        // The client source is constructed directly (not through WebSocketTestClient, which only
        // exposes IHostedService) so it can be held as its concrete type for IFaultInjectable and
        // ISubjectSource.StateChanged.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithSourceMonitoring();
        var root = new TestRoot(context);

        await using var source = new WebSocketSubjectClientSource(
            root,
            new WebSocketClientConfiguration
            {
                ServerUri = new Uri($"ws://localhost:{portLease.Port}/ws"),
                ReconnectDelay = TimeSpan.FromMilliseconds(200),
                MaxReconnectDelay = TimeSpan.FromSeconds(2)
            },
            NullLogger<WebSocketSubjectClientSource>.Instance);

        // Subscribed before anything can transition the source: the outage is asserted from the
        // recorded transitions, because the Synchronizing window between the disconnect and the
        // reconnect can be shorter than the interval at which a test can sample the current state.
        var stateRecorder = SourceStateRecorder.SubscribeTo(source);

        try
        {
            await source.StartAsync(CancellationToken.None);

            await stateRecorder.WaitForStatesAsync(
                TimeSpan.FromSeconds(30),
                "Initial sync should complete.",
                SourceState.Synchronized);

            // Act - Disconnect is the soft fault: it aborts the socket without stopping the
            // connector, matching a real network blip.
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);

            // Assert
            await stateRecorder.WaitForStatesAsync(
                TimeSpan.FromSeconds(15),
                "The disconnect should have been reported as an outage instead of the source staying Synchronized.",
                SourceState.Synchronized, SourceState.Synchronizing);

            var outage = await stateRecorder.WaitForStatesAsync(
                TimeSpan.FromSeconds(30),
                "Source should recover to Synchronized after reconnecting.",
                SourceState.Synchronized, SourceState.Synchronizing, SourceState.Synchronized);

            // Each transition carries the timestamp that ISubjectSource.StateChangeTime was set to,
            // so these compare the moments themselves rather than whatever the property reads back as
            // once the outage is over.
            var firstSynchronizedAt = outage[0].Timestamp;
            var outageDetectedAt = outage[1].Timestamp;
            var recoveredAt = outage[2].Timestamp;

            Assert.True(outageDetectedAt > firstSynchronizedAt,
                "Losing synchronization should have moved StateChangeTime past the initial sync, but the " +
                $"recorded transitions were: {stateRecorder}.");

            // Against the outage moment, not the initial sync: the timestamp only ever advances, so
            // comparing against firstSynchronizedAt again could not fail.
            Assert.True(recoveredAt > outageDetectedAt,
                "Recovering should have moved StateChangeTime past the moment the outage was detected, but the " +
                $"recorded transitions were: {stateRecorder}.");
        }
        finally
        {
            stateRecorder.Dispose();
            await source.StopAsync(CancellationToken.None);
        }
    }
}
