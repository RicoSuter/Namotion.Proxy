using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for concurrent operations and edge cases during OPC UA operations.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaConcurrencyTests
{
    private readonly ITestOutputHelper _output;

    public OpcUaConcurrencyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private async Task<(OpcUaTestServer<TestRoot> Server, OpcUaTestClient<TestRoot> Client, PortLease Port, TestLogger Logger)> StartServerAndClientAsync()
    {
        var logger = new TestLogger(_output);
        var port = await OpcUaTestPortPool.AcquireAsync();

        var server = new OpcUaTestServer<TestRoot>(logger);
        await server.StartAsync(
            context => new TestRoot(context),
            (context, root) =>
            {
                root.Connected = true;
                root.Name = "Initial";
                root.Number = 42m;
            },
            baseAddress: port.BaseAddress,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        var client = new OpcUaTestClient<TestRoot>(logger);
        await client.StartAsync(
            context => new TestRoot(context),
            isConnected: root => root.Connected,
            serverUrl: port.ServerUrl,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        return (server, client, port, logger);
    }

    [Fact]
    public async Task ConcurrentPropertyChanges_DuringReconnection_NoDataCorruption()
    {
        // Verifies that property changes during reconnection don't cause corruption.

        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;
        TestLogger? logger = null;

        try
        {
            (server, client, port, logger) = await StartServerAndClientAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Source);
            Assert.True(client.Source.Diagnostics.IsOperational);
            logger.Log("Initial connection established");

            // Background updates
            var cts = new CancellationTokenSource();
            var updateCounter = 0;
            var exceptions = new List<Exception>();

            var updateTask = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        server.Root.Number = updateCounter;
                        server.Root.Name = $"Update{updateCounter}";
                        Interlocked.Increment(ref updateCounter);
                        await Task.Delay(100, cts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            }, cts.Token);

            // Wait for updates to start
            await AsyncTestHelpers.WaitUntilAsync(
                () => updateCounter > 0,
                timeout: TimeSpan.FromSeconds(90));
            logger.Log($"Updates started: {updateCounter}");

            // Restart during updates
            logger.Log("Stopping server...");
            await server.StopAsync();
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Source.Diagnostics.IsOperational == false,
                timeout: TimeSpan.FromSeconds(90),
                message: "Client should detect disconnection");
            logger.Log("Client detected disconnection");

            await server.RestartAsync();

            // Verify updates continue
            var countBefore = updateCounter;
            await AsyncTestHelpers.WaitUntilAsync(
                () => updateCounter > countBefore,
                timeout: TimeSpan.FromSeconds(180),
                message: "Updates should continue after reconnection");

            // Cleanup
            await cts.CancelAsync();
            try { await updateTask; } catch (OperationCanceledException) { }

            logger.Log($"Total updates: {updateCounter}, Exceptions: {exceptions.Count}");
            Assert.Empty(exceptions);

            // Verify final consistency
            server.Root.Name = "FinalValue";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "FinalValue",
                timeout: TimeSpan.FromSeconds(90),
                message: "Final value should sync");
            Assert.Equal("FinalValue", client.Root.Name);
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task DisposalEdgeCases_NoCrashesOrExceptions()
    {
        // Tests disposal edge cases in one setup:
        // 1. Concurrent disposal of client and server
        // 2. Diagnostics access during disposal

        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;
        TestLogger? logger = null;

        try
        {
            (server, client, port, logger) = await StartServerAndClientAsync();

            Assert.NotNull(server.Server);
            Assert.NotNull(client.Source);

            logger.Log($"Server sessions: {server.Server.Diagnostics.ActiveSessionCount}, Client connected: {client.Source.Diagnostics.IsOperational}");

            // === Test 1: Diagnostics access during disposal ===
            logger.Log("=== Test 1: Diagnostics during disposal ===");
            var diagnosticsExceptions = new List<Exception>();

            // Start rapid diagnostics access
            var accessTask = Task.Run(async () =>
            {
                for (var i = 0; i < 50; i++)
                {
                    try
                    {
                        _ = server.Server.Diagnostics.ActiveSessionCount;
                        _ = server.Server.Diagnostics.IsOperational;
                        _ = client.Source.Diagnostics.IsOperational;
                        _ = client.Source.Diagnostics.MonitoredItemCount;
                    }
                    catch (Exception ex)
                    {
                        diagnosticsExceptions.Add(ex);
                    }
                    await Task.Delay(5);
                }
            });

            // Small delay then start disposal
            await Task.Delay(25);

            // === Test 2: Concurrent disposal ===
            logger.Log("=== Test 2: Concurrent disposal ===");
            var disposeClient = client.DisposeAsync().AsTask();
            var disposeServer = server.DisposeAsync().AsTask();

            await Task.WhenAll(disposeClient, disposeServer, accessTask);

            logger.Log($"Diagnostics exceptions: {diagnosticsExceptions.Count}");
            Assert.Empty(diagnosticsExceptions);

            logger.Log("All disposal edge cases passed");

            client = null;
            server = null;
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }
}
