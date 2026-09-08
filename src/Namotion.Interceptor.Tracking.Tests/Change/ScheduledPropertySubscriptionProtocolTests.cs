using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class ScheduledPropertySubscriptionProtocolTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    public ScheduledPropertySubscriptionProtocolTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenChangesAreWritten_ThenOneDrainDeliversInOrderAndSettlesToZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()), scheduler);

        // Act
        person.FirstName = "one";
        person.FirstName = "two";
        person.FirstName = "three";

        // Assert
        Assert.Equal(1, scheduler.ScheduleCallCount);
        Assert.Equal(3, subscription.PendingCount);

        scheduler.RunUntilIdle();

        Assert.Equal(["one", "two", "three"], received);
        Assert.Equal(0, subscription.WorkInProgressForTests);
        Assert.Equal(0, subscription.PendingCount);
    }

    [Fact]
    public void WhenMoreThanOneBatchIsQueued_ThenTheDrainYieldsAndHandsOffInsteadOfLooping()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var delivered = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => delivered++, scheduler);

        var total = ScheduledPropertySubscription.MaxBatch + 10;

        // Act
        for (var i = 0; i < total; i++)
        {
            person.FirstName = i.ToString();
        }

        var firstBatch = scheduler.RunOne();

        // Assert
        Assert.True(firstBatch);
        Assert.Equal(ScheduledPropertySubscription.MaxBatch, delivered);
        Assert.Equal(2, scheduler.ScheduleCallCount);

        scheduler.RunUntilIdle();
        Assert.Equal(total, delivered);
        Assert.Equal(0, subscription.WorkInProgressForTests);
    }

    [Fact]
    public async Task WhenAnAcceptedInlineSchedulerDrainsMultipleBatches_ThenScheduleCallsDoNotNest()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new DepthTrackingInlineScheduler();
        using var firstDeliveryEntered = new ManualResetEventSlim();
        using var releaseFirstDelivery = new ManualResetEventSlim();
        var waitTimeoutCount = 0;
        var deliveryCount = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    if (Interlocked.Increment(ref deliveryCount) == 1)
                    {
                        firstDeliveryEntered.Set();
                        if (!releaseFirstDelivery.Wait(WaitTimeout))
                        {
                            Interlocked.Increment(ref waitTimeoutCount);
                        }
                    }
                },
                scheduler);

        var additionalChanges = (ScheduledPropertySubscription.MaxBatch * 2) + 10;
        var firstWriter = Task.Run(() => person.FirstName = "initial");

        try
        {
            Assert.True(firstDeliveryEntered.Wait(WaitTimeout), "the first delivery did not enter");

            // Act
            await Task.Run(() =>
            {
                for (var index = 0; index < additionalChanges; index++)
                {
                    person.FirstName = index.ToString();
                }
            }).WaitAsync(WaitTimeout);
        }
        finally
        {
            releaseFirstDelivery.Set();
            await firstWriter.WaitAsync(WaitTimeout);
        }

        // Assert
        Assert.Equal(additionalChanges + 1, deliveryCount);
        Assert.Equal(3, scheduler.ScheduleCallCount);
        Assert.Equal(1, scheduler.MaximumScheduleDepth);
        Assert.Equal(0, Volatile.Read(ref waitTimeoutCount));
        Assert.Equal(0, subscription.PendingCount);
        Assert.Equal(0, subscription.WorkInProgressForTests);
    }

    [Fact]
    public void WhenAnInlineSchedulerRunsThenThrowsAfterReentrantDisposal_ThenDisposalWinsAndTheWriterDoesNotSeeTheFailure()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new DepthTrackingInlineScheduler(throwAfterAction: true);
        var errors = new List<Exception>();
        var deliveryCount = 0;
        ScheduledPropertySubscription? subscription = null;

        subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    deliveryCount++;
                    subscription!.Dispose();
                },
                scheduler,
                errors.Add);

        // Act
        var escaped = Record.Exception(() => person.FirstName = "one");

        // Assert
        Assert.Equal(1, deliveryCount);
        Assert.Null(escaped);
        Assert.IsType<InvalidOperationException>(Assert.Single(errors));
        Assert.False(subscription.IsFaulted);
        Assert.Equal(1, scheduler.ScheduleCallCount);
        Assert.Equal(1, scheduler.MaximumScheduleDepth);
        Assert.Equal(0, subscription.PendingCount);
        Assert.Equal(0, subscription.WorkInProgressForTests);
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenAnObserverWritesTheSamePropertyDuringDelivery_ThenTheSuccessorIsDeliveredInTheSameBatch()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange change) =>
                {
                    received.Add(change.GetNewValue<string?>());
                    if (received.Count == 1)
                    {
                        person.FirstName = "two";
                    }
                },
                scheduler);

        // Act
        person.FirstName = "one";
        scheduler.RunUntilIdle();

        // Assert
        Assert.Equal(["one", "two"], received);
        Assert.Equal(1, scheduler.ScheduleCallCount);
        Assert.Equal(0, subscription.WorkInProgressForTests);
        Assert.Equal(0, subscription.PendingCount);
    }

    [Fact]
    public async Task WhenManyWritersRaceOneProperty_ThenTheObserverIsNeverReenteredAndNothingIsLost()
    {
        // Arrange
        const int writers = 8;
        const int writesPerWriter = 2_000;

        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);

        using var allDelivered = new CountdownEvent(writers * writesPerWriter);
        var observer = new ReentrancyProbeObserver(allDelivered);
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(observer, System.Reactive.Concurrency.Scheduler.Default);

        // Act
        await Task.WhenAll(Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            for (var i = 0; i < writesPerWriter; i++)
            {
                person.FirstName = $"{writer}-{i}";
            }
        })));

        Assert.True(
            allDelivered.Wait(TimeSpan.FromSeconds(30)),
            $"only {observer.DeliveryCount} of {writers * writesPerWriter} arrived");

        // Assert
        Assert.Equal(0, observer.ReentrancyCount);
        Assert.Equal(writers * writesPerWriter, observer.DeliveryCount);
        Assert.Equal(0, subscription.PendingCount);
    }

    [Fact]
    public void WhenTheObserverThrows_ThenTheCounterStillSettlesAndDeliveryContinues()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var errors = new List<Exception>();
        var deliveries = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    deliveries++;
                    if (deliveries == 1)
                    {
                        throw new InvalidOperationException("observer failed");
                    }
                },
                scheduler,
                errors.Add);

        // Act
        person.FirstName = "one";
        person.FirstName = "two";
        scheduler.RunUntilIdle();

        // Assert
        Assert.Equal(2, deliveries);
        Assert.Single(errors);
        Assert.Equal(0, subscription.WorkInProgressForTests);
        Assert.Equal(0, subscription.PendingCount);
    }

    [Fact]
    public void WhenDisposedBeforeTheQueueDrains_ThenQueuedChangesAreDroppedAndReleased()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()), scheduler);

        person.FirstName = "one";
        person.FirstName = "two";

        Assert.Equal(2, subscription.PendingCount);

        // Act
        subscription.Dispose();
        scheduler.RunUntilIdle();

        // Assert
        Assert.Empty(received);
        Assert.Equal(0, subscription.PendingCount);
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenDisposed_ThenTheObserverReferenceIsReleased()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();

        var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => { }, scheduler);

        Assert.False(subscription.IsObserverReleasedForTests);

        // Act
        subscription.Dispose();

        // Assert
        Assert.True(subscription.IsObserverReleasedForTests);
    }

    [Fact]
    public void WhenTheObserverDisposesItsOwnSubscriptionMidBatch_ThenTheRestOfTheBatchIsDroppedAndNothingEscapes()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var inner = new ControllableScheduler();
        var scheduler = new RecordingScheduler(inner);
        var received = new List<string?>();
        var errors = new List<Exception>();
        ScheduledPropertySubscription? subscription = null;

        subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange change) =>
                {
                    received.Add(change.GetNewValue<string?>());
                    subscription!.Dispose();
                },
                scheduler,
                errors.Add);

        person.FirstName = "one";
        person.FirstName = "two";
        person.FirstName = "three";

        // Act
        inner.RunUntilIdle();

        // Assert
        Assert.Equal(["one"], received);
        Assert.Empty(scheduler.Escaped);
        Assert.Empty(errors);
        Assert.True(subscription.IsObserverReleasedForTests);
        Assert.Equal(0, subscription.PendingCount);
    }

    [Fact]
    public void WhenTheObserverDisposesItsOwnSubscriptionAndThenThrows_ThenTheExceptionStillReachesOnError()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var inner = new ControllableScheduler();
        var scheduler = new RecordingScheduler(inner);
        var errors = new List<Exception>();
        ScheduledPropertySubscription? subscription = null;

        subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    subscription!.Dispose();
                    throw new InvalidOperationException("observer failed");
                },
                scheduler,
                errors.Add);

        // Act
        person.FirstName = "one";
        inner.RunUntilIdle();

        // Assert
        Assert.IsType<InvalidOperationException>(Assert.Single(errors));
        Assert.Empty(scheduler.Escaped);
    }

    [Fact]
    public void WhenTheSchedulerFaultsASubscription_ThenIsFaultedReportsItAndDisposalDoesNot()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var healthy = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => { }, new ControllableScheduler());
        using var faulting = new PropertyReference(person, nameof(Person.LastName))
            .Subscribe((in SubjectPropertyChange _) => { }, new ThrowingScheduler());

        Assert.False(healthy.IsFaulted);
        Assert.False(faulting.IsFaulted);

        // Act
        healthy.Dispose();
        person.LastName = "one";

        // Assert
        Assert.False(healthy.IsFaulted);
        Assert.True(faulting.IsFaulted);
    }

    [Fact]
    public void WhenOnErrorHandlesASchedulerFaultAndDisposes_ThenIsFaultedIsAlreadyTrueAndStaysTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        ScheduledPropertySubscription? subscription = null;
        bool? faultedInsideHandler = null;

        subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) => { },
                new ThrowingScheduler(),
                _ =>
                {
                    faultedInsideHandler = subscription!.IsFaulted;
                    subscription.Dispose();
                });

        // Act
        person.FirstName = "one";

        // Assert
        Assert.True(faultedInsideHandler, "the handler being told about the fault read IsFaulted as false");
        Assert.True(subscription.IsFaulted);
    }

    [Fact]
    public void WhenDisposedTwice_ThenTheProcessWideCountIsDecrementedOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var other = new Person(context);
        var scheduler = new ControllableScheduler();

        using var keepAlive = new PropertyReference(other, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => { }, scheduler);
        var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => { }, scheduler);

        // Act
        subscription.Dispose();
        subscription.Dispose();

        // Assert
        Assert.Equal(1, PropertyChangeSubscriptions.ReadSubscriptionCount());
        Assert.False(new PropertyReference(person, nameof(Person.FirstName))
            .TryGetPropertyData(PropertyChangeSubscription.ListenersKey, out _));
    }

    [Fact]
    public void WhenTheSchedulerThrows_ThenItIsReportedAndDoesNotReachTheWriter()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var errors = new List<Exception>();

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => { }, new ThrowingScheduler(), errors.Add);

        // Act
        person.FirstName = "one";

        // Assert
        Assert.Equal("one", person.FirstName);
        Assert.IsType<ObjectDisposedException>(Assert.Single(errors));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
        Assert.False(new PropertyReference(person, nameof(Person.FirstName))
            .TryGetPropertyData(PropertyChangeSubscription.ListenersKey, out _));
    }

    [Fact]
    public void WhenTheSchedulerAcceptsWorkAndNeverRunsIt_ThenTheSubscriptionGoesQuietWithoutReporting()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new BlackHoleScheduler();
        var errors = new List<Exception>();
        var delivered = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => delivered++, scheduler, errors.Add);

        // Act
        for (var i = 0; i < 20; i++)
        {
            person.FirstName = i.ToString();
        }

        // Assert
        Assert.Equal(0, delivered);
        Assert.Empty(errors);
        Assert.Equal(1, scheduler.ScheduleCallCount);
        Assert.Equal(20, subscription.PendingCount);
    }

    [Fact]
    public void WhenOnErrorThrows_ThenNothingEscapesIntoTheSchedulerAndDeliveryContinues()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var inner = new ControllableScheduler();
        var scheduler = new RecordingScheduler(inner);
        var deliveries = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    deliveries++;
                    throw new InvalidOperationException("observer failed");
                },
                scheduler,
                _ => throw new InvalidOperationException("error handler failed"));

        // Act
        person.FirstName = "one";
        person.FirstName = "two";
        inner.RunUntilIdle();

        // Assert
        Assert.Empty(scheduler.Escaped);
        Assert.Equal(2, deliveries);
        Assert.Equal(0, subscription.WorkInProgressForTests);
    }

    [Fact]
    public void WhenTheObserverThrowsWithNoErrorHandler_ThenItIsSwallowedAndDeliveryContinues()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var inner = new ControllableScheduler();
        var scheduler = new RecordingScheduler(inner);
        var deliveries = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    deliveries++;
                    throw new InvalidOperationException("observer failed");
                },
                scheduler);

        // Act
        person.FirstName = "one";
        person.FirstName = "two";
        inner.RunUntilIdle();

        // Assert
        Assert.Empty(scheduler.Escaped);
        Assert.Equal(2, deliveries);
    }

    [Fact]
    public void WhenTheWriterCarriesAmbientState_ThenTheObserverDoesNotSeeIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var ambient = new AsyncLocal<string?>();
        string? observed = "not-run";

        using var delivered = new CountdownEvent(1);
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    observed = ambient.Value;
                    delivered.Signal();
                },
                System.Reactive.Concurrency.Scheduler.Default);

        // Act
        ambient.Value = "writer-scope";
        person.FirstName = "one";

        // Assert
        Assert.True(delivered.Wait(TimeSpan.FromSeconds(30)));
        Assert.Null(observed);
    }

    [Fact]
    public async Task WhenDeliveryHappensDuringATransactionCommit_ThenTheObserverDoesNotSeeTheTransaction()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var person = new Person(context);
        object? observedTransaction = new object();

        using var delivered = new CountdownEvent(1);
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    observedTransaction = SubjectTransaction.Current;
                    delivered.Signal();
                },
                System.Reactive.Concurrency.Scheduler.Default);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "one";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.True(delivered.Wait(TimeSpan.FromSeconds(30)));
        Assert.Null(observedTransaction);
    }

    private sealed class ReentrancyProbeObserver(CountdownEvent allDelivered) : IPropertyChangeObserver
    {
        private int _inFlight;
        private int _reentrancyCount;
        private int _deliveryCount;

        public int ReentrancyCount => Volatile.Read(ref _reentrancyCount);
        public int DeliveryCount => _deliveryCount;

        public void OnChange(in SubjectPropertyChange change)
        {
            if (Interlocked.Increment(ref _inFlight) != 1)
            {
                Interlocked.Increment(ref _reentrancyCount);
            }

            try
            {
                _deliveryCount++;
                allDelivered.Signal();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
