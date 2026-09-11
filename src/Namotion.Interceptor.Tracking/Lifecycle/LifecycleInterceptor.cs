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

    // The runtime cannot identify what a gate holder is waiting for. Continuous blocked-state
    // observations are only a timeout heuristic; holder or transaction changes reset that window.
    // Sampling occurs only on the contended path, after a timed gate acquisition fails.
    private const int HolderSampleIntervalMilliseconds = 20;
    private const int DefaultBlockedHolderThresholdMilliseconds = 30_000;

    // Per instance rather than constant so a test can convict in milliseconds instead of spending
    // the full threshold per case. Per instance and not static, so tests running in parallel cannot
    // shorten each other's threshold. Read only by a thread already waiting on a contended gate.
    internal int BlockedHolderThresholdMilliseconds { get; set; } = DefaultBlockedHolderThresholdMilliseconds;

    // A total bound also covers holders that keep running or whose blocking is not observable.
    // Unlike the blocked window, it does not reset when the holder or transaction changes.
    private const int DefaultGateWaitTimeoutMilliseconds = 300_000;

    // Settable for the same reason as the threshold above, and more sharply: at its real size
    // no test can afford to reach it, so without this the bound would ship untested.
    internal int GateWaitTimeoutMilliseconds { get; set; } = DefaultGateWaitTimeoutMilliseconds;

    // The thread inside a topology transaction of this lifecycle, null when there is none. Written
    // under the gate and read without it, by a waiter that is diagnosing its own wait rather than
    // deciding anything: a stale read costs one sample of a window that needs all of them.
    private Thread? _gateHolder;

    // A waiter can miss both release and reacquisition by the same thread between samples.
    // Published before the holder so separate transactions cannot share one blocked window.
    private long _gateTransactionRevision;

    // How many topology gates the current thread holds, across every lifecycle. Gates have no
    // order among themselves, so a thread holding one and blocking on another deadlocks against a
    // thread taking them the other way round: a second transaction on a different lifecycle is
    // rejected instead of waiting.
    [ThreadStatic]
    private static int _heldGateCount;

    /// <summary>
    /// Reports a subject entering the object graph. Queued delivery can observe a later graph state.
    /// Handler failures propagate after pending notifications drain and do not roll back the attach.
    /// Handlers must be fast (invoked inside lock). Never hand structural
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
    /// Reports a subject leaving the object graph, before its ILifecycleHandler notifications.
    /// The executor retains its context through queued teardown. Ownership queries can observe
    /// a later reattachment rather than the historical detach described by this event.
    /// Handler failures propagate after pending notifications drain and do not roll back the detach.
    /// Handlers must be fast (invoked inside lock). Never hand structural
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
        _attach.Reconciler = _reconciler;
        _admission = new PropertyAdmission(_graph, _reconciler);
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
            Volatile.Write(ref _gateTransactionRevision, unchecked(_gateTransactionRevision + 1));
            Volatile.Write(ref _gateHolder, Thread.CurrentThread);
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
        if (--_heldGateCount == 0)
        {
            Volatile.Write(ref _gateHolder, null);
        }

        _gate.Exit();
    }

    // Its own method, and never inlined, so EnterGate keeps its stack frame: inlined, this method's
    // locals and the message building are set up on every successful acquisition too.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WaitForGate()
    {
        var deadline = Environment.TickCount64 + GateWaitTimeoutMilliseconds;
        var blockedWindow = new BlockedGateHolderWindow();

        while (true)
        {
            // Sampled before the wait rather than after it, so the window is the wait itself and a
            // holder that releases the gate during it is seen as gone on the next pass.
            var transactionRevision = Volatile.Read(ref _gateTransactionRevision);
            var holder = Volatile.Read(ref _gateHolder);
            var isBlocked = holder is not null &&
                (holder.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0;
            if (blockedWindow.Observe(holder, transactionRevision, isBlocked, Environment.TickCount64, BlockedHolderThresholdMilliseconds) &&
                transactionRevision == Volatile.Read(ref _gateTransactionRevision))
            {
                ThrowHolderBlocked(BlockedHolderThresholdMilliseconds);
            }

            if (_gate.TryEnter(HolderSampleIntervalMilliseconds))
            {
                return;
            }

            // End this wait even if no single holder met the blocked-state threshold.
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
            "This observation does not identify what the holder is waiting for. It may have " +
            "dispatched structural work to another thread and be waiting for that work, which " +
            "cannot acquire this gate until the enclosing operation returns. Never wait for " +
            "structural work on another thread from inside a structural write, lifecycle callback " +
            "or interceptor. Complete the enclosing operation first or hand off without waiting. " +
            "The holder may also be blocked on unrelated work. Nothing was read and nothing was changed " +
            "by this waiting operation; the holder was not aborted.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowGateWaitTimedOut(int timeoutMilliseconds)
    {
        throw new LifecycleContractViolationException(
            $"Timed out after {TimeSpan.FromMilliseconds(timeoutMilliseconds).TotalSeconds:0.##} seconds waiting for the topology " +
            "gate of this context. This total wait bound applies regardless of holder activity " +
            "or changes of holder and cannot diagnose the cause of contention. Nothing was read " +
            "and nothing was changed by this waiting operation; the holder was not aborted.");
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
    /// A terminal may normalize its input or create a new subject. If the stored component cannot
    /// be claimed, such as a subject owned by another context, reconciliation fails without
    /// committing new graph edges. The framework cannot restore arbitrary terminal storage, so
    /// the backing field retains the rejected value. Terminals emitted by the source generator
    /// store the proposed value and skip this second claim.
    /// </remarks>
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var property = context.Property;
        var metadata = property.Metadata;
        if (!metadata.IsStructural<TProperty>())
        {
            next(ref context);
            return;
        }

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
            if (_graph.IsReleasing(subject))
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

            registration.GetProperties();
            if (!ReferenceEquals(subject.Executor.AttachedContext, _context))
            {
                return false;
            }

            if (_graph.IsOwned(subject))
            {
                _admission.Admit(registration);
            }
            else
            {
                // A claimed subject may never be published, and a releasing subject has no
                // descent left. Only a future ownership entry may seed these new properties.
                registration.Publish();
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
            executor.TryGetAttachment(out var attachedContext, out var currentAnchor, out var rootAttachmentRevision);
            InterceptorSubjectExtensions.ValidateRootAnchor(attachedContext, currentAnchor, context, anchor);

            if (attachedContext is not null)
            {
                if (anchor == SubjectAttachmentAnchorKind.Provisional)
                {
                    return;
                }

                _graph.SetAnchor(subject, anchor);
                if (!_graph.IsReleasing(subject))
                {
                    // Anchoring reaches an already owned subject without recording an edge, so this
                    // is the one route that has to resume a failed seed itself.
                    _attach.ResumeFailedSeed(subject);
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
                executor.TryGetAttachment(out _, out _, out rootAttachmentRevision);
                _attach.AttachRoot(subject);
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
                    RollbackRejectedAttach(subject, anchor, rootAttachmentRevision, claimed, consumedAnchors);
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

    /// <summary>Removes a rejected root anchor and releases the component that loses its support.</summary>
    /// <remarks>
    /// Restore consumed provisional anchors before release so pre-existing roots keep their support.
    /// A root retained by an independently committed edge keeps its installed children and failed-seed
    /// state for retry; draining those children would invalidate that surviving ownership lifetime.
    /// </remarks>
    private void RollbackRejectedAttach(
        IInterceptorSubject subject,
        SubjectAttachmentAnchorKind anchor,
        long rootAttachmentRevision,
        List<IInterceptorSubject> claimed,
        List<(IInterceptorSubject Subject, long Revision)> consumedAnchors)
    {
        try
        {
            // A getter can detach and explicitly reattach this root, even while outside support
            // retains its ownership record. That newer anchor does not belong to this rollback.
            subject.Executor.TryGetAttachment(out _, out _, out var currentRootRevision);
            var ownsRootAnchor = currentRootRevision == rootAttachmentRevision;
            foreach (var (consumedSubject, revision) in consumedAnchors)
            {
                consumedSubject.Executor.TryUpdateAttachment(revision, _context, SubjectAttachmentAnchorKind.Provisional, out _);
            }

            if (ownsRootAnchor)
            {
                _graph.SetAnchor(subject, SubjectAttachmentAnchorKind.None);

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
    }

    /// <inheritdoc />
    public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
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
