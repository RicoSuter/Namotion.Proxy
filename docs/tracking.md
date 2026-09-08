# Tracking

The `Namotion.Interceptor.Tracking` package provides comprehensive change tracking for interceptor subjects, including property value changes, derived property updates, subject lifecycle events, and parent-child relationships. A single `PropertyChangeInterceptor`, enabled with `WithPropertyChangeSubscriptions()`, routes property changes through three channels that share one write path: an **Rx observable** for composition and UI, a **high-performance queue** for high-throughput consumers, and **per-property subscriptions** for observing one property on one subject instance.

## Setup

Enable full property tracking in your interceptor context:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking(); // Includes all tracking features
```

This is a convenience method that registers:
- Equality checking to prevent unnecessary change notifications
- Derived property change detection
- Property change notifications (the `PropertyChangeInterceptor`, exposing the Rx observable, the high-performance queue, and per-property subscriptions)
- Context inheritance for child subjects

> **Note**: Transaction support is opt-in. Add `.WithTransactions()` or `.WithSourceTransactions()` to enable transaction support.

You can also enable features individually for more granular control.

## Change Tracking

All property change notifications flow through a single `PropertyChangeInterceptor`, registered with `WithPropertyChangeSubscriptions()` (also included in `WithFullPropertyTracking()`). The interceptor exposes three channels over one shared write path: the Rx observable, the high-performance queue, and per-property subscriptions. Enable it once and pick whichever channel fits the consumer.

| API | Delivery | Serialization | Main cost |
|---|---|---|---|
| `GetPropertyChangeObservable()` | scheduler by default | yes | context-wide fan-out and Rx subscription state |
| `CreatePropertyChangeQueueSubscription()` | caller-owned consumer | one consumer | an unbounded queue and a blocked consumer thread |
| `SubscribeInline(...)` | writing thread | no | lowest fixed cost, but observer latency and failures affect the setter |
| `Subscribe(..., scheduler, onError)` | scheduler | per subscription | an unbounded queue and scheduled drain work |

### Property Change Observable (Rx-based)

The observable channel uses Reactive Extensions (Rx) and is ideal for UI scenarios, complex query composition, and when you need rich operator support:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithPropertyChangeSubscriptions();

context
    .GetPropertyChangeObservable()
    .Subscribe(change =>
    {
        Console.WriteLine(
            $"Property '{change.Property.Name}' changed " +
            $"from '{change.GetOldValue<object?>()}' to '{change.GetNewValue<object?>()}'.");
    });

var person = new Person(context)
{
    FirstName = "John",
    LastName = "Doe"
};
```

### Property Change Queue (High Performance)

The queue channel gives a dedicated consumer direct control over draining changes. It is suited to background services, IoT data processing, and source synchronization:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithPropertyChangeSubscriptions();

using var subscription = context.CreatePropertyChangeQueueSubscription();

while (subscription.TryDequeue(out var change, cancellationToken))
{
    Console.WriteLine(
        $"Property '{change.Property.Name}' changed " +
        $"from '{change.GetOldValue<object?>()}' to '{change.GetNewValue<object?>()}'.");
}
```

**Queue semantics and threading:**

- Enqueue is fully thread-safe and needs no synchronization; `TryDequeue` is single-consumer, so each subscription must be drained by one thread.
- Each subscription owns an isolated queue, so different subscriptions can be consumed concurrently.
- Independent subscriptions may observe different relative orderings under concurrent writes: dispatch enqueues to each subscription in turn on the writing thread, so two writers can interleave differently per subscription. There is no order that all subscriptions agree on.
- The implementation is deadlock-free and never loses an enqueued item.
- The queue is unbounded with no backpressure or overflow policy, so a slow consumer causes unbounded memory growth.
- Disposal returns immediately: it wakes a waiting consumer and stops future enqueues but does not wait for buffered items, which the consumer may still drain (`TryDequeue` returns the remaining items, then `false`). An enqueue already in flight may finish after `Dispose` returns.
- Cancellation takes priority over buffered items: `TryDequeue` checks the token before dequeuing, so a cancelled call returns `false` even when items are available.

**Queue limitations:**
- `TryDequeue` is synchronous and blocks a consumer thread until an item arrives, cancellation is requested, or the subscription is disposed. Continuously draining several subscriptions therefore costs one blocked consumer thread per subscription while they are idle, whereas the observable multiplexes all its subscribers onto the dispatch thread and its scheduler.
- There is no asynchronous consumer API: `TryDequeue` returns the change through an `out` parameter, so it cannot be awaited.

### Per-Property Subscriptions

When you only care about a single property on a single subject, subscribe to that property directly instead of filtering the whole stream. Choose inline delivery for a fast callback that can safely run inside the setter. Choose scheduled delivery to isolate callback failures, and use an asynchronous scheduler to keep callback latency out of the setter.

```csharp
// Inline on the writing thread:
using var inlineSubscription = person.SubscribeToPropertyInline(
    x => x.FirstName,
    (in SubjectPropertyChange change) =>
    {
        Console.WriteLine($"FirstName is now '{change.GetNewValue<string?>()}'.");
    });

