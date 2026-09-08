using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Verifies that an outage during the SDK's own auto-reconnect window (triggered from
/// SessionManager.OnKeepAlive, before any manual reconnection takes over) is visible on
/// <see cref="ISubjectSource.State"/> instead of leaving the source reporting Synchronized
/// throughout the whole outage.
/// </summary>
[Trait("Category", "Integration")]
public class OutageStateTests
{
    private readonly ITestOutputHelper _output;

    public OutageStateTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenTheConnectionIsLost_ThenTheSourceReportsSynchronizingUntilItRecovers()
    {
        // Arrange
        var logger = new TestLogger(_output);
        using var port = await OpcUaTestPortPool.AcquireAsync();

        await using var server = new OpcUaTestServer<TestRoot>(logger);
        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) =>
            {
                root.Connected = true;
                root.Name = "Initial";
            },
            baseAddress: port.BaseAddress,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddXunit(logger, "Client", LogLevel.Information);
        });

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithSourceTransactions()
            .WithSourceMonitoring();

        var root = new TestRoot(context);

        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = port.ServerUrl,
            RootPath = ["Root"],
            TypeResolver = new OpcUaTypeResolver(loggerFactory.CreateLogger<OpcUaTypeResolver>()),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),

            // Fast detection/recovery so the test observes the outage window without waiting minutes.
            // SubscriptionHealthCheckInterval is set well above KeepAliveInterval on purpose: the health
            // check loop has its own independent dead-session detection (HandleDeadSessionAsync), and a
            // wide margin makes it overwhelmingly unlikely for that path to race ahead of OnKeepAlive and
            // win, which would leave the SDK auto-reconnect window this test exists to cover unexercised.
            ReconnectInterval = TimeSpan.FromSeconds(1),
            ReconnectHandlerTimeout = TimeSpan.FromSeconds(5),
            MaxReconnectDuration = TimeSpan.FromSeconds(20),
            SubscriptionHealthCheckInterval = TimeSpan.FromSeconds(10),

            SessionTimeout = TimeSpan.FromSeconds(30),
            KeepAliveInterval = TimeSpan.FromSeconds(1),
            OperationTimeout = TimeSpan.FromSeconds(30),

            BufferTime = TimeSpan.FromMilliseconds(100),

            CertificateStoreBasePath = port.CertificateStoreBasePath
        };

        await using var source = new OpcUaSubjectClientSource(root, configuration, loggerFactory.CreateLogger<OpcUaSubjectClientSource>());

        // How far reconnection had got when the source first reported the outage. Read inside the
        // transition lock, which is what makes it answer "at that moment" rather than "by the time the
        // test got around to looking". Subscribed ahead of the recorder so that a transition visible in
        // the recording has already been counted here.
        var reconnectsCompletedWhenOutageReported = -1L;
        void CaptureReconnectProgress(object? sender, SourceEvent transition)
        {
            if (transition is { OldState: SourceState.Synchronized, NewState: SourceState.Synchronizing } &&
                Volatile.Read(ref reconnectsCompletedWhenOutageReported) < 0)
            {
                Volatile.Write(ref reconnectsCompletedWhenOutageReported, source.Diagnostics.Reconnects.TotalSucceeded);
            }
        }

        source.StateChanged += CaptureReconnectProgress;

        // Subscribed before anything can transition the source: the outage is asserted from the
        // recorded transitions, because the Synchronizing window can be shorter than the interval at
        // which a test can sample the current state.
        var stateRecorder = SourceStateRecorder.SubscribeTo(source);

        try
        {
            await source.StartAsync(CancellationToken.None);

            await stateRecorder.WaitForStatesAsync(
                TimeSpan.FromSeconds(60),
                "Initial sync should complete.",
                SourceState.Synchronized);

            // Act - Disconnect is the soft fault: it breaks the transport without stopping the
            // connector, which is exactly what a real network blip does. It kills the transport
            // channel while the session stays assigned, so OnKeepAlive sees the bad status and
            // hands off to the SDK's SessionReconnectHandler.BeginReconnect - the code path this
            // test exists to cover.
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);

            // Assert
            await stateRecorder.WaitForStatesAsync(
                TimeSpan.FromSeconds(30),
                "Losing the transport should have been reported as an outage instead of the source staying Synchronized.",
                SourceState.Synchronized, SourceState.Synchronizing);

            // Without this, the pair of waits would pass vacuously even without the fix:
            // PerformFullStateSyncIfNeededAsync buffers briefly once the SDK has finished reconnecting on
            // its own, so Synchronizing would flash by right before Synchronized whether or not
            // OnKeepAlive reported the loss up front. Only the report made at detection happens while no
            // reconnection has completed yet.
            var reconnectsCompleted = Volatile.Read(ref reconnectsCompletedWhenOutageReported);
            Assert.True(reconnectsCompleted == 0,
                $"The outage should have been reported when the connection was lost, but {reconnectsCompleted} " +
                $"reconnection(s) had already completed by then. Recorded transitions: {stateRecorder}.");

            var outage = await stateRecorder.WaitForStatesAsync(
                TimeSpan.FromSeconds(60),
                "Source should recover to Synchronized after the SDK reconnects.",
                SourceState.Synchronized, SourceState.Synchronizing, SourceState.Synchronized);

            // Each transition carries the timestamp that ISubjectSource.StateChangeTime was set to,
            // so these compare the moments themselves rather than whatever the property reads back as
            // once the outage is over.
            var firstSynchronizedAt = outage[0].Timestamp;
            var outageDetectedAt = outage[1].Timestamp;
            var recoveredAt = outage[2].Timestamp;

            Assert.True(outageDetectedAt > firstSynchronizedAt,
                "Losing synchronization should have moved StateChangeTime past the initial sync, but the " +
                $"recorded transitions were: {stateRecorder}.");

            // Against the outage moment, not the initial sync: the timestamp only ever advances, so
            // comparing against firstSynchronizedAt again could not fail.
            Assert.True(recoveredAt > outageDetectedAt,
                "Recovering should have moved StateChangeTime past the moment the outage was detected, but the " +
                $"recorded transitions were: {stateRecorder}.");
        }
        finally
        {
            stateRecorder.Dispose();
            source.StateChanged -= CaptureReconnectProgress;
            await source.StopAsync(CancellationToken.None);
        }
    }
}
