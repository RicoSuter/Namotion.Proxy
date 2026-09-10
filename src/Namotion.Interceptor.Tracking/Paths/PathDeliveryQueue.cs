using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>
/// Ordered exclusive-drain delivery for one <see cref="SubjectPathSubscription{TValue}"/>. At most one
/// thread is the drainer (<c>_draining</c>); every other computed event enqueues into <c>_pending</c> and
/// the drainer delivers them FIFO with the lock released. This flattens nested and concurrent writes into
/// one totally ordered stream and keeps observer callbacks off the lock.
/// </summary>
/// <remarks>
/// The queue shares the coordinator's <see cref="Lock"/> and observes the coordinator's disposed flag
/// through the injected predicate; it owns only <c>_pending</c> and <c>_draining</c>, both guarded by that
/// shared lock. Each method documents whether it must be called under the lock or with the lock released.
/// Observer callbacks are invoked ONLY by <see cref="Drain"/>, which runs with the lock released.
/// </remarks>
internal sealed class PathDeliveryQueue<TValue>
{
    private readonly Lock _lock;
    // Nulled by ReleaseObserver on dispose (Volatile.Write) so a retained handle does not pin the observer's
    // closure; Drain reads it into a local first. Mirrors PropertyChangeSubscription's _observer handling.
    private ISubjectPathChangeObserver<TValue>? _observer;
    private readonly Func<bool> _isDisposed;

    // Both guarded by the shared lock. _draining marks the single active drainer; _pending is the FIFO
    // backlog every non-drainer producer appends to. Steady-state delivery keeps _pending empty.
    private readonly Queue<SubjectPathChange<TValue>> _pending = new();
    private bool _draining;

    internal PathDeliveryQueue(Lock sharedLock, ISubjectPathChangeObserver<TValue> observer, Func<bool> isDisposed)
    {
        _lock = sharedLock;
        _observer = observer;
        _isDisposed = isDisposed;
    }

    /// <summary>True when the backlog is empty. Callers must hold the lock.</summary>
    internal bool IsEmpty
    {
        get
        {
            Debug.Assert(_lock.IsHeldByCurrentThread, "IsEmpty must be read under the coordinator lock.");
            return _pending.Count == 0;
        }
    }

    /// <summary>Appends one computed event to the FIFO backlog. Callers must hold the lock.</summary>
    internal void Enqueue(in SubjectPathChange<TValue> change)
    {
        Debug.Assert(_lock.IsHeldByCurrentThread, "Enqueue must be called under the coordinator lock.");
        _pending.Enqueue(change);
    }

    /// <summary>
    /// Claim-drainer step for a producer that has finished computing. <paramref name="held"/> /
    /// <paramref name="hasHeldEvent"/> is the caller's uncontended zero-copy candidate (an event produced
    /// while the backlog was empty). Returns true when this producer becomes the drainer and must call
    /// <see cref="Drain"/> once the lock is released; in that case a held event is handed back through
    /// <paramref name="directEvent"/> to dispatch directly, skipping the enqueue/dequeue copy of the large
    /// change struct. Returns false when a drainer is already active (the held event, if any, is handed off
    /// to it) or when there is nothing to deliver. Callers must hold the lock.
    /// </summary>
    internal bool TryClaimDrainer(
        bool hasHeldEvent, in SubjectPathChange<TValue> held,
        out SubjectPathChange<TValue> directEvent, out bool hasDirectEvent)
    {
        Debug.Assert(_lock.IsHeldByCurrentThread, "TryClaimDrainer must be called under the coordinator lock.");

        directEvent = default;
        hasDirectEvent = false;

        if (_draining)
        {
            // A drainer is active (a nested or concurrent write): hand it everything this call produced and
            // let it deliver after the current callback returns. Flattens nesting, preserves FIFO.
            if (hasHeldEvent)
            {
                _pending.Enqueue(held);
            }

            return false;
        }

        if (!hasHeldEvent && _pending.Count == 0)
        {
            // Every pass was suppressed and no prior throwing callback stranded a backlog: nothing to do.
            return false;
        }

        // Claim the drainer; the caller runs the callbacks with the lock released.
        _draining = true;

        if (hasHeldEvent)
        {
            // hasHeldEvent implies _pending was empty when the single event was produced and no later pass or
            // concurrent producer could enqueue under the held lock, so this is the uncontended zero-copy fast
            // path: deliver directly without touching the queue.
            Debug.Assert(_pending.Count == 0, "the zero-copy fast path requires an empty backlog.");
            directEvent = held;
            hasDirectEvent = true;
        }
        // Otherwise multiple events and/or a stranded backlog are already queued; the drain loop delivers.

        return true;
    }