// Deferred on the selected scheduler:
using var scheduledSubscription = person.SubscribeToProperty(
    x => x.FirstName,
    (in SubjectPropertyChange change) =>
    {
        Console.WriteLine($"FirstName was changed to '{change.GetNewValue<string?>()}'.");
    },
    Scheduler.Default,
    onError: exception => Console.Error.WriteLine(exception));

// PropertyReference offers the same observer and callback forms:
var property = new PropertyReference(person, nameof(Person.FirstName));
using var scheduledReferenceSubscription = property.Subscribe(
    (in SubjectPropertyChange change) => Console.WriteLine(change.Property.Name),
    Scheduler.Default);
```

The observer can be an `IPropertyChangeObserver` implementation or a `PropertyChangeCallback` delegate; both receive the change by `in` reference. The typed overloads accept only a direct property access on the lambda parameter (`x => x.FirstName`). Chained (`x => x.Child.Foo`), captured-variable, static, field, and method selectors throw `ArgumentException`. The property must be intercepted or derived so that its changes enter the interception chain.

**Inline delivery**: `SubscribeInline(...)` and `SubscribeToPropertyInline(...)` invoke the observer on the writing thread, outside the subject lock. Concurrent writers can invoke the observer concurrently. The observer must be fast, non-blocking, thread-safe, and exception-free. An exception propagates from the setter after the value has committed and can suppress later notifications for that write.

**Scheduled delivery**: `Subscribe(..., scheduler, onError)` and `SubscribeToProperty(..., scheduler, onError)` queue accepted changes and invoke the observer serially on the scheduler. Serialization belongs to one subscription. An observer or callback shared by several subscriptions can still be invoked concurrently. Observer failures invoke `onError` on the scheduler execution thread. A synchronous `IScheduler.Schedule` failure invokes `onError` immediately on the thread calling the scheduler. When scheduling occurs while accepting a change, this is the writing thread and the handler completes before the setter returns, so a slow handler delays the setter. Error handlers shared by subscriptions may therefore be invoked concurrently and must be thread-safe. Observer and scheduler exceptions never escape to the writer, and exceptions thrown by `onError` are swallowed. A synchronous scheduling failure faults the subscription only if it wins the terminal transition. Concurrent or reentrant disposal may win instead, while the failure is still reported and `IsFaulted` remains false.

The scheduled queue is unbounded and has no backpressure or overflow policy. `PendingCount` reports accepted changes that have not yet been dequeued, and is exact once writes and deliveries are quiescent. The built-in `ImmediateScheduler.Instance` and `CurrentThreadScheduler.Instance` singletons are rejected. A custom or wrapped scheduler can still run work inline and cannot be detected; in that case the callback runs inside the setter, adds to its latency, and sees the writer's current ambient state. For asynchronous work, automatic flow of the writer's `ExecutionContext` is suppressed so it is not captured. Suppression does not clear ambient state already present on a scheduler-owned worker thread.

**Instance and lifecycle**: a per-property subscription binds to one subject instance and property name, not an object-graph path. It is dormant while the subject is detached from a context with `PropertyChangeInterceptor`, and revives when the subject is attached again. Detaching stops new changes from being accepted, but changes already queued by a scheduled subscription still drain.

**Delivery and disposal**: provided the downstream interceptor chain returns normally after commit, a write that commits after subscription returns is accepted while the subscription remains live and no earlier synchronous observer throws. A write that committed before subscription returned may not be delivered, so read the property after subscribing to observe that earlier state. Always dispose the returned handle. Inline disposal stops future delivery but may race a callback already in flight. Scheduled disposal also drops queued changes. A delivery already in flight may still invoke the observer or finish after disposal returns.

When the scheduler defers delivery, the captured old and new values can be stale by callback time. Use `change.GetCurrentValue<TValue>()` to read the property's current state when freshness matters. Under concurrent writes, delivery order can differ from commit order for every channel.

### Concurrency and Delivery

Dispatch starts on the writing thread, outside the subject lock. The pull queue and inline per-property subscriptions accept changes there. `GetPropertyChangeObservable()` also receives changes there but reschedules its subscribers by default. Scheduled per-property subscriptions invoke observers through their selected schedulers, which may run asynchronously or inline.

- **Lifecycle runs first** (with `WithLifecycle()`, included in `WithFullPropertyTracking()`): subject-typed writes reconcile ownership and drain pending lifecycle notifications before each property-change publication group. Nested same-context writes from lifecycle callbacks are supported and settle ownership before returning, while their callbacks and Registry maintenance wait for the notification drain. Property publication groups remain FIFO, so a change made during an attach callback does not necessarily publish before the structural change that introduced the subject. Within a single property group, one observer can write the graph and a later observer can see that new ownership before its queued Registry updates. Delivered values describe the recorded change; read current state when freshness matters.
- **Synchronous channel order**: each `PropertyChangeInterceptor` enqueues to its pull queues first, publishes to its Rx observable second, and dispatches to its resolved per-property subscriptions last. A scheduled per-property subscription accepts the change in that last phase, although its callback may run later or inline according to its scheduler. With aggregated contexts, the innermost interceptor resolves the per-property subscriptions, so they may accept or invoke a change before an outer context's pull queue and Rx channels. A throwing synchronous observer propagates out of the write and suppresses later deliveries in this order; queue items already enqueued remain available.
- **Ordering**: under concurrent writes to the same property, notifications may arrive out of commit order. If you need the current value, re-read the property rather than relying on the delivered new value: `change.GetCurrentValue<TValue>()` does this for you, reading the property now instead of returning the value captured when the change was created, without needing to keep a separately typed reference to the subject. `GetOldValue<TValue>()` is the value the setter observed when it started, including when the subscription raced the write. It is not necessarily the value immediately preceding the commit, so under concurrency delivered old and new pairs may not chain.
- **A derived recalculation publishes the stabilized value**: the change carries the value the recalculation committed rather than a fresh read of the getter. The getter therefore runs once per recalculation instead of twice, a throwing getter does not suppress the notification, and an interceptor that rewrites `NewValue` on that path now changes what is published.
- **Transactions replay on commit**: with `WithTransactions()`, writes captured inside a transaction do not notify during capture. They replay through the interceptor on commit and notifications fire then. If the transaction is rolled back (disposed without commit), the changes are discarded, no notifications fire, and the property keeps its pre-transaction value. If a best-effort commit partially applies and then reverts, listeners observe the apply-and-revert pair, so a consumer such as a watchdog or dirty flag must not treat the revert as a user change.

### Delivery Guarantees

Every committed write carries a `SubjectPropertyChange.Revision`: a counter that is monotonic **per subject** over committed writes, so two changes to the *same* subject are ordered by comparing it, the higher revision committed later. Revisions of *different* subjects are **not** comparable, and a change constructed outside a terminal write carries `0`, which orders against nothing.

The revision exists because arrival order can differ from commit order. Dispatch happens after the commit and outside the subject lock, so under concurrent writers a change that committed later can reach a consumer first. A consumer that has to converge on the current value compares `Revision` and keeps the higher one, or re-reads the property.

| Channel | Exactly-once | Order | Consumer runs on |
|---|---|---|---|
| Inline per-property callback | conditional (a) | arrival | writer thread |
| Scheduled per-property callback | conditional (a, c) | accepted arrival per subscription | configured scheduler |
| Observable | conditional (a) | arrival | scheduler by default |
| Pull queue | conditional (a) | arrival | consumer thread |
| `ChangeQueueProcessor`, buffer > 0 | no, latest-state-wins | arrival of survivors; per-property newest within a flush (b) | processor thread |

(a) An earlier synchronous observer, including an inline per-property observer or synchronous Rx observer, can suppress delivery for the rest of its property publication group. Queued lifecycle handlers and property groups continue after a lifecycle callback failure; such failures propagate after the drain and do not roll back the committed write.

(b) Per property, a flush collapses to the newest commit in that batch, and collapsing also applies **across** flushes: a change whose revision the property has already moved past is dropped rather than emitted. Which commits count as having moved the property past it depends on the connector, via `ChangeDeliveryRule`; see [Change Batching and Merging](connectors.md#change-batching-and-merging). A consumer that needs the current value still re-reads the property rather than assuming arrival order matches commit order.

(c) Scheduled delivery also depends on the scheduler executing accepted work. Disposal drops queued changes. A synchronous scheduling failure faults the subscription only if it wins the terminal transition. Concurrent or reentrant disposal may win instead, while the failure is still reported and `IsFaulted` remains false.

Note what the old value is and is not, on every channel. Revisions decide *which* change's old value survives a collapse, not that it is the value the property held at the preceding revision: the old value is captured by the generated setter at the call site, outside the subject lock, so under concurrent writers it can be a value that was already superseded. The new value is exact, the old value is a best-effort diff baseline. Compare `Revision` or re-read the property if you need more than that.

## Property Value Equality Check

Prevents unnecessary change notifications when a property is set to the same value:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithEqualityCheck();

var person = new Person(context);
person.Name = "John"; // Triggers change
person.Name = "John"; // No change triggered (same value)
```

