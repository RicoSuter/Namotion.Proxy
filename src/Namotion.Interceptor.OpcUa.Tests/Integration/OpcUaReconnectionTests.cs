using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for OPC UA client reconnection after server restarts.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaReconnectionTests
{
    private readonly ITestOutputHelper _output;

    // Reconnection tests need fast disconnection detection
    // SessionTimeout determines when connection is considered lost (min 10s due to server MinSessionTimeout)
    // KeepAliveInterval determines how often to check (1s for rapid detection)
    private static readonly Action<OpcUaClientConfiguration> FastDisconnectionConfig = config =>
    {
        config.SessionTimeout = TimeSpan.FromSeconds(10); // Minimum allowed by server
        config.KeepAliveInterval = TimeSpan.FromSeconds(1); // Fast disconnection detection
    };

    // Config for instant restart tests - longer session timeout gives SDK more time to reconnect
    // when server comes back quickly (before client fully detects disconnection)
    private static readonly Action<OpcUaClientConfiguration> InstantRestartConfig = config =>
    {
        config.SessionTimeout = TimeSpan.FromSeconds(30); // Standard timeout for graceful recovery
        config.KeepAliveInterval = TimeSpan.FromSeconds(2); // Moderate detection speed
        config.ReconnectInterval = TimeSpan.FromSeconds(2); // Try reconnecting frequently
    };

    // Isolates the SDK reconnect path: the health check loop is the only other place that raises
    // liveness, so an interval far longer than the whole test leaves the reconnect completion as the
    // only candidate.
    private static readonly Action<OpcUaClientConfiguration> HealthCheckSuppressedConfig = config =>
    {
        config.SessionTimeout = TimeSpan.FromSeconds(30);
        config.KeepAliveInterval = TimeSpan.FromSeconds(1);
        config.ReconnectInterval = TimeSpan.FromSeconds(2);
        config.SubscriptionHealthCheckInterval = TimeSpan.FromMinutes(5);
    };

    public OpcUaReconnectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private async Task<(OpcUaTestServer<TestRoot> Server, OpcUaTestClient<TestRoot> Client, PortLease Port, TestLogger Logger)> StartServerAndClientAsync(
        Action<OpcUaClientConfiguration>? configureClient = null)
    {
        var logger = new TestLogger(_output);
        var port = await OpcUaTestPortPool.AcquireAsync();

        var server = new OpcUaTestServer<TestRoot>(logger);
        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) =>
            {
                root.Connected = true;
                root.Name = "Initial";
                root.Number = 42m;
            },
            baseAddress: port.BaseAddress,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        var client = new OpcUaTestClient<TestRoot>(logger, configureClient ?? FastDisconnectionConfig);
        await client.StartAsync(
            context => new TestRoot(context),
            isConnected: root => root.Connected,
            serverUrl: port.ServerUrl,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        return (server, client, port, logger);
    }

    [Fact]
    public async Task ServerRestart_WithDisconnectionWait_ClientRecovers()
    {
        // Tests reconnection when client detects disconnection before server restarts.

        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            (server, client, port, var logger) = await StartServerAndClientAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Source);

            // Track session swaps across the reconnection. Handler is sync per the event
            // contract; lock keeps the captured pairs consistent if multiple swaps fire.
            var sessionTransitions = new List<(bool HadPrevious, bool HasCurrent)>();
            var transitionsLock = new object();
            void OnSessionChanged(object? sender, OpcUaCurrentSessionChangedEventArgs args)
            {
                lock (transitionsLock)
                {
                    sessionTransitions.Add((args.PreviousSession is not null, args.CurrentSession is not null));
                }
            }

            client.Source.CurrentSessionChanged += OnSessionChanged;
            try
            {
                // LastError is sticky for the whole epoch, so a connect attempt that failed once and
                // succeeded on retry leaves it populated. Captured as a baseline instead, so what
                // follows means nothing failed after the connection was established.
                var errorAfterConnecting = client.Source!.Diagnostics.LastError;

                // Verify initial sync
                server.Root.Name = "Initial";
                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Root.Name == "Initial",
                    timeout: TimeSpan.FromSeconds(90),
                    message: "Initial sync should complete");
                logger.Log("Initial sync verified");

                Assert.Same(errorAfterConnecting, client.Source!.Diagnostics.LastError);

                var initialAttempts = client.Source!.Diagnostics.Reconnects.TotalAttempts;

                // Stop server and wait for client to detect disconnection
                logger.Log("Stopping server...");
                await server.StopAsync();

                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Source!.Diagnostics.IsOperational == false,
                    timeout: TimeSpan.FromSeconds(90),
                    message: "Client should detect disconnection");
                logger.Log("Client detected disconnection");

                // Restart server
                await server.RestartAsync();

                // Verify data flows after reconnection
                server.Root.Name = "AfterRestart";
                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Root.Name == "AfterRestart",
                    timeout: TimeSpan.FromSeconds(180),
                    message: "Data should flow after restart");
                logger.Log($"Client received: {client.Root.Name}");

                // Verify metrics
                var finalAttempts = client.Source!.Diagnostics.Reconnects.TotalAttempts;
                var successfulReconnections = client.Source!.Diagnostics.Reconnects.TotalSucceeded;

                logger.Log($"Reconnection attempts: {initialAttempts} -> {finalAttempts}");
                logger.Log($"Successful reconnections: {successfulReconnections}");

                Assert.True(finalAttempts > initialAttempts,
                    $"Reconnection attempts should have increased (was {initialAttempts}, now {finalAttempts})");
                Assert.True(successfulReconnections >= 1,
                    $"Should have at least 1 successful reconnection, had {successfulReconnections}");

                // After a healthy reconnect the connector is connected again with a live session.
                // Waited for rather than asserted outright: liveness is edge-driven, and which edge
                // raises it depends on which reconnect path ran. Bounded at four health check
                // intervals (5s each here), so a rise that arrived late enough to be worthless
                // still fails.
                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Source!.Diagnostics.IsOperational == true,
                    timeout: TimeSpan.FromSeconds(20),
                    message: "Client should report itself operational again after reconnecting");
                Assert.NotNull(client.Source.CurrentSession);

                // Session swap visible to consumers. The reconnect cycle takes one of two paths
                // depending on timing and server behavior:
                //   A) SDK transfer succeeds → single (HadPrevious=true, HasCurrent=true) swap
                //   B) SDK transfer fails / preserved session / stall reset →
                //      (HadPrevious=true, HasCurrent=false) abandon + (HadPrevious=false, HasCurrent=true) recreate
                // Either way, the consumer must see at least one transition releasing a previous
                // session AND at least one transition with a current session — which together prove
                // the connector raises events for both sides of the swap, including null transitions.
                lock (transitionsLock)
                {
                    Assert.Contains(sessionTransitions, t => t.HadPrevious);
                    Assert.Contains(sessionTransitions, t => t.HasCurrent);
                }

                // Reconnection counter invariant: once all in-flight attempts have resolved,
                // Total == Successful + Failed + Abandoned. We are connected with a live session
                // here, so no attempt is in flight. Capture once to avoid torn reads across counters.
                var total = client.Source!.Diagnostics.Reconnects.TotalAttempts;
                var success = client.Source!.Diagnostics.Reconnects.TotalSucceeded;
                var failed = client.Source!.Diagnostics.Reconnects.TotalFailed;
                var abandoned = client.Source!.Diagnostics.Reconnects.TotalAbandoned;
                Assert.Equal(total, success + failed + abandoned);

                logger.Log("Test passed");
            }
            finally
            {
                client.Source.CurrentSessionChanged -= OnSessionChanged;
            }
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task ServerRestart_Instant_ClientRecovers()
    {
        // Tests reconnection when server restarts immediately (no time for client to detect disconnection).
        // Uses InstantRestartConfig with longer session timeout to give SDK more time to recover.

        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            (server, client, port, var logger) = await StartServerAndClientAsync(InstantRestartConfig);

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Source!.Diagnostics);

            // Verify initial sync
            server.Root.Name = "Initial";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "Initial",
                timeout: TimeSpan.FromSeconds(90),
                message: "Initial sync should complete");
            logger.Log("Initial sync verified");

            // Instant restart - no waiting for disconnection detection
            logger.Log("Instant server restart...");
            await server.StopAsync();
            await server.RestartAsync();

            // Verify data flows after reconnection
            server.Root.Name = "AfterInstantRestart";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterInstantRestart",
                timeout: TimeSpan.FromSeconds(180),
                message: "Data should flow after instant restart");
            logger.Log($"Client received: {client.Root.Name}");

            logger.Log("Test passed");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task WhenTheSdkTransfersItsSubscriptions_ThenLivenessRisesWithoutWaitingForTheNextHealthCheck()
    {
        // A server restart makes the SDK recreate the session and carry the subscriptions across, so
        // reporting the connector as down until the next health check would show an operator
        // "disconnected" while values update.

        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange
            (server, client, port, var logger) = await StartServerAndClientAsync(HealthCheckSuppressedConfig);

            Assert.NotNull(client.Source);
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Source!.Diagnostics.IsOperational == true,
                timeout: TimeSpan.FromSeconds(60),
                message: "Client should report operational after the initial connect");

            // Act
            await server.StopAsync();
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Source!.Diagnostics.IsOperational == false,
                timeout: TimeSpan.FromSeconds(60),
                message: "Client should detect the outage");
            logger.Log("Client detected disconnection");

            await server.RestartAsync();

            // Assert - the health check loop cannot be what raises this: its next tick is five minutes
            // out. The bound is far below that, so a rise that arrived late enough to be worthless
            // still fails.
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Source!.Diagnostics.IsOperational == true,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should report operational as soon as the reconnect transferred its subscriptions");

            Assert.True(client.Source!.Diagnostics.Reconnects.TotalSucceeded >= 1,
                "The rise should follow a completed reconnect rather than an outage that never happened");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task LargeSubscriptionCount_AllPropertiesResync()
    {
        // Verifies all subscriptions recreated after restart with many properties.

        var logger = new TestLogger(_output);
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            port = await OpcUaTestPortPool.AcquireAsync();

            server = new OpcUaTestServer<TestRoot>(logger);
            await server.StartAsync(
                context => new TestRoot(context),
                (context, root) =>
                {
                    root.Connected = true;
                    root.Name = "LargeSubscription";
                    root.Number = 100m;

                    var people = new TestPerson[20];
                    for (var i = 0; i < people.Length; i++)
                    {
                        people[i] = new TestPerson(context)
                        {
                            FirstName = $"First{i}",
                            LastName = $"Last{i}",
                            Scores = [i * 1.0, i * 2.0, i * 3.0]
                        };
                    }
                    root.People = people;
                },
                baseAddress: port.BaseAddress,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            client = new OpcUaTestClient<TestRoot>(logger, FastDisconnectionConfig);
            await client.StartAsync(
                context => new TestRoot(context),
                isConnected: root => root.Connected,
                serverUrl: port.ServerUrl,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            // Verify initial sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 20,
                timeout: TimeSpan.FromSeconds(90),
                message: "All people should sync");

            var monitoredCount = client.Source!.Diagnostics!.MonitoredItemCount;
            logger.Log($"Monitored items: {monitoredCount}");

            // Restart
            logger.Log("Stopping server...");
            await server.StopAsync();
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Source!.Diagnostics.IsOperational == false,
                timeout: TimeSpan.FromSeconds(90),
                message: "Client should detect disconnection");
            logger.Log("Client detected disconnection");

            await server.RestartAsync();

            // Verify all properties resync
            server.Root.Name = "AfterRestart";
            server.Root.People[0].FirstName = "Updated0";
            server.Root.People[19].FirstName = "Updated19";

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterRestart" &&
                      client.Root.People[0].FirstName == "Updated0" &&
                      client.Root.People[19].FirstName == "Updated19",
                timeout: TimeSpan.FromSeconds(180),
                message: "All properties should resync");

            logger.Log($"All {client.Source!.Diagnostics.MonitoredItemCount} items resynced");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }
}
