using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Monitoring;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Writes inbound property updates from sources to subjects.
/// Implements the buffer-load-replay pattern to ensure eventual consistency during source initialization.
/// </summary>
/// <remarks>
/// During initialization, updates are buffered. Once <see cref="LoadInitialStateAndResumeAsync"/> is called,
/// the initial state is loaded, buffered updates are replayed, and subsequent writes are applied immediately.
/// This buffering behavior is transparent to sources - they simply call <see cref="Write{TState}"/>.
/// </remarks>
public sealed class SubjectPropertyWriter
{
    private readonly SubjectSourceBase _source;
    private readonly ILogger _logger;
    private readonly QueueMetrics? _inboundBufferMetrics;
    private readonly Lock _lock = new();

    private List<Action>? _updates = [];

    // Bumped by every StartBuffering call. LoadInitialStateAndResumeAsync captures the generation
    // in effect when it starts and compares it again after its (possibly long) await: if a later
    // StartBuffering happened in between, this call's snapshot is stale and must not be applied,
    // replayed, or certified as Synchronized - see LoadInitialStateAndResumeAsync.
    private int _generation;
    private int _bufferedUpdateCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubjectPropertyWriter"/> class.
    /// </summary>
    /// <param name="source">The source associated with this writer.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="inboundBufferMetrics">
    /// Where this writer reports the depth of its buffer and the updates a superseded load throws
    /// away, or <c>null</c> for a writer whose buffer nothing observes. The instance must have no live
    /// registration: this constructor registers on it and never deregisters, so a derived source must
    /// not pass the <c>SourceMetrics.InboundBuffer</c> that <see cref="SubjectSourceBase"/> has already
    /// registered for the writer it owns.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="inboundBufferMetrics"/> already has a live registration.
    /// </exception>
    /// <remarks>
    /// Typed to the concrete base rather than <see cref="ISubjectSource"/> because this writer
    /// drives the source's state transitions, which only <see cref="SubjectSourceBase"/> defines. A
    /// source implementing the interface directly owns its own write path and its own transitions.
    /// </remarks>
    internal SubjectPropertyWriter(SubjectSourceBase source, ILogger logger, QueueMetrics? inboundBufferMetrics = null)
    {
        _source = source;
        _logger = logger;
        _inboundBufferMetrics = inboundBufferMetrics;

        // Never disposed: the writer and its buffer live as long as the source does.
        _ = _inboundBufferMetrics?.Register(() => BufferedUpdateCount, capacity: null);
    }

    /// <summary>
    /// Gets how many inbound updates are currently buffered while the initial state loads.
    /// </summary>
    /// <remarks>
    /// Maintained under the writer's own lock and read without taking it: a lock-taking getter would
    /// close an ABBA cycle, since <see cref="StartBuffering"/> holds that lock while transitioning the
    /// source's state, which reaches registered monitors synchronously.
    /// </remarks>
    internal int BufferedUpdateCount => Volatile.Read(ref _bufferedUpdateCount);

    /// <summary>
    /// Starts buffering updates instead of applying them directly.
    /// Buffered updates will be replayed when <see cref="LoadInitialStateAndResumeAsync"/> is called.
    /// This method should be called before the source starts listening for changes.
    /// </summary>
    public void StartBuffering()
    {
        lock (_lock)
        {
            // The replaced list is a superseded snapshot that must not be applied. Counted anyway,
            // because it is the only signal of how often initial loads are being superseded.
            _inboundBufferMetrics?.AddDropped(_updates?.Count ?? 0);

            _updates = [];
            Volatile.Write(ref _bufferedUpdateCount, 0);
            _generation++;

            // Under _lock, paired with the generation change that governs it, so the transition
            // cannot be observed out of sync with the buffer it belongs to.
            _source.TransitionStateTo(SourceState.Synchronizing);
        }
    }