Uses `EqualityComparer<T>.Default` for every property type. Reference equality is used only when the type does not provide value equality.

## Transactions

Transactions allow you to batch property changes and commit them atomically. Changes are captured during the transaction and applied together on commit, with change notifications fired after all changes are applied.

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithTransactions(); // Required for transaction support (opt-in)

var person = new Person(context);

using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
{
    person.FirstName = "John";
    person.LastName = "Doe";

    // Changes captured but not applied yet
    // Reading returns pending values (read-your-writes)
    Console.WriteLine(person.FullName); // Output: John Doe

    await transaction.CommitAsync(cancellationToken);
    // All changes applied, notifications fired
}
```

Key features:
- **Atomic commits**: All changes applied together
- **Read-your-writes**: Reading returns pending values inside the transaction
- **Notification suppression**: Change notifications fired after commit, not during capture
- **Rollback on dispose**: Uncommitted changes discarded if transaction not committed

For external source integration (OPC UA, MQTT, etc.), use `WithSourceTransactions()` from the Connectors package to write changes to external sources before applying them to the local model.

See [Transactions](tracking-transactions.md) for detailed documentation.

## Derived Property Change Detection

Automatically tracks dependencies between properties and triggers change events for derived properties when their dependencies change:

> **Prerequisite**: Automatic derived-property notifications require `WithDerivedPropertyChangeDetection()`, which is bundled in `WithFullPropertyTracking()`. Manual `RecalculateDerivedProperty()` (below) also requires it.

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }
    public partial string LastName { get; set; }

    [Derived]
    public string FullName => $"{FirstName} {LastName}";
}

var context = InterceptorSubjectContext
    .Create()
    .WithDerivedPropertyChangeDetection()
    .WithPropertyChangeSubscriptions();

context.GetPropertyChangeObservable().Subscribe(change =>
{
    Console.WriteLine($"{change.Property.Name}: {change.GetOldValue<object?>()} → {change.GetNewValue<object?>()}");
});

var person = new Person(context);
person.FirstName = "John";
// Output: FirstName:  → John
// Output: FullName:  → John

person.LastName = "Doe";
// Output: LastName:  → Doe
// Output: FullName: John  → John Doe
```

