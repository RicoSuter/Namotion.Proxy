using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

public class SubjectTransactionAsyncTests
{
    [Fact]
    public async Task WhenExclusiveTransactionActive_ThenSecondBeginWaitsUntilFirstEnds()
    {
        // Arrange
        // Test that BeginTransactionAsync serializes concurrent transactions
        // The lock lives on SubjectTransactionInterceptor (one per context)
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();

        Task<SubjectTransaction> tx2Task;
        var tx2AcquiredTcs = new TaskCompletionSource<bool>();
        var tx2Started = new TaskCompletionSource<bool>();

        // Act
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            // Try to acquire another transaction in the background
            // Use ExecutionContext.SuppressFlow to prevent AsyncLocal inheritance
            using (ExecutionContext.SuppressFlow())
            {
                tx2Task = Task.Run(async () =>
                {
                    tx2Started.SetResult(true);
                    var tx = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
                    tx2AcquiredTcs.SetResult(true);
                    return tx;
                });
            }

            // Assert
            // Wait for tx2 to start, then verify it hasn't acquired the lock
            await tx2Started.Task;
            await Task.Yield();
            Assert.False(tx2AcquiredTcs.Task.IsCompleted, "tx2 should be waiting for lock");
        }

        // Now tx2 should complete
        using var tx2 = await tx2Task;
        Assert.True(await tx2AcquiredTcs.Task);
    }

    [Fact]
    public async Task WhenTransactionBegun_ThenItIsBoundToTheContext()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();

        // Act
        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        // Assert
        Assert.NotNull(tx);
        Assert.Same(context, tx.Context);
    }

    [Fact]
    public async Task WhenWritingSubjectOfDifferentContext_ThenThrows()
    {
        // Arrange
        var context1 = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();

        var context2 = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();

        var person1 = new Person(context1);
        var person2 = new Person(context2);

        using var tx = await context1.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        // Act
        // Should work - same context
        person1.FirstName = "John";

        // Should throw - different context
        var ex = Assert.Throws<InvalidOperationException>(() => person2.FirstName = "Jane");

        // Assert
        Assert.Contains("different context", ex.Message);
    }

    [Fact]
    public async Task WhenWritingTwoSubjectsOfSameContext_ThenBothChangesAreCaptured()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();

        var person1 = new Person(context);
        var person2 = new Person(context);

        // Both subjects are attached to the same exact context, so they share one
        // SubjectTransactionInterceptor

        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        // Assert
        // Verify transaction context is the original context
        Assert.Same(context, tx.Context);

        // Act
        // Should work - same interceptor is accessible from both via fallback resolution
        person1.FirstName = "John";
        person2.FirstName = "Jane";

        // Assert
        var changes = tx.GetPendingChanges();
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public async Task WhenConflictBehaviorSpecified_ThenTransactionStoresIt()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();

        // Act
        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            conflictBehavior: TransactionConflictBehavior.Ignore);

        // Assert
        Assert.Equal(TransactionConflictBehavior.Ignore, tx.ConflictBehavior);
    }

    [Fact]
    public async Task WhenValueChangedExternallyWithFailOnConflict_ThenCommitThrowsConflict()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();

        var person = new Person(context);
        person.FirstName = "Original";

        // Start transaction with FailOnConflict
        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);

        // Make a change within the transaction - captures OldValue = "Original"
        person.FirstName = "TransactionValue";

        // Verify change was captured
        var changes = tx.GetPendingChanges();
        Assert.Single(changes);
        Assert.Equal("Original", changes[0].GetOldValue<string>());

        // Simulate an external change by running outside the transaction's AsyncLocal context
        // This mimics another thread/process modifying the underlying value
        Task externalTask;
        var asyncFlowControl = ExecutionContext.SuppressFlow();
        try
        {
            externalTask = Task.Run(() => person.FirstName = "ExternalChange");
        }
        finally
        {
            asyncFlowControl.Undo();
        }
        await externalTask;

        // Act
        // CommitAsync should throw because current value != captured OldValue
        var ex = await Assert.ThrowsAsync<SubjectTransactionConflictException>(() => tx.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Contains(nameof(Person.FirstName), ex.Message);
        Assert.Single(ex.ConflictingProperties);
    }

    [Fact]
    public async Task WhenValueChangedExternallyWithIgnoreConflict_ThenCommitSucceeds()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();

        var person = new Person(context);
        person.FirstName = "Original";

        // Start transaction with Ignore behavior
        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            conflictBehavior: TransactionConflictBehavior.Ignore);

        // Make a change within the transaction
        person.FirstName = "TransactionValue";

        // Simulate an external change by running outside the transaction's AsyncLocal context
        Task externalTask;
        var asyncFlowControl = ExecutionContext.SuppressFlow();
        try
        {
            externalTask = Task.Run(() => person.FirstName = "ExternalChange");
        }
        finally
        {
            asyncFlowControl.Undo();
        }
        await externalTask;

        // Act
        // CommitAsync should NOT throw because ConflictBehavior is Ignore
        await tx.CommitAsync(CancellationToken.None);

        // Assert
        // Verify the transaction value was applied (overwrites external change)
        Assert.Equal("TransactionValue", person.FirstName);
    }

    [Fact]
    public async Task WhenNoExternalChangeWithFailOnConflict_ThenCommitSucceeds()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();

        var person = new Person(context);
        person.FirstName = "Original";

        // Start transaction with FailOnConflict
        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);

        // Make a change within the transaction
        person.FirstName = "TransactionValue";

        // Act
        // CommitAsync should succeed - no external changes to underlying value
        await tx.CommitAsync(CancellationToken.None);

        // Assert
        // Verify the value was applied
        Assert.Equal("TransactionValue", person.FirstName);
    }

    [Fact]
    public async Task WhenOldValueIsNullAndUnchanged_ThenFailOnConflictCommitSucceeds()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();

        var person = new Person(context);
        // FirstName starts as null

        // Start transaction with FailOnConflict
        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);

        // Make a change within the transaction - captures OldValue = null
        person.FirstName = "NewValue";

        // Act
        // CommitAsync should succeed - underlying value is still null
        await tx.CommitAsync(CancellationToken.None);

        // Assert
        // Verify the value was applied
        Assert.Equal("NewValue", person.FirstName);
    }

    [Fact]
    public async Task WhenConflictResolvedExternally_ThenSameTransactionRetrySucceeds()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();

        var person = new Person(context);
        person.FirstName = "Original";

        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);

        person.FirstName = "TransactionValue";

        // Simulate external change to cause conflict
        Task externalTask;
        var asyncFlowControl = ExecutionContext.SuppressFlow();
        try
        {
            externalTask = Task.Run(() => person.FirstName = "ExternalChange");
        }
        finally
        {
            asyncFlowControl.Undo();
        }
        await externalTask;

        // Act: First commit fails due to conflict
        await Assert.ThrowsAsync<SubjectTransactionConflictException>(
            () => tx.CommitAsync(CancellationToken.None).AsTask());

        // Pending changes are still intact after conflict failure
        Assert.Single(tx.GetPendingChanges());

        // Update to match current state and retry — old value is still "Original"
        // but the real field is now "ExternalChange", so we need to resolve the conflict.
        // Overwrite with a new value using Ignore to skip conflict check on retry.
        // Since FailOnConflict would detect the same conflict again, we verify retry works
        // by confirming the pending changes survive and a second commit attempt is allowed.
        person.FirstName = "ResolvedValue";

        // Simulate resolving the conflict by reverting the external change
        asyncFlowControl = ExecutionContext.SuppressFlow();
        try
        {
            externalTask = Task.Run(() => person.FirstName = "Original");
        }
        finally
        {
            asyncFlowControl.Undo();
        }
        await externalTask;

        // Retry commit — now current value matches captured OldValue ("Original")
        await tx.CommitAsync(CancellationToken.None);

        // Assert: The transaction's value was applied
        Assert.Equal("ResolvedValue", person.FirstName);
    }

    [Fact]
    public async Task WhenConflictPersists_ThenRetryFailsAgainWithConflict()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();

        var person = new Person(context);
        person.FirstName = "Original";

        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);

        person.FirstName = "TransactionValue";

        // External change that is never resolved.
        Task externalTask;
        var asyncFlowControl = ExecutionContext.SuppressFlow();
        try
        {
            externalTask = Task.Run(() => person.FirstName = "ExternalChange");
        }
        finally
        {
            asyncFlowControl.Undo();
        }
        await externalTask;

        // Act & Assert: first commit fails with a conflict and stays retryable.
        await Assert.ThrowsAsync<SubjectTransactionConflictException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        Assert.Single(transaction.GetPendingChanges());

        // The retry detects the same unresolved conflict and fails the same way; pending changes survive.
        await Assert.ThrowsAsync<SubjectTransactionConflictException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        Assert.Single(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenCommitFailsWithConflict_ThenPendingChangesRemainIntact()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();

        var person = new Person(context);
        person.FirstName = "Original";
        person.LastName = "Smith";

        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);

        person.FirstName = "NewFirst";
        person.LastName = "NewLast";

        // Simulate external change on FirstName only
        Task externalTask;
        var asyncFlowControl = ExecutionContext.SuppressFlow();
        try
        {
            externalTask = Task.Run(() => person.FirstName = "ExternalFirst");
        }
        finally
        {
            asyncFlowControl.Undo();
        }
        await externalTask;

        // Act: Commit fails due to conflict on FirstName
        var ex = await Assert.ThrowsAsync<SubjectTransactionConflictException>(
            () => tx.CommitAsync(CancellationToken.None).AsTask());

        // Assert: Both pending changes are still intact
        var pendingChanges = tx.GetPendingChanges();
        Assert.Equal(2, pendingChanges.Count);

        // Within the transaction, reads still return pending values (transaction is still active)
        Assert.Equal("NewFirst", person.FirstName);
        Assert.Equal("NewLast", person.LastName);
    }

}
