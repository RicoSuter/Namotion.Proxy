# Source Monitoring

Every `ISubjectSource` reports whether it's still synchronizing, has completed its initial load, or has stopped. The `Namotion.Interceptor.Connectors.Monitoring` namespace turns that per-source state into a per-tree registry, a typed event stream, and an awaitable primitive, so an application can ask "is my tree live yet" instead of polling `TryGetSource()` in a loop.

## Getting Started

Add source monitoring to the tree root context, then await synchronization from anywhere holding a reference to the tree (or a subtree):

```csharp
using Namotion.Interceptor.Connectors;          // WithSourceMonitoring
using Namotion.Interceptor.Registry;            // WithRegistry
using Namotion.Interceptor.Tracking;            // WithFullPropertyTracking

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithSourceMonitoring(builder.Services);

var root = new Root(context);
builder.Services.AddSingleton(root);
builder.Services.AddOpcUaSubjectClientSource<Root>("opc.tcp://localhost:4840", "opc");
builder.Services.AddHostedService<Worker>();
```

```csharp
using Namotion.Interceptor.Connectors.Monitoring;

internal sealed class Worker(Root root, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        switch (await root.WaitForSynchronizationAsync(stoppingToken))
        {
            case SourceSynchronizationResult.Synchronized:
            case SourceSynchronizationResult.Stale:
                // every source delivered its initial load, so the values are real
                break;

            case SourceSynchronizationResult.Incomplete:
                logger.LogWarning("A source never delivered: part of the tree may hold defaults.");
                break;
        }
    }
}
```

## Waiting on Part of the Tree

`WaitForSynchronizationAsync` is an extension on `IInterceptorSubject`, not on the context: the subject you call it on is the wait's anchor, and only sources in scope of that anchor are waited on. Change the anchor from the tree root to a subtree to wait on only that branch:

```csharp
var result = await root.Kitchen.WaitForSynchronizationAsync(stoppingToken);
```

A source is in scope for an anchor when its root subject and the anchor lie on the same root-to-leaf path, in either direction: the source's root is an ancestor of (or is) the anchor, so it may claim into the awaited branch, or the source's root sits inside the anchor's own subtree. A source on a sibling branch is in neither set, so a source that never connects, or fails, on an unrelated branch never blocks a wait scoped to a branch it cannot claim into.

Given that scope, the rule is short: once registration is complete, a wait completes when no in-scope source is `Synchronizing`. `Synchronized` and `Stopped` both count as settled, so a scope matching no source at all, or one where every source has stopped, completes rather than blocks.

## What the Result Means

Every in-scope source is `Synchronized` or `Stopped` when a wait completes, which makes these four cases total:

| in-scope sources when the wait completes | result | meaning |
|---|---|---|
| all currently `Synchronized` | `Synchronized` | delivered and still live |
| none (empty scope) | `Synchronized` | nothing to wait for |
| all synchronized at least once, at least one now `Stopped` | `Stale` | delivered, values may be out of date |
| at least one `Stopped` that never synchronized | `Incomplete` | never received data, may still hold CLR defaults |

Worst wins: one `Incomplete` source makes the whole branch `Incomplete`. `Incomplete` is the enum's zero value, so an unassigned field defaults to the most pessimistic answer.

The verdict is per source, not per property: it answers whether every source completed its initial load, not whether every property holds a value from the external system. A load that resolved only some of its nodes still reports `Synchronized`.

`Incomplete` is not "not yet". `Stopped` is terminal, so awaiting again returns it immediately; handle it in a switch, not a retry loop. Only taking the dead source out of scope changes the verdict, by disposing or unregistering it. Adding a replacement alongside it does not, because worst wins.

Host shutdown moves a branch through all three answers, so a consumer that runs during shutdown must treat `Stale` as a success and cannot read `Synchronized` as proof of anything. Hooked on `ApplicationStopping` it still sees `Synchronized`, since that fires before hosted services stop; after they stop it sees `Stale`; and once the container disposes them they unregister, so the scope empties and it reads `Synchronized` again.

Two things about that scoping matter before you rely on a wait.

No wait can complete before source registration is complete, whatever its scope: that is the first condition checked, ahead of any scope evaluation. So until `CompleteSourceRegistration()` has run (and any `DeferWaitCompletion()` holds are released), every wait blocks, empty scope or not.

A pending wait is not frozen to the sources that existed when registration completed. It is re-evaluated on every registration, unregistration and state change, so a source registering later can block it. Only a wait that has already completed is frozen, since a completed task cannot be un-completed.

An empty scope is the expected answer for a branch with no external source, such as configuration or computed state. It is also what you get from a wrong anchor, a source that was never created, one created but never started, and one that stopped and was then disposed, since disposal unregisters. All report `Synchronized`, and the library cannot tell them apart, so treat that result as "nothing here is still loading" rather than as proof the branch is live.