**How it works:**
- During derived property evaluation, the handler records which properties are read
- When a dependency changes, the derived property is recalculated
- If the derived value changes, a change event is triggered with `Source = null` (indicating local calculation)

Computed derived properties are projections and create no ownership edges. They may return subjects that are detached or belong to another context; reading or publishing the projection does not attach them or extend their lifetime. Use an intercepted stored property or an explicit attachment when the returned subject should be tracked. A `[Derived]` partial property with a backing field remains a stored structural property.

### Manual Recalculation

When a derived property's getter depends on data outside the interceptor system (external APIs, services, static state, etc.), automatic dependency tracking cannot detect changes. Use `RecalculateDerivedProperty()` to manually trigger recalculation:

```csharp
[InterceptorSubject]
public partial class Sensor
{
    public partial string? Label { get; set; }

    [Derived]
    public double CalibratedTemperature => _externalService.GetCalibratedTemperature();
}

// When external data changes, trigger recalculation:
var property = new PropertyReference(sensor, nameof(Sensor.CalibratedTemperature));
property.RecalculateDerivedProperty();
// Getter is re-evaluated; if the value changed, change notifications fire
```

This goes through the same pipeline as automatic recalculation: the getter is re-evaluated, dependencies are updated, and all notifications (observable, queue, per-property subscriptions, `INotifyPropertyChanged`) fire if the value changed. It is fully thread-safe and can be called concurrently with property writes. Like automatic detection, it requires `WithDerivedPropertyChangeDetection()`.

> **Internal design:** For details on the dependency graph, concurrency model, and correctness guarantees, see [Derived Property Design](design/tracking-derived-properties.md).

## Context Inheritance

Attaching a subject into a graph attaches it to that graph's context, so it participates in the same tracking and interception pipeline. This is intrinsic to the lifecycle:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithLifecycle();

var car = new Car(context);
var tire = new Tire(); // No context assigned yet

