using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

[Trait("Category", "Integration")]
public class OpcUaTransactionTests : SharedServerTestBase
{
    public OpcUaTransactionTests(
        SharedOpcUaServerFixture serverFixture,
        SharedOpcUaClientFixture clientFixture,
        ITestOutputHelper output)
        : base(serverFixture, clientFixture, output) { }

    [Fact]
    public async Task Transaction_CommitSingleProperty_ServerReceivesChangeOnlyAfterCommit()
    {
        // Arrange
        var serverArea = ServerFixture.ServerRoot.Transactions.SingleProperty;
        var clientArea = Client!.Root!.Transactions.SingleProperty;
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.Name == serverArea.Name,
            timeout: TimeSpan.FromSeconds(90),
            message: "Client should receive the isolated area's initial name");
        var initialName = serverArea.Name;

        // Act
        using (var transaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Name = "Transaction Value";

            // Server should still have old value before commit
            Assert.Equal(initialName, serverArea.Name);

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Name == "Transaction Value",
            timeout: TimeSpan.FromSeconds(90),
            message: "Server should receive committed transaction value");
    }

    [Fact]
    public async Task Transaction_CommitMultipleProperties_ServerReceivesAllChangesOnlyAfterCommit()
    {
        // Arrange
        var serverArea = ServerFixture.ServerRoot.Transactions.MultiProperty;
        var clientArea = Client!.Root!.Transactions.MultiProperty;
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.Name == serverArea.Name && clientArea.Number == serverArea.Number,
            timeout: TimeSpan.FromSeconds(90),
            message: "Client should receive the isolated area's initial values");
        var initialName = serverArea.Name;
        var initialNumber = serverArea.Number;

        // Act
        using (var transaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Name = "Multi-Property Test";
            clientArea.Number = 123.45m;

            // Server should still have old values before commit
            Assert.Equal(initialName, serverArea.Name);
            Assert.Equal(initialNumber, serverArea.Number);

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Name == "Multi-Property Test" && serverArea.Number == 123.45m,
            timeout: TimeSpan.FromSeconds(90),
            message: "Server should receive all committed transaction values");

        Assert.Equal("Multi-Property Test", serverArea.Name);
        Assert.Equal(123.45m, serverArea.Number);
    }

    [Fact]
    public async Task Transaction_DisposedWithoutCommit_ServerShouldNotReceiveChanges()
    {
        // Arrange
        var serverArea = ServerFixture.ServerRoot.Transactions.SinglePropertyRollback;
        var clientArea = Client!.Root!.Transactions.SinglePropertyRollback;
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.Name == "InitialValue",
            timeout: TimeSpan.FromSeconds(90),
            message: "Client should receive the rollback area's initial value");
        var changes = new ConcurrentQueue<SubjectPropertyChange>();
        using var subscription = Client.Context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => ReferenceEquals(change.Property.Subject, clientArea))
            .Subscribe(changes.Enqueue);

        // Act
        SubjectTransaction transaction;
        using (transaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Name = "UncommittedValue";
            Assert.Single(transaction.GetPendingChanges());
            Assert.Equal("UncommittedValue", clientArea.Name);
        }

        // The ordinary outbound path must make progress before checking the remote state.
        Client.Root.Transactions.SynchronizationMarker = "Single rollback completed";
        await AsyncTestHelpers.WaitUntilAsync(
            () => ServerFixture.ServerRoot.Transactions.SynchronizationMarker == "Single rollback completed",
            timeout: TimeSpan.FromSeconds(90),
            message: "Server should receive the write following transaction disposal");

        // Assert
        Assert.Null(SubjectTransaction.Current);
        Assert.Empty(transaction.GetPendingChanges());
        Assert.DoesNotContain(changes, change => change.GetNewValue<string>() == "UncommittedValue");
        Assert.Equal("InitialValue", clientArea.Name);
        Assert.Equal("InitialValue", serverArea.Name);
    }

    [Fact]
    public async Task Transaction_MultipleProperties_DisposedWithoutCommit_ServerShouldNotReceiveChanges()
    {
        // Arrange
        var serverArea = ServerFixture.ServerRoot.Transactions.MultiPropertyRollback;
        var clientArea = Client!.Root!.Transactions.MultiPropertyRollback;
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.Name == "SetupName" && clientArea.Number == 100m,
            timeout: TimeSpan.FromSeconds(90),
            message: "Client should receive the rollback area's initial values");
        var changes = new ConcurrentQueue<SubjectPropertyChange>();
        using var subscription = Client.Context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => ReferenceEquals(change.Property.Subject, clientArea))
            .Subscribe(changes.Enqueue);

        // Act
        SubjectTransaction transaction;
        using (transaction = await Client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            clientArea.Name = "UncommittedName";
            clientArea.Number = 999m;
            Assert.Equal(2, transaction.GetPendingChanges().Count);
            Assert.Equal("UncommittedName", clientArea.Name);
            Assert.Equal(999m, clientArea.Number);
        }

        Client.Root.Transactions.SynchronizationMarker = "Multiple rollback completed";
        await AsyncTestHelpers.WaitUntilAsync(
            () => ServerFixture.ServerRoot.Transactions.SynchronizationMarker == "Multiple rollback completed",
            timeout: TimeSpan.FromSeconds(90),
            message: "Server should receive the write following transaction disposal");

        // Assert
        Assert.Null(SubjectTransaction.Current);
        Assert.Empty(transaction.GetPendingChanges());
        Assert.DoesNotContain(changes, change =>
            change.Property.Name == nameof(clientArea.Name) && change.GetNewValue<string>() == "UncommittedName" ||
            change.Property.Name == nameof(clientArea.Number) && change.GetNewValue<decimal>() == 999m);
        Assert.Equal("SetupName", clientArea.Name);
        Assert.Equal(100m, clientArea.Number);
        Assert.Equal("SetupName", serverArea.Name);
        Assert.Equal(100m, serverArea.Number);
    }
}
