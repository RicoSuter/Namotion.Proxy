using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Abstract base for source classes that owns the entire pump lifecycle
/// (buffer -> listen -> load initial state -> run change queue processor -> retry on failure).
/// Derived classes override three hooks to plug in protocol-specific behavior:
/// <see cref="StartListeningAsync"/> (protected), <see cref="LoadInitialStateAsync"/> (public),
/// and <see cref="WriteChangesAsync"/> (public).
/// </summary>
public abstract class SubjectSourceBase : SubjectConnectorBase, ISubjectSource
{
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;

    // A source we talk to over a wire: what it hands us was produced before it saw our write, so it
    // cannot rank against our commits. Named once because the processor and the reconcile
    // must agree; if only one ranked against the last commit, the other would still deliver an older one.
    private const ChangeDeliveryRule DeliveryRule = ChangeDeliveryRule.SourceValuesMayBeStale;
    private readonly TimeSpan _retryTime;
    private readonly SubjectPropertyWriter _propertyWriter;

    private static readonly TimeSpan ConnectWindowDrainInterval = TimeSpan.FromSeconds(1);

    private readonly Lock _stateLock = new();

    // The state and its two timestamps are swapped as one value: in separate fields a reader can see
    // the new state beside the previous timestamp, and LastSynchronizedAt has to be visible before
    // State becomes Stopped.
    private sealed record SourceStateSnapshot(
        SourceState State, DateTimeOffset ChangeTime, DateTimeOffset? LastSynchronizedAt);

    private SourceStateSnapshot _stateSnapshot = new(SourceState.Synchronizing, DateTimeOffset.UtcNow, null);
    private int _started;

    private ImmutableArray<SourceMonitor> _registeredMonitors = [];

    // The resume gate: even while outbound delivery is open, odd while a resume holds it, and its value
    // is that resume's epoch. The retry queue must not be flushed while it is held, because the peer's
    // state has not landed yet, so a parked write would be sent against a view the reconcile has not
    // judged it against and an older value can win over a newer one.
    //
    // State and owner are one field so that BeginResume and TryEndResume each move the gate in a single
    // atomic step; see TryEndResume for what splitting them costs. Every write moves the field to a
    // strictly higher value (BeginResume to the next odd one, TryEndResume to the following even one),
    // so an epoch compares equal only while it still owns the gate, and no epoch is handed out twice
    // until the counter wraps 2^31 resumes later. Parity survives that wrap, so the held/open reading
    // stays correct across it.
    private int _resumeGate;

    // Test seam for the window between the retry-queue enqueue and the second gate read in
    // WriteChangesViaRetryQueueAsync, which has no externally observable synchronization point: a test
    // can clear the gate from inside this hook to land exactly in that window. Always null in
    // production.
    internal Action? AfterResumeGateObserved { get; set; }

    // Whether outbound delivery is currently suspended. Nearly the only place that decodes the gate,
    // so a test can assert on the gate itself rather than on a write's observable fate.
    internal bool IsResumeGateHeld => (Volatile.Read(ref _resumeGate) & 1) == 1;

    internal WriteRetryQueue WriteRetryQueue { get; }

    protected SubjectSourceBase(
        IInterceptorSubjectContext context,
        ILogger logger,
        TimeSpan? bufferTime = null,
        TimeSpan? retryTime = null,
        int writeRetryQueueSize = 1000,
        ThroughputCounter? incomingThroughput = null,
        ThroughputCounter? outgoingThroughput = null)
        : this(context, logger, bufferTime, retryTime, writeRetryQueueSize,
            new SourceMetrics(incomingThroughput, outgoingThroughput))
    {
    }

    // A constructor initializer cannot reference this, so the metrics instance is threaded through
    // here to reach both base(...) and the narrowed Metrics property as the same object.
    private SubjectSourceBase(
        IInterceptorSubjectContext context,
        ILogger logger,
        TimeSpan? bufferTime,
        TimeSpan? retryTime,
        int writeRetryQueueSize,
        SourceMetrics metrics)
        : base(metrics)
    {
        Metrics = metrics;
        Diagnostics = new SourceDiagnostics(metrics);

        _context = context;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
        _retryTime = retryTime ?? TimeSpan.FromSeconds(10);
        ArgumentOutOfRangeException.ThrowIfNegative(writeRetryQueueSize);

        WriteRetryQueue = new WriteRetryQueue(writeRetryQueueSize, logger, metrics.OutboundRetries);

        // The registration lives as long as the source, and the queue count stays readable after
        // the queue itself is disposed.
        _ = metrics.OutboundRetries.Register(
            () => WriteRetryQueue.PendingWriteCount, capacity: writeRetryQueueSize);

        _propertyWriter = new SubjectPropertyWriter(this, logger, metrics.InboundBuffer);
    }

