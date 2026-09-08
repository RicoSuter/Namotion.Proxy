using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Client;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Client;

/// <summary>
/// Pins that the claimed-property gauge is pointed at the client's own ownership manager, and that a
/// client which has never connected says so. The liveness transitions need a server and are covered
/// by <see cref="WebSocketClientLivenessTests"/>.
/// </summary>
public class WebSocketClientDiagnosticsTests
{
    private readonly ITestOutputHelper _output;

    public WebSocketClientDiagnosticsTests(ITestOutputHelper output)
    {
        _output = output;
    }

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
        var property = ((TestRoot)source.RootSubject).GetPropertyReference(nameof(TestRoot.Name));

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

    /// <summary>
    /// The reconnect loop lives in the listen lifetime, outside the try in
    /// <c>SubjectSourceBase.RunAsync</c> that records per-attempt failures, so the client has to
    /// report these itself.
    /// </summary>
    [Trait("Category", "Integration")]
    [Fact]
    public async Task WhenTheServerStaysDownAfterAConnection_ThenTheFailedReconnectReachesLastError()
    {
        // Arrange - connected first, so the failure under test is the reconnect rather than the
        // initial connect, which the base class would report on its own.
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        var server = new WebSocketTestServer<TestRoot>(_output);
        await server.StartAsync(context => new TestRoot(context), port: portLease.Port);
        await using var source = CreateClientSource(portLease.Port);

        await source.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == true,
                message: "The client should report operational once the handshake is accepted.");
            Assert.Null(source.Diagnostics.LastError);

            // Act
            await server.StopAsync();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.LastError is not null,
                message: "A client that cannot reconnect should report the failure.");
            Assert.False(source.Diagnostics.IsOperational);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            await server.DisposeAsync();
        }
    }

    private static WebSocketSubjectClientSource CreateClientSource(int? port = null)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

        return new WebSocketSubjectClientSource(
            new TestRoot(context),
            new WebSocketClientConfiguration
            {
                // Port 59999 is never dialled: nothing but the reconnect test starts a connect attempt.
                ServerUri = new Uri($"ws://localhost:{port ?? 59999}/ws"),
                ReconnectDelay = TimeSpan.FromMilliseconds(200),
                MaxReconnectDelay = TimeSpan.FromSeconds(2)
            },
            NullLogger<WebSocketSubjectClientSource>.Instance);
    }
}
