using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Server;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Server;

/// <summary>
/// The server owns its own restart loop, so nothing outside it can tell whether the listener is up.
/// These pin the transitions the loop is responsible for, and that a restart can register its own
/// outbound change queue: the metrics permit one live registration at a time.
/// </summary>
[Trait("Category", "Integration")]
public class WebSocketServerLivenessTests
{
    [Fact]
    public async Task WhenTheListenerIsUp_ThenTheServerReportsOperationalUntilItStops()
    {
        // Arrange
        using var port = await WebSocketTestPortPool.AcquireAsync();
        await using var server = CreateServer(port.Port);

        // Act
        await server.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Diagnostics.IsOperational == true,
            message: "The server should report operational once the listener is accepting connections.");

        // Assert
        Assert.NotNull(server.Diagnostics.OperationalChangeTime);
        Assert.NotNull(server.Diagnostics.StartTime);
        Assert.Null(server.Diagnostics.LastError);
        Assert.Equal(0, server.Diagnostics.ConnectionCount);
        Assert.Equal(0L, server.Diagnostics.CurrentSequence);

        // Act
        await server.StopAsync(CancellationToken.None);

        // Assert
        Assert.False(server.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenTheListenerIsUp_ThenItsOutboundChangeQueueIsRegisteredAsUnbounded()
    {
        // Arrange: a buffer time that outlasts the test, so a captured change stays in the processor's
        // queue instead of being flushed away before the depth can be read.
        using var port = await WebSocketTestPortPool.AcquireAsync();
        await using var server = CreateServer(port.Port, bufferTime: TimeSpan.FromMinutes(5));
        var root = (TestRoot)server.RootSubject;

        await server.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.IsOperational == true,
                message: "The server should report operational once the listener is accepting connections.");

            // Act
            // Re-written on each poll because the processor only captures changes once it is running.
            var probeValue = 0;
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    root.Name = "v" + probeValue++;
                    return server.Diagnostics.OutboundChanges.Depth > 0;
                },
                message: "The outbound change queue never reported a depth, so it was never registered.");

            // Assert
            Assert.Null(server.Diagnostics.OutboundChanges.Capacity);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheServerIsForceKilled_ThenItBecomesOperationalAgain()
    {
        // Arrange
        using var port = await WebSocketTestPortPool.AcquireAsync();
        await using var server = CreateServer(port.Port);

        await server.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.IsOperational == true,
                message: "The server should report operational once the listener is accepting connections.");

            var firstOperationalTime = server.Diagnostics.OperationalChangeTime;

            // Act
            await ((IFaultInjectable)server).InjectFaultAsync(FaultType.Kill, CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.IsOperational == true &&
                      server.Diagnostics.OperationalChangeTime != firstOperationalTime,
                message: "The server should report operational again after restarting.");

            // Only this one error is ruled out rather than every error: the restart rebinds the same
            // port immediately, and a bind failure the loop absorbs by backing off belongs in
            // LastError by design.
            Assert.False(
                server.Diagnostics.LastError is InvalidOperationException,
                "The kill was recorded as a processing layer that ended on its own.");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheListenerCannotBind_ThenTheFailureIsReported()
    {
        // Arrange: the port is already taken, so the server fails inside the loop, which swallows the
        // exception rather than letting the base class see it.
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;

        await using var server = CreateServer(occupiedPort);

        await server.StartAsync(CancellationToken.None);
        try
        {
            // Act
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.LastError is not null,
                message: "A server that cannot bind should report the failure.");

            // Assert
            Assert.False(server.Diagnostics.IsOperational);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheServerIsStoppedDuringItsRestartBackoff_ThenTheHostedTaskCompletes()
    {
        // Arrange: the same occupied port as above, so the server fails to bind and spends the next
        // few seconds in its backoff, which is where the stop below lands. That delay sits outside
        // every catch around the attempt, so cancelling there needs a clause of its own.
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;

        await using var server = CreateServer(occupiedPort);

        await server.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Diagnostics.LastError is not null,
            message: "A server that cannot bind should report the failure before it backs off.");

        // Act
        await server.StopAsync(CancellationToken.None);

        // Assert
        var hostedTask = server.ExecuteTask;
        Assert.NotNull(hostedTask);
        Assert.Equal(TaskStatus.RanToCompletion, hostedTask.Status);
    }

    private static WebSocketSubjectServer CreateServer(int port, TimeSpan? bufferTime = null)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        return new WebSocketSubjectServer(
            new TestRoot(context),
            new WebSocketServerConfiguration
            {
                Port = port,
                BufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8)
            },
            NullLogger<WebSocketSubjectServer>.Instance);
    }
}
