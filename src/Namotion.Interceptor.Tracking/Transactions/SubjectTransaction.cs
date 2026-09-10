using System.Buffers;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Performance;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Represents a transaction that captures property changes and commits them atomically.
/// Changes are buffered until <see cref="CommitAsync"/> is called.
/// </summary>
public sealed class SubjectTransaction : IDisposable
{
    private static readonly AsyncLocal<SubjectTransaction?> CurrentTransaction = new();
    private static int _activeTransactionCount;

    [ThreadStatic]
    private static SubjectTransaction? _commitModelAccessTransaction;

    /// <summary>
    /// Gets a value indicating whether any transaction is currently active across all contexts.
    /// This is a fast-path check using a volatile read; it may briefly return true after
    /// all transactions have completed due to memory visibility delays. This is safe because
    /// a false positive only results in an unnecessary AsyncLocal read.
    /// </summary>
    internal static bool HasActiveTransaction => Volatile.Read(ref _activeTransactionCount) > 0;

    private readonly TransactionFailureHandling _failureHandling;
    private readonly TransactionRequirement _requirement;
    private readonly TimeSpan _commitTimeout;
    private readonly IDisposable? _lockReleaser; // null for Optimistic until commit

    /// <summary>
    /// Last write wins if the same property is written multiple times.
    /// Preserves insertion order so that commit replays changes in the order they were written.
    /// Access must be synchronized via <see cref="_pendingChangesLock"/>.
    /// </summary>
    private static readonly ObjectPool<OrderedDictionary<PropertyReference, SubjectPropertyChange>> PendingChangesPool
        = new(() => new OrderedDictionary<PropertyReference, SubjectPropertyChange>(PropertyReference.Comparer));

    private OrderedDictionary<PropertyReference, SubjectPropertyChange>? _pendingChanges = PendingChangesPool.Rent();
    private readonly Lock _pendingChangesLock = new();

    private volatile bool _isCommitting;
    private volatile bool _isCommitted;
    private int _commitStarted;
    private int _isDisposed;

    // Handoff from Dispose to EndCommit: when Dispose runs mid-commit, EndCommit releases the exclusive lock
    // once the commit finishes. Accessed only under _pendingChangesLock (no volatile needed; never read lock-free).
    private bool _disposeRequestedDuringCommit;

    /// <summary>
    /// Gets the current transaction in this execution context, or null if none is active.
    /// </summary>
    public static SubjectTransaction? Current => CurrentTransaction.Value;

    /// <summary>
    /// Enables runtime validation that a custom <see cref="ITransactionWriter"/> fulfills the in-place
    /// marking contract of <see cref="ITransactionWriter.WriteToSourcesAsync"/>: the commit then fails
    /// terminally if the writer moved or replaced a snapshot slot. Intended for developing custom writers
    /// (costs one array allocation and sweep per commit); process-wide, set once at startup.
    /// </summary>
    public static bool ValidateWriterContract { get; set; }

    /// <summary>
    /// Sets the current transaction in this execution context.
    /// This is needed for async patterns where AsyncLocal must be set in the caller's context.
    /// </summary>
    internal static void SetCurrent(SubjectTransaction? transaction)
    {
        CurrentTransaction.Value = transaction;
    }

    /// <summary>
    /// Gets a value indicating whether the transaction is currently committing changes.
    /// </summary>
    internal bool IsCommitting => _isCommitting;

    /// <summary>
    /// Gets a value indicating whether the transaction has reached a terminal committed state.
    /// </summary>
    internal bool IsCommitted => _isCommitted;

    /// <summary>
    /// Gets a value indicating whether the transaction has been disposed.
    /// </summary>
    internal bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

    /// <summary>
    /// Gets a value indicating whether this thread is synchronously replaying this transaction's model work.
    /// </summary>
    internal bool IsCommitModelAccessAuthorized => ReferenceEquals(_commitModelAccessTransaction, this);

    /// <summary>
    /// Gets the interceptor this transaction is bound to (for cross-context validation).
    /// </summary>
    internal SubjectTransactionInterceptor Interceptor { get; }

    /// <summary>
    /// Gets a snapshot of the pending changes as a read-only list, in insertion order.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> GetPendingChanges()
    {
        lock (_pendingChangesLock)
        {
            return _pendingChanges?.Values.ToList() ?? [];
        }
    }

