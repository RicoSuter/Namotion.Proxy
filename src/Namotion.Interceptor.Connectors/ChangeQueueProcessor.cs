using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Processes property changes from a queue, buffering and merging them before writing.
/// Used by both client sources and server background services.
/// </summary>
public class ChangeQueueProcessor : IDisposable
{
    /// <summary>
    /// How long final delivery and the source retry handoff may block. The bound is fixed per connector;
    /// the host's shared <c>HostOptions.ShutdownTimeout</c> remains the consumer-configurable budget.
    /// </summary>
    internal static readonly TimeSpan TeardownFlushBound = TimeSpan.FromSeconds(5);

    private const int ClosedDelivery = -1;
    private const int IdleState = 0;
    private const int ProcessingState = 1;
    private const int DisposedState = 2;

    private readonly Func<PropertyReference, bool> _propertyFilter;
    private readonly Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> _writeHandler;
    private readonly object? _source;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;
    private readonly ChangeDeliveryRule _deliveryRule;
    private readonly Action<long>? _dropHandler;
    private readonly bool _writeHandlerOwnsChanges;
    private Action? _terminalHandler;
    private readonly Func<CancellationToken, ValueTask>? _completionHandler;
    private readonly Func<int, bool>? _mergedDeliveryAdmission;

    // Use a concurrent, lock-free queue for collecting changes from the subscription thread.
    private readonly ConcurrentQueue<SubjectPropertyChange> _changes = new();

    private readonly int? _maxQueueDepth;
    private long _dropCount;
    private int _deliveryState;
    private int _flushGate; // 0 = free, 1 = flushing
    private int _lifecycleState;

    /// <summary>
    /// The rule this processor decides supersession with. Exposed so a connector can pin which rule it
    /// wired up: choosing wrongly is silent, so "it compiles" is not evidence that it chose correctly.
    /// </summary>
    internal ChangeDeliveryRule DeliveryRule => _deliveryRule;

    /// <summary>
    /// Number of changes dropped due to bounded-queue overflow or ordinary write failure, plus changes
    /// whose delivery was still locally unconfirmed when terminal ownership closed.
    /// </summary>
    public long DropCount => Interlocked.Read(ref _dropCount);

    /// <summary>
    /// Gets the number of changes currently buffered. Approximate: read without a lock while the
    /// pump is running. Normally 0 on the immediate path, except while a cancelled delivery is being
    /// handed to terminal accounting during teardown.
    /// </summary>
    public int QueueDepth => _changes.Count;

    // Scratch state used only while holding the flush gate (single-threaded access)
    private readonly List<SubjectPropertyChange> _flushChanges = [];
    private ChangeMerger? _changeMerger;

    // Reusable single-item buffer for the no-buffer (immediate) path
    private readonly SubjectPropertyChange[] _immediateBuffer = new SubjectPropertyChange[1];