    /// <summary>
    /// Invalidates the current generation without touching the update buffer, for a connection loss
    /// detected before the reconnect's own <see cref="StartBuffering"/> call runs.
    /// </summary>
    /// <remarks>
    /// An in-flight <see cref="LoadInitialStateAndResumeAsync"/> captured the old generation before
    /// it started awaiting, so bumping it here is what makes that call discard its pre-outage
    /// snapshot instead of applying it and reporting Synchronized. Deliberately does not reset
    /// _updates: whatever has been buffered since the outage must survive until the reconnect's own
    /// StartBuffering.
    /// </remarks>
    internal void InvalidateGeneration()
    {
        lock (_lock)
        {
            _generation++;
        }
    }

    /// <summary>
    /// Completes initialization by loading initial state from the source
    /// and replaying all buffered updates. This ensures zero data loss during the initialization period.
    /// </summary>
    /// <remarks>
    /// If the load fails, the exception propagates to signal initialization failure
    /// and trigger reconnection.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The task.</returns>
    public async Task LoadInitialStateAndResumeAsync(CancellationToken cancellationToken)
    {
        // Published under _lock by StartBuffering/InvalidateGeneration; the comparison that matters
        // happens under the lock below, so this read needs no lock of its own.
        var generation = Volatile.Read(ref _generation);

        var applyAction = await _source.LoadInitialStateAsync(cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            if (generation != _generation)
            {
                // Superseded by a later StartBuffering: applying this snapshot would overwrite the
                // newer cycle's writes, and reporting Synchronized would certify stale data.
                _logger.LogDebug("LoadInitialStateAndResumeAsync discarded a stale snapshot superseded by a later reconnect.");
                return;
            }

            applyAction?.Invoke();

            // Replay previously buffered updates
            var updates = _updates;
            if (updates is null)
            {
                // Already replayed by a concurrent/previous call (race between automatic and manual reconnection).
                // This is safe - it means another reconnection cycle already loaded state and replayed updates.
                _logger.LogDebug("LoadInitialStateAndResumeAsync called but updates already replayed by concurrent reconnection.");
            }
            else
            {
                foreach (var action in updates)
                {
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Failed to apply subject update.");
                    }
                }

                // Must be after replay: Write() reads _updates without lock on the fast path.
                _updates = null;
                Volatile.Write(ref _bufferedUpdateCount, 0);
            }

            // Reported while still holding _lock, atomically with the generation check above, so a
            // StartBuffering landing in between cannot let a superseded cycle certify Synchronized.
            // Lock order writer._lock -> _stateLock -> monitor._lock (TransitionTo can reach a
            // registered monitor synchronously) is never reversed anywhere, so it cannot deadlock.
            _source.TransitionStateTo(SourceState.Synchronized);
        }
    }

    /// <summary>
    /// Writes a property update to the subject. During initialization, the update is buffered;
    /// otherwise it is applied immediately. This buffering is transparent to the caller.
    /// </summary>
    /// <param name="state">The state provided to the action (allows static delegates to avoid allocations).</param>
    /// <param name="update">The update action to apply to the subject.</param>
    public void Write<TState>(TState state, Action<TState> update)
    {
        // Hot path optimization: plain read (no volatile read) is fastest.
        // Changes to _updates are rare (only during initialization/reconnection).
        // If we see stale non-null during transition, we take lock and re-check - still correct.
        var updates = _updates;
        if (updates is not null)
        {
            lock (_lock)
            {
                updates = _updates;
                if (updates is not null)
                {
                    // Still initializing, buffer the update (cold path, allocations acceptable)
                    AddBeforeInitializationUpdate(updates, state, update);
                    Volatile.Write(ref _bufferedUpdateCount, updates.Count);
                    return;
                }
            }
        }

        try
        {
            update(state);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to apply subject update.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddBeforeInitializationUpdate<TState>(List<Action> beforeInitializationUpdates, TState state, Action<TState> update)
    {
        // The allocation for the closure happens only on the cold path (needs to be in an own non-inlined method
        // to avoid capturing unnecessary locals and causing allocations on the hot path).
        beforeInitializationUpdates.Add(() => update(state));
    }
}