    /// <summary>
    /// Tries to read a pending value for the given property.
    /// Returns false if the transaction is committing or no pending value exists.
    /// </summary>
    internal bool TryGetPendingValue<TProperty>(PropertyReference property, out TProperty value)
    {
        lock (_pendingChangesLock)
        {
            if (Volatile.Read(ref _isDisposed) != 0 || _isCommitted || _pendingChanges is null)
            {
                value = default!;
                return false;
            }

            ThrowIfCommittingConcurrently();

            if (_pendingChanges.TryGetValue(property, out var change))
            {
                value = change.GetNewValue<TProperty>();
                return true;
            }

            value = default!;
            return false;
        }
    }

    /// <summary>
    /// Atomically captures a property change into the pending dictionary.
    /// First write preserves <paramref name="currentValue"/> as old value for conflict detection;
    /// subsequent writes preserve the original old value (last write wins). Returns false when
    /// disposal wins the race, allowing the caller to continue the normal write chain.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if a concurrent commit is in progress (TOCTOU race).</exception>
    internal bool TryCaptureChange<TProperty>(
        PropertyReference property,
        ChangeOrigin origin,
        DateTimeOffset changedTimestamp,
        DateTimeOffset? receivedTimestamp,
        TProperty currentValue,
        TProperty newValue)
    {
        lock (_pendingChangesLock)
        {
            if (Volatile.Read(ref _isDisposed) != 0 || _isCommitted || _pendingChanges is null)
            {
                return false;
            }

            ThrowIfCommittingConcurrently();

            var pendingChanges = _pendingChanges!;
            var isFirstWrite = !pendingChanges.TryGetValue(property, out var existingChange);
            pendingChanges[property] = SubjectPropertyChange.Create(
                property,
                origin: origin,
                changedTimestamp: changedTimestamp,
                receivedTimestamp: receivedTimestamp,
                isFirstWrite ? currentValue : existingChange.GetOldValue<TProperty>(),
                newValue);
            return true;
        }
    }

    /// <summary>
    /// Rechecks an interceptor's lock-free state observation. Returns true when the transaction became
    /// inactive, throws while a live commit still owns the model, and otherwise returns false so the caller
    /// can continue the live non-committing path.
    /// </summary>
    internal bool IsInactiveUnderLockOrThrowIfCommitting()
    {
        lock (_pendingChangesLock)
        {
            if (Volatile.Read(ref _isDisposed) != 0 || _isCommitted || _pendingChanges is null)
            {
                return true;
            }

            ThrowIfCommittingConcurrently();
            return false;
        }
    }

    private void ThrowIfCommittingConcurrently()
    {
        if (_isCommitting)
        {
            throw new InvalidOperationException(
                "Cannot access transactional property while commit is in progress. " +
                "Source writes and transaction writer callbacks run on the committing flow: build the " +
                "payload from the supplied changes instead of reading or writing subject properties. " +
                "This is also reported when the transaction is used from another thread.");
        }
    }

    /// <summary>
    /// Gets the context this transaction is bound to.
    /// </summary>
    public IInterceptorSubjectContext Context { get; }

    /// <summary>
    /// Gets the conflict behavior for this transaction.
    /// </summary>
    public TransactionConflictBehavior ConflictBehavior { get; }

    /// <summary>
    /// Gets the locking mode for this transaction.
    /// </summary>
    public TransactionLocking Locking { get; }

    private SubjectTransaction(
        IInterceptorSubjectContext context,
        SubjectTransactionInterceptor interceptor,
        TransactionFailureHandling failureHandling,
        TransactionLocking locking,
        TransactionRequirement requirement,
        TransactionConflictBehavior conflictBehavior,
        TimeSpan commitTimeout,
        IDisposable? lockReleaser)
    {
        Context = context;
        Interceptor = interceptor;
        _failureHandling = failureHandling;
        Locking = locking;
        _requirement = requirement;
        ConflictBehavior = conflictBehavior;
        _commitTimeout = commitTimeout;
        _lockReleaser = lockReleaser;

        Interlocked.Increment(ref _activeTransactionCount);
    }