    /// <summary>
    /// Gets the write side of this source's diagnostics, narrowed to <see cref="SourceMetrics"/>.
    /// </summary>
    /// <remarks>
    /// A derived source must not register on <see cref="Diagnostics.ConnectorMetrics.OutboundChanges"/>,
    /// <see cref="SourceMetrics.OutboundRetries"/> or <see cref="SourceMetrics.InboundBuffer"/>: this
    /// base owns all three, and a second live registration makes
    /// <see cref="Diagnostics.QueueMetrics.Register"/> throw.
    /// </remarks>
    protected new SourceMetrics Metrics { get; }

    /// <summary>
    /// Gets what this source reports about its transport and its buffers.
    /// </summary>
    public override SourceDiagnostics Diagnostics { get; }

    /// <inheritdoc cref="ISubjectSource.WriteBatchSize" />
    public virtual int WriteBatchSize => 0;

    /// <summary>
    /// Initializes the source and starts listening for external changes.
    /// </summary>
    /// <param name="propertyWriter">The writer to use for applying inbound property updates to the subject.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// An async disposable that can be used to stop listening for changes,
    /// or <c>null</c> if there is nothing to dispose.
    /// </returns>
    protected abstract Task<IAsyncDisposable?> StartListeningAsync(
        SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken);

