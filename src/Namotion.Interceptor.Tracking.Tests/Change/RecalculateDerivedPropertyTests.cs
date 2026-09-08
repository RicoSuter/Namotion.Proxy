using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class RecalculateDerivedPropertyTests
{
    [Fact]
    public void WhenRecalculateCalled_ThenGetterIsReEvaluatedAndNotificationFired()
    {
        // Arrange
        var externalValue = 10.0;
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var sensor = new ExternalSensor(context);
        sensor.ExternalValueProvider = () => externalValue;
        var property = new PropertyReference(sensor, nameof(ExternalSensor.CalibratedTemperature));
        property.RecalculateDerivedProperty();
        changes.Clear();

        // Act
        externalValue = 42.0;
        property.RecalculateDerivedProperty();

        // Assert
        Assert.Single(changes);
        Assert.Equal(nameof(ExternalSensor.CalibratedTemperature), changes[0].Property.Name);
        Assert.Equal(10.0, changes[0].GetOldValue<double>());
        Assert.Equal(42.0, changes[0].GetNewValue<double>());
    }

    [Fact]
    public void WhenRecalculateCalledAndValueUnchanged_ThenNoNotificationFired()
    {
        // Arrange
        var externalValue = 10.0;
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var sensor = new ExternalSensor(context);
        sensor.ExternalValueProvider = () => externalValue;
        var property = new PropertyReference(sensor, nameof(ExternalSensor.CalibratedTemperature));
        property.RecalculateDerivedProperty();
        changes.Clear();

        // Act
        property.RecalculateDerivedProperty();

        // Assert
        Assert.Empty(changes);
    }

    [Fact]
    public void WhenRecalculateCalledOnNonDerivedProperty_ThenNoOp()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var sensor = new ExternalSensor(context);

        // Act & Assert
        var property = new PropertyReference(sensor, nameof(ExternalSensor.Label));
        property.RecalculateDerivedProperty();
    }

    [Fact]
    public void WhenRecalculateCalledUnderExplicitTimestampScope_ThenChangeTimestampMatchesScope()
    {
        // Arrange
        var externalValue = 10.0;
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var sensor = new ExternalSensor(context);
        sensor.ExternalValueProvider = () => externalValue;
        var property = new PropertyReference(sensor, nameof(ExternalSensor.CalibratedTemperature));
        property.RecalculateDerivedProperty();
        changes.Clear();

        var explicitTimestamp = DateTimeOffset.UtcNow.AddDays(-100);

        // Act
        externalValue = 42.0;
        using (SubjectChangeContext.WithChangedTimestamp(explicitTimestamp))
        {
            property.RecalculateDerivedProperty();
        }

        // Assert
        var change = Assert.Single(changes);
        Assert.Equal(explicitTimestamp, change.ChangedTimestamp);
        Assert.Equal(explicitTimestamp, property.TryGetWriteTimestamp());
    }

    [Fact]
    public void WhenRecalculateCalledWithNoScope_ThenAllEventsShareSingleTimestamp()
    {
        // Arrange
        var externalValue = 10.0;
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var sensor = new ExternalSensor(context);
        sensor.ExternalValueProvider = () => externalValue;
        var property = new PropertyReference(sensor, nameof(ExternalSensor.CalibratedTemperature));
        property.RecalculateDerivedProperty();
        changes.Clear();
        var capturesBefore = MonotonicTimestampClock.CurrentThreadCount;

        // Act
        externalValue = 42.0;
        property.RecalculateDerivedProperty();

        // Assert: exactly one timestamp captured; the observed event and the stored timestamp both use it.
        var captureCount = MonotonicTimestampClock.CurrentThreadCount - capturesBefore;
        Assert.Equal(1, captureCount);
        var change = Assert.Single(changes);
        var storedTimestamp = property.TryGetWriteTimestamp();
        Assert.NotNull(storedTimestamp);
        Assert.Equal(storedTimestamp, change.ChangedTimestamp);
    }

    [Fact]
    public void WhenRecalculateCalledUnderNullScope_ThenStoredTimestampIsNullAndAllEventsShareSingleTimestamp()
    {
        // Arrange
        var externalValue = 10.0;
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var sensor = new ExternalSensor(context);
        sensor.ExternalValueProvider = () => externalValue;
        var property = new PropertyReference(sensor, nameof(ExternalSensor.CalibratedTemperature));
        property.RecalculateDerivedProperty();
        changes.Clear();
        var capturesBefore = MonotonicTimestampClock.CurrentThreadCount;

        // Act
        externalValue = 42.0;
        using (SubjectChangeContext.WithChangedTimestamp(null))
        {
            property.RecalculateDerivedProperty();
        }

        // Assert: storage stays null (never-written sentinel); publishing captured exactly
        // one timestamp which the single observed event published verbatim.
        Assert.Null(property.TryGetWriteTimestamp());
        var captureCount = MonotonicTimestampClock.CurrentThreadCount - capturesBefore;
        Assert.Equal(1, captureCount);
        Assert.Single(changes);
    }


    [Fact]
    public void WhenRecalculateCalledConcurrently_ThenAllChangesAreSerializedAndNoNotificationsLost()
    {
        // Arrange
        var callCount = 0;
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                lock (changes) { changes.Add(change); }
            });

        var sensor = new ExternalSensor(context);
        sensor.ExternalValueProvider = () => Interlocked.Increment(ref callCount);
        var property = new PropertyReference(sensor, nameof(ExternalSensor.CalibratedTemperature));
        property.RecalculateDerivedProperty();

        lock (changes) { changes.Clear(); }
        Interlocked.Exchange(ref callCount, 0);

        // Act
        Parallel.For(0, 100, _ =>
        {
            property.RecalculateDerivedProperty();
        });

        // Assert
        // Thread-safety contract: concurrent calls must not deadlock, notifications must
        // arrive in order (no stale value after a newer one), and the final settled value
        // must be correct. The count is non-deterministic because IsRecalculating coalesces
        // concurrent calls, so fewer than 100 getter evaluations occur.
        lock (changes)
        {
            Assert.True(changes.Count > 0, "At least some recalculations should produce change notifications");

            for (var i = 1; i < changes.Count; i++)
            {
                var previous = changes[i - 1].GetNewValue<double>();
                var current = changes[i].GetNewValue<double>();
                Assert.True(current > previous,
                    $"Notifications must be monotonically increasing but got {previous} -> {current} at index {i}");
            }

            var finalNotifiedValue = changes[^1].GetNewValue<double>();
            Assert.Equal((double)callCount, finalNotifiedValue);
        }
    }

    [Fact]
    public async Task WhenManualRecalculationRunsInsideATransaction_ThenTrackingUsesTheModelView()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "committed",
            ExternalSuffix = "before"
        };
        var property = new PropertyReference(subject, nameof(TransactionCascadeSubject.ManualCombined));
        property.RecalculateDerivedProperty();

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name == nameof(TransactionCascadeSubject.ManualCombined))
            .Subscribe(changes.Add);

        // Act
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            subject.Plain = "pending";
            subject.ExternalSuffix = "after";
            property.RecalculateDerivedProperty();

            // Assert
            var change = Assert.Single(changes);
            Assert.Equal("committed|before", change.GetOldValue<string>());
            Assert.Equal("committed|after", change.GetNewValue<string>());
            Assert.Equal("pending|after", subject.ManualCombined);
        }
    }

    [Fact]
    public async Task WhenAGetterThrowsInsideATransaction_ThenLaterDirectReadsStillReturnPendingValues()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var subject = new TransactionCascadeSubject(context) { Plain = "committed" };
        subject.ProbeEvaluator = _ => throw new InvalidOperationException("Probe failure.");
        var property = new PropertyReference(subject, nameof(TransactionCascadeSubject.Probe));

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            subject.Plain = "pending";
            property.RecalculateDerivedProperty();

            // Assert
            Assert.Same(transaction, SubjectTransaction.Current);
            Assert.Equal("pending", subject.Plain);

            subject.ProbeEvaluator = value => $"{value.Plain}|recovered";
            property.RecalculateDerivedProperty();
            Assert.Equal("pending", subject.Plain);
        }
    }

    [Fact]
    public async Task WhenANotificationSubscriberReadsInsideATransaction_ThenItSeesPendingValues()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "committed",
            DerivedWithSetter = "d0"
        };
        _ = subject.Combined;

        string? valueReadBySubscriber = null;
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name == nameof(TransactionCascadeSubject.Combined))
            .Subscribe(_ => valueReadBySubscriber = subject.Combined);

        // Act
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            subject.Plain = "pending";
            subject.DerivedWithSetter = "d1";

            // Assert
            Assert.Equal("pending|d1", valueReadBySubscriber);
        }
    }

    [Fact]
    public void WhenAGetterStartsATransaction_ThenItsStoredValueStillUsesTheModelView()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "initial",
            SideEffect = "model"
        };
        var property = new PropertyReference(subject, nameof(TransactionCascadeSubject.Probe));
        SubjectTransaction? startedTransaction = null;
        subject.ProbeEvaluator = value =>
        {
            startedTransaction ??= context
                .BeginTransactionAsync(TransactionFailureHandling.BestEffort)
                .GetAwaiter()
                .GetResult();
            value.SideEffect = "pending-from-getter";
            return value.SideEffect;
        };

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name == nameof(TransactionCascadeSubject.Probe))
            .Subscribe(changes.Add);

        // Act
        try
        {
            property.RecalculateDerivedProperty();

            // Assert
            Assert.NotNull(startedTransaction);
            Assert.Same(startedTransaction, SubjectTransaction.Current);
            Assert.Equal("model", Assert.Single(changes).GetNewValue<string?>());
            Assert.Equal("pending-from-getter", subject.SideEffect);
        }
        finally
        {
            startedTransaction?.Dispose();
        }
    }

    [Fact]
    public async Task WhenAGetterReplacesTheAmbientTransaction_ThenItsStoredValueUsesTheModelViewAndTheReplacementRemainsAmbient()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "initial",
            SideEffect = "model"
        };
        var property = new PropertyReference(subject, nameof(TransactionCascadeSubject.Probe));
        var originalTransaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        SubjectTransaction? replacementTransaction = null;
        subject.ProbeEvaluator = value =>
        {
            originalTransaction.Dispose();
            replacementTransaction ??= context
                .BeginTransactionAsync(TransactionFailureHandling.BestEffort)
                .GetAwaiter()
                .GetResult();
            value.SideEffect = "pending-in-replacement";
            return value.SideEffect;
        };

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name == nameof(TransactionCascadeSubject.Probe))
            .Subscribe(changes.Add);

        // Act
        try
        {
            property.RecalculateDerivedProperty();

            // Assert
            Assert.NotNull(replacementTransaction);
            Assert.Same(replacementTransaction, SubjectTransaction.Current);
            Assert.Equal("model", Assert.Single(changes).GetNewValue<string?>());
            Assert.Equal("pending-in-replacement", subject.SideEffect);
        }
        finally
        {
            replacementTransaction?.Dispose();
            originalTransaction.Dispose();
        }
    }

    [Fact]
    public async Task WhenDerivedEvaluationsAreNested_ThenEveryGetterUsesTheModelView()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "plain-model",
            SideEffect = "side-model",
            ExternalSuffix = "before"
        };
        var innerProperty = new PropertyReference(subject, nameof(TransactionCascadeSubject.ManualCombined));
        var outerProperty = new PropertyReference(subject, nameof(TransactionCascadeSubject.Probe));
        innerProperty.RecalculateDerivedProperty();
        subject.ProbeEvaluator = value =>
        {
            innerProperty.RecalculateDerivedProperty();
            return $"{value.Plain}|{value.SideEffect}";
        };

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name is
                nameof(TransactionCascadeSubject.ManualCombined) or
                nameof(TransactionCascadeSubject.Probe))
            .Subscribe(changes.Add);

        // Act
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            subject.Plain = "plain-pending";
            subject.SideEffect = "side-pending";
            subject.ExternalSuffix = "after";
            outerProperty.RecalculateDerivedProperty();

            // Assert
            Assert.Collection(
                changes,
                change =>
                {
                    Assert.Equal(nameof(TransactionCascadeSubject.ManualCombined), change.Property.Name);
                    Assert.Equal("plain-model|before", change.GetOldValue<string>());
                    Assert.Equal("plain-model|after", change.GetNewValue<string>());
                },
                change =>
                {
                    Assert.Equal(nameof(TransactionCascadeSubject.Probe), change.Property.Name);
                    Assert.Equal("plain-model", change.GetOldValue<string>());
                    Assert.Equal("plain-model|side-model", change.GetNewValue<string>());
                });
        }
    }

    [Fact]
    public async Task WhenDerivedEvaluationCompletes_ThenItsBypassDoesNotFlowToAQueuedWorkItem()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var subject = new TransactionCascadeSubject(context) { Plain = "model" };
        var property = new PropertyReference(subject, nameof(TransactionCascadeSubject.Probe));
        using var evaluationCompleted = new ManualResetEventSlim();
        using var workStarted = new ManualResetEventSlim();
        using var workCompleted = new ManualResetEventSlim();
        string? queuedRead = null;
        subject.ProbeEvaluator = value =>
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                workStarted.Set();
                evaluationCompleted.Wait();
                queuedRead = value.Plain;
                workCompleted.Set();
            });
            return value.Plain;
        };

        // Act
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            subject.Plain = "pending";
            try
            {
                property.RecalculateDerivedProperty();
                Assert.True(workStarted.Wait(TimeSpan.FromSeconds(10)), "queued work did not start");
            }
            finally
            {
                evaluationCompleted.Set();
            }

            Assert.True(workCompleted.Wait(TimeSpan.FromSeconds(10)), "queued work did not finish");

            // Assert
            Assert.Equal("pending", queuedRead);
        }
    }

    [Fact]
    public async Task WhenDerivedEvaluationsRunOnParallelThreads_ThenTheirBypassesAreIsolated()
    {
        // Arrange
        var firstContext = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var secondContext = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var ordinaryReadContext = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var first = new TransactionCascadeSubject(firstContext) { Plain = "model-1" };
        var second = new TransactionCascadeSubject(secondContext) { Plain = "model-2" };
        var ordinaryReadSubject = new TransactionCascadeSubject(ordinaryReadContext) { Plain = "ordinary-model" };
        using var evaluationsEntered = new CountdownEvent(2);
        using var releaseEvaluations = new ManualResetEventSlim();

        first.ProbeEvaluator = value =>
        {
            evaluationsEntered.Signal();
            if (!releaseEvaluations.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("first derived evaluation was not released");
            }

            return $"{value.Plain}|evaluated";
        };
        second.ProbeEvaluator = value =>
        {
            evaluationsEntered.Signal();
            if (!releaseEvaluations.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("second derived evaluation was not released");
            }

            return $"{value.Plain}|evaluated";
        };

        string? firstTrackedValue = null;
        string? secondTrackedValue = null;
        string? ordinaryPendingRead = null;
        using var firstSubscription = firstContext
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name == nameof(TransactionCascadeSubject.Probe))
            .Subscribe(change => firstTrackedValue = change.GetNewValue<string?>());
        using var secondSubscription = secondContext
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name == nameof(TransactionCascadeSubject.Probe))
            .Subscribe(change => secondTrackedValue = change.GetNewValue<string?>());

        // Act
        // Both evaluations must be in flight together for the overlap this test needs.
        var firstTask = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(async () =>
        {
            using (await firstContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
            {
                first.Plain = "pending-1";
                new PropertyReference(first, nameof(TransactionCascadeSubject.Probe)).RecalculateDerivedProperty();
            }
        });
        var secondTask = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(async () =>
        {
            using (await secondContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
            {
                second.Plain = "pending-2";
                new PropertyReference(second, nameof(TransactionCascadeSubject.Probe)).RecalculateDerivedProperty();
            }
        });

        try
        {
            Assert.True(evaluationsEntered.Wait(TimeSpan.FromSeconds(10)), "parallel evaluations did not overlap");
            using (await ordinaryReadContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
            {
                ordinaryReadSubject.Plain = "ordinary-pending";
                ordinaryPendingRead = ordinaryReadSubject.Plain;
            }
        }
        finally
        {
            releaseEvaluations.Set();
        }

        await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal("model-1|evaluated", firstTrackedValue);
        Assert.Equal("model-2|evaluated", secondTrackedValue);
        Assert.Equal("ordinary-pending", ordinaryPendingRead);
    }
}
