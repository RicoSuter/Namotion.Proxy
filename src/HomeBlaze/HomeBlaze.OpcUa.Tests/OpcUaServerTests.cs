using HomeBlaze.Abstractions;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.OpcUa.Tests;

public class OpcUaServerTests
{
    /// <summary>
    /// How long the root stays unloaded while the start is observed not to have attached anything. See
    /// the test that uses it for why the observation cannot false fail.
    /// </summary>
    private static readonly TimeSpan UnloadedRootObservation = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task WhenTheConfiguredPathDoesNotResolve_ThenTheStartFailsAndKeepsNoAttachment()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        await testHost.LoadRootAsync();

        var server = testHost.CreateServer("/NotInTheGraph", isEnabled: false);
        testHost.Container.Server = server;

        // Act
        await server.StartAsync();

        // Assert
        // A synchronous factory can only report a failed lookup by throwing, and the awaited attach is
        // transactional, so the failure has to arrive as the wrapper's own status with nothing left
        // attached behind it. An attachment kept here would block every later start.
        Assert.Equal(ServiceStatus.Error, server.Status);
        Assert.Equal("Could not resolve subject at path: /NotInTheGraph", server.StatusMessage);
        Assert.Empty(server.GetHostedServiceAttachments());
        Assert.False(server.IsServerRunning);
    }

    [Fact]
    public async Task WhenThePathIsNotConfigured_ThenTheStartFailsWithoutWaitingForTheRoot()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var server = testHost.CreateServer(path: string.Empty, isEnabled: false);
        testHost.Container.Server = server;

        // Act
        await server.StartAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, server.Status);
        Assert.Equal("Path is not configured", server.StatusMessage);
        Assert.Empty(server.GetHostedServiceAttachments());

        // The guard runs ahead of the wait on the root, so an unconfigured server reports its error at
        // once instead of sitting in Starting until a root arrives.
        Assert.False(testHost.RootManager.IsLoaded);
    }

    [Fact]
    public async Task WhenTheRootHasNotLoaded_ThenTheStartWaitsForItBeforeAttaching()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var server = testHost.CreateServer("/NotInTheGraph");

        // Act
        testHost.Container.Server = server;
        await OpcUaTestHost.WaitForStatusAsync(() => server.Status, ServiceStatus.Starting);

        // Assert
        // A timed observation of something that has to not happen. It cannot false fail: only
        // LoadRootAsync completes RootLoaded, nothing else in the process can, and the start parks on
        // that task, so waiting longer cannot change the answer.
        await Task.Delay(UnloadedRootObservation);
        Assert.Equal(ServiceStatus.Starting, server.Status);
        Assert.Empty(server.GetHostedServiceAttachments());

        // Act
        await testHost.LoadRootAsync();

        // Assert
        // The message as well as the status, because the failing start writes the status first: waiting
        // for that alone and then reading the message lands between the two writes.
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Status == ServiceStatus.Error &&
                  server.StatusMessage == "Could not resolve subject at path: /NotInTheGraph",
            message: "The start did not fail against the configured path once the root had loaded.");
    }

    [Fact]
    public async Task WhenTheHostHasDrained_ThenTheStartReportsThatNothingWasStarted()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        await testHost.LoadRootAsync();

        var server = testHost.CreateServer("/NotInTheGraph", isEnabled: false);
        testHost.Container.Server = server;
        await testHost.StopHostAsync();

        // Act
        await server.StartAsync();

        // Assert
        // The drained handler appends nothing, so the awaited attach hands back a handle holding no
        // instance and no fault. Reaching this without the factory having run at all is what
        // distinguishes it from the resolution failure above.
        Assert.Equal(ServiceStatus.Error, server.Status);
        Assert.Equal("Not attached to a running host, so nothing was started", server.StatusMessage);
        Assert.Empty(server.GetHostedServiceAttachments());
    }

    [Fact]
    public async Task WhenAnEnabledServerIsReconfigured_ThenTheStartRunsAgainstTheEditedPath()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        await testHost.LoadRootAsync();

        var server = testHost.CreateServer("/NotInTheGraph");
        testHost.Container.Server = server;

        // The message as well as the status, because the failing start writes the status first: waiting
        // for that alone and then reading the message lands between the two writes.
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Status == ServiceStatus.Error &&
                  server.StatusMessage == "Could not resolve subject at path: /NotInTheGraph",
            message: "The start did not fail against the configured path.");

        // Act
        server.Path = "/AlsoNotInTheGraph";
        await server.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        // The failed start kept no attachment, so the stop here has nothing to do and the edit is
        // applied by the start alone. The factory resolves the path on every attach, so a message
        // naming the edited path is that start having run rather than the previous one still reported.
        Assert.Equal(ServiceStatus.Error, server.Status);
        Assert.Equal("Could not resolve subject at path: /AlsoNotInTheGraph", server.StatusMessage);
        Assert.Empty(server.GetHostedServiceAttachments());
    }

    [Fact]
    public async Task WhenTheServerSubjectLeavesTheGraph_ThenTheUnwindReportsStopped()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        await testHost.LoadRootAsync();

        var server = testHost.CreateServer("/NotInTheGraph");
        testHost.Container.Server = server;
        await OpcUaTestHost.WaitForStatusAsync(() => server.Status, ServiceStatus.Error);

        // Act
        testHost.Container.Server = null;

        // Assert
        // The unwind neither detaches nor takes the gate, so a detach that had to wait for the
        // wrapper's own stop transition would hang here rather than fail.
        //
        // The message is waited for alongside the status rather than read after it: the wrapper arrives
        // here from Error, the message is the text behind that status alone, and the reported stop
        // writes the status first.
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Status == ServiceStatus.Stopped && server.StatusMessage is null,
            message: "The server subject did not report its stop, or kept the error text behind it.");

        // The diagnostics are deliberately not asserted here. This server never reaches Running, since
        // its path does not resolve, so they were never set and asserting them null passes with the
        // unwind's own reset deleted. Pinning that needs a server that binds a port.

    }
}
