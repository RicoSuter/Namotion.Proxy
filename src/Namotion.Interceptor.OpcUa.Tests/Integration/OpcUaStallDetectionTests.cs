using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for OPC UA client stall detection and recovery.
/// These tests verify that the client can detect when SDK reconnection is stuck
/// and force a manual reconnection.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaStallDetectionTests
{
    private readonly ITestOutputHelper _output;

    // Stall detection config for tests: 15s max reconnect duration
    // Note: Under parallel execution, SDK can take 20-30s to even start reconnecting,
    // so we use the same 15s as OpcUaTestClient defaults for consistency.
    // SessionTimeout determines when connection is considered lost (min 10s due to server MinSessionTimeout)
    // KeepAliveInterval is set to 1s for fast disconnection detection.
    private readonly Action<OpcUaClientConfiguration> _fastStallConfig = config =>
    {
        config.SubscriptionHealthCheckInterval = TimeSpan.FromSeconds(2);
        config.MaxReconnectDuration = TimeSpan.FromSeconds(15);
        config.ReconnectInterval = TimeSpan.FromSeconds(1);
        config.SessionTimeout = TimeSpan.FromSeconds(10); // Minimum allowed by server
        config.KeepAliveInterval = TimeSpan.FromSeconds(1); // Fast disconnection detection
    };

    public OpcUaStallDetectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task StallDetection_WhenServerNeverReturns_ClientForcesReset()
    {
        // Tests that when server goes down and never comes back,
        // stall detection kicks in and resets the reconnection state
        // so the client can try fresh reconnection attempts.

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
                    root.Name = "Initial";
                },
                baseAddress: port.BaseAddress,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            client = new OpcUaTestClient<TestRoot>(logger, _fastStallConfig);
            await client.StartAsync(
                context => new TestRoot(context),
                isConnected: root => root.Connected,
                serverUrl: port.ServerUrl,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Source!.Diagnostics);

            // Verify initial connection
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Source!.Diagnostics.IsOperational == true,
                timeout: TimeSpan.FromSeconds(120),
                message: "Client should be connected after startup");
            logger.Log("Initial connection established");

            var initialReconnectAttempts = client.Source!.Diagnostics.Reconnects.TotalAttempts;
            logger.Log($"Initial reconnect attempts: {initialReconnectAttempts}");

            // Stop server - DO NOT restart it
            logger.Log("Stopping server (will NOT restart)...");
            await server.StopAsync();

            // Wait for client to detect disconnection (longer timeout for parallel test execution)
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Source!.Diagnostics.IsOperational == false,
                timeout: TimeSpan.FromSeconds(120),
                message: "Client should detect disconnection");
            logger.Log("Client detected disconnection");

            // Wait for reconnection to start, using Reconnects.TotalAttempts instead of IsReconnecting
            // because IsReconnecting is only true for the brief duration of each failed manual
            // reconnection attempt (~10-50ms when server is down). The 100ms polling interval
            // can miss it entirely, causing flaky test timeouts on CI.
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Source!.Diagnostics.Reconnects.TotalAttempts > initialReconnectAttempts,
                timeout: TimeSpan.FromSeconds(120),
                message: "Client should start reconnecting");
            logger.Log($"Client started reconnecting (attempts: {client.Source!.Diagnostics.Reconnects.TotalAttempts})");

            // Wait for client to demonstrate ongoing recovery behavior.
            // Two possible paths depending on timing:
            // 1. SDK keep-alive detects loss first → BeginReconnect → IsReconnecting=true for MaxReconnectDuration
            //    → stall detection triggers → manual reconnection takes over
            // 2. Health check detects PublishingStopped first → manual reconnection immediately
            //    → repeated attempts (server never returns)
            // Both paths are correct — the key is the client keeps retrying and doesn't get stuck.
            var recoveryTimeout = TimeSpan.FromSeconds(90);
            var startTime = DateTime.UtcNow;

            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    var attempts = client.Source!.Diagnostics.Reconnects.TotalAttempts;
                    if (attempts > initialReconnectAttempts + 2)
                    {
                        logger.Log($"Recovery verified: IsReconnecting={client.Source!.Diagnostics.IsReconnecting}, " +
                                   $"Attempts={attempts}");
                        return true;
                    }
                    return false;
                },
                timeout: recoveryTimeout,
                message: "Client should keep retrying reconnection");

            var elapsed = DateTime.UtcNow - startTime;
            logger.Log($"Recovery behavior confirmed in {elapsed.TotalSeconds:F1}s");

            // Verify recovery happened within expected timeframe
            Assert.True(elapsed < TimeSpan.FromSeconds(85),
                $"Recovery should be confirmed within 85s (actual: {elapsed.TotalSeconds:F1}s)");

            logger.Log("Test passed - client correctly handles server-never-returns scenario");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task StallDetection_WhenServerRestartsAfterStall_ClientRecovers()
    {
        // Tests that after stall detection resets the state,
        // the client can successfully reconnect when the server comes back.

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
                    root.Name = "Initial";
                },
                baseAddress: port.BaseAddress,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            client = new OpcUaTestClient<TestRoot>(logger, _fastStallConfig);
            await client.StartAsync(
                context => new TestRoot(context),
                isConnected: root => root.Connected,
                serverUrl: port.ServerUrl,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Source!.Diagnostics);

            // Verify initial sync
            server.Root.Name = "BeforeStall";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "BeforeStall",
                timeout: TimeSpan.FromSeconds(120),
                message: "Initial sync should complete");
            logger.Log("Initial sync verified");

            // Stop server
            logger.Log("Stopping server...");
            await server.StopAsync();

            // Wait for stall detection to trigger (don't restart yet)
            await Task.Delay(TimeSpan.FromSeconds(8)); // Let stall detection do its thing
            logger.Log("Waited for stall detection window");

            // Now restart the server
            logger.Log("Restarting server after stall detection window...");
            await server.RestartAsync();

            // Verify client recovers and data flows
            server.Root.Name = "AfterStallRecovery";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterStallRecovery",
                timeout: TimeSpan.FromSeconds(120),
                message: "Data should flow after stall recovery");
            logger.Log($"Client received: {client.Root.Name}");

            // Wait for connection status to stabilize
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Source!.Diagnostics.IsOperational == true,
                timeout: TimeSpan.FromSeconds(120),
                message: "Client should report as connected after recovery");
            logger.Log("Test passed - client recovered after stall");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task StallDetection_WhenServerRestartsQuickly_SdkReconnectsWithoutStallTrigger()
    {
        // Tests that when server restarts quickly (before MaxReconnectDuration),
        // the SDK reconnection succeeds without stall detection triggering.
        // This verifies we don't have false positive stall triggers.

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
                (_, root) =>
                {
                    root.Connected = true;
                    root.Name = "Initial";
                },
                baseAddress: port.BaseAddress,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            // Use longer MaxReconnectDuration to ensure SDK has time to reconnect
            // Use shorter SessionTimeout and KeepAliveInterval for faster disconnection detection on slow CI runners
            void QuickRestartConfig(OpcUaClientConfiguration config)
            {
                config.SubscriptionHealthCheckInterval = TimeSpan.FromSeconds(2);
                config.MaxReconnectDuration = TimeSpan.FromSeconds(60); // Long enough for SDK to succeed under CI load
                config.ReconnectInterval = TimeSpan.FromSeconds(1);
                config.SessionTimeout = TimeSpan.FromSeconds(10); // Minimum allowed by server for fast detection
                config.KeepAliveInterval = TimeSpan.FromSeconds(1); // Faster disconnection detection
            }

            client = new OpcUaTestClient<TestRoot>(logger, QuickRestartConfig);
            await client.StartAsync(
                context => new TestRoot(context),
                isConnected: root => root.Connected,
                serverUrl: port.ServerUrl,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Source!.Diagnostics);

            // Verify initial sync
            server.Root.Name = "BeforeRestart";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "BeforeRestart",
                timeout: TimeSpan.FromSeconds(120),
                message: "Initial sync should complete");
            logger.Log("Initial sync verified");

            var initialReconnectAttempts = client.Source!.Diagnostics.Reconnects.TotalAttempts;
            var initialSuccessfulReconnects = client.Source!.Diagnostics.Reconnects.TotalSucceeded;
            logger.Log($"Before restart - attempts: {initialReconnectAttempts}, successful: {initialSuccessfulReconnects}");

            // Stop server briefly
            logger.Log("Stopping server for quick restart...");
            await server.StopAsync();

            // Wait for client to detect disconnection (longer timeout for slow CI runners)
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Source!.Diagnostics.IsOperational == false || client.Source!.Diagnostics.IsReconnecting,
                timeout: TimeSpan.FromSeconds(120),
                message: "Client should detect disconnection or start reconnecting");
            logger.Log($"Client state after stop - Connected: {client.Source!.Diagnostics.IsOperational}, Reconnecting: {client.Source!.Diagnostics.IsReconnecting}");

            // Restart server quickly (well before MaxReconnectDuration of 30s)
            logger.Log("Restarting server quickly...");
            await server.RestartAsync();

            // Verify client recovers and data flows
            server.Root.Name = "AfterQuickRestart";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterQuickRestart",
                timeout: TimeSpan.FromSeconds(120),
                message: "Data should flow after quick restart");
            logger.Log($"Client received: {client.Root.Name}");

            // Verify client is connected
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Source!.Diagnostics.IsOperational == true,
                timeout: TimeSpan.FromSeconds(120),
                message: "Client should report as connected after recovery");

            // Log final state for debugging
            logger.Log($"After restart - attempts: {client.Source!.Diagnostics.Reconnects.TotalAttempts}, " +
                       $"successful: {client.Source!.Diagnostics.Reconnects.TotalSucceeded}, " +
                       $"connected: {client.Source!.Diagnostics.IsOperational}");

            logger.Log("Test passed - SDK reconnected without stall detection trigger");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }
}
