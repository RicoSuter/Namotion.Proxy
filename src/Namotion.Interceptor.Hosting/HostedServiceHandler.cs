using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting;

[RunsAfter(typeof(ContextInheritanceHandler))]
internal sealed class HostedServiceHandler : IHostedService, ILifecycleHandler
{
    // A workaround, not a design choice, and now the stop body's alone: the start waits for a startup
    // scope instead. See docs/design/hosting-service-ownership.md#the-50-ms-delay.
    private const int TransitionDelayMilliseconds = 50;

    /// <summary>
    /// How long the drain waits between reads of the in flight count. Once per process, and every stop
    /// it waits for already spends <see cref="TransitionDelayMilliseconds"/> of its own.
    /// </summary>
    private const int DrainPollMilliseconds = 1;

    private readonly HostedServiceGate _gate = new();
    private readonly ConcurrentDictionary<HostedServiceTarget, IInterceptorSubject> _owned = new();
    // Reference equality, as everywhere else a subject is a key: a hand written subject may compare by
    // value, and two of those sharing one entry means detaching either one clears the other's liveness
    // while it is still in the graph, which its next start reads and declines on.
    private readonly ConcurrentDictionary<IInterceptorSubject, byte> _liveSubjects =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The startup scope open in the flow that appends a start, or null. Ambient because the flow that
    /// constructs a subject is the one that knows when it is configured, and the attach it triggers
    /// runs inside a property write with nothing to pass a scope through.
    /// </summary>
    /// <remarks>
    /// Cleared at the top of every transition body through <see cref="ClearAmbientStartupScope"/>, or a
    /// stop body would capture the scope of the flow that appended it for anything it attaches.
    /// </remarks>
    private readonly AsyncLocal<HostedServiceStartupScope?> _startupScope = new();

    internal HostedServiceStartupScope DeferStartup() => new(_startupScope);

    /// <summary>
    /// Drops the ambient scope for the calling flow, which for a transition body is its own copy.
    /// </summary>
    internal void ClearAmbientStartupScope() => _startupScope.Value = null;

    /// <summary>
    /// Transitions this handler appended that have not finished. Read by the drain rather than
    /// signalled: a completion source has to cope with the count already being zero when the drain
    /// starts, with a transient zero before the drain's own stops land, and with a store-load
    /// reordering on both sides. Re-reading has none of those cases because it re-reads.
    /// </summary>
    private int _inFlight;

    /// <summary>
    /// Set once, by the service provider factory that hands this handler to the host, on whichever
    /// thread first resolves the hosted services. Volatile because the readers are transition threads
    /// and attaching threads that have no ordering against that one, and a stale null here silently
    /// drops the errors this logger exists to report.
    /// </summary>
    private volatile ILogger? _logger;

    internal void SetLogger(ILogger logger) => _logger = logger;

    private ILogger? Logger => _logger;

    /// <summary>
    /// Test seam, awaited in <see cref="StopAsync"/> after the drain begins and before liveness is
    /// cleared. Null in production. A start appended while it is held sees a draining gate and a still
    /// live subject, which is the interleaving the start body's gate re-read exists for.
    /// </summary>
    internal Func<Task>? DrainGate { get; set; }

    /// <summary>
    /// Test seam, invoked after the take and before the gate re-read. Null in production. Holds open
    /// the window in which a handler that passed the gate read on entry owns a target it may never
    /// start.
    /// </summary>
    internal Action? OwnershipTakenGate { get; set; }

    /// <summary>
    /// Test seam, invoked inside <see cref="LifecycleInterceptor.TryRunWhileAttached"/>'s callback,
    /// between the membership answer and the liveness write. Null in production. Holding it holds the
    /// graph mutation lock, which is the property it makes observable.
    /// </summary>
    internal Action? LivenessWriteGate { get; set; }

