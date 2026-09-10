# Hosting

The `Namotion.Interceptor.Hosting` package binds `IHostedService` implementations to interceptor subjects and drives them from the .NET Generic Host (`Microsoft.Extensions.Hosting`). It works with any host based application: ASP.NET Core, worker services, console apps.

## The Rule

**A hosted service runs exactly while its subject is in the graph. The `HostedServiceHandler` on the subject's context is the only thing that starts or stops it, and it disposes exactly what it created.**

Everything else on this page follows from that one sentence:

- A subject entering the graph starts its services, and a subject leaving the graph stops them. A subject that re-enters gets them back.
- The handler creates every factory attachment instance, so it disposes every factory attachment instance.
- The handler never creates a subject, so it never disposes one. It starts and stops a subject that implements `IHostedService` and leaves disposal to whoever constructed it: the dependency injection container for a subject registered through `AddSubject<T>()`, you for a subject you constructed yourself. That is what makes moving a subject through the graph non destructive.

Nothing else may start these services. In particular, do not also register a subject the handler manages with `AddHostedService<T>()`, because that is a second owner and a second start.

> **Internal design:** For the concurrency model behind this, the ordering guarantees and the deadlock shapes that are accepted rather than guarded, see [Hosted Service Ownership](design/hosting-service-ownership.md).

## Setup

Configure hosting support on the context and register it with the host:

```csharp
var builder = Host.CreateApplicationBuilder();

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithHostedServices(builder.Services);

var host = builder.Build();
await host.StartAsync();
```

`WithHostedServices()` creates the `HostedServiceHandler` for this context and registers it with the host, so the handler opens for business when the host starts and drains when the host stops. It also enables `WithLifecycle()`, which raises the context attach and detach events the handler listens to. Each context gets its own handler, so two contexts sharing one `IServiceCollection` both work, and a subject reachable from two hosting enabled contexts is still started once.

Hosting below the root needs context inheritance. `WithFullPropertyTracking()` includes `WithContextInheritance()`. If you compose the context by hand, add `WithContextInheritance()` yourself.

Without it, a subject one level below the root still starts, because the graph write that attaches it invokes the handler through the parent's context. Two things break instead, and both are silent:

- **The descent stops at level one.** Inheritance is what gives a child the parent's context, and it is that assignment which walks the child's own children into the graph. Without it nothing below the first level is ever attached, so nothing below the first level is ever started.
- **Attaching to a subject already in the graph resolves no handler.** `AttachHostedService` looks the handler up on `subject.Context`. A child that never inherited the parent's context resolves nothing there, so the factory is stored and no instance is created.

Starts and stops queued before the host starts run once it does. Each managed service has its own queue, so its own starts and stops never overlap, while unrelated services run concurrently. The one ordering guarantee across services is the one that matters for cleanup: when a subject leaves the graph, its own stop runs before the stops of the services attached to it.

## Which Pattern When

| You have | Use |
|---|---|
| A subject with no constructor dependencies, and you want the instance during configuration | Construct it and register the instance |
| A subject whose constructor dependencies only exist after `builder.Build()` | `services.AddSubject<T>()` |
| A service that should run for as long as a subject is in the graph | Factory attachment |
| A subject whose own purpose is a background loop | Let the subject implement `BackgroundService` |

### Construct and register directly

When a subject needs nothing from the container beyond its context, construct it during configuration. This is what the connector samples do, because they need the instance itself before the host is built:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithHostedServices(builder.Services);

var root = Root.CreateWithPersons(context);
context.AddService(root);

builder.Services.AddSingleton(root);
```

Constructing with the context attaches the subject there and then, so the handler already owns whatever that subject brings with it and starts it when the host starts.

The container does not dispose an instance registered with `AddSingleton(instance)`. Disposal stays with you.

### `AddSubject<T>()`

Use `AddSubject<T>()` when the subject's constructor needs services that only exist after `builder.Build()`, such as `IHttpClientFactory` or `ILogger<T>`:

```csharp
using Namotion.Interceptor.Hosting;

