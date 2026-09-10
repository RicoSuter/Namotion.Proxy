using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for basic read/write synchronization using the shared OPC UA server.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaReadWriteTests : SharedServerTestBase
{
    public OpcUaReadWriteTests(
        SharedOpcUaServerFixture serverFixture,
        SharedOpcUaClientFixture clientFixture,
        ITestOutputHelper output)
        : base(serverFixture, clientFixture, output) { }

    [Fact]
    public async Task WriteAndReadPrimitives_ShouldUpdateClient()
    {
        // Arrange - use dedicated test area
        var serverArea = ServerFixture.ServerRoot.ReadWrite.BasicSync;
        var clientArea = Client!.Root!.ReadWrite.BasicSync;
        var diagnostics = Client!.Source!.Diagnostics;

        // Assert - on a healthy connection the diagnostics must be populated:
        // session/server live and Name is bound on both sides.
        Assert.NotNull(Client.Source);
        Assert.NotNull(Client.Source.CurrentSession);
        Assert.NotNull(ServerFixture.Server.CurrentServer);

        // LastError is sticky for the whole epoch, so a transient failure on the shared fixture's
        // first connect attempt would still be readable here. Captured as a baseline instead, so the
        // assertion at the end means nothing failed after the connection was established.
        var errorBeforeExercise = diagnostics.LastError;

        // Each side owns its own subject tree, so the lookup must use that side's property reference.
        Assert.True(
            Client.Source.TryGetNodeId(clientArea.GetPropertyReference(nameof(BasicSyncArea.Name)), out var clientNodeId),
            "Client must have resolved a NodeId for the bound BasicSync.Name property");
        Assert.NotNull(clientNodeId);

        Assert.True(
            ServerFixture.Server.TryGetVariableNode(serverArea.GetPropertyReference(nameof(BasicSyncArea.Name)), out var serverVariable),
            "Server must expose a BaseDataVariableState for the bound BasicSync.Name property");
        Assert.NotNull(serverVariable);

        // Act & Assert - Test string property from server
        serverArea.Name = "Updated Server Name";
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.Name == "Updated Server Name",
            timeout: TimeSpan.FromSeconds(90),
            message: "Client should receive server's name update");

        // Test string property from client
        clientArea.Name = "Updated Client Name";
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Name == "Updated Client Name",
            timeout: TimeSpan.FromSeconds(90),
            message: "Server should receive client's name update");

        // Test numeric property from server
        serverArea.Number = 123.45m;
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.Number == 123.45m,
            timeout: TimeSpan.FromSeconds(90),
            message: "Client should receive server's number update");

        // Test numeric property from client
        clientArea.Number = 54.321m;
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Number == 54.321m,
            timeout: TimeSpan.FromSeconds(90),
            message: "Server should receive client's number update");

        // Verify throughput diagnostics after bidirectional writes
        Assert.True(diagnostics.Throughput.IncomingPerSecond > 0.0,
            $"Throughput.IncomingPerSecond should be positive after receiving updates, was {diagnostics.Throughput.IncomingPerSecond}");
        Assert.True(diagnostics.Throughput.OutgoingPerSecond > 0.0,
            $"Throughput.OutgoingPerSecond should be positive after writing changes, was {diagnostics.Throughput.OutgoingPerSecond}");

        // Same reference, so no round trip above recorded a new failure.
        Assert.Same(errorBeforeExercise, diagnostics.LastError);
    }

    [Fact]
    public async Task WriteAndReadArraysOnServer_ShouldUpdateClient()
    {
        // Arrange - use dedicated test area
        var serverArea = ServerFixture.ServerRoot.ReadWrite.ArraySync;
        var clientArea = Client!.Root!.ReadWrite.ArraySync;

        Logger.Log($"Server initial ScalarNumbers: [{string.Join(", ", serverArea.ScalarNumbers)}]");
        Logger.Log($"Client initial ScalarNumbers: [{string.Join(", ", clientArea.ScalarNumbers)}]");

        // Act - update server arrays
        var newNumbers = new[] { 100, 200, 300 };
        serverArea.ScalarNumbers = newNumbers;
        Logger.Log($"Server updated ScalarNumbers: [{string.Join(", ", serverArea.ScalarNumbers)}]");

        // Assert - wait for array synchronization
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.ScalarNumbers.SequenceEqual(newNumbers),
            timeout: TimeSpan.FromSeconds(90),
            message: "Client should receive server's array update");

        Logger.Log($"Client ScalarNumbers after update: [{string.Join(", ", clientArea.ScalarNumbers)}]");
        Assert.Equal(newNumbers, clientArea.ScalarNumbers);
    }
}

/// <summary>
/// Tests for nested structure synchronization (server→client) using the shared OPC UA server.
/// Tests object references, arrays, dictionaries, deep nesting, OpcUaValue pattern, and PropertyAttribute.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaNestedStructureTests : SharedServerTestBase
{
    public OpcUaNestedStructureTests(
        SharedOpcUaServerFixture serverFixture,
        SharedOpcUaClientFixture clientFixture,
        ITestOutputHelper output)
        : base(serverFixture, clientFixture, output) { }

    [Fact]
    public async Task WriteAndReadNestedStructures_ShouldUpdateClient()
    {
        var serverArea = ServerFixture.ServerRoot.Nested;
        var clientArea = Client!.Root!.Nested;

        // Write all properties at once (no waiting between writes)
        serverArea.Person.FirstName = "UpdatedFirst";
        serverArea.People[0].LastName = "UpdatedLast";
        serverArea.PeopleByName!["john"].FirstName = "Johnny";
        serverArea.Person.Address!.City = "New York";
        serverArea.People[0].Address!.ZipCode = "12345";
        serverArea.Sensor!.Value = 42.5;
        serverArea.Sensor.Unit = "°F";
        serverArea.Sensor.MinValue = -50.0;
        serverArea.Number_Unit = "items";

        // Wait for all properties to sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.Person.FirstName == "UpdatedFirst" &&
                  clientArea.People[0].LastName == "UpdatedLast" &&
                  clientArea.PeopleByName!["john"].FirstName == "Johnny" &&
                  clientArea.Person.Address!.City == "New York" &&
                  clientArea.People[0].Address!.ZipCode == "12345" &&
                  Math.Abs(clientArea.Sensor!.Value - 42.5) < 0.01 &&
                  clientArea.Sensor!.Unit == "°F" &&
                  clientArea.Sensor?.MinValue == -50.0 &&
                  clientArea.Number_Unit == "items",
            timeout: TimeSpan.FromSeconds(90),
            message: "Client should receive all nested structure updates");

        Logger.Log("All nested structure tests passed!");
    }
}
