using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Owns structural graph membership for one context: which subjects it holds, through which
/// occurrence-aware edges, and when a subject that lost its last support leaves.
/// </summary>
/// <remarks>
/// A subject is attached to exactly one context. It is held either by a root anchor (an explicit
/// attach, or the provisional anchor a context-taking constructor leaves) or by a path of structural
/// edges from an anchored root. The provisional anchor is consumed by the first edge that supports
/// the subject independently of that anchor, so construction-time attachment does not create roots
/// that nothing ever releases; an explicit anchor is only ever cleared explicitly.
///
/// All topology changes are serialized by one private reentrant lock. Parent and reference-count
/// reads deliberately do not take it: they read published per-subject state, because consumers call
/// them from inside their own locks and from inside lifecycle callbacks.
///
/// Sealed because both the ordering seam and the default-lifecycle idempotence check key on this
/// exact type: a subclass would silently unbind every [RunsBefore]/[RunsAfter] constraint naming
/// <see cref="LifecycleInterceptor"/> and would satisfy the WithLifecycle() exists check without
/// being the default lifecycle. Third parties extend through <see cref="ILifecycleInterceptor"/>.
/// </remarks>
public sealed class LifecycleInterceptor : ILifecycleInterceptor, ILifecycleHandler
{
    private readonly IInterceptorSubjectContext _context;
    private readonly OwnershipGraph _graph;
    private readonly ReachabilityWalk _reachability;
    private readonly ReleaseTraversal _release;
    private readonly StructuralReconciler _reconciler;
    private readonly AttachTraversal _attach;
    private readonly LifecycleNotifier _notifier;
    private readonly PropertyAdmission _admission;

    // One reentrant topology lock per lifecycle, and the outermost lock of the structural write
    // order (see the executor's _attachmentLock note for the full order). Reentrancy is required:
    // Core enters the gate before the chain is resolved and this interceptor enters it again from
    // inside the chain. Always taken through EnterGate, never directly, so the
    // one-transaction-per-thread rule below sees every acquisition.
    private readonly Lock _gate = new();

    // The one deadlock this design cannot prevent is a gate holder waiting for topology work it
    // dispatched to another thread: it cannot release the gate until that work finishes and that
    // work cannot start until the gate is released. Only a thread holding no gate at all ever waits
    // here, because the rule above rejects a second transaction and a reentrant acquisition never
    // blocks, so a wait that never ends is that deadlock rather than a lock-ordering one, and is
    // turned into an exception instead of a hang.
    //
    // A waiter tells the deadlock from ordinary contention by looking at the holder rather than at
    // the clock: a holder that is running is making progress, however long it takes, while a holder
    // that never runs again can only be one that waits for work needing this gate. So a holder seen
    // running resets the verdict and no amount of elapsed time alone convicts. Only continuous
    // blocking does, over a threshold far above any lock a holder legitimately waits on and far
    // below the point an operator calls the process hung.
    //
    // The runtime offers no way to ask what a thread waits on, and a dispatch through a task, a
    // queue or a pool thread carries no link back to its origin, so sampling the holder's state is
    // the only in-process signal that exists. It costs nothing on any normal path: a waiter reaches
    // this only after failing to take the gate within HolderSampleIntervalMilliseconds, and it
    // parks in the same wait it would have parked in anyway.
    private const int HolderSampleIntervalMilliseconds = 20;
    private const int DefaultBlockedHolderThresholdMilliseconds = 30_000;

    // Per instance rather than constant so a test can convict in milliseconds instead of spending
    // the full threshold per case. Per instance and not static, so tests running in parallel cannot
    // shorten each other's threshold. Read only by a thread already waiting on a contended gate.
    internal int BlockedHolderThresholdMilliseconds { get; set; } = DefaultBlockedHolderThresholdMilliseconds;

    // The last resort for what the holder check cannot see at all: a holder looping forever, one
    // spinning, one blocked inside unmanaged code, and one polling so it never blocks for the whole
    // threshold above. None of those is distinguishable from work, so this cannot be a judgement
    // about the holder and is only a bound past which a process is stuck by any reasonable measure.
    // Sized far above the longest legitimate hold rather than near it: attaching a quarter of a
    // million subjects measures in seconds, so this keeps two orders of magnitude of room and still
    // reports before a test harness or an operator calls the process hung.
    private const int DefaultGateWaitTimeoutMilliseconds = 300_000;

    // Settable for the same reason as the threshold above, and more sharply: at its real size
    // no test can afford to reach it, so without this the bound would ship untested.
    internal int GateWaitTimeoutMilliseconds { get; set; } = DefaultGateWaitTimeoutMilliseconds;