    /// <summary>
    /// Begins a new transaction bound to the specified context.
    /// For Exclusive locking, waits if another transaction is active on this context.
    /// For Optimistic locking, returns immediately and acquires lock only during commit.
    /// </summary>
    /// <param name="context">The context to bind the transaction to.</param>
    /// <param name="failureHandling">The failure handling mode controlling what happens when writes fail.</param>
    /// <param name="locking">The locking mode controlling transaction synchronization.</param>
    /// <param name="requirement">The transaction requirement for validation.</param>
    /// <param name="conflictBehavior">The conflict detection behavior.</param>
    /// <param name="commitTimeout">Timeout for commit operations. User cancellation is ignored during commit and only timeout-based cancellation is used. Use <see cref="Timeout.InfiniteTimeSpan"/> to disable timeout.</param>
    /// <param name="cancellationToken">The cancellation token (used before commit starts, ignored during commit).</param>
    /// <returns>A new SubjectTransaction instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when transactions are not enabled or when nested transaction is attempted.</exception>
    internal static async ValueTask<SubjectTransaction> BeginTransactionAsync(
        IInterceptorSubjectContext context,
        TransactionFailureHandling failureHandling,
        TransactionLocking locking,
        TransactionRequirement requirement,
        TransactionConflictBehavior conflictBehavior,
        TimeSpan commitTimeout,
        CancellationToken cancellationToken)
    {
        // Check before acquiring the context lock so an attempted nested exclusive transaction cannot deadlock.
        if (CurrentTransaction.Value is { IsDisposed: false })
        {
            throw new InvalidOperationException("Nested transactions are not supported.");
        }

        var interceptor = context.TryGetService<SubjectTransactionInterceptor>()
            ?? throw new InvalidOperationException(
                "Transactions are not enabled. Call WithTransactions() when creating the context.");

        IDisposable? transactionLock = null;

        if (locking == TransactionLocking.Exclusive)
        {
            transactionLock = await interceptor.AcquireTransactionLockAsync(cancellationToken).ConfigureAwait(false);
        }

        // An AsyncLocal assignment here would not flow back through this async method's await;
        // the caller assigns the transaction in its own execution context.
        return new SubjectTransaction(
            context,
            interceptor,
            failureHandling,
            locking,
            requirement,
            conflictBehavior,
            commitTimeout,
            transactionLock);
    }

    /// <summary>
    /// Commits all pending changes. If external write handlers are configured on subjects' contexts,
    /// changes are written to external sources first, then applied to the local model.
    /// The behavior on partial failure depends on the <see cref="_failureHandling"/> specified at transaction creation.
    /// </summary>
    /// <remarks>
    /// For Optimistic locking, the lock is acquired at the start of commit and released after completion.
    /// Conflict detection compares captured OldValue with current value at commit time.
    /// This catches changes from other transactions and external sources that occurred
    /// after the transaction started.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="ObjectDisposedException">Thrown when the transaction has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when commit is called from a different async flow than the one the transaction is active in,
    /// when the transaction was already committed, or when another commit is already in progress.
    /// </exception>
    /// <exception cref="SubjectTransactionException">
    /// Thrown when one or more changes failed to commit. A registered <see cref="ITransactionWriter"/>
    /// that throws instead of reporting failures makes the transaction terminal (it must be disposed,
    /// not retried) and may leave its sources un-reverted.
    /// </exception>
    public ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        ValidateCanCommit();

        lock (_pendingChangesLock)
        {
            if (_pendingChanges is null || _pendingChanges.Count == 0)
            {
                _isCommitted = true;
                return default;
            }
        }

        var writer = Context.TryGetService<ITransactionWriter>();
        if (writer is null)
        {
            // No source writer: the entire commit is local. For the default (Exclusive) locking
            // mode the lock is already held, so the whole flow runs synchronously without a
            // CancellationTokenSource or an async state machine. Optimistic locking needs an async lock
            // acquisition, so only that case falls back to the async wrapper.
            var lockTask = AcquireOptimisticLockIfNeededAsync(cancellationToken);
            if (lockTask.IsCompletedSuccessfully)
            {
                CommitWithoutWriter(lockTask.Result);
                return default;
            }

            return CommitLocalAfterLockAsync(lockTask);
        }

