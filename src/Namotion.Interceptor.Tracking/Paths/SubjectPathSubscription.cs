using System;
using System.Buffers;
using System.Threading;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>
/// A live subscription to a decomposed path from a root subject. It installs one per-property listener
/// per resolved segment (a subscribe-before-read chain) so a write to any watched link is observed, and
/// exposes the path's current observed value via a fresh, tracker-lock-free walk. Disposal is one-shot.
/// On a watched write it computes the event (a validating walk plus a divergent retrack of the suffix)
/// under the lock, then delivers it through an ordered exclusive-drain queue: one drainer at a time runs
/// callbacks outside the lock, so nested and concurrent writes enqueue and are delivered in FIFO order
/// after the current callback returns (flattened, never inline).
/// </summary>
public sealed class SubjectPathSubscription<TValue> : IDisposable
{
    private readonly Lock _lock = new();

    // Exposed to the split collaborators for their debug-only lock-precondition asserts (compiled out of
    // Release builds); this type stays the sole owner of the lock.
    internal bool IsLockHeldByCurrentThread => _lock.IsHeldByCurrentThread;

    // The segment chain (installed listeners, per-position observers, resolved subjects and cached
    // accessors) and the exclusive-drain delivery are split into collaborators, but this type stays the
    // SOLE owner of _lock, _disposed, the reentrancy guard, and transaction ordering. Both collaborators
    // are called under _lock (except the lock-free Current walk); neither holds a lock of its own.
    private readonly PathSubscriptionChain<TValue> _chain;
    private readonly PathDeliveryQueue<TValue> _deliveryQueue;

    // Per-subscription validation mode: with LeafOnly, a callback fired by the resolved leaf re-reads
    // only the leaf on its cached subject instead of running the from-root validating walk. Structural
    // (upper-segment) callbacks always walk and retrack in both modes.
    private readonly SubjectPathValidation _validation;

    // A single reusable walk buffer for the under-lock event computation: the ctor seed walk, every event
    // walk and every retrack re-walk write into it. All of those run under _lock and never nest (the
    // reentrancy guard returns without walking), so one buffer is safe. Current does NOT use it: Current is
    // lock-free and concurrent, so it rents a throwaway buffer from the pool instead.
    private readonly IInterceptorSubject?[] _scratch;

    // The last observed value, seeded at subscribe time and advanced in TryComputeEvent (before the
    // observer callback runs) so an event's Old reflects prior observed state rather than a false
    // unresolved starting point.
    private SubjectPathValue<TValue> _lastObserved;

    // Set (under _lock) by the thread-affine reentrancy guard when a property getter invoked during this
    // subscription's own event walk writes a watched segment and re-enters ProcessSegmentCallback on the
    // SAME thread. The in-flight computation loop observes it and re-walks, so the getter's write is picked
    // up by a fresh event after the current walk finishes instead of corrupting it.
    private bool _deferredRevalidation;

    private bool _disposed;

    internal SubjectPathSubscription(
        IInterceptorSubject root,
        PathSegment[] segments,
        ISubjectPathChangeObserver<TValue> observer,
        SubjectPathValidation validation)
    {
        _validation = validation;
        _chain = new PathSubscriptionChain<TValue>(this, root, segments);
        _deliveryQueue = new PathDeliveryQueue<TValue>(_lock, observer, () => Volatile.Read(ref _disposed));
        _scratch = new IInterceptorSubject?[segments.Length];

        lock (_lock)
        {
            try
            {
                _chain.BuildFrom(0, root);

                // Seed from a fresh walk AFTER the listeners are installed: any write that lands between the
                // build and this read is already covered by an installed listener, so the seed cannot open a
                // false unresolved -> resolved edge on the first delivery. Runs under _lock before any
                // concurrent access, so the reusable _scratch buffer is safe here.
                _lastObserved = _chain.Walk(_scratch);
            }
            catch
            {
                // Teardown on throw: BuildFrom only throws on a reserved-key contract violation
                // (PropertyChangeSubscription.Create raising InvalidOperationException for a foreign ni.pcl
                // value; name resolution during the build is non-throwing, and the seed walk never throws).
                // Dispose the segment handles already installed so the process-wide count is rolled back
                // before the exception propagates and no partial subscription escapes to the caller.
                _chain.DisposeAll();
                throw;
            }
        }
    }