car.Tire = tire; // tire is now attached to the same context; tire.TryGetContext() returns it
```

This ensures that all objects in the subject graph share the same context, enabling consistent tracking, validation, and other interceptor features.

Configure the context fully before attaching anything to it. A subject attached to a context that has no lifecycle yet is anchored but never enters the ownership graph a later `WithLifecycle()` brings, so registering one behind an attach throws. `WithRegistry()`, `WithFullPropertyTracking()` and `WithDerivedPropertyChangeDetection()` register the lifecycle themselves and are rejected the same way.

```csharp
var context = InterceptorSubjectContext.Create();
var car = new Car(context); // anchors car on a context with no lifecycle
context.WithRegistry();     // throws: the lifecycle would never see car
```

## Subject Lifecycle Tracking

Track when subjects enter or leave the object graph, and when property references are added or removed:

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string Name { get; set; }
    public partial Person[] Children { get; set; }
}

var context = InterceptorSubjectContext
    .Create()
    .WithLifecycle()
    .WithService(() => new MyLifecycleHandler());

var person = new Person(context);
var child = new Person { Name = "Child" };

person.Children = [child]; // HandleLifecycleChange: IsContextAttach + IsPropertyReferenceAdded
person.Children = [];      // HandleLifecycleChange: IsPropertyReferenceRemoved + IsContextDetach

public class MyLifecycleHandler : ILifecycleHandler
{
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextAttach)
        {
            Console.WriteLine($"Attached: {change.Subject} via {change.Property?.Name}");
        }
        if (change.IsContextDetach)
        {
            Console.WriteLine($"Detached: {change.Subject} via {change.Property?.Name}");
        }
    }
}
```

### SubjectLifecycleChange Flags

The `HandleLifecycleChange` method receives a `SubjectLifecycleChange` with flags indicating what happened:

| Flag | Description |
|------|-------------|
| `IsContextAttach` | Subject entered the graph, through a root anchor or an incoming edge |
| `IsPropertyReferenceAdded` | A property reference to the subject was added |
| `IsPropertyReferenceRemoved` | A property reference to the subject was removed |
| `IsContextDetach` | Subject left the graph after losing anchor reachability |

Flags can be combined. For example, when a child is first assigned to a property:
- `IsContextAttach = true` and `IsPropertyReferenceAdded = true`

When the same subject is assigned to a second property:
- `IsContextAttach = false` (already in graph) and `IsPropertyReferenceAdded = true`

**Lifecycle tracking is used by:**
- **Hosting package**: Start/stop `IHostedService` implementations when attached/detached
- **Registry package**: Track subjects and properties in the registry
- **Sources package**: Subscribe/unsubscribe from external data sources
- **Derived property detection**: Initialize derived properties on attach

### Lifecycle Events

In addition to `ILifecycleHandler`, the `LifecycleInterceptor` provides events for dynamic subscribers:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithLifecycle();

var lifecycleInterceptor = context.TryGetLifecycleInterceptor();

lifecycleInterceptor.SubjectAttached += change =>
{
    Console.WriteLine($"Subject attached: {change.Subject}");
};

lifecycleInterceptor.SubjectDetaching += change =>
{
    Console.WriteLine($"Subject detaching: {change.Subject}");
};
```

**Important distinction:**
- `ILifecycleHandler.HandleLifecycleChange`: Called for **every** lifecycle change (context attach, property add, property remove, context detach)
- `SubjectAttached` event: Reports entry into the graph once per ownership lifetime
- `SubjectDetaching` event: Reports departure from the graph once per ownership lifetime

**Event timing:**

- `SubjectAttached` fires after the subject's attach lifecycle handlers have been attempted.
- `SubjectDetaching` fires before the subject's detach lifecycle handlers.

These queued events describe historical transitions. Current ownership queries may already reflect removal, a later write, or reattachment; the full historical graph is not preserved. The subject retains its context through queued teardown. A handler that threw may not have completed its initialization.

Events are useful for:
- Cache invalidation when subjects are removed from the object graph
- Dynamic subscribers that register/unregister at runtime (unlike `ILifecycleHandler` which is registered at startup)
- Integration packages (MQTT, OPC UA) that need to clean up internal state

### Thread Safety

The lifecycle interceptor is fully thread-safe. Multiple threads can concurrently write to the same structural property. Reference counts remain consistent, no subjects are orphaned, and all attach/detach callbacks fire exactly once per transition.

> **Internal design:** For details on the concurrency model and correctness guarantees, see [Lifecycle Interceptor Design](design/tracking-lifecycle.md).

### Handler Requirements

> **Important**: Both `ILifecycleHandler` methods and lifecycle events are invoked **synchronously inside a lock**. Handlers must follow these requirements:

1. **Failures are post-commit errors**: Callback failures do not roll back committed changes. Remaining queued notifications and cleanup are attempted. One failure is rethrown; multiple failures are aggregated with the primary operation failure first. See the [callback contract](design/tracking-lifecycle.md#callback-contract) for property observer boundaries and error grouping.

2. **Must be fast**: The lock is held during invocation, so blocking operations will degrade performance across the entire system. Keep handlers to prompt in-memory bookkeeping such as dictionary operations.

3. **Dispatch long-running work, and never wait for it**: If you need I/O, network calls or other slow operations, hand them to an external queue and return. Waiting for the dispatched work is what turns a slow handler into a deadlock, see [Never Wait for Topology Work on Another Thread](#never-wait-for-topology-work-on-another-thread) below.

```csharp
// Good: Fast dispatch to queue
lifecycleInterceptor.SubjectDetaching += change =>
{
    _cleanupQueue.Enqueue(change.Subject); // Returns immediately
};

