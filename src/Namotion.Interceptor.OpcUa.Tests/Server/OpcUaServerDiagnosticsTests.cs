using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Tests.Server;

/// <summary>
/// Pins that the diagnostics read the metrics the connector itself writes to, and that a server which
/// has never served says so. The liveness transitions need a running transport and are covered by
/// <see cref="Integration.OpcUaServerLivenessTests"/>.
/// </summary>
public class OpcUaServerDiagnosticsTests
{
    /// <summary>
    /// A compile-level pin of the member tree rather than behavioural coverage: every value asserted
    /// here is what a fresh <c>ConnectorMetrics</c> reports.
    /// </summary>
    [Fact]
    public void WhenNeverStarted_ThenTheServerReportsUnavailableLiveness()
    {
        // Arrange
        using var server = CreateServer();

        // Act
        var diagnostics = server.Diagnostics;
        int activeSessionCount = diagnostics.ActiveSessionCount;

        // Assert
        Assert.Null(diagnostics.IsOperational);
        Assert.Null(diagnostics.OperationalChangeTime);
        Assert.Null(diagnostics.StartTime);
        Assert.Null(diagnostics.LastError);
        Assert.Equal(0, diagnostics.ConsecutiveFailures);
        Assert.Equal(0, activeSessionCount);
    }

    [Fact]
    public void WhenTheSdkServerInstanceIsUnavailable_ThenActiveSessionCountIsZero()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration();
        using var server = CreateServer(configuration);
        using var sdkServer = new OpcUaStandardServer(
            server.RootSubject, server, configuration, NullLogger.Instance);

        typeof(OpcUaSubjectServer)
            .GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(server, sdkServer);

        // Act
        var count = server.Diagnostics.ActiveSessionCount;

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void WhenThroughputIsInstrumented_ThenBothDirectionsAreWiredToACounter()
    {
        // Arrange
        using var server = CreateServer();

        // Act
        server.IncomingThroughput.Add(60);
        server.OutgoingThroughput.Add(120);
        var throughput = server.Diagnostics.Throughput;

        // Assert
        Assert.Equal(1.0, throughput.IncomingPerSecond!.Value);
        Assert.Equal(2.0, throughput.OutgoingPerSecond!.Value);
    }

    [Fact]
    public void WhenFailuresAreRecordedConcurrently_ThenDiagnosticsReportsEveryFailure()
    {
        // Arrange
        using var server = CreateServer();
        const int workerCount = 16;
        const int failuresPerWorker = 50_000;
        using var startGate = new ManualResetEventSlim();
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => new Thread(() =>
            {
                startGate.Wait();
                for (var failure = 0; failure < failuresPerWorker; failure++)
                {
                    server.RecordConsecutiveFailure();
                }
            }))
            .ToArray();

        foreach (var worker in workers)
        {
            worker.Start();
        }

        // Act
        startGate.Set();
        foreach (var worker in workers)
        {
            worker.Join();
        }

        // Assert
        Assert.Equal(workerCount * failuresPerWorker, server.Diagnostics.ConsecutiveFailures);
    }

    /// <summary>
    /// The application instance is built outside the restart loop's own try, so a failure there leaves
    /// the pump rather than being retried. It is the cheapest reachable failure that pins the
    /// diagnostics to the connector's own metrics.
    /// </summary>
    [Fact]
    public async Task WhenTheServerCannotBuildItsApplication_ThenTheFailureReachesItsOwnDiagnostics()
    {
        // Arrange
        using var server = CreateServer(new FailingOpcUaServerConfiguration());

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.StartAsync(CancellationToken.None));

        // Assert
        Assert.IsType<InvalidOperationException>(server.Diagnostics.LastError);
        Assert.NotNull(server.Diagnostics.StartTime);
        Assert.False(server.Diagnostics.IsOperational);
    }

    /// <summary>
    /// The restart loop builds a new change queue processor per attempt and registers it against a
    /// QueueMetrics that permits one live registration at a time, so a missing release would make the
    /// second attempt fail on the registration rather than on the transport. <c>LastError</c> is the
    /// only place that distinguishes the two.
    /// </summary>
    [Trait("Category", "Integration")]
    [Fact]
    public async Task WhenAStartAttemptFails_ThenTheNextAttemptCanRegisterItsOwnChangeQueue()
    {
        // Arrange: the certificate check is the first failure point inside the loop's own try, so the
        // attempt gets far enough to have registered its processor before it fails.
        using var server = CreateServer(
            new UncheckableCertificateOpcUaServerConfiguration { CleanCertificateStore = false });

        try
        {
            await server.StartAsync(CancellationToken.None);

            // Act
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.ConsecutiveFailures >= 2,
                message: "The server should have retried its failing start at least once.");

            // Assert
            Assert.NotNull(server.Diagnostics.LastError);
            Assert.DoesNotContain(
                "registration is already live",
                server.Diagnostics.LastError!.Message,
                StringComparison.Ordinal);
            Assert.False(server.Diagnostics.IsOperational);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static OpcUaSubjectServer CreateServer(OpcUaServerConfiguration? configuration = null)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        return new OpcUaSubjectServer(
            new DeliveryRuleTestRoot(context),
            configuration ?? new OpcUaServerConfiguration(),
            NullLogger.Instance);
    }

    private sealed class FailingOpcUaServerConfiguration : OpcUaServerConfiguration
    {
        public override Task<ApplicationInstance> CreateApplicationInstanceAsync() =>
            throw new InvalidOperationException("The OPC UA application instance could not be created.");
    }

    private sealed class UncheckableCertificateOpcUaServerConfiguration : OpcUaServerConfiguration
    {
        public override async Task<ApplicationInstance> CreateApplicationInstanceAsync()
        {
            var application = await base.CreateApplicationInstanceAsync().ConfigureAwait(false);

            // Without a configuration the certificate check fails immediately, without touching the
            // certificate stores or a port.
            application.ApplicationConfiguration = null!;
            return application;
        }
    }
}
