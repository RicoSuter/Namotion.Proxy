using System.ComponentModel.DataAnnotations;
using System.Reactive.Concurrency;
using Moq;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Namotion.Interceptor.Validation;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Tests for property interception during transactions: write/read behavior, notifications, derived properties.
/// </summary>
public class SubjectTransactionPropertyTests : TransactionTestBase
{
    [Fact]
    public async Task WhenPropertyWrittenDuringTransaction_ThenChangeIsCaptured()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "John";

        // Assert
        Assert.Single(transaction.GetPendingChanges());
        var change = transaction.GetPendingChanges().First();
        Assert.Equal("John", change.GetNewValue<string>());
        Assert.Null(change.GetOldValue<string>());
    }

    [Fact]
    public void WhenNoTransactionActive_ThenWritePassesThrough()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        // Act
        person.FirstName = "John";

        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Null(SubjectTransaction.Current);
    }

    [Fact]
    public async Task WhenCommitting_ThenApplyWritesPassThroughToModel()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        string finalValue;

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";

            await transaction.CommitAsync(CancellationToken.None);

            finalValue = person.FirstName!;
        }

        // Assert
        Assert.Equal("John", finalValue);
    }

    [Fact]
    public async Task WhenDerivedPropertyChangesDuringTransaction_ThenOnlyBasePropertiesAreCaptured()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "John";
        person.LastName = "Doe";

        _ = person.FullName;

        // Assert
        Assert.Equal(2, transaction.GetPendingChanges().Count);
        Assert.All(transaction.GetPendingChanges(), change => Assert.False(change.Property.Metadata.IsDerived));
    }

    [Fact]
    public async Task WhenChangeCapturedWithChangeContext_ThenSourceAndTimestampsArePreserved()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var mockSource = Mock.Of<ISubjectSource>();
        var changedTime = DateTime.UtcNow.AddMinutes(-5);
        var receivedTime = DateTime.UtcNow;

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        new PropertyReference(person, nameof(Person.FirstName))
            .SetValueFromSource(mockSource, changedTime, receivedTime, "John");

        // Assert
        var change = transaction.GetPendingChanges().First();
        Assert.Same(mockSource, change.Origin.Source);
        Assert.Equal(changedTime, change.ChangedTimestamp);
        Assert.Equal(receivedTime, change.ReceivedTimestamp);
    }

    [Fact]
    public async Task WhenCommitted_ThenNotificationsPreserveSourceAndTimestamps()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var mockSource = Mock.Of<ISubjectSource>();
        var changedTime = DateTime.UtcNow.AddMinutes(-5);
        var receivedTime = DateTime.UtcNow;

        var notifications = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(Scheduler.Immediate)
            .Subscribe(change => notifications.Add(change));

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            new PropertyReference(person, nameof(Person.FirstName))
                .SetValueFromSource(mockSource, changedTime, receivedTime, "John");

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        var change = notifications.Single(n => n.Property.Metadata.Name == nameof(Person.FirstName));
        Assert.Same(mockSource, change.Origin.Source);
        Assert.Equal(changedTime, change.ChangedTimestamp);
        Assert.Equal(receivedTime, change.ReceivedTimestamp);
        Assert.Equal(changedTime, person.GetPropertyReference(nameof(Person.FirstName)).TryGetWriteTimestamp());
    }

    [Fact]
    public async Task WhenReadingDuringTransaction_ThenPendingValueIsReturned()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        person.FirstName = "Original";

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "Pending";
            var readValue = person.FirstName;

            // Assert
            Assert.Equal("Pending", readValue);
            Assert.Single(transaction.GetPendingChanges());
        }

        Assert.Equal("Original", person.FirstName);
    }

    [Fact]
    public void WhenNoTransactionActive_ThenReadReturnsActualValue()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        person.FirstName = "Stored";

        // Act
        var result = person.FirstName;

        // Assert
        Assert.Equal("Stored", result);
    }

    [Fact]
    public async Task WhenNoPendingChangeForProperty_ThenReadReturnsActualValue()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        person.FirstName = "Stored";

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var result = person.FirstName;

        // Assert
        Assert.Equal("Stored", result);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenReadingDuringCommitNotification_ThenAppliedValueIsReturned()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        person.FirstName = "Original";
        string? readDuringCommit = null;

        var changeObservable = context.GetPropertyChangeObservable(Scheduler.Immediate);
        changeObservable.Subscribe(change =>
        {
            if (change.Property.Metadata.Name == nameof(Person.FirstName))
            {
                readDuringCommit = person.FirstName;
            }
        });

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "Pending";

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal("Pending", readDuringCommit);
        Assert.Equal("Pending", person.FirstName);
    }

    [Fact]
    public async Task WhenCommitted_ThenChangeNotificationsFire()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var notifications = new List<SubjectPropertyChange>();

        var changeObservable = context.GetPropertyChangeObservable(Scheduler.Immediate);
        changeObservable.Subscribe(change => notifications.Add(change));

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";

            Assert.Empty(notifications);

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.True(notifications.Count >= 1, "Expected at least 1 notification");
        Assert.Contains(notifications, n => n.Property.Metadata.Name == nameof(Person.FirstName));
        var firstNameNotification = notifications.First(n => n.Property.Metadata.Name == nameof(Person.FirstName));
        Assert.Equal("John", firstNameNotification.GetNewValue<string>());
    }

    [Fact]
    public async Task WhenDisposedWithoutCommit_ThenNoChangeNotificationsFire()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var notifications = new List<SubjectPropertyChange>();

        var changeObservable = context.GetPropertyChangeObservable(Scheduler.Immediate);
        changeObservable.Subscribe(change => notifications.Add(change));

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
        }

        // Assert
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task WhenMultipleSubjectsModified_ThenAllChangesAreApplied()
    {
        // Arrange
        var context = CreateContext();
        var person1 = new Person(context);
        var person2 = new Person(context);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person1.FirstName = "John";
            person2.FirstName = "Jane";

            Assert.Equal(2, transaction.GetPendingChanges().Count);

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal("John", person1.FirstName);
        Assert.Equal("Jane", person2.FirstName);
    }

    [Fact]
    public async Task WhenCommitted_ThenDerivedPropertyReflectsNewValues()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            var duringTransaction = person.FullName;

            await transaction.CommitAsync(CancellationToken.None);

            var afterCommit = person.FullName;

            // Assert
            Assert.Equal("John Doe", duringTransaction);
            Assert.Equal("John Doe", afterCommit);
        }
    }

    [Fact]
    public async Task WhenTransactionActive_ThenPropertyChangedNotFired()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";

            // Assert - PropertyChanged should NOT fire during transaction
            Assert.Empty(firedEvents);
        }
    }

    [Fact]
    public async Task WhenTransactionCommits_ThenPropertyChangedFired()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";

            Assert.Empty(firedEvents); // Not fired yet

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert - PropertyChanged should fire after commit
        Assert.Contains("FirstName", firedEvents);
    }

    [Fact]
    public async Task WhenTransactionCommits_ThenDerivedPropertyChangedFired()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            Assert.Empty(firedEvents); // Not fired yet

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert - PropertyChanged should fire for normal AND derived properties
        Assert.Contains("FirstName", firedEvents);
        Assert.Contains("LastName", firedEvents);
        Assert.Contains("FullName", firedEvents);
    }

    [Fact]
    public async Task WhenDependentPropertiesCommitted_ThenInsertionOrderIsPreserved()
    {
        // Arrange: Motor with MaxAllowedSpeed=100 and a validator that rejects MotorSpeed > MaxAllowedSpeed.
        // Setting MotorSpeed=150 requires MaxAllowedSpeed to be raised first.
        var context = CreateContextWithMotorValidation();
        var motor = new Motor(context);

        // Act: Raise limit first, then set speed above old limit
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        motor.MaxAllowedSpeed = 200;
        motor.MotorSpeed = 150;

        await transaction.CommitAsync(CancellationToken.None);

        // Assert: Both values applied because commit replayed in insertion order
        Assert.Equal(200, motor.MaxAllowedSpeed);
        Assert.Equal(150, motor.MotorSpeed);
    }

    [Fact]
    public async Task WhenDependentPropertyInvalidDuringCapture_ThenValidatorRejectsWrite()
    {
        // Arrange: Motor with MaxAllowedSpeed=100
        var context = CreateContextWithMotorValidation();
        var motor = new Motor(context);

        // Act & Assert: Setting MotorSpeed=150 without raising limit is rejected during capture
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        Assert.Throws<ValidationException>(() => motor.MotorSpeed = 150);
    }

    [Fact]
    public async Task WhenConfirmedReplayWouldFailValidation_ThenValueLandsAndSourceIsNotReverted()
    {
        // Arrange: MaxAllowedSpeed is bound to a failing source, so it lands in failedSource and is
        // excluded from the local apply. The landed limit therefore stays 100 while MotorSpeed, which
        // its own source accepted, replays stamped Confirmed against that stale limit.
        // BestEffort is required: Rollback returns before the local apply when a source write fails.
        var context = CreateContextWithMotorValidation();
        var motor = new Motor(context);

        var speedSource = CreateSucceedingSource();
        var limitSource = CreateFailingSource();

        new PropertyReference(motor, nameof(Motor.MotorSpeed)).SetSource(speedSource.Object);
        new PropertyReference(motor, nameof(Motor.MaxAllowedSpeed)).SetSource(limitSource.Object);

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        motor.MaxAllowedSpeed = 200;
        motor.MotorSpeed = 150;

        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert: the limit write genuinely failed and is still reported as such
        Assert.Contains(exception.FailedChanges, c => c.Property.Name == nameof(Motor.MaxAllowedSpeed));

        // Assert: the speed write was accepted by its source, so it lands locally and is not
        // reported as failed. Re-validating it on replay would reject it against the stale limit.
        Assert.Equal(150, motor.MotorSpeed);
        Assert.DoesNotContain(exception.FailedChanges, c => c.Property.Name == nameof(Motor.MotorSpeed));

        // Assert: no compensating write back to the source. A replay rejection makes the commit
        // revert the accepted write, which other subscribers of that source observe as a flap.
        speedSource.Verify(
            s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WhenLocalCommitReplayViolatesAValidator_ThenTheChangeIsStillRejected()
    {
        // Arrange: no sources, so every change replays stamped Local and keeps its veto. The commit
        // position of a property is its FIRST write, so re-writing the limit downwards makes the replay
        // lower it before revalidating the speed against it. This pins the deliberate carve-out: only a
        // change a source confirmed skips replay validation, never a local one. The re-write must differ
        // from the landed value (100, set in the constructor), or the equality check drops it and the
        // pending value stays at the first write.
        var context = CreateContextWithMotorValidation();
        var motor = new Motor(context);

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        motor.MaxAllowedSpeed = 200;
        motor.MotorSpeed = 150;      // capture: 150 <= pending 200, accepted
        motor.MaxAllowedSpeed = 120; // re-write, commit position stays ahead of MotorSpeed

        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert: the replay lowered the limit first, then rejected the speed against it.
        Assert.Contains(exception.FailedChanges, c => c.Property.Name == nameof(Motor.MotorSpeed));
        Assert.Equal(120, motor.MaxAllowedSpeed);
        Assert.Equal(0, motor.MotorSpeed);
    }

    private static IInterceptorSubjectContext CreateContextWithMotorValidation()
    {
        return InterceptorSubjectContext
            .Create()
            .WithPropertyValidation()
            .WithService<IPropertyValidator>(() => new MotorSpeedValidator())
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking()
            .WithSourceTransactions();
    }

    /// <summary>
    /// Validates that MotorSpeed does not exceed MaxAllowedSpeed.
    /// Reads MaxAllowedSpeed through the property getter, which returns the pending value during transactions.
    /// </summary>
    private class MotorSpeedValidator : IPropertyValidator
    {
        public IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context)
        {
            var property = context.Property;
            if (property.Metadata.Name == nameof(Motor.MotorSpeed) &&
                context.Value is int speed &&
                property.Subject is Motor motor)
            {
                // Reading MaxAllowedSpeed goes through the interceptor chain,
                // so during a transaction it returns the pending value
                var maxAllowedSpeed = motor.MaxAllowedSpeed;
                if (speed > maxAllowedSpeed)
                {
                    return [new ValidationResult(
                        $"MotorSpeed {speed} exceeds MaxAllowedSpeed {maxAllowedSpeed}.")];
                }
            }

            return [];
        }
    }
}
