using System;
using System.Collections.Generic;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Paths;
using Namotion.Interceptor.Tracking.Tests.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

[Collection(PerPropertySubscriptionCollection.Name)]
public class PathDisposeTests
{
    public PathDisposeTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenDisposed_ThenDeliveryStopsAllSegmentsTornDownAndDoubleDisposeIsNoOp()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var father = new Person { FirstName = "Joe" };
        var person = new Person(context) { Father = father };
        var count = 0;
        var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) => count++, SubjectPathValidation.Full);
        Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount()); // two segments, two listeners

        // Act
        father.FirstName = "Jack"; // delivered before dispose
        subscription.Dispose();
        father.FirstName = "Max"; // not delivered: listeners torn down

        // Assert: exactly one delivery, all segment listeners torn down (count back to zero).
        Assert.Equal(1, count);
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());

        // White-box: the reserved listeners key is gone from every watched segment's subject.
        Assert.False(new PropertyReference(person, nameof(Person.Father))
            .TryGetPropertyData(PropertyChangeSubscription.ListenersKey, out _));
        Assert.False(new PropertyReference(father, nameof(Person.FirstName))
            .TryGetPropertyData(PropertyChangeSubscription.ListenersKey, out _));

        // Act & Assert: double dispose is a no-op (idempotent, count stays zero, no throw).
        subscription.Dispose();
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenDisposed_ThenCurrentReturnsUnresolvedDefault()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context) { Father = new Person { FirstName = "Joe" } };
        var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) => { }, SubjectPathValidation.Full);
        Assert.True(subscription.Current.IsResolved); // resolved while live

        // Act
        subscription.Dispose();

        // Assert
        Assert.False(subscription.Current.IsResolved);
    }

    [Fact]
    public void WhenQueuedEventsStrandedByThrow_ThenDisposeDropsThemAndTheyAreNotDeliveredLater()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var father = new Person { FirstName = "Joe" };
        var person = new Person(context) { Father = father };

        var delivered = new List<SubjectPathChange<string?>>();
        var nestedWriteDone = false;
        var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> change) =>
        {
            delivered.Add(change);
            if (change.NewState.GetValueOrDefault() == "Jack")
            {
                if (!nestedWriteDone)
                {
                    nestedWriteDone = true;
                    father.FirstName = "Zed"; // enqueues Jack->Zed into the active drain
                }

                throw new InvalidOperationException("boom");
            }
        }, SubjectPathValidation.Full);

        // Act: the throwing callback strands the queued Jack->Zed event in _pending (drainer flag reset).
        Assert.Throws<InvalidOperationException>(() => father.FirstName = "Jack");
        Assert.Single(delivered); // only Joe->Jack delivered; Jack->Zed stranded

        // Act: dispose drops the stranded backlog.
        subscription.Dispose();

        // A later write must not recover the dropped backlog (nor deliver anything): the subscription is gone.
        father.FirstName = "Max";

        // Assert
        Assert.Single(delivered);
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenDisposedMidDrain_ThenRemainingQueuedBacklogIsDroppedNotDelivered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var father = new Person { FirstName = "Joe" };
        var person = new Person(context) { Father = father };

        SubjectPathSubscription<string?> subscription = null!;
        var delivered = new List<string?>();
        var nestedWritesDone = false;
        subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> change) =>
        {
            var value = change.NewState.GetValueOrDefault();
            delivered.Add(value);
            if (value == "Jack" && !nestedWritesDone)
            {
                // Two nested writes queue a backlog (Jack->Zed, Zed->Max) behind the active drainer.
                nestedWritesDone = true;
                father.FirstName = "Zed";
                father.FirstName = "Max";
            }
            else if (value == "Zed")
            {
                // Dispose mid-drain, before the loop dequeues the queued Zed->Max: the disposed drain loop
                // must drop the remaining backlog rather than deliver it after disposal.
                subscription.Dispose();
            }
        }, SubjectPathValidation.Full);

        // Act
        father.FirstName = "Jack";

        // Assert: Joe->Jack (direct) then Jack->Zed (first backlog) delivered; the queued Zed->Max is dropped
        // by dispose. Without the disposed drop the loop would dequeue and deliver Zed->Max after disposal.
        Assert.Equal(new[] { "Jack", "Zed" }, delivered);
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public async Task WhenCallbackDisposesSubscription_ThenNoFurtherDeliveryWithoutDeadlockOrException()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var father = new Person { FirstName = "Joe" };
        var person = new Person(context) { Father = father };

        SubjectPathSubscription<string?> subscription = null!;
        var count = 0;
        subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) =>
        {
            count++;
            subscription.Dispose(); // self-dispose from inside the callback (runs outside _lock)
        }, SubjectPathValidation.Full);

        // Act: the write runs on a worker thread bounded by a timeout so a deadlock regression fails fast
        // instead of hanging the suite. Self-dispose acquires the lock the drainer does not hold, so it
        // completes; the drain loop then observes _disposed and stops.
        await Task.Run(() => father.FirstName = "Jack").WaitAsync(TimeSpan.FromSeconds(10));

        // A subsequent write to the former leaf delivers nothing (listeners torn down, disposal observed).
        father.FirstName = "Max";

        // Assert
        Assert.Equal(1, count);
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
        Assert.False(subscription.Current.IsResolved);
    }

    [Fact]
    public void WhenBuildThrowsAtDeepSegment_ThenInstalledSegmentsDisposedAndCountRestored()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var father = new Person { FirstName = "Joe" };
        var person = new Person(context) { Father = father };

        // Occupy the leaf property's reserved listeners key with a foreign value so the second segment
        // install fails loud (InvalidOperationException) during BuildFrom, after the first is installed.
        new PropertyReference(father, nameof(Person.FirstName))
            .SetPropertyData(PropertyChangeSubscription.ListenersKey, "foreign");

        // Act & Assert: the throw propagates out of the rejected subscribe, and the already-installed first
        // segment listener is disposed so the process-wide count is rolled back to zero (no partial leak).
        Assert.Throws<InvalidOperationException>(() =>
            person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) => { }, SubjectPathValidation.Full));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public async Task WhenDisposeRacesDirectDispatchCallback_ThenInFlightCallbackCompletesAndNoNewDeliveryStarts()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var father = new Person { FirstName = "Joe" };
        var person = new Person(context) { Father = father };

        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var callbackCount = 0;
        Exception? callbackFailure = null;

        var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) =>
        {
            Interlocked.Increment(ref callbackCount);
            entered.Set(); // signal the callback is mid-flight (dispatched outside _lock)
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                callbackFailure = new TimeoutException("release gate never opened");
            }
        }, SubjectPathValidation.Full);

        // Act: trigger a delivery on a worker thread; the callback parks mid-flight outside the lock.
        var writer = Task.Run(() => father.FirstName = "Jack");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the direct-dispatch callback never entered");

        // Dispose on this thread while the callback is parked: it acquires the lock the drainer does not
        // hold, sets disposed, tears down, and clears _pending. No ObjectDisposedException, no torn state.
        subscription.Dispose();

        // Release the parked callback; it completes its stack-local event, then the drain loop observes
        // _disposed and starts no new delivery.
        release.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Null(callbackFailure);
        Assert.Equal(1, callbackCount); // exactly the one in-flight callback; no delivery started after dispose
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
        Assert.False(subscription.Current.IsResolved);
    }
}
