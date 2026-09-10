using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Single write interceptor that delivers property changes through three channels: an Rx observable
/// (see <see cref="InterceptorSubjectContextExtensions.GetPropertyChangeObservable"/>), a
/// high-performance pull queue (see <see cref="InterceptorSubjectContextExtensions.CreatePropertyChangeQueueSubscription"/>),
/// and inline and scheduled per-property subscriptions (see <see cref="PropertyChangeSubscriptionExtensions"/>).
/// </summary>
// RunsBefore orders the way IN, but everything this interceptor publishes happens after next(),
// on the unwind, where the outer interceptor runs LAST. So running before the lifecycle
// interceptor means dispatching AFTER attach/detach reconciliation: a consumer sees the new
// child attached, context-inherited and registered, and writes it makes to that child are
// themselves tracked. Without the edge the order falls to registration index, and dispatch
// announces a child that is still dormant.
[RunsAfter(typeof(SubjectTransactionInterceptor))]
[RunsBefore(typeof(LifecycleInterceptor))]
public sealed class PropertyChangeInterceptor : IObservable<SubjectPropertyChange>, IWriteInterceptor,
    ISingletonContextService<PropertyChangeInterceptor>
{
    private readonly Lock _modificationLock = new();

    // The single hot-path field. Null means neither channel has consumers (idle).
    private volatile DispatchState? _dispatchState;

    // Observable channel state, guarded by _modificationLock. Lazily created on first subscribe.
    private Subject<SubjectPropertyChange>? _subject;
    private ISubject<SubjectPropertyChange>? _syncSubject;
    private int _observableConsumerCount;

    // White-box test hook: true when neither channel has consumers. The idle write fast path
    // additionally requires the process-wide subscription count to be zero.
    internal bool IsIdle => _dispatchState is null;

    // Only surface the sync subject while at least one observable consumer is live; the field is
    // never cleared, so publishing it unconditionally would resurrect an observer-less subject and
    // permanently defeat the idle gate after transient observable use followed by queue churn.
    private ISubject<SubjectPropertyChange>? ActiveSyncSubject => _observableConsumerCount > 0 ? _syncSubject : null;

    private sealed class DispatchState
    {
        public required PropertyChangeQueueSubscription[] QueueSubscriptions { get; init; } // never null
        public required ISubject<SubjectPropertyChange>? SyncSubject { get; init; } // null = no observers
    }

    // Called under _modificationLock.
    private void UpdateDispatchState(PropertyChangeQueueSubscription[] queueSubscriptions, ISubject<SubjectPropertyChange>? syncSubject)
    {
        _dispatchState = queueSubscriptions.Length == 0 && syncSubject is null
            ? null
            : new DispatchState { QueueSubscriptions = queueSubscriptions, SyncSubject = syncSubject };
    }

    internal PropertyChangeQueueSubscription CreateQueueSubscription()
    {
        PropertyChangeQueueSubscription subscription;
        lock (_modificationLock)
        {
            subscription = new PropertyChangeQueueSubscription(this);
            var current = _dispatchState?.QueueSubscriptions ?? [];
            UpdateDispatchState(CopyOnWriteArray.Add(current, subscription), ActiveSyncSubject);
        }

        // Subscriber-side Dekker half: the state publish must be globally visible before the
        // caller's post-create property reads (a lock exit alone is only a release). Pairs with
        // the write path's post-commit fence and state re-read.
        Interlocked.MemoryBarrier();
        return subscription;
    }

    internal void RemoveQueueSubscription(PropertyChangeQueueSubscription subscription)
    {
        lock (_modificationLock)
        {
            var current = _dispatchState?.QueueSubscriptions ?? [];
            var index = Array.IndexOf(current, subscription);
            if (index < 0)
            {
                return;
            }

            UpdateDispatchState(CopyOnWriteArray.RemoveAt(current, index), ActiveSyncSubject);
        }
    }

    public IDisposable Subscribe(IObserver<SubjectPropertyChange> observer)
    {
        // Validate first: a later throw would leave the consumer count published with no handle to dispose.
        ArgumentNullException.ThrowIfNull(observer);

        IDisposable inner;
        lock (_modificationLock)
        {
            if (_subject is null)
            {
                _subject = new Subject<SubjectPropertyChange>();
                _syncSubject = Subject.Synchronize(_subject);
            }

            // Join BEFORE publishing state: the join's CAS-install precedes the volatile _dispatchState
            // publish in program order, so a writer that observes the state also observes the
            // join; joining after the publish reopens the missed-write window. Safe under the
            // lock only because this subject is never completed, errored, or disposed (Subscribe
            // is then a pure CAS); the Synchronize wrapper gates OnNext only.
            inner = _subject.Subscribe(observer);

            _observableConsumerCount++;
            if (_observableConsumerCount == 1)
            {
                // Mirrors RemoveObservableConsumer's 1 to 0; later consumers share the published subject.
                UpdateDispatchState(_dispatchState?.QueueSubscriptions ?? [], ActiveSyncSubject);
            }
        }

        // Subscriber-side Dekker half (see CreateQueueSubscription).
        Interlocked.MemoryBarrier();
        return new ObservableSubscription(this, inner);
    }

    private void RemoveObservableConsumer()
    {
        lock (_modificationLock)
        {
            if (_observableConsumerCount > 0 && --_observableConsumerCount == 0)
            {
                UpdateDispatchState(_dispatchState?.QueueSubscriptions ?? [], null);
            }
        }
    }

    private sealed class ObservableSubscription(PropertyChangeInterceptor interceptor, IDisposable inner) : IDisposable
    {
        private PropertyChangeInterceptor? _interceptor = interceptor;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _interceptor, null) is not { } owner)
            {
                return; // one-shot
            }

            inner.Dispose();
            owner.RemoveObservableConsumer();
        }
    }

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        // Pre-commit gate only selects the dispatch path; listener RESOLUTION is post-commit
        // (see ResolveListeners), so an install racing this write is never missed.
        if (_dispatchState is null && PropertyChangeSubscriptions.ReadSubscriptionCount() == 0)
        {
            next(ref context);
            DispatchLateConsumers(ref context);
            return;
        }

        next(ref context);

        if (!context.IsWritten)
        {
            return; // vetoed by an inner interceptor: nothing was stored, publish nothing
        }

        // Resolved during unwind, once per write: the singleton contract guarantees one instance
        // of this interceptor per chain.
        var listeners = ResolveListeners(ref context);

        // Re-read post-commit so a subscription installed mid-write is delivered; a full fence
        // already ran on this thread (inside ResolveListeners).
        var state = _dispatchState;
        var subscriptions = state?.QueueSubscriptions ?? [];
        var syncSubject = state?.SyncSubject;

        if (syncSubject is null && subscriptions.Length == 0 && listeners is null)
        {
            return;
        }

        var change = SubjectPropertyChange.Create(
            context.Property,
            context.Origin,
            context.WriteTimestampForPublishing,
            SubjectChangeContext.Current.ReceivedTimestamp,
            context.CurrentValue,
            context.GetFinalValue(),
            context.Revision);

        for (var i = 0; i < subscriptions.Length; i++)
        {
            subscriptions[i].Enqueue(in change);
        }

        syncSubject?.OnNext(change);

        if (listeners is not null)
        {
            PropertyChangeSubscription.Dispatch(listeners, in change);
        }
    }

    /// <summary>
    /// Idle-entry path: the gate saw no consumers, but a listener or channel subscription installed
    /// while the write was in flight is still delivered. Non-inlined to keep the fast path small.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DispatchLateConsumers<TProperty>(ref PropertyWriteContext<TProperty> context)
    {
        if (!context.IsWritten)
        {
            return; // vetoed: nothing was stored
        }

        var listeners = ResolveListeners(ref context);

        // Channel state re-read behind the fence (ResolveListeners fenced on this thread): pairs
        // with the fence after the channel install.
        var state = _dispatchState;
        if (state is null && listeners is null)
        {
            return;
        }

        var finalValue = context.GetFinalValue();
        var change = SubjectPropertyChange.Create(
            context.Property,
            context.Origin,
            context.WriteTimestampForPublishing,
            SubjectChangeContext.Current.ReceivedTimestamp,
            context.CurrentValue,
            finalValue,
            context.Revision);

        if (state is not null)
        {
            var subscriptions = state.QueueSubscriptions;
            for (var i = 0; i < subscriptions.Length; i++)
            {
                subscriptions[i].Enqueue(in change);
            }

            state.SyncSubject?.OnNext(change);
        }

        if (listeners is not null)
        {
            PropertyChangeSubscription.Dispatch(listeners, in change);
        }
    }

    // Post-commit listener resolution, shared by the two mutually exclusive entry paths and
    // therefore performed once per write. A listener installed after this resolution is not owed
    // the write (it committed before the install; the post-subscribe read observes it). The fence
    // is what the channel state re-reads rely on, and the count read must stay BEHIND it (Dekker
    // read side pairing with subscription install).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PropertyChangeSubscription[]? ResolveListeners<TProperty>(ref PropertyWriteContext<TProperty> context)
    {
        Interlocked.MemoryBarrier();
        if (PropertyChangeSubscriptions.ReadSubscriptionCount() == 0)
        {
            return null;
        }

        return TryGetListeners(context.Property);
    }

    private static PropertyChangeSubscription[]? TryGetListeners(PropertyReference property)
    {
        return property.Subject.Data.TryGetValue((property.Name, PropertyChangeSubscription.ListenersKey), out var value)
            ? value as PropertyChangeSubscription[]
            : null;
    }
}
