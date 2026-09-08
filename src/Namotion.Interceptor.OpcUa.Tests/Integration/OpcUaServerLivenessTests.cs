using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// The server owns its own restart loop, so nothing outside it can tell whether it is serving. These
/// pin the transitions the loop is responsible for.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaServerLivenessTests
{
    private readonly ITestOutputHelper _output;

    public OpcUaServerLivenessTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenTheServerIsAcceptingConnections_ThenItReportsOperationalUntilItStops()
    {
        // Arrange
        var logger = new TestLogger(_output);
        using var port = await OpcUaTestPortPool.AcquireAsync();

        await using var server = new OpcUaTestServer<LivenessTestRoot>(logger);
        await server.StartAsync(
            createRoot: context => new LivenessTestRoot(context),
            baseAddress: port.BaseAddress,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        var serverService = server.Server!;

        // Act
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverService.Diagnostics.IsOperational == true,
            message: "The server should report operational once it has started serving.");

        // Assert
        Assert.NotNull(serverService.Diagnostics.OperationalChangeTime);
        Assert.NotNull(serverService.Diagnostics.StartTime);
        Assert.Equal(0, serverService.Diagnostics.ConsecutiveFailures);
        Assert.Null(serverService.Diagnostics.LastError);

        // Act
        await server.StopAsync();

        // Assert
        Assert.False(serverService.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenTheServerIsForceKilled_ThenItBecomesOperationalAgain()
    {
        // Arrange
        var logger = new TestLogger(_output);
        using var port = await OpcUaTestPortPool.AcquireAsync();

        await using var server = new OpcUaTestServer<LivenessTestRoot>(logger);
        await server.StartAsync(
            createRoot: context => new LivenessTestRoot(context),
            baseAddress: port.BaseAddress,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        var serverService = server.Server!;

        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => serverService.Diagnostics.IsOperational == true,
                message: "The server should report operational once it has started serving.");

            var firstOperationalTime = serverService.Diagnostics.OperationalChangeTime;

            // Act
            await ((IFaultInjectable)serverService).InjectFaultAsync(FaultType.Kill, CancellationToken.None);

            // Assert
            // The timestamp only moves when the flag does, so this also pins that the server reported
            // itself down between the two runs rather than staying up across the restart.
            await AsyncTestHelpers.WaitUntilAsync(
                () => serverService.Diagnostics.IsOperational == true &&
                      serverService.Diagnostics.OperationalChangeTime != firstOperationalTime,
                message: "The server should report operational again after restarting.");

            // Nothing is asserted about LastError: the restart rebinds the same endpoint immediately,
            // so a bind failure the loop absorbs by backing off can legitimately leave an error behind.
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task WhenTheServerIsServing_ThenItsOutboundChangeQueueIsRegisteredAsUnbounded()
    {
        // Arrange: a buffer time that outlasts the test, so a captured change stays in the processor's
        // queue instead of being flushed away before the depth can be read.
        var logger = new TestLogger(_output);
        using var port = await OpcUaTestPortPool.AcquireAsync();

        await using var server = new OpcUaTestServer<LivenessTestRoot>(logger);
        await server.StartAsync(
            createRoot: context => new LivenessTestRoot(context),
            baseAddress: port.BaseAddress,
            certificateStoreBasePath: port.CertificateStoreBasePath,
            bufferTime: TimeSpan.FromMinutes(5));

        var serverService = server.Server!;

        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => serverService.Diagnostics.IsOperational == true,
                message: "The server should report operational once it has started serving.");

            // Act
            // Re-written on each poll because the processor only captures changes once it is running.
            var probeValue = 0;
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    server.Root!.Value = "v" + probeValue++;
                    return serverService.Diagnostics.OutboundChanges.Depth > 0;
                },
                message: "The outbound change queue never reported a depth, so it was never registered.");

            // Assert
            Assert.Null(serverService.Diagnostics.OutboundChanges.Capacity);
        }
        finally
        {
            await server.StopAsync();
        }
    }
}

[InterceptorSubject]
public partial class LivenessTestRoot
{
    public LivenessTestRoot()
    {
        Value = string.Empty;
    }

    [Path("opc", "Value")]
    public partial string Value { get; set; }
}
