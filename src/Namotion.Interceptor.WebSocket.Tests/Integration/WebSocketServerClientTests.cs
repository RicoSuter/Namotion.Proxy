using System;
using System.Threading.Tasks;
using Namotion.Interceptor.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

/// <summary>
/// Integration tests for the standalone WebSocket server (AddWebSocketSubjectServer).
/// </summary>
[Trait("Category", "Integration")]
public class WebSocketServerClientTests
{
    private readonly ITestOutputHelper _output;

    public WebSocketServerClientTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ServerWriteProperty_ShouldUpdateClient()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port);

        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "Initial",
            message: "Client should receive initial state");

        // Act
        server.Root!.Name = "Updated from Server";

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "Updated from Server",
            message: "Client should receive server update");
    }

    [Fact]
    public async Task ClientWriteProperty_ShouldUpdateServer()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port);

        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "Initial",
            message: "Client should receive initial state");

        // Act
        client.Root!.Name = "Updated from Client";

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Root!.Name == "Updated from Client",
            message: "Server should receive client update");
    }

    [Fact]
    public async Task NumericProperty_ShouldSyncBidirectionally()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(context => new TestRoot(context), port: portLease.Port);
        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        // Act - Server updates
        server.Root!.Number = 123.45m;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Number == 123.45m,
            message: "Client should receive server's number update");

        // Act - Client updates
        client.Root!.Number = 678.90m;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Root.Number == 678.90m,
            message: "Server should receive client's number update");
    }

    [Fact]
    public async Task MultipleClients_ShouldAllReceiveUpdates()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client1 = new WebSocketTestClient<TestRoot>(_output);
        await using var client2 = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(context => new TestRoot(context), port: portLease.Port);
        await client1.StartAsync(context => new TestRoot(context), port: portLease.Port);
        await client2.StartAsync(context => new TestRoot(context), port: portLease.Port);

        // Act
        server.Root!.Name = "Broadcast Test";

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => client1.Root!.Name == "Broadcast Test" &&
                  client2.Root!.Name == "Broadcast Test",
            message: "Both clients should receive broadcast");
    }

    [Fact]
    public async Task ClientWriteProperty_ShouldPropagateToOtherClient()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client1 = new WebSocketTestClient<TestRoot>(_output);
        await using var client2 = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port);

        await client1.StartAsync(context => new TestRoot(context), port: portLease.Port);
        await client2.StartAsync(context => new TestRoot(context), port: portLease.Port);

        await AsyncTestHelpers.WaitUntilAsync(
            () => client1.Root!.Name == "Initial" &&
                  client2.Root!.Name == "Initial",
            message: "Both clients should receive initial state");

        // Act
        client1.Root!.Name = "From Client 1";

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Root!.Name == "From Client 1" &&
                  client2.Root!.Name == "From Client 1",
            message: "Server and client2 should receive client1's update");
    }

    [Fact]
    public async Task ServerRestart_WithDisconnectionWait_ClientRecovers()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port);

        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "Initial",
            message: "Initial sync should complete");

        _output.WriteLine("Initial sync verified");

        // Act - Stop and restart server
        _output.WriteLine("Stopping server...");
        await server.StopAsync();

        _output.WriteLine("Restarting server...");
        await server.RestartAsync();

        server.Root!.Name = "AfterRestart";

        // Assert - Wait for client to reconnect and sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "AfterRestart",
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should receive update after server restart");

        _output.WriteLine($"Client received: {client.Root!.Name}");
    }

    [Fact]
    public async Task ServerRestart_Instant_ClientRecovers()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port);

        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "Initial",
            message: "Initial sync should complete");

        _output.WriteLine("Initial sync verified");

        // Act - Instant restart - no waiting for disconnection detection
        _output.WriteLine("Instant server restart...");
        await server.StopAsync();
        await server.RestartAsync();

        server.Root!.Name = "AfterInstantRestart";

        // Assert - Wait for client to reconnect and sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "AfterInstantRestart",
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should receive update after instant restart");

        _output.WriteLine($"Client received: {client.Root!.Name}");
    }

    [Fact]
    public async Task ServerRestart_WithCollectionItems_AllPropertiesResync()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (context, root) =>
            {
                root.Name = "WithItems";
                root.Number = 100m;
                root.Items =
                [
                    new TestItem(context) { Label = "Item1", Value = 10 },
                    new TestItem(context) { Label = "Item2", Value = 20 },
                    new TestItem(context) { Label = "Item3", Value = 30 }
                ];
            },
            port: portLease.Port);

        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Items.Length == 3,
            timeout: TimeSpan.FromSeconds(10),
            message: "Initial collection sync");

        Assert.Equal("WithItems", client.Root!.Name);
        Assert.Equal(3, client.Root.Items.Length);
        _output.WriteLine("Initial sync verified with 3 items");

        // Act - Restart server and update state
        _output.WriteLine("Restarting server...");
        await server.StopAsync();
        await server.RestartAsync();

        server.Root!.Name = "AfterRestart";
        server.Root.Items[0].Label = "Updated1";
        server.Root.Items[2].Value = 300;

        // Assert - Wait for client to reconnect and sync all properties
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "AfterRestart" &&
                  client.Root.Items.Length == 3 &&
                  client.Root.Items[0].Label == "Updated1" &&
                  client.Root.Items[2].Value == 300,
            timeout: TimeSpan.FromSeconds(30),
            message: "All properties should resync after restart");

        _output.WriteLine($"Client synced: Name={client.Root.Name}, Items[0].Label={client.Root.Items[0].Label}, Items[2].Value={client.Root.Items[2].Value}");
    }

    [Fact]
    public async Task Server_MaxConnectionsReached_ShouldRejectNewClient()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client1 = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port,
            configureServer: config => config.MaxConnections = 1);

        await client1.StartAsync(context => new TestRoot(context), port: portLease.Port);

        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Server!.Diagnostics.ConnectionCount == 1,
            message: "First client should connect");

        _output.WriteLine($"First client connected. ConnectionCount: {server.Server!.Diagnostics.ConnectionCount}");

        // Act - Second client connects but gets rejected -- enters reconnect loop
        await using var client2 = new WebSocketTestClient<TestRoot>(_output);
        await client2.StartAsync(context => new TestRoot(context), port: portLease.Port);

        // Brief wait to give second client time to attempt connection
        await Task.Delay(500);

        // Assert - Server should still have only 1 active connection
        _output.WriteLine($"After second client attempt. ConnectionCount: {server.Server.Diagnostics.ConnectionCount}");
        Assert.Equal(1, server.Server.Diagnostics.ConnectionCount);
    }

    [Fact]
    public async Task AbruptClientDisconnect_ServerShouldCleanupConnection()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(context => new TestRoot(context), port: portLease.Port);
        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Server!.Diagnostics.ConnectionCount == 1,
            message: "Client should connect");

        _output.WriteLine($"Client connected. ConnectionCount: {server.Server!.Diagnostics.ConnectionCount}");

        // Act - Abruptly stop client
        await client.StopAsync();
        _output.WriteLine("Client stopped abruptly");

        // Trigger updates to hit the 3-failure zombie threshold.
        // Uses Task.Delay because the connection may already be removed by the
        // receive loop, leaving no clients and preventing sequence increment.
        for (var i = 0; i < 5; i++)
        {
            server.Root!.Name = $"Update-{i}";
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        // Assert - Wait for cleanup
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Server.Diagnostics.ConnectionCount == 0,
            timeout: TimeSpan.FromSeconds(10),
            message: "Server should clean up zombie connection");

        _output.WriteLine($"After cleanup. ConnectionCount: {server.Server.Diagnostics.ConnectionCount}");
    }
}