    // The thread inside a topology transaction of this lifecycle, null when there is none. Written
    // under the gate and read without it, by a waiter that is diagnosing its own wait rather than
    // deciding anything: a stale read costs one sample of a window that needs all of them.
    private Thread? _gateHolder;

    // How many topology gates the current thread holds, across every lifecycle. Gates have no
    // order among themselves, so a thread holding one and blocking on another deadlocks against a
    // thread taking them the other way round: a second transaction on a different lifecycle is
    // rejected instead of waiting.
    [ThreadStatic]
    private static int _heldGateCount;

    // How many threads are inside a topology transaction of this lifecycle. Counted once per
    // thread, not once per acquisition, because the gate is reentrant. The window between a
    // terminal store and its reconcile is user-visible and cannot be closed, so readers that would
    // otherwise convict a subject caught in it ask this instead.
    private int _transactionsInFlight;

    // Work registered by a reader that withheld a verdict because this lifecycle was mid-transaction,
    // to be re-run once it is not. A handshake rather than an inference: nothing else guarantees
    // that the thread which opened the window will recalculate what read through it. Guarded by its
    // own lock, which is a leaf: it is taken by a thread holding the reader's own lock, and
    // released before anything registered here runs.
    private readonly Lock _withheldLock = new();
    private List<Action>? _withheldRecalculations;

    /// <summary>
    /// Raised when a subject is attached to the object graph.
    /// Handlers must be exception-free and fast (invoked inside lock). Never hand structural
    /// work to another thread and wait for it from here: the dispatched write needs the very
    /// gate this thread is holding. Dispatching a read, a scalar write or input and output is
    /// safe, and so is handing structural work off without waiting.
    /// </summary>
    public event Action<SubjectLifecycleChange>? SubjectAttached
    {
        add => _notifier.SubjectAttached += value;
        remove => _notifier.SubjectAttached -= value;
    }

    /// <summary>
    /// Raised when a subject is about to be detached from the object graph.
    /// Fires BEFORE ILifecycleHandler.HandleLifecycleChange (symmetric with SubjectAttached which fires AFTER).
    /// The subject's ownership record and baselines are already gone by this point, so GetParents()
    /// answers empty and GetReferenceCount() answers zero; the subject still resolves its context,
    /// which is what the teardown callbacks need.
    /// Handlers must be exception-free and fast (invoked inside lock). Never hand structural
    /// work to another thread and wait for it from here: the dispatched write needs the very
    /// gate this thread is holding. Dispatching a read, a scalar write or input and output is
    /// safe, and so is handing structural work off without waiting.
    /// </summary>
    public event Action<SubjectLifecycleChange>? SubjectDetaching
    {
        add => _notifier.SubjectDetaching += value;
        remove => _notifier.SubjectDetaching -= value;
    }

    /// <summary>
    /// Creates the lifecycle for one context. That context is the single exact context this
    /// interceptor claims subjects for.
    /// </summary>
    public LifecycleInterceptor(IInterceptorSubjectContext context)
    {
        _context = context;
        _graph = new OwnershipGraph(context);
        _notifier = new LifecycleNotifier(context, _graph, this);
        _reachability = new ReachabilityWalk(_graph);
        _attach = new AttachTraversal(_notifier, _graph, _reachability);
        _release = new ReleaseTraversal(_notifier, _graph, _reachability);
        _reconciler = new StructuralReconciler(_notifier, _graph, _attach, _release);
        _admission = new PropertyAdmission(_graph, _reconciler, _attach);
    }

    #region Structural writes

    /// <inheritdoc />
    public void EnterStructuralWriteGate()
    {
        EnterGate();
    }

    /// <inheritdoc />
    public void ExitStructuralWriteGate()
    {
        ExitGate();
    }

    /// <summary>
    /// Enters the topology gate, rejecting a second transaction on a different lifecycle before it
    /// can block. Re-entering the gate this thread already holds is legal and load-bearing.
    /// A thread that has to wait watches the holder, so the one deadlock this design cannot prevent,
    /// a gate holder waiting for topology work it dispatched to another thread, ends in a named
    /// exception on the dispatched thread rather than in a permanent hang.
    /// </summary>
    private GateScope EnterGate()
    {
        if (_heldGateCount > 0 && !_gate.IsHeldByCurrentThread)
        {
            throw new LifecycleContractViolationException(
                "A thread runs at most one lifecycle topology transaction at a time, and this one " +
                "is already inside a transaction of another context. Topology gates have no order " +
                "among themselves, so waiting for a second one can deadlock against a thread " +
                "taking them the other way round. Nothing was read and nothing was changed: defer " +
                "the second operation until the enclosing one completes.");
        }

        if (!_gate.TryEnter(HolderSampleIntervalMilliseconds))
        {
            WaitForGate();
        }

        if (_heldGateCount++ == 0)
        {
            _gateHolder = Thread.CurrentThread;
            // Past the rejection above, a nonzero count means this thread already holds this very
            // gate, so a reentrant acquisition is not a new transaction.
            Interlocked.Increment(ref _transactionsInFlight);
        }

        return new GateScope(this);
    }

