using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace HomeBlaze.OpcUa.Tests;

public class OpcUaClientTests
{
    private const string NotAttachedMessage = "Not attached to a running host, so nothing was started";
    private const string FactoryFailureMessage = "The factory could not build a client source.";

    private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A diagnostics poll fast enough to observe. It is the reconciliation the poll performs, rather
    /// than the loop itself, that several of the states below are only reachable through, and the
    /// production interval of ten seconds puts all of them out of reach of a suite that runs in seconds.
    /// </summary>
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// How long the poll is observed not to have written, which is twenty five of its rounds. See the
    /// tests that use it for why the observation cannot false fail.
    /// </summary>
    private static readonly TimeSpan PollObservation = TimeSpan.FromMilliseconds(500);

    /// <summary>A diagnostics value no reconciliation produces, so the poll replacing it is visible.</summary>
    private const int SentinelItemCount = -1;

    [Fact]
    public async Task WhenTheClientIsStartedStoppedAndRestarted_ThenTheAttachmentAndTheRootFollowIt()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient();

        // Act
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForRunningClientAsync(client);

        // Assert
        var firstAttachment = Assert.Single(client.GetHostedServiceAttachments());
        var firstRoot = client.Root;
        Assert.NotNull(firstAttachment.Current);
        Assert.NotNull(firstRoot);
        Assert.NotNull(client.PendingWriteCount);
        Assert.NotNull(client.TotalReconnections);
        Assert.Null(client.StatusMessage);
        Assert.True(client.Stop_IsEnabled);

        // Act
        await client.StopAsync();

        // Assert
        Assert.Equal(ServiceStatus.Stopped, client.Status);
        Assert.Null(client.StatusMessage);
        Assert.False(client.IsEnabled);
        Assert.Empty(client.GetHostedServiceAttachments());
        Assert.Null(client.Root);
        Assert.Null(client.PendingWriteCount);
        Assert.Null(client.TotalReconnections);
        Assert.True(client.Start_IsEnabled);

        // Act
        await client.StartAsync();

