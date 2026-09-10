using System;
using System.Reactive.Concurrency;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// Measures the property write hot path of <see cref="PropertyChangeInterceptor"/> in every consumer
/// state named by the plan. The MemoryDiagnoser allocations-per-op column is the proof of the
/// build-once win and of the zero-allocation idle and listener-elsewhere fast paths.
///
/// Each state runs in its own process (BenchmarkDotNet default), set up by a targeted
/// <see cref="GlobalSetupAttribute"/>. This isolation matters: the per-property listener live count
/// is a process-wide static, so a listener created for one state must not be visible to another.
/// The context is registered with WithPropertyChangeSubscriptions only (no equality check), so every
/// write reaches the interceptor even when the value does not change.
/// </summary>
[MemoryDiagnoser]
public class PropertyChangeSubscriptionsBenchmark
{
    private const string WriteValue = "benchmark-value";
    private static readonly TimeSpan ScheduledDeliveryTimeout = TimeSpan.FromSeconds(30);

    private readonly object _scheduledDeliveryGate = new();

    private IInterceptorSubjectContext _context;
    private Car _car;

    private PropertyChangeQueueSubscription? _queueSubscription;
    private IDisposable? _observableSubscription;
    private IDisposable? _perPropertySubscription;
    private ScheduledPropertySubscription? _scheduledSubscription;
    private AutoResetEvent? _scheduledDeliveryCompleted;
    private bool _scheduledDeliverySignalDisposed;

    // A reference-typed value (not a string, not inline-sized): its change takes the two-holder
    // BoxedValueHolder path, so the build-once merge shows up as halved allocations under both channels.
    private Car[]? _boxedValue;

    private CancellationTokenSource? _drainCancellation;
    private Thread? _drainThread;

    // idle: no consumers at all (gates the post-commit fence plus count re-read added by the merge).
    [GlobalSetup(Target = nameof(WriteIdle))]
    public void SetupIdle()
    {
        _car = CreateCarInFreshContext();
    }

    // queue-only active: a single pull-queue subscription with a background consumer draining it.
    [GlobalSetup(Target = nameof(WriteWithQueueConsumer))]
    public void SetupQueueOnly()
    {
        _car = CreateCarInFreshContext();
        _queueSubscription = _context.CreatePropertyChangeQueueSubscription();
        StartQueueDrain(_queueSubscription);
    }

    // observable-only active: one Rx observer on the ImmediateScheduler (synchronous delivery).
    [GlobalSetup(Target = nameof(WriteWithObservableConsumer))]
    public void SetupObservableOnly()
    {
        _car = CreateCarInFreshContext();
        _observableSubscription = _context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(_ => { });
    }

    // both-active: queue consumer plus Rx observer.
    [GlobalSetup(Target = nameof(WriteWithQueueAndObservableConsumers))]
    public void SetupBoth()
    {
        _car = CreateCarInFreshContext();
        _queueSubscription = _context.CreatePropertyChangeQueueSubscription();
        StartQueueDrain(_queueSubscription);
        _observableSubscription = _context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(_ => { });
    }

    // listener-on-written-property: a per-property listener on the property the benchmark writes (lookup hit).
    [GlobalSetup(Target = nameof(WriteWithListenerOnSameProperty))]
    public void SetupListenerOnWrittenProperty()
    {
        _car = CreateCarInFreshContext();
        _perPropertySubscription = _car.SubscribeToPropertyInline(x => x.Name, (in SubjectPropertyChange _) => { });
    }

