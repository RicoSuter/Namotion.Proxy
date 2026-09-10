using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Interceptor that captures property changes during transactions.
/// Must run before all downstream interceptors to suppress side effects during capture.
/// Also manages the per-context transaction lock for serialized transactions.
/// </summary>
[RunsBefore(typeof(DerivedPropertyChangeHandler))]
public sealed class SubjectTransactionInterceptor : IReadInterceptor, IWriteInterceptor
{
    private readonly SemaphoreSlim _exclusiveTransactionLock = new(1, 1);

    /// <summary>
    /// Acquires the transaction lock for this context.
    /// Used by serialized transactions to ensure only one transaction executes at a time.
    /// </summary>
    internal async ValueTask<IDisposable> AcquireTransactionLockAsync(CancellationToken cancellationToken)
    {
        await _exclusiveTransactionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new LockReleaser(_exclusiveTransactionLock);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
    {
        if (!SubjectTransaction.HasActiveTransaction)
        {
            return next(ref context);
        }

        var transaction = SubjectTransaction.Current;
        if (transaction is null || transaction.IsDisposed || transaction.IsCommitted)
        {
            return next(ref context);
        }

        if (transaction.IsCommitting)
        {
            if (transaction.IsCommitModelAccessAuthorized)
            {
                return next(ref context);
            }

            if (transaction.IsInactiveUnderLockOrThrowIfCommitting())
            {
                return next(ref context);
            }
        }

        // Recorder-active reads are the landed-model view. This access linearizes at the non-committing
        // observation above; do not hold the transaction lock while a user getter executes.
        if (DerivedPropertyChangeHandler.IsRecordingDerivedProperty)
        {
            return next(ref context);
        }

        if (transaction.TryGetPendingValue<TProperty>(context.Property, out var pendingValue))
        {
            return pendingValue;
        }

        return next(ref context);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        if (!SubjectTransaction.HasActiveTransaction)
        {
            next(ref context);
            return;
        }

        var transaction = SubjectTransaction.Current;
        if (transaction is null || transaction.IsDisposed || transaction.IsCommitted)
        {
            next(ref context);
            return;
        }

        if (transaction.IsCommitting)
        {
            if (transaction.IsCommitModelAccessAuthorized)
            {
                next(ref context);
                return;
            }

            if (transaction.IsInactiveUnderLockOrThrowIfCommitting())
            {
                next(ref context);
                return;
            }
        }

        // Settable-derived writes are immediate and outside the transaction. This access linearizes at the
        // non-committing observation above; do not hold the transaction lock while a user setter executes.
        if (context.Property.Metadata.IsDerived)
        {
            next(ref context);
            return;
        }

        var subjectInterceptors = context.Property.Subject.Context
            .GetServices<SubjectTransactionInterceptor>();
        var isBoundToThisContext = subjectInterceptors.Length == 1
            ? ReferenceEquals(subjectInterceptors[0], transaction.Interceptor)
            : ContainsByReference(subjectInterceptors, transaction.Interceptor);

        if (!isBoundToThisContext)
        {
            throw new InvalidOperationException(
                $"Cannot modify property '{context.Property.Metadata.Name}': Transaction is bound to a different context.");
        }

        // Origin comparison can run user equality code, so resolve it before taking the transaction lock.
        var resolvedOrigin = context.GetFinalOrigin();
        var changedTimestamp = context.WriteTimestampForPublishing;
        var receivedTimestamp = SubjectChangeContext.Current.ReceivedTimestamp;

        if (transaction.TryCaptureChange(
                context.Property,
                resolvedOrigin,
                changedTimestamp,
                receivedTimestamp,
                context.CurrentValue,
                context.NewValue))
        {
            return;
        }

        next(ref context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ContainsByReference(
        ImmutableArray<SubjectTransactionInterceptor> interceptors,
        SubjectTransactionInterceptor target)
    {
        for (var index = 0; index < interceptors.Length; index++)
        {
            if (ReferenceEquals(interceptors[index], target))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class LockReleaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