        // Assert
        Assert.Equal(ServiceStatus.Running, client.Status);
        Assert.True(client.IsEnabled);
        var secondAttachment = Assert.Single(client.GetHostedServiceAttachments());
        Assert.NotSame(firstAttachment, secondAttachment);
        Assert.NotNull(client.Root);
        Assert.NotSame(firstRoot, client.Root);
    }

    [Fact]
    public async Task WhenTheSubjectLeavesTheGraph_ThenTheUnwindKeepsTheAttachment()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient();
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForStatusAsync(() => client.Status, ServiceStatus.Running);

        var attachment = Assert.Single(client.GetHostedServiceAttachments());
        var root = client.Root;

        // Act
        testHost.Container.Client = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () => attachment.Current is null && client.Status == ServiceStatus.Stopped,
            message: "The instance was not stopped, or the wrapper did not report the stop.");

        // Assert
        // Detaching from the unwind would wedge the subject's own stop behind the attachment stop that
        // waits for it, so this test would hang rather than fail. Reaching the assertions is half of
        // what it pins; the attachment still being there is the other half.
        Assert.Same(attachment, Assert.Single(client.GetHostedServiceAttachments()));
        Assert.Null(attachment.Fault);

        // The tree the stopped source filled is deliberately left in place, because the unwind runs
        // while that source is still live. It is dropped by the next reconciliation instead.
        Assert.Same(root, client.Root);
    }

    [Fact]
    public async Task WhenTheSubjectLeavesTheGraph_ThenTheHandlerDisposesTheSourceRatherThanTheWrapper()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync(context => context.WithSourceMonitoring());
        var monitor = testHost.Context.GetSourceMonitor();

        var client = testHost.CreateClient();
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForStatusAsync(() => client.Status, ServiceStatus.Running);

        var attachment = Assert.Single(client.GetHostedServiceAttachments());
        var source = Assert.IsAssignableFrom<ISubjectSource>(attachment.Current);
        Assert.Contains(source, monitor.Sources);

        // Act
        testHost.Container.Client = null;

        // Assert
        // A source unregisters from the monitor in Dispose and nowhere else, so this is the one
        // externally visible difference between the handler having stopped the instance and the handler
        // having disposed it. The wrapper never sees this detach: it owns no path that could dispose it.
        await AsyncTestHelpers.WaitUntilAsync(
            () => !monitor.Sources.Contains(source),
            message: "The handler stopped the source but never disposed it.");

        Assert.Equal(SourceState.Stopped, source.State);
        Assert.Same(attachment, Assert.Single(client.GetHostedServiceAttachments()));
    }

    [Fact]
    public async Task WhenTheInstanceIsReplacedByAFault_ThenTheDiagnosticsAndTheRootAreCleared()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartReAttachableAsync();
        var client = testHost.CreateClient();
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForRunningClientAsync(client);

        var attachment = Assert.Single(client.GetHostedServiceAttachments());
        Assert.NotNull(client.PendingWriteCount);

        await ReAttachWithAFailingFactoryAsync(testHost, client, attachment);

        // Put back by hand, because the unwind and the reconciliation between them leave all of this
        // clear on every graph path into a fault. What this pins is the fault branch dropping the tree
        // and the numbers in its own right rather than inheriting it from whatever ran first.
        client.Root = new OpcUaDynamicSubject("stale");
        client.IsConnected = true;
        client.IncomingChangesPerSecond = 1;
        client.OutgoingChangesPerSecond = 2;
        client.MonitoredItemCount = 3;
        client.PollingItemCount = 4;
        client.PendingWriteCount = 5;
        client.TotalReconnections = 6;

        // Act
        await client.StartAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, client.Status);
        Assert.Equal(FactoryFailureMessage, client.StatusMessage);
        Assert.Null(client.Root);
        Assert.Null(client.IsConnected);
        Assert.Null(client.IncomingChangesPerSecond);
        Assert.Null(client.OutgoingChangesPerSecond);
        Assert.Null(client.MonitoredItemCount);
        Assert.Null(client.PollingItemCount);
        Assert.Null(client.PendingWriteCount);
        Assert.Null(client.TotalReconnections);
    }

    [Fact(Skip = "Reproduces PR #440 follow-up item 1: a start cannot clear a fault and Error offers no stop.")]
    public async Task WhenAReAttachFaultedAndTheStartOperationIsInvoked_ThenTheWrapperIsNotStuckAtError()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartReAttachableAsync();
        var client = testHost.CreateClient();
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForStatusAsync(() => client.Status, ServiceStatus.Running);

        var attachment = Assert.Single(client.GetHostedServiceAttachments());
        await ReAttachWithAFailingFactoryAsync(testHost, client, attachment);

        await client.StartAsync();
        Assert.Equal(ServiceStatus.Error, client.Status);

        // Act
        // The one operation the UI offers at Error, since Stop_IsEnabled is false there.
        await client.StartAsync();

        // Assert
        // The start skips the attach while the faulted attachment is still held and only reconciles, so
        // it reads the same fault back. Either the start has to reach a working instance again or the
        // wrapper has to offer the stop that clears the attachment; what must not survive a fix is
        // Error with nothing that leaves it.
        Assert.True(
            client.Status != ServiceStatus.Error || client.Stop_IsEnabled,
            "The wrapper reports Error, offers Start, and the start cannot clear it.");
    }

    [Fact(Skip = "Reproduces PR #440 follow-up item 2: a reconciliation inside the re-attach window detaches the fresh root and fails the start.")]
    public async Task WhenAReconciliationLandsInsideAReAttach_ThenTheReAttachStillProducesARunningClient()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartReAttachableAsync();
        var client = testHost.CreateClient();
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForStatusAsync(() => client.Status, ServiceStatus.Running);

        var attachment = Assert.Single(client.GetHostedServiceAttachments());
        testHost.Container.Client = null;
        await AsyncTestHelpers.WaitUntilAsync(() => attachment.Current is null);

        // The factory publishes Root before the handler publishes Current, and the two run on different
        // chains, so the window between them is the wrapper's own state being briefly inconsistent.
        // Holding the factory there is what makes the reconciliation land inside it every run.
        using var rootWritten = new ManualResetEventSlim();
        using var releaseFactory = new ManualResetEventSlim();
        var held = 0;
        testHost.WriteSeam.ArmAfterWrite((property, value) =>
        {
            if (!ReferenceEquals(property.Subject, client) ||
                property.Name != nameof(OpcUaClient.Root) ||
                value is null ||
                Interlocked.Exchange(ref held, 1) == 1)
            {
                return;
            }

            rootWritten.Set();
            releaseFactory.Wait(HoldTimeout);
        });

        try
        {
            // Act
            testHost.Container.Client = client;
            Assert.True(rootWritten.Wait(HoldTimeout), "The re-attach never rebuilt the root.");

            // The start operation reconciles against the handle without attaching, which is what the
            // diagnostics poll does on its own timer. Standing in for it keeps the window deterministic.
            await client.StartAsync();
        }
        finally
        {
            releaseFactory.Set();
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => attachment.Current is not null || attachment.Fault is not null,
            message: "The held re-attach never finished.");

        await client.StartAsync();

        // Assert
        // The reconciliation does not merely lose the reference. Clearing Root takes the tree the
        // factory has just published back out of the graph, so the source built on it starts against a
        // detached subject and fails with a message about context configuration, which is the last
        // thing anyone reading it would look at.
        Assert.Null(attachment.Fault);
        Assert.Equal(ServiceStatus.Running, client.Status);
        Assert.NotNull(client.Root);
    }

    [Fact]
    public async Task WhenAWrapperThatNeverAttachedIsDisabled_ThenItReportsStopped()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient(serverUrl: null);
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForStatusAsync(() => client.Status, ServiceStatus.Error);
        Assert.Empty(client.GetHostedServiceAttachments());

        // Act
        client.IsEnabled = false;
        await client.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        // The guarded start is skipped now that the client is disabled, so this stop is the last thing
        // that writes the status. Whatever it leaves behind is what the client reports from here on.
        Assert.Equal(ServiceStatus.Stopped, client.Status);
        Assert.Null(client.StatusMessage);
    }

    [Fact]
    public async Task WhenAStartFaultedAfterPublishingItsTree_ThenDisablingTheClientDropsIt()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient();

        // The factory publishes the tree before it builds the source that fills it, so a build that
        // fails leaves a tree behind that nothing is filling. Failing the write once it has committed
        // is the only seam into that window.
        //
        // Armed before the client enters the graph, and the start left to the run loop rather than
        // invoked here: the Start operation enables the client, which is what the loop reads, so a
        // second start could run against a seam that has already spent itself and succeed.
        var failed = 0;
        testHost.WriteSeam.ArmAfterWrite((property, value) =>
        {
            if (!ReferenceEquals(property.Subject, client) ||
                property.Name != nameof(OpcUaClient.Root) ||
                value is null ||
                Interlocked.Exchange(ref failed, 1) == 1)
            {
                return;
            }

            testHost.WriteSeam.ArmAfterWrite(null);
            throw new InvalidOperationException(FactoryFailureMessage);
        });

        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForStatusAsync(() => client.Status, ServiceStatus.Error);

        // The awaited attach takes the attachment back out before it rethrows, so the wrapper holds
        // nothing and the stop below takes the branch that has nothing to detach.
        Assert.Empty(client.GetHostedServiceAttachments());
        Assert.NotNull(client.Root);

        // Act
        client.IsEnabled = false;
        await client.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Stopped, client.Status);
        Assert.Null(client.StatusMessage);
        Assert.Null(client.Root);
    }

    [Fact]
    public async Task WhenAFaultedClientStillHoldsItsAttachment_ThenDisablingItDropsTheErrorTextWithTheStatus()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartReAttachableAsync();
        var client = testHost.CreateClient(diagnosticsPollInterval: FastPoll);
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForStatusAsync(() => client.Status, ServiceStatus.Running);

        var attachment = Assert.Single(client.GetHostedServiceAttachments());

        // Disabled before the re-attach, so the run loop the re-attach restarts issues no start of its
        // own. The poll is then the only thing that writes, and it is gated against the stop below.
        client.IsEnabled = false;
        await ReAttachWithAFailingFactoryAsync(testHost, client, attachment);

        // The fault only reaches the wrapper through a reconciliation, which the stop the unwind reported
        // short circuits until something asks for a start again. Lifting it by hand rather than through
        // the Start operation, which would enable the client and hand the run loop a start.
        client.Status = ServiceStatus.Starting;
        await OpcUaTestHost.WaitForStatusAsync(() => client.Status, ServiceStatus.Error);
        Assert.Equal(FactoryFailureMessage, client.StatusMessage);
        Assert.Same(attachment, Assert.Single(client.GetHostedServiceAttachments()));

        // The stop publishes its status and then awaits the detach, so what stands beside that status is
        // what the client reports for the whole of that window. Read from the write, because nothing here
        // can hold the detach open to read it afterwards.
        var messageBesideStopping = default(string);
        var reachedStopping = 0;
        testHost.WriteSeam.ArmAfterWrite((property, value) =>
        {
            if (ReferenceEquals(property.Subject, client) &&
                property.Name == nameof(OpcUaClient.Status) &&
                value is ServiceStatus.Stopping &&
                Interlocked.Increment(ref reachedStopping) == 1)
            {
                messageBesideStopping = client.StatusMessage;
            }
        });

        // Act
        await client.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        // The message is the text behind Error and nothing else, so the status that leaves Error has to
        // leave it behind too rather than carry it across the detach.
        Assert.Equal(1, reachedStopping);
        Assert.Null(messageBesideStopping);

        Assert.Equal(ServiceStatus.Stopped, client.Status);
        Assert.Null(client.StatusMessage);
        Assert.Empty(client.GetHostedServiceAttachments());
    }

    [Fact]
    public async Task WhenTheServerUrlIsNotConfigured_ThenTheStartFailsWithoutAttaching()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient(serverUrl: null, isEnabled: false);
        testHost.Container.Client = client;

        // Act
        await client.StartAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, client.Status);
        Assert.Equal("Server URL is not configured", client.StatusMessage);
        Assert.Empty(client.GetHostedServiceAttachments());
        Assert.Null(client.Root);
    }

    [Fact]
    public async Task WhenTheHostHasDrained_ThenTheStartReportsThatNothingWasStarted()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient(isEnabled: false);
        testHost.Container.Client = client;
        await testHost.StopHostAsync();

        // Act
        await client.StartAsync();

        // Assert
        // The awaited attach returns a handle holding nothing, with no fault to explain it, and the
        // wrapper has to turn that into an error of its own rather than wait for an instance that is
        // never coming.
        Assert.Equal(ServiceStatus.Error, client.Status);
        Assert.Equal(NotAttachedMessage, client.StatusMessage);
        Assert.Empty(client.GetHostedServiceAttachments());
        Assert.Null(client.Root);
    }

    [Fact]
    public async Task WhenTheContextHasNoHostingHandler_ThenTheStartReportsThatNothingWasStarted()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var container = new WrapperContainer(context);
        var client = new OpcUaClient(NullLogger<OpcUaClient>.Instance)
        {
            ServerUrl = OpcUaTestHost.DeadServerUrl,
            IsEnabled = false
        };

        container.Client = client;

        // Act
        await client.StartAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, client.Status);
        Assert.Equal(NotAttachedMessage, client.StatusMessage);
        Assert.Empty(client.GetHostedServiceAttachments());
    }

    [Fact]
    public async Task WhenStartsAndStopsRunConcurrently_ThenTheWrapperNeverHoldsMoreThanOneAttachment()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient(isEnabled: false);
        testHost.Container.Client = client;

        using var operationsDone = new CancellationTokenSource();
        var mostAttachmentsSeen = 0;

        // Act
        var poll = Task.Run(async () =>
        {
            while (!operationsDone.IsCancellationRequested)
            {
                var count = client.GetHostedServiceAttachments().Length;
                if (count > mostAttachmentsSeen)
                {
                    Interlocked.Exchange(ref mostAttachmentsSeen, count);
                }

                await Task.Yield();
            }
        });

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (var round = 0; round < 3; round++)
            {
                await client.StartAsync();
                await client.StopAsync();
            }
        })));

        await operationsDone.CancelAsync();
        await poll;

        // Assert
        // Two live attachments is what the gate exists to prevent: each start builds its own root, so
        // the second source would be unreachable from the wrapper and could never be stopped from here.
        // An upper bound, not an equality: the sampler is unsynchronized, every worker ends on a stop,
        // and on a small runner it can be starved across the whole burst without that meaning anything.
        // Reachability of one is settled below, deterministically.
        Assert.True(mostAttachmentsSeen <= 1, $"Saw {mostAttachmentsSeen} live attachments at once.");

        // The gate serializes the paths but nothing orders the callers, so the state the race settles on
        // is whichever ran last. What has to hold is that the wrapper and its attachment agree, and that
        // one more start still gets a running client rather than a wedged one.
        await client.StartAsync();
        Assert.Equal(ServiceStatus.Running, client.Status);
        var attachment = Assert.Single(client.GetHostedServiceAttachments());
        Assert.NotNull(attachment.Current);
        Assert.NotNull(client.Root);
    }

    [Fact]
    public async Task WhenTheAttachmentHoldsNoInstance_ThenTheReconciliationDropsTheRoot()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient();
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForStatusAsync(() => client.Status, ServiceStatus.Running);

        var attachment = Assert.Single(client.GetHostedServiceAttachments());
        testHost.Container.Client = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () => attachment.Current is null && client.Status == ServiceStatus.Stopped,
            message: "The instance was not stopped, or the wrapper did not report the stop.");

        // The unwind leaves the tree alone, because the source that filled it was still live there.
        Assert.NotNull(client.Root);

        // Act
        // The attachment is still held, so the start skips the attach and does nothing but reconcile,
        // which is the same call the diagnostics poll makes on its own timer.
        await client.StartAsync();

        // Assert
        // The source is gone by now, so this is where what it published is dropped, and the wrapper
        // reports the instance it is waiting to be given rather than one that is running.
        Assert.Null(client.Root);
        Assert.Equal(ServiceStatus.Starting, client.Status);
        Assert.Same(attachment, Assert.Single(client.GetHostedServiceAttachments()));
    }

    [Fact]
    public async Task WhenTheWrapperAlreadyReportsStopped_ThenTheDiagnosticsPollLeavesItThere()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient(diagnosticsPollInterval: FastPoll);
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForRunningClientAsync(client);

        // Nothing but the poll writes the diagnostics once the start has returned, so the sentinel being
        // replaced is what establishes that the loop is running at the interval this test assumes.
        client.PollingItemCount = SentinelItemCount;
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.PollingItemCount != SentinelItemCount,
            message: "The diagnostics poll never ran.");

        // Act
        // The state the unwind leaves behind: the wrapper reports a stop that the poll did not perform,
        // while the attachment it still holds still holds a live instance.
        //
        // Written until it holds, because a round already inside the reconciliation when it is written
        // finishes by writing both of these back, and that one round is ahead of the short circuit
        // rather than covered by it. Under the defect no round ever leaves them alone, so this times
        // out with the same meaning as the observation below.
        await AsyncTestHelpers.WaitUntilAsync(
            () => ReportStopUntilItHolds(client),
            message: "Every diagnostics poll wrote over the reported stop.");

        await Task.Delay(PollObservation);

        // Assert
        // A timed observation of something that has to not happen. Starving the loop for the whole
        // window would let it pass without having observed anything, but it cannot make it fail: every
        // round that does run writes both of these unless the reported stop stops it.
        Assert.Equal(ServiceStatus.Stopped, client.Status);
        Assert.Equal(SentinelItemCount, client.PollingItemCount);

        // Act
        client.Status = ServiceStatus.Starting;

        // Assert
        // The same loop, still running and still reconciling the same attachment, which is what rules
        // out a window that passed because the loop had died. Both are waited for rather than the status
        // alone: the reconciliation writes Running before it publishes the numbers, and the numbers here
        // were never reset, so nothing else distinguishes the round that wrote them.
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Status == ServiceStatus.Running && client.PollingItemCount != SentinelItemCount,
            message: "The diagnostics poll did not resume once the reported stop was lifted.");
    }

    [Fact]
    public async Task WhenAStartHoldsTheGate_ThenTheStartAndThePollBothStillFinish()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient(diagnosticsPollInterval: FastPoll);

        // The factory's first act is publishing the tree it is about to bind a source to, and the start
        // holds the gate across the whole attach, so holding it there holds the gate.
        //
        // Armed before the client enters the graph, and the start left to the run loop rather than
        // invoked here: the Start operation enables the client, which is what the loop reads, so a second
        // start would queue on the gate and write Starting after the first one has reported Running.
        using var factoryReached = new ManualResetEventSlim();
        using var releaseFactory = new ManualResetEventSlim();
        var held = 0;
        testHost.WriteSeam.ArmAfterWrite((property, value) =>
        {
            if (!ReferenceEquals(property.Subject, client) ||
                property.Name != nameof(OpcUaClient.Root) ||
                value is null ||
                Interlocked.Exchange(ref held, 1) == 1)
            {
                return;
            }

            factoryReached.Set();
            releaseFactory.Wait(HoldTimeout);
        });

        try
        {
            // Act
            testHost.Container.Client = client;
            Assert.True(factoryReached.Wait(HoldTimeout), "The start never reached the factory.");

            // Nothing is observed while the gate is held. A poll that queued behind it instead would
            // reconcile the same attachment to the same state, so the non blocking wait itself has no
            // test behind it. What this pins is liveness: a start parked in its factory and a poll
            // running against it both still finish.
        }
        finally
        {
            releaseFactory.Set();
        }

        // Assert
        // The diagnostics as well as the status, because the start writes Running before it publishes
        // them: the sentinel below has to go in after that write, or the start replacing it would stand
        // in for the poll that is what this waits for.
        await OpcUaTestHost.WaitForRunningClientAsync(client);
        var attachment = Assert.Single(client.GetHostedServiceAttachments());
        Assert.NotNull(attachment.Current);
        Assert.NotNull(client.Root);

        client.PollingItemCount = SentinelItemCount;
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.PollingItemCount != SentinelItemCount,
            message: "The diagnostics poll did not resume once the start released the gate.");
    }

    [Fact]
    public async Task WhenAnEnabledClientIsReconfigured_ThenItIsStartedAgainFromTheEdit()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient();
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForStatusAsync(() => client.Status, ServiceStatus.Running);

        var firstAttachment = Assert.Single(client.GetHostedServiceAttachments());
        var firstRoot = client.Root;

        // Act
        client.RootPath = "Machines/Edited";
        await client.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        // The factory names the tree after the last segment of the configured path, so a tree with the
        // edited name is the attach having run again against the edit rather than the first source
        // still being the one that is attached.
        Assert.Equal(ServiceStatus.Running, client.Status);
        var secondAttachment = Assert.Single(client.GetHostedServiceAttachments());
        Assert.NotSame(firstAttachment, secondAttachment);
        Assert.NotNull(secondAttachment.Current);
        Assert.NotSame(firstRoot, client.Root);
        Assert.Equal("Edited", Assert.IsType<OpcUaDynamicSubject>(client.Root).Title);
    }

    [Fact]
    public async Task WhenADisabledClientIsReconfigured_ThenTheStopIsNotFollowedByAStart()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient();
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForStatusAsync(() => client.Status, ServiceStatus.Running);

        // Act
        client.IsEnabled = false;
        await client.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        // Without the guard on the start, an edit that disables the client would stop it and start it
        // again in the same call.
        Assert.Equal(ServiceStatus.Stopped, client.Status);
        Assert.Empty(client.GetHostedServiceAttachments());
        Assert.Null(client.Root);
    }

    /// <summary>
    /// Reports a stop the poll did not perform, and answers whether the previous attempt survived.
    /// </summary>
    private static bool ReportStopUntilItHolds(OpcUaClient client)
    {
        if (client.Status == ServiceStatus.Stopped && client.PollingItemCount == SentinelItemCount)
        {
            return true;
        }

        client.Status = ServiceStatus.Stopped;
        client.PollingItemCount = SentinelItemCount;
        return false;
    }

    /// <summary>
    /// Takes the client out of the graph and puts it back with a factory that throws, which is the shape
    /// a re-attach has when the configuration it reads has stopped being resolvable. The wrapper's
    /// factory is private and builds from properties none of which can be made to fail, so the failure
    /// is injected into the write it starts with.
    /// </summary>
    private static async Task ReAttachWithAFailingFactoryAsync(
        OpcUaTestHost testHost, OpcUaClient client, IHostedServiceAttachment attachment)
    {
        testHost.Container.Client = null;
        await AsyncTestHelpers.WaitUntilAsync(() => attachment.Current is null);

        var failed = 0;
        testHost.WriteSeam.ArmBeforeWrite((property, value) =>
        {
            if (!ReferenceEquals(property.Subject, client) ||
                property.Name != nameof(OpcUaClient.Root) ||
                value is null ||
                Interlocked.Exchange(ref failed, 1) == 1)
            {
                return;
            }

            // Only the factory's own write, which publishes a tree. The wrapper clears Root from the
            // same property while it reconciles, and failing that write would fail the reporting
            // rather than the factory.
            testHost.WriteSeam.ArmBeforeWrite(null);
            throw new InvalidOperationException(FactoryFailureMessage);
        });

        testHost.Container.Client = client;
        await AsyncTestHelpers.WaitUntilAsync(
            () => attachment.Fault is not null,
            message: "The re-attach did not fault.");
    }
}