    private readonly PropertyChangeQueueSubscription _subscription;
    private readonly PropertyChangeQueueSubscription? _ownedSubscription;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangeQueueProcessor"/> class.
    /// The subscription is created immediately so that changes are captured from this point,
    /// even before <see cref="ProcessAsync"/> is called. This prevents change loss during
    /// initialization gaps (e.g., between OPC UA node creation and processing start).
    /// </summary>
    /// <param name="source">Source to ignore (to prevent update loops).</param>
    /// <param name="context">The interceptor subject context.</param>
    /// <param name="propertyFilter">Filter to determine if a property change should be included.
    /// The <see cref="PropertyReference"/> may not have a registered property (e.g., when the subject
    /// is momentarily unregistered due to a concurrent structural mutation). Callers should handle
    /// this case explicitly, typically by resolving via <c>TryGetRegisteredProperty()</c> and
    /// returning <c>false</c> when null.</param>
    /// <param name="writeHandler">Handler to write batched changes.</param>
    /// <param name="deliveryRule">Which commits may supersede a change this processor is about to
    /// write; see <see cref="ChangeDeliveryRule"/> for the condition that decides it. Deliberately
    /// has no default: picking the wrong one is silent and its damage is permanent, so every connector
    /// states which it is.</param>
    /// <param name="bufferTime">Time to buffer changes before flushing.</param>
    /// <param name="maxQueueDepth">Bound on the buffered change queue, or null for unbounded (existing
    /// connector behavior). When set, enqueuing past the bound drops the oldest unprocessed change and
    /// increments <see cref="DropCount"/>, so the newest change is retained. Read only on the buffered
    /// path, so a processor with a buffer time of zero never touches the queue this bounds.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="dropHandler">Optional handler invoked when bounded-queue overflow, an ordinary
    /// write failure, or terminal delivery closure drops changes. Terminal closure reporting may be
    /// dispatched asynchronously. Use this to report the count to queue diagnostics without adding
    /// work to successful enqueue or dequeue operations.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="deliveryRule"/> is
    /// <see cref="ChangeDeliveryRule.Unspecified"/> or not a defined value. Rejected here rather than at
    /// the first flush, where it would end delivery for this processor's lifetime. Also thrown when
    /// <paramref name="maxQueueDepth"/> is zero or negative and <paramref name="bufferTime"/> is
    /// greater than zero, since a bound has to leave room for at least one change.</exception>
    public ChangeQueueProcessor(
        object? source,
        IInterceptorSubjectContext context,
        Func<PropertyReference, bool> propertyFilter,
        Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> writeHandler,
        ChangeDeliveryRule deliveryRule,
        TimeSpan? bufferTime,
        int? maxQueueDepth,
        ILogger logger,
        Action<long>? dropHandler = null)
    {
        _source = source;
        _propertyFilter = propertyFilter;
        _writeHandler = writeHandler;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
        _dropHandler = dropHandler;
        _writeHandlerOwnsChanges = false;
        _mergedDeliveryAdmission = TryAdmitMergedDelivery;

        try
        {
            ValidateMaxQueueDepth(maxQueueDepth, _bufferTime);

            _maxQueueDepth = maxQueueDepth;
            _deliveryRule = ValidateRule(deliveryRule);

            _changeMerger = new ChangeMerger();
            _subscription = context.CreatePropertyChangeQueueSubscription();
            _ownedSubscription = _subscription;
        }
        catch
        {
            _changeMerger?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Initializes the processor with an externally owned subscription. The caller keeps ownership:
    /// <see cref="Dispose"/> does not dispose the subscription. Use this when the subscription must
    /// outlive the processor, for example a source-lifetime subscription reused across reconnects.
    /// </summary>
    internal ChangeQueueProcessor(
        object? source,
        PropertyChangeQueueSubscription subscription,
        Func<PropertyReference, bool> propertyFilter,
        Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> writeHandler,
        ChangeDeliveryRule deliveryRule,
        TimeSpan? bufferTime,
        int? maxQueueDepth,
        ILogger logger,
        Action<long>? dropHandler = null,
        bool writeHandlerOwnsChanges = false,
        Action? terminalHandler = null,
        Func<CancellationToken, ValueTask>? completionHandler = null)
    {
        _source = source;
        _propertyFilter = propertyFilter;
        _writeHandler = writeHandler;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
        _dropHandler = dropHandler;
        _writeHandlerOwnsChanges = writeHandlerOwnsChanges;
        _terminalHandler = terminalHandler;
        _completionHandler = completionHandler;
        _mergedDeliveryAdmission = writeHandlerOwnsChanges ? null : TryAdmitMergedDelivery;

        ValidateMaxQueueDepth(maxQueueDepth, _bufferTime);

        _maxQueueDepth = maxQueueDepth;
        _subscription = subscription;
        _deliveryRule = ValidateRule(deliveryRule);
        _changeMerger = new ChangeMerger();
    }

    // Only on the buffered path: a buffer time of zero writes each change as it is dequeued and never
    // fills the queue this bounds, so the bound is not read there.
    private static void ValidateMaxQueueDepth(int? maxQueueDepth, TimeSpan bufferTime)
    {
        if (maxQueueDepth is <= 0 && bufferTime > TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQueueDepth), maxQueueDepth,
                "A bounded change queue must have room for at least one change. Pass null for an unbounded " +
                "queue, or a buffer time of zero for the immediate path, which writes each change as it is " +
                "dequeued and buffers nothing.");
        }
    }

    // Rejects every unnamed value, not just zero: the delivery decision throws on an unknown rule from
    // inside the flush, outside the try that wraps the write handler. The periodic loop's catch does
    // catch it, but that catch sits outside the loop, so the loop never resumes and delivery ends for
    // this processor's lifetime while the queue keeps filling.
    private static ChangeDeliveryRule ValidateRule(ChangeDeliveryRule rule)
    {
        if (rule is not (ChangeDeliveryRule.SourceValuesMayBeStale or ChangeDeliveryRule.SourceValuesAreSettled))
        {
            throw new ArgumentOutOfRangeException(nameof(rule), rule,
                "A delivery rule must be chosen explicitly; see ChangeDeliveryRule for the condition that decides it.");
        }

        return rule;
    }

