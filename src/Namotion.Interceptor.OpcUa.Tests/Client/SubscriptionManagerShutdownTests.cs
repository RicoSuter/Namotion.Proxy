using Namotion.Interceptor.OpcUa.Client.Connection;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Disposal of a <see cref="SubscriptionManager"/> is terminal: the owning session manager disposes
/// at most once and never replaces its subscription manager. The shutdown flag that suppresses
/// inbound data change callbacks must therefore be monotonic, so a reconnect racing disposal cannot
/// resume callbacks on a disposed manager.
/// </summary>
public class SubscriptionManagerShutdownTests
{
    [Fact]
    public async Task WhenDisposedManagerRunsSubscriptionSetup_ThenCallbacksStaySuppressed()
    {
        // Arrange
        var harness = SubscriptionManagerTestHarness.Create();
        await harness.Manager.DisposeAsync();

        // Act: a reconnect that starts after disposal runs subscription setup again. Passing no
        // monitored items makes the null session safe: the batching loop that dereferences the
        // session never executes for an empty item list.
        await harness.Manager.CreateBatchedSubscriptionsAsync([], null!, CancellationToken.None);
        harness.RegisterMonitoredItem(clientHandle: 7, propertyName: "Value");
        harness.Manager.OnFastDataChange(
            new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()),
            SubscriptionManagerTestHarness.CreateNotification(clientHandle: 7, value: 42d),
            []);

        // Assert
        Assert.NotEqual(42d, harness.GetValue("Value"));
    }
}