    /// <summary>
    /// The path's current observed value, computed by a fresh walk from the root on every read (never
    /// cached) so it always reflects the live graph. The walk takes no tracker lock and never throws:
    /// any unreachable segment resolves to <see cref="SubjectPathValue{TValue}.Unresolved"/>. Returns
    /// unresolved once the subscription is disposed.
    /// </summary>
    public SubjectPathValue<TValue> Current
    {
        get
        {
            if (Volatile.Read(ref _disposed))
            {
                return SubjectPathValue<TValue>.Unresolved;
            }

            // A per-read buffer rented from the shared pool rather than allocated: the array holds
            // reference-type subjects, so it cannot be stack allocated, and Current is lock-free and
            // concurrent, so it cannot share the event path's _scratch. Only Walk's return value is used
            // (never the buffer contents), so an oversized rented array is fine; it is returned cleared so
            // no subject reference is retained in the pool.
            var rented = ArrayPool<IInterceptorSubject?>.Shared.Rent(_chain.Length);
            try
            {
                return _chain.Walk(rented);
            }
            finally
            {
                ArrayPool<IInterceptorSubject?>.Shared.Return(rented, clearArray: true);
            }
        }
    }

    /// <summary>
    /// One-shot dispose: tears down every installed segment listener (returning the process-wide count to
    /// zero), drops any queued-but-undelivered events, releases the buffered walk references, the graph root,
    /// and the observer, and marks the subscription disposed. Idempotent and never throws; after it returns
    /// <see cref="Current"/> is unresolved. A callback already dispatched outside the lock may run to
    /// completion, but the drain loop observes the disposed flag and starts no new delivery, so post-dispose
    /// delivery is bounded.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _disposed, true);
            _chain.DisposeAll();
            _deliveryQueue.Clear();

