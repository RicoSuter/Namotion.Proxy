using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Connectors.Monitoring;

/// <summary>
/// One subscriber to the source event stream, with its own queue and its own drain.
/// </summary>
/// <remarks>
/// A slow handler delays only its own subscription. The queue starts empty, so it cannot hold
/// events from before the subscription existed; pair with <see cref="Sources"/> to observe every
/// change exactly once.
/// </remarks>
public sealed class SourceSubscription : IDisposable
{
    private readonly ConcurrentQueue<SourceEvent> _queue = new();
    private readonly Action<SourceEvent> _handler;
    private readonly Action<SourceSubscription> _onDisposed;
    private readonly SourceMonitor _monitor;
    private readonly Action _drain;

    private int _draining;
    private volatile bool _disposed;

    internal SourceSubscription(
        Action<SourceEvent> handler,
        ImmutableArray<ISubjectSource> sources,
        Action<SourceSubscription> onDisposed,
        SourceMonitor monitor)
    {
        _handler = handler;
        Sources = sources;
        _onDisposed = onDisposed;
        // The monitor, not a resolved ILogger: subscribing usually happens before the host is
        // built, so a logger captured here would be null for this subscription's lifetime.
        _monitor = monitor;
        // Cached rather than a Task.Run(Drain) method-group conversion at each call site: that
        // allocates a fresh Action delegate on every wakeup, and Drain is only ever run this way.
        _drain = Drain;
    }

    /// <summary>
    /// The sources registered at the moment this subscription was created, captured atomically with
    /// it. Reading SourceMonitor.Sources separately after subscribing is not race-free: a source
    /// registering between the two calls appears in both, and a naive consumer double-counts it.
    /// </summary>
    public ImmutableArray<ISubjectSource> Sources { get; }

    internal void Enqueue(in SourceEvent sourceEvent)
    {
        if (_disposed)
        {
            return;
        }

        _queue.Enqueue(sourceEvent);

        // Single-flight: one drain at a time, and it exits when the queue is empty, so an idle
        // subscription owns no task.
        if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
        {
            _ = Task.Run(_drain);
        }
    }

    private void Drain()
    {
        do
        {
            while (_queue.TryDequeue(out var sourceEvent))
            {
                if (_disposed)
                {
                    // Leaves _draining set rather than resetting it: harmless, since Enqueue checks
                    // _disposed before _draining, so no further Enqueue can reach the CompareExchange
                    // that would need _draining back at 0. The flag just goes dead with the subscription.
                    return;
                }

                try
                {
                    _handler(sourceEvent);
                }
                catch (Exception exception)
                {
                    try
                    {
                        _monitor.ResolveLogger()?.LogError(
                            exception, "A source event handler threw and was ignored.");
                    }
                    catch
                    {
                        // Reporting must not escape either: an exception here leaves _draining set,
                        // and Enqueue's CompareExchange then never schedules another drain, so this
                        // subscription would stop delivering permanently and grow its queue forever.
                    }
                }
            }

        }
        while (TryReacquireForPendingEvents());
    }

    /// <summary>
    /// Releases the single-flight flag and takes it straight back if the queue turned out not to be
    /// empty. Returns true when this thread must keep draining.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="Drain"/> so a test can drive the handoff directly; the window it
    /// closes is nanoseconds wide inside a running drain.
    /// <para>
    /// Must be Interlocked.Exchange, not Volatile.Write. Volatile.Write is release-only, so the
    /// !_queue.IsEmpty read below can be satisfied before the write is globally visible; a
    /// concurrent Enqueue in that window sees _draining still 1 and declines to schedule, while this
    /// thread sees an empty queue and exits, stranding the event. The full fence makes those two
    /// misses mutually exclusive. The race is beyond any test's reach, so a test pins this literal
    /// line instead - do not "simplify" it.
    /// </para>
    /// </remarks>
    internal bool TryReacquireForPendingEvents()
    {
        Interlocked.Exchange(ref _draining, 0);
        return !_queue.IsEmpty && Interlocked.CompareExchange(ref _draining, 1, 0) == 0;
    }

    /// <summary>
    /// Marks the subscription disposed and removes it from the monitor. Does not block on draining.
    /// </summary>
    /// <remarks>
    /// Disposal is asynchronous with respect to delivery, in both directions:
    /// <list type="bullet">
    /// <item>A handler invocation already in progress when Dispose is called is not interrupted; it
    /// runs to completion on its own, even after Dispose has returned.</item>
    /// <item>Any event still sitting in the queue, dequeued by the drain loop after Dispose has set
    /// the disposed flag, is dropped rather than delivered. Delivery is therefore best-effort at the
    /// disposal boundary: an event enqueued shortly before Dispose is not guaranteed to be handled.</item>
    /// </list>
    /// </remarks>
    public void Dispose()
    {
        _disposed = true;
        _onDisposed(this);
    }
}
