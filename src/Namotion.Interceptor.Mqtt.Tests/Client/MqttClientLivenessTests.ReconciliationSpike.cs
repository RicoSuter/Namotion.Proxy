using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using Namotion.Interceptor.Connectors.Reconciliation;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Mqtt.Tests.Client;

public partial class MqttClientLivenessTests
{
    [Fact]
    public async Task WhenApplicationReaderIsProvided_ThenDelayedMqttMessageIsVerifiedBeforeApplying()
    {
        // Arrange: real MQTT transport; the authoritative read capability is supplied by the test application.
        var port = GetFreeTcpPort();
        await using var broker = CreateBroker(port);
        var brokerRoot = (LivenessTestRoot)broker.RootSubject;
        await using var source = CreateClientSource(port, reconciliationReader: new BrokerModelReader(brokerRoot));
        var root = (LivenessTestRoot)source.RootSubject;
        await broker.StartAsync(CancellationToken.None);
        await source.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => root.Name == "Initial" && !source.PropertyWriter.HasPendingReconciliation);
            root.Name = "Current";
            await AsyncTestHelpers.WaitUntilAsync(() => brokerRoot.Name == "Current" && !source.PropertyWriter.HasPendingReconciliation);
            var observed = new ConcurrentQueue<string>();
            using var changes = ((IInterceptorSubject)root).Context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
                .Subscribe(change => { if (change.Property.Name == nameof(LivenessTestRoot.Name)) observed.Enqueue(change.GetNewValue<string>()); });
            var readCount = source.PropertyWriter.VerificationReadCount;
            var callback = Assert.Single(GetApplicationMessageHandlers(GetCurrentClient(source)));

            // Act
            await callback(CreateMessageReceivedEventArgs("Old"));
            await AsyncTestHelpers.WaitUntilAsync(() => source.PropertyWriter.VerificationReadCount > readCount && !source.PropertyWriter.HasPendingReconciliation);

            // Assert
            Assert.Equal("Current", root.Name);
            Assert.DoesNotContain("Old", observed);
            brokerRoot.Name = "External";
            await AsyncTestHelpers.WaitUntilAsync(() => root.Name == "External" && !source.PropertyWriter.HasPendingReconciliation);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            await broker.StopAsync(CancellationToken.None);
        }
    }

    // This deliberately does not claim that MQTT itself has a read service.
    private sealed class BrokerModelReader(LivenessTestRoot root) : ISourcePropertyReader
    {
        public ValueTask<IReadOnlyList<SourcePropertyValue>> ReadPropertiesAsync(ReadOnlyMemory<PropertyReference> properties, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<SourcePropertyValue>>(
                properties.ToArray().Select(property => new SourcePropertyValue(property, root.Name)).ToArray());
    }
}