builder.Services.AddSubject<WeatherStation>(station =>
{
    station.PollingInterval = TimeSpan.FromSeconds(5);
});
```

It registers `T` as a singleton, forces its construction at host start, and attaches it to the context resolved from the container (or from the optional `contextResolver`). The context is applied after construction whether or not `T` declares a constructor taking an `IInterceptorSubjectContext`, so a subject with only injected dependencies is attached just the same.

If `T` also implements `IHostedService`, the handler starts it as usual, and host startup waits for that start and fails if it throws, the way `AddHostedService<T>` does. `AddSubject<T>()` also serves plain subjects that only need to exist and be attached at startup.

Three sharp edges:

- One instance per type. A second `AddSubject<T>()` for the same `T` throws, because its `configure` and `contextResolver` could not take effect. To run several instances of one type, construct them and attach them to the object graph rather than registering each one.
- If you already registered `T` yourself, `AddSubject<T>()` applies neither the context nor `configure`.
- When the resolved context has no hosting handler, because `WithHostedServices()` was never called on it or because `contextResolver` returned null, there is nothing to hand the subject to. `AddSubject<T>()` then starts an `IHostedService` subject itself at host start and stops that same instance at host shutdown. It never disposes it.

`configure` always runs before the attach `AddSubject` performs, and construction and `configure` both run inside a [startup scope](#configuration-before-startup) on the resolved context, so the subject is fully configured before anything can start it. What still differs between the shapes is whether those assignments are intercepted:

- **`T` has no constructor taking a context**, or it declares the documented `MySubject(IInterceptorSubjectContext? context = null)` parameter and never attaches with it. Nothing is attached while `configure` runs, so its assignments are not intercepted and not tracked.
- **Construction attaches the subject**, which is what the generated context constructor does. `configure` runs against an attached subject, so its assignments are intercepted and tracked.

#### What it costs at host startup

`AddSubject<T>()` registers one hosted activation per type, and when `T` implements `IHostedService` that activation waits for the subject to start before host startup moves on. The generic host starts hosted services one after another by default, so those waits do not overlap and the cost is linear in the number of such registrations, at whatever each subject's own `StartAsync` takes. A registered type that is a plain subject waits for no start and adds nothing.

Let the host start its services concurrently to get the waits overlapping:

```csharp
builder.Services.Configure<HostOptions>(options => options.ServicesStartConcurrently = true);
```

`AddSubject<T>()` deliberately does not set this for you. The option is host wide, so it changes the startup of every hosted service in the application, including ones registered by libraries that know nothing about this package, and that decision belongs to whoever owns the host.

Subjects that reach the graph as part of an object tree do not pay this cost at all. Their starts are queued on independent chains and run concurrently, so 50 subjects attached together cost about as much as one.

### A service bound to a subject

When a service is not the subject itself but should run for as long as a subject is in the graph, attach a factory to that subject. See [Factory Attachment](#factory-attachment) below.

### A subject that is its own background loop

When the subject's whole purpose is a background loop over its own properties, let it extend `BackgroundService`. See [Subject as Hosted Service](#subject-as-hosted-service) below.

## Factory Attachment

Attach a factory to a subject and the handler runs an instance of it for exactly as long as the subject is in the graph:

```csharp
public class PersonBackgroundService : BackgroundService
{
    private readonly Person _person;

    public PersonBackgroundService(Person person)
    {
        _person = person;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _person.FirstName = "John";
        _person.LastName = "Doe";

        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }
}

// Usage
var person = new Person(context);
var attachment = person.AttachHostedService(() => new PersonBackgroundService(person));
```

`AttachHostedService` stores the factory on the subject and returns a handle. When the subject is already inside a hosting enabled graph, the handler takes ownership and queues a create and start. When it is not, the factory is stored and nothing runs until the subject enters one. Each call yields its own attachment, so attaching the same factory twice gives two independently managed instances.

### The factory must construct

`() => existingInstance` is the one shape that defeats the design. The handler [stops and disposes what it created](#detach-stops-and-disposes-and-keeps-the-attachment) when the subject leaves the graph and invokes the factory again when the subject comes back, so a factory that hands out a captured instance would restart something it has already stopped.

```csharp
// Correct: a fresh instance every time the handler needs one.
subject.AttachHostedService(() => new DataSyncService(subject));

