using System.Reactive.Concurrency;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class WriteTimestampTests
{
    [Fact]
    public void WriteTimestamp_BeforeAnyWrite_ShouldBeNull()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);

        // Act
        var timestamp = person.GetPropertyReference("FirstName").TryGetWriteTimestamp();

        // Assert
        Assert.Null(timestamp);
    }

    [Fact]
    public void WriteTimestamp_AfterWrite_ShouldBeSet()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var before = DateTimeOffset.UtcNow;

        // Act
        person.FirstName = "John";

        // Assert
        var timestamp = person.GetPropertyReference("FirstName").TryGetWriteTimestamp();
        Assert.NotNull(timestamp);
        Assert.True(timestamp.Value >= before);
    }

    [Fact]
    public void WriteTimestamp_SecondWriteOverwritesFirst()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var firstTimestamp = DateTimeOffset.UtcNow.AddDays(-100);
        var secondTimestamp = DateTimeOffset.UtcNow.AddDays(-50);

        // Act
        using (SubjectChangeContext.WithChangedTimestamp(firstTimestamp))
        {
            person.FirstName = "John";
        }

        using (SubjectChangeContext.WithChangedTimestamp(secondTimestamp))
        {
            person.FirstName = "Jane";
        }

        // Assert
        var timestamp = person.GetPropertyReference("FirstName").TryGetWriteTimestamp();
        Assert.Equal(secondTimestamp, timestamp);
    }

    [Fact]
    public void WriteTimestamp_DifferentProperties_HaveIndependentTimestamps()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var firstTimestamp = DateTimeOffset.UtcNow.AddDays(-100);
        var secondTimestamp = DateTimeOffset.UtcNow.AddDays(-50);

        // Act
        using (SubjectChangeContext.WithChangedTimestamp(firstTimestamp))
        {
            person.FirstName = "John";
        }

        using (SubjectChangeContext.WithChangedTimestamp(secondTimestamp))
        {
            person.LastName = "Doe";
        }

        // Assert
        Assert.Equal(firstTimestamp, person.GetPropertyReference("FirstName").TryGetWriteTimestamp());
        Assert.Equal(secondTimestamp, person.GetPropertyReference("LastName").TryGetWriteTimestamp());
    }

    [Fact]
    public void WriteTimestamp_WithExplicitTimestamp_ChangeEventsShouldMatch()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var changes = new List<SubjectPropertyChange>();
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        var timestamp = DateTimeOffset.UtcNow.AddDays(-200);

        // Act
        using (SubjectChangeContext.WithChangedTimestamp(timestamp))
        {
            person.FirstName = "Mother";
        }

        // Assert
        Assert.Equal(3, changes.Count); // backed, derived, derived
        Assert.True(changes.All(c => c.ChangedTimestamp == timestamp));
    }

    [Fact]
    public void WriteTimestamp_WithNullTimestamp_CascadeChangeEventsShareSingleTimestamp()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var changes = new List<SubjectPropertyChange>();
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        // Act
        using (SubjectChangeContext.WithChangedTimestamp(null))
        {
            person.FirstName = "Mother";
        }

        // Assert: storage stays null (never-written sentinel) but all published change events
        // (trigger + cascade dependents) share a single synthesized timestamp.
        Assert.Null(person.GetPropertyReference("FirstName").TryGetWriteTimestamp());
        Assert.Equal(3, changes.Count); // backed, derived, derived
        var first = changes[0].ChangedTimestamp;
        Assert.True(changes.All(c => c.ChangedTimestamp == first));
    }

    [Fact]
    public void WriteTimestamp_ConcurrentWrites_ValueAndTimestampAreConsistent()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);

        // Each thread writes a unique value with a matching unique timestamp. Every published
        // change event captures value and timestamp inside the write, and the stored pair is
        // checked once the writers settle; both observations go through supported surfaces, so
        // no external lock on the write path is needed.
        var observedChanges = new System.Collections.Concurrent.ConcurrentQueue<SubjectPropertyChange>();
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (change.Property.Name == nameof(Person.FirstName))
                {
                    observedChanges.Enqueue(change);
                }
            });

        const int threadCount = 8;
        const int iterationsPerThread = 5_000;
        var barrier = new Barrier(threadCount);

        // Act
        var threads = Enumerable.Range(0, threadCount).Select(threadIndex => new Thread(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < iterationsPerThread; i++)
            {
                var index = threadIndex * iterationsPerThread + i;
                var timestamp = new DateTimeOffset(index + 1, TimeSpan.Zero); // ticks = index+1 (avoid 0)

                using (SubjectChangeContext.WithChangedTimestamp(timestamp))
                {
                    person.FirstName = $"Name{index}";
                }
            }
        })).ToArray();

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        // Assert: every observed change pairs the value with the timestamp of the same write.
        Assert.NotEmpty(observedChanges);
        foreach (var change in observedChanges)
        {
            var expectedIndex = (int)(change.ChangedTimestamp.UtcTicks - 1);
            Assert.Equal($"Name{expectedIndex}", change.GetNewValue<string>());
        }

        // Assert: once the writers settle, the stored value and timestamp belong to one write.
        var storedTimestamp = person.GetPropertyReference("FirstName").TryGetWriteTimestamp();
        Assert.NotNull(storedTimestamp);
        var storedIndex = (int)(storedTimestamp!.Value.UtcTicks - 1);
        Assert.Equal($"Name{storedIndex}", person.FirstName);
    }

    [Fact]
    public void WriteTimestamp_AfterScopeEnds_ShouldUseCurrentTime()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var explicitTimestamp = DateTimeOffset.UtcNow.AddDays(-200);

        using (SubjectChangeContext.WithChangedTimestamp(explicitTimestamp))
        {
            person.FirstName = "First";
        }

        // Act
        var before = DateTimeOffset.UtcNow;
        person.LastName = "Second";

        // Assert
        var timestamp = person.GetPropertyReference("LastName").TryGetWriteTimestamp();
        Assert.NotNull(timestamp);
        Assert.True(timestamp.Value >= before);
        Assert.NotEqual(explicitTimestamp, timestamp);
    }

    [Fact]
    public void WhenWrittenWithoutScope_ThenQueueChangeTimestampMatchesStoredTimestamp()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        using var subscription = context.CreatePropertyChangeQueueSubscription();
        var person = new Person(context);

        // Act
        person.FirstName = "John";

        // Assert
        var storedTimestamp = person.GetPropertyReference("FirstName").TryGetWriteTimestamp();
        Assert.True(subscription.TryDequeue(out var change, CancellationToken.None));
        Assert.NotNull(storedTimestamp);
        Assert.Equal(storedTimestamp.Value, change.ChangedTimestamp);
    }

    [Fact]
    public void WhenWrittenWithoutScope_ThenObservableChangeTimestampMatchesStoredTimestamp()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var changes = new List<SubjectPropertyChange>();
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        var person = new Person(context);

        // Act
        person.FirstName = "John";

        // Assert
        var storedTimestamp = person.GetPropertyReference("FirstName").TryGetWriteTimestamp();
        var firstNameChange = changes.First(c => c.Property.Name == "FirstName");
        Assert.NotNull(storedTimestamp);
        Assert.Equal(storedTimestamp.Value, firstNameChange.ChangedTimestamp);
    }

    [Fact]
    public void WhenWrittenWithNullTimestamp_ThenQueueChangeTimestampFallsBackToCurrentTime()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        using var subscription = context.CreatePropertyChangeQueueSubscription();
        var person = new Person(context);
        var before = DateTimeOffset.UtcNow;

        // Act
        using (SubjectChangeContext.WithChangedTimestamp(null))
        {
            person.FirstName = "John";
        }

        // Assert
        Assert.Null(person.GetPropertyReference("FirstName").TryGetWriteTimestamp());
        Assert.True(subscription.TryDequeue(out var change, CancellationToken.None));
        Assert.True(change.ChangedTimestamp >= before);
    }

    [Fact]
    public void WhenWrittenWithNullTimestamp_ThenObservableChangeTimestampFallsBackToCurrentTime()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var changes = new List<SubjectPropertyChange>();
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        var person = new Person(context);
        var before = DateTimeOffset.UtcNow;

        // Act
        using (SubjectChangeContext.WithChangedTimestamp(null))
        {
            person.FirstName = "John";
        }

        // Assert
        Assert.Null(person.GetPropertyReference("FirstName").TryGetWriteTimestamp());
        var firstNameChange = changes.First(c => c.Property.Name == "FirstName");
        Assert.True(firstNameChange.ChangedTimestamp >= before);
    }

    [Fact]
    public void WhenWrittenWithExplicitTimestamp_ThenStoredQueueAndObservableTimestampsAllMatch()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        using var subscription = context.CreatePropertyChangeQueueSubscription();
        var observableChanges = new List<SubjectPropertyChange>();
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => observableChanges.Add(c));

        var person = new Person(context);
        var explicitTimestamp = DateTimeOffset.UtcNow.AddDays(-7);

        // Act
        using (SubjectChangeContext.WithChangedTimestamp(explicitTimestamp))
        {
            person.FirstName = "John";
        }

        // Assert
        var storedTimestamp = person.GetPropertyReference("FirstName").TryGetWriteTimestamp();
        Assert.True(subscription.TryDequeue(out var queuedChange, CancellationToken.None));
        var observedChange = observableChanges.First(c => c.Property.Name == "FirstName");

        Assert.Equal(explicitTimestamp, storedTimestamp);
        Assert.Equal(explicitTimestamp, queuedChange.ChangedTimestamp);
        Assert.Equal(explicitTimestamp, observedChange.ChangedTimestamp);
    }

    [Fact]
    public void WhenReadingWriteTimestamp_MultipleReads_ThenAllReturnSameValue()
    {
        // Arrange
        var capturingInterceptor = new ContextTimestampCapturingInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService<IWriteInterceptor>(() => capturingInterceptor, _ => false);

        var person = new Person(context);

        // Act: reads occur both before and after next(). Both must observe the same lazily-captured value.
        person.FirstName = "John";

        // Assert
        Assert.NotNull(capturingInterceptor.BeforeNext);
        Assert.NotNull(capturingInterceptor.AfterNext);
        Assert.Equal(capturingInterceptor.BeforeNext, capturingInterceptor.AfterNext);

        var storedTimestamp = person.GetPropertyReference("FirstName").TryGetWriteTimestamp();
        Assert.Equal(storedTimestamp, capturingInterceptor.BeforeNext);
    }

    [Fact]
    public void WhenDerivedGetterHasSideEffectWriteDuringCascade_ThenSideEffectTimestampIsIndependentOfCascadeTrigger()
    {
        // Pins the deliberate semantic that the cascade does not push a SubjectChangeContext
        // scope: a write performed inside a derived getter's recalc body resolves its timestamp
        // against the outer (caller's) scope, capturing its own UtcNow when no outer scope is
        // active. The cascade trigger and the side-effect write are independent timestamped
        // events. A future refactor that re-introduced a cascade scope push would make the
        // side-effect write inherit the trigger's captured value, which this test catches.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new SideEffectWritePerson(context);
        person.Name = "Initial"; // priming write: ensures cascade dependency Name -> Greeting is established
        var capturesBefore = MonotonicTimestampClock.CurrentThreadCount;

        // Act: a single write under no scope. The trigger captures once at its terminal write.
        // The cascade recalculates Greeting, whose getter writes SideEffectTarget; that
        // side-effect write resolves under no scope and captures a distinct UtcNow.
        person.Name = "Updated";

        // Assert: at least two captures (trigger + side effect), and the timestamps are distinct.
        var captureCount = MonotonicTimestampClock.CurrentThreadCount - capturesBefore;
        Assert.True(captureCount >= 2, $"Expected at least 2 captures (trigger + side effect); got {captureCount}");
        var triggerTs = person.GetPropertyReference(nameof(SideEffectWritePerson.Name)).TryGetWriteTimestamp();
        var sideEffectTs = person.GetPropertyReference(nameof(SideEffectWritePerson.SideEffectTarget)).TryGetWriteTimestamp();
        Assert.NotNull(triggerTs);
        Assert.NotNull(sideEffectTs);
        Assert.NotEqual(triggerTs, sideEffectTs);
        Assert.True(sideEffectTs > triggerTs, "side-effect capture must occur after the trigger capture");
    }

    private sealed class ContextTimestampCapturingInterceptor : IWriteInterceptor
    {
        public DateTimeOffset? BeforeNext { get; private set; }
        public DateTimeOffset? AfterNext { get; private set; }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            BeforeNext ??= context.WriteTimestamp;
            next(ref context);
            AfterNext ??= context.WriteTimestamp;
        }
    }
}
