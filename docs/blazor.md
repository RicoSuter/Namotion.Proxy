# Namotion.Interceptor.Blazor

Automatic change tracking and re-rendering for Blazor applications using interceptor subjects.

## Getting Started

### Installation

Add the `Namotion.Interceptor.Blazor` package to your Blazor project and ensure your context is configured with property tracking:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithReadPropertyRecorder();  // Required for TrackingScope
```

### Basic Usage

Wrap your content in a `TrackingScope` to automatically re-render when tracked properties change:

```razor
@using Namotion.Interceptor
@using Namotion.Interceptor.Blazor

<TrackingScope Context="@person.TryGetContext()">
    <p>@person.FirstName @person.LastName</p>
    <p>Age: @person.Age</p>
</TrackingScope>
```

When any property accessed inside the scope changes, the component automatically re-renders.

### Debug Mode

Enable `ShowDebugInfo` to visualize tracking during development:

```razor
<TrackingScope Context="@context" ShowDebugInfo="true">
    @* Content shows green border and glow on updates *@
    @* Hover to see list of tracked properties *@
</TrackingScope>
```

## Features

### Automatic Property Tracking

Properties read during render are automatically tracked. No manual subscription required.

```razor
<TrackingScope Context="@context">
    @* These properties are automatically tracked: *@
    <p>@motor.Speed RPM</p>
    <p>@motor.Temperature °C</p>
</TrackingScope>
```

### Derived Property Support

Works seamlessly with `[Derived]` properties. When underlying dependencies change, derived properties update and trigger re-renders.

```csharp
[InterceptorSubject]
public partial class Motor
{
    public partial int Speed { get; set; }

    [Derived]
    public string Status => Speed > 0 ? "Running" : "Stopped";
}
```

### Hierarchical Tracking

Child subjects are tracked automatically. Changes to nested properties trigger re-renders.

```razor
<TrackingScope Context="@context">
    @* Changes to any child motor property will trigger rerender *@
    @foreach (var motor in factory.Motors)
    {
        <p>@motor.Name: @motor.Speed RPM</p>
    }
</TrackingScope>
```

### Session Isolation

Each browser session has isolated tracking. Server-side Blazor with multiple concurrent users works correctly.

## Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `Context` | `IInterceptorSubjectContext` | Required. The context to subscribe to for property changes. |
| `ThrottleMilliseconds` | `int` | Optional. Minimum time between rerenders in ms. Default: 33 (~30fps). |
| `ShowDebugInfo` | `bool` | Optional. Shows visual debugging (border, tracked properties tooltip). |
| `ChildContent` | `RenderFragment` | The content to render within the tracking scope. |

## Guarantees

- **At-Least-Once Rerender**: Any property change affecting tracked content triggers a re-render
- **Session Isolation**: No cross-talk between browser sessions in server-side Blazor
- **Thread Safety**: Safe for concurrent access from multiple threads

## Limitations

### Excessive Re-renders for Child Subject Changes

Due to Blazor's architecture, `AsyncLocal` values are lost when RenderFragments are invoked in a different async context (e.g., virtualized DataGrid cells, deferred rendering).

To work around this, TrackingScope tracks **subjects** (not just properties) and re-renders when any descendant of a tracked subject changes. This means:

```razor
<TrackingScope Context="@context">
    @* Only Speed is displayed, but changes to Temperature also trigger rerender *@
    <p>@motor.Speed RPM</p>