    /// <summary>
    /// Drains the backlog: dispatches the optional <paramref name="directEvent"/> first, then delivers each
    /// queued event FIFO, re-acquiring the lock per dequeue. Callbacks run here, OUTSIDE the lock, because a
    /// callback may re-enter (a nested write) and holding the lock across it would serialize the graph or
    /// deadlock. Callers must have released the lock and must be the drainer claimed by
    /// <see cref="TryClaimDrainer"/>.
    /// </summary>
    internal void Drain(bool hasDirectEvent, in SubjectPathChange<TValue> directEvent)
    {
        Debug.Assert(!_lock.IsHeldByCurrentThread, "Drain must be called with the coordinator lock released.");

        // Read the observer once, outside the lock, into a local. Dispose may null it concurrently (releasing
        // the closure), but an already-claimed drain must still run its in-flight callbacks to completion, so
        // the captured local is used for the whole session. A null here means Dispose won the race before any
        // delivery began; deliver nothing (post-dispose delivery stays bounded) but still clear the drainer.
        var observer = Volatile.Read(ref _observer);

        try
        {
            if (hasDirectEvent && observer is not null)
            {
                observer.OnChange(in directEvent);
            }

            while (true)
            {
                SubjectPathChange<TValue> next;
                lock (_lock)
                {
                    // Atomic empty-check and drainer clear: a write enqueuing just as the drainer exits is
                    // either seen here (dequeued and delivered) or finds _draining false under the lock and
                    // becomes the next drainer. Neither loses the event nor lets two threads drain at once.
                    // The disposed/observer check bounds post-dispose delivery: once disposal is observed the
                    // drainer starts no new delivery and drops the remaining backlog (Dispose already cleared
                    // the backlog), so at most the one callback already dispatched outside the lock completes.
                    if (observer is null || _isDisposed() || _pending.Count == 0)
                    {
                        _draining = false;
                        return;
                    }

                    next = _pending.Dequeue();
                }

                observer.OnChange(in next);
            }
        }
        catch
        {
            // A throwing callback abandons the drain. Reset the drainer flag (leaving _pending intact) so the
            // next computed event can claim the drainer and deliver the stranded backlog; without this reset
            // _draining stays true forever and every later event strands permanently.
            lock (_lock)
            {
                _draining = false;
            }

            throw;
        }
    }

    /// <summary>Drops the queued-but-undelivered backlog. Callers must hold the lock (used by dispose).</summary>
    internal void Clear()
    {
        Debug.Assert(_lock.IsHeldByCurrentThread, "Clear must be called under the coordinator lock.");
        _pending.Clear();
    }

    /// <summary>
    /// Releases the observer on dispose (<c>Volatile.Write</c>) so a retained handle does not pin its closure.
    /// Paired with the <c>Volatile.Read</c> at the top of <see cref="Drain"/>: a drain already in flight keeps
    /// delivering through its captured local, so an in-flight callback still completes. Called from dispose
    /// under the lock, but the release itself needs only the volatile write.
    /// </summary>
    internal void ReleaseObserver() => Volatile.Write(ref _observer, null);
}
