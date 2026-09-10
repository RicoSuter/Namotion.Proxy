using System.Reactive.Concurrency;
using System.Reactive.Disposables;

namespace Namotion.Interceptor.Tracking.Tests.Change;

// Runs scheduled work only when the test pumps it, so an interleaving is chosen rather than raced for.
internal sealed class ControllableScheduler : IScheduler
{
    private const int RunUntilIdleBudget = 8_192;

    private readonly object _gate = new();
    private readonly Queue<Action> _queue = new();
    private int _scheduleCallCount;

    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public int ScheduleCallCount => Volatile.Read(ref _scheduleCallCount);

    public int QueuedCount
    {
        get { lock (_gate) { return _queue.Count; } }
    }

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
    {
        Interlocked.Increment(ref _scheduleCallCount);
        lock (_gate)
        {
            _queue.Enqueue(() => action(this, state));
        }

        return Disposable.Empty;
    }

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
        => Schedule(state, action);

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
        => Schedule(state, action);

    public bool RunOne()
    {
        Action? work;
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                return false;
            }

            work = _queue.Dequeue();
        }

        work();
        return true;
    }

    // Runs every item queued at entry, without following items those items queue.
    public int RunAll()
    {
        int budget;
        lock (_gate)
        {
            budget = _queue.Count;
        }

        // Dequeued one at a time so an item that throws leaves its untouched siblings queued.
        var ran = 0;
        while (ran < budget)
        {
            Action work;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    break;
                }

                work = _queue.Dequeue();
            }

            ran++;
            work();
        }

        return ran;
    }

    // Runs items, including ones scheduled by earlier items. The budget turns a drain that reschedules itself
    // without making progress into a red test instead of a hung test run, its only other symptom.
    public void RunUntilIdle()
    {
        var ran = 0;
        while (RunOne())
        {
            if (++ran > RunUntilIdleBudget)
            {
                throw new InvalidOperationException(
                    $"RunUntilIdle ran more than {RunUntilIdleBudget} work items without the queue going idle, " +
                    "which means a self-sustaining reschedule loop rather than slow progress.");
            }
        }
    }
}

// Reproduces a scheduler disposed before the subscription: Schedule throws on the writer thread.
internal sealed class ThrowingScheduler : IScheduler
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
        => throw new ObjectDisposedException(nameof(ThrowingScheduler));

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
        => throw new ObjectDisposedException(nameof(ThrowingScheduler));

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
        => throw new ObjectDisposedException(nameof(ThrowingScheduler));
}

// Reproduces a scheduler disposed while a drain was already queued: Schedule succeeds and the work item never
// runs, the half of the scheduler-failure space the design cannot recover from.
internal sealed class BlackHoleScheduler : IScheduler
{
    private int _scheduleCallCount;

    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public int ScheduleCallCount => Volatile.Read(ref _scheduleCallCount);

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
    {
        Interlocked.Increment(ref _scheduleCallCount);
        return Disposable.Empty;
    }

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
        => Schedule(state, action);

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
        => Schedule(state, action);
}

// Records anything that escapes a work item. On a real pool scheduler such an escape is unhandled and
// terminates the test host, so it can only be asserted by catching it here first.
internal sealed class RecordingScheduler(IScheduler inner) : IScheduler
{
    private readonly List<Exception> _escaped = [];
    private int _scheduleCallCount;

    public DateTimeOffset Now => inner.Now;

    public int ScheduleCallCount => Volatile.Read(ref _scheduleCallCount);

    public IReadOnlyList<Exception> Escaped
    {
        get { lock (_escaped) { return _escaped.ToArray(); } }
    }

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
    {
        Interlocked.Increment(ref _scheduleCallCount);
        return inner.Schedule(state, (_, innerState) =>
        {
            try
            {
                // The work item gets this wrapper, not the inner scheduler, so anything it schedules from
                // inside itself is still counted and recorded.
                return action(this, innerState);
            }
            catch (Exception exception)
            {
                lock (_escaped)
                {
                    _escaped.Add(exception);
                }

                return Disposable.Empty;
            }
        });
    }

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
        => Schedule(state, action);

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
        => Schedule(state, action);
}

internal sealed class LongRunningTrapScheduler : IScheduler, ISchedulerLongRunning
{
    private readonly ControllableScheduler _inner = new();
    private int _longRunningCallCount;

    public DateTimeOffset Now => _inner.Now;
    public int ScheduleCallCount => _inner.ScheduleCallCount;
    public int LongRunningCallCount => Volatile.Read(ref _longRunningCallCount);

    public IDisposable Schedule<TState>(
        TState state,
        Func<IScheduler, TState, IDisposable> action)
    {
        return _inner.Schedule(
            (Scheduler: this, State: state, Action: action),
            static (_, item) => item.Action(item.Scheduler, item.State));
    }

    public IDisposable Schedule<TState>(
        TState state,
        TimeSpan dueTime,
        Func<IScheduler, TState, IDisposable> action) => Schedule(state, action);

    public IDisposable Schedule<TState>(
        TState state,
        DateTimeOffset dueTime,
        Func<IScheduler, TState, IDisposable> action) => Schedule(state, action);

    public IDisposable ScheduleLongRunning<TState>(
        TState state,
        Action<TState, ICancelable> action)
    {
        Interlocked.Increment(ref _longRunningCallCount);
        return Disposable.Empty;
    }

    public void RunUntilIdle() => _inner.RunUntilIdle();
}

// Runs accepted work synchronously while recording whether successor submissions grow the call stack.
internal sealed class DepthTrackingInlineScheduler(bool throwAfterAction = false) : IScheduler
{
    private int _scheduleCallCount;
    private int _scheduleDepth;
    private int _maximumScheduleDepth;

    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public int ScheduleCallCount => Volatile.Read(ref _scheduleCallCount);
    public int MaximumScheduleDepth => Volatile.Read(ref _maximumScheduleDepth);

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
    {
        Interlocked.Increment(ref _scheduleCallCount);
        var depth = Interlocked.Increment(ref _scheduleDepth);
        RecordMaximumDepth(depth);

        try
        {
            var disposable = action(this, state);
            if (throwAfterAction)
            {
                throw new InvalidOperationException("The scheduler failed after running the action.");
            }

            return disposable;
        }
        finally
        {
            Interlocked.Decrement(ref _scheduleDepth);
        }
    }

    public IDisposable Schedule<TState>(
        TState state,
        TimeSpan dueTime,
        Func<IScheduler, TState, IDisposable> action) => Schedule(state, action);

    public IDisposable Schedule<TState>(
        TState state,
        DateTimeOffset dueTime,
        Func<IScheduler, TState, IDisposable> action) => Schedule(state, action);

    private void RecordMaximumDepth(int depth)
    {
        var maximum = Volatile.Read(ref _maximumScheduleDepth);
        while (depth > maximum)
        {
            var observed = Interlocked.CompareExchange(ref _maximumScheduleDepth, depth, maximum);
            if (observed == maximum)
            {
                return;
            }

            maximum = observed;
        }
    }
}