    /// <inheritdoc />
    public abstract Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken);

    /// <inheritdoc />
    public abstract ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken);

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Stopped is terminal, but the platform won't enforce it: BackgroundService.StartAsync
        // creates a fresh CancellationTokenSource each call, so a second StartAsync would run
        // ExecuteAsync again against an uncancelled token. Without this guard, a "restarted" source
        // would claim, load and apply live values while State stayed Stopped.
        if (State == SourceState.Stopped)
        {
            _logger.LogWarning(
                "Source {Source} was stopped and cannot be restarted. Create a new instance instead.",
                GetType().Name);
            return Task.CompletedTask;
        }

        // A source registered in DI AND attached to the subject graph is started down both paths.
        // Without this latch both run a pump: the first to exit latches Stopped in its finally while
        // the second is still applying live values.
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            _logger.LogWarning(
                "Source {Source} is already started and the duplicate start was ignored. It is most " +
                "likely both registered in DI and attached to the subject graph; use one or the other.",
                GetType().Name);
            return Task.CompletedTask;
        }

        // Registration precedes the pump so SourceRegistered precedes any StateChanged of this source.
        var monitors = RootSubject.Context.GetSourceMonitors();
        ImmutableInterlocked.InterlockedExchange(ref _registeredMonitors, monitors);
        try
        {
            foreach (var monitor in monitors)
            {
                monitor.Register(this);
            }
        }
        catch
        {
            // Read before the transition below sets it, so Stopped still means Dispose, whose own
            // unwind may have found nothing registered yet.
            if (State == SourceState.Stopped)
            {
                UnwindRegistrations(monitors);
                throw;
            }

            // This source will never pump. Reporting a stop keeps it in scope as never-synchronized,
            // so in-scope waits answer Incomplete; unwinding left them on a vacuous Synchronized.
            // Nothing unregisters it until Dispose, which a graph-attached source may never get.
            WriteRetryQueue.Retire();
            TransitionStateTo(SourceState.Stopped);
            throw;
        }

        // Dispose can interleave with the registration above, and Stopped is terminal, so seeing it
        // here means Dispose already ran.
        if (State == SourceState.Stopped)
        {
            UnwindRegistrations(monitors);
            return Task.CompletedTask;
        }

        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Drops the registrations <see cref="StartAsync"/> just made, through its LOCAL array rather
    /// than the field, which a concurrent <see cref="Dispose"/> may already have emptied: re-reading
    /// it would strand them. Unregister no-ops on an unregistered source, so a double unwind is safe.
    /// </summary>
    private void UnwindRegistrations(ImmutableArray<SourceMonitor> monitors)
    {
        ImmutableInterlocked.InterlockedExchange(ref _registeredMonitors, ImmutableArray<SourceMonitor>.Empty);
        foreach (var monitor in monitors)
        {
            monitor.Unregister(this);
        }
    }

    /// <inheritdoc />
    protected sealed override async Task RunAsync(CancellationToken stoppingToken)
    {
        // Inside the try, so the finally below still publishes Stopped when startup fails. Outside it, a
        // configuration error leaves the source registered as Synchronizing for the process lifetime:
        // the DI path tears the host down, but on the graph-attach path the faulted task is swallowed and
        // every WaitForSynchronizationAsync on that branch blocks until its caller's token fires. A silent
        // hang in place of the loud failure the guard exists to give.
        try
        {
            // A missing PropertyChangeInterceptor means the source can capture no writes: a configuration
            // error, so fail fast with an actionable message instead of running silently inert. Detect it
            // precisely (null-check, not catch-all) so unrelated failures surface with their own diagnosis.
            if (_context.TryGetService<PropertyChangeInterceptor>() is null)
            {
                throw new InvalidOperationException(
                    "Cannot start source: no PropertyChangeInterceptor is registered in the interceptor context. " +
                    "Add WithPropertyChangeSubscriptions() or WithFullPropertyTracking() to the context configuration.");
            }

            // Source-lifetime capture: one subscription for the whole source, so writes are captured
            // continuously (including during the retry delay) and never fall into a no-subscription gap.
            using var subscription = _context.CreatePropertyChangeQueueSubscription();

            var firstAttempt = true;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!firstAttempt)
                    {
                        await Task.Delay(_retryTime, stoppingToken).ConfigureAwait(false);
                    }
                    firstAttempt = false;

                    // Park writes captured since the previous attempt (retry delay + any failed attempt).
                    // This also caps memory across repeated failed attempts.
                    DrainOwnedWritesToRetryQueue(subscription);

                    // Closes the connect window against an abandoned teardown flush from the previous
                    // attempt, which can otherwise still be writing when this one starts and would send
                    // pre-load values on the new connection.
                    var resumeEpoch = BeginResume();

                    _propertyWriter.StartBuffering();
                    await using var listenLifetime = await StartListeningAsync(_propertyWriter, stoppingToken).ConfigureAwait(false);

                    // Caps the window's memory at the retry queue's size; the subscription itself is
                    // unbounded. Starts only after StartListeningAsync, because ownership is established in
                    // there and the drain discards what it cannot attribute, which leaves the longer leg
                    // unguarded rather than the shorter one: for OPC UA the browse runs inside that call and
                    // takes minutes, while the load this wraps is a batched read. Covering it needs an
                    // ownership-neutral accumulator and an eviction contract to go with it.
                    using (var windowDrain = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                    {
                        var windowDrainTask = DrainConnectWindowPeriodicallyAsync(subscription, windowDrain.Token);
                        try
                        {
                            await _propertyWriter.LoadInitialStateAndResumeAsync(stoppingToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            // Awaited before the drain below runs, so the subscription keeps a single consumer.
                            await windowDrain.CancelAsync().ConfigureAwait(false);
                            await windowDrainTask.ConfigureAwait(false);
                        }
                    }

                    // Park connect-window writes captured during listen/load.
                    DrainOwnedWritesToRetryQueue(subscription);

                    // Single reconcile point: send (model already holds it), restore (the load moved the
                    // model off it), drop (a later local write supersedes it).
                    await CompleteResumeAsync(resumeEpoch, stoppingToken).ConfigureAwait(false);

                    // Connected phase reuses the source-lifetime subscription and does not own it.
                    using var processor = new ChangeQueueProcessor(
                        this,
                        subscription,
                        propertyReference => propertyReference.TryGetSource(out var source) && source == this,
                        WriteChangesViaRetryQueueAsync,
                        DeliveryRule,
                        _bufferTime,
                        maxQueueDepth: null,
                        logger: _logger,
                        dropHandler: Metrics.OutboundChanges.CreateDropReporter(),
                        writeHandlerOwnsChanges: true,
                        terminalHandler: () =>
                        {
                            if (stoppingToken.IsCancellationRequested)
                            {
                                WriteRetryQueue.Retire();
                            }
                        },
                        completionHandler: async teardownToken =>
                        {
                            await WriteRetryQueue.FlushAsync(this, teardownToken).ConfigureAwait(false);
                        });

                    // Declared after the processor so it is released first, which is what lets the
                    // retry loop's next attempt register its own: a second Register while one is
                    // still live throws. Drops are reported into the lifetime-owned metrics directly,
                    // so releasing this depth provider cannot lose them.
                    using var outboundRegistration = Metrics.OutboundChanges.Register(
                        () => processor.QueueDepth, capacity: null);

                    // Deliberately not a using: StopIdleFlushAsync can return while the drain is still
                    // running, and it owns the disposal for that reason.
                    var idleFlush = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var idleFlushTask = FlushRetryQueuePeriodicallyAsync(idleFlush.Token);
                    try
                    {
                        await processor.ProcessAsync(stoppingToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        await StopIdleFlushAsync(idleFlush, idleFlushTask).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // The base class only sees exceptions that leave RunAsync, and this loop swallows
                    // every per-attempt failure, so a source that can never connect would otherwise
                    // report no error at all. Guarded because the clause above covers only the
                    // cancellation, while a stop tears the connection down mid-connect with an
                    // arbitrary exception: recording that would overwrite the genuine fault for good,
                    // since LastError is sticky and a stopped source never restarts.
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        Metrics.ReportError(ex);
                    }

                    // Whatever it reported before the failure, the source is no longer serving the model.
                    TransitionStateTo(SourceState.Synchronizing);
                    _logger.LogError(ex, "Failed to listen for changes in source.");
                    // The next iteration delays before reconnecting, with the subscription still capturing.
                }
            }
        }
        finally
        {
            WriteRetryQueue.Retire();
            TransitionStateTo(SourceState.Stopped);
        }
    }

    /// <summary>
    /// Parks owned writes into the retry queue at intervals while the initial state loads, so a slow
    /// load cannot grow the subscription without bound. Collapsed per property like every other drain,
    /// so a property written repeatedly costs one slot rather than one per write.
    /// </summary>
    private async Task DrainConnectWindowPeriodicallyAsync(
        PropertyChangeQueueSubscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(ConnectWindowDrainInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                DrainOwnedWritesToRetryQueue(subscription);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the load finished, or the source is stopping.
        }
    }

    internal void DrainOwnedWritesToRetryQueue(PropertyChangeQueueSubscription subscription)
    {
        List<SubjectPropertyChange>? owned = null;
        while (subscription.TryDequeueImmediate(out var change))
        {
            if (ReferenceEquals(change.Origin.Source, this) && !ChangeDeliveryFilter.NeedsWriteBack(in change))
            {
                // This source's own applies (inbound / source-tagged). The exception is a transaction
                // confirmation on a property a connector has written out, which has to reach the source
                // to repair it; skipping it here would discard the repair for the whole connect window.
                continue;
            }

            if (!(change.Property.TryGetSource(out var source) && source == this))
            {
                continue; // not owned by this source
            }

            (owned ??= []).Add(change);
        }

        if (owned is not null)
        {
            // Collapsed before parking, not only at reconcile time. The queue is a bounded ring buffer
            // that drops its oldest entries, so parking raw changes lets a burst on one property evict
            // other properties' window writes before the reconcile ever sees them. Collapsing first
            // makes the space this costs proportional to the number of properties written rather than
            // to the number of writes.
            WriteRetryQueue.Enqueue(CollapsePerProperty(owned.ToArray()).ToArray());
        }
    }

    /// <summary>
    /// Collapses parked changes to one per property, keeping the oldest old value and the new value
    /// of the highest-revision commit.
    /// </summary>
    /// <remarks>
    /// Reconciliation classifies each change against the live value and mutates that value when it
    /// restores, so two writes to one property have to be judged as one. Left separate, an older
    /// write can match the live value, get restored, and thereby make the newer write look diverged,
    /// which drops it: the older write would win over the newer one.
    /// <para>
    /// Which one is newer is decided by <see cref="SubjectPropertyChange.Revision"/>, not by capture
    /// order. Changes are enqueued after their commit and outside the subject lock, so under
    /// concurrent writers arrival order is a race order. Both changes are writes to the same
    /// property and therefore to the same subject, so their revisions are comparable. A change
    /// carrying revision 0 was built outside a terminal write and orders against nothing, so
    /// capture order decides between those and the survivor carries no revision either, matching
    /// the flush-path collapse in <c>ChangeMerger</c> on unordered changes. The two still differ on which
    /// old value survives when every revision is ordered, which the delivery contract calls best effort.
    /// </para>
    /// </remarks>
    private static List<SubjectPropertyChange> CollapsePerProperty(SubjectPropertyChange[] changes)
    {
        var collapsed = new List<SubjectPropertyChange>(changes.Length);
        var indices = new Dictionary<PropertyReference, int>(changes.Length, PropertyReference.Comparer);

        foreach (var change in changes)
        {
            if (!indices.TryGetValue(change.Property, out var index))
            {
                indices[change.Property] = collapsed.Count;
                collapsed.Add(change);
                continue;
            }

            var kept = collapsed[index];
            collapsed[index] = change.Revision == 0 || kept.Revision == 0
                // One of them orders against nothing, so capture order decides and the survivor carries
                // no revision either. Same rule as the flush-path collapse: keeping a revision here would
                // let the survivor be ranked against the property marker and dropped, on a comparison
                // against a value it was not ordered by.
                ? kept.MergeWithNewer(change).WithoutRevision()
                : change.Revision < kept.Revision
                    ? change.MergeWithNewer(kept)
                    : kept.MergeWithNewer(change);
        }

        return collapsed;
    }

    /// <summary>
    /// Parks the batch while a resume holds the gate, and otherwise hands it to the retry queue's own
    /// owned-write path.
    /// </summary>
    /// <remarks>
    /// A parked batch is enqueued rather than dropped even while the source is stopping, because
    /// <see cref="WriteRetryQueue.Enqueue"/> takes ownership of what it parks: a batch that arrives after
    /// the queue is retired is counted as dropped there, and one parked before it is counted by
    /// <see cref="WriteRetryQueue.Retire"/>. Either way the loss is accounted for, so this path does not
    /// need a stop-specific discard of its own.
    /// </remarks>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask WriteChangesViaRetryQueueAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (IsResumeGateHeld)
        {
            // Parked rather than sent: CompleteResumeAsync judges it against the loaded state.
            WriteRetryQueue.Enqueue(changes);

            AfterResumeGateObserved?.Invoke();

            if (!IsResumeGateHeld)
            {
                // The gate cleared while this batch was being parked, so no completing resume will come
                // back for it, and an empty later batch means the write handler is not called again.
                // Judge it here rather than leaving it stranded.
                await ReconcileRetryQueueAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await WriteRetryQueue.WriteAsync(this, changes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drains the write retry queue on a timer, so that parked writes are not held until the next
    /// change happens to trigger a flush.
    /// </summary>
    /// <remarks>
    /// The processor's flush returns early on an empty batch, so its write handler, which is the only
    /// other thing that drains the queue, is never called while the model is idle.
    /// <para>
    /// This is a second sender running alongside the write handler. They agree on order because
    /// <see cref="WriteRetryQueue.WriteAsync"/> holds the flush gate across both its drain and its send,
    /// so a batch cannot be sent past one this drain already has in flight.
    /// </para>
    /// </remarks>
    private async Task FlushRetryQueuePeriodicallyAsync(CancellationToken cancellationToken)
    {
        if (_retryTime <= TimeSpan.Zero)
        {
            // PeriodicTimer rejects a non-positive period. Reported by doing nothing rather than by
            // logging: the queue is still drained by the write handler, exactly as it was before this
            // drain existed, and a line logged once per attempt would be noise. Nothing below this can
            // throw outside the try, which is what lets StopIdleFlushAsync treat the task as fault-free.
            return;
        }

        try
        {
            using var timer = new PeriodicTimer(_retryTime);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (IsResumeGateHeld || WriteRetryQueue.IsEmpty)
                {
                    continue;
                }

                await WriteRetryQueue.FlushAsync(this, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Swallowed rather than propagated: this loop is raced against the processor and its
            // failure must not end the attempt that the processor is still serving.
            _logger.LogError(exception, "Failed to flush the write retry queue on the idle tick.");
        }
    }

    /// <summary>
    /// Cancels the idle drain and waits for it to stop, but not indefinitely.
    /// </summary>
    /// <remarks>
    /// Bounded rather than simply awaited, because the drain can be inside a
    /// <see cref="WriteChangesAsync"/> that never observes its token. An unbounded wait there stops
    /// <see cref="RunAsync"/> from returning at all, which the DI path escapes only at the host's stop
    /// deadline and the graph-attach detach path never escapes. Bounded by the same value as the
    /// processor's teardown flush, which answers the same question about the same write handlers.
    /// </remarks>
    private async Task StopIdleFlushAsync(CancellationTokenSource idleFlush, Task idleFlushTask)
    {
        await idleFlush.CancelAsync().ConfigureAwait(false);

        // Disposal is deferred to the drain's own completion rather than done here, because
        // CancellationTokenSource.Dispose is not safe against the timer's concurrent registration on the
        // token, and the wait below can return before the drain has stopped using it. Attached after the
        // cancel above, so it can never run before it. Cannot fault: FlushRetryQueuePeriodicallyAsync
        // catches everything it can throw.
        _ = idleFlushTask.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            idleFlush,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            await idleFlushTask.WaitAsync(ChangeQueueProcessor.TeardownFlushBound).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Gave up waiting after {Timeout} for the idle retry-queue drain to stop. A write handler " +
                "that ignores cancellation may still complete it.",
                ChangeQueueProcessor.TeardownFlushBound);
        }
    }

    /// <summary>
    /// Suspends outbound delivery until the returned epoch is passed to <see cref="CompleteResumeAsync"/>
    /// or <see cref="TryEndResume"/>, so that writes captured while the connection is being replaced are
    /// parked rather than sent.
    /// </summary>
    /// <remarks>
    /// The epoch exists because more than one loop can hold the gate. A connector's own reconnect loop
    /// runs concurrently with the attempt loop for the whole first connect window, so without it the
    /// attempt loop's completion clears a gate the reconnect is still holding and leaves a writable
    /// socket unguarded.
    /// <para>
    /// A transactional commit bypasses this gate entirely, because it reaches the source without passing
    /// through the retry-queue wrapper. That is acceptable: it carries fresh intent, and the reconcile
    /// drops any parked entry the commit supersedes.
    /// </para>
    /// <para>
    /// Callers must take this gate at the moment the drop is detected, not just before the reconnect
    /// begins, and the backoff between the two is not incidental slack. The gate read in the write
    /// handler and the socket becoming writable again are not atomic, so a flush that races past the
    /// gate check an instant before this call still targets the socket that just failed and simply
    /// parks, because that socket stays unwritable for at least the reconnect delay. Taking the gate
    /// later, just before the reconnect itself, would move that same race onto a socket about to become
    /// writable: the raced flush would block on the connection lock instead of failing, and be released
    /// the instant the new connection opens, sending the whole unjudged retry queue ahead of the
    /// initial-state load.
    /// </para>
    /// </remarks>
    protected int BeginResume()
    {
        // Taking the epoch and publishing it are one compare-exchange. Split in two, as an increment
        // followed by a write, two resumes opening at once can take 5 and 6 and then publish in the
        // other order, leaving the older one owning the gate while the newer reconnect is the live one.
        int current;
        int epoch;
        do
        {
            current = Volatile.Read(ref _resumeGate);
            epoch = (current + 1) | 1;
        }
        while (Interlocked.CompareExchange(ref _resumeGate, epoch, current) != current);

        return epoch;
    }

    /// <summary>
    /// Re-opens outbound delivery if <paramref name="resumeEpoch"/> still owns the gate, so that a
    /// completing resume cannot clear a gate a later resume has taken over.
    /// </summary>
    /// <returns>
    /// <c>true</c> if this call actually cleared the gate, <c>false</c> if a later resume already owns
    /// it or it was already clear. Callers that only act when they were the ones who cleared it (rather
    /// than merely observing the gate as clear, which a later resume's own clearing can also produce)
    /// should gate on this rather than re-reading the field.
    /// </returns>
    /// <remarks>
    /// Test and clear are one compare-exchange rather than a read followed by a write. Split in two,
    /// a newer <see cref="BeginResume"/> landing between them has its gate cleared by the older resume,
    /// which leaves outbound delivery open for the whole of the newer reconnect: the two-instruction
    /// race produces an unguarded window as long as a connect.
    /// <para>
    /// <see cref="BeginResume"/> only ever hands out odd epochs, so the parity test rejects both the
    /// zero a connector's reconnect loop carries before its first resume and any other value that never
    /// held the gate. Without it, an even epoch could match an open gate and be reported as ownership.
    /// </para>
    /// </remarks>
    protected bool TryEndResume(int resumeEpoch)
    {
        if ((resumeEpoch & 1) == 0)
        {
            return false;
        }

        // Releasing moves the gate to the following even value rather than back to a fixed one, so the
        // field keeps increasing and a later resume can never be matched by an earlier epoch.
        return Interlocked.CompareExchange(ref _resumeGate, resumeEpoch + 1, resumeEpoch) == resumeEpoch;
    }

    /// <summary>
    /// Reconciles the parked writes against the state that was just loaded, then re-opens outbound
    /// delivery unless a later resume has already taken the gate over. Runs after the initial-state
    /// load, never before it.
    /// </summary>
    /// <remarks>
    /// The second reconcile pass below and the self-heal inside
    /// <see cref="WriteChangesViaRetryQueueAsync"/> both exist because judged and unjudged entries share
    /// one retry queue and the flush that drains it is edge-triggered on a non-empty batch rather than
    /// level-triggered on the queue's contents. The second pass covers entries parked during the
    /// reconcile above, and the self-heal covers the entry parked in the window between the gate read
    /// and the enqueue, which the second pass's emptiness check cannot see.
    /// </remarks>
    protected async Task CompleteResumeAsync(int resumeEpoch, CancellationToken cancellationToken)
    {
        // Skipped only when a different resume is still holding the gate. That one reconciles against
        // the state its own load delivers, and the retry queue is shared, so it judges these entries
        // along with its own. Reconciling here as well would judge them against a model that resume has
        // not loaded yet and flush the send arm straight out, landing the unjudged queue on the
        // replacement connection ahead of its initial-state load.
        //
        // An already-open gate is deliberately not skipped, even though this resume no longer owns it.
        // Nothing else is coming for these entries then, and the drain that fed them (RunAsync parks the
        // connect-window writes immediately before calling this) can have run after the other resume
        // took its own drain. Returning here would leave them unjudged in the queue for the idle drain
        // to flush raw, which is the "an older value can win over a newer one" loss the gate exists to
        // prevent. The reconcile is safe on an open gate: its send arm is the ordinary connected path.
        var gate = Volatile.Read(ref _resumeGate);
        var heldByAnotherResume = (gate & 1) == 1 && gate != resumeEpoch;
        if ((resumeEpoch & 1) == 0 || heldByAnotherResume)
        {
            return;
        }

        bool clearedByThisResume;
        try
        {
            await ReconcileRetryQueueAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Cleared even when the reconcile throws: a stuck gate parks every later write for the
            // life of the source, which is a worse failure than an unreconciled flush.
            clearedByThisResume = TryEndResume(resumeEpoch);
        }

        // The reconcile's own restores are local commits, so a live processor consuming the same
        // subscription can park them right back while the gate is still held above, and an empty later
        // batch means the write handler is never called again to drain them. They were parked after the
        // drain that fed the reconcile above, so nothing has judged them yet: a second pass judges them
        // rather than leaving them stuck, now that the gate is clear so their own send reaches the source
        // through the normal connected path instead of being parked again.
        //
        // Gated on this call having actually cleared the gate, not merely on the gate reading clear: a
        // newer resume can have taken the gate over and already cleared it itself, and running a second
        // pass here on top of that one's would race it rather than complete this one's own.
        if (clearedByThisResume && !WriteRetryQueue.IsEmpty)
        {
            await ReconcileRetryQueueAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task ReconcileRetryQueueAsync(CancellationToken cancellationToken)
    {
        var retryChanges = await WriteRetryQueue.DrainForLocalReapplyAsync(cancellationToken).ConfigureAwait(false);
        if (retryChanges.Length == 0)
        {
            return;
        }

        var restored = 0;
        var sent = 0;
        // Counted apart from dropped and failed, because only those two are added to
        // OutboundRetries.TotalDropped.
        var superseded = 0;
        var dropped = 0;
        var failed = 0;
        List<SubjectPropertyChange>? toSend = null;

        foreach (var change in CollapsePerProperty(retryChanges))
        {
            try
            {
                var property = change.Property;

                if (!ChangeDeliveryFilter.IsCurrent(in change, DeliveryRule))
                {
                    // A later local commit supersedes it, and that commit's change is delivered in its
                    // place.
                    superseded++;
                    continue;
                }

                // Still the latest local intent, so it has to reach the source. Decided by commit order
                // rather than by comparing values: the load writes the source's value into the model
                // without advancing the marker, and a value comparison cannot tell that apart from a
                // newer local write, so it discarded live writes.
                var currentValue = property.Metadata.GetValue?.Invoke(property.Subject);
                if (Equals(currentValue, change.GetNewValue<object?>()))
                {
                    // Already the current model value: the source has not received it, so send it.
                    // Marked here because this path flushes the retry queue directly rather than going
                    // through the processor, and without the mark a later transaction confirmation on
                    // this property is not written back, which is the divergence that repair exists for.
                    ChangeDeliveryFilter.MarkPropertyAsPublishedToSource(in change);
                    (toSend ??= []).Add(change);
                    sent++;
                }
                else if (property.Metadata.SetValue is { } setValue)
                {
                    // The load moved the model off it: restore locally so the connected phase captures
                    // and sends the re-applied write.
                    setValue(property.Subject, change.GetNewValue<object?>());
                    restored++;
                }
                else
                {
                    // No setter, so there is nothing to restore and the change has already left the
                    // queue. Derived properties reach this: their recomputation commits as Local and is
                    // parked like any other write. Counted as dropped rather than reported as restored.
                    dropped++;
                    Metrics.OutboundRetries.AddDropped(1);
                    _logger.LogWarning(
                        "Cannot restore the queued write for property '{PropertyName}': it has no setter, so the change is dropped.",
                        property.Name);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception,
                    "Failed to reconcile retry queue change for property '{PropertyName}', dropping.",
                    change.Property.Name);
                failed++;
                Metrics.OutboundRetries.AddDropped(1);
            }
        }

        if (toSend is not null)
        {
            WriteRetryQueue.Enqueue(toSend.ToArray());
            await WriteRetryQueue.FlushAsync(this, cancellationToken).ConfigureAwait(false);
        }

        if (superseded > 0 || dropped > 0 || failed > 0)
        {
            _logger.LogWarning(
                "Retry queue reconcile: {Restored} restored over the loaded source value, {Sent} sent, {Superseded} superseded by a later local write, {Dropped} dropped because the property has no setter, {Failed} failed.",
                restored, sent, superseded, dropped, failed);
        }
        else if (restored > 0 || sent > 0)
        {
            _logger.LogInformation(
                "Retry queue reconcile: {Restored} restored, {Sent} sent.", restored, sent);
        }
    }

    // ---- Source monitoring surface ----

    /// <inheritdoc />
    public SourceState State => Volatile.Read(ref _stateSnapshot).State;

    /// <inheritdoc />
    public DateTimeOffset StateChangeTime => Volatile.Read(ref _stateSnapshot).ChangeTime;

    /// <inheritdoc />
    public DateTimeOffset? LastSynchronizedAt => Volatile.Read(ref _stateSnapshot).LastSynchronizedAt;

    /// <inheritdoc />
    public event EventHandler<SourceEvent>? StateChanged;

    /// <summary>
    /// Reports that the connection was lost, for connectors that detect an outage before they
    /// start buffering.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="SubjectPropertyWriter.StartBuffering"/>: calling that
    /// at detection time would replace the buffer with a fresh list, and the later StartBuffering
    /// on the reconnect path would then discard everything buffered in between. Protected rather
    /// than public: application code holding an ISubjectSource reference must not be able to flip a
    /// synchronized source back to Synchronizing. A concrete source in another assembly that needs to
    /// call this from a helper object outside its own inheritance hierarchy (SessionManager for
    /// OpcUaSubjectClientSource) needs an internal forwarder on that source; see
    /// OpcUaSubjectClientSource for the pattern.
    /// <para>
    /// Also invalidates the property writer's generation (see
    /// <see cref="SubjectPropertyWriter.InvalidateGeneration"/>): an initial load already in flight
    /// when the connection drops must not apply the pre-outage snapshot it eventually returns, or
    /// certify it as Synchronized. Without this, that stale report would stand until the reconnect's
    /// own StartBuffering runs - the whole tail of the in-flight load, not a narrow race.
    /// </para>
    /// </remarks>
    protected void ReportConnectionLost()
    {
        _propertyWriter.InvalidateGeneration();
        TransitionStateTo(SourceState.Synchronizing);
    }

    /// <summary>
    /// Moves to <paramref name="newState"/> and publishes the change, or does nothing when the
    /// transition is a no-op or the source has already stopped.
    /// </summary>
    /// <remarks>
    /// The state write, timestamp write and event raise are all inside one lock: a bare
    /// compare-exchange is not enough, since a writer could set Synchronized, be preempted, let
    /// disposal set Stopped and unregister, then resume and publish Synchronized after Stopped -
    /// both compare-exchanges would have succeeded, so no stickiness rule could prevent it.
    /// </remarks>
    internal void TransitionStateTo(SourceState newState)
    {
        lock (_stateLock)
        {
            var current = _stateSnapshot;
            var oldState = current.State;
            if (oldState == newState || oldState == SourceState.Stopped)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;

            // Never cleared, so it still answers whether a good period ever began.
            var lastSynchronizedAt = newState == SourceState.Synchronized ? now : current.LastSynchronizedAt;

            Volatile.Write(ref _stateSnapshot, new SourceStateSnapshot(newState, now, lastSynchronizedAt));

            var handlers = StateChanged;
            if (handlers is not null)
            {
                var sourceEvent = new SourceEvent(
                    SourceEventKind.StateChanged, this, null, oldState, newState, now);

                foreach (var handler in handlers.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<SourceEvent>)handler)(this, sourceEvent);
                    }
                    catch (Exception exception)
                    {
                        // A buggy handler must not be mistaken for a source failure, and must not
                        // prevent the remaining subscribers from observing the transition.
                        _logger.LogError(exception, "A StateChanged handler threw and was ignored.");
                    }
                }
            }
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        // Close outbound admission before publishing the final Stopped, so an observer blocked inside
        // the notification cannot still hand a write to the transport. Publishing while registered keeps
        // a dispose without a stop visible to the monitors.
        WriteRetryQueue.Retire();
        TransitionStateTo(SourceState.Stopped);

        // Take-and-clear in one step, so a concurrent StartAsync unwinding through its own local
        // array (see StartAsync) cannot have this method unregister the same entries a second time
        // on a later call, and so the field is never read while another thread is writing it.
        var monitors = ImmutableInterlocked.InterlockedExchange(
            ref _registeredMonitors, ImmutableArray<SourceMonitor>.Empty);
        foreach (var monitor in monitors)
        {
            monitor.Unregister(this);
        }

        WriteRetryQueue.Dispose();
        base.Dispose();
    }
}