</TrackingScope>
```

If `motor.Temperature` changes (even though it's not displayed), the component still re-renders because `motor` was accessed.

**Impact**: More re-renders than strictly necessary in some scenarios. The guarantee is "at-least-once" not "exactly-once".

### Ideal Solution (Pending Blazor API)

The ideal solution would be for Blazor to preserve `ExecutionContext`/`AsyncLocal` through RenderFragment invocations. This would allow tracking **specific properties** instead of entire subjects.

We have proposed this enhancement to the Blazor team:
- [Feature Request: Preserve AsyncLocal/ExecutionContext Through RenderFragment Invocations](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/docs/issues/BlazorTrackingApi_GitHub.md)

If Blazor adds support for ambient render context (either via ExecutionContext preservation or a `RenderTreeBuilder.PushAmbientValue<T>()` API), this library can be updated to provide true fine-grained reactivity like Vue.js or MobX.

---

# Implementation Details

## Architecture Overview

The tracking system consists of three core components:

| Component | Purpose |
|-----------|---------|
| `TrackingScope` | Blazor component that wraps content and triggers re-renders |
| `ReadPropertyRecorder` | Interceptor that records property reads using `AsyncLocal<T>` |
| `ReadPropertyRecorderScope` | Disposable scope that collects property references during render |

## Data Flow

```
┌─────────────────────────────────────────────────────────────────┐
│                     Render Cycle                                 │
│                                                                 │
│  ShouldRender()              OnAfterRender()                    │
│       │                            │                            │
│       ▼                            ▼                            │
│  StartRecording()            StopRecording()                    │
│       │                            │                            │
│       ▼                            ▼                            │
│  AsyncLocal<Scopes>          Swap collecting ↔ active           │
│  push scope                  Extract tracked subjects           │
│       │                      Clear ancestor cache               │
│       ▼                            │                            │
│  Property reads ─────────────────► _collectingProperties        │
│  via interceptor                                                │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│                   Property Change Detection                      │
│                                                                 │
│  PropertyChangeInterceptor ─── Subscribe ──► TrackingScope      │
│        │                                          │             │
│        ▼                                          ▼             │
│   change.Property              1. Check _properties directly    │
│                                2. Check IsDescendantOfTracked   │
│                                          │                      │
│                                          ▼                      │
│                                   BFS Parent Walk               │
│                                   (all branches)                │
│                                          │                      │
│                                          ▼                      │
│                                   TriggerRerender()             │
│                                   (coalesced)                   │
└─────────────────────────────────────────────────────────────────┘
```

## Design Decisions

### AsyncLocal-Based Recording

**Problem**: Context-based storage (singleton) caused cross-client interference in server-side Blazor.

**Solution**: Use `AsyncLocal<T>` to maintain per-async-context isolation. Each browser session gets its own recording scope.

```csharp
private static readonly AsyncLocal<List<ReadPropertyRecorderScope>?> _activeScopes = new();
```

### Subject Descendant Tracking

**Problem**: Properties read during deferred RenderFragment invocation (e.g., DataGrid virtualization) aren't captured by AsyncLocal because the async context is lost.

**Solution**: Track subjects whose properties are read. When any property changes, check if the changed subject is a descendant of any tracked subject using BFS parent traversal.

### BFS Parent Walking

**Problem**: A subject can have multiple parents (multi-parent graphs). Following only one path misses ancestors via other branches.

**Solution**: Use BFS (breadth-first search) with a `Queue<T>` to explore ALL parent branches.

```csharp
var visited = new HashSet<IInterceptorSubject>();
var queue = new Queue<IInterceptorSubject>();
queue.Enqueue(subject);

while (queue.Count > 0)
{
    var current = queue.Dequeue();
    foreach (var parent in current.GetParents())
    {
        if (trackedSubjects.Contains(parent.Property.Subject))
            return true;  // Found tracked ancestor
        if (visited.Add(parent.Property.Subject))
            queue.Enqueue(parent.Property.Subject);  // Continue BFS
    }
}
```

### Conservative Caching

**Problem**: Caching false results leads to missed re-renders when the parent graph changes.

**Solution**: Only cache positive (true) results. False results are always recomputed.

```csharp
// Cache only positive results
if (found)
    _ancestorCache.TryAdd(subject, true);

// Never cache false - graph may change
return false;
```

### Rerender Coalescing

**Problem**: Rapid property changes can cause excessive re-renders.

**Solution**: Property changes are buffered within a configurable time window (`ThrottleMilliseconds`, default 33ms). All changes in the window are checked, and at most one rerender is triggered per window.

## Thread Safety

All shared state is protected:

- `Lock` for `_properties`, `_trackedSubjects` swaps
- `ConcurrentDictionary` for `_ancestorCache`
- `volatile` flags for `_disposed`, `_rerenderPending`
- Disposal guards to prevent callback exceptions during component teardown
