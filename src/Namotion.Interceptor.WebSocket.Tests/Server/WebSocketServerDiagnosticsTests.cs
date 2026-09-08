using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Server;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Server;

/// <summary>
/// Pins that a server which has never listened says so, and that the standalone server reports its two
/// transport numbers through its own diagnostics rather than only through the handler it wraps. The
/// liveness transitions need a bound port and are covered by
/// <see cref="WebSocketServerLivenessTests"/>.
/// </summary>
public class WebSocketServerDiagnosticsTests
{
    /// <summary>
    /// A compile-level pin of the member tree rather than behavioural coverage: every value asserted
    /// here is what a fresh <c>ConnectorMetrics</c> and an idle handler report.
    /// </summary>
    [Fact]
    public void WhenNeverStarted_ThenTheServerReportsUnavailableLiveness()
    {
        // Arrange
        using var server = CreateServer();

        // Act
        var diagnostics = server.Diagnostics;

        // Assert
        Assert.Null(diagnostics.IsOperational);
        Assert.Null(diagnostics.OperationalChangeTime);
        Assert.Null(diagnostics.StartTime);
        Assert.Null(diagnostics.LastError);
        Assert.Equal(0, diagnostics.ConnectionCount);
        Assert.Equal(0L, diagnostics.CurrentSequence);

        // Null rather than 0: the server measures neither direction.
        Assert.Null(diagnostics.Throughput.IncomingPerSecond);
        Assert.Null(diagnostics.Throughput.OutgoingPerSecond);
    }

    private static WebSocketSubjectServer CreateServer()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        return new WebSocketSubjectServer(
            new TestRoot(context),
            new WebSocketServerConfiguration(),
            NullLogger<WebSocketSubjectServer>.Instance);
    }
}