    /// <summary>
    /// Processes changes from the queue until cancellation is requested.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The task.</returns>
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        var cancellationSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The wait handle is signalled before token callbacks run, so a later blocking callback cannot delay teardown.
        var cancellationWait = ThreadPool.RegisterWaitForSingleObject(
            cancellationToken.WaitHandle,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
            cancellationSignal,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: true);
        var previousState = Interlocked.CompareExchange(ref _lifecycleState, ProcessingState, IdleState);
        if (previousState != IdleState)
        {
            cancellationWait.Unregister(null);
            throw previousState == ProcessingState
                ? new InvalidOperationException("The processor is already running.")
                : new ObjectDisposedException(nameof(ChangeQueueProcessor));
        }

        if (Volatile.Read(ref _lifecycleState) == DisposedState)
        {
            // Dispose raced the Idle-to-Processing transition before the processing task was created,
            // so no live run remains to release the merger in its finally block.
            cancellationWait.Unregister(null);
            DisposeMerger();
            throw new ObjectDisposedException(nameof(ChangeQueueProcessor));
        }

        var processingTokenSource = new CancellationTokenSource();
        var teardownTokenSource = new CancellationTokenSource();
        var teardownStarted = new TaskCompletionSource<ExceptionDispatchInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processingTask = Task.Run(
            () => ProcessCoreAsync(processingTokenSource.Token, teardownTokenSource.Token, teardownStarted),
            CancellationToken.None);

        var completedTask = await Task.WhenAny(processingTask, teardownStarted.Task, cancellationSignal.Task).ConfigureAwait(false);
        cancellationWait.Unregister(null);
        if (completedTask == processingTask)
        {
            try { await processingTask.ConfigureAwait(false); }
            finally
            {
                processingTokenSource.Dispose();
                teardownTokenSource.Dispose();
            }
            return;
        }

