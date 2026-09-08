using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// A server keeps two stores: the node tree it serves and the subject. A client writes into the first
/// directly, so an applied value is already settled there before the subject sees it, and pushing an
/// older commit over it leaves the two disagreeing with nothing to correct them. The applied commit
/// comes from this source, so it is skipped as an echo and never carries the client's value back.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaCrossStoreConvergenceTests
{
    private readonly ITestOutputHelper _output;

    public OpcUaCrossStoreConvergenceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenAClientWroteTheNodeAfterAChangeWasQueued_ThenTheOlderCommitIsNotWrittenOut()
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
        var systemContext = standardServer.CurrentInstance.DefaultSystemContext;

        // A client write, exactly as the SDK performs one: under the node manager lock, assigned and then
        // flushed, which is the path that reaches the subject.
        lock (standardServer.NodeManagerLock!)
        {
            node!.Value = "from-client";
            node.ClearChangeMasks(systemContext, false);
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => child.Value == "from-client",
            message: $"the client write must reach the subject; it holds '{child.Value}'");

        Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: false, out var lastNonSourceCommitRevision, out _));
        Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: true, out var lastCommitRevision, out _));
        Assert.True(lastCommitRevision > lastNonSourceCommitRevision,
            "applying the client's write must have committed without moving the non-source marker");

        // Act: a local commit that predates the client's write reaches the write loop late, which is what
        // happens when its enqueue is preempted past a flush.
        var straggler = SubjectPropertyChange.Create(
            property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null,
            "initial", "straggler", lastCommitRevision - 1);

        await serverService.WriteChangesAsync(new[] { straggler }, CancellationToken.None);

        // Assert: both stores still hold what the client wrote.
        Assert.Equal("from-client", node!.Value);
        Assert.Equal("from-client", child.Value);

        // Positive control, so that a server which had stopped writing for any other reason (no node
        // mapped, no registered property, a converter throwing) could not pass the assertion above.
        child.Value = "later-local";
        Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: true, out var laterRevision, out _));

        var current = SubjectPropertyChange.Create(
            property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null,
            "from-client", "later-local", laterRevision);

        await serverService.WriteChangesAsync(new[] { current }, CancellationToken.None);

        Assert.Equal("later-local", node.Value);
    }

}