Detaching the awaited branch empties its scope the same way, and there the answer is actively wrong rather than merely uninformative: scope is resolved through the parent graph, so a source rooted inside the branch leaves scope along with it, and a wait pending on that branch completes as `Synchronized` even though that source is still loading and has delivered nothing. Do not detach a subtree while something is waiting on it.

A source that fails terminally sets `Stopped` but is not disposed, so it stays registered and its branch keeps reporting whatever the stop earned. The same applies to a source whose registration itself failed. That is correct while nothing replaced it, and it is not free: the monitor holds every registered source, and through it the subtree under that source's root, until the source is disposed. If you do replace it, dispose the one you are replacing: the monitor cannot distinguish an abandoned source from a dead one, so leaving both registered keeps the branch on the dead one's verdict forever, and keeps both alive.

A subject belongs to exactly one context, and attaching it into a second context throws. Two trees that share subjects must therefore share one context, in which case one monitor serves both trees.

## Reading Per-Property State

`property.GetSourceState()` reads a property's synchronization state, derived from its owning source with no per-property storage:

```csharp
using Namotion.Interceptor.Connectors.Monitoring;

var property = new PropertyReference(root.Kitchen, nameof(Kitchen.Temperature));
var state = property.GetSourceState();
```

See the XML docs on `SourceState` for what each member means. `Synchronized` specifically means the owning source completed its initial load, and what that guarantees differs per protocol; see [What Synchronized Means per Protocol](#what-synchronized-means-per-protocol).

`GetSourceState()` is only fully meaningful once the branch containing the property has been awaited through `WaitForSynchronizationAsync`. Before any claiming has happened, `Unclaimed` cannot be distinguished from "not yet claimed, but will be." After a claim it reports `Synchronizing`, so "will synchronize, still loading" is already distinguishable from "no source" even before the wait completes.

## What Synchronized Means per Protocol

[OPC UA](connectors-opcua-client.md)'s `LoadInitialStateAsync` batch-reads every owned property from the session before returning, and WebSocket's applies the full-state message the server sends on connect. For both, reaching `Synchronized` means real values were confirmed from the external system.

[MQTT](connectors-mqtt.md) is weaker: `Synchronized` means the subscriptions are established, not that retained values have arrived. Retained messages are indistinguishable from live ones and neither 3.1.1 nor 5.0 signals when a topic's retained backlog is exhausted, so there is nothing to wait for. Raising QoS does not help, since it governs delivery of messages that are sent. [#418](https://github.com/RicoSuter/Namotion.Interceptor/issues/418) tracks an opt-in per-topic barrier.

## Applications That Create Sources at Runtime

There are two ways to declare registration complete, and no third: pick by whether your sources exist before host startup finishes.

`WithSourceMonitoring(builder.Services)` completes registration once `IHostApplicationLifetime.ApplicationStarted` fires, which covers every source existing by the end of host startup - DI-registered or attached to the subject graph. Attaching queues a source's `StartAsync` rather than running it inline, so the hosting layer holds registration open from the attach until that start has run. Nested attaches compose.

Because that gate opens only after every `IHostedService.StartAsync` has returned, **do not await `WaitForSynchronizationAsync` inside a `StartAsync` override, or before `host.RunAsync()`** when using this overload: registration can never complete while the host is still starting, so the wait blocks host startup and neither ever finishes. Await it from `ExecuteAsync`, or from any code that runs once the host is up, as the sample above does.

An application that creates its sources dynamically, during startup but after the DI container is built, uses the parameterless `WithSourceMonitoring()` overload instead and declares registration complete itself:

```csharp
using Namotion.Interceptor.Connectors.Monitoring;

protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    await LoadAsync(stoppingToken);
    _context.CompleteSourceRegistration();
}
```

A `SourceMonitor` is born with one registration hold already taken, at `WithSourceMonitoring()` time, during context configuration and before the host is even built. That is what makes the ordering above work regardless of hosted service construction order: no wait can complete until something explicitly releases that hold.

For a later batch, more devices discovered after the first `CompleteSourceRegistration()` call, take a further hold with `DeferWaitCompletion()` so pending waits do not complete mid-batch:

```csharp
using var hold = _context.DeferWaitCompletion();
// create and start this batch's sources
```

Taking a hold blocks any wait that is still pending, but it never un-completes a wait that has already finished. Dispose the hold once the batch has registered.

That is also the limit of this feature: waits describe startup, not steady state. A source created long after startup cannot re-block a wait that already completed, so follow sources that come and go at runtime through the event stream instead.

## Observing Changes

Two ways to observe state, depending on what you're holding.

A consumer holding one specific source subscribes to its `StateChanged` directly. That event is declared on `ISubjectSource`, so a connector-specific handle such as `IOpcUaSubjectClientSource` must be cast first. It fires synchronously inside the source's transition lock, so handlers must be observe-only: no blocking, and no causing a transition of any source.

An aggregate consumer, a wait, a diagnostics dashboard, an index across every source in the tree, subscribes to the monitor stream instead:

```csharp
using Namotion.Interceptor.Connectors.Monitoring;

using var subscription = context.GetSourceMonitor().Subscribe(sourceEvent =>
{
    // handle sourceEvent
});
```

Delivery is queued per subscription and runs outside every lock, so a slow handler delays only itself. Handlers run on a thread-pool thread, so a UI consumer must marshal. `Subscribe` also returns the sources registered at that moment (`SourceSubscription.Sources`), captured atomically with the subscription, so seeding from that snapshot and then processing the stream sees every source exactly once.

`OldState` and `NewState` describe one transition and must not be applied blindly: the ownership compare-and-set and the enqueue are not atomic, so events for one property can arrive inverted. Apply `CurrentState`, which re-resolves at read time.

So the stream is not a ledger. It cannot reconstruct a history, because the arrival order is not the transition order. Build a view of current state on it, not a log.

`CurrentState` reports ownership only, never whether the subject is still in the object graph - ask `ISubjectRegistry.TryGetRegisteredSubject` for that. On `StateChanged`, which carries no property, it is the source's own state. The two rarely need distinguishing: built-in connectors release their claims on detach, which publishes `PropertyReleased`.

## The State Model, Transitions, and Delivery Contract

```csharp
public enum SourceState
{
    Unclaimed,
    Synchronizing,
    Synchronized,
    Stopped
}
```

See the XML docs on `SourceState` for what each member means. On a source itself (as opposed to a property, via `GetSourceState()`), `Unclaimed` never occurs.

State follows the pump: construction starts at `Synchronizing`, `StartBuffering()` returns to `Synchronizing` on every connect and reconnect, a completed initial load reaches `Synchronized`, and a pump failure falls back to `Synchronizing` before the retry delay. A connector that detects a loss *before* it buffers calls the protected `ReportConnectionLost()` so `State` does not sit at `Synchronized` for the whole reconnect window; OPC UA does this from its keep-alive handler and its manual reconnect path.

`Stopped` is terminal: no further transition succeeds, and `SubjectSourceBase.RunAsync` sets it in a `finally` so it fires on every exit path. A guard in `SubjectSourceBase.StartAsync` enforces this, because `BackgroundService` would otherwise happily run the pump again against a fresh token. Create a new instance rather than restarting a stopped one.

Two timestamps sit next to `State`, and they answer different questions.

`StateChangeTime` moves on every transition and is stamped at construction, so it is always meaningful. Read with `State` it says how long the current state has lasted: `Synchronized` plus T reads as in sync since T, `Synchronizing` plus T reads as stale since T. That is the stale-duration question operators ask during an incident.

`LastSynchronizedAt` records when the most recent initial synchronization completed, or `null` if it never has. Because it is stamped only on the way into `Synchronized` and never cleared, it answers whether a good period ever began, not when the current one did: a source that synchronized a week ago and dropped an hour ago still reports the week. It is load-bearing rather than diagnostic, and is what lets a completed wait tell `Stale` from `Incomplete` (see [What the Result Means](#what-the-result-means)).

Neither can be derived from the other, which is why both exist: `StateChangeTime` cannot say whether the source was ever synchronized, and `LastSynchronizedAt` cannot say when synchronization was lost.

Reading `State` and `StateChangeTime` in sequence is two separate snapshots. Each read is internally consistent. These wall-clock timestamps come from `UtcNow`, so a system clock adjustment can move a later timestamp backward. The pair is not guaranteed to be from the same instant, so do not build a decision that depends on it being so.

### Diagnostics and State answer different questions

`State` answers "can I trust these values". It describes the inbound direction only, it is what `WaitForSynchronizationAsync` gates on, and it drives program behaviour.

`ISubjectConnector.Diagnostics` answers "what is the transport doing", in both directions, and gates nothing. It is where liveness, throughput, the outbound backlog and the last error live. See [Connector Diagnostics](connectors.md#connector-diagnostics) for the full member tree.

The outbound backlog is the clearest case of the split: `Diagnostics.OutboundRetries.Depth` can be non-zero during entirely normal synchronized operation, because a queued write says nothing about whether the model mirrors the external system.

Read the two together to tell a reported network outage from a connected source that is still loading: the first is `IsOperational == false`, the second is `IsOperational == true` with a state of `Synchronizing`. `IsOperational == null` means the source is running and has not published liveness, so it identifies neither condition and does not change the source's synchronization state. A stopped source always reads `false`.

### The Event Stream

Every source metadata change is one `SourceEventKind`:

| Kind | `Property` | When |
|---|---|---|
| `SourceRegistered` | `null` | A source registered, which happens when it starts. |
| `SourceUnregistered` | `null` | A source unregistered, which happens when it is disposed. |
| `StateChanged` | `null` | A source's own state changed. |
| `PropertyClaimed` | set | A source took ownership of a property. |
| `PropertyReleased` | set | A source gave up ownership of a property. |

The monitor enqueues every event onto each subscriber's own queue; each subscription drains its own queue on a single worker at a time, so a slow handler delays only that subscription. There is no ordering guarantee across subscriptions: two subscribers can observe events in different relative orders under concurrent activity.

## Worked Sample: Availability Attributes

A common pattern: expose an `IsAvailable` flag on the bound property itself, as a derived attribute, rather than a separate per-device shadow property.

```csharp
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class Device
{
    public partial double Temperature { get; set; }
}
```

`RegisteredSubjectProperty.AddDerivedAttribute` attaches the attribute; `TryGetAttribute` reads it back:

```csharp
using Namotion.Interceptor.Registry;

var isAvailable = device
    .TryGetRegisteredProperty(nameof(Device.Temperature))?
    .TryGetAttribute("IsAvailable")?
    .GetValue();
```

Subscribe before any source starts claiming: `PropertyClaimed` is the only way this updater learns about a claim, and there is no way to enumerate what a source has already claimed. A property claimed earlier never gets the attribute.

The updater subscribes once and reacts to every event kind that can change a property's availability. `SourceRegistered` and `SourceUnregistered` carry no property, so nothing needs applying until a property is actually claimed:

```csharp
using System.Collections.Concurrent;
using System.Collections.Immutable;
using Namotion.Interceptor;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Registry;

public sealed class DeviceAvailabilityUpdater : IDisposable
{
    private const string IsAvailableAttribute = "IsAvailable";

    // Properties each source has claimed. Needed only for StateChanged, which carries no property
    // (see Handle below).
    private readonly ConcurrentDictionary<ISubjectSource, ImmutableHashSet<PropertyReference>> _bySource = new();

    // Backing store for each property's IsAvailable attribute. The attribute's setValue writes
    // here and its getValue reads from here, so a write goes through the interceptor chain and
    // publishes a change. Storing the value before calling SetValue instead would leave getValue
    // already returning the new value, and the equality check that WithFullPropertyTracking
    // installs would drop the write as a no-op - the attribute would read correctly but never
    // notify.
    private readonly ConcurrentDictionary<PropertyReference, bool> _isAvailable = new();

    private readonly SourceSubscription _subscription;

    public DeviceAvailabilityUpdater(SourceMonitor monitor)
    {
        _subscription = monitor.Subscribe(Handle);
    }

    public void Dispose() => _subscription.Dispose();

    private void Handle(SourceEvent sourceEvent)
    {
        switch (sourceEvent.Kind)
        {
            case SourceEventKind.PropertyClaimed:
                Track(sourceEvent.Source, sourceEvent.Property!.Value);
                Apply(sourceEvent.Property!.Value, sourceEvent.CurrentState);
                break;

            case SourceEventKind.PropertyReleased:
                Apply(sourceEvent.Property!.Value, sourceEvent.CurrentState);
                break;

            case SourceEventKind.StateChanged:
                if (_bySource.TryGetValue(sourceEvent.Source, out var properties))
                {
                    foreach (var property in properties)
                    {
                        Apply(property, property.GetSourceState());
                    }
                }
                break;
        }
    }

    private void Track(ISubjectSource source, PropertyReference property)
    {
        _bySource.AddOrUpdate(
            source,
            _ => ImmutableHashSet.Create(property),
            (_, existing) => existing.Add(property));

        // First sighting of this property: give it an IsAvailable attribute.
        var registeredProperty = property.TryGetRegisteredProperty();
        if (registeredProperty is not null && registeredProperty.TryGetAttribute(IsAvailableAttribute) is null)
        {
            registeredProperty.AddDerivedAttribute(
                IsAvailableAttribute, typeof(bool),
                getValue: _ => _isAvailable.TryGetValue(property, out var available) && available,
                setValue: (_, value) => _isAvailable[property] = value is true);
        }
    }

    private void Apply(PropertyReference property, SourceState state)
    {
        property.TryGetRegisteredProperty()?
            .TryGetAttribute(IsAvailableAttribute)?
            .SetValue(state == SourceState.Synchronized);
    }
}
```

For the two property-kind events, `sourceEvent.CurrentState` resolves through `GetSourceState()` for that specific property (see [Observing Changes](#observing-changes)), so `Apply` can use it directly. `StateChanged` carries no property at all: `sourceEvent.CurrentState` there is the source's own state and says nothing about any individual property, so the handler instead walks `_bySource` for that source and calls `property.GetSourceState()` on each claimed property directly.