        var processingCancellationTask = processingTokenSource.CancelAsync();
        var teardownCancellationTask = Task.CompletedTask;
        using var teardownDelayCancellation = new CancellationTokenSource();
        var teardownDelay = Task.Delay(TeardownFlushBound, teardownDelayCancellation.Token);
        try
        {
            if (await Task.WhenAny(processingTask, teardownDelay).ConfigureAwait(false) == processingTask)
            {
                await teardownDelayCancellation.CancelAsync().ConfigureAwait(false);
                await processingTask.ConfigureAwait(false);
            }
            else
            {
                teardownCancellationTask = teardownTokenSource.CancelAsync();
                Dispose();
                if (completedTask == teardownStarted.Task)
                {
                    (await teardownStarted.Task.ConfigureAwait(false))?.Throw();
                }
            }
        }
        finally
        {
            ObserveLateLifetimeInBackground(
                processingTask,
                processingCancellationTask,
                teardownCancellationTask,
                processingTokenSource,
                teardownTokenSource);
        }
    }

    private async Task ProcessCoreAsync(CancellationToken processingToken, CancellationToken teardownToken, TaskCompletionSource<ExceptionDispatchInfo?> teardownStarted)
    {
        try
        {
            // Connect-window staleness is positional: changes arriving after this snapshot are steady state.
            var queuedBeforeStart = _subscription.Count;
            using var periodicTimer = _bufferTime > TimeSpan.Zero ? new PeriodicTimer(_bufferTime) : null;

            var flushTask = periodicTimer is not null
                ? Task.Run(async () =>
                {
                    var flushFailureReported = false;
                    try
                    {
                        while (await periodicTimer.WaitForNextTickAsync(processingToken).ConfigureAwait(false))
                        {
                            // Catch per tick so a consumer callback cannot permanently stop delivery while dequeueing continues.
                            try
                            {
                                await TryFlushAsync(processingToken).ConfigureAwait(false);
                                flushFailureReported = false;
                            }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            {
                                ReportFlushFailure(exception, ref flushFailureReported);
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception exception)
                    {
                        ReportFlushFailure(exception, ref flushFailureReported);
                    }
                })
                : Task.CompletedTask;

            if (periodicTimer is null)
            {
                _logger.LogWarning(
                    "Change queue processor is running without buffering (bufferTime <= 0). " +
                    "Each property change will be processed individually without merging, " +
                    "which can cause high CPU usage under load. " +
                    "Consider setting a bufferTime (e.g., 8-50ms) to enable batching and merging.");
            }

            try
            {
                while (_subscription.TryDequeue(out var change, processingToken))
                {
                    var wasQueuedBeforeStart = queuedBeforeStart > 0;
                    if (wasQueuedBeforeStart)
                    {
                        queuedBeforeStart--;
                    }

                    if (ReferenceEquals(change.Origin.Source, _source) && !ChangeDeliveryFilter.NeedsWriteBack(in change))
                    {
                        continue;
                    }

                    if (!_propertyFilter(change.Property))
                    {
                        continue;
                    }

                    if (wasQueuedBeforeStart && !ChangeDeliveryFilter.IsCurrent(in change, _deliveryRule))
                    {
                        continue;
                    }

                    if (periodicTimer is null)
                    {
                        // Client changes preserve every intermediate value without a merge. Servers must
                        // still avoid serving a value that their subject has already superseded.
                        if (_deliveryRule == ChangeDeliveryRule.SourceValuesAreSettled &&
                            !ChangeDeliveryFilter.TryAcceptForDelivery(in change, _deliveryRule))
                        {
                            continue;
                        }
                        if (_deliveryRule == ChangeDeliveryRule.SourceValuesMayBeStale)
                        {
                            ChangeDeliveryFilter.MarkPropertyAsPublishedToSource(in change);
                        }

                        _immediateBuffer[0] = change;
                        await WriteChangesAsync(_immediateBuffer, processingToken).ConfigureAwait(false);
                    }
                    else
                    {
                        _changes.Enqueue(change);
                        if (_maxQueueDepth is int maxQueueDepth && _changes.Count > maxQueueDepth)
                        {
                            DropOverflow(maxQueueDepth);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                teardownStarted.TrySetResult(ExceptionDispatchInfo.Capture(exception));
                throw;
            }
            finally
            {
                teardownStarted.TrySetResult(null);
                periodicTimer?.Dispose();
                await flushTask.ConfigureAwait(false);
                try
                {
                    await TryFlushAsync(teardownToken).ConfigureAwait(false);
                }
                finally
                {
                    if (_completionHandler is not null)
                    {
                        await _completionHandler(teardownToken).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            _immediateBuffer[0] = default;
            if (Interlocked.CompareExchange(ref _lifecycleState, IdleState, ProcessingState) == DisposedState)
            {
                DisposeMerger();
            }
        }
    }

    private static void ObserveLateLifetimeInBackground(
        Task processingTask,
        Task processingCancellationTask,
        Task teardownCancellationTask,
        CancellationTokenSource processingTokenSource,
        CancellationTokenSource teardownTokenSource)
    {
        _ = Task.WhenAll(processingTask, processingCancellationTask, teardownCancellationTask).ContinueWith(
            task =>
            {
                _ = task.Exception;
                processingTokenSource.Dispose();
                teardownTokenSource.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool TryAdmitDelivery(int count) =>
        Interlocked.CompareExchange(ref _deliveryState, count, 0) == 0;

    private bool TryAdmitMergedDelivery(int count)
    {
        var admitted = TryAdmitDelivery(count);
        if (!admitted)
        {
            ReportTerminalDrops(count);
        }

        return admitted;
    }

    private int CloseDeliveryAndDrain()
    {
        // Cancellation requeue and failure accounting must settle before close observes the delivery
        // state and queue together, or close could miss their ownership transition.
        lock (_changes)
        {
            var count = Math.Max(0, Interlocked.Exchange(ref _deliveryState, ClosedDelivery));
            while (_changes.TryDequeue(out _))
            {
                count++;
            }

            return count;
        }
    }

    private void ReportTerminalDrops(int count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _dropCount, count);
        _ = Task.Run(() =>
        {
            InvokeDropHandler(count);
            try
            {
                _logger.LogWarning(
                    "Gave up waiting after {Timeout} for {Count} changes to be written while stopping. " +
                    "A write handler may already have completed them remotely or may still complete them.",
                    TeardownFlushBound,
                    count);
            }
            catch
            {
                // Reporting is best effort after ownership has already been settled.
            }
        });
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken,
        bool deliveryAdmitted = false)
    {
        if (_writeHandlerOwnsChanges)
        {
            try
            {
                await _writeHandler(changes, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to hand changes to their delivery owner.");
            }
            return;
        }

        var count = changes.Length;
        if (!deliveryAdmitted && !TryAdmitDelivery(count))
        {
            ReportTerminalDrops(count);
            return;
        }

        try
        {
            await _writeHandler(changes, cancellationToken).ConfigureAwait(false);
            Interlocked.CompareExchange(ref _deliveryState, 0, count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_changes)
            {
                if (Interlocked.CompareExchange(ref _deliveryState, 0, count) == count)
                {
                    foreach (var change in changes.Span)
                    {
                        _changes.Enqueue(change);
                    }
                }
            }

            throw;
        }
        catch (Exception exception)
        {
            var counted = false;
            lock (_changes)
            {
                if (Interlocked.CompareExchange(ref _deliveryState, 0, count) == count)
                {
                    Interlocked.Add(ref _dropCount, count);
                    counted = true;
                }
            }

            if (counted)
            {
                InvokeDropHandler(count);
                _logger.LogError(exception, "Failed to write changes.");
            }
        }
    }

    private void InvokeDropHandler(long count)
    {
        try { _dropHandler?.Invoke(count); } catch { }
    }

    // Report only the first consecutive failure and guard the consumer-supplied logger.
    private void ReportFlushFailure(Exception exception, ref bool alreadyReported)
    {
        if (alreadyReported)
        {
            return;
        }

        alreadyReported = true;
        try { _logger.LogError(exception, "Failed to flush changes."); } catch { }
    }

    /// <summary>
    /// Drops the oldest buffered changes until the queue is back within <paramref name="maxQueueDepth"/>,
    /// incrementing <see cref="DropCount"/> for each. Best-effort: a concurrent flush may drain the queue
    /// below the bound first, in which case fewer drops occur.
    /// </summary>
    private void DropOverflow(int maxQueueDepth)
    {
        var droppedCount = 0L;
        while (_changes.Count > maxQueueDepth && _changes.TryDequeue(out _))
        {
            Interlocked.Increment(ref _dropCount);
            droppedCount++;
        }

        if (droppedCount > 0)
        {
            InvokeDropHandler(droppedCount);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask TryFlushAsync(CancellationToken cancellationToken)
    {
        // Fast, allocation-free try-enter
        if (Interlocked.Exchange(ref _flushGate, 1) == 1)
        {
            return;
        }

        // Whether the merger was handed a batch, which decides whether it has one to release below. Set
        // before the call rather than after, so a throw part-way through a merge still releases it.
        var merged = false;

        try
        {
            // Drain the concurrent queue into the scratch buffer under exclusive flush
            _flushChanges.Clear();
            while (_changes.TryDequeue(out var change))
            {
                _flushChanges.Add(change);
            }

            if (_flushChanges.Count == 0)
            {
                return;
            }

            merged = true;
            var mergedChanges = _changeMerger!.Merge(
                CollectionsMarshal.AsSpan(_flushChanges),
                _deliveryRule,
                _mergedDeliveryAdmission);

            if (mergedChanges.Length > 0)
            {
                await WriteChangesAsync(
                    mergedChanges,
                    cancellationToken,
                    deliveryAdmitted: !_writeHandlerOwnsChanges).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                // Clear buffers to allow GC of SubjectPropertyChange objects
                _flushChanges.Clear();

                if (merged && Volatile.Read(ref _lifecycleState) != DisposedState)
                {
                    // Only when there was a batch. An idle tick has nothing to release, and resetting
                    // anyway would feed the merger a zero-width batch: at the default buffer time that is
                    // roughly 125 of them a second, which drives its trim and shrink policies off how long
                    // the source has been quiet rather than off how wide its flushes actually are.
                    _changeMerger!.Reset();
                }
            }
            finally
            {
                // Unconditionally, and after the cleanup rather than with it: a gate left at 1 makes every
                // later flush return at the try-enter while the dequeue loop keeps filling the queue, so
                // cleanup throwing would stop delivery permanently and grow the queue without bound.
                Volatile.Write(ref _flushGate, 0);
            }
        }
    }

    /// <summary>
    /// Disposes the processor and returns the rented buffer to the pool.
    /// </summary>
    public void Dispose()
    {
        var previousState = Interlocked.Exchange(ref _lifecycleState, DisposedState);
        if (previousState != DisposedState)
        {
            _ownedSubscription?.Dispose();
        }

        ReportTerminalDrops(CloseDeliveryAndDrain());
        if (previousState == IdleState)
        {
            DisposeMerger();
        }
        InvokeTerminalHandlerOnce();
    }

    private void InvokeTerminalHandlerOnce()
    {
        lock (_changes)
        {
            // Exchange inside the reentrant delivery monitor: competing threads wait for callback
            // completion, while callback reentry sees null and does not recurse or retry an exception.
            Interlocked.Exchange(ref _terminalHandler, null)?.Invoke();
        }
    }

    // Called only before processing starts or after its final flush, so nulling cannot race merger use.
    private void DisposeMerger() => Interlocked.Exchange(ref _changeMerger, null)?.Dispose();
}