    [GlobalSetup(Target = nameof(WriteAndWaitForScheduledListenerOnSameProperty))]
    public void SetupScheduledListenerOnWrittenProperty()
    {
        _car = CreateCarInFreshContext();
        var deliveryCompleted = new AutoResetEvent(false);
        _scheduledDeliveryCompleted = deliveryCompleted;
        _scheduledDeliverySignalDisposed = false;
        _scheduledSubscription = new PropertyReference(_car, nameof(Car.Name))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    lock (_scheduledDeliveryGate)
                    {
                        if (!_scheduledDeliverySignalDisposed)
                        {
                            deliveryCompleted.Set();
                        }
                    }
                },
                Scheduler.Default);
    }

    // listener-elsewhere: a per-property listener on a DIFFERENT property, so the live count is nonzero
    // but the per-write lookup misses. This is the active-lookup fast path.
    [GlobalSetup(Target = nameof(WriteWithListenerOnOtherProperty))]
    public void SetupListenerElsewhere()
    {
        _car = CreateCarInFreshContext();
        _perPropertySubscription = new PropertyReference(_car, nameof(Car.Name_MaxLength_Unit))
            .SubscribeInline((in SubjectPropertyChange _) => { });
    }

    // observable-subscribed-then-fully-unsubscribed: subscribe an observer then dispose it, so the gate
    // must have re-closed back to the idle fast path.
    [GlobalSetup(Target = nameof(WriteAfterObservableFullyUnsubscribed))]
    public void SetupObservableThenUnsubscribed()
    {
        _car = CreateCarInFreshContext();
        var subscription = _context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(_ => { });
        subscription.Dispose();
    }

    // observable-only, reference-typed value: only one channel builds the change, so the allocation
    // count is the single-channel baseline (parity with the old code, which also builds once here).
    [GlobalSetup(Target = nameof(WriteObservableOnlyBoxed))]
    public void SetupObservableOnlyBoxed()
    {
        _car = CreateCarInFreshContext();
        _boxedValue = new[] { _car };
        _observableSubscription = _context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(_ => { });
    }

    // both-active, reference-typed value: the old code built the change twice (four holder allocations),
    // the merged interceptor builds once (two). This is where the build-once allocation win shows.
    [GlobalSetup(Target = nameof(WriteBothActiveBoxed))]
    public void SetupBothBoxed()
    {
        _car = CreateCarInFreshContext();
        _boxedValue = new[] { _car };
        _queueSubscription = _context.CreatePropertyChangeQueueSubscription();
        StartQueueDrain(_queueSubscription);
        _observableSubscription = _context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(_ => { });
    }

    [Benchmark(Baseline = true)]
    public void WriteIdle()
    {
        _car.Name = WriteValue;
    }

    [Benchmark]
    public void WriteObservableOnlyBoxed()
    {
        _car.PreviousCars = _boxedValue;
    }

    [Benchmark]
    public void WriteBothActiveBoxed()
    {
        _car.PreviousCars = _boxedValue;
    }

    [Benchmark]
    public void WriteWithQueueConsumer()
    {
        _car.Name = WriteValue;
    }

    [Benchmark]
    public void WriteWithObservableConsumer()
    {
        _car.Name = WriteValue;
    }

    [Benchmark]
    public void WriteWithQueueAndObservableConsumers()
    {
        _car.Name = WriteValue;
    }

    [Benchmark]
    public void WriteWithListenerOnSameProperty()
    {
        _car.Name = WriteValue;
    }

    // Measures the write plus one completed scheduled delivery. Waiting for each callback prevents
    // an active-run backlog from accumulating across benchmark invocations.
    [Benchmark]
    public void WriteAndWaitForScheduledListenerOnSameProperty()
    {
        _car.Name = WriteValue;
        // PendingCount can reach zero before the callback finishes, so wait for the observer's signal.
        if (!_scheduledDeliveryCompleted!.WaitOne(ScheduledDeliveryTimeout))
        {
            throw new TimeoutException("The scheduled property change was not delivered before the diagnostic timeout.");
        }
    }

    [Benchmark]
    public void WriteWithListenerOnOtherProperty()
    {
        _car.Name = WriteValue;
    }

    [Benchmark]
    public void WriteAfterObservableFullyUnsubscribed()
    {
        _car.Name = WriteValue;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scheduledSubscription?.Dispose();
        lock (_scheduledDeliveryGate)
        {
            _scheduledDeliverySignalDisposed = true;
            _scheduledDeliveryCompleted?.Dispose();
            _scheduledDeliveryCompleted = null;
        }

        _drainCancellation?.Cancel();
        _drainThread?.Join();
        _drainCancellation?.Dispose();

        _perPropertySubscription?.Dispose();
        _observableSubscription?.Dispose();
        _queueSubscription?.Dispose();
    }

    // Establishes a fresh _context as a side effect and returns a subject bound to it; the name
    // signals that the field is (re)assigned, not just that a Car is constructed.
    private Car CreateCarInFreshContext()
    {
        _context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeSubscriptions();

        return new Car(_context);
    }

    // A pull queue grows without bound unless a consumer drains it. A background thread keeps memory
    // bounded across the many-million-op measured run while leaving the write itself as the only work
    // in the benchmark method.
    private void StartQueueDrain(PropertyChangeQueueSubscription subscription)
    {
        _drainCancellation = new CancellationTokenSource();
        var cancellationToken = _drainCancellation.Token;
        _drainThread = new Thread(() =>
        {
            // TryDequeue observes cancellation as its first action and returns false, so the loop
            // exits on Cancel() even while producers keep feeding the queue.
            while (subscription.TryDequeue(out _, cancellationToken))
            {
            }
        })
        {
            IsBackground = true,
            Name = "PropertyChangeQueueDrain"
        };
        _drainThread.Start();
    }
}
