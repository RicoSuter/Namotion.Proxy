using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

[Trait("Category", "Integration")]
public class OpcUaSourceReconciliationSpikeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task WhenDelayedCallbackFollowsTransaction_ThenReadbackPreservesStateAndAcceptsLaterSourceChange()
    {
        // Arrange
        var logger = new TestLogger(output);
        using var port = await OpcUaTestPortPool.AcquireAsync();
        await using var server = new OpcUaTestServer<SelfWriteTestRoot>(logger);
        await server.StartAsync(context => new SelfWriteTestRoot(context), (_, root) => root.Value = "initial",
            baseAddress: port.BaseAddress, certificateStoreBasePath: port.CertificateStoreBasePath);
        await using var client = new OpcUaTestClient<SelfWriteTestRoot>(logger, configuration =>
        {
            configuration.EnableExperimentalSourceReconciliation = true;
            configuration.SubscriptionSequentialPublishing = true;
        });
        await client.StartAsync(context => new SelfWriteTestRoot(context), root => root.Value == "initial",
            serverUrl: port.ServerUrl, certificateStoreBasePath: port.CertificateStoreBasePath + "-client");
        var source = (OpcUaSubjectClientSource)client.Source!;
        using (var transaction = await client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            client.Root!.Value = "committed";
            await transaction.CommitAsync(CancellationToken.None);
        }
        await AsyncTestHelpers.WaitUntilAsync(() => server.Root!.Value == "committed" && !source.PropertyWriter.HasPendingReconciliation);
        var subscription = source.SessionManager!.CurrentSession!.Subscriptions.Single();
        var item = subscription.MonitoredItems.Single();
        var changes = new ConcurrentQueue<string?>();
        using var changeSubscription = client.Context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => { if (change.Property.Name == nameof(SelfWriteTestRoot.Value)) changes.Enqueue(change.GetNewValue<string?>()); });
        var readsBefore = source.PropertyWriter.VerificationReadCount;

        // Act: inject a delayed SDK callback, then use a real verification read over OPC UA.
        source.SessionManager.SubscriptionManager.OnFastDataChange(subscription, new DataChangeNotification
        {
            MonitoredItems = [new MonitoredItemNotification
            {
                ClientHandle = item.ClientHandle,
                Value = new DataValue { Value = "initial", SourceTimestamp = DateTime.UtcNow.AddMinutes(-1), StatusCode = StatusCodes.Good }
            }]
        }, []);
        await AsyncTestHelpers.WaitUntilAsync(() => source.PropertyWriter.VerificationReadCount > readsBefore && !source.PropertyWriter.HasPendingReconciliation);

        // Assert
        Assert.Equal("committed", client.Root!.Value);
        Assert.DoesNotContain("initial", changes);
        server.Root!.Value = "external";
        await AsyncTestHelpers.WaitUntilAsync(() => client.Root.Value == "external" && !source.PropertyWriter.HasPendingReconciliation);
        Assert.Equal("external", client.Root.Value);
    }
}
