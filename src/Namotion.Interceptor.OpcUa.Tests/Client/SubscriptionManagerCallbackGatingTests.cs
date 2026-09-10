using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class SubscriptionManagerCallbackGatingTests
{
    [Fact]
    public void WhenDataChangeArrivesBeforeSetupCompletes_ThenNotificationIsIgnored()
    {
        // Arrange
        var harness = SubscriptionManagerTestHarness.Create();
        harness.RegisterMonitoredItem(clientHandle: 7, propertyName: "Value");

        var notification = SubscriptionManagerTestHarness.CreateNotification(clientHandle: 7, value: 42d);

        // Act: deliver before setup completes, so the gate is still closed
        harness.Manager.OnFastDataChange(new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()), notification, []);

        // Assert
        Assert.Equal(0d, harness.GetValue("Value"));

        // Act: complete setup (which opens the gate) and deliver the same notification
        harness.Manager.CompleteSetup([]);
        harness.Manager.OnFastDataChange(new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()), notification, []);

        // Assert
        Assert.Equal(42d, harness.GetValue("Value"));
    }

    [Fact]
    public void WhenSubscriptionsAreTransferred_ThenTheCallbackGateStaysOpen()
    {
        // Arrange: setup has completed, then the reconnect handler hands over a transferred subscription.
        var harness = SubscriptionManagerTestHarness.Create();
        harness.RegisterMonitoredItem(clientHandle: 7, propertyName: "Value");
        harness.Manager.CompleteSetup([]);
        harness.Manager.UpdateTransferredSubscriptions([new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions())]);

        // Act
        harness.Manager.OnFastDataChange(
            new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()),
            SubscriptionManagerTestHarness.CreateNotification(clientHandle: 7, value: 42d),
            []);

        // Assert
        Assert.Equal(42d, harness.GetValue("Value"));
    }
}
