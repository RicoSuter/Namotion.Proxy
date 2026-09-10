using System.Reactive.Concurrency;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class PropertyChangeInterceptorTests
{
    [Fact]
    public void WhenQueueSubscriberExists_ThenWriteIsEnqueued()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        person.FirstName = "John";

        // Assert
        Assert.True(subscription.TryDequeue(out var change, new CancellationTokenSource(1000).Token));
        Assert.Equal(nameof(Person.FirstName), change.Property.Name);
        Assert.Null(change.GetOldValue<string?>());
        Assert.Equal("John", change.GetNewValue<string?>());
    }

    [Fact]
    public void WhenObservableSubscriberExists_ThenWriteIsPublished()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        SubjectPropertyChange? captured = null;
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => captured = c);

        // Act
        person.FirstName = "John";

        // Assert
        Assert.NotNull(captured);
        Assert.Equal(nameof(Person.FirstName), captured.Value.Property.Name);
    }

    [Fact]
    public void WhenBothChannelsActive_ThenBothReceiveTheSameChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        SubjectPropertyChange? observed = null;
        using var observable = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => observed = c);
        using var queue = context.CreatePropertyChangeQueueSubscription();

        // Act
        person.FirstName = "John";

        // Assert
        Assert.True(queue.TryDequeue(out var dequeued, new CancellationTokenSource(1000).Token));
        Assert.NotNull(observed);
        Assert.Equal(observed.Value.Property, dequeued.Property);
    }

    [Fact]
    public void WhenLastObservableSubscriberLeaves_ThenGateCloses()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        var person = new Person(context);
        var received = 0;
        var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(_ => received++);

        // Act
        subscription.Dispose();
        person.FirstName = "John"; // no consumers left: must take the idle fast path

        // Assert: the gate actually closed (idle) and the write delivered nothing.
        Assert.True(interceptor.IsIdle);
        Assert.Equal(0, received);
    }

    [Fact]
    public void WhenObservableChurnedThenQueueChurned_ThenGateReclosesToIdle()
    {
        // Arrange: transient observable use (subscribe an Rx observer, then dispose it) leaves the
        // never-cleared _syncSubject field non-null. A later queue create/dispose must not resurrect
        // that observer-less subject into the dispatch state and keep the gate open forever.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var interceptor = context.GetService<PropertyChangeInterceptor>();

        // Act: churn the observable channel, then churn the queue channel.
        var observer = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(_ => { });
        observer.Dispose();

        var queue = context.CreatePropertyChangeQueueSubscription();
        queue.Dispose();

        // Assert: both channels are empty again, so the gate has re-closed to idle. Before the
        // ActiveSyncSubject gating this would be false (the stale subject was re-published).
        Assert.True(interceptor.IsIdle);
    }

    [Fact]
    public async Task WhenDisposedWhileConsumerWaits_ThenConsumerReturnsFalseWithoutBlocking()
    {
        // Arrange (lost-wakeup race fix)
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        using var consumerStarted = new ManualResetEventSlim(false);
        bool? result = null;

        var consumer = Task.Run(() =>
        {
            consumerStarted.Set();
            // Long cancellation bound: only a working completion wake (not the token) should end this.
            result = subscription.TryDequeue(out _, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        });

        // Act: dispose while the consumer is blocked (or about to block) on the empty queue.
        consumerStarted.Wait();
        subscription.Dispose();

        // Assert: the consumer wakes on completion well within the 30s token bound (no lost wakeup).
        // WaitAsync throws TimeoutException if the consumer did not return, failing the test.
        await consumer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result);
    }

    [Fact]
    public async Task WhenConcurrentWriteRacesSubscriptionDispose_ThenNoObjectDisposedException()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);

        // Act & Assert (enqueue-vs-dispose race fix): repeatedly race a producing write (which fans
        // out to subscription.Enqueue -> _signal.Set()) against Dispose. Because Dispose no longer
        // disposes _signal, Set() must never hit a disposed signal. Awaiting the writer rethrows any
        // ObjectDisposedException, failing the test.
        for (var i = 0; i < 1000; i++)
        {
            var subscription = context.CreatePropertyChangeQueueSubscription();
            using var start = new ManualResetEventSlim(false);
            var writer = Task.Run(() =>
            {
                start.Wait();
                person.FirstName = "John";
            });

            start.Set();
            subscription.Dispose();
            await writer;
        }
    }

    [Fact]
    public async Task WhenQueueSubscriptionCreatedWhileWriteInFlight_ThenCommittedChangeIsDelivered()
    {
        // Arrange: an existing subscription keeps the write on the active path; the blocker parks
        // the writer after the interceptor's pre-commit work and before the commit.
        var blocker = new BlockingWriteInterceptor();
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        context.WithService(() => blocker);
        var person = new Person(context);
        using var existing = context.CreatePropertyChangeQueueSubscription();

        // The write parks in the interceptor chain, so it must not wait for a pool thread.
        var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(
            () => { person.FirstName = "John"; });
        Assert.True(blocker.EnteredInnerChain.Wait(TimeSpan.FromSeconds(10)));

        // Act: subscribe while the write is in flight, then release the commit.
        using var late = context.CreatePropertyChangeQueueSubscription();
        blocker.ProceedWithCommit.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: the write committed after the late subscription was created, so it is delivered
        // (channel state is re-read post-commit).
        Assert.True(late.TryDequeue(out var change, new CancellationTokenSource(1000).Token));
        Assert.Equal("John", change.GetNewValue<string?>());
        Assert.Null(change.GetOldValue<string?>());
    }

    [Fact]
    public async Task WhenSecondObserverSubscribedWhileWriteInFlight_ThenBothObserversReceiveTheChange()
    {
        // Arrange: an existing observer keeps the dispatch state active; the blocker parks the
        // writer after the pre-commit work and before the commit.
        var blocker = new BlockingWriteInterceptor();
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        context.WithService(() => blocker);
        var person = new Person(context);
        SubjectPropertyChange? first = null;
        SubjectPropertyChange? second = null;
        var observable = context.GetPropertyChangeObservable(ImmediateScheduler.Instance);
        using var existing = observable
            .Subscribe(change => first = change);

        // The write parks in the interceptor chain, so it must not wait for a pool thread.
        var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(
            () => { person.FirstName = "John"; });
        Assert.True(blocker.EnteredInnerChain.Wait(TimeSpan.FromSeconds(10)));

        // Act: a second observer joins the same observable mid-write. Its subscription must reach
        // the interceptor's guaranteed subscribe path before the commit is released.
        using var late = observable
            .Subscribe(change => second = change);
        blocker.ProceedWithCommit.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: the write committed after the second Subscribe returned, so both observers
        // receive it, with the old value properly captured (active path).
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("John", second.Value.GetNewValue<string?>());
        Assert.Null(second.Value.GetOldValue<string?>());
    }

    [Fact]
    public void WhenNullObserverSubscribed_ThenThrowsAndInterceptorStaysIdle()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var interceptor = context.GetService<PropertyChangeInterceptor>();

        // Act & Assert: validation runs before the consumer count is published, so the failed
        // subscribe cannot permanently open the notification gate.
        Assert.Throws<ArgumentNullException>(() => interceptor.Subscribe(null!));
        Assert.True(interceptor.IsIdle);
    }

    [Fact]
    public void WhenDisposedWithBufferedItems_ThenConsumerDrainsThemBeforeFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var subscription = context.CreatePropertyChangeQueueSubscription();
        person.FirstName = "John";
        person.LastName = "Doe";

        // Act
        subscription.Dispose();

        // Assert: buffered items stay drainable after disposal, then the queue reports completion.
        Assert.True(subscription.TryDequeue(out var first, CancellationToken.None));
        Assert.Equal(nameof(Person.FirstName), first.Property.Name);
        Assert.True(subscription.TryDequeue(out var second, CancellationToken.None));
        Assert.Equal(nameof(Person.LastName), second.Property.Name);
        Assert.False(subscription.TryDequeue(out _, CancellationToken.None));
    }

    [Fact]
    public void WhenTokenAlreadyCancelled_ThenTryDequeueReturnsFalseEvenWithBufferedItems()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        person.FirstName = "John";
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act & Assert: cancellation takes priority over buffered items (documented contract);
        // the item remains available to a non-cancelled call.
        Assert.False(subscription.TryDequeue(out _, cancellation.Token));
        Assert.True(subscription.TryDequeue(out var change, CancellationToken.None));
        Assert.Equal("John", change.GetNewValue<string?>());
    }

    [Fact]
    public void WhenMultiplePropertiesChanged_ThenAllChangesAreDequeuedInOrder()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        person.FirstName = "John";
        person.LastName = "Doe";

        // Assert
        Assert.True(subscription.TryDequeue(out var change1, CancellationToken.None));
        Assert.Equal(nameof(Person.FirstName), change1.Property.Name);

        Assert.True(subscription.TryDequeue(out var change2, CancellationToken.None));
        Assert.Equal(nameof(Person.LastName), change2.Property.Name);
    }

    [Fact]
    public void WhenMultipleSubscriptionsExist_ThenAllReceiveChanges()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var subscription1 = context.CreatePropertyChangeQueueSubscription();
        using var subscription2 = context.CreatePropertyChangeQueueSubscription();

        // Act
        person.FirstName = "John";

        // Assert
        Assert.True(subscription1.TryDequeue(out var change1, CancellationToken.None));
        Assert.Equal("John", change1.GetNewValue<string?>());

        Assert.True(subscription2.TryDequeue(out var change2, CancellationToken.None));
        Assert.Equal("John", change2.GetNewValue<string?>());
    }

    [Fact]
    public async Task WhenWriteArrivesWhileConsumerWaits_ThenConsumerWakesWithItem()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        using var consumerStarted = new ManualResetEventSlim(false);
        SubjectPropertyChange? received = null;

        var consumer = Task.Run(() =>
        {
            consumerStarted.Set();
            // Long cancellation bound: only a producing write (not the token) should wake the consumer.
            if (subscription.TryDequeue(out var change, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token))
            {
                received = change;
            }
        });

        // Act: produce a change while the consumer is blocked (or about to block) on the empty queue.
        consumerStarted.Wait();
        person.FirstName = "John";

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => consumer.IsCompleted, message: "Consumer should have received change");
        Assert.NotNull(received);
        Assert.Equal("John", received.Value.GetNewValue<string?>());
    }

    [Fact]
    public void WhenSubscriptionDisposed_ThenSubsequentWriteIsNotDelivered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var subscription = context.CreatePropertyChangeQueueSubscription();

        person.FirstName = "Before";
        Assert.True(subscription.TryDequeue(out _, CancellationToken.None));

        // Act
        subscription.Dispose();
        person.FirstName = "After";

        // Assert
        Assert.False(subscription.TryDequeue(out _, CancellationToken.None));
    }

    [Fact]
    public void WhenSetValueFromSourceCalled_ThenOriginAndTimestampsPropagateThroughQueue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        var source = new object();
        var changedTimestamp = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);
        var receivedTimestamp = new DateTimeOffset(2026, 1, 15, 10, 30, 1, TimeSpan.Zero);

        // Act
        var property = new PropertyReference(person, nameof(Person.FirstName));
        property.SetValueFromSource(source, changedTimestamp, receivedTimestamp, "FromSource");

        // Assert
        Assert.True(subscription.TryDequeue(out var change, CancellationToken.None));
        Assert.Equal("FromSource", change.GetNewValue<string?>());
        Assert.Same(source, change.Origin.Source);
        Assert.Equal(changedTimestamp, change.ChangedTimestamp);
        Assert.Equal(receivedTimestamp, change.ReceivedTimestamp);
    }

    [Fact]
    public void WhenConcurrentProducers_ThenAllChangesAreDequeued()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        var changeCount = 100;

        // Act
        Parallel.For(0, changeCount, i =>
        {
            person.FirstName = $"Name{i}";
        });

        // Assert: Parallel.For has joined, so all changes are already enqueued; drain exactly
        // changeCount (instant, no timeout burned). The safety-net token only elapses if a change
        // was lost (fails cleanly instead of hanging). After dispose the queue is completed, so a
        // surplus change would still dequeue true, proving none-extra as well as all-delivered.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (var i = 0; i < changeCount; i++)
        {
            Assert.True(subscription.TryDequeue(out _, cts.Token));
        }

        subscription.Dispose();
        Assert.False(subscription.TryDequeue(out _, CancellationToken.None));
    }

    [Fact]
    public void WhenDisposedConcurrentlyFromManyThreads_ThenDisposeIsOneShotAndGateCloses()
    {
        // Arrange (atomic-dispose race fix): Dispose claims the owner with a single
        // Interlocked.Exchange, so only one caller may run the teardown even when many race.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        var subscription = context.CreatePropertyChangeQueueSubscription();
        Assert.False(interceptor.IsIdle);

        // Act: dispose from many threads at once.
        Parallel.For(0, 64, _ => subscription.Dispose());

        // Assert: no thread threw and the channel returned to idle. This pins the observable
        // contract (idempotent, no exceptions under a dispose storm) rather than the one-shot
        // mechanism itself: RemoveQueueSubscription is idempotent, so a non-atomic dispose would
        // also pass here. Kept as a regression guard against Dispose throwing or leaving state.
        Assert.True(interceptor.IsIdle);
        Assert.False(subscription.TryDequeue(out _, CancellationToken.None));
    }
}