    /// <summary>
    /// Test seam, invoked inside the chain lock between the liveness read and the ownership take. Null
    /// in production. Holds open the window in which a context detach reads the owner as null and
    /// releases nothing, so the take that follows is one no release reaches.
    /// </summary>
    internal Action? LivenessReadGate { get; set; }

    /// <summary>
    /// Test seam, awaited in <see cref="StopAsync"/> between the owned snapshot and the stops it
    /// appends. Null in production. Ownership moving while it is held is the interleaving the append's
    /// own ownership read exists for, and the only one that reaches it.
    /// </summary>
    internal Func<Task>? DrainAppendGate { get; set; }

    /// <summary>
    /// Test seam, awaited in <see cref="StopAsync"/> after the first wait for in flight transitions and
    /// before ownership is released. Null in production. A stop appended while it is held lands after
    /// the count first reached zero and while a context detach still reads this handler as the owner,
    /// which is the interleaving the second wait exists for.
    /// </summary>
    internal Func<Task>? DrainReleaseGate { get; set; }

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        // Runs inside LifecycleInterceptor's lock, so everything here only appends, which never blocks
        // and never runs a body. Third party code does run under that lock through TakeStartupHolds,
        // an accepted hazard with a constraint on the implementer: see IStartupCompletionDeferrer and
        // residual hazard 4 in docs/design/hosting-service-ownership.md.
        if (change.IsContextAttach)
        {
            AttachSubject(change.Subject);
        }
        else if (change.IsContextDetach)
        {
            DetachSubject(change.Subject);
        }
    }

    private void AttachSubject(IInterceptorSubject subject)
    {
        if (_gate.State is HostedServiceGateState.Draining or HostedServiceGateState.Drained)
        {
            return;
        }

        var subjectTarget = subject is IHostedService hostedService
            ? hostedService.GetOrAddSubjectTarget()
            : null;

        var attachments = subject.GetHostedServiceAttachments();
        if (subjectTarget is null && attachments.IsEmpty)
        {
            // The common case, and why liveness is recorded here rather than for every attaching
            // subject: every reader of liveness holds a target, so a subject with no target has no
            // reader. MarkLiveIfAttached covers the one moment that is not true.
            return;
        }

        _liveSubjects[subject] = 0;

        if (subjectTarget is not null)
        {
            TryTakeOwnershipAndStart(subject, subjectTarget);
        }

        foreach (var attachment in attachments)
        {
            TryTakeOwnershipAndStart(subject, ((IHostedServiceAttachmentTarget)attachment).Target);
        }

        if (_gate.State is HostedServiceGateState.Draining or HostedServiceGateState.Drained)
        {
            // Re-read after the write, for the same reason the target guard re-reads after its own:
            // a liveness entry that lands after the drain cleared the set roots the subject on a dead
            // handler for the rest of that handler's life.
            _liveSubjects.TryRemove(subject, out _);
        }
    }

    private void DetachSubject(IInterceptorSubject subject)
    {
        // Read before anything is allocated: a completion source per subject is 1.76 MB of garbage per
        // detach of a 20,000 subject graph, under the lifecycle lock. The type test stands in for a
        // second data lookup, since only AttachSubject creates a subject target and only for an
        // IHostedService.
        //
        // "Has ever hosted", not "hosts now": they differ for a subject whose last attachment was
        // detached while it stayed in the graph, and skipping the clear there leaves an entry that
        // outlives its membership, which a queued start reads and acts on.
        var everHosted = subject.TryGetHostedServiceAttachments(out var attachments);
        var subjectTarget = subject is IHostedService ? subject.TryGetSubjectTarget() : null;
        if (subjectTarget is null && !everHosted)
        {
            return;
        }

        // Liveness is per subject and cleared here, under the lifecycle lock. It cannot be per target:
        // one subject reachable from two hosting enabled contexts shares a single target with two
        // handlers, and both are live for it while only one of them owns it.
        _liveSubjects.TryRemove(subject, out _);

        if (subjectTarget is null && attachments.IsEmpty)
        {
            // Hosted something once, hosts nothing now. Liveness is gone, and there is no target left
            // to stop or release.
            return;
        }

        // A handler stops what it owns and nothing else, or it disposes an instance another handler
        // created and is running. Not readable from the transition body either, since ownership is
        // released just below and the body would always see a stranger.
        //
        // Appended now, never deferred into another transition: deferring disposes the instance a
        // re-attach created and leaks the one this detach meant to stop. Walkthrough in
        // docs/design/hosting-service-ownership.md#why-a-composite-transition-is-wrong.
        TaskCompletionSource? subjectStopped = null;
        if (subjectTarget is not null && ReferenceEquals(subjectTarget.Owner, this))
        {
            // Allocated only when there is an attachment to order behind it: the signal exists to hold
            // the attachment stops until the subject's own stop returned, and nothing else reads it.
            subjectStopped = attachments.IsEmpty
                ? null
                : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            AppendStop(subject, subjectTarget, subjectStopped, waitFor: null, CancellationToken.None);
        }

        foreach (var attachment in attachments)
        {
            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            if (ReferenceEquals(target.Owner, this))
            {
                // A null wait is the "nothing to order behind" case, which is what the subject target
                // being absent or owned by another handler means. The stop body skips it, so that
                // case allocates no completed task to await.
                AppendStop(subject, target, signal: null, waitFor: subjectStopped?.Task, CancellationToken.None);
            }
        }

        // Released after the stops are appended, and never from inside a transition body: releasing
        // from the body would clobber ownership a re-attach has already retaken, and the re-attach's
        // start would then no-op itself. Releasing a target this handler does not own is a no-op.
        subjectTarget?.ReleaseOwnership(this);
        foreach (var attachment in attachments)
        {
            ((IHostedServiceAttachmentTarget)attachment).Target.ReleaseOwnership(this);
        }
    }

    /// <summary>
    /// Takes ownership of the target for this handler and appends its start, returning the appended
    /// transition. Returns null when the handler took nothing, because the subject is no longer live
    /// for it, because another handler owns the target, or because this handler is draining.
    /// </summary>
    /// <remarks>
    /// The transition carries no cancellation token: a caller's token bounds its wait, never the
    /// transition, or cancelling an <c>AttachHostedServiceAsync</c> await would abort a start already
    /// under way and record it as a failure.
    /// </remarks>
    internal Task? TryTakeOwnershipAndStart(IInterceptorSubject subject, HostedServiceTarget target)
    {
        if (_gate.State is HostedServiceGateState.Draining or HostedServiceGateState.Drained)
        {
            // A draining or drained handler must not take ownership. Nothing it owns can ever start,
            // and a target left owned by a dead handler makes every future handler over that subject
            // lose the compare and exchange. Read here as well as after the append so the ordinary
            // drained case never installs an owner that a live handler could race against at all.
            return null;
        }

        // Taken here, synchronously, and not inside the body: appending never runs the body, so the
        // service is not running when the attach returns, and anything that treats "the graph has
        // finished starting" as a completion point would otherwise reach it while this start is still
        // queued. Taking the hold before the append leaves no window in which that can happen.
        var startupHolds = TakeStartupHolds(subject.Context);

        // Read in the appending flow, which is the one that opened it. The body runs later and, on the
        // paths where a caller does not await the attach, in a flow that has already moved on.
        var startupScope = _startupScope.Value;

        var start = target.TryTakeOwnershipAndAppendAsync(
            this,
            subject,
            () => RunStartAsync(subject, target, startupHolds, startupScope),
            out var ownershipTaken);

        if (start is null)
        {
            ReleaseStartupHolds(startupHolds);
            return null;
        }

        OwnershipTakenGate?.Invoke();

        if (ownershipTaken && _gate.State is HostedServiceGateState.Draining or HostedServiceGateState.Drained)
        {
            // Re-read after both writes, which turns the check at the top from a narrowing into a
            // guard: the record and the owner landed while the gate still read Running, so the drain's
            // snapshot covers this target. Any later read may already have been swept past.
            //
            // A stop rather than a plain retirement of the record, because the start appended just
            // above may already be past every one of its guards: it read the gate as Running before
            // BeginDraining and is committed to creating an instance. Retiring the record then hides
            // that instance from a snapshot taken afterwards and nothing ever stops or disposes it. The
            // stop is behind the start on the same chain, so it stops whatever the start creates.
            AppendStop(subject, target, signal: null, waitFor: null, CancellationToken.None);

            // Released after the stop is appended, never before, for the reason on the context detach
            // path. Outside the chain lock only because a draining handler installs nothing, so no take
            // of this handler's can be in flight for ReleaseOwnership to clobber, matching as it does
            // on the handler rather than on the take. The liveness equivalent has no such guarantee and
            // lives inside the chain lock.
            //
            // Only an ownership this call installed. An earlier attach's may be running, and undoing
            // that one would pull it out of the set the drain is about to stop.
            target.ReleaseOwnership(this);
        }

        return start;
    }

    private async Task RunStartAsync(
        IInterceptorSubject subject,
        HostedServiceTarget target,
        IDisposable[] startupHolds,
        HostedServiceStartupScope? startupScope)
    {
        try
        {
            await _gate.WaitForOpenAsync().ConfigureAwait(false);
            if (_gate.State != HostedServiceGateState.Running)
            {
                // Inside the body, never at append time: a start queued when shutdown begins has to
                // re-read, and a body skipped at append time would never run its signalling.
                return;
            }

            // Two windows, so neither condition is redundant: a detach clears liveness before it
            // releases ownership, so a body reading in between is refused by liveness alone, while one
            // appended after the detach finished is refused by ownership alone. No test separates them,
            // because holding a body in that window means holding the lifecycle lock.
            if (!_liveSubjects.ContainsKey(subject) || !ReferenceEquals(target.Owner, this))
            {
                return;
            }

            if (target.Current is not null)
            {
                // One instance per target, checked in the body where the chain serializes it. Ownership
                // stops a second handler, but a subject reachable from two hosting enabled contexts
                // raises one context attach per context, and the owning handler sees both: measured as
                // StartCount 2 without this guard.
                return;
            }

            if (startupScope is not null && !await WaitForConfigurationAsync(subject, target, startupScope).ConfigureAwait(false))
            {
                return;
            }

            // Cleared after every guard, never before: a start that is gated out or skipped must not
            // drop a fault that a caller has not read yet.
            target.SetFault(null);

            try
            {
                var instance = target.Subject ?? target.Factory!();
                if (target.IsHandlerOwnedInstance && !target.TryRecordFactoryInstance(instance))
                {
                    // Refused for every factory attachment, whatever the instance is, and recorded as
                    // a fault because that is the channel the caller already reads. Why it fails
                    // closed rather than only where the repeat would be a use after dispose is in
                    // docs/design/hosting-service-ownership.md#faults-and-failed-starts.
                    throw new InvalidOperationException(
                        "The hosted service factory returned the instance it returned last time. The handler owns " +
                        "every instance it creates and stops it when the subject leaves the graph, disposing it as " +
                        "well when it is disposable, so the factory must construct a new one on every call.");
                }

                try
                {
                    await instance.StartAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    if (target.IsHandlerOwnedInstance)
                    {
                        await DisposeInstanceAsync(instance).ConfigureAwait(false);
                    }

                    throw;
                }

                target.SetCurrent(instance);
            }
            catch (Exception exception)
            {
                target.SetFault(exception);
                Logger?.LogError(exception, "Failed to start hosted service for subject {Subject}.", subject);
            }
        }
        finally
        {
            // In a finally, so every way out releases.
            ReleaseStartupHolds(startupHolds);
        }
    }

    /// <summary>
    /// Waits for the startup scope this start was captured in and reports whether it may still run.
    /// </summary>
    /// <remarks>
    /// Every guard the caller read before this is re-read after it: a scope holds a start for as long
    /// as its flow stays open, and the subject can leave the graph in that time.
    /// <para>
    /// The drain releases the wait as well as the scope does. A start parked here is counted in flight,
    /// so a scope nobody disposes would otherwise hold the drain's barrier for the whole shutdown
    /// deadline.
    /// </para>
    /// </remarks>
    private async Task<bool> WaitForConfigurationAsync(
        IInterceptorSubject subject, HostedServiceTarget target, HostedServiceStartupScope startupScope)
    {
        if (!startupScope.IsReady)
        {
            await Task.WhenAny(
                    startupScope.WaitAsync(CancellationToken.None),
                    _gate.WaitForDrainingAsync())
                .ConfigureAwait(false);
        }

        return _gate.State == HostedServiceGateState.Running
            && _liveSubjects.ContainsKey(subject)
            && ReferenceEquals(target.Owner, this)
            && target.Current is null;
    }

    /// <summary>
    /// Takes a completion hold on every deferrer reachable from <paramref name="context"/>.
    /// </summary>
    /// <remarks>
    /// Empty for an application with no deferring subsystem, the common case. The constraint this call
    /// site puts on an implementer is on <see cref="IStartupCompletionDeferrer"/>.
    /// </remarks>
    private IDisposable[] TakeStartupHolds(IInterceptorSubjectContext context)
    {
        var deferrers = context.GetServices<IStartupCompletionDeferrer>();
        if (deferrers.IsEmpty)
        {
            return [];
        }

        var holds = new IDisposable[deferrers.Length];
        var taken = 0;
        foreach (var deferrer in deferrers)
        {
            try
            {
                holds[taken] = deferrer.DeferCompletion();
                taken++;
            }
            catch (Exception exception)
            {
                // One deferrer throwing must not abandon the holds already taken, and must not
                // propagate: an attach runs under the lifecycle lock inside a property write, so the
                // exception would surface at an unrelated assignment.
                Logger?.LogError(exception, "Taking a startup completion hold threw and was ignored.");
            }
        }

        return taken == holds.Length ? holds : holds[..taken];
    }

    private void ReleaseStartupHolds(IDisposable[] startupHolds)
    {
        foreach (var hold in startupHolds)
        {
            try
            {
                hold.Dispose();
            }
            catch (Exception exception)
            {
                // One deferrer throwing must not strand the others, for the same reason the release
                // sits in a finally at all.
                Logger?.LogError(exception, "Releasing a startup completion hold threw and was ignored.");
            }
        }
    }

    internal Task AppendStop(
        IInterceptorSubject subject,
        HostedServiceTarget target,
        TaskCompletionSource? signal,
        Task? waitFor,
        CancellationToken cancellationToken)
        => target.AppendAsync(this, CreateStopBody(subject, target, signal, waitFor, cancellationToken));

    /// <summary>
    /// Appends a stop only while this handler still owns the target, the two decided under one
    /// acquisition of the chain lock. Returns null when the append was refused, which the caller must
    /// not then hand to another stop as a wait: a refused stop has no body, so nothing sets its signal.
    /// </summary>
    private Task? AppendStopIfOwned(
        IInterceptorSubject subject,
        HostedServiceTarget target,
        TaskCompletionSource? signal,
        Task? waitFor,
        CancellationToken cancellationToken)
        => target.AppendIfOwnedAsync(this, CreateStopBody(subject, target, signal, waitFor, cancellationToken));

    private Func<Task> CreateStopBody(
        IInterceptorSubject subject,
        HostedServiceTarget target,
        TaskCompletionSource? signal,
        Task? waitFor,
        CancellationToken cancellationToken)
        => async () =>
        {
            try
            {
                if (waitFor is not null)
                {
                    // Orders a subject's stop ahead of its attachments. Acyclic: the subject's chain
                    // waits on nothing. A hosted service must therefore not detach an attachment from
                    // inside its own stop path, or this becomes a cycle.
                    await waitFor.ConfigureAwait(false);
                }

                // Waited for, but not read: a stop runs at every state, Drained included, because one
                // appended after the drain snapshotted everything would otherwise never stop and never
                // dispose its instance. The null check below is what makes a stop idempotent.
                await _gate.WaitForOpenAsync().ConfigureAwait(false);

                var instance = target.Current;
                if (instance is null)
                {
                    return;
                }

                target.SetCurrent(null);

                await Task.Delay(TransitionDelayMilliseconds, CancellationToken.None).ConfigureAwait(false);

                try
                {
                    await instance.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Caught rather than filtered out, and not recorded as a fault: a cancelled stop
                    // is the caller's token expiring, not a failure, but the dispose below still has
                    // to run. Current is already cleared, so an instance that escaped here would be
                    // unreachable and never disposed, which is the ordinary ShutdownTimeout path.
                }
                catch (Exception exception)
                {
                    target.SetFault(exception);
                    Logger?.LogError(exception, "Failed to stop hosted service for subject {Subject}.", subject);
                }

                if (target.IsHandlerOwnedInstance)
                {
                    await DisposeInstanceAsync(instance).ConfigureAwait(false);
                }
            }
            finally
            {
                // Always signals, including on the gated-out and cancelled paths, or a paired
                // attachment stop parks forever on a signal that is never set.
                signal?.TrySetResult();
            }
        };

    private async Task DisposeInstanceAsync(IHostedService instance)
    {
        try
        {
            switch (instance)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch (Exception exception)
        {
            // Contains as well as reports, and the containment matters on one path: the cleanup dispose
            // in RunStartAsync runs inside a catch that rethrows the start's own exception afterwards.
            // An escape from here skips that rethrow, so the caller waiting on the attach is told the
            // dispose failed and never learns why the start did.
            Logger?.LogError(exception, "Failed to dispose hosted service {Service}.", instance.ToString());
        }
    }

    /// <summary>
    /// Opens the startup gate if it has never been opened. A one way ratchet: it does nothing once the
    /// handler is draining or drained.
    /// </summary>
    internal void EnsureStarted() => _gate.EnsureStarted();

    /// <summary>
    /// Waits for the start this handler appended for the subject and rethrows the fault it recorded,
    /// so a subject that fails to start aborts host startup the way <c>AddHostedService</c> does.
    /// Returns false when nothing was started, which the caller must not read as a start.
    /// </summary>
    internal async Task<bool> WaitForStartAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        // Reads the target and never creates one, and never takes ownership: this handler either
        // already claimed the target in AttachSubject or has no business claiming it. A drained
        // handler releases only what its own drain snapshotted, so a claim taken here would never be
        // released and the next handler over the same subject would lose the compare and exchange
        // forever. The liveness read is the same guard the attach paths use, and it is what excludes
        // a draining or drained handler, which has no start queued and never will have one.
        var target = subject.TryGetSubjectTarget();
        if (target is null || !_liveSubjects.ContainsKey(subject) || !ReferenceEquals(target.Owner, this))
        {
            return false;
        }

        // An empty transition on the same chain. Appending never runs a body, so this completes only
        // once the start appended ahead of it has run.
        await target
            .AppendAsync(this, () => Task.CompletedTask)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        if (target.Fault is { } fault)
        {
            // Captured rather than rethrown, for the reason on AttachHostedServiceAsync: this is the
            // exception a failing subject aborts host startup with, so it is the one users read.
            ExceptionDispatchInfo.Capture(fault).Throw();
        }

        // Read after the fault, and read at all because the guards above cannot cover a drain that
        // begins while this wait is queued behind the start: the start body then gates itself out and
        // sets nothing, and only the start body ever sets Current.
        return target.Current is not null;
    }

    internal bool IsLive(IInterceptorSubject subject) => _liveSubjects.ContainsKey(subject);

    /// <summary>
    /// Records a target this handler installed itself as the owner of, so its drain can stop and
    /// release it. Written from the take, on the install only: a repeat take finds a record an earlier
    /// take made, and undoing that one would pull a running instance out of the drain's snapshot.
    /// </summary>
    internal void RecordOwnership(HostedServiceTarget target, IInterceptorSubject subject) => _owned[target] = subject;

    /// <summary>
    /// Retires a target's record. Called by the release, and by an explicit detach, which stops a
    /// target without releasing it and inherits the rule that it must still retire the record.
    /// </summary>
    internal void ForgetOwnership(HostedServiceTarget target) => _owned.TryRemove(target, out _);

    internal void EnterTransition() => Interlocked.Increment(ref _inFlight);

    internal void LeaveTransition() => Interlocked.Decrement(ref _inFlight);

    /// <summary>
    /// How many transitions this handler has appended that have not finished. Test only.
    /// </summary>
    internal int InFlightTransitionCount => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Whether this handler holds the target in the set its drain would stop. Test only: a target left
    /// here for a subject that has left the graph is the leak, not an observable behaviour.
    /// </summary>
    internal bool IsOwned(HostedServiceTarget target) => _owned.ContainsKey(target);

    /// <summary>
    /// Records liveness for a subject already in the graph that hosted nothing when it entered, so
    /// <c>AttachSubject</c> recorded none. The one moment the answer cannot be taken from a target.
    /// </summary>
    /// <remarks>
    /// The write happens inside <see cref="LifecycleInterceptor.TryRunWhileAttached"/>'s callback, not
    /// after it: reading membership and then writing releases the lock in between, and a graph move
    /// landing in that gap makes the write land on the opposite answer.
    /// <para>
    /// Only the write needs that, not the take after it, which reads liveness under the chain lock and
    /// refuses on its own. Keeping the take outside also keeps this off the list of places that run an
    /// <see cref="IStartupCompletionDeferrer"/> under the graph lock.
    /// </para>
    /// <para>
    /// Every reachable interceptor is asked, because one not holding the subject says nothing about
    /// another. A handler can therefore be marked live on the strength of a graph it does not serve;
    /// see the multi context note in docs/design/hosting-service-ownership.md.
    /// </para>
    /// </remarks>
    internal void MarkLiveIfAttached(IInterceptorSubject subject)
    {
        if (_gate.State is HostedServiceGateState.Draining or HostedServiceGateState.Drained)
        {
            return;
        }

        var interceptors = subject.Context.GetServices<LifecycleInterceptor>();

        // Hoisted, so several interceptors cost one delegate rather than one each.
        var record = () =>
        {
            LivenessWriteGate?.Invoke();
            _liveSubjects[subject] = 0;
        };

        var recorded = false;
        for (var index = 0; index < interceptors.Length && !recorded; index++)
        {
            recorded = interceptors[index].TryRunWhileAttached(subject, record);
        }

        if (recorded && _gate.State is HostedServiceGateState.Draining or HostedServiceGateState.Drained)
        {
            // Re-read after the write, for the same reason AttachSubject re-reads after its own: an
            // entry that lands after the drain cleared the set roots the subject on a dead handler for
            // the rest of that handler's life.
            _liveSubjects.TryRemove(subject, out _);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureStarted();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _gate.BeginDraining();

        if (DrainGate is { } drainGate)
        {
            await drainGate().ConfigureAwait(false);
        }

        // Liveness ends where the drain begins. A handler that still reports a subject as live claims
        // ownership of every attachment added to it afterwards, appends a start that no-ops, and never
        // releases it, because the release loop below only covers the drain's own snapshot.
        _liveSubjects.Clear();

        var snapshot = _owned.ToArray();

        if (DrainAppendGate is { } drainAppendGate)
        {
            await drainAppendGate().ConfigureAwait(false);
        }

        // Shutdown uses the same shape per owned subject as a context detach does, rather than
        // stopping every target independently: a subject's own stop has to return before the
        // attachments it uses are stopped and disposed underneath it.
        Dictionary<IInterceptorSubject, TaskCompletionSource>? subjectStops = null;
        foreach (var (target, subject) in snapshot)
        {
            if (target.Subject is null)
            {
                continue;
            }

            var subjectStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (AppendStopIfOwned(subject, target, subjectStopped, waitFor: null, cancellationToken) is null)
            {
                continue;
            }

            // Recorded only for an accepted append. A refused one has no body and therefore no finally,
            // so an attachment stop handed this signal would park on it for the whole shutdown deadline.
            (subjectStops ??= new Dictionary<IInterceptorSubject, TaskCompletionSource>(ReferenceEqualityComparer.Instance))[subject] = subjectStopped;
        }

        foreach (var (target, subject) in snapshot)
        {
            if (target.Subject is not null)
            {
                continue;
            }

            var waitFor = subjectStops is not null && subjectStops.TryGetValue(subject, out var subjectStopped)
                ? subjectStopped.Task
                : null;

            // Discarded rather than collected: the count is what the drain waits on, and a stop this
            // append refused is one another handler now owns.
            _ = AppendStopIfOwned(subject, target, signal: null, waitFor, cancellationToken);
        }

        // Bounded by the token, which for a host is the shutdown deadline. Nothing further down
        // observes it: the chain waits inside a stop body are untokened by design, and a stop that is
        // wedged behind one of them would otherwise hold the process open forever. A service that
        // ignores its own stop token, or a chain wedged by the forbidden self detach shape, is exactly
        // what this barrier has to give up on.
        var remaining = await WaitForTransitionsAsync(cancellationToken).ConfigureAwait(false);

        if (DrainReleaseGate is { } drainReleaseGate)
        {
            await drainReleaseGate().ConfigureAwait(false);
        }

        foreach (var (target, _) in snapshot)
        {
            // Released after the stops so a second host cannot start ahead of this host's stop, and
            // released at all so a second host over the same subjects is not blocked forever. Released
            // even when the wait above gave up, because a wrong stop is recoverable and a target owned
            // by a dead handler is not.
            target.ReleaseOwnership(this);
        }

        if (remaining == 0)
        {
            // Read again rather than held: an append landing after the count first reached zero went
            // through the same increment, and only a second read sees it. Past the release above a
            // context detach appends nothing for this handler, because it reads Owner and finds a
            // stranger, so one more round is the last that path can need.
            remaining = await WaitForTransitionsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (remaining != 0)
        {
            // Logged rather than thrown, and the release above still ran: rethrowing would abandon it,
            // so every target this handler owns would stay owned by a dead handler and a second host
            // over the same subjects would start nothing. The host treats an exception here as a failed
            // shutdown and disposes the provider anyway, so there is nothing to gain.
            Logger?.LogWarning(
                "Shutdown gave up waiting for {Count} hosted service transitions; they keep running unobserved.",
                remaining);
        }

        // The owned set is not cleared here. After the release loop the only entries left are installs
        // whose own gate re-read releases them, and clearing is what would make the set and the owner
        // field disagree for anything still in flight.
        _gate.CompleteDraining();
    }

    /// <summary>
    /// Waits for every transition this handler appended to finish, and reports how many were still
    /// running when <paramref name="cancellationToken"/> expired. Zero means the wait completed.
    /// </summary>
    private async Task<int> WaitForTransitionsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var remaining = Volatile.Read(ref _inFlight);
            if (remaining == 0)
            {
                return 0;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return remaining;
            }

            // Untokened, so every round reads the count and the deadline in the same order. The cost of
            // noticing an expired deadline late is one poll.
            await Task.Delay(DrainPollMilliseconds, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
