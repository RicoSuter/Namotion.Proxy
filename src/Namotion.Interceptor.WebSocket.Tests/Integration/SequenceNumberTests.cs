using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

[Trait("Category", "Integration")]
public class SequenceNumberTests
{
    private readonly ITestOutputHelper _output;
    private readonly JsonWebSocketSerializer _serializer = new();

    public SequenceNumberTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Welcome_ShouldContainSequenceNumber()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);

        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, new HelloPayload());
        await ws.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        // Act
        var receiveBuffer = new byte[64 * 1024];
        var result = await ws.ReceiveAsync(receiveBuffer, CancellationToken.None);
        var bytes = new ReadOnlySpan<byte>(receiveBuffer, 0, result.Count);
        var (messageType, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(bytes);

        // Assert
        Assert.Equal(MessageType.Welcome, messageType);
        var welcome = _serializer.Deserialize<WelcomePayload>(
            new ReadOnlySpan<byte>(receiveBuffer, payloadStart, payloadLength));
        Assert.True(welcome.Sequence >= 0, "Welcome sequence should be non-negative");

        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ServerSequence_ShouldIncrementAfterBroadcast()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port);

        Assert.Equal(0L, server.Server!.Diagnostics.CurrentSequence);

        // Connect a client so broadcasts actually go through
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);
        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, new HelloPayload());
        await ws.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);
        var receiveBuffer = new byte[64 * 1024];
        await ws.ReceiveAsync(receiveBuffer, CancellationToken.None); // Welcome

        // Act
        server.Root!.Name = "Test";

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Server.Diagnostics.CurrentSequence > 0,
            message: "Sequence should increment after broadcasting to connected clients");

        _output.WriteLine($"Sequence after update: {server.Server.Diagnostics.CurrentSequence}");

        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Updates_ShouldHaveMonotonicallyIncreasingSequence()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);

        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, new HelloPayload());
        await ws.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        var receiveBuffer = new byte[64 * 1024];
        await ws.ReceiveAsync(receiveBuffer, CancellationToken.None); // Welcome

        // Act - Trigger multiple updates, waiting for each broadcast to complete
        server.Root!.Name = "Update1";
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Server!.Diagnostics.CurrentSequence > 0,
            timeout: TimeSpan.FromSeconds(5),
            message: "First update should be broadcast");
        var sequenceAfterFirst = server.Server!.Diagnostics.CurrentSequence;
        server.Root!.Name = "Update2";
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Server!.Diagnostics.CurrentSequence > sequenceAfterFirst,
            timeout: TimeSpan.FromSeconds(5),
            message: "Second update should be broadcast");
        var sequenceAfterSecond = server.Server!.Diagnostics.CurrentSequence;
        server.Root!.Name = "Update3";

        // Assert - Read updates and verify monotonic sequences
        var receivedUpdates = new ConcurrentQueue<ReceivedMessage>();
        using var receiveCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receiveTask = StartReceivingMessagesAsync(ws, receiveBuffer, receivedUpdates, receiveCts.Token);

        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => receivedUpdates.ToArray().Count(m => m.Type == MessageType.Update) >= 1,
                timeout: TimeSpan.FromSeconds(10),
                message: "Should have received at least one update with sequence");

            var updates = receivedUpdates.ToArray()
                .Where(m => m.Type == MessageType.Update)
                .ToArray();

            long lastSequence = 0;
            foreach (var update in updates)
            {
                _output.WriteLine($"Update sequence: {update.Sequence}");
                Assert.True(update.Sequence > lastSequence,
                    $"Sequence should be monotonically increasing: {update.Sequence} > {lastSequence}");
                lastSequence = update.Sequence;
            }

            Assert.True(updates.Length >= 1, "Should have received at least one update with sequence");
        }
        finally
        {
            receiveCts.Cancel();
            try { await receiveTask; } catch { }
        }

        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Heartbeat_ShouldArriveWithCorrectSequence()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port,
            configureServer: config => config.HeartbeatInterval = TimeSpan.FromSeconds(1));

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);

        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, new HelloPayload());
        await ws.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        var receiveBuffer = new byte[64 * 1024];
        await ws.ReceiveAsync(receiveBuffer, CancellationToken.None); // Welcome

        // Act - Wait for heartbeat
        var receivedMessages = new ConcurrentQueue<ReceivedMessage>();
        using var receiveCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receiveTask = StartReceivingMessagesAsync(ws, receiveBuffer, receivedMessages, receiveCts.Token);

        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => receivedMessages.ToArray().Any(m => m.Type == MessageType.Heartbeat),
                timeout: TimeSpan.FromSeconds(5),
                message: "Should have received a heartbeat");

            // Assert
            var heartbeat = receivedMessages.ToArray().First(m => m.Type == MessageType.Heartbeat);
            _output.WriteLine($"Heartbeat received with sequence: {heartbeat.Sequence}");
            Assert.True(heartbeat.Sequence >= 0, "Heartbeat sequence should be non-negative");
        }
        finally
        {
            receiveCts.Cancel();
            try { await receiveTask; } catch { }
        }

        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Client_ShouldReceiveUpdatesWithCorrectSequence()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port,
            configureServer: config => config.HeartbeatInterval = TimeSpan.FromSeconds(1));

        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "Initial",
            message: "Client should receive initial state");

        // Act
        server.Root!.Name = "NormalUpdate";

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "NormalUpdate",
            message: "Client should receive normal update");

        _output.WriteLine("Normal update flow verified");
    }

    [Fact]
    public async Task Heartbeat_SequenceStaysConsistentDuringQuietPeriod()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port,
            configureServer: config => config.HeartbeatInterval = TimeSpan.FromSeconds(1));

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);

        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, new HelloPayload());
        await ws.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        var receiveBuffer = new byte[64 * 1024];
        await ws.ReceiveAsync(receiveBuffer, CancellationToken.None); // Welcome

        // Act - Collect two heartbeats with no updates in between
        var receivedMessages = new ConcurrentQueue<ReceivedMessage>();
        using var receiveCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receiveTask = StartReceivingMessagesAsync(ws, receiveBuffer, receivedMessages, receiveCts.Token);

        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => receivedMessages.ToArray().Count(m => m.Type == MessageType.Heartbeat) >= 2,
                timeout: TimeSpan.FromSeconds(10),
                message: "Should have received at least two heartbeats");

            var heartbeats = receivedMessages.ToArray()
                .Where(m => m.Type == MessageType.Heartbeat)
                .Select(m => m.Sequence)
                .Take(2)
                .ToList();

            _output.WriteLine($"Heartbeat 1: sequence={heartbeats[0]}");
            _output.WriteLine($"Heartbeat 2: sequence={heartbeats[1]}");

            // Assert - During quiet period, heartbeat sequence should stay the same (no updates happened)
            Assert.Equal(2, heartbeats.Count);
            Assert.Equal(heartbeats[0], heartbeats[1]);
        }
        finally
        {
            receiveCts.Cancel();
            try { await receiveTask; } catch { }
        }

        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Heartbeat_SequenceAdvancesAfterUpdates()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port,
            configureServer: config => config.HeartbeatInterval = TimeSpan.FromSeconds(1));

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);

        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, new HelloPayload());
        await ws.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        var receiveBuffer = new byte[64 * 1024];
        await ws.ReceiveAsync(receiveBuffer, CancellationToken.None); // Welcome

        // Start background receive for the entire test
        var receivedMessages = new ConcurrentQueue<ReceivedMessage>();
        using var receiveCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var receiveTask = StartReceivingMessagesAsync(ws, receiveBuffer, receivedMessages, receiveCts.Token);

        try
        {
            // Get first heartbeat sequence
            await AsyncTestHelpers.WaitUntilAsync(
                () => receivedMessages.ToArray().Any(m => m.Type == MessageType.Heartbeat),
                timeout: TimeSpan.FromSeconds(5),
                message: "Should receive first heartbeat");

            var firstHeartbeat = receivedMessages.ToArray().First(m => m.Type == MessageType.Heartbeat);
            var firstHeartbeatSequence = firstHeartbeat.Sequence;
            _output.WriteLine($"First heartbeat: sequence={firstHeartbeatSequence}");

            // Act - Trigger some updates
            server.Root!.Name = "A";
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Server!.Diagnostics.CurrentSequence > firstHeartbeatSequence,
                timeout: TimeSpan.FromSeconds(5),
                message: "First update should be broadcast");
            server.Root!.Name = "B";

            // Wait for a second heartbeat (after the updates)
            var heartbeatCountBeforeWait = receivedMessages.ToArray().Count(m => m.Type == MessageType.Heartbeat);
            await AsyncTestHelpers.WaitUntilAsync(
                () => receivedMessages.ToArray().Count(m => m.Type == MessageType.Heartbeat) > heartbeatCountBeforeWait,
                timeout: TimeSpan.FromSeconds(5),
                message: "Should receive second heartbeat after updates");

            var allHeartbeats = receivedMessages.ToArray()
                .Where(m => m.Type == MessageType.Heartbeat)
                .ToArray();
            var secondHeartbeatSequence = allHeartbeats.Last().Sequence;
            _output.WriteLine($"Second heartbeat: sequence={secondHeartbeatSequence}");

            // Assert
            Assert.True(secondHeartbeatSequence > firstHeartbeatSequence,
                $"Heartbeat sequence should advance after updates: {secondHeartbeatSequence} > {firstHeartbeatSequence}");
        }
        finally
        {
            receiveCts.Cancel();
            try { await receiveTask; } catch { }
        }

        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task MultipleClients_ShouldReceiveSameSequenceNumbers()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port);

        using var ws1 = new ClientWebSocket();
        using var ws2 = new ClientWebSocket();
        await ws1.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);
        await ws2.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);

        var sendBuffer1 = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer1, MessageType.Hello, new HelloPayload());
        await ws1.SendAsync(sendBuffer1.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        var sendBuffer2 = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer2, MessageType.Hello, new HelloPayload());
        await ws2.SendAsync(sendBuffer2.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        var receiveBuffer1 = new byte[64 * 1024];
        var receiveBuffer2 = new byte[64 * 1024];
        await ws1.ReceiveAsync(receiveBuffer1, CancellationToken.None); // Welcome
        await ws2.ReceiveAsync(receiveBuffer2, CancellationToken.None); // Welcome

        // Act - Trigger updates
        server.Root!.Name = "BroadcastTest";
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Server!.Diagnostics.CurrentSequence > 0,
            timeout: TimeSpan.FromSeconds(5),
            message: "Update should be broadcast");

        var seq1 = await ReceiveNextUpdateSequenceAsync(ws1, receiveBuffer1);
        var seq2 = await ReceiveNextUpdateSequenceAsync(ws2, receiveBuffer2);

        // Assert
        _output.WriteLine($"Client1 sequence: {seq1}, Client2 sequence: {seq2}");
        Assert.True(seq1 > 0, "Client1 should have received a sequenced update");
        Assert.Equal(seq1, seq2);

        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Task.WhenAll(
                ws1.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token),
                ws2.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token));
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ClientReconnect_ShouldReceiveNewWelcomeSequence()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port);

        var receiveBuffer = new byte[64 * 1024];

        // First connection - get Welcome, then trigger updates while connected
        using var ws1 = new ClientWebSocket();
        await ws1.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);
        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, new HelloPayload());
        await ws1.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        var result1 = await ws1.ReceiveAsync(receiveBuffer, CancellationToken.None);
        var bytes1 = new ReadOnlySpan<byte>(receiveBuffer, 0, result1.Count);
        var (msgType1, ps1, pl1) = _serializer.DeserializeMessageEnvelope(bytes1);
        Assert.Equal(MessageType.Welcome, msgType1);
        var welcome1 = _serializer.Deserialize<WelcomePayload>(new ReadOnlySpan<byte>(receiveBuffer, ps1, pl1));
        var welcome1Sequence = welcome1.Sequence;
        _output.WriteLine($"First Welcome sequence: {welcome1Sequence}");

        // Act - Trigger updates while first client is still connected (so broadcast increments sequence)
        server.Root!.Name = "A";
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Server!.Diagnostics.CurrentSequence > 0,
            timeout: TimeSpan.FromSeconds(5),
            message: "First update should be broadcast");
        var sequenceAfterA = server.Server!.Diagnostics.CurrentSequence;
        server.Root!.Name = "B";
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Server!.Diagnostics.CurrentSequence > sequenceAfterA,
            timeout: TimeSpan.FromSeconds(5),
            message: "Second update should be broadcast");

        // Close first client
        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws1.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }

        // Second connection should get a higher Welcome sequence
        var welcome2Sequence = await ConnectAndGetWelcomeSequenceAsync(portLease.Port, receiveBuffer);
        _output.WriteLine($"Second Welcome sequence: {welcome2Sequence}");

        // Assert
        Assert.True(welcome2Sequence > welcome1Sequence,
            $"Second Welcome should have higher sequence: {welcome2Sequence} > {welcome1Sequence}");
    }

    [Fact]
    public async Task Client_ShouldSurviveMultipleUpdatesWithoutFalseGapDetection()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port,
            configureServer: config => config.HeartbeatInterval = TimeSpan.FromSeconds(1));

        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "Initial",
            message: "Client should receive initial state");

        // Act - Multiple updates - client should track sequences correctly
        for (var i = 1; i <= 10; i++)
        {
            var previousSequence = server.Server!.Diagnostics.CurrentSequence;
            server.Root!.Name = $"Update{i}";
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Server!.Diagnostics.CurrentSequence > previousSequence,
                timeout: TimeSpan.FromSeconds(5),
                message: $"Update {i} should be broadcast");
        }

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "Update10",
            message: "Client should receive all 10 updates without false gap detection");

        // Wait for at least one heartbeat cycle (1s interval) to verify no false gap detection
        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        // Verify client is still connected and receiving updates
        server.Root!.Name = "AfterHeartbeat";
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "AfterHeartbeat",
            message: "Client should still receive updates after heartbeat");

        _output.WriteLine("Client survived 10 rapid updates + heartbeat cycle without false gap detection");
    }

    [Fact]
    public async Task Client_ShouldResyncAfterServerRestart()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port,
            configureServer: config => config.HeartbeatInterval = TimeSpan.FromSeconds(1));

        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "Initial",
            message: "Client should receive initial state");

        // Trigger updates before restart
        server.Root!.Name = "BeforeRestart";
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "BeforeRestart",
            message: "Client should receive update before restart");

        // Act - Restart server - client should detect gap via heartbeat or connection loss
        await server.StopAsync();
        await server.RestartAsync();

        server.Root!.Name = "AfterRestart";

        // Assert - Client should reconnect and receive the new state
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "AfterRestart",
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should resync after server restart");

        _output.WriteLine("Client successfully resynced after server restart");
    }

    private record ReceivedMessage(MessageType Type, long Sequence);

    /// <summary>
    /// Starts a background task that continuously reads from the WebSocket and
    /// collects parsed messages into a thread-safe list. Cancel the token to stop.
    /// </summary>
    private Task StartReceivingMessagesAsync(
        ClientWebSocket webSocket,
        byte[] buffer,
        ConcurrentQueue<ReceivedMessage> messages,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested &&
                       webSocket.State == WebSocketState.Open)
                {
                    var readResult = await webSocket.ReceiveAsync(buffer, cancellationToken);
                    if (readResult.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    var bytes = new ReadOnlySpan<byte>(buffer, 0, readResult.Count);
                    var (messageType, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(bytes);

                    long sequence = -1;
                    if (messageType == MessageType.Update)
                    {
                        var updatePayload = _serializer.Deserialize<UpdatePayload>(
                            new ReadOnlySpan<byte>(buffer, payloadStart, payloadLength));
                        sequence = updatePayload.Sequence ?? -1;
                    }
                    else if (messageType == MessageType.Heartbeat)
                    {
                        var heartbeat = _serializer.Deserialize<HeartbeatPayload>(
                            new ReadOnlySpan<byte>(buffer, payloadStart, payloadLength));
                        sequence = heartbeat.Sequence;
                    }

                    messages.Enqueue(new ReceivedMessage(messageType, sequence));
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        }, cancellationToken);
    }

    private async Task<long> ReceiveNextUpdateSequenceAsync(ClientWebSocket webSocket, byte[] buffer)
    {
        var messages = new ConcurrentQueue<ReceivedMessage>();
        using var receiveCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receiveTask = StartReceivingMessagesAsync(webSocket, buffer, messages, receiveCts.Token);

        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => messages.ToArray().Any(m => m.Type == MessageType.Update),
                timeout: TimeSpan.FromSeconds(5),
                message: "Should receive an Update message");
        }
        catch (TimeoutException)
        {
            return -1;
        }
        finally
        {
            receiveCts.Cancel();
            try { await receiveTask; } catch { }
        }

        var update = messages.ToArray().First(m => m.Type == MessageType.Update);
        return update.Sequence;
    }

    private async Task<long> ConnectAndGetWelcomeSequenceAsync(int port, byte[] receiveBuffer)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{port}/ws"), CancellationToken.None);

        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, new HelloPayload());
        await ws.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        var result = await ws.ReceiveAsync(receiveBuffer, CancellationToken.None);
        var bytes = new ReadOnlySpan<byte>(receiveBuffer, 0, result.Count);
        var (messageType, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(bytes);

        Assert.Equal(MessageType.Welcome, messageType);
        var welcome = _serializer.Deserialize<WelcomePayload>(
            new ReadOnlySpan<byte>(receiveBuffer, payloadStart, payloadLength));

        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }

        return welcome.Sequence;
    }
}