// Wrong: refused on the second start. The handler stopped this instance on detach.
var service = new DataSyncService(subject);
subject.AttachHostedService(() => service);
```

The second shape is caught rather than left to fail obscurely. The handler compares each instance the factory produces against the previous one and, on a repeat, refuses the start before calling `StartAsync`: `Current` stays null and an `InvalidOperationException` explaining the rule lands on `attachment.Fault`.

The refusal is wider than the damage, and deliberately so. A repeat is refused whatever the instance is, including a hosted service that implements neither disposable interface and was therefore never disposed and would in fact restart cleanly. Constructing on every call is a rule about the attachment; a rule that held for some service types and not others would be worse than one that fails closed.

Only the repeat is refused, so the first start of `() => service` succeeds and a subject that never leaves and re-enters the graph is never told anything is wrong. The check is also one reference comparison against the last instance, which catches the immediate repeat and nothing more. A pooling factory that alternates between two instances still hands back a stopped one, and that is not detected.

The factory runs inside the handler's transition, outside every lock, so it can read live state rather than a snapshot taken at attach time. It is deliberately narrow: `Func<T>`, no cancellation token, no service provider, not async.

### Do not detach from your own stop path

**A hosted service must not detach an attachment from inside its own stop path.** That includes anything reached through its `StopAsync`, and for a `BackgroundService` it includes the tail of `ExecuteAsync` as it unwinds.

When a subject leaves the graph, the handler stops the subject first and holds each of its attachments' stops behind that, so an attachment is never disposed underneath a subject that is still unwinding. A stop that waits for a detach of one of those attachments therefore waits for itself.

Nothing resolves that. The wedged queue never drains, the instance is never stopped and never disposed, and every later start or stop for the same service queues behind it for the rest of the process. Shutdown is the one thing the wedge cannot hold: the handler stops waiting for its outstanding stops when the host's `ShutdownTimeout` expires, so `StopAsync` returns even though the wedged service is still sitting there. That bounds the process, not the damage.

Detaching from an operation, from a configuration change, or from any path not reached through the service's own stop is fine. Nothing detects the bad shape, so it is a rule rather than a guard. Both OPC UA wrappers in this repository had it and were changed, so it is a shape that gets written rather than a hypothetical one. `HostedServiceHandlerTests.WhenASubjectOwningAnAttachmentIsStoppedByTheHost_ThenShutdownCompletesWellInsideTheTimeout` is the regression guard.

### Keep the dispose path out of the lifecycle lock

The handler disposes what the factory built, from a transition that can run while a detach cascade still holds the lifecycle lock. A service disposed this way must therefore obey two rules:

- its dispose path must not enter the lifecycle lock, directly or transitively
- it must not block on a lock that its own `SubjectDetaching` handler acquires

Writing a scalar property from a dispose path is safe. Writing a property whose type can contain subjects takes the lifecycle lock and is not safe; attaching or detaching a subject enters the same lock without being a property write at all; and so does attaching a hosted service, which takes it to settle whether the subject is still in the graph. That last one is the likeliest of the three to appear on a hosted service's own teardown path. Nothing enforces this and no test covers it, which is exactly why it is written down here. The lock order that makes it a deadlock rather than a slow path is in [Disposal from a handler transition](design/hosting-service-ownership.md#disposal-from-a-handler-transition).

### Reading the outcome

The handle carries the state of the attachment:

- `Current` is the running instance, or null when nothing is running: before the first start, after a stop, and after a start that failed.
- `Fault` is the exception from the last failed transition, or null. Only a start clears it, and only once it has got past its own guards, so that a start skipped by a shutdown does not drop a fault nobody has read yet. A stop never clears it. A start that failed followed by a clean stop therefore leaves `Fault` set with `Current` null, which is the shape of "this should be running and is not".

```csharp
if (attachment.Fault is { } fault)
{
    logger.LogError(fault, "The attached service is not running.");
}
else if (attachment.Current is { } service)
{
    // Running.
}
```

`AttachHostedService` and `DetachHostedService` return once the transition has been queued rather than run, so neither result means "started" or "stopped". Queueing is not instant on the attach side: it takes the lifecycle lock to record liveness, so it blocks for as long as any graph move already holding that lock takes. Do not call it while holding a lock that a lifecycle handler could need, and do not call it from a hosted service's own dispose path. `Current` and `Fault` are how the outcome is observed. `DetachHostedService` returns false when the attachment was not on the subject, which is what a second detach of the same handle gets. The awaitable overloads wait for the transition instead:

```csharp
var attachment = await person.AttachHostedServiceAsync(
    () => new PersonBackgroundService(person), cancellationToken);