    internal bool TryQueuePropertyCallback(PropertyReference property, bool attach)
    {
        if (!_gate.IsHeldByCurrentThread) return false;
        _notifier.QueueProperty(property, attach);
        return true;
    }

    internal bool TryQueuePropertyChange(Change.PropertyChangeInterceptor.Publication publication)
    {
        if (!_gate.IsHeldByCurrentThread) return false;
        _notifier.QueuePropertyChange(publication);
        return true;
    }

    private void ExitGate()
    {
        try
        {
            if (_heldGateCount == 1) _notifier.Drain();
        }
        finally
        {
            ReleaseGate();
        }
    }

    private void ReleaseGate()
    {
        // Decrement first, so an unbalanced exit leaves the count too low rather than too high: a
        // count stranded above zero on a pooled thread would reject that thread's next unrelated
        // transaction, while one below zero only stops the rule firing.
        var leftTheTransaction = --_heldGateCount == 0;
        if (leftTheTransaction)
        {
            // Before the gate is released and before the drain below takes the registration lock,
            // so a reader that registers after this point reads a settled count and is told to
            // decide for itself rather than to wait for a transaction that has ended.
            Interlocked.Decrement(ref _transactionsInFlight);
            _gateHolder = null;
        }

        _gate.Exit();

        if (leftTheTransaction)
        {
            // Unconditionally, without first peeking at the list: a peek outside the registration
            // lock creates a third outcome for a registration that has passed the count check and
            // not yet published its entry, which is then neither drained nor refused.
            RunWithheldRecalculations();
        }
    }