            // Release what the listener teardown above leaves behind so a retained disposed handle pins
            // nothing: the last-walked subjects in the reusable scratch buffer (some already detached) and the
            // last observed leaf value (both touched only under _lock, so cleared here race-free), plus the
            // graph root, the decomposed segments (their fixed dictionary-key/index arguments may be arbitrary
            // objects), and the observer closure. Root, segments, and observer are nulled via Volatile.Write,
            // matching the per-property primitive (PropertyChangeSubscription): the lock-free Current walk and
            // an in-flight drain read them into a local first, so an already-claimed callback still completes.
            Array.Clear(_scratch);
            _lastObserved = SubjectPathValue<TValue>.Unresolved;
            _chain.ReleaseReferences();
            _deliveryQueue.ReleaseObserver();
        }
    }

    /// <summary>
    /// Entry point for a per-segment listener callback. All state (the validating walk, a divergent
    /// retrack, the kind decision, the suppression check, the <see cref="_lastObserved"/> advance and the
    /// enqueue/claim-drainer decision) is computed under <see cref="_lock"/>; the observer callbacks are
    /// then run OUTSIDE the lock by the exclusive drainer. A getter that writes a watched segment during the
    /// walk re-enters here on the same thread; the thread-affine guard defers it and the computation loops
    /// until no deferral remains, so a side-effecting getter never deadlocks nor runs a callback under the
    /// lock. When disposed or when the callback's observer is stale (a rebuild has already replaced it at its
    /// position), the invocation is dropped.
    /// </summary>
    internal void ProcessSegmentCallback(PathSegmentObserver<TValue> observer, in SubjectPropertyChange cause)
    {
        // Thread-affine reentrancy guard. If THIS thread already holds _lock we are re-entering our own
        // computation: a property getter invoked by the event walk below wrote a watched segment of this
        // same subscription, and that write dispatched synchronously back here. Computing now would run a
        // nested walk while the outer one is mid-flight (corrupting the outer walk and the _lastObserved
        // advance) and could even claim the drainer and run a callback while _lock is still held, an AB-BA
        // hazard. Instead flag a deferred revalidation and return WITHOUT computing; the outer computation
        // loop re-walks once it finishes, so the getter's write is observed by a fresh event and callbacks
        // never run under _lock. IsHeldByCurrentThread is a thread-local read, free on the common path.
        if (_lock.IsHeldByCurrentThread)
        {
            _deferredRevalidation = true;
            return;
        }

        // The uncontended fast path hands the drainer a single stack-local event, skipping the enqueue and
        // dequeue copies of the large change struct. Both live outside the lock so the drain section can read
        // them after the lock is released.
        SubjectPathChange<TValue> directEvent = default;
        var hasDirectEvent = false;
        var shouldDrain = false;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            // Slot identity: a callback whose observer is no longer the current one for its position was
            // fired by a torn-down listener; drop it rather than acting on a stale chain.
            if (!_chain.IsCurrentObserver(observer))
            {
                return;
            }

            // The leaf-only fast path applies exactly when the firing observer is the resolved leaf's own
            // listener: the compute below then re-reads only the cached leaf instead of walking from the
            // root, skipping divergence detection and retrack (the mode's documented carve-out). Any
            // upper-segment callback keeps the full validating walk in both modes. This is the FIRST-pass
            // determination only; a deferred re-pass forces the full walk (see the loop below).
            var leafOnlyLeafWrite = _validation == SubjectPathValidation.LeafOnly && observer.Position == _chain.Length - 1;

            // Suppress the ambient transaction for the whole computation below. A [Derived]-with-setter or
            // cross-context write on a transaction-holding flow bypasses staging and dispatches here
            // synchronously, so the validating walk and every retrack read run WHILE a transaction is active.
            // Left unsuppressed, those reads would return the transaction's staged read-your-writes view, and
            // a divergence computed against a speculative staged subject would retrack the chain onto it and
            // then be stranded when the transaction rolls back. Reading committed state keeps every retrack
            // decision durable. Restored (by the scope's Dispose) BEFORE the drain runs callbacks (outside the
            // lock), so callbacks still observe the caller's transaction, and even if a retrack throws.
            using var ambientTransactionScope = new AmbientTransactionScope();

            // Deferred-revalidation loop. Each pass computes one event (a validating walk, a divergent
            // retrack, the kind decision, the suppression check and the _lastObserved advance). A getter
            // that wrote a watched segment during a pass's walk re-entered via the guard above and set
            // _deferredRevalidation, so the loop recomputes a fresh event until no deferral remains. Passes
            // enqueue in order, giving total FIFO delivery; the loop settles fully before any callback runs,
            // so callbacks never run under _lock. The common (no side-effecting getter) case runs the loop
            // exactly once. Termination assumes convergence: a getter that writes a fresh, non-suppressed
            // watched value on EVERY walk would loop here forever holding _lock, but that is a caller
            // contract violation (getters must be side-effect-free), not a supported case.
            SubjectPathChange<TValue> heldEvent = default;
            var hasHeldEvent = false;
            var firstPass = true;
            do
            {
                _deferredRevalidation = false;

                // Only the first pass corresponds to the actual firing observer, so only it may take the
                // leaf-only fast path. A deferred re-pass was triggered by a watched-segment write a getter
                // made during the previous pass's walk; the reentrancy guard did not record that write's
                // position, so it may be a structural (upper-segment) change. Force the full from-root walk on
                // every deferred pass so such a change is detected and retracked instead of being trusted as a
                // leaf re-read (which would strand the chain on the departed branch).
                var leafFast = leafOnlyLeafWrite && firstPass;
                firstPass = false;

                if (!TryComputeEvent(in cause, leafFast, out var change))
                {
                    // Suppressed pass: nothing to deliver. It still counts for the loop because its own walk
                    // may have set _deferredRevalidation via a getter write.
                    continue;
                }

                if (!hasHeldEvent && _deliveryQueue.IsEmpty)
                {
                    // Hold the first event of an uncontended call in a local so the common no-deferral path
                    // can deliver it without touching the queue (zero copy). A later pass flushes it first.
                    heldEvent = change;
                    hasHeldEvent = true;
                }
                else
                {
                    // Second (deferred) event this call, or a prior throwing callback stranded a backlog:
                    // flush any held first event, then enqueue, so total order is enqueue order.
                    if (hasHeldEvent)
                    {
                        _deliveryQueue.Enqueue(in heldEvent);
                        hasHeldEvent = false;
                    }

                    _deliveryQueue.Enqueue(in change);
                }
            }
            while (_deferredRevalidation);

            shouldDrain = _deliveryQueue.TryClaimDrainer(hasHeldEvent, in heldEvent, out directEvent, out hasDirectEvent);
        }

        // Delivery runs the observer callbacks, OUTSIDE _lock. A callback may re-enter (a nested write);
        // holding the lock across it would serialize the graph or deadlock.
        if (shouldDrain)
        {
            _deliveryQueue.Drain(hasDirectEvent, in directEvent);
        }
    }

    /// <summary>
    /// Computes one event for the triggering <paramref name="cause"/>. The default head is a fresh
    /// validating walk into <see cref="_scratch"/>, the divergence compare, a divergent retrack (via
    /// <see cref="PathSubscriptionChain{TValue}.DisposeSuffix"/> plus
    /// <see cref="PathSubscriptionChain{TValue}.BuildFrom"/>) and re-walk, and the kind decision; with
    /// <paramref name="leafFast"/> (a leaf callback under <see cref="SubjectPathValidation.LeafOnly"/>)
    /// the head instead re-reads only the cached leaf, with no walk, divergence check, or retrack. The
    /// shared tail is the suppression check and the <see cref="_lastObserved"/> advance-before-callback.
    /// Returns false when the event is suppressed (no delivery), though the baseline still advances to the
    /// fresh value. Callers must hold <see cref="_lock"/>.
    /// </summary>
    private bool TryComputeEvent(in SubjectPropertyChange cause, bool leafFast, out SubjectPathChange<TValue> change)
    {
        change = default;

        SubjectPathValue<TValue> newValue;
        SubjectPathChangeKind kind;
        if (leafFast)
        {
            // Leaf-only fast path: the firing listener IS the resolved leaf's, so the cached leaf binding is
            // re-read directly and the upper segments are trusted (an intact chain's upper segments cannot
            // have changed without their own listeners firing; a divergence created while dormant is the
            // mode's documented carve-out and heals on the next structural callback).
            newValue = _chain.ReadResolvedLeaf();
            kind = newValue.IsResolved ? SubjectPathChangeKind.ValueChange : SubjectPathChangeKind.PathChange;
        }
        else
        {
            // Fresh validating walk from the root: it recomputes the observed value AND records, per position,
            // the subject each segment now reads on, which the divergence check compares against the subscribed
            // chain. Reuses the under-lock _scratch buffer (Walk clears it first); every access below runs under
            // _lock, and any divergent subject is captured into a local before the retrack re-walk overwrites the
            // buffer.
            var scratch = _scratch;
            newValue = _chain.Walk(scratch);

            // Divergence is the first position where the fresh walk reads on a different subject than the
            // subscribed chain (reference identity). This one comparison covers every structural case: an intact
            // chain never diverges; a reassigned intermediate, a heal (null -> subject) and a break
            // (subject -> null) each first differ at the affected position.
            var divergencePoint = _chain.FindDivergence(scratch);

            var diverged = divergencePoint >= 0;
            if (diverged)
            {
                // Retrack the suffix below the change: tear down the stale listeners, then reinstall the
                // subscribe-before-read chain from the new subject. A null divergent subject is a break
                // (an unresolved intermediate); dispose only, leaving the suffix torn down.
                var divergentSubject = scratch[divergencePoint];
                _chain.DisposeSuffix(divergencePoint);
                if (divergentSubject is not null)
                {
                    _chain.BuildFrom(divergencePoint, divergentSubject);
                }

                // Recompute from the rebuilt chain: the retrack's reads supersede the initial walk, so a write
                // that raced the listener install is observed rather than lost. The captured divergentSubject
                // local above is unaffected by reusing scratch for this re-walk.
                newValue = _chain.Walk(scratch);
            }

            // ValueChange only when the intact chain's own resolved leaf was the write; any structural change
            // (divergence) or an unresolved result is a PathChange.
            kind = SubjectPathChangeKind.PathChange;
            if (!diverged && newValue.IsResolved && _chain.IsResolvedLeafWrite(in cause))
            {
                kind = SubjectPathChangeKind.ValueChange;
            }
        }

        if (SubjectPathValue<TValue>.AreEquivalent(_lastObserved, newValue))
        {
            // Suppressed: an observed state equivalent to the last one delivers nothing. The baseline still
            // advances to the fresh value: a distinct-but-Equals-equal instance (only reachable for a
            // value-equality TValue) must not leave the baseline pinning the departed instance or later
            // surface it as a stale OldState. The transition stays unobservable because equivalence is
            // value-based, so the new baseline is value-equal to the old one.
            _lastObserved = newValue;
            return false;
        }

        // Advance BEFORE the callback: a transition chained from the callback then sees the new baseline, and
        // a throwing callback cannot replay this same transition.
        var oldObserved = _lastObserved;
        _lastObserved = newValue;

        // A deferred pass reuses the outer triggering cause (not the getter write that produced this delta),
        // so Cause/Kind reflect the triggering write while Old/New are the true observed transition. Only
        // reachable under a getter contract violation, so acceptable, but noted so a future debugger of the
        // cause fields is not confused.
        change = new SubjectPathChange<TValue>(kind, oldObserved, newValue, in cause);
        return true;
    }

    /// <summary>
    /// Suppresses the caller's ambient transaction for the duration of an under-lock event computation and
    /// restores it on <see cref="Dispose"/>, which runs (at the end of the lock block) BEFORE any callback
    /// is dispatched outside the lock and even if a retrack throws. See the suppression rationale at the use
    /// site. Cost is a single AsyncLocal round-trip, paid only when a transaction is active during dispatch
    /// (gated on <see cref="SubjectTransaction.HasActiveTransaction"/>).
    /// </summary>
    private readonly ref struct AmbientTransactionScope
    {
        private readonly SubjectTransaction? _suppressed;

        public AmbientTransactionScope()
        {
            _suppressed = SubjectTransaction.HasActiveTransaction ? SubjectTransaction.Current : null;
            if (_suppressed is not null)
            {
                SubjectTransaction.SetCurrent(null);
            }
        }

        public void Dispose()
        {
            if (_suppressed is not null)
            {
                SubjectTransaction.SetCurrent(_suppressed);
            }
        }
    }
}