// Bad: the event is an Action, so this is an async void lambda. It returns at the first await
// and the rest runs later on a pool thread, outside the lifecycle's ordering, where a thrown
// exception has nobody to observe it.
lifecycleInterceptor.SubjectDetaching += async change =>
{
    await database.DeleteAsync(change.Subject);
};
```

4. **Thread-safe operations**: Use thread-safe data structures like `ConcurrentDictionary` with atomic operations (`TryRemove`, `TryAdd`) rather than check-then-act patterns.

> **Tip**: Multiple handlers can be ordered using `[RunsBefore]`, `[RunsAfter]`, `[RunsFirst]`, and `[RunsLast]` attributes. See [Service Ordering](interceptor.md#service-ordering) for details.

### Never Wait for Topology Work on Another Thread

Topology changes on one context are serialized behind a single gate, and that gate is held across the whole write chain, every lifecycle callback and every structural getter the lifecycle reads. Code running inside one of those must not hand **structural** work to another thread and then block waiting for that thread: the worker cannot take the gate until the enclosing operation finishes, and the operation cannot finish until the worker returns.

Five places run inside the gate and can do it:

- a downstream `IWriteInterceptor`
- an `ILifecycleHandler`
- an `IPropertyLifecycleHandler`
- a `SubjectAttached` or `SubjectDetaching` handler
- a structural property's getter, which the lifecycle reads while attaching the subject

Four of the five deadlock on older versions as well; only the downstream write interceptor is new. This is a long-standing rule being written down, not a new restriction.

What the rule does not cover:

- **Scalar writes.** Only structural properties, the ones whose declared type can hold subjects, take the gate. Dispatching a scalar write to another thread and waiting for it is fine.
- **Same-thread re-entry.** The gate is reentrant, so re-entering the same context on the same thread stays legal.
- **Fetching in parallel.** Fetch on as many threads as you like, then assign on the thread already inside the operation, or after it returns. Parallelising the assignments themselves never bought anything anyway: they serialize on the gate whichever thread runs them.

A waiter whose gate holder stays blocked, never once seen running, throws `LifecycleContractViolationException` naming the pattern and the alternative. A holder that keeps running is never reported however long it takes, so a large attach is waited out rather than cut short. Behind that sits a last-resort bound of a few minutes for a holder nothing can observe at all, one that spins, polls or waits inside unmanaged code, with a different message saying it cannot tell which cause it was. Older versions hang silently instead.

```csharp
// Wrong: the assignment inside Task.Run needs the gate this interceptor is holding, so the
// worker waits for the interceptor and the interceptor waits for the worker.
public class LoadAxesInterceptor : IWriteInterceptor
{
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        next(ref context);

        if (context.Property.Name == nameof(Machine.Controller) &&
            context.Property.Subject is Machine machine)
        {
            // The worker throws LifecycleContractViolationException once the holder is seen
            // blocked throughout, and Wait() rethrows it here wrapped in an AggregateException.
            Task.Run(() => machine.Axes = BrowseAxes(machine)).Wait();
        }
    }
}
```

```csharp
// Right: hand the work off and let the write finish. The worker browses and assigns once the
// gate is free, which is also the only place the browse itself may block.
public class LoadAxesInterceptor : IWriteInterceptor
{
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        next(ref context);

        if (context.Property.Name == nameof(Machine.Controller) &&
            context.Property.Subject is Machine machine)
        {
            _pendingBrowses.Enqueue(machine); // returns immediately
        }
    }
}
```

A structural getter is the same trap in a shape that does not look like a handler at all, because the lifecycle reads it while holding the gate. This is the [dynamic property](registry.md#add-properties) form, the other being a hand-written subject supplying its own metadata:

```csharp
// Wrong: the lifecycle reads this getter while attaching the subject. If LoadAxesAsync writes
// structural properties on a worker this deadlocks; if it merely blocks, it breaks the same
// "fast, waits on nothing" contract and a concurrent write reports it.
registered.AddProperty("Axes", typeof(Axis[]),
    getValue: _ => _axes ??= LoadAxesAsync().Result,
    setValue: (_, value) => _axes = (Axis[])value!);

