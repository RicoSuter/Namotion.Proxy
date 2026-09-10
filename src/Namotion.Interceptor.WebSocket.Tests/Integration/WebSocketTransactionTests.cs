using System.Threading;
using System.Threading.Tasks;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

/// <summary>
/// Covers outbound WebSocket writes that happen inside a transaction commit. The commit writes to
/// sources from the committing flow, so the source runs while the ambient transaction is committing
/// and property reads on that flow are rejected.
/// </summary>
[Trait("Category", "Integration")]
public class WebSocketTransactionTests
{
    private readonly ITestOutputHelper _output;

    public WebSocketTransactionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenPropertyIsWrittenInTransaction_ThenServerReceivesCommittedValue()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port);

        await client.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port,
            configureContext: context => context.WithSourceTransactions());

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "Initial",
            message: "Client should receive initial state");

        await AssertSourceOwnsAsync(client.Root!, nameof(TestRoot.Name));

        // Act
        using (var transaction = await client.Context!.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            client.Root!.Name = "From transaction";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Root!.Name == "From transaction",
            message: "Server should receive the transactionally committed value");
    }

    [Fact]
    public async Task WhenMultiplePropertiesAreWrittenInTransaction_ThenServerReceivesAllOfThem()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port);

        await client.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port,
            configureContext: context => context.WithSourceTransactions());

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "Initial",
            message: "Client should receive initial state");

        await AssertSourceOwnsAsync(client.Root!, nameof(TestRoot.Number));

        // Act
        using (var transaction = await client.Context!.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            client.Root!.Name = "Batched";
            client.Root.Number = 42.5m;
            client.Root.Connected = true;
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Root!.Name == "Batched" &&
                  server.Root.Number == 42.5m &&
                  server.Root.Connected,
            message: "Server should receive every property committed by the transaction");
    }

    [Fact]
    public async Task WhenNestedSubjectPropertyIsWrittenInTransaction_ThenServerReceivesCommittedValue()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) =>
            {
                root.Name = "Initial";
                root.Child = new TestItem { Label = "ServerChild" };
            },
            port: portLease.Port);

        await client.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port,
            configureContext: context => context.WithSourceTransactions());

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Child?.Label == "ServerChild",
            message: "Client should receive the initial child state");

        await AssertSourceOwnsAsync(client.Root!.Child!, nameof(TestItem.Label));

        // Act
        using (var transaction = await client.Context!.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            client.Root!.Child!.Label = "From transaction";
            client.Root.Child.Value = 7;
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Root!.Child?.Label == "From transaction" && server.Root.Child.Value == 7,
            message: "Server should receive the transactionally committed nested values");
    }

    /// <summary>
    /// The commit only writes to sources that own the changed property, so an ownership regression
    /// would silently downgrade these tests to covering plain non-transactional propagation.
    /// </summary>
    /// <remarks>
    /// Settled rather than sampled. The waits above only establish that a value arrived, and ownership
    /// is registered on its own schedule as the subject is attached, so sampling it the instant a value
    /// lands fails on timing rather than on ownership. A real regression still fails here, on the
    /// timeout, with the same message.
    /// </remarks>
    private static Task AssertSourceOwnsAsync(IInterceptorSubject subject, string propertyName) =>
        AsyncTestHelpers.WaitUntilAsync(
            () => new PropertyReference(subject, propertyName).TryGetSource(out _),
            message: $"The WebSocket client source must own {propertyName} for the commit to write through it");
}