// The instance is running, or this call threw.

await person.DetachHostedServiceAsync(attachment, cancellationToken);
// The instance has stopped, and has been disposed when it is disposable.
```

`AttachHostedServiceAsync` is transactional for its own transition: when that start faults, the attachment is removed before the exception propagates, so a `catch` block is never left owning an invisible attachment. When a context attach had already queued a create for the same attachment, the caller awaits the second transition rather than the first. A graph driven start that faults keeps the attachment with `Current` null and `Fault` set, so the next context attach retries it.

The cancellation token bounds the wait, not the work. A cancelled await leaves the transition running to completion, so a caller that gives up waiting still ends with a started instance rather than a half started one.

`GetHostedServiceAttachments()` returns an immutable snapshot of a subject's attachments.

### Detach stops and disposes, and keeps the attachment

Two different things are called detaching, and they differ in exactly one respect:

- **The subject leaves the graph.** The handler stops the instance, disposes it and clears `Current`, and **keeps the attachment on the subject**. The factory survives, so the next time the subject enters a hosting enabled graph the handler invokes it again and a fresh instance runs. This is what makes moving a subject through the graph work.
- **`DetachHostedService` or `DetachHostedServiceAsync`.** The same stop and dispose, and the attachment is removed from the subject as well, so a later context attach starts nothing.

Disposal prefers `IAsyncDisposable` and falls back to `IDisposable`, and a service that implements neither is simply dropped after its stop. A dispose that throws is logged and never propagated, because the disposal can run from inside a property write that has nothing to do with the service.

```csharp
var parent = new Parent(context);
var child = new Child();
child.AttachHostedService(() => new ChildMonitorService(child));