// Right: load first, register second. The getter only hands back what is already there.
_axes = await LoadAxesAsync();
registered.AddProperty("Axes", typeof(Axis[]),
    getValue: _ => _axes,
    setValue: (_, value) => _axes = (Axis[])value!);
```

Fetching in parallel and assigning afterwards is fine and is what most callers want, as long as neither half runs inside a lifecycle operation:

```csharp
var axes = await Task.WhenAll(controllers.Select(BrowseAxesAsync));
machine.Axes = [.. axes.SelectMany(a => a)];
```

> **Internal design:** For why this cannot be rejected up front and how the report is decided, see [Lock Ordering](design/tracking-lifecycle.md#lock-ordering).

### Reference Counting

Each subject tracks how many structural edge occurrences point to it via `GetReferenceCount()`:

```csharp
var referenceCount = subject.GetReferenceCount();
// Returns the number of structural edge occurrences pointing at this subject
// Returns 0 if not attached or lifecycle tracking is disabled
```

**Important notes:**
- The count is per occurrence, not per property: a subject listed twice in one collection counts 2
- Subjects created directly with context (root subjects) have `refs: 0` - no edge points at an anchored root
- Subjects attached via properties have their reference count incremented/decremented per occurrence added or removed
- `GetReferenceCount()` is never an attachment predicate: an anchored root reports 0 while attached, so use `TryGetContext()` to test attachment and a non-None `Executor.AttachmentAnchor` to test root-ness. An empty `GetParents()` is not a root test either: it answers the same for a root, an unattached subject, and a subject inside its own release

The `SubjectLifecycleChange` includes `ReferenceCount` after the operation. Use the flags to determine the event type:

```csharp
public void HandleLifecycleChange(SubjectLifecycleChange change)
{
    if (change.IsContextDetach)
    {
        // Subject leaving graph - safe to clean up
        CleanupResources(change.Subject);
    }
}
```

This enables proper cleanup when subjects are removed from all parent references, even when referenced by multiple properties or collections.

### Object Graph Behavior

Understanding how the lifecycle system handles different graph topologies:

**Hierarchies (Trees)**

When a branch is removed, the entire subtree cascades detachment:

```
Root
  ├── Device1  ← stays attached
  └── Device2  ← detached when Root.Device2 = null
       ├── Child1  ← cascade detached
       └── Child2  ← cascade detached
```

Siblings are protected - removing Device2 doesn't affect Device1.

**DAGs (Directed Acyclic Graphs)**

Shared nodes stay attached if they have remaining references:

```
Root
  ├── A ──┐
  └── B ──┴── Shared (refs: 2)
```

Removing A reduces Shared's refs to 1 - it stays attached via B. Removing B after A detaches Shared (refs: 0), in either removal order, and a detached subject stops resolving the graph's services.

**Cycles**

Nodes that only reference each other are released together once nothing outside the cycle reaches them:

```
Root → A → B ↔ C (internal cycle)
```

If `Root.A = null`:
- A detaches (lost its reference from Root)
- B and C detach as well, because the only thing keeping them attached is each other

Attachment follows reachability from a root rather than a reference count, so a closed cycle with no way in is released like any other unreachable subgraph. A subject that was explicitly attached stays until it is explicitly detached.

### Structural Properties and Lazy Getters

A property contributes to the object graph only when it is **intercepted** and its declared type can hold subjects. Interception exists only for `partial` properties and for dynamic properties added through the registry, so this tracks nothing at all:

```csharp
[InterceptorSubject]
public partial class Car
{
    private Tire[]? _tires;

    // Not partial, so not intercepted, so no ownership edge. The tires are never attached, never
    // registered and never given a lifecycle callback, the getter is not even called during
    // attach, and nothing warns.
    public Tire[] Tires => _tires ??= [new Tire()];
}
```

This shape is unsupported, and it fails silently: there is no exception and no diagnostic. Declare the property `partial` and fill it in the constructor instead:

```csharp
[InterceptorSubject]
public partial class Car
{
    public partial Tire[] Tires { get; set; }

    public Car()
    {
        Tires = [new Tire()];
    }
}
```

A generated `partial` property cannot be lazy in the first place, because the generator writes the getter. Two shapes do supply a getter of their own and can therefore be lazy: a hand-written `IInterceptorSubject` that builds its own `SubjectPropertyMetadata` (see [hand-written base classes](generator.md#hand-written-base-classes-and-subclasses)) and a dynamic property registered through [`AddProperty`](registry.md#add-properties).

**Where a lazy structural getter is supported, `??=` is required rather than merely allowed.** Discovery, seeding, and reconciliation can read structural getters repeatedly. Reentry, retained claims, resurrection, and retry can change which reads occur; callers must not depend on an exact count. A getter that answers with a fresh graph each call is a contract violation.

```csharp
// Correct: the same instance on every read.
registered.AddProperty("Tires", typeof(Tire[]),
    getValue: _ => _tires ??= [new Tire()],
    setValue: (_, value) => _tires = (Tire[])value!);

