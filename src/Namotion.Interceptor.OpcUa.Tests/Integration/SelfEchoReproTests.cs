using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Regression test for the detach against flush loop race: a detach on another thread must not
/// flush node state the write loop has set but not yet flushed itself, back into the subject.
/// </summary>
[Trait("Category", "Integration")]
public class SelfEchoReproTests
{
    private readonly ITestOutputHelper _output;

    public SelfEchoReproTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenAClientWriteLandsBetweenTheAssignmentAndTheFlush_ThenItIsNotSwallowedAsOurOwn()
    {
        // Arrange: the write loop assigns node.Value and then flushes it, and the flush reports whatever
        // the node holds at that moment, so a write landing in between is reported on the loop's own
        // thread. Suppressing by "we are flushing" alone drops it: the node keeps that value and serves
        // it to every client, while the subject never receives it, so the server sits behind its clients
        // until the property is written again.
        //
        // The SDK's write service cannot produce this, since it takes the node manager lock the loop
        // holds for the whole batch. Pinned anyway, because that is what makes identifying our own
        // reflection by value exact rather than a bet on who else can write a node.
        var logger = new TestLogger(_output);
        using var port = await OpcUaTestPortPool.AcquireAsync();

        await using var server = new OpcUaTestServer<SelfWriteTestParent>(logger);
        await server.StartAsync(
            createRoot: context => new SelfWriteTestParent(context),
            initializeDefaults: (context, root) =>
                root.Child = new SelfWriteTestChild(context) { Value = "initial" },
            baseAddress: port.BaseAddress,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        var serverService = (OpcUaSubjectServer)server.Server!;
        var child = server.Root!.Child!;
        var property = new PropertyReference(child, nameof(SelfWriteTestChild.Value));

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverService.TryGetVariableNode(property, out _),
            message: "the child's variable node should exist");

        Assert.True(serverService.TryGetVariableNode(property, out var node));

        var standardServer = (OpcUaStandardServer)serverService.CurrentServer!;
        var systemContext = standardServer.CurrentInstance.DefaultSystemContext;

        // Act: reproduce the loop's state, then let a client write overwrite the node before the flush.
        lock (standardServer.NodeManagerLock!)
        {
            node!.Value = "ours";
            OpcUaSubjectServer.IsWritingOwnNodeValues = true;
            OpcUaSubjectServer.SelfWrittenNodeValue = "ours";
            try
            {
                node.Value = "from-client";
                node.ClearChangeMasks(systemContext, false);
            }
            finally
            {
                OpcUaSubjectServer.IsWritingOwnNodeValues = false;
                OpcUaSubjectServer.SelfWrittenNodeValue = null;
            }
        }

        // Assert: the client's value reached the subject rather than being dropped as our reflection.
        await AsyncTestHelpers.WaitUntilAsync(
            () => child.Value == "from-client",
            message: $"the client write must reach the subject; it holds '{child.Value}'");
    }

    [Fact]
    public async Task WhenDetachRacesTheFlushLoopMidWrite_ThenTheStaleValueIsNotAppliedBack()
    {
        // Arrange
        var logger = new TestLogger(_output);
        using var port = await OpcUaTestPortPool.AcquireAsync();

        await using var server = new OpcUaTestServer<SelfWriteTestParent>(logger);
        await server.StartAsync(
            createRoot: context => new SelfWriteTestParent(context),
            initializeDefaults: (context, root) =>
                root.Child = new SelfWriteTestChild(context) { Value = "initial" },
            baseAddress: port.BaseAddress,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        var serverService = (OpcUaSubjectServer)server.Server!;
        var child = server.Root!.Child!;
        var property = new PropertyReference(child, nameof(SelfWriteTestChild.Value));

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverService.TryGetVariableNode(property, out _),
            message: "the child's variable node should exist");

        Assert.True(serverService.TryGetVariableNode(property, out var node));

        var standardServer = (OpcUaStandardServer)serverService.CurrentServer!;
        var nodeManagerLock = standardServer.NodeManagerLock!;
        var systemContext = standardServer.CurrentInstance.DefaultSystemContext;

        using var valueAssigned = new ManualResetEventSlim();
        using var detachRequested = new ManualResetEventSlim();
        Exception? flushError = null;
        Exception? detachError = null;
        Thread? detachThread = null;

        // Simulates the flush loop's exact state between assigning a node value and its own
        // ClearChangeMasks: NodeManagerLock held, node.Value set, Value change mask pending.
        var flushThread = new Thread(() =>
        {
            try
            {
                lock (nodeManagerLock)
                {
                    node!.Value = "stale-echo";
                    // The loop records what it wrote alongside arming the guard, so that a client write
                    // landing before its own flush is not mistaken for its reflection.
                    OpcUaSubjectServer.SelfWrittenNodeValue = "stale-echo";
                    valueAssigned.Set();
                    Assert.True(detachRequested.Wait(TimeSpan.FromSeconds(10)));

                    // Wait (bounded, no fixed sleep) until the detach thread is either blocked on a
                    // lock (correct code: RemoveSubjectNodes waits for NodeManagerLock; defective
                    // code: the echo has already fired and DeleteNode blocks at RemoveRootNotifier)
                    // or has finished entirely. Both outcomes are terminal for the interleaving, so
                    // proceeding is deterministic in both directions. The only wait on the detach
                    // thread after detachRequested.Set() is a lock, so WaitSleepJoin is the expected signal.
                    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                    while (detachThread!.IsAlive &&
                           (detachThread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0)
                    {
                        Assert.True(DateTime.UtcNow < deadline, "detach thread should block or finish");
                        Thread.SpinWait(1000);
                    }

                    OpcUaSubjectServer.IsWritingOwnNodeValues = true;
                    try
                    {
                        node.ClearChangeMasks(systemContext, false);
                    }
                    finally
                    {
                        OpcUaSubjectServer.IsWritingOwnNodeValues = false;
                    }
                }
            }
            catch (Exception exception)
            {
                flushError = exception;
            }
        });

        detachThread = new Thread(() =>
        {
            try
            {
                Assert.True(valueAssigned.Wait(TimeSpan.FromSeconds(10)));
                detachRequested.Set();
                server.Root.Child = null;
            }
            catch (Exception exception)
            {
                detachError = exception;
            }
        });

        // Act
        flushThread.Start();
        detachThread.Start();

        Assert.True(flushThread.Join(TimeSpan.FromSeconds(30)), "flush thread should finish (no deadlock)");
        Assert.True(detachThread.Join(TimeSpan.FromSeconds(30)), "detach thread should finish (no deadlock)");
        Assert.Null(flushError);
        Assert.Null(detachError);

        // Assert: the mid-write value must not have been applied back to the subject.
        Assert.Equal("initial", child.Value);
        Assert.Equal(0d, serverService.Diagnostics.Throughput.IncomingPerSecond);
    }
}