parent.Child = child;   // child enters the graph, an instance is created and started
parent.Child = null;    // that instance is stopped and disposed, the attachment stays
parent.Child = child;   // the factory runs again, a different instance is now running
```

Host shutdown does the same thing as a context detach for everything the handler owns, with the host's stopping token. The token bounds two things: each instance's own `StopAsync` receives it, and the handler stops waiting for its outstanding stops once it expires. So shutdown returns at `ShutdownTimeout` whatever the services do. It does not bound the services themselves. One that ignores its token keeps running after the host has stopped, is never disposed, and, if it was created by a factory attachment, is unreachable by then.

## Subject as Hosted Service

A subject can implement `IHostedService` itself, usually by extending `BackgroundService`. The handler starts it on the first context attach, stops it on the last context detach, and never disposes it.

```csharp
[InterceptorSubject]
public partial class SensorMonitor : BackgroundService
{
    public partial double Temperature { get; set; }
    public partial double Humidity { get; set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Temperature = ReadTemperatureSensor();
            Humidity = ReadHumiditySensor();

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}

// Usage
var monitor = new SensorMonitor(context);
// Starts when the subject is in the graph, stops when it leaves or the host stops.
```

This pattern fits when the subject's entire purpose is to run a background task that updates its own properties. Two contract requirements come with it:

**`ExecuteAsync` must tolerate being run more than once.** A subject that leaves the graph and re-enters it is restarted in place, on the same instance, because the handler does not dispose subjects. A plain `BackgroundService` handles this. Anything that latches a "done" or "disposed" flag does not, so reset per run state at the top of `ExecuteAsync` rather than in the constructor.

**A hand written `IHostedService` must honour `StopAsync`.** The handler passes `CancellationToken.None` to `StartAsync`, so a service that captured the `StartAsync` token as its only stop signal will never be cancelled. `BackgroundService` is unaffected, because it cancels its own execution token in `StopAsync`.

## Configuration Before Startup

A subject that takes the context in its constructor is attached during construction, which queues its service start. Object initializers, property assignments and deserializers all run afterwards, so the service can start against a subject that is not configured yet.

Either build the subject detached, configure it and attach it once it is ready, or keep the context-taking constructor and wrap the work in a startup scope:

```csharp
using (context.DeferHostedServiceStartup())
{
    var person = new Person(context) { FirstName = "John", LastName = "Doe" };
    person.AttachHostedService(() => new PersonBackgroundService(person));
}
```

Attaching still takes effect immediately, so the subject joins the graph and is visible to the registry and to sources. Only the start waits for the block to exit, and leaving the block releases it even when configuration throws: the scope says when a subject is ready, never whether it is fit to run. Validating configuration stays with the service and its caller.

Three rules have consequences:

- Do not await a captured service's start, or its detach, inside its own block. Both wait for that start, which cannot run until the block exits.
- A scope nobody disposes holds its starts until the host shuts down.
- `DeferHostedServiceStartup()` returns null on a context without hosting support, and `using` accepts that.

`AddSubject<T>()` and HomeBlaze's configuration loading already wrap their own construction this way, so nothing extra is needed there. The exact contract, including nesting, disposal order and what happens to a start still waiting when its subject leaves the graph, is in [Startup Scopes](design/hosting-service-ownership.md#startup-scopes).

## Deferred Starts and Startup Completion

Attaching a hosted service queues its `StartAsync` without waiting for it. Any subsystem that treats "the graph has finished starting" as a completion point would otherwise pass that point while a queued start is still on its way in.

A subsystem says so by implementing `IStartupCompletionDeferrer` and registering it on the context. Before queueing a start, the hosting layer takes a hold on every deferrer reachable from the subject's context and releases it once the start has run, including when the start is skipped because the host is shutting down and when it throws. This applies to every start the handler queues, whether it came from an explicit attach or from a subject entering the graph, and to the awaiting and fire and forget attach paths alike: awaiting the start blocks the caller, but it does not block whatever else is deciding that startup is finished, so the gap still needs holding open.

Holds are counted, so nested attaches compose: a service that attaches children during its own `StartAsync` takes their holds before its own is released.

`SourceMonitor` is the one implementation in this repository. It is what makes an attached source count towards source registration from the moment it is attached rather than from the moment it finally starts, so a synchronization wait cannot complete against a tree whose sources have not registered yet. See [Applications That Create Sources at Runtime](connectors-monitoring.md#applications-that-create-sources-at-runtime).

A deferrer runs inside the lifecycle lock, so neither `DeferCompletion` nor the hold's `Dispose` may block on anything that needs that lock to make progress, and a lock of the deferrer's own is allowed only where its order against the lifecycle lock is already fixed. The full constraint is on `IStartupCompletionDeferrer`, and an implementation that follows it cannot take part in the deadlock: see [A deferrer that takes a lock of its own](design/hosting-service-ownership.md#4-a-deferrer-that-takes-a-lock-of-its-own).

## For Library Authors

If you're building a library that provides hosted subjects, see [Subject Guidelines - Implementing Hosted Subjects for DI](subject-guidelines.md#implementing-hosted-subjects-for-di) for the recommended pattern using `AddSubject<T>()`.
