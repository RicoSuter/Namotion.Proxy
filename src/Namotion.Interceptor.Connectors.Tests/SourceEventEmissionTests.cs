using System.Collections.Concurrent;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceEventEmissionTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithSourceMonitoring();

    [Fact]
    public async Task WhenAPropertyIsClaimed_ThenPropertyClaimedIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source = new TestStateSource(person);

        // Act
        var claimed = property.SetSource(source);

        // Assert
        Assert.True(claimed);
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.PropertyClaimed));
        var claimEvent = received.First(e => e.Kind == SourceEventKind.PropertyClaimed);
        Assert.Equal(SourceState.Unclaimed, claimEvent.OldState);
        Assert.Equal(SourceState.Synchronizing, claimEvent.NewState);
    }

    [Fact]
    public async Task WhenTheSameSourceReclaims_ThenNothingIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source = new TestStateSource(person);
        property.SetSource(source);
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        var claimed = property.SetSource(source);

        // Assert
        Assert.True(claimed);
        // Delivery is asynchronous, so an empty queue proves nothing on its own: a wrongly published
        // event may simply not have arrived. Publish a known event afterwards and wait for it; once
        // that has been delivered, anything enqueued before it would have been delivered too.
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Property?.Name == nameof(Person.LastName)));
        Assert.DoesNotContain(received, e => e.Property?.Name == nameof(Person.FirstName));
    }

    [Fact]
    public async Task WhenADifferentSourceClaimsAnOwnedProperty_ThenTheClaimIsRejectedAndNothingIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        property.SetSource(new TestStateSource(person));
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        var claimed = property.SetSource(new TestStateSource(person));

        // Assert
        Assert.False(claimed);
        // Same marker technique as above: an asynchronous absence needs a delivered successor.
        await SettleDeliveryAsync(person, received);
        Assert.DoesNotContain(received, e => e.Property?.Name == nameof(Person.FirstName));
    }

    [Fact]
    public async Task WhenOwnershipIsActuallyRemoved_ThenPropertyReleasedIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source = new TestStateSource(person);
        property.SetSource(source);
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        var removed = property.RemoveSource(source);

        // Assert
        Assert.True(removed);
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.PropertyReleased));
        var releaseEvent = received.First(e => e.Kind == SourceEventKind.PropertyReleased);
        Assert.Equal(SourceState.Unclaimed, releaseEvent.NewState);
    }

    [Fact]
    public async Task WhenRemovingAPropertyThatIsNotOwned_ThenNothingIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source = new TestStateSource(person);
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        var removed = property.RemoveSource(source);

        // Assert
        Assert.False(removed);
        // Delivery is asynchronous, so an empty queue proves nothing on its own: publish a known
        // event afterwards and wait for it, then anything wrongly published earlier would already
        // have been delivered too.
        await SettleDeliveryAsync(person, received);
        Assert.DoesNotContain(received, e => e.Kind == SourceEventKind.PropertyReleased);
    }

    [Fact]
    public async Task WhenRemovingAPropertyOwnedByADifferentSource_ThenNothingIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var owningSource = new TestStateSource(person);
        property.SetSource(owningSource);
        var otherSource = new TestStateSource(person);
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        var removed = property.RemoveSource(otherSource);

        // Assert
        Assert.False(removed);
        Assert.True(property.TryGetSource(out var stillOwning));
        Assert.Same(owningSource, stillOwning);
        await SettleDeliveryAsync(person, received);
        Assert.DoesNotContain(received, e => e.Kind == SourceEventKind.PropertyReleased);
    }

    [Fact]
    public async Task WhenNoMonitorIsReachable_ThenClaimingDoesNotThrowAndPublishesNothing()
    {
        // Arrange
        // "No monitor reachable" itself has nothing to observe directly: PublishOwnershipChange's
        // null-monitor early return publishes nothing, so no assertion here can distinguish the
        // guard existing from it being deleted. What IS a real, checkable claim is that this claim,
        // made in a context with no monitor, never reaches an
        // unrelated, independently monitored context: the monitor lookup stays correctly scoped to
        // the property's subject's attached context rather than leaking somewhere global.
        var isolatedContext = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var isolatedPerson = new Person(isolatedContext);
        var isolatedProperty = new PropertyReference(isolatedPerson, nameof(Person.FirstName));

        var monitoredContext = CreateContext();
        var monitor = monitoredContext.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        var claimed = isolatedProperty.SetSource(new TestStateSource(isolatedPerson));

        // Assert
        Assert.True(claimed);
        var markerPerson = new Person(monitoredContext);
        new PropertyReference(markerPerson, nameof(Person.LastName)).SetSource(new TestStateSource(markerPerson));
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Property?.Name == nameof(Person.LastName)));
        Assert.DoesNotContain(received, e => ReferenceEquals(e.Property?.Subject, isolatedPerson));
    }

    [Fact]
    public void WhenTheSubjectIsStillAttached_ThenCurrentStateReadsThroughToTheSource()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source = new TestStateSource(person);
        property.SetSource(source);
        // Captured while the source is still Synchronizing, so NewState here is Synchronizing - a
        // regression that made CurrentState just return NewState instead of re-resolving fresh
        // would still pass if the source never moved on after capture. It does, below.
        var sourceEvent = new SourceEvent(
            SourceEventKind.PropertyClaimed, source, property,
            SourceState.Unclaimed, SourceState.Synchronizing, DateTimeOffset.UtcNow);

        // Act - the source synchronizes after the event was captured.
        source.ReportSynchronized();
        var current = sourceEvent.CurrentState;

        // Assert
        Assert.Equal(SourceState.Synchronized, current);
        Assert.Equal(SourceState.Synchronizing, sourceEvent.NewState);
    }

    /// <summary>
    /// Settles asynchronous delivery so an absence can be asserted: claims a marker property and
    /// waits for its event. Delivery per subscription is FIFO, so once that arrives, anything
    /// wrongly published earlier would already have arrived too.
    /// </summary>
    private static async Task SettleDeliveryAsync(Person person, ConcurrentQueue<SourceEvent> received)
    {
        new PropertyReference(person, nameof(Person.LastName)).SetSource(new TestStateSource(person));
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Property?.Name == nameof(Person.LastName)));
    }
}