// Wrong: a new graph on every read.
registered.AddProperty("Tires", typeof(Tire[]),
    getValue: _ => new[] { new Tire() },
    setValue: (_, value) => _tires = (Tire[])value!);
```

Caching from inside the getter is supported, including when it writes its value back through the property's own intercepted setter. Discovery of an unattached subject reads before claiming it, so a store at that point is invisible to interception. Retained claims, resurrection, and retry can instead reach seeding while attached. A setter invoked from its own active seeding getter stores the value and defers that property's reconciliation until the getter returns.

An unstable getter costs a discarded subject. Discovery claims the first value and seeding commits the second, so the first never joins the graph: it is unregistered, has a reference count of 0, and its claim is handed back at the end of the attach, which leaves it unattached and reusable rather than stranded on the context. No exception is raised, so the wasted work is invisible.

A same-context lifecycle callback may initialize a child through an intercepted structural setter. Replacing a child delivers detach callbacks before attach callbacks. The nested setter settles ownership, while its callbacks and Registry updates wait for the queued drain. A callback exception is a post-commit notification failure, not a veto of the assignment. Nested structural work in a different context remains prohibited. See the [callback contract](design/tracking-lifecycle.md#callback-contract) for notification ordering and error behavior.

Collection edges are captured when a structural value is reconciled. Release and reachability reuse that capture; they do not repeat side effects or exceptions from the old collection's enumerator. Change topology through intercepted property assignments; mutating a collection in place does not initiate reconciliation.

> **Internal design:** For the exact read points and why the discard is cleaned up rather than reported, see [Structural Getters](design/tracking-lifecycle.md#structural-getters).

## Parent-Child Relationship Tracking

Parent relationships come from the lifecycle itself, so any context with `WithLifecycle()` (included in `WithFullPropertyTracking()` and `WithRegistry()`) answers them:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking();

var car = new Car(context);
var tire = new Tire();

car.Tires = [tire];

var parents = tire.GetParents(); // Returns ImmutableArray with [(car, "Tires", 0)]
```

Entries are per occurrence: a subject listed twice in one collection has two, one per index. Nothing is materialised for a subject until `GetParents()` is first called on it, so a consumer that never asks pays nothing. The order of the entries is unspecified; only the set of occurrences is meaningful.

This enables scenarios like:
- Finding the root object of a subject graph
- Navigating from child to parent for validation or business logic
- Building hierarchical displays in UI

## Read Property Recorder

Records which properties are accessed during a specific scope, useful for advanced dependency tracking or auditing:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithReadPropertyRecorder();

var person = new Person(context);

using var scope = ReadPropertyRecorder.Start();

var fullName = person.FullName; // Records FirstName and LastName

var accessedProperties = scope.GetPropertiesAndDispose();
// accessedProperties contains references to FirstName and LastName
```

This is primarily used internally by the derived property change detection system but can also be used for custom scenarios.

## Change Origin and Timestamps

**Change Sources**: Use the `SetValueFromSource()` extension method to apply a value coming from an external source:

```csharp
propertyReference.SetValueFromSource(
    source: mqttSource,
    changedTimestamp: DateTimeOffset.Now,
    receivedTimestamp: DateTimeOffset.Now,
    valueFromSource: newValue);
// change.Origin is ChangeOrigin.FromSource(mqttSource)
```

Source marking is per write, not through an ambient scope. This prevents feedback loops where changes from external sources are written back to those same sources.

**Atomic Timestamps**: Use `SubjectChangeContext.WithChangedTimestamp()` when several property writes belong to one logical event and should publish with the same timestamp. Without the scope, each write reads `UtcNow` separately and consumers can see distinct timestamps. Pass `null` when the source has no timestamp.

```csharp
using (SubjectChangeContext.WithChangedTimestamp(DateTimeOffset.UtcNow))
{
    position.X = 1.0;
    position.Y = 2.0;
    position.Z = 3.0;
}
```

The scope reads `UtcNow` once on entry and reuses it for every write inside (also slightly faster). Keep the scope short: the timestamp does not update, so late writes still get the original time.

## Integration with Other Packages

The Tracking package is foundational and used by:

- **Registry**: Requires `WithLifecycle()` for subject/property registration
- **Hosting**: Requires `WithLifecycle()` for hosted service management  
- **Sources**: Uses the high-performance queue via `WithPropertyChangeSubscriptions()` for synchronization
- **Validation**: Can trigger validation on property changes
- **Blazor**: Uses `WithPropertyChangeSubscriptions()` for UI updates

See the individual package documentation for integration details.
