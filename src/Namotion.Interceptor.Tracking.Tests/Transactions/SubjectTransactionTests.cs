using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

[InterceptorSubject]
internal partial class DisposalCaptureGateSubject
{
    public partial DisposalCaptureGateValue? Value { get; set; }
}

internal sealed class DisposalCaptureGateValue(
    string value,
    ManualResetEventSlim? comparisonEntered = null,
    ManualResetEventSlim? continueComparison = null) : IEquatable<DisposalCaptureGateValue>
{
    private readonly string _value = value;

    public bool Equals(DisposalCaptureGateValue? other)
    {
        if (comparisonEntered is not null)
        {
            comparisonEntered.Set();
            if (!continueComparison!.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The test did not release the origin comparison within 10 seconds.");
            }
        }

        return other is not null && _value == other._value;
    }

    public override bool Equals(object? obj) => obj is DisposalCaptureGateValue other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode(StringComparison.Ordinal);
}

public class SubjectTransactionTests
{
    private static async Task RunWithoutAsyncLocalFlowAsync(Action action)
    {
        Task task;
        var flowControl = ExecutionContext.SuppressFlow();
        try
        {
            task = Task.Run(action);
        }
        finally
        {
            flowControl.Undo();
        }

        await task;
    }

    private static IInterceptorSubjectContext CreateTransactionContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
    }

    [Fact]
    public async Task WhenTransactionsNotEnabled_ThenBeginThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithPropertyChangeSubscriptions();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        });

        Assert.Contains("WithTransactions()", exception.Message);
    }


    [Fact]
    public async Task WhenTransactionIsBoundToAnUnrelatedContext_ThenWriteThrows()
    {
        // Arrange
        var subjectContext = CreateTransactionContext();
        var transactionContext = CreateTransactionContext();
        var person = new Person(subjectContext);

        using var transaction = await transactionContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => person.FirstName = "John");

        // Assert
        Assert.Contains("Transaction is bound to a different context", exception.Message);
        Assert.Empty(transaction.GetPendingChanges());
        Assert.Null(person.FirstName);
    }

    [Fact]
    public async Task WhenTransactionCommitted_ThenChangesAreApplied()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task WhenTransactionDisposedWithoutCommit_ThenChangesAreDiscarded()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Original" };

        // Act
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "Modified";
        }

        // Assert
        Assert.Equal("Original", person.FirstName);
    }

    [Fact]
    public async Task WhenReadingPropertyDuringTransaction_ThenPendingValueIsReturned()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Original" };

        // Act & Assert
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "Pending";
            Assert.Equal("Pending", person.FirstName);
            await transaction.CommitAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenSamePropertyWrittenTwice_ThenLastWriteWinsAndOriginalOldValuePreserved()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Original" };

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "First";
            person.FirstName = "Second";

            var pending = transaction.GetPendingChanges();
            Assert.Single(pending);
            Assert.Equal("Original", pending[0].GetOldValue<string?>());
            Assert.Equal("Second", pending[0].GetNewValue<string?>());

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal("Second", person.FirstName);
    }

    [Fact]
    public async Task WhenCommittedWithNoChanges_ThenSucceeds()
    {
        // Arrange
        var context = CreateTransactionContext();

        // Act & Assert
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            var commitTask = transaction.CommitAsync(CancellationToken.None);
            Assert.True(commitTask.IsCompletedSuccessfully, "Empty commit should complete synchronously.");
            await commitTask;
            Assert.Empty(transaction.GetPendingChanges());
        }
    }

    [Fact]
    public async Task WhenConflictDetected_ThenConflictExceptionThrown()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Original" };

        using (var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict))
        {
            person.FirstName = "InTransaction";

            await RunWithoutAsyncLocalFlowAsync(() => { person.FirstName = "ExternalChange"; });

            // Act & Assert
            var ex = await Assert.ThrowsAsync<SubjectTransactionConflictException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());

            Assert.Single(ex.ConflictingProperties);
            Assert.Equal(nameof(Person.FirstName), ex.ConflictingProperties[0].Name);
            Assert.Contains(nameof(Person.FirstName), ex.Message);
            Assert.Empty(ex.AppliedChanges);
            Assert.Empty(ex.FailedChanges);
        }
    }

    [Fact]
    public async Task WhenConflictBehaviorIsIgnore_ThenNoConflictException()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Original" };

        using (var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.Ignore))
        {
            person.FirstName = "InTransaction";

            await RunWithoutAsyncLocalFlowAsync(() => { person.FirstName = "ExternalChange"; });

            // Act
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal("InTransaction", person.FirstName);
    }

    [Fact]
    public async Task WhenTransactionAlreadyCommitted_ThenCommitAgainThrows()
    {
        // Arrange: a successful non-empty commit, so the committed state comes from the
        // full commit path rather than the empty-commit early return.
        var context = CreateTransactionContext();
        var person = new Person(context);

        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);
            Assert.Equal("John", person.FirstName);
            Assert.Empty(transaction.GetPendingChanges());

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
            Assert.Contains("already been committed", exception.Message);
        }
    }

    [Fact]
    public async Task WhenEmptyCommitIsTerminal_ThenLaterAccessUsesTheModel()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context);
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        await transaction.CommitAsync(CancellationToken.None);

        // Act
        person.FirstName = "landed-after-empty-commit";
        string? landedValue = null;
        await RunWithoutAsyncLocalFlowAsync(() => landedValue = person.FirstName);
        await RunWithoutAsyncLocalFlowAsync(() => person.FirstName = "external-after-empty-commit");

        // Assert
        Assert.Equal("landed-after-empty-commit", landedValue);
        Assert.Equal("external-after-empty-commit", person.FirstName);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenSuccessfulCommitIsTerminal_ThenLaterAccessUsesTheModel()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "model" };
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "committed";
        await transaction.CommitAsync(CancellationToken.None);

        // Act
        person.LastName = "landed-after-commit";
        string? landedValue = null;
        await RunWithoutAsyncLocalFlowAsync(() => landedValue = person.LastName);
        await RunWithoutAsyncLocalFlowAsync(() => person.LastName = "external-after-commit");

        // Assert
        Assert.Equal("committed", person.FirstName);
        Assert.Equal("landed-after-commit", landedValue);
        Assert.Equal("external-after-commit", person.LastName);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenCommitFailureIsTerminal_ThenLaterAccessUsesTheModel()
    {
        // Arrange
        var context = CreateTransactionContext();
        var subject = new TransactionCascadeSubject(context) { Plain = "model" };
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        subject.Plain = "applied";
        subject.Failing = "fails";
        subject.ThrowOnFailingWrite = true;
        await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Act
        subject.SideEffect = "landed-after-terminal-failure";
        string? landedValue = null;
        await RunWithoutAsyncLocalFlowAsync(() => landedValue = subject.SideEffect);
        await RunWithoutAsyncLocalFlowAsync(() => subject.SideEffect = "external-after-terminal-failure");

        // Assert
        Assert.Equal("applied", subject.Plain);
        Assert.Equal("landed-after-terminal-failure", landedValue);
        Assert.Equal("external-after-terminal-failure", subject.SideEffect);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenCommitFailureIsRetryable_ThenLaterWritesRemainCaptured()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "original" };
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);
        person.FirstName = "pending";
        await RunWithoutAsyncLocalFlowAsync(() => person.FirstName = "external");
        await Assert.ThrowsAsync<SubjectTransactionConflictException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Act
        person.LastName = "captured-after-conflict";

        // Assert
        Assert.Equal("captured-after-conflict", person.LastName);
        Assert.Contains(transaction.GetPendingChanges(),
            change => change.Property.Name == nameof(Person.LastName));

        string? modelLastName = null;
        await RunWithoutAsyncLocalFlowAsync(() => modelLastName = person.LastName);
        Assert.Null(modelLastName);

        await RunWithoutAsyncLocalFlowAsync(() => person.FirstName = "original");
        await transaction.CommitAsync(CancellationToken.None);
        Assert.Equal("pending", person.FirstName);
        Assert.Equal("captured-after-conflict", person.LastName);
    }

    [Fact]
    public async Task WhenTransactionDisposed_ThenCommitThrows()
    {
        // Arrange
        var context = CreateTransactionContext();
        var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        transaction.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task WhenNestedTransactionAttempted_ThenThrows()
    {
        // Arrange
        var context = CreateTransactionContext();

        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
            });
            Assert.Contains("Nested transactions are not supported", exception.Message);
        }
    }

    [Fact]
    public async Task WhenExclusiveLocking_ThenSecondTransactionWaits()
    {
        // Arrange
        var context = CreateTransactionContext();
        var order = new List<int>();
        var task1CanCommit = new ManualResetEventSlim(false);
        var task2Waiting = new ManualResetEventSlim(false);

        // Act
        // Both sides block until the other reaches its gate, and the waits below are unbounded, so a
        // participant left in the pool queue would hang the run rather than fail it.
        var task1 = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(async () =>
        {
            using (var t = await context.BeginTransactionAsync(
                TransactionFailureHandling.BestEffort,
                TransactionLocking.Exclusive))
            {
                lock (order) { order.Add(1); }
                task2Waiting.Set();
                task1CanCommit.Wait();
                await t.CommitAsync(CancellationToken.None);
            }
        });

        task2Waiting.Wait();
        var task2 = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(async () =>
        {
            task1CanCommit.Set();
            using (var t = await context.BeginTransactionAsync(
                TransactionFailureHandling.BestEffort,
                TransactionLocking.Exclusive))
            {
                lock (order) { order.Add(2); }
                await t.CommitAsync(CancellationToken.None);
            }
        });

        await Task.WhenAll(task1, task2);

        // Assert
        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task WhenOptimisticLocking_ThenBothTransactionsCanBegin()
    {
        // Arrange
        var context = CreateTransactionContext();
        var bothStarted = new CountdownEvent(2);

        // Act
        // Neither side can reach the countdown unless both are actually running.
        var task1 = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(async () =>
        {
            using (var t = await context.BeginTransactionAsync(
                TransactionFailureHandling.BestEffort,
                TransactionLocking.Optimistic,
                conflictBehavior: TransactionConflictBehavior.Ignore))
            {
                bothStarted.Signal();
                bothStarted.Wait(TimeSpan.FromSeconds(5));
                await t.CommitAsync(CancellationToken.None);
            }
        });

        var task2 = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(async () =>
        {
            using (var t = await context.BeginTransactionAsync(
                TransactionFailureHandling.BestEffort,
                TransactionLocking.Optimistic,
                conflictBehavior: TransactionConflictBehavior.Ignore))
            {
                bothStarted.Signal();
                bothStarted.Wait(TimeSpan.FromSeconds(5));
                await t.CommitAsync(CancellationToken.None);
            }
        });

        // Assert
        await Task.WhenAll(task1, task2);
    }

    [Fact]
    public async Task WhenTransactionCommitted_ThenObservableNotificationsFire()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = CreateTransactionContext();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var person = new Person(context);
        changes.Clear();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            Assert.Empty(changes);

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Contains(changes, c =>
            c.Property.Name == nameof(Person.FirstName) &&
            c.GetNewValue<string?>() == "John");
    }

    [Fact]
    public async Task WhenTransactionDisposedWithoutCommit_ThenNoObservableNotificationsFire()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = CreateTransactionContext();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var person = new Person(context) { FirstName = "Original" };
        changes.Clear();

        // Act
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "Modified";
        }

        // Assert
        Assert.Empty(changes);
    }

    [Fact]
    public async Task WhenGetPendingChangesCalled_ThenReturnsSnapshotOfPendingChanges()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            var pending = transaction.GetPendingChanges();

            // Assert
            Assert.Equal(2, pending.Count);
            Assert.Contains(pending, c => c.Property.Name == nameof(Person.FirstName));
            Assert.Contains(pending, c => c.Property.Name == nameof(Person.LastName));
        }
    }

    [Fact]
    public async Task WhenDisposeCalledMultipleTimes_ThenIsIdempotent()
    {
        // Arrange
        var context = CreateTransactionContext();
        var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        Assert.Same(transaction, SubjectTransaction.Current);

        // Act & Assert
        transaction.Dispose();
        Assert.Null(SubjectTransaction.Current);

        transaction.Dispose();
        Assert.Null(SubjectTransaction.Current);
    }

    [Fact]
    public async Task WhenMultipleSubjectsModified_ThenAllChangesCommittedAtomically()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person1 = new Person(context);
        var person2 = new Person(context);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person1.FirstName = "Alice";
            person2.FirstName = "Bob";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal("Alice", person1.FirstName);
        Assert.Equal("Bob", person2.FirstName);
    }

    [Fact]
    public async Task WhenExclusiveTransactionDisposed_ThenLockIsReleased()
    {
        // Arrange
        var context = CreateTransactionContext();

        using (await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Exclusive))
        {
        }

        // Act & Assert
        using (var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Exclusive))
        {
            await transaction.CommitAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenDerivedPropertyRead_ThenReflectsPendingChanges()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            // Assert
            Assert.Equal("John Doe", person.FullName);

            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.Equal("John Doe", person.FullName);
    }

    [Fact]
    public async Task WhenConflictExceptionThrown_ThenConflictingPropertiesAreReported()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Original1", LastName = "Original2" };

        using (var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict))
        {
            person.FirstName = "InTx1";
            person.LastName = "InTx2";

            await RunWithoutAsyncLocalFlowAsync(() =>
            {
                person.FirstName = "External1";
                person.LastName = "External2";
            });

            // Act & Assert
            var ex = await Assert.ThrowsAsync<SubjectTransactionConflictException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());

            Assert.Equal(2, ex.ConflictingProperties.Count);
            Assert.Contains(ex.ConflictingProperties, p => p.Name == nameof(Person.FirstName));
            Assert.Contains(ex.ConflictingProperties, p => p.Name == nameof(Person.LastName));
        }
    }

    [Fact]
    public async Task WhenPropertyNotChangedExternally_ThenNoConflictDetected()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Original" };

        // Act
        using (var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict))
        {
            person.FirstName = "Modified";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal("Modified", person.FirstName);
    }

    [Fact]
    public async Task WhenCommittingFromDifferentAsyncFlow_ThenThrows()
    {
        // Arrange: begin in a separate flow so its AsyncLocal current-transaction does not reach here.
        var context = CreateTransactionContext();
        var transaction = await Task.Run(async () =>
            await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort));

        // Act & Assert
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
            Assert.Contains("async flow", exception.Message);
        }
        finally
        {
            transaction.Dispose();
        }
    }

    [Fact]
    public async Task WhenDisposingFromDifferentAsyncFlow_ThenOtherTransactionSlotIsNotCleared()
    {
        // Arrange
        var context = CreateTransactionContext();

        // transaction1 is begun in a separate flow, so its SetCurrent does not reach this flow.
        // Optimistic locking means neither transaction takes the lock at begin, so they can coexist.
        var transaction1 = await Task.Run(async () =>
            await context.BeginTransactionAsync(
                TransactionFailureHandling.BestEffort, TransactionLocking.Optimistic));

        using var transaction2 = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, TransactionLocking.Optimistic);
        Assert.Same(transaction2, SubjectTransaction.Current);

        // Act: disposing transaction1 from transaction2's flow must not clear transaction2's slot.
        transaction1.Dispose();

        // Assert
        Assert.Same(transaction2, SubjectTransaction.Current);
    }

    [Fact]
    public async Task WhenAmbientTransactionIsDisposedAndAnotherIsOpen_ThenWriteAppliesToModelAndIsNotCapturedByTheOtherTransaction()
    {
        // Arrange: capture the flow while a transaction is current, then dispose that transaction. The
        // captured flow keeps pointing at the disposed transaction, which is what any thread started
        // inside a transaction sees for its whole life (an Rx EventLoopScheduler worker, for example).
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Old" };

        var disposedTransaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var frozenFlow = ExecutionContext.Capture()
            ?? throw new InvalidOperationException("Execution context flow must not be suppressed in this test.");
        disposedTransaction.Dispose();

        // The second transaction keeps the process-wide active count above zero, so the interceptor does
        // not take its no-transaction fast path.
        using var openTransaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.LastName = "Doe";

        // Act
        ExecutionContext.Run(frozenFlow, _ => person.FirstName = "New", null);

        // Assert: the write went straight to the model instead of into the pooled dictionary. Read it
        // from a flow with no ambient transaction so no pending value can mask the stored value.
        string? modelFirstName = null;
        await RunWithoutAsyncLocalFlowAsync(() => modelFirstName = person.FirstName);
        Assert.Equal("New", modelFirstName);
        Assert.DoesNotContain(openTransaction.GetPendingChanges(), change => change.Property.Name == nameof(Person.FirstName));

        await openTransaction.CommitAsync(CancellationToken.None);
        Assert.Equal("New", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task WhenAmbientTransactionIsDisposedAndAnotherIsOpen_ThenDerivedPropertiesStillRecalculate()
    {
        // Arrange: same frozen-flow setup as the write test above. The write reaches the model, so its
        // derived cascade has to run with it: the disposed transaction has no commit left to replay it on,
        // so suppressing the cascade would drop the recalculation forever.
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Old", LastName = "Doe" };

        var disposedTransaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var frozenFlow = ExecutionContext.Capture()
            ?? throw new InvalidOperationException("Execution context flow must not be suppressed in this test.");
        disposedTransaction.Dispose();

        // Keeps the process-wide active count above zero, so the derived handler does not take its
        // no-transaction fast path.
        using var openTransaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        var changedProperties = new List<string>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changedProperties.Add(change.Property.Name));

        // Act
        ExecutionContext.Run(frozenFlow, _ => person.FirstName = "New", null);

        // Assert
        Assert.Contains(nameof(Person.FirstName), changedProperties);
        Assert.Contains(nameof(Person.FullName), changedProperties);
        Assert.Contains(nameof(Person.FullNameWithPrefix), changedProperties);
    }

    [Fact]
    public async Task WhenAmbientTransactionIsOpen_ThenWriteIsCapturedInsteadOfAppliedToModel()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Old" };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "New";

        // Assert
        var pendingChange = Assert.Single(transaction.GetPendingChanges());
        Assert.Equal(nameof(Person.FirstName), pendingChange.Property.Name);
        Assert.Equal("New", pendingChange.GetNewValue<string?>());

        string? modelFirstName = null;
        await RunWithoutAsyncLocalFlowAsync(() => modelFirstName = person.FirstName);
        Assert.Equal("Old", modelFirstName);
    }

    [Fact]
    public async Task WhenAmbientTransactionIsDisposedAndAnotherIsOpen_ThenReadReturnsTheModelValueInsteadOfAPendingValue()
    {
        // Arrange: capture the flow while a transaction is current and holds a pending value, then dispose
        // that transaction. The captured flow keeps pointing at the disposed transaction, which is what any
        // thread started inside a transaction sees for its whole life (an Rx EventLoopScheduler worker,
        // for example).
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Model" };

        var disposedTransaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var frozenFlow = ExecutionContext.Capture()
            ?? throw new InvalidOperationException("Execution context flow must not be suppressed in this test.");
        person.FirstName = "PendingInDisposedTransaction";
        disposedTransaction.Dispose();

        // The second transaction keeps the process-wide active count above zero, so the interceptor does not
        // take its no-transaction fast path. Whether the pool hands it the dictionary the first transaction
        // returned does not matter: Dispose nulled that transaction's reference to it.
        using var openTransaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "PendingInOpenTransaction";

        // Act
        string? readThroughDisposedTransaction = null;
        ExecutionContext.Run(frozenFlow, _ => readThroughDisposedTransaction = person.FirstName, null);

        // Assert: neither the disposed transaction's captured value nor the open transaction's pending value.
        Assert.Equal("Model", readThroughDisposedTransaction);

        // The disposed transaction holds nothing to serve, whichever transaction owns the pooled dictionary.
        Assert.Empty(disposedTransaction.GetPendingChanges());
        Assert.False(disposedTransaction.TryGetPendingValue<string?>(
            new PropertyReference(person, nameof(Person.FirstName)), out _));

        string? modelFirstName = null;
        await RunWithoutAsyncLocalFlowAsync(() => modelFirstName = person.FirstName);
        Assert.Equal("Model", modelFirstName);

        // The open transaction still masks reads in its own flow.
        Assert.Equal("PendingInOpenTransaction", person.FirstName);
    }

    [Fact]
    public async Task WhenDisposeWinsAfterThePublicWritePassedItsAmbientCheck_ThenTheWriteLandsWithoutAnException()
    {
        // Arrange: final-origin resolution invokes user equality immediately before TryCaptureChange. Park
        // there so Dispose deterministically wins after the interceptor's lock-free ambient-state check.
        // Regression mutation: throwing instead of returning false from TryCaptureChange when disposal wins
        // lets ObjectDisposedException escape this public write path instead of falling through to the model.
        var context = CreateTransactionContext();
        var subject = new DisposalCaptureGateSubject(context)
        {
            Value = new DisposalCaptureGateValue("Model")
        };
        var property = new PropertyReference(subject, nameof(DisposalCaptureGateSubject.Value));
        using var comparisonEntered = new ManualResetEventSlim();
        using var continueComparison = new ManualResetEventSlim();
        var sentValue = new DisposalCaptureGateValue("AfterDispose", comparisonEntered, continueComparison);
        var valueWrittenAfterDispose = new DisposalCaptureGateValue("AfterDispose");

        var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var frozenFlow = ExecutionContext.Capture()
            ?? throw new InvalidOperationException("Execution context flow must not be suppressed in this test.");
        Exception? escapedException = null;
        var writerThread = new Thread(() => ExecutionContext.Run(frozenFlow, _ =>
        {
            try
            {
                property.SetValueFromOrigin(
                    ChangeOrigin.FromSource(new object()),
                    changedTimestamp: null,
                    receivedTimestamp: null,
                    value: valueWrittenAfterDispose,
                    sentValue: sentValue);
            }
            catch (Exception exception)
            {
                escapedException = exception;
            }
        }, null));

        // Act
        bool writerStopped;
        try
        {
            writerThread.Start();
            Assert.True(comparisonEntered.Wait(TimeSpan.FromSeconds(10)), "write did not reach origin comparison");
            transaction.Dispose();
        }
        finally
        {
            transaction.Dispose();
            continueComparison.Set();

            // Only capture here: an assertion inside the finally would replace a failure from the try body,
            // and a writer parked elsewhere never stops, so the join failure would hide the real one.
            writerStopped = writerThread.Join(TimeSpan.FromSeconds(10));
        }

        // Assert
        Assert.True(writerStopped, "writer did not stop");
        Assert.Null(escapedException);
        Assert.Same(valueWrittenAfterDispose, subject.Value);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenAmbientTransactionWasDisposedByAnotherFlow_ThenTheFrozenFlowCanBeginANewTransaction()
    {
        // Arrange: freeze a flow while a transaction is current, then dispose that transaction from the flow
        // that owns it. Dispose clears the ambient slot only for the disposing flow, so the frozen flow keeps
        // pointing at the disposed transaction for the rest of its life.
        var context = CreateTransactionContext();
        var disposedTransaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var frozenFlow = ExecutionContext.Capture()
            ?? throw new InvalidOperationException("Execution context flow must not be suppressed in this test.");
        disposedTransaction.Dispose();

        // Act
        SubjectTransaction? newTransaction = null;
        try
        {
            Exception? failure = null;
            ExecutionContext.Run(frozenFlow, _ =>
            {
                Assert.Same(disposedTransaction, SubjectTransaction.Current);
                try
                {
                    newTransaction = context
                        .BeginTransactionAsync(TransactionFailureHandling.BestEffort)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }, null);

            // Assert
            Assert.Null(failure);
            Assert.NotNull(newTransaction);
        }
        finally
        {
            newTransaction?.Dispose();
        }
    }

    [Fact]
    public async Task WhenCommitIsParkedOnTheTransactionLockAndTheTransactionIsDisposed_ThenTheCommitCompletesWithoutFailing()
    {
        // Arrange: an optimistic commit parks acquiring the per-context transaction lock. A dispose on
        // another flow then releases the pending-changes buffer while the commit is still parked, so the
        // commit resumes with a null buffer. The null tolerance in StartCommitAndSnapshotChanges and
        // FinishCommit is what keeps that from being a NullReferenceException; it is load-bearing, not
        // defensive.
        var context = CreateTransactionContext();
        var person = new Person(context) { LastName = "Model" };

        var lockHolder = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        SubjectTransaction? parkedTransaction = null;
        Task? parkedCommit = null;
        var commitParked = new ManualResetEventSlim();

        // Suppressed so the parked transaction does not inherit lockHolder as its ambient transaction.
        var flowControl = ExecutionContext.SuppressFlow();
        Task starter;
        try
        {
            starter = Task.Run(async () =>
            {
                parkedTransaction = await context.BeginTransactionAsync(
                    TransactionFailureHandling.BestEffort, TransactionLocking.Optimistic);
                person.LastName = "Pending";

                // CommitAsync has already parked on the lock by the time it returns its incomplete task.
                parkedCommit = parkedTransaction.CommitAsync(CancellationToken.None).AsTask();
                commitParked.Set();
                await parkedCommit;
            });
        }
        finally
        {
            flowControl.Undo();
        }

        Assert.True(commitParked.Wait(TimeSpan.FromSeconds(10)), "commit did not reach the transaction lock");

        // Act
        parkedTransaction!.Dispose();
        lockHolder.Dispose();
        await starter.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.True(parkedCommit!.IsCompletedSuccessfully);
        Assert.Empty(parkedTransaction.GetPendingChanges());

        string? modelLastName = null;
        await RunWithoutAsyncLocalFlowAsync(() => modelLastName = person.LastName);
        Assert.Equal("Model", modelLastName);
    }

    [Fact]
    public async Task WhenTransactionIsDisposedDuringCommit_ThenTryCaptureChangeReturnsFalse()
    {
        // Arrange
        var writer = new ControllableTransactionWriter();
        var context = CreateTransactionContext();
        context.AddService<ITransactionWriter>(writer);
        var person = new Person(context) { LastName = "Model" };
        var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Pending";
        var commitTask = transaction.CommitAsync(CancellationToken.None).AsTask();
        bool TryCaptureChange() => transaction.TryCaptureChange(
            new PropertyReference(person, nameof(Person.LastName)),
            ChangeOrigin.Local,
            DateTimeOffset.UnixEpoch,
            receivedTimestamp: null,
            currentValue: "Model",
            newValue: "AfterDispose");

        try
        {
            Assert.True(writer.CommitStarted.Wait(TimeSpan.FromSeconds(10)), "commit did not reach the writer");
            Assert.Throws<InvalidOperationException>(() => TryCaptureChange());
            transaction.Dispose();

            // Act
            var captured = TryCaptureChange();

            // Assert
            Assert.False(captured);
        }
        finally
        {
            writer.Release();
            await commitTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task WhenDisposedDuringExternalWriterCommit_ThenRawWriteLandsBeforeFrozenReplayOverwritesIt()
    {
        // Arrange
        var writer = new ControllableTransactionWriter();
        var context = CreateTransactionContext();
        context.AddService<ITransactionWriter>(writer);
        var person = new Person(context) { FirstName = "Model" };
        var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "FrozenReplay";
        var commitTask = transaction.CommitAsync(CancellationToken.None).AsTask();

        try
        {
            Assert.True(writer.CommitStarted.Wait(TimeSpan.FromSeconds(10)), "commit did not reach the writer");
            transaction.Dispose();
            Assert.Null(SubjectTransaction.Current);

            // Act: disposal makes the ambient transaction inactive even though its frozen commit continues.
            person.FirstName = "RawAfterDispose";

            // Assert: observe the landed model outside any ambient transaction before replay is released.
            string? intermediateModelValue = null;
            await RunWithoutAsyncLocalFlowAsync(() => intermediateModelValue = person.FirstName);
            Assert.Equal("RawAfterDispose", intermediateModelValue);
        }
        finally
        {
            transaction.Dispose();
            writer.Release();
            await commitTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // Assert: transaction disposal is not isolation from an already-frozen commit snapshot.
        Assert.Equal("FrozenReplay", person.FirstName);
    }

    [Fact]
    public async Task WhenCommitIsBlockedInWriter_ThenCallerAccessCannotEscapeTheSnapshot()
    {
        // Arrange
        var writer = new ControllableTransactionWriter();
        var context = CreateTransactionContext();
        context.AddService<ITransactionWriter>(writer);
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "model",
            SideEffect = "side-model",
            DerivedWithSetter = "d0"
        };
        _ = subject.CombinedAgain;
        var sideEffectSubject = new SideEffectWritePerson(context) { Name = "before" };
        var sideEffectBeforeCommit = sideEffectSubject.SideEffectTarget;

        var derivedChanges = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name is
                nameof(TransactionCascadeSubject.Combined) or
                nameof(TransactionCascadeSubject.CombinedAgain))
            .Subscribe(derivedChanges.Add);

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        subject.Plain = "snapshot";
        sideEffectSubject.Name = "committed-name";
        var commitTask = transaction.CommitAsync(CancellationToken.None).AsTask();

        try
        {
            Assert.True(writer.CommitStarted.Wait(TimeSpan.FromSeconds(10)), "commit did not reach the writer");

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => _ = subject.Plain);
            Assert.Throws<InvalidOperationException>(() => subject.SideEffect = "outside-snapshot");
            Assert.Throws<InvalidOperationException>(() => subject.DerivedWithSetter = "outside-snapshot");

            var frozenChanges = transaction.GetPendingChanges();
            Assert.Equal(2, frozenChanges.Count);
            Assert.Contains(frozenChanges, change =>
                change.Property.Name == nameof(TransactionCascadeSubject.Plain) &&
                change.GetNewValue<string>() == "snapshot");
            Assert.Contains(frozenChanges, change =>
                change.Property.Name == nameof(SideEffectWritePerson.Name) &&
                change.GetNewValue<string>() == "committed-name");

            string? modelPlain = null;
            string? modelSideEffect = null;
            string? modelDerivedWithSetter = null;
            await RunWithoutAsyncLocalFlowAsync(() =>
            {
                modelPlain = subject.Plain;
                modelSideEffect = subject.SideEffect;
                modelDerivedWithSetter = subject.DerivedWithSetter;
            });
            Assert.Equal("model", modelPlain);
            Assert.Equal("side-model", modelSideEffect);
            Assert.Equal("d0", modelDerivedWithSetter);
        }
        finally
        {
            writer.Release();
            await commitTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal("snapshot", subject.Plain);
        Assert.Equal("side-model", subject.SideEffect);
        Assert.Equal("d0", subject.DerivedWithSetter);
        Assert.Equal("[snapshot|d0]", subject.CombinedAgain);
        Assert.Equal("committed-name", sideEffectSubject.Name);
        Assert.NotEqual(sideEffectBeforeCommit, sideEffectSubject.SideEffectTarget);
        Assert.Contains(derivedChanges, change =>
            change.Property.Name == nameof(TransactionCascadeSubject.Combined) &&
            change.GetNewValue<string>() == "snapshot|d0");
        Assert.Contains(derivedChanges, change =>
            change.Property.Name == nameof(TransactionCascadeSubject.CombinedAgain) &&
            change.GetNewValue<string>() == "[snapshot|d0]");
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenCommitIsBlockedInWriter_ThenForkedAmbientAccessIsRejected()
    {
        // Arrange
        var writer = new ControllableTransactionWriter();
        var context = CreateTransactionContext();
        context.AddService<ITransactionWriter>(writer);
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "model",
            SideEffect = "side-model",
            DerivedWithSetter = "d0"
        };

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        subject.Plain = "snapshot";
        var commitTask = transaction.CommitAsync(CancellationToken.None).AsTask();

        try
        {
            Assert.True(writer.CommitStarted.Wait(TimeSpan.FromSeconds(10)), "commit did not reach the writer");

            // Act
            var accessTask = Task.Run(() =>
            {
                var readException = CaptureException(() => _ = subject.Plain);
                var writeException = CaptureException(() => subject.SideEffect = "forked-outside-snapshot");
                var derivedWriteException = CaptureException(
                    () => subject.DerivedWithSetter = "forked-outside-snapshot");
                return (readException, writeException, derivedWriteException);
            });
            var exceptions = await accessTask.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.IsType<InvalidOperationException>(exceptions.readException);
            Assert.IsType<InvalidOperationException>(exceptions.writeException);
            Assert.IsType<InvalidOperationException>(exceptions.derivedWriteException);

            var frozenChange = Assert.Single(transaction.GetPendingChanges());
            Assert.Equal(nameof(TransactionCascadeSubject.Plain), frozenChange.Property.Name);
            Assert.Equal("snapshot", frozenChange.GetNewValue<string>());

            string? modelPlain = null;
            string? modelSideEffect = null;
            string? modelDerivedWithSetter = null;
            await RunWithoutAsyncLocalFlowAsync(() =>
            {
                modelPlain = subject.Plain;
                modelSideEffect = subject.SideEffect;
                modelDerivedWithSetter = subject.DerivedWithSetter;
            });
            Assert.Equal("model", modelPlain);
            Assert.Equal("side-model", modelSideEffect);
            Assert.Equal("d0", modelDerivedWithSetter);
        }
        finally
        {
            writer.Release();
            await commitTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal("snapshot", subject.Plain);
        Assert.Equal("side-model", subject.SideEffect);
        Assert.Equal("d0", subject.DerivedWithSetter);
    }

    [Fact]
    public async Task WhenWriterAccessesAmbientPropertyDuringCommit_ThenCommitReportsConcurrentAccess()
    {
        // Arrange
        var context = CreateTransactionContext();
        var subject = new TransactionCascadeSubject(context) { Plain = "model" };
        context.AddService<ITransactionWriter>(new PropertyAccessingTransactionWriter(() => _ = subject.Plain));
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        subject.Plain = "pending";

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        var error = Assert.IsType<InvalidOperationException>(Assert.Single(exception.Errors));
        Assert.Contains("commit is in progress", error.Message);
        Assert.Equal("model", subject.Plain);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenSourceValueIsTransformedBeforeCapture_ThenTheCapturedOriginIsLocal()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions()
            .WithRegistry();
        context.AddService<IWriteInterceptor>(new TransformingWriteInterceptor());

        var person = new Person(context);
        var source = new object();
        var registeredProperty = person.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(Person.LastName))!;

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        registeredProperty.SetValueFromSource(source, null, null, "smith");

        // Assert
        var pending = Assert.Single(transaction.GetPendingChanges());
        Assert.Equal("SMITH", pending.GetNewValue<string?>());
        Assert.Equal(ChangeOriginKind.Local, pending.Origin.Kind);

        await transaction.CommitAsync(CancellationToken.None);
        Assert.Equal("SMITH", person.LastName);
    }

    [Fact]
    public async Task WhenBestEffortPartiallyApplies_ThenDerivedAndDerivedOfDerivedTrackTheAppliedModel()
    {
        // Arrange
        var context = CreateTransactionContext();
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "original",
            DerivedWithSetter = "d0"
        };
        _ = subject.CombinedAgain;

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name is
                nameof(TransactionCascadeSubject.Combined) or
                nameof(TransactionCascadeSubject.CombinedAgain))
            .Subscribe(changes.Add);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            subject.Plain = "applied";
            subject.Failing = "fails";
            subject.ThrowOnFailingWrite = true;
            await Assert.ThrowsAsync<SubjectTransactionException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
        }

        // Assert
        Assert.Collection(
            changes,
            change => AssertDerivedChange(change, nameof(TransactionCascadeSubject.Combined), "original|d0", "applied|d0"),
            change => AssertDerivedChange(change, nameof(TransactionCascadeSubject.CombinedAgain), "[original|d0]", "[applied|d0]"));
        Assert.Equal("applied", subject.Plain);
        Assert.Equal("applied|d0", subject.Combined);
        Assert.Equal("[applied|d0]", subject.CombinedAgain);

        changes.Clear();
        subject.DerivedWithSetter = "d1";
        Assert.Contains(changes, change =>
            change.Property.Name == nameof(TransactionCascadeSubject.Combined) &&
            change.GetOldValue<string>() == "applied|d0" &&
            change.GetNewValue<string>() == "applied|d1");
    }

    [Fact]
    public async Task WhenRollbackRevertsALocalApply_ThenDerivedAndDerivedOfDerivedReturnToTheOriginalModel()
    {
        // Arrange
        var context = CreateTransactionContext();
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "original",
            DerivedWithSetter = "d0"
        };
        _ = subject.CombinedAgain;

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name is
                nameof(TransactionCascadeSubject.Combined) or
                nameof(TransactionCascadeSubject.CombinedAgain))
            .Subscribe(changes.Add);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback))
        {
            subject.Plain = "temporarily-applied";
            subject.Failing = "fails";
            subject.ThrowOnFailingWrite = true;
            await Assert.ThrowsAsync<SubjectTransactionException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
        }

        // Assert
        Assert.Collection(
            changes,
            change => AssertDerivedChange(change, nameof(TransactionCascadeSubject.Combined), "original|d0", "temporarily-applied|d0"),
            change => AssertDerivedChange(change, nameof(TransactionCascadeSubject.CombinedAgain), "[original|d0]", "[temporarily-applied|d0]"),
            change => AssertDerivedChange(change, nameof(TransactionCascadeSubject.Combined), "temporarily-applied|d0", "original|d0"),
            change => AssertDerivedChange(change, nameof(TransactionCascadeSubject.CombinedAgain), "[temporarily-applied|d0]", "[original|d0]"));
        Assert.Equal("original", subject.Plain);
        Assert.Equal("original|d0", subject.Combined);
        Assert.Equal("[original|d0]", subject.CombinedAgain);

        changes.Clear();
        subject.DerivedWithSetter = "d1";
        Assert.Contains(changes, change =>
            change.Property.Name == nameof(TransactionCascadeSubject.Combined) &&
            change.GetOldValue<string>() == "original|d0" &&
            change.GetNewValue<string>() == "original|d1");
    }

    [Fact]
    public async Task WhenConflictPreventsApply_ThenDerivedTrackingRemainsOnTheExternalModel()
    {
        // Arrange
        var context = CreateTransactionContext();
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "original",
            DerivedWithSetter = "d0"
        };
        _ = subject.CombinedAgain;

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name is
                nameof(TransactionCascadeSubject.Combined) or
                nameof(TransactionCascadeSubject.CombinedAgain))
            .Subscribe(changes.Add);

        // Act
        using (var transaction = await context.BeginTransactionAsync(
                   TransactionFailureHandling.BestEffort,
                   TransactionLocking.Optimistic,
                   conflictBehavior: TransactionConflictBehavior.FailOnConflict))
        {
            subject.Plain = "transaction";
            await RunWithoutAsyncLocalFlowAsync(() => subject.Plain = "external");
            changes.Clear();

            await Assert.ThrowsAsync<SubjectTransactionConflictException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
            Assert.Empty(changes);
        }

        // Assert
        Assert.Equal("external", subject.Plain);
        Assert.Equal("external|d0", subject.Combined);
        Assert.Equal("[external|d0]", subject.CombinedAgain);

        subject.DerivedWithSetter = "d1";
        Assert.Contains(changes, change =>
            change.Property.Name == nameof(TransactionCascadeSubject.Combined) &&
            change.GetOldValue<string>() == "external|d0" &&
            change.GetNewValue<string>() == "external|d1");
    }

    [Fact]
    public async Task WhenWriterFailurePreventsApply_ThenNoDerivedTrackingStateChanges()
    {
        // Arrange
        var context = CreateTransactionContext();
        context.AddService<ITransactionWriter>(new FailingTransactionWriter());
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "original",
            DerivedWithSetter = "d0"
        };
        _ = subject.CombinedAgain;

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name is
                nameof(TransactionCascadeSubject.Combined) or
                nameof(TransactionCascadeSubject.CombinedAgain))
            .Subscribe(changes.Add);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback))
        {
            subject.Plain = "not-applied";
            await Assert.ThrowsAsync<SubjectTransactionException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
        }

        // Assert
        Assert.Empty(changes);
        Assert.Equal("original", subject.Plain);
        Assert.Equal("original|d0", subject.Combined);
        Assert.Equal("[original|d0]", subject.CombinedAgain);

        subject.DerivedWithSetter = "d1";
        Assert.Contains(changes, change =>
            change.Property.Name == nameof(TransactionCascadeSubject.Combined) &&
            change.GetOldValue<string>() == "original|d0" &&
            change.GetNewValue<string>() == "original|d1");
    }

    private static void AssertDerivedChange(
        SubjectPropertyChange change,
        string propertyName,
        string oldValue,
        string newValue)
    {
        Assert.Equal(propertyName, change.Property.Name);
        Assert.Equal(oldValue, change.GetOldValue<string>());
        Assert.Equal(newValue, change.GetNewValue<string>());
    }

    private sealed class FailingTransactionWriter : ITransactionWriter
    {
        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
        {
            return new ValueTask<SourceWriteResult>(new SourceWriteResult(
                Written: [],
                Failed: changes.ToArray(),
                Errors: [new InvalidOperationException("Writer rejected the changes.")],
                RevertState: null));
        }

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
            => new(new SourceRevertResult([], []));
    }

    private sealed class ControllableTransactionWriter : ITransactionWriter
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim CommitStarted { get; } = new();

        public async ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
        {
            CommitStarted.Set();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return new SourceWriteResult([], [], [], RevertState: null);
        }

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
            => new(new SourceRevertResult([], []));

        public void Release() => _release.TrySetResult();
    }

    private sealed class PropertyAccessingTransactionWriter(Action accessProperty) : ITransactionWriter
    {
        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
        {
            accessProperty();
            return new ValueTask<SourceWriteResult>(new SourceWriteResult([], [], [], RevertState: null));
        }

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
            => new(new SourceRevertResult([], []));
    }

    private static Exception? CaptureException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// Writes nothing to any source but marks each accepted snapshot slot with a fixed source in place,
    /// emulating a custom writer fulfilling the in-place marking contract.
    /// </summary>
    private sealed class MarkingTransactionWriter(object source) : ITransactionWriter
    {
        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
        {
            var span = changes.Span;
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = span[i].WithOrigin(ChangeOrigin.Confirmed(source));
            }
            return new ValueTask<SourceWriteResult>(new SourceWriteResult([], [], [], RevertState: null));
        }

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
            => new(new SourceRevertResult([], []));
    }

    [Fact]
    public async Task WhenCustomWriterMarksSnapshotInPlace_ThenApplyNotificationsCarryThatSource()
    {
        // Arrange
        var source = new object();
        var changes = new List<SubjectPropertyChange>();
        var context = CreateTransactionContext();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        context.AddService<ITransactionWriter>(new MarkingTransactionWriter(source));

        var person = new Person(context);
        changes.Clear();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert: the local apply published the FirstName change carrying the writer's source.
        var firstNameChange = Assert.Single(changes, c => c.Property.Name == nameof(Person.FirstName));
        Assert.Same(source, firstNameChange.Origin.Source);
        Assert.Equal("John", firstNameChange.GetNewValue<string?>());
    }

    /// <summary>
    /// Fulfills the writer contract but never marks any snapshot slot, like a custom writer
    /// predating the in-place marking contract.
    /// </summary>
    private sealed class NonMarkingTransactionWriter : ITransactionWriter
    {
        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
            => new(new SourceWriteResult([], [], [], RevertState: null));

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
            => new(new SourceRevertResult([], []));
    }

    [Fact]
    public async Task WhenCustomWriterDoesNotMarkSnapshot_ThenApplyNotificationsCarryNoSource()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = CreateTransactionContext();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        context.AddService<ITransactionWriter>(new NonMarkingTransactionWriter());

        var person = new Person(context);
        changes.Clear();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert: the commit succeeds, but unmarked slots publish with no source, so an outbound
        // connector queue would not recognize the notification as an echo and would push the value
        // to the source a second time (the documented graceful degradation for non-marking writers).
        var firstNameChange = Assert.Single(changes, c => c.Property.Name == nameof(Person.FirstName));
        Assert.Null(firstNameChange.Origin.Source);
        Assert.Equal("John", firstNameChange.GetNewValue<string?>());
        Assert.Equal("John", person.FirstName);
    }

}