        return CommitWithWriterAsync(writer, cancellationToken);
    }

    private async ValueTask CommitLocalAfterLockAsync(ValueTask<IDisposable?> lockTask)
    {
        IDisposable? commitLock;
        try
        {
            commitLock = await lockTask.ConfigureAwait(false);
        }
        catch
        {
            // A failed optimistic lock must not wedge the transaction: reset so a retry is possible.
            Volatile.Write(ref _commitStarted, 0);
            throw;
        }
        CommitWithoutWriter(commitLock);
    }

    /// <summary>
    /// Fully synchronous local commit when no <see cref="ITransactionWriter"/> is registered.
    /// </summary>
    private void CommitWithoutWriter(IDisposable? commitLock)
    {
        SubjectPropertyChange[]? rentedArray = null;
        try
        {
            var (rented, changes) = StartCommitAndSnapshotChanges();
            rentedArray = rented;

            SubjectTransactionException? failure = null;
            using (EnterCommitModelAccess())
            {
                ThrowIfConflictsDetected(changes.Span);

                var (applied, applyFailed, applyErrors) = SubjectPropertyChangeOperations.ApplyLocalChanges(changes.Span, exclude: null);

                if (applyFailed.Count > 0)
                {
                    if (_failureHandling == TransactionFailureHandling.Rollback)
                    {
                        var (revertFailed, revertErrors) = SubjectPropertyChangeOperations.RevertLocalChanges(applied);
                        failure = CreateFailureException([], SubjectPropertyChangeOperations.Concat(applyFailed, revertFailed), SubjectPropertyChangeOperations.Concat(applyErrors, revertErrors));
                    }
                    else
                    {
                        failure = CreateFailureException(applied, applyFailed, applyErrors);
                    }
                }
            }

            FinishCommit();

            if (failure is not null)
            {
                throw failure;
            }
        }
        finally
        {
            EndCommit(rentedArray, commitLock);
        }
    }

    /// <summary>
    /// Commit when an <see cref="ITransactionWriter"/> is registered.
    /// </summary>
    private async ValueTask CommitWithWriterAsync(ITransactionWriter writer, CancellationToken cancellationToken)
    {
        IDisposable? commitLock = null;
        SubjectPropertyChange[]? rentedArray = null;
        try
        {
            commitLock = await AcquireOptimisticLockIfNeededAsync(cancellationToken).ConfigureAwait(false);
            var (rented, changes) = StartCommitAndSnapshotChanges();
            rentedArray = rented;

            using (EnterCommitModelAccess())
            {
                ThrowIfConflictsDetected(changes.Span);
            }

            using var timeoutCts = CreateCommitTimeoutCts();
            var commitToken = timeoutCts?.Token ?? CancellationToken.None;

            var failure = await ReconcileWithWriterAsync(writer, changes, commitToken).ConfigureAwait(false);

            FinishCommit();

            if (failure is not null)
            {
                throw failure;
            }
        }
        finally
        {
            EndCommit(rentedArray, commitLock);
        }
    }

    /// <summary>
    /// Implements the failure/rollback matrix for the writer path. Returns null on full success.
    /// </summary>
    private async ValueTask<SubjectTransactionException?> ReconcileWithWriterAsync(
        ITransactionWriter writer,
        Memory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        // The writer marks accepted snapshot slots with the confirming source so the local apply and
        // revert notifications are echo-suppressed by the outbound connector queue (#343).
        var capturedProperties = ValidateWriterContract ? CaptureSnapshotProperties(changes.Span) : null;

        // A throwing writer returns neither the written set nor the revert state, so sources cannot be
        // reverted: report a full failure, which the caller makes terminal. A contract violation is
        // terminal too (source writes already happened, a retry would repeat them). Only a commit-timeout
        // OperationCanceledException propagates and stays retryable; the built-in writer throws it
        // only from its revert phase (write-phase cancellation is reported as a failure).
        SourceWriteResult writeResult;
        try
        {
            writeResult = await writer
                .WriteToSourcesAsync(changes, _requirement, cancellationToken).ConfigureAwait(false);

            VerifySnapshotIntegrity(capturedProperties, changes.Span);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new SubjectTransactionException(
                "The transaction writer threw an exception or violated its contract during commit. Sources " +
                "may be in an undefined, un-reverted state; the transaction is terminal and must be disposed, " +
                "not retried.",
                appliedChanges: [],
                failedChanges: changes.ToArray(),
                errors: [exception]);
        }

        var (written, failedSource, sourceErrors, revertState) = writeResult;

        // Rollback with any source-write failure or reported error: nothing is applied to the local model;
        // revert what reached a source and report. Local (no-source) changes did not commit either and are
        // reported as failed. An error without failed changes (custom writers only) must not be swallowed.
        if (_failureHandling == TransactionFailureHandling.Rollback && (failedSource.Count > 0 || sourceErrors.Count > 0))
        {
            var revert = await RevertSourceWritesSafelyAsync(writer, written, revertState, cancellationToken).ConfigureAwait(false);
            var notApplied = SubjectPropertyChangeOperations.ExcludeByProperty(changes.Span, failedSource, written);
            return CreateFailureException(
                [],
                SubjectPropertyChangeOperations.Concat(failedSource, notApplied, revert.Failed),
                SubjectPropertyChangeOperations.Concat(sourceErrors, revert.Errors));
        }

        // Apply the whole snapshot except the source-write failures in a single pass. With no source
        // failure, this is the entire snapshot, and ApplyAllChanges returns an empty Successful set.
        var exclude = failedSource.Count == 0 ? null : failedSource;
        IReadOnlyList<SubjectPropertyChange> applied;
        IReadOnlyList<SubjectPropertyChange> applyFailed;
        IReadOnlyList<Exception> applyErrors;
        IReadOnlyList<SubjectPropertyChange> revertFailed = [];
        IReadOnlyList<Exception> revertErrors = [];
        using (EnterCommitModelAccess())
        {
            (applied, applyFailed, applyErrors) = SubjectPropertyChangeOperations.ApplyLocalChanges(changes.Span, exclude);
            if (applyFailed.Count > 0 && _failureHandling == TransactionFailureHandling.Rollback)
            {
                (revertFailed, revertErrors) = SubjectPropertyChangeOperations.RevertLocalChanges(applied);
            }
        }

        if (applyFailed.Count > 0)
        {
            if (_failureHandling == TransactionFailureHandling.Rollback)
            {
                // All-or-nothing: revert local applies, then the source writes.
                var sourceRevert = await RevertSourceWritesSafelyAsync(writer, written, revertState, cancellationToken).ConfigureAwait(false);
                // The no-source local changes were applied then reverted; under all-or-nothing they did not
                // commit, so report them as failed too (consistent with the source-write-failure branch and
                // the documented Rollback contract). Exclude source-bound changes (in 'written'; failedSource
                // is empty on this branch) and anything already reported as an apply/revert failure.
                var rolledBackLocals = SubjectPropertyChangeOperations.ExcludeByProperty(
                    changes.Span, written, SubjectPropertyChangeOperations.Concat(applyFailed, revertFailed));
                return CreateFailureException(
                    [],
                    SubjectPropertyChangeOperations.Concat(failedSource, applyFailed, rolledBackLocals, revertFailed, sourceRevert.Failed),
                    SubjectPropertyChangeOperations.Concat(sourceErrors, applyErrors, revertErrors, sourceRevert.Errors));
            }

            // BestEffort: keep source == model for failed-apply properties by reverting only the
            // source writes whose property failed to apply (matched by Property equality).
            var toRevert = SubjectPropertyChangeOperations.IntersectByProperty(applyFailed, written);
            var bestEffortRevert = await RevertSourceWritesSafelyAsync(writer, toRevert, revertState, cancellationToken).ConfigureAwait(false);
            return CreateFailureException(
                applied,
                SubjectPropertyChangeOperations.Concat(failedSource, applyFailed, bestEffortRevert.Failed),
                SubjectPropertyChangeOperations.Concat(sourceErrors, applyErrors, bestEffortRevert.Errors));
        }

        // Apply succeeded. Report any remaining source failure or writer error (BestEffort; Rollback
        // returned above). On the no-exclude path ApplyLocalChanges returned an empty Successful set,
        // so materialize the snapshot as the applied set.
        if (failedSource.Count > 0 || sourceErrors.Count > 0)
        {
            var appliedChanges = exclude is null ? changes.ToArray() : applied;
            return CreateFailureException(appliedChanges, failedSource, sourceErrors);
        }

        return null;
    }

    private static PropertyReference[] CaptureSnapshotProperties(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        var properties = new PropertyReference[changes.Length];
        for (var i = 0; i < changes.Length; i++)
        {
            properties[i] = changes[i].Property;
        }
        return properties;
    }

    /// <summary>
    /// Verifies no snapshot slot was moved or replaced by the writer. Checks slot identity only; tampered
    /// values or timestamps are not detected. No-op when validation is disabled (null capture).
    /// </summary>
    private static void VerifySnapshotIntegrity(PropertyReference[]? capturedProperties, ReadOnlySpan<SubjectPropertyChange> changes)
    {
        if (capturedProperties is null)
        {
            return;
        }

        for (var i = 0; i < changes.Length; i++)
        {
            if (!PropertyReference.Comparer.Equals(changes[i].Property, capturedProperties[i]))
            {
                throw new InvalidOperationException(
                    "The transaction writer violated its contract: it changed the property at snapshot slot " +
                    $"{i} instead of only marking the accepting source. A writer must change only the Source " +
                    "of an accepted slot and must never move a change to a different slot.");
            }
        }
    }

    /// <summary>
    /// Converts a contract-violating throw from <see cref="ITransactionWriter.RevertSourceWritesAsync"/>
    /// into "every requested revert failed" so the commit stays on the reported-failure path and becomes
    /// terminal; a propagating throw would leave the transaction retryable, and a retry would re-push
    /// to sources that already accepted writes. Only the commit timeout propagates.
    /// </summary>
    private static async ValueTask<SourceRevertResult> RevertSourceWritesSafelyAsync(
        ITransactionWriter writer,
        IReadOnlyList<SubjectPropertyChange> written,
        object? revertState,
        CancellationToken cancellationToken)
    {
        try
        {
            return await writer.RevertSourceWritesAsync(written, revertState, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new SourceRevertResult(written, [exception]);
        }
    }

    /// <summary>
    /// Clears pending changes and marks the transaction committed BEFORE any exception is thrown so
    /// later property reads do not return stale captured values via TryGetPendingValue.
    /// </summary>
    private void FinishCommit()
    {
        lock (_pendingChangesLock)
        {
            _pendingChanges?.Clear();
            _isCommitted = true;
        }
    }

    /// <summary>
    /// Common cleanup for every commit path (success or failure): resets commit state for retry on
    /// failure, returns the pooled snapshot array, and releases the optimistic lock.
    /// </summary>
    private void EndCommit(SubjectPropertyChange[]? rentedArray, IDisposable? commitLock)
    {
        bool releaseDeferredLock;
        OrderedDictionary<PropertyReference, SubjectPropertyChange>? pendingChangesToReturn = null;
        lock (_pendingChangesLock)
        {
            // Clear under the lock so Dispose observes the in-flight commit consistently. A disposing caller
            // defers exclusive-lock release until commit completion so another transaction cannot interleave
            // with the apply pass. If it also owns the pending dictionary, detach it here and return it after
            // the lock so ownership transfers exactly once.
            _isCommitting = false;
            releaseDeferredLock = _disposeRequestedDuringCommit;
            if (releaseDeferredLock)
            {
                pendingChangesToReturn = _pendingChanges;
                _pendingChanges = null;
            }
        }

        if (pendingChangesToReturn is not null)
        {
            PendingChangesPool.Return(pendingChangesToReturn);
        }

        if (rentedArray != null)
        {
            // clearArray: true because SubjectPropertyChange holds object references (subject, source,
            // boxed value holders); leaving them in the pooled buffer would keep those graphs alive
            // until the slot is next overwritten.
            ArrayPool<SubjectPropertyChange>.Shared.Return(rentedArray, clearArray: true);
        }

        // Release the optimistic lock before allowing a retry to start.
        commitLock?.Dispose();

        if (releaseDeferredLock)
        {
            _lockReleaser?.Dispose();
        }

        if (!_isCommitted)
        {
            // Only failures that escape before FinishCommit are retryable; later failures are terminal.
            Volatile.Write(ref _commitStarted, 0);
        }
    }

    private void ValidateCanCommit()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
            throw new ObjectDisposedException(nameof(SubjectTransaction));

        if (!ReferenceEquals(CurrentTransaction.Value, this))
            throw new InvalidOperationException(
                "Transaction is being committed from a different async flow than the one it is active in. " +
                "Begin, use, commit, and dispose a transaction within the same async flow.");

        if (_isCommitted)
            throw new InvalidOperationException("Transaction has already been committed.");

        if (Interlocked.CompareExchange(ref _commitStarted, 1, 0) != 0)
            throw new InvalidOperationException("CommitAsync is already in progress.");
    }

    private ValueTask<IDisposable?> AcquireOptimisticLockIfNeededAsync(CancellationToken cancellationToken)
    {
        if (Locking != TransactionLocking.Optimistic)
        {
            return default;
        }
        return AcquireOptimisticLockSlowAsync(cancellationToken);
    }

    private async ValueTask<IDisposable?> AcquireOptimisticLockSlowAsync(CancellationToken cancellationToken)
    {
        return await Interceptor.AcquireTransactionLockAsync(cancellationToken).ConfigureAwait(false);
    }

    private (SubjectPropertyChange[] RentedArray, Memory<SubjectPropertyChange> Changes) StartCommitAndSnapshotChanges()
    {
        lock (_pendingChangesLock)
        {
            // Rent before setting _isCommitting so an allocation failure leaves the transaction reusable.
            var pendingChanges = _pendingChanges;
            var changeCount = pendingChanges?.Count ?? 0;
            var rentedArray = ArrayPool<SubjectPropertyChange>.Shared.Rent(changeCount);

            // Set this while holding the lock so no write can be captured between snapshot and commit.
            _isCommitting = true;

            var index = 0;
            if (pendingChanges is not null)
            {
                foreach (var change in pendingChanges.Values)
                {
                    rentedArray[index++] = change;
                }
            }

            return (rentedArray, rentedArray.AsMemory(0, changeCount));
        }
    }

    private void ThrowIfConflictsDetected(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        if (ConflictBehavior == TransactionConflictBehavior.FailOnConflict)
        {
            var conflictingProperties = SubjectPropertyChangeOperations.DetectChangeConflicts(changes);
            if (conflictingProperties.Count > 0)
            {
                throw new SubjectTransactionConflictException(conflictingProperties);
            }
        }
    }

    private CancellationTokenSource? CreateCommitTimeoutCts()
    {
        return _commitTimeout == Timeout.InfiniteTimeSpan
            ? null
            : new CancellationTokenSource(_commitTimeout);
    }

    private CommitModelAccessScope EnterCommitModelAccess() => new(this);

    /// <summary>
    /// Grants model access only to synchronous commit-owned work on this thread. The previous identity is
    /// restored for nested/reentrant use. Callers must dispose the scope before awaiting external code.
    /// </summary>
    private readonly struct CommitModelAccessScope : IDisposable
    {
        private readonly SubjectTransaction? _previousTransaction;

        public CommitModelAccessScope(SubjectTransaction transaction)
        {
            _previousTransaction = _commitModelAccessTransaction;
            _commitModelAccessTransaction = transaction;
        }

        public void Dispose()
        {
            _commitModelAccessTransaction = _previousTransaction;
        }
    }

    private SubjectTransactionException CreateFailureException(
        IReadOnlyList<SubjectPropertyChange> successful,
        IReadOnlyList<SubjectPropertyChange> failed,
        IReadOnlyList<Exception> errors)
    {
        var message = _failureHandling switch
        {
            TransactionFailureHandling.BestEffort => "One or more changes failed. Successful changes have been applied.",
            TransactionFailureHandling.Rollback => "One or more changes failed. Rollback was attempted. No changes have been applied to the local model.",
            _ => "One or more changes failed."
        };

        return new SubjectTransactionException(message, successful, failed, errors);
    }

    /// <summary>
    /// Disposes the transaction, discarding any uncommitted changes.
    /// </summary>
    public void Dispose()
    {
        // Publish disposal before taking the lock so a waiting capture observes it and continues normally.
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 0)
        {
            Interlocked.Decrement(ref _activeTransactionCount);

            bool committing;
            OrderedDictionary<PropertyReference, SubjectPropertyChange>? pendingChangesToReturn = null;
            lock (_pendingChangesLock)
            {
                _pendingChanges?.Clear();
                committing = _isCommitting;
                if (committing)
                {
                    _disposeRequestedDuringCommit = true;
                }
                else
                {
                    // Detach while locked; return the pooled dictionary after releasing the lock.
                    pendingChangesToReturn = _pendingChanges;
                    _pendingChanges = null;
                }
            }

            // Clear the slot only when it holds this transaction so a cross-flow Dispose cannot
            // clobber an unrelated transaction.
            if (CurrentTransaction.Value == this)
            {
                CurrentTransaction.Value = null;
            }

            if (!committing)
            {
                if (pendingChangesToReturn is not null)
                {
                    PendingChangesPool.Return(pendingChangesToReturn);
                }
                _lockReleaser?.Dispose(); // May be null for Optimistic transactions
            }
        }
    }
}