    // Its own method, and never inlined, so EnterGate keeps its stack frame: inlined, this method's
    // locals and the message building are set up on every successful acquisition too.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WaitForGate()
    {
        var deadline = Environment.TickCount64 + GateWaitTimeoutMilliseconds;
        Thread? blockedHolder = null;
        var blockedSince = 0L;

        while (true)
        {
            // Sampled before the wait rather than after it, so the window is the wait itself and a
            // holder that releases the gate during it is seen as gone on the next pass.
            var holder = Volatile.Read(ref _gateHolder);
            if (holder is not null && (holder.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0)
            {
                // Per holder, not per wait: a gate handed from one thread to the next is a queue
                // draining, and each new holder starts its own window. Measured as elapsed time
                // rather than as a sample count, so load stretches the sampling rate without
                // stretching what the threshold means.
                if (!ReferenceEquals(holder, blockedHolder))
                {
                    blockedHolder = holder;
                    blockedSince = Environment.TickCount64;
                }
                else if (Environment.TickCount64 - blockedSince >= BlockedHolderThresholdMilliseconds)
                {
                    ThrowHolderBlocked(BlockedHolderThresholdMilliseconds);
                }
            }
            else
            {
                blockedHolder = null;
            }

            if (_gate.TryEnter(HolderSampleIntervalMilliseconds))
            {
                return;
            }

            // The holder check above sees only a thread the runtime reports as blocked, so it
            // cannot see a holder looping forever, spinning, blocked inside unmanaged code, or
            // polling in a way that never blocks for the whole threshold. Those hang without this,
            // so the last resort is a plain bound: nothing legitimate comes near it, and a process
            // that reaches it is already stuck.
            if (Environment.TickCount64 >= deadline)
            {
                ThrowGateWaitTimedOut(GateWaitTimeoutMilliseconds);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowHolderBlocked(int thresholdMilliseconds)
    {
        throw new LifecycleContractViolationException(
            "The thread holding the topology gate of this context has been blocked, never once seen " +
            $"running, for {TimeSpan.FromMilliseconds(thresholdMilliseconds).TotalSeconds:0.##} seconds. " +
            "A holder that makes progress is never reported here however long it takes, so this is " +
            "one of two things, " +
            "and both break the contract that a gate holder is fast and waits on nothing. The first " +
            "is the deadlock this framework cannot prevent: the thread inside the topology " +
            "transaction dispatched structural work to another thread and waits for it, so it cannot " +
            "release the gate until that work finishes and that work cannot start until the gate is " +
            "released. A dispatch through Task.Run, the thread pool or a queue carries no link back " +
            "to its origin, so watching the holder is the only way this can be seen at all. Never " +
            "wait for structural work on another thread from inside a structural write, a lifecycle " +
            "callback or an interceptor: complete the enclosing operation first and run the work " +
            "after it returns, or hand it off without waiting. Dispatching a read, a scalar write or " +
            "input and output and waiting for it is safe and is not this. The second is a holder " +
            "blocked that long on a sleep, on input or output, or on a lock of its own. Nothing was " +
            "read and nothing was changed.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowGateWaitTimedOut(int timeoutMilliseconds)
    {
        throw new LifecycleContractViolationException(
            $"Timed out after {TimeSpan.FromMilliseconds(timeoutMilliseconds).TotalSeconds:0.##} seconds waiting for the topology " +
            "gate of this context, which another thread has held for that whole time without ever " +
            "being seen blocked. Nothing here can tell which it is, because none of the causes is " +
            "distinguishable from work: a lifecycle callback or an interceptor genuinely running that " +
            "long, a loop spinning or polling instead of blocking, or a holder waiting inside " +
            "unmanaged code for topology work it dispatched to another thread. All of them break the " +
            "contract that a gate holder is fast and waits on nothing. Nothing was read and nothing " +
            "was changed.");
    }

    /// <summary>Releases what <see cref="EnterGate"/> took. A struct, so the using costs nothing.</summary>
    private readonly struct GateScope(LifecycleInterceptor lifecycle) : IDisposable
    {
        public void Dispose()
        {
            lifecycle.ExitGate();
        }

        public void DrainOnFailure(Exception operationFailure)
        {
            if (_heldGateCount == 1) lifecycle._notifier.Drain(operationFailure);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Scalar properties never take the topology lock. A structural property validates and claims the
    /// whole component the proposed value opens up before the backing writer runs, so a write that
    /// would pull in a subject of another context fails before the property changes. The value the
    /// terminal actually stored is claimed as well, because a normalizing or hand-written terminal
    /// can store a graph the caller never proposed.
    ///
    /// A terminal that stores a subject the write never proposed is a contract violation, and the
    /// guarantee has one boundary there: the graph is left untouched, but the backing field holds
    /// whatever that terminal stored. The framework can only invoke the terminal it was given, and a
    /// terminal that is not a function of its argument cannot be replayed to restore the prior
    /// value: replaying it with the pre-write value re-stores the same subject and fails again, and
    /// going through <c>SetValue</c> is worse, being full chain re-entry with the same substitution
    /// and therefore unbounded recursion. A terminal that stores what it was given, which is every
    /// terminal the source generator emits, never reaches the boundary and skips the second claim.
    /// </remarks>
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var property = context.Property;
        var metadata = property.Metadata;
        if (!metadata.Type.CanContainSubjects<TProperty>() || !metadata.IsIntercepted ||
            metadata is { IsDerived: true, IsDynamic: true, SetValue: null })
        {
            // Scalar, non-intercepted or a derived projection: never a graph edge. Same rule as
            // OwnershipGraph.IsStructural, which carries the reasoning, restated here because the
            // generic overload of CanContainSubjects is the write path's type fast path.
            next(ref context);
            return;
        }

        CallbackReentrancyGuard.ThrowIfInsideCallback();

        var subject = property.Subject;
        if (!ReferenceEquals(subject.Executor.AttachedContext, _context))
        {
            // Not this lifecycle's subject: either unattached, or owned by another context whose own
            // lifecycle reconciles this write.
            next(ref context);
            return;
        }

        var gate = EnterGate();
        try
        {
            if (_graph.IsReleasingUnderGate(subject) && !_graph.IsOwned(subject))
            {
                next(ref context);
                return;
            }

            var claimed = LifecycleScratch.RentSubjectList();
            try
            {
                ClaimProposedComponent(metadata.Type, context.NewValue, claimed);
                if (!_graph.IsOwned(subject) || _graph.IsEvaluatingSeedingGetter(property))
                {
                    // The enclosing seed getter supplies the stored value after this setter returns.
                    // Rereading it here recursively invokes a getter that writes its own property.
                    next(ref context);
                    return;
                }

                try
                {
                    next(ref context);
                }
                catch (Exception writeException) when (context.IsWritten)
                {
                    // A downstream interceptor can throw after the terminal committed its value.
                    try
                    {
                        ReconcileStoredValue(ref context, metadata, claimed);
                    }
                    catch (Exception reconciliationException)
                    {
                        throw new AggregateException("The write and its lifecycle reconciliation both failed.",
                            writeException, reconciliationException);
                    }

                    throw;
                }

                ReconcileStoredValue(ref context, metadata, claimed);
            }
            finally
            {
                // Claims that never became ownership are handed back; see
                // OwnershipGraph.ReleaseUnusedClaims for what leaves them behind.
                _graph.ReleaseUnusedClaims(claimed);
                LifecycleScratch.Return(claimed);
            }
        }
        catch (Exception operationFailure)
        {
            gate.DrainOnFailure(operationFailure);
            throw;
        }
        finally
        {
            gate.Dispose();
        }
    }

    private void ReconcileStoredValue<TProperty>(ref PropertyWriteContext<TProperty> context, SubjectPropertyMetadata metadata, List<IInterceptorSubject> claimed)
    {
        var property = context.Property;

        // The authoritative getter output rather than the proposed value: a normalizing or
        // derived setter may store a different graph than the caller passed.
        var getValue = metadata.GetValue;
        var storedValue = getValue is not null ? getValue(property.Subject) : context.NewValue;
        if (!IsTheProposedValue(storedValue, context.NewValue))
        {
            // The terminal stored something else, so the claim above covers a graph that is
            // not the one now in the property. Claiming what was actually stored keeps the
            // foreign-subject rejection ahead of every graph mutation: the baseline, the
            // ownership records and the attach notifications all come after this point.
            ClaimProposedComponent(metadata.Type, storedValue, claimed);
        }

        _reconciler.Reconcile(property, metadata, storedValue);
    }

    /// <summary>
    /// Whether the terminal stored the value it was given, which is what lets a write skip claiming
    /// the stored component a second time.
    /// </summary>
    /// <remarks>
    /// The question is always identity of storage, never equality of value. A reference-typed value
    /// is compared by reference and deliberately not through <see cref="object.Equals(object?)"/>:
    /// a type that overrides equality could otherwise report a different instance as the same value
    /// and suppress the claim on subjects nothing validated. A value type has no reference to
    /// compare, because the authoritative getter boxes it afresh on every call, so the question can
    /// only be asked of one whose own equality is itself storage identity, which
    /// <see cref="ImmutableArray{T}"/> is. Every other value type is claimed a second time instead,
    /// which costs one scan and cannot be wrong.
    /// </remarks>
    private static bool IsTheProposedValue<TProperty>(object? storedValue, TProperty proposedValue)
    {
        if (default(TProperty) is null)
        {
            return ReferenceEquals(storedValue, proposedValue);
        }

        return StorageIdentity<TProperty>.IsItsOwnEquality &&
               storedValue is TProperty typedStoredValue &&
               EqualityComparer<TProperty>.Default.Equals(typedStoredValue, proposedValue);
    }

    /// <summary>
    /// Whether a value type's own equality is a comparison of its storage rather than of its
    /// contents. Resolved once per property type by the runtime, so the write path reads a static.
    /// </summary>
    private static class StorageIdentity<TProperty>
    {
        internal static readonly bool IsItsOwnEquality =
            typeof(TProperty).IsGenericType &&
            typeof(TProperty).GetGenericTypeDefinition() == typeof(ImmutableArray<>);
    }

    /// <summary>
    /// Validates every subject the proposed value reaches against this context and claims the
    /// unattached ones, before the backing writer runs.
    /// </summary>
    private void ClaimProposedComponent(Type declaredType, object? proposedValue, List<IInterceptorSubject> claimed)
    {
        if (proposedValue is null)
        {
            return;
        }

        var visited = LifecycleScratch.RentSubjectSet();
        try
        {
            _graph.DiscoverComponent(declaredType, proposedValue, visited, claimed);
        }
        finally
        {
            LifecycleScratch.Return(visited);
        }

        if (!_graph.TryClaimDiscovered(claimed, null, SubjectAttachmentAnchorKind.None))
        {
            claimed.Clear();
            throw new InvalidOperationException(
                "Another context claimed a subject of the assigned graph while this write was validating it. " +
                "The write was rejected before reaching the backing field.");
        }
    }

    /// <inheritdoc />
    public bool TryAddProperties(SubjectPropertyRegistration registration)
    {
        // EnterGate rejects an admission that would open a second transaction, before the input is
        // enumerated and before anything blocks. A same-lifecycle callback re-enters this gate and
        // is the supported dynamic-property-initializer case.
        var gate = EnterGate();
        try
        {
            var subject = registration.Subject;
            if (!ReferenceEquals(subject.Executor.AttachedContext, _context))
            {
                // The attachment moved between the caller's routing read and the gate; the caller
                // re-routes against the fresh attachment.
                return false;
            }

            if (_graph.IsOwned(subject))
            {
                _admission.Admit(registration);
            }
            else
            {
                // Claimed for this context but not owned by the graph: this thread's own attach
                // descent before it publishes, or a detach callback after the release dropped the
                // record; see AdmitUnowned for the shapes.
                _admission.AdmitUnowned(registration);
            }

            return true;
        }
        catch (Exception operationFailure)
        {
            gate.DrainOnFailure(operationFailure);
            throw;
        }
        finally
        {
            gate.Dispose();
        }
    }

    #endregion

    #region Ordered handler slot (the descent)

    /// <summary>
    /// The lifecycle's slot in the ordered <see cref="ILifecycleHandler"/> fan-out: when an edge
    /// pulls a subject into the graph, it seeds that subject's own structural properties, which is
    /// the recursive attach descent. This slot is the public ordering seam: a handler runs ahead
    /// of the descent with <c>[RunsBefore(typeof(LifecycleInterceptor))]</c> and behind it with
    /// <c>[RunsAfter]</c>, and detach changes pass through it unhandled so that the same seam
    /// orders both directions.
    /// </summary>
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextAttach)
        {
            _attach.SeedChildrenIfNeeded(change.Subject);
        }
    }

    #endregion

    #region Explicit attach and detach

    /// <inheritdoc />
    public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAttachmentAnchorKind anchor)
    {
        CallbackReentrancyGuard.ThrowIfInsideCallback();

        if (!ReferenceEquals(context, _context))
        {
            throw new InvalidOperationException("The subject cannot be attached through the lifecycle of another context.");
        }

        if (anchor == SubjectAttachmentAnchorKind.None)
        {
            throw new InvalidOperationException("An attach without a root anchor would be released by the next reachability decision.");
        }

        var gate = EnterGate();
        try
        {
            var executor = subject.Executor;
            executor.TryGetAttachment(out var attachedContext, out var currentAnchor, out _);
            InterceptorSubjectExtensions.ValidateRootAnchor(attachedContext, currentAnchor, context, anchor);

            if (attachedContext is not null)
            {
                if (anchor == SubjectAttachmentAnchorKind.Provisional)
                {
                    return;
                }

                _graph.SetAnchor(subject, anchor);
                if (_graph.IsOwned(subject) || !_graph.IsReleasingUnderGate(subject))
                {
                    return;
                }

                // A retained teardown claim needs the same seeding rollback and consumed-anchor
                // tracking as a fresh attach, because a resurrection getter can reject its graph.
            }

            var claimed = LifecycleScratch.RentSubjectList();
            var consumedAnchors = LifecycleScratch.RentConsumedAnchorList();

            // Saved and restored rather than assumed null: user code the seed invokes at callback
            // depth zero can attach another root, and each attach hands back only what it consumed.
            var enclosingConsumedAnchors = _attach.ConsumedAnchors;
            _attach.ConsumedAnchors = consumedAnchors;

            var published = false;
            try
            {
                ClaimComponentForRoot(subject, anchor, claimed);
                SeedAndAttachComponent(subject);
                published = true;
            }
            finally
            {
                _attach.ConsumedAnchors = enclosingConsumedAnchors;
                if (published)
                {
                    // Seeding rereads what discovery read, so a structural getter that answers
                    // differently across the two, or a concurrent write landing in the window
                    // before the claim, leaves a claimed subject no edge points at. It would be
                    // attached, unowned and out of reach of every release; handing it back is the
                    // compensation the write path already applies to the same residue.
                    _graph.ReleaseUnusedClaims(claimed);
                }
                else
                {
                    RollbackRejectedAttach(subject, anchor, claimed, consumedAnchors);
                }

                LifecycleScratch.Return(claimed);
                LifecycleScratch.Return(consumedAnchors);
            }
        }
        catch (Exception operationFailure)
        {
            gate.DrainOnFailure(operationFailure);
            throw;
        }
        finally
        {
            gate.Dispose();
        }
    }

    /// <summary>
    /// Hands back everything a rejected attach had already written. Discovery reads user values
    /// before the claim publishes anything, so a concurrent write can install a child that seeding
    /// then refuses, and the anchor, the seeded baselines and the claims taken in between must not
    /// outlive that refusal.
    /// </summary>
    /// <remarks>
    /// Whatever the seed managed to publish hangs off the root's committed baselines, so removing
    /// those edges releases it the ordinary way, cascade and detach callbacks included. That is also
    /// the only handle on a subject a concurrent write installed after the scan: it is published but
    /// was never in the claimed set, so a claim-only rollback would leave it attached.
    ///
    /// The order is deliberate: the root keeps its anchor, its baselines and its claim until the
    /// drain has actually finished, so a rollback that cannot complete leaves the root attached and
    /// detachable rather than stripped of the very state <c>DetachFromContext</c> needs.
    ///
    /// A provisional anchor the seed consumed goes back before the drain, because the edge that
    /// consumed it is among the edges drained, and without the anchor that removal would release a
    /// subject that was attached and held before the attach began, together with everything it
    /// holds. The restore is a compare-and-swap against the revision the consumption produced, so
    /// a subject whose attachment moved since (promoted, detached, released) keeps what it has.
    /// </remarks>
    private void RollbackRejectedAttach(
        IInterceptorSubject subject,
        SubjectAttachmentAnchorKind anchor,
        List<IInterceptorSubject> claimed,
        List<(IInterceptorSubject Subject, long Revision)> consumedAnchors)
    {
        var children = LifecycleScratch.RentChildList();
        try
        {
            foreach (var (consumedSubject, revision) in consumedAnchors)
            {
                consumedSubject.Executor.TryUpdateAttachment(revision, _context, SubjectAttachmentAnchorKind.Provisional, out _);
            }

            _graph.CollectStructuralChildren(subject, children, seed: false);
            foreach (var (property, occurrence, _) in children)
            {
                _release.RemoveEdge(occurrence.Subject, property, occurrence.Index);
            }

            _graph.SetAnchor(subject, SubjectAttachmentAnchorKind.None);

            // The drain above ran while the anchor was still set, so a back edge inside the
            // component kept the root anchor-reachable and nothing released it. Re-evaluate now
            // that the anchor is gone, or the root stays owned with no anchor and no way to
            // detach it.
            var ownership = _graph.TryGetOwnership(subject);
            if (ownership is null)
            {
                _graph.ReleaseClaim(subject);
            }
            else if (ownership.IncomingCount == 0 || !_reachability.IsAnchorReachable(subject, null))
            {
                try
                {
                    _release.ReleaseRoot(subject);
                }
                catch
                {
                    // The release runs detach callbacks, so it can fail partway. Put the anchor
                    // back: the trace below tells the caller to detach the root explicitly, and
                    // without an anchor that is exactly what DetachFromContext refuses to do.
                    _graph.SetAnchor(subject, anchor);
                    throw;
                }
            }

            // An edge that survived the drain (one published from outside the component by user
            // code the seed invoked) supports its subject exactly as the drained edge did, so the
            // anchor is consumed again rather than left as a root that nothing ever releases.
            foreach (var (consumedSubject, _) in consumedAnchors)
            {
                if (_graph.TryGetOwnership(consumedSubject) is { IncomingCount: > 0 } &&
                    _reachability.IsAnchorReachable(consumedSubject, consumedSubject))
                {
                    _graph.SetAnchor(consumedSubject, SubjectAttachmentAnchorKind.None, onlyFrom: SubjectAttachmentAnchorKind.Provisional);
                }
            }

            foreach (var claimedSubject in claimed)
            {
                if (!_graph.IsOwned(claimedSubject))
                {
                    _graph.RemoveBaselines(claimedSubject);
                }
            }

            _graph.ReleaseUnusedClaims(claimed);
        }
        catch (Exception exception)
        {
            // This runs while the attach's own exception is in flight, and that one is the
            // diagnostic worth keeping: it says why the attach was refused, where this one only
            // says the cleanup after it went wrong. So the original wins and this is traced instead
            // of thrown, the rollback stops where it stood, and the root keeps the anchor and claim
            // an explicit detach needs.
            Trace.TraceError(
                $"LifecycleInterceptor: rolling back a rejected attach of {subject.GetType().Name} " +
                $"failed with {exception.GetType().Name}: {exception.Message}. The attach's own " +
                "exception is propagating and this one is not, so part of the attach is still " +
                "published and the root is still attached; detach it explicitly to clean up.");
        }
        finally
        {
            LifecycleScratch.Return(children);
        }
    }

    /// <inheritdoc />
    public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        CallbackReentrancyGuard.ThrowIfInsideCallback();

        if (!ReferenceEquals(context, _context))
        {
            throw new InvalidOperationException("The subject cannot be detached through the lifecycle of another context.");
        }

        var gate = EnterGate();
        try
        {
            var executor = subject.Executor;
            executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
            InterceptorSubjectExtensions.ValidateDetach(attachedContext, anchor, context);

            _graph.SetAnchor(subject, SubjectAttachmentAnchorKind.None);

            var ownership = _graph.TryGetOwnership(subject);
            if (ownership is null)
            {
                _graph.ReleaseClaim(subject);
                return;
            }

            if (ownership.IncomingCount == 0 || !_reachability.IsAnchorReachable(subject, null))
            {
                _release.ReleaseRoot(subject);
            }
        }
        catch (Exception operationFailure)
        {
            gate.DrainOnFailure(operationFailure);
            throw;
        }
        finally
        {
            gate.Dispose();
        }
    }

    /// <summary>
    /// Seeds and publishes a freshly claimed root's component. Runs under <see cref="_gate"/>.
    /// </summary>
    private void SeedAndAttachComponent(IInterceptorSubject subject)
    {
        _attach.AttachRoot(subject);
    }

    /// <summary>
    /// Validates the component the subject opens up and claims every unattached subject in it, with
    /// the requested anchor on the root. The claims and the anchor are the only things it writes,
    /// and <see cref="RollbackRejectedAttach"/> is what hands them back when the attach is refused
    /// after this point.
    /// </summary>
    private void ClaimComponentForRoot(IInterceptorSubject subject, SubjectAttachmentAnchorKind anchor, List<IInterceptorSubject> unattached)
    {
        var visited = LifecycleScratch.RentSubjectSet();
        try
        {
            _graph.DiscoverComponent(subject, visited, unattached);
            if (!_graph.TryClaimDiscovered(unattached, subject, anchor))
            {
                throw new InvalidOperationException(
                    "Another context claimed a subject of this graph while the attach was validating it.");
            }
        }
        finally
        {
            LifecycleScratch.Return(visited);
        }
    }

    #endregion

    #region Committed state queries

    // Internal for tests only: committed baselines have no public observer, and the
    // released-parent regression tests must assert that none survives a subject's release.
    internal OwnershipGraph Graph => _graph;

    internal bool IsPendingRelease(IInterceptorSubject subject)
    {
        return !_graph.IsOwned(subject) && _graph.IsReleasing(subject);
    }

    /// <summary>
    /// Asks whether there is an in-flight transaction to wait for, and registers work to run once it
    /// ends. Returns false when there is nothing in flight, in which case nothing was registered and
    /// the caller decides on the value it is holding. A null <paramref name="recalculation"/> asks
    /// the question without registering, for a caller whose earlier booking is still outstanding.
    /// </summary>
    /// <remarks>
    /// The question and the registration share one lock, and the transaction count is decremented
    /// before the drain takes that lock, so the two cannot both miss: a registration that lands
    /// before the drain's swap is drained, and one that lands after it reads a count of zero and is
    /// refused. That is also why every caller has to ask here rather than answering from state of
    /// its own. Nothing registered here runs under this lock, and the caller may hold its own lock
    /// while registering, so this stays a leaf.
    /// </remarks>
    internal bool TryRunWhenTransactionEnds(Action? recalculation)
    {
        lock (_withheldLock)
        {
            if (Volatile.Read(ref _transactionsInFlight) <= (_gate.IsHeldByCurrentThread ? 1 : 0))
            {
                return false;
            }

            if (recalculation is not null)
            {
                (_withheldRecalculations ??= []).Add(recalculation);
            }

            return true;
        }
    }

    /// <summary>
    /// Runs everything a reader deferred until this transaction ended. The gate is already released,
    /// so the work is free to take it again, and this lifecycle holds no lock while it runs.
    /// </summary>
    /// <remarks>
    /// Exceptions do not escape: this is reached from a <c>finally</c>, so a conviction that
    /// surfaces here would replace the exception of a transaction that is already failing, and the
    /// reader that produced the value has long returned. It is traced instead, and raised against a
    /// caller on the next evaluation with nothing in flight. Nothing schedules that evaluation, so
    /// this is best effort and not a guarantee: a derived property whose dependencies are written
    /// once at startup, or one orphaned by the last write of a batch, is reported only into the
    /// trace, which is silent unless a listener is configured.
    /// </remarks>
    private void RunWithheldRecalculations()
    {
        List<Action>? withheld;
        lock (_withheldLock)
        {
            withheld = _withheldRecalculations;
            _withheldRecalculations = null;
        }

        if (withheld is null)
        {
            return;
        }

        foreach (var recalculation in withheld)
        {
            try
            {
                recalculation();
            }
            catch (Exception exception)
            {
                Trace.TraceError(
                    "LifecycleInterceptor: a recalculation deferred until this topology transaction " +
                    $"ended failed with {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Gets the number of committed incoming edge occurrences, which is the subject's reference
    /// count. An anchored root with no edge reports zero, so this is not an attachment predicate.
    /// </summary>
    /// <remarks>Takes no lock: consumers call it from inside lifecycle callbacks and their own locks.</remarks>
    public int GetReferenceCount(IInterceptorSubject subject)
    {
        return _graph.TryGetOwnership(subject)?.IncomingCount ?? 0;
    }

    /// <summary>
    /// Gets the subject's occurrence-aware parents. The first call on a subject activates parent
    /// publication for it; a subject nobody asks about never allocates a snapshot.
    /// </summary>
    /// <remarks>Takes no lock; see <see cref="OwnershipGraph.GetParents"/> for why that is required.</remarks>
    public ImmutableArray<SubjectParent> GetParents(IInterceptorSubject subject)
    {
        return _graph.GetParents(subject);
    }

    #endregion
}
