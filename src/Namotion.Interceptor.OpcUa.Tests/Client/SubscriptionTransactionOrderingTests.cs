using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Opc.Ua;
using Opc.Ua.Client;
using static Namotion.Interceptor.OpcUa.Tests.Client.ClientSourceTestFactory;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class SubscriptionTransactionOrderingTests
{
    [Theory]
    [InlineData("Committed", 0, false)]
    [InlineData("Initial", -1, true)]
    [InlineData("External", 0, true)]
    [InlineData("External", 1, true)]
    public async Task WhenSubscriptionArrivesDuringTransaction_ThenConflictDependsOnValue(
        string incomingValue, int timestampOffsetSeconds, bool expectConflict)
    {
        // Arrange
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var root = (TestRoot)source.RootSubject;
        var context = source.RootSubject.Context.WithTransactions();
        var writer = new AcceptingWriter(source);
        context.AddService<ITransactionWriter>(writer);
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await propertyWriter.LoadInitialStateAndResumeAsync(CancellationToken.None);
        var errors = new List<Exception>();
        await using var manager = new SubscriptionManager(
            source, propertyWriter, pollingManager: null, readAfterWriteManager: null,
            configuration, errors.Add, NullLogger.Instance);
        var property = new RegisteredSubject(root).TryGetProperty(nameof(TestRoot.Name))!;
        using var subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        var monitoredItem = new MonitoredItem(NullTelemetryContext.Instance)
        {
            StartNodeId = new NodeId("Name", 2),
            AttributeId = Opc.Ua.Attributes.Value,
            Handle = property
        };
        subscription.AddItem(monitoredItem);
        manager.TrackMonitoredItem(monitoredItem);
        root.Name = "Initial";
        var committedTimestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero);
        using (var setup = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            using (SubjectChangeContext.WithChangedTimestamp(committedTimestamp))
            {
                root.Name = "Committed";
            }
            await setup.CommitAsync(CancellationToken.None);
        }
        Assert.Equal("Committed", root.Name);
        Assert.Equal(committedTimestamp, property.Reference.TryGetWriteTimestamp());
        Assert.Equal("Committed", writer.SourceValue);

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        root.Name = "Pending";
        var incomingTimestamp = committedTimestamp.AddSeconds(timestampOffsetSeconds);
        var notification = new DataChangeNotification
        {
            MonitoredItems =
            [
                new MonitoredItemNotification
                {
                    ClientHandle = monitoredItem.ClientHandle,
                    Value = new DataValue
                    {
                        Value = incomingValue,
                        SourceTimestamp = incomingTimestamp.UtcDateTime,
                        StatusCode = StatusCodes.Good
                    }
                }
            ],
            DiagnosticInfos = []
        };

        // Act
        // SDK notifications arrive independently of the caller's ambient transaction.
        Task callback;
        using (ExecutionContext.SuppressFlow())
        {
            callback = Task.Run(() => manager.OnFastDataChange(subscription, notification, []));
        }
        await callback.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Empty(errors);
        Assert.Equal("Pending", root.Name);
        // Characterizes the documented absence of timestamp ordering on subscription applies.
        Assert.Equal(incomingTimestamp, property.Reference.TryGetWriteTimestamp());
        if (expectConflict)
        {
            await Assert.ThrowsAsync<SubjectTransactionConflictException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
            Assert.Single(transaction.GetPendingChanges());
            Assert.Equal("Committed", writer.SourceValue);
            transaction.Dispose();
            Assert.Equal(incomingValue, root.Name);
            Assert.Equal(incomingTimestamp, property.Reference.TryGetWriteTimestamp());
        }
        else
        {
            await transaction.CommitAsync(CancellationToken.None);
            Assert.Empty(transaction.GetPendingChanges());
            Assert.Equal("Pending", writer.SourceValue);
            Assert.Equal("Pending", root.Name);
        }
    }

    private sealed class AcceptingWriter(object source) : ITransactionWriter
    {
        public string? SourceValue { get; private set; }

        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes, TransactionRequirement requirement,
            CancellationToken cancellationToken)
        {
            var change = Assert.Single(changes.ToArray());
            SourceValue = change.GetNewValue<string>();
            changes.Span[0] = change.WithOrigin(ChangeOrigin.Confirmed(source));
            return new(new SourceWriteResult([change], [], [], null));
        }

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written, object? revertState,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
