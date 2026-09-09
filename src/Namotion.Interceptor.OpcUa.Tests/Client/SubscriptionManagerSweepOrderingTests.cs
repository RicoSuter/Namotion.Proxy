using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.Registry;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class SubscriptionManagerSweepOrderingTests
{
    [Fact]
    public void WhenSubjectDetachesBeforeItsItemsAreRegistered_ThenTheSweepStillDropsThem()
    {
        // Arrange: enter the state CreateBatchedSubscriptionsAsync establishes, with the callback
        // gate closed, the monitored-item dictionary cleared, and setup marked as in progress. Both
        // child subjects are still in the registry, because the lifecycle interceptor raises
        // SubjectDetaching before the registry handler runs, so a subject detaching right now looks
        // exactly like one that is staying.
        var harness = SubscriptionManagerTestHarness.Create();
        harness.Manager.BeginSetup();

        var survivorProperty = harness.CreateAttachedChildSubjectProperty("Kept");
        var detachedProperty = harness.CreateAttachedChildSubjectProperty("Gone");
        Assert.NotNull(detachedProperty.Subject.TryGetRegisteredSubject());

        // Act: the detach callback arrives mid-setup, so it finds no items to remove.
        harness.Manager.RemoveItemsForSubject(detachedProperty.Subject);

        // Setup then tracks the items, which is what makes the sweep the only remaining chance
        // to drop them.
        var survivorItem = harness.TrackMonitoredItem(clientHandle: 1, survivorProperty);
        var detachedItem = harness.TrackMonitoredItem(clientHandle: 2, detachedProperty);

        harness.Manager.CompleteSetup([survivorItem, detachedItem]);

        // Assert: the detached subject's handle (2) is gone and the survivor's (1) remains, and the
        // count agrees with the dictionary.
        Assert.False(harness.Manager.MonitoredItems.ContainsKey(2));
        Assert.True(harness.Manager.MonitoredItems.ContainsKey(1));
        Assert.Equal(1, harness.Manager.MonitoredItemCount);

        // Assert: the sweep drained what it recorded and recorded nothing while removing.
        Assert.Equal(0, harness.Manager.DetachedDuringSetupCountForTesting);
    }

    [Fact]
    public void WhenSubjectDetachesDuringSetup_ThenItIsSweptAndNeverRegisteredForReadAfterWrite()
    {
        // Arrange: mid-setup, the state production is in when CompleteSetup runs. Both items pass
        // the read-after-write filter (requested interval zero, revised positive), so only the
        // sweep keeps the detached one out of the index.
        var harness = SubscriptionManagerTestHarness.Create();
        harness.Manager.BeginSetup();

        var survivorItem = harness.RegisterMonitoredItem(clientHandle: 1, propertyName: "Kept", revisedSamplingIntervalMs: 100);
        var detachedItem = harness.RegisterMonitoredItemThenDetachSubject(clientHandle: 2, propertyName: "Gone", revisedSamplingIntervalMs: 100);

        // Act: the whole completion sequence, so reordering sweep and registration fails the test.
        // The item list still contains both handles, as in production, where it comes from the SDK
        // subscriptions and the sweep only prunes the manager's own dictionary.
        harness.Manager.CompleteSetup([survivorItem, detachedItem]);

        // Assert: the sweep removed the detached subject's handle (2) and kept the survivor (1)
        Assert.False(harness.Manager.MonitoredItems.ContainsKey(2));
        Assert.True(harness.Manager.MonitoredItems.ContainsKey(1));
        Assert.Equal(1, harness.Manager.MonitoredItemCount);

        // Assert: only the survivor is registered for read-after-write
        Assert.Equal(1, harness.ReadAfterWriteManager.TrackedPropertyCount);

        // Assert: removing through the sweep recorded nothing.
        Assert.Equal(0, harness.Manager.DetachedDuringSetupCountForTesting);
    }

    [Fact]
    public async Task WhenAnItemWasSweptForADetachedSubject_ThenEscalationDoesNotPollIt()
    {
        // Arrange: the SDK subscription still holds an item whose handle the sweep dropped, and the
        // item keeps failing with a status the health monitor would retry.
        var harness = SubscriptionManagerTestHarness.Create(withPollingFallback: true);
        var sweptItem = new MonitoredItem(NullTelemetryContext.Instance)
        {
            StartNodeId = new NodeId(1u, 2),
            AttributeId = Opc.Ua.Attributes.Value,
            Handle = harness.CreateAttachedChildSubjectProperty("Gone")
        };
        sweptItem.SetError(new ServiceResult(StatusCodes.BadTimeout));

        var subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        subscription.AddItem(sweptItem);
        harness.Manager.UpdateTransferredSubscriptions([subscription]);

        // Act: enough heal ticks to escalate a tracked item.
        for (var attempt = 0; attempt < SubscriptionManager.MaxHealAttemptsBeforeEscalation; attempt++)
        {
            await harness.Manager.EscalatePersistentlyFailedItemsAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal(0, harness.PollingManager!.PollingItemCount);
    }
}
