using Namotion.Interceptor.Testing;
using System.Linq.Expressions;
using System.Reactive.Concurrency;

using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class ScheduledPropertySubscriptionTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    public ScheduledPropertySubscriptionTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenSubscribedByTypedSelector_ThenChangesAreDeliveredOnTheScheduler()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        using var subscription = person.SubscribeToProperty(
            x => x.FirstName,
            (in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()),
            scheduler);

        // Act
        person.FirstName = "Rico";

        // Assert
        Assert.Empty(received);
        scheduler.RunUntilIdle();
        Assert.Equal(["Rico"], received);
    }

    [Fact]
    public void WhenSelectorIsNotADirectPropertyAccess_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => person.SubscribeToProperty(
            x => x.Father!.FirstName,
            (in SubjectPropertyChange _) => { },
            scheduler));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenAnyScheduledSubscribeArgumentIsNull_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var scheduler = new ControllableScheduler();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => property.Subscribe((IPropertyChangeObserver)null!, scheduler));
        Assert.Throws<ArgumentNullException>(() => property.Subscribe((PropertyChangeCallback)null!, scheduler));
        Assert.Throws<ArgumentNullException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, null!));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToProperty(x => x.FirstName, (IPropertyChangeObserver)null!, scheduler));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToProperty(x => x.FirstName, (PropertyChangeCallback)null!, scheduler));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToProperty(x => x.FirstName, (in SubjectPropertyChange _) => { }, null!));
        Assert.Throws<ArgumentNullException>(() => ((Person)null!).SubscribeToProperty(x => x.FirstName, (in SubjectPropertyChange _) => { }, scheduler));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToProperty((Expression<Func<Person, string?>>)null!, (in SubjectPropertyChange _) => { }, scheduler));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenSchedulerIsSynchronous_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act & Assert
        Assert.Throws<ArgumentException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, ImmediateScheduler.Instance));
        Assert.Throws<ArgumentException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, Scheduler.Immediate));
        Assert.Throws<ArgumentException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, CurrentThreadScheduler.Instance));
        Assert.Throws<ArgumentException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, Scheduler.CurrentThread));
        Assert.Throws<ArgumentException>(() => person.SubscribeToProperty(x => x.FirstName, (in SubjectPropertyChange _) => { }, ImmediateScheduler.Instance));
        Assert.Throws<ArgumentException>(() => person.SubscribeToProperty(x => x.FirstName, (in SubjectPropertyChange _) => { }, CurrentThreadScheduler.Instance));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenPropertyIsNotIntercepted_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var holder = new PlainPropertyHolder(context);
        var scheduler = new ControllableScheduler();

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new PropertyReference(holder, nameof(PlainPropertyHolder.PlainProperty))
                .Subscribe((in SubjectPropertyChange _) => { }, scheduler));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenObserverThrows_ThenTheSetterReturnsNormally()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var deliveries = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    deliveries++;
                    throw new InvalidOperationException("boom");
                },
                scheduler);

        // Act
        person.FirstName = "one";
        scheduler.RunUntilIdle();

        // Assert
        Assert.Equal(1, deliveries);
        Assert.Equal("one", person.FirstName);
    }

    [Fact]
    public void WhenScheduledObserverThrows_ThenAnInlineListenerOnTheSamePropertyStillFires()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var scheduler = new ControllableScheduler();
        var inline = new List<string?>();
        var scheduledDeliveries = 0;

        using var scheduled = property.Subscribe(
            (in SubjectPropertyChange _) =>
            {
                scheduledDeliveries++;
                throw new InvalidOperationException("boom");
            },
            scheduler);
        using var plain = property.SubscribeInline(
            (in SubjectPropertyChange change) => inline.Add(change.GetNewValue<string?>()));

        // Act
        person.FirstName = "one";
        scheduler.RunUntilIdle();

        // Assert
        Assert.Equal(1, scheduledDeliveries);
        Assert.Equal(["one"], inline);
    }

    [Fact]
    public async Task WhenWriteCommitsAfterSubscribeReturns_ThenItIsDelivered()
    {
        // Arrange
        var blocker = new BlockingWriteInterceptor();
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        context.WithService(() => blocker);
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        // The write parks in the interceptor chain, so it must not wait for a pool thread.
        var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(
            () => { person.FirstName = "John"; });
        Assert.True(blocker.EnteredInnerChain.Wait(TimeSpan.FromSeconds(10)));

        // Act
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()), scheduler);
        blocker.ProceedWithCommit.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        scheduler.RunUntilIdle();

        // Assert
        Assert.Equal(["John"], received);
    }

    [Fact]
    public void WhenSubjectIsDetachedWithChangesQueued_ThenThoseChangesAreStillDelivered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var parent = new Person(context);
        var child = new Person();
        parent.Father = child;

        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        using var subscription = new PropertyReference(child, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()), scheduler);

        child.FirstName = "one";

        // Act
        parent.Father = null;
        child.FirstName = "after-detach";
        scheduler.RunUntilIdle();

        // Assert
        Assert.Equal(["one"], received);
    }

    [Fact]
    public void WhenSubjectIsDetachedAndReattached_ThenTheSubscriptionRevives()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var parent = new Person(context);
        var child = new Person();
        parent.Father = child;

        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        using var subscription = new PropertyReference(child, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()), scheduler);

        parent.Father = null;
        child.FirstName = "dormant";
        scheduler.RunUntilIdle();
        Assert.Empty(received);

        // Act
        parent.Father = child;
        child.FirstName = "live";
        scheduler.RunUntilIdle();

        // Assert
        Assert.Equal(["live"], received);
    }

    [Fact]
    public void WhenOneObserverIsSharedAcrossTwoSubscriptions_ThenItCanBeInvokedConcurrently()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var observer = new OverlapProbeObserver();

        using var first = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(observer, Scheduler.Default);
        using var second = new PropertyReference(person, nameof(Person.LastName))
            .Subscribe(observer, Scheduler.Default);

        // Act
        person.FirstName = "one";
        person.LastName = "two";

        // Assert
        Assert.True(observer.WaitForAllDeliveries(WaitTimeout), "both deliveries should have completed");
        Assert.True(observer.OverlapObserved, "the two deliveries never overlapped");
    }

    [Fact]
    public void WhenSchedulerSupportsLongRunningWork_ThenTheSubscriptionUsesOnlyOrdinarySchedule()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new LongRunningTrapScheduler();
        var delivered = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => delivered++, scheduler);

        // Act
        person.FirstName = "Rico";
        scheduler.RunUntilIdle();

        // Assert
        Assert.Equal(1, delivered);
        Assert.Equal(1, scheduler.ScheduleCallCount);
        Assert.Equal(0, scheduler.LongRunningCallCount);
    }

    private sealed class OverlapProbeObserver : IPropertyChangeObserver, IDisposable
    {
        private const int ExpectedDeliveries = 2;

        private readonly ManualResetEventSlim _bothArrived = new();
        private readonly CountdownEvent _allCompleted = new(ExpectedDeliveries);

        private int _inFlight;
        private int _overlapObserved;

        public bool OverlapObserved => Volatile.Read(ref _overlapObserved) == 1;

        public void OnChange(in SubjectPropertyChange change)
        {
            if (Interlocked.Increment(ref _inFlight) == ExpectedDeliveries)
            {
                Volatile.Write(ref _overlapObserved, 1);
                _bothArrived.Set();
            }
            else if (!_bothArrived.Wait(WaitTimeout))
            {
                _bothArrived.Set();
            }

            Interlocked.Decrement(ref _inFlight);
            _allCompleted.Signal();
        }

        public bool WaitForAllDeliveries(TimeSpan timeout) => _allCompleted.Wait(timeout);

        public void Dispose()
        {
            _bothArrived.Dispose();
            _allCompleted.Dispose();
        }
    }
}
