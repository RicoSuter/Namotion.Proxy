using System;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Client;
using Namotion.Interceptor.WebSocket.Internal;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Client;

/// <summary>
/// The client reports liveness from two places: the accepted handshake and the exit of the receive
/// loop that the handshake starts. Neither is observable without a real server.
/// </summary>
[Trait("Category", "Integration")]
public class WebSocketClientLivenessTests
{
    private readonly ITestOutputHelper _output;

    public WebSocketClientLivenessTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenTheClientConnects_ThenItReportsOperationalUntilItStops()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        await using var source = CreateClientSource(portLease.Port);

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.IsOperational == true,
            message: "The client should report operational once the handshake is accepted.");

        // Assert
        Assert.NotNull(source.Diagnostics.OperationalChangeTime);
        Assert.NotNull(source.Diagnostics.StartTime);

        // Act
        await source.StopAsync(CancellationToken.None);

        // Assert
        Assert.False(source.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenTheConnectionDrops_ThenLivenessFallsAndRisesAgainOnReconnect()
    {
        // Arrange - a reconnect delay far longer than the poll interval below, so the client cannot
        // be back before the drop has been observed.
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        await using var source = CreateClientSource(portLease.Port, reconnectDelay: TimeSpan.FromSeconds(3));

        await source.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == true,
                message: "The client should report operational once the handshake is accepted.");

            var connectedAt = source.Diagnostics.OperationalChangeTime;

            // Act - Disconnect is the soft fault: it aborts the socket without stopping the connector,
            // so the monitor loop reconnects to the still-running server.
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == false,
                message: "A client whose receive loop has exited should stop reporting that it is serving.");

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == true,
                message: "The client should report operational again once it has reconnected.");

            // The rise is a second transition rather than the first one never having been dropped.
            Assert.True(source.Diagnostics.OperationalChangeTime > connectedAt);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheClientIsKilled_ThenLivenessFallsAndRisesOnAReplacementAttempt()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        await using var source = CreateClientSource(portLease.Port);
        await source.StartAsync(CancellationToken.None);

        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.IsOperational == true);
            var connectedAt = source.Diagnostics.OperationalChangeTime;

            // Act
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);

            // Assert - the transition timestamp only moves when the operational flag flips, so an
            // operational client with a newer timestamp proves the liveness fell and rose again even
            // when both transitions happen between two polls.
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == true && source.Diagnostics.OperationalChangeTime > connectedAt,
                message: "The kill should drop liveness and a replacement attempt should raise it again.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenOrdinaryReloadIsKilled_ThenForceKillStartsReplacementAttempt()
    {
        // Arrange - the reconnect delay leaves a wide window between the observed drop and the
        // reconnect, so the value below is staged while the client is provably offline and can only
        // arrive through the reconnect's initial-state reload.
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        var reloadGate = new ReconnectReloadGate();
        await using var source = CreateClientSource(
            portLease.Port, reconnectDelay: TimeSpan.FromSeconds(2), writeInterceptor: reloadGate);
        await source.StartAsync(CancellationToken.None);
        var clientRoot = (TestRoot)source.RootSubject;
        await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.IsOperational == true);

        try
        {
            // Act
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.IsOperational == false);
            reloadGate.Arm();
            server.Root!.Name = "Reload";
            await reloadGate.ReloadStarted.WaitAsync(TimeSpan.FromSeconds(10));
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);
            reloadGate.Release();
            server.Root.Name = "AfterKill";

            // Assert - the killed reload's connection is gone, so only a working replacement
            // connection can deliver the value staged after the kill.
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientRoot.Name == "AfterKill",
                timeout: TimeSpan.FromSeconds(15),
                message: "A force-kill during an ordinary reconnect's reload should start a working replacement attempt.");
        }
        finally
        {
            reloadGate.Release();
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenPreviousReceiveLoopTimesOutBeforeUpdateAdmission_ThenLateUpdateCannotOverwriteReplacementWelcome()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        using var admissionGate = new UpdateAdmissionGate();
        await using var source = CreateClientSource(portLease.Port);
        await source.StartAsync(CancellationToken.None);

        var clientRoot = (TestRoot)source.RootSubject;
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.IsOperational == true && clientRoot.Name == "Initial");

        var oldLoopCompletion = GetReceiveLoopCompletion(source);
        source.BeforeUpdateCommitAdmission = admissionGate.Wait;
        admissionGate.Arm();

        try
        {
            server.Root!.Name = "Old";
            await admissionGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            server.Root.Name = "Recovered";

            // Act - the reconnect's join with the paused old loop genuinely times out here, which is
            // the production path this scenario is about.
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientRoot.Name == "Recovered",
                timeout: TimeSpan.FromSeconds(20),
                message: "The replacement Welcome should load while the retired old loop is still paused.");
            admissionGate.Release();
            await oldLoopCompletion.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.Equal("Recovered", clientRoot.Name);
        }
        finally
        {
            admissionGate.Release();
            source.BeforeUpdateCommitAdmission = null;
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenOldReceiveLoopCommitIsAdmittedBeforeReplacement_ThenWelcomeCommitsLast()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        using var commitGate = new GatedWriteInterceptor("Old");
        await using var source = CreateClientSource(portLease.Port, writeInterceptor: commitGate);
        await source.StartAsync(CancellationToken.None);

        var clientRoot = (TestRoot)source.RootSubject;
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.IsOperational == true && clientRoot.Name == "Initial");

        try
        {
            server.Root!.Name = "Old";
            await commitGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            server.Root.Name = "Recovered";
            await GetReceiveCancellation(source).CancelAsync();

            // Samples, at the instant the replacement's drain releases it, whether the commit the
            // drain was supposed to wait for had already been applied. A replacement that skipped the
            // drain reaches this while the gate still holds the commit, so it samples false.
            var drainWaitedForCommit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.AfterReceiveLoopCommitDrain = () => drainWaitedForCommit.TrySetResult(commitGate.BlockedWriteCompleted);

            // Act
            var replacementTask = (Task<TimeSpan>)typeof(WebSocketSubjectClientSource)
                .GetMethod("ReconnectAndResumeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(source,
                [
                    "WebSocket reconnected during test",
                    0, // the resume gate is not what this test exercises, so an epoch that owns nothing is fine
                    TimeSpan.FromMilliseconds(200),
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None
                ])!;

            // The lease field is cleared right when retirement starts, so this is the point from which
            // the drain either holds the replacement back or does not.
            await AsyncTestHelpers.WaitUntilAsync(
                () => GetReceiveLoopCommitLease(source) is null,
                message: "The replacement should retire the old loop's lease before anything else.");
            Assert.False(replacementTask.IsCompleted);
            Assert.NotEqual("Recovered", clientRoot.Name);

            commitGate.Release();
            await replacementTask.WaitAsync(TimeSpan.FromSeconds(15));

            // Assert - the reload applies the Welcome before the replacement task completes, so a
            // final value other than the Welcome's would mean the old commit landed after it.
            Assert.True(
                await drainWaitedForCommit.Task.WaitAsync(TimeSpan.FromSeconds(10)),
                "The replacement should stay in the drain until the admitted commit has been applied.");
            Assert.Equal("Recovered", clientRoot.Name);
        }
        finally
        {
            commitGate.Release();
            source.AfterReceiveLoopCommitDrain = null;
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenShutdownTimesOutBeforeUpdateAdmission_ThenLateUpdateIsRejected()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        using var admissionGate = new UpdateAdmissionGate();
        await using var source = CreateClientSource(portLease.Port);
        await source.StartAsync(CancellationToken.None);

        var clientRoot = (TestRoot)source.RootSubject;
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.IsOperational == true && clientRoot.Name == "Initial");

        var oldLoopCompletion = GetReceiveLoopCompletion(source);
        source.BeforeUpdateCommitAdmission = admissionGate.Wait;
        admissionGate.Arm();
        server.Root!.Name = "Old";
        await admissionGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        Task? stopTask = null;
        try
        {
            // Act - the stop's join with the paused old loop genuinely times out here, which is the
            // production path this scenario is about.
            stopTask = source.StopAsync(CancellationToken.None);
            await stopTask.WaitAsync(TimeSpan.FromSeconds(20));
            admissionGate.Release();
            await oldLoopCompletion.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.Equal("Initial", clientRoot.Name);
        }
        finally
        {
            admissionGate.Release();
            if (stopTask is not null)
            {
                await stopTask.WaitAsync(TimeSpan.FromSeconds(20));
            }
            source.BeforeUpdateCommitAdmission = null;
        }
    }

    [Fact]
    public async Task WhenShutdownRetiresAnAdmittedCommit_ThenStopWaitsForThatCommit()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        using var commitGate = new GatedWriteInterceptor("Old");
        await using var source = CreateClientSource(portLease.Port, writeInterceptor: commitGate);
        await source.StartAsync(CancellationToken.None);

        var clientRoot = (TestRoot)source.RootSubject;
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.IsOperational == true && clientRoot.Name == "Initial");

        server.Root!.Name = "Old";
        await commitGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await GetReceiveCancellation(source).CancelAsync();
        await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);

        // Samples, at the instant the shutdown's drain releases it, whether the commit the drain was
        // supposed to wait for had already been applied. A shutdown that skipped the drain reaches
        // this while the gate still holds the commit, so it samples false.
        var drainWaitedForCommit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.AfterReceiveLoopCommitDrain = () => drainWaitedForCommit.TrySetResult(commitGate.BlockedWriteCompleted);

        Task? cleanupTask = null;
        try
        {
            // Act
            cleanupTask = ((ValueTask)typeof(WebSocketSubjectClientSource)
                .GetMethod("DisposeWebSocketConnectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(source, null)!).AsTask();

            // The lease field is cleared right when retirement starts, so this is the point from which
            // the drain either holds the shutdown back or does not.
            await AsyncTestHelpers.WaitUntilAsync(
                () => GetReceiveLoopCommitLease(source) is null,
                message: "The shutdown should retire the receive loop's lease before anything else.");
            Assert.False(cleanupTask.IsCompleted);

            commitGate.Release();
            await cleanupTask.WaitAsync(TimeSpan.FromSeconds(15));

            // Assert
            Assert.True(
                await drainWaitedForCommit.Task.WaitAsync(TimeSpan.FromSeconds(10)),
                "The shutdown should stay in the drain until the admitted commit has been applied.");
            Assert.Equal("Old", clientRoot.Name);
        }
        finally
        {
            commitGate.Release();
            if (cleanupTask is not null)
            {
                await cleanupTask.WaitAsync(TimeSpan.FromSeconds(15));
            }
            source.AfterReceiveLoopCommitDrain = null;
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenForceKillReplacementWaitsForWelcomeAndIsKilledAgain_ThenAnotherAttemptStarts()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WelcomeStallingServer();
        await server.StartAsync(portLease.Port);
        await using var source = CreateClientSource(portLease.Port, reconnectDelay: TimeSpan.FromMilliseconds(20));
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.IsOperational == true);

        try
        {
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);
            await server.ReplacementWaitingForWelcome.WaitAsync(TimeSpan.FromSeconds(10));

            // Act
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);

            // Assert - a further connection reaching the server is the observable form of another
            // replacement attempt.
            await server.SecondReplacementConnected.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAnOldReceiveLoopFinishes_ThenItCannotLowerReplacementLiveness()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        await using var source = CreateClientSource(portLease.Port);
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.IsOperational == true);

        var oldCompletion = GetReceiveLoopCompletion(source);
        var receiveCts = GetReceiveCancellation(source);
        var replacementCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetReceiveLoopCompletion(source, replacementCompletion);
        PublishReceiveLoopAndMarkOperational(source, replacementCompletion);

        try
        {
            // Act
            await receiveCts.CancelAsync();
            await oldCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.True(source.Diagnostics.IsOperational);
        }
        finally
        {
            replacementCompletion.TrySetResult();
        }
    }

    [Fact]
    public async Task WhenAReplacementAttemptFails_ThenMonitorRetriesUntilTheServerReturns()
    {
        // Arrange - the circuit breaker is disabled so the repeated genuine connection failures
        // below exercise the monitor's own retry loop rather than the breaker's cooldown.
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        await using var source = CreateClientSource(
            portLease.Port,
            reconnectDelay: TimeSpan.FromMilliseconds(100),
            circuitBreakerFailureThreshold: 0);

        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.IsOperational == true);

        try
        {
            // Act - stopping the server fails every reconnect attempt for real until it returns.
            await server.StopAsync();
            await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.IsOperational == false);
            await server.RestartAsync();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == true,
                timeout: TimeSpan.FromSeconds(15),
                message: "The monitor should keep retrying failed replacement attempts until one succeeds.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAnOldReceiveLoopIsPreemptedBeforeLoweringLiveness_ThenItCannotOverrideTheReplacement()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        await using var source = CreateClientSource(portLease.Port);
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.IsOperational == true);

        var oldCompletion = GetReceiveLoopCompletion(source);
        var receiveCts = GetReceiveCancellation(source);
        var replacementCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldLoopReachedLivenessTransition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOldLoopToContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementPublicationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.BeforeReceiveLoopLivenessTransition = () =>
        {
            oldLoopReachedLivenessTransition.TrySetResult();
            allowOldLoopToContinue.Task.GetAwaiter().GetResult();
        };
        source.BeforeReceiveLoopPublication = () => replacementPublicationStarted.TrySetResult();

        try
        {
            // Act
            var cancellationTask = receiveCts.CancelAsync();
            await oldLoopReachedLivenessTransition.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replacementPublicationTask = Task.Run(() =>
            {
                SetReceiveLoopCompletion(source, replacementCompletion);
                PublishReceiveLoopAndMarkOperational(source, replacementCompletion);
            });
            await replacementPublicationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            allowOldLoopToContinue.TrySetResult();
            await cancellationTask;
            await replacementPublicationTask;
            await oldCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.True(source.Diagnostics.IsOperational);
        }
        finally
        {
            allowOldLoopToContinue.TrySetResult();
            source.BeforeReceiveLoopLivenessTransition = null;
            source.BeforeReceiveLoopPublication = null;
            replacementCompletion.TrySetResult();
        }
    }

    [Fact]
    public async Task WhenAWriteHappensAfterReconnectButBeforeLoad_ThenItParksUntilTheLoadAndReconcileComplete()
    {
        // Arrange - design document case D4: the reconnect's socket is writable, and its receive loop
        // is already running, before the load has applied the Welcome and the reconcile has judged the
        // retry queue against it.
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        await using var source = CreateClientSource(portLease.Port, reconnectDelay: TimeSpan.FromMilliseconds(50));
        await source.StartAsync(CancellationToken.None);

        var clientRoot = (TestRoot)source.RootSubject;
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.IsOperational == true && clientRoot.Name == "Initial");

        var loadGate = new ReconnectLoadGate();
        source.BeforeReconnectInitialStateLoad = loadGate.Wait;

        try
        {
            // Act - the connection drops and the monitor loop reconnects for real; the seam blocks the
            // reconnect right after the socket becomes writable and before the load runs.
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);
            await loadGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            clientRoot.Name = "WrittenBeforeLoad";

            // Assert - the write parks rather than reaching the socket. The gate branch never attempts
            // a send once it takes that path, so observing the park is proof the write did not go out
            // while the reconnect had not yet loaded and reconciled.
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > 0,
                message: "The write should have parked while the reconnect had not yet loaded and reconciled.");
            Assert.Equal("Initial", server.Root!.Name);

            loadGate.Release();

            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Root!.Name == "WrittenBeforeLoad",
                timeout: TimeSpan.FromSeconds(10),
                message: "The write should reach the server once the load and reconcile complete.");
        }
        finally
        {
            loadGate.Release();
            source.BeforeReconnectInitialStateLoad = null;
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAWriteHappensAfterAForceKilledReconnectButBeforeLoad_ThenItParksUntilTheLoadAndReconcileComplete()
    {
        // Arrange - design document case D4, through the other of the two BeginResume() call sites: the
        // WasForceKilled catch arm rather than drop detection. Both routes lead into
        // ReconnectAndResumeAsync and through the same BeforeReconnectInitialStateLoad seam, so a fix
        // that only guards the drop-detection route would leave this one open.
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        await using var source = CreateClientSource(portLease.Port, reconnectDelay: TimeSpan.FromMilliseconds(50));
        await source.StartAsync(CancellationToken.None);

        var clientRoot = (TestRoot)source.RootSubject;
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.IsOperational == true && clientRoot.Name == "Initial");

        var loadGate = new ReconnectLoadGate();
        source.BeforeReconnectInitialStateLoad = loadGate.Wait;

        try
        {
            // Act - a force-kill reaches the seam through the WasForceKilled catch arm's own
            // BeginResume() and the forceReconnect branch's call to ReconnectAndResumeAsync.
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);
            await loadGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            clientRoot.Name = "WrittenBeforeLoad";

            // Assert - the write parks rather than reaching the socket, the same as the drop-detection
            // route.
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > 0,
                message: "The write should have parked while the force-killed reconnect had not yet loaded and reconciled.");
            Assert.Equal("Initial", server.Root!.Name);

            loadGate.Release();

            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Root!.Name == "WrittenBeforeLoad",
                timeout: TimeSpan.FromSeconds(10),
                message: "The write should reach the server once the load and reconcile complete.");
        }
        finally
        {
            loadGate.Release();
            source.BeforeReconnectInitialStateLoad = null;
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenReconnectConnectsButLoadingFails_ThenTheGateIsClearedRatherThanStuck()
    {
        // Arrange - the reconnect connects for real (fresh socket, fresh Welcome) but its own load
        // then throws before the reconcile runs. The socket is left open by design: the receive loop
        // already started inside ConnectAsync, so the monitor loop's next iteration just waits on the
        // still-open connection instead of retrying, which is why nothing else in production clears
        // the gate this attempt opened.
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        var loadFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = CreateClientSource(
            portLease.Port, reconnectDelay: TimeSpan.FromMilliseconds(50));
        await source.StartAsync(CancellationToken.None);

        var clientRoot = (TestRoot)source.RootSubject;
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.IsOperational == true && clientRoot.Name == "Initial");

        try
        {
            // Act - a real disconnect and reconnect, with the load failing on the reconnect's own
            // Welcome apply.
            // Thrown from the seam that fires once the reconnect holds a live connection and before it
            // loads, which is inside the window the gate covers: the gate is up, the connect succeeded,
            // and the failure lands in the same catch that has to clear it.
            source.BeforeReconnectInitialStateLoad = () =>
            {
                source.BeforeReconnectInitialStateLoad = null;
                loadFailed.TrySetResult();
                throw new InvalidOperationException("Injected load failure.");
            };

            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);
            await loadFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            clientRoot.Name = "AfterFailedLoad";

            // Assert - a write made after the failed reconnect reaches the source, proving the gate
            // that attempt opened did not survive its own failure.
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Root!.Name == "AfterFailedLoad",
                timeout: TimeSpan.FromSeconds(10),
                message: "A write after the failed reconnect should reach the source once the gate is cleared.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    private async Task<WebSocketTestServer<TestRoot>> StartServerAsync(int port)
    {
        var server = new WebSocketTestServer<TestRoot>(_output);
        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: port);
        return server;
    }

    private static WebSocketSubjectClientSource CreateClientSource(
        int port,
        TimeSpan? reconnectDelay = null,
        IWriteInterceptor? writeInterceptor = null,
        int? circuitBreakerFailureThreshold = null)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

        if (writeInterceptor is not null)
        {
            context.AddService(writeInterceptor);
        }

        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri($"ws://localhost:{port}/ws"),
            ReconnectDelay = reconnectDelay ?? TimeSpan.FromMilliseconds(200),
            MaxReconnectDelay = TimeSpan.FromSeconds(10)
        };

        if (circuitBreakerFailureThreshold is { } threshold)
        {
            configuration.CircuitBreakerFailureThreshold = threshold;
        }

        return new WebSocketSubjectClientSource(
            new TestRoot(context),
            configuration,
            NullLogger<WebSocketSubjectClientSource>.Instance);
    }

    /// <summary>
    /// Blocks inside <see cref="WebSocketSubjectClientSource.BeforeReconnectInitialStateLoad"/>, the
    /// window between a reconnect's socket becoming writable and its load applying the Welcome.
    /// </summary>
    private sealed class ReconnectLoadGate
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the reconnect has reached the window this gate blocks.</summary>
        public Task Entered => _entered.Task;

        public void Wait()
        {
            _entered.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class ReconnectReloadGate : IWriteInterceptor
    {
        private readonly TaskCompletionSource _reloadStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowReload =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _blocked;

        public Task ReloadStarted => _reloadStarted.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _allowReload.TrySetResult();

        public void WriteProperty<TProperty>(
            ref PropertyWriteContext<TProperty> context,
            WriteInterceptionDelegate<TProperty> next)
        {
            if (Volatile.Read(ref _armed) == 1 &&
                context.Property.Name == nameof(TestRoot.Name) &&
                context.NewValue is "Reload" &&
                Interlocked.CompareExchange(ref _blocked, 1, 0) == 0)
            {
                _reloadStarted.TrySetResult();
                _allowReload.Task.GetAwaiter().GetResult();
            }

            next(ref context);
        }
    }

    private sealed class UpdateAdmissionGate : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _blocked;

        public Task Entered => _entered.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Wait()
        {
            if (Volatile.Read(ref _armed) == 1 &&
                Interlocked.CompareExchange(ref _blocked, 1, 0) == 0)
            {
                _entered.TrySetResult();
                _release.Wait();
            }
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }

    private sealed class GatedWriteInterceptor(string blockedValue) : IWriteInterceptor, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked;
        private int _blockedWriteCompleted;

        public Task Entered => _entered.Task;

        /// <summary>
        /// Set once the blocked write has actually been applied, which happens before the commit that
        /// carries it is released, so a drain that observes this as <c>false</c> did not wait for it.
        /// </summary>
        public bool BlockedWriteCompleted => Volatile.Read(ref _blockedWriteCompleted) == 1;

        public void WriteProperty<TProperty>(
            ref PropertyWriteContext<TProperty> context,
            WriteInterceptionDelegate<TProperty> next)
        {
            if (Equals(context.NewValue, blockedValue) &&
                Interlocked.CompareExchange(ref _blocked, 1, 0) == 0)
            {
                _entered.TrySetResult();
                _release.Wait();
                next(ref context);
                Volatile.Write(ref _blockedWriteCompleted, 1);
                return;
            }

            next(ref context);
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }

    private sealed class WelcomeStallingServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly TaskCompletionSource _replacementWaitingForWelcome =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondReplacementConnected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebApplication? _application;
        private int _connectionCount;

        public Task ReplacementWaitingForWelcome => _replacementWaitingForWelcome.Task;

        public Task SecondReplacementConnected => _secondReplacementConnected.Task;

        public async Task StartAsync(int port)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

            var application = builder.Build();
            application.UseWebSockets();
            application.Map("/ws", HandleConnectionAsync);
            await application.StartAsync();
            _application = application;
        }

        private async Task HandleConnectionAsync(Microsoft.AspNetCore.Http.HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var connectionNumber = Interlocked.Increment(ref _connectionCount);

            try
            {
                using var hello = await WebSocketMessageReader.ReadMessageAsync(
                    webSocket,
                    64 * 1024,
                    _stopping.Token);
                if (!hello.Success)
                {
                    return;
                }

                if (connectionNumber == 1)
                {
                    var welcome = new WelcomePayload { Sequence = 0 };
                    var bytes = JsonWebSocketSerializer.Instance.SerializeMessage(MessageType.Welcome, welcome);
                    await webSocket.SendAsync(
                        bytes,
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        _stopping.Token);
                }
                else if (connectionNumber == 2)
                {
                    _replacementWaitingForWelcome.TrySetResult();
                }
                else if (connectionNumber == 3)
                {
                    _secondReplacementConnected.TrySetResult();
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, _stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            if (_application is not null)
            {
                await _application.StopAsync();
                await _application.DisposeAsync();
            }
            _stopping.Dispose();
        }
    }

    private static CancellationTokenSource GetReceiveCancellation(WebSocketSubjectClientSource source) =>
        (CancellationTokenSource)typeof(WebSocketSubjectClientSource)
            .GetField("_receiveCts", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(source)!;

    private static object? GetReceiveLoopCommitLease(WebSocketSubjectClientSource source) =>
        typeof(WebSocketSubjectClientSource)
            .GetField("_receiveLoopCommitLease", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(source);

    private static TaskCompletionSource GetReceiveLoopCompletion(WebSocketSubjectClientSource source) =>
        (TaskCompletionSource)typeof(WebSocketSubjectClientSource)
            .GetField("_receiveLoopCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(source)!;

    private static void SetReceiveLoopCompletion(
        WebSocketSubjectClientSource source,
        TaskCompletionSource completion) =>
        typeof(WebSocketSubjectClientSource)
            .GetField("_receiveLoopCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(source, completion);

    private static void PublishReceiveLoopAndMarkOperational(
        WebSocketSubjectClientSource source,
        TaskCompletionSource completion) =>
        typeof(WebSocketSubjectClientSource)
            .GetMethod("PublishReceiveLoopAndMarkOperational", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(source, [completion]);
}
