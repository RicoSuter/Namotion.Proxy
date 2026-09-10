# Blazor RenderFragment Context Tracking

Status: Design Document / Investigation
Date: 2026-05-20

## Problem Statement

### The general problem

`AsyncLocal<T>` values set during a Blazor component's render are lost when `RenderFragment` delegates passed as component parameters are invoked in a later render batch. This is a general Blazor framework limitation that affects any library or application relying on `AsyncLocal<T>` during rendering:

- **Logging scopes**: `ILogger.BeginScope()` uses `AsyncLocal` internally. Log entries emitted from deferred RenderFragments lose their scope context.
- **Auth/tenant context**: Multi-tenant applications that set `AsyncLocal<TenantId>` during rendering lose tenant context in deferred fragments.
- **Telemetry correlation**: `Activity`/`DiagnosticSource` correlation IDs stored in `AsyncLocal` are lost across render batch boundaries.
- **Reactive UI tracking**: Libraries that record property reads during rendering (similar to Vue.js/MobX reactivity) rely on `AsyncLocal` scopes and lose tracking in deferred fragments.

This is NOT the same as `AsyncLocal` loss in async lifecycle methods (`OnInitializedAsync`, etc.) or event handlers, which is about async continuations. This is specifically about **stored RenderFragment delegates** being invoked outside their original `ExecutionContext`.

**Affected versions:** .NET 8, 9, 10, and 11 preview. No fix has been introduced in any version.

### Minimal reproduction

A standalone Blazor app demonstrating the problem with no external dependencies:

```csharp
// AsyncLocalDemo.razor
@page "/demo"
@using System.Threading

<h3>AsyncLocal in RenderFragments</h3>

<p>Direct read (same batch): @_directValue</p>

<Virtualize Items="_items" Context="item">
    <p>Virtualized read: @GetAsyncLocalValue() - Item @item</p>
</Virtualize>

@code {
    private static readonly AsyncLocal<string> _ambientValue = new();
    private string? _directValue;
    private List<int> _items = Enumerable.Range(1, 100).ToList();

    protected override void OnParametersSet()
    {
        _ambientValue.Value = "hello from parent";
    }

    protected override bool ShouldRender()
    {
        _ambientValue.Value = "hello from parent";
        return true;
    }

    // Called directly during this component's render - works
    // Called from Virtualize's deferred template - returns null
    private string GetAsyncLocalValue()
    {
        return _ambientValue.Value ?? "(null - AsyncLocal lost!)";
    }
}
```

When the user scrolls, `Virtualize<T>` renders new items in a new batch. The `ItemContent` template calls `GetAsyncLocalValue()`, but `_ambientValue.Value` is `null` because the `AsyncLocal` was set in the parent's render context, which is no longer active.

### How this applies to Namotion.Interceptor

TrackingScope in `Namotion.Interceptor.Blazor` uses this exact pattern: it sets an `AsyncLocal<List<ReadPropertyRecorderScope>>` during rendering, and the `ReadPropertyRecorder` interceptor checks this AsyncLocal on every property getter call to record which properties were accessed. When a RenderFragment is deferred, property reads inside it are not recorded, forcing a fallback to conservative subject-level tracking (see "Current workaround" below).

### What is a deferred RenderFragment?

### What is a deferred RenderFragment?

A `RenderFragment` (or `RenderFragment<T>`) is a delegate that builds part of a Blazor render tree. When a parent component passes a RenderFragment as a parameter to a child component (e.g., `ChildContent`, `RowTemplate`, `ItemContent`), the child stores it and invokes it during its own rendering.

If the child renders in the **same synchronous batch** as the parent, the `AsyncLocal` is still active and property reads are captured. But if the child defers rendering to a **later batch**, the context is lost.

### When does deferred rendering happen?

Common real-world scenarios:

1. **Virtualized lists** (e.g., MudBlazor `MudDataGrid`, Blazor's built-in `Virtualize<T>`) store item templates and only invoke them when items scroll into view. Each scroll event triggers a new render batch.

2. **Lazy panels** (e.g., accordion content, tab panels) store their content RenderFragment and only invoke it when the panel is expanded/tab is selected. The expand/select is a user event that triggers a new render batch.

3. **Conditional rendering in third-party components** where a component stores a RenderFragment and invokes it based on internal state changes (dialog content, popover bodies, drawer content).

4. **`SectionContent`/`SectionOutlet`** where content defined in one component tree is rendered in a completely different location.

### What goes wrong?

```
Timeline:

Batch 1 (TrackingScope renders):
  ShouldRender()       → StartRecording() → AsyncLocal scope ACTIVE
  BuildRenderTree()    → ChildContent runs
    @motor.Speed       → RECORDED (AsyncLocal active)
    <MudDataGrid>      → RowTemplate stored by DataGrid (not invoked yet)
  OnAfterRender()      → StopRecording() → AsyncLocal scope DISPOSED

Batch 2 (User scrolls, DataGrid renders new rows):
  DataGrid.BuildRenderTree()
    RowTemplate(item)  → invokes stored fragment
      @item.Name       → NOT RECORDED (AsyncLocal scope is gone)
```

### Current workaround and its cost

TrackingScope falls back to **subject-level tracking**: it tracks which subjects (objects) had properties read, not which specific properties. When any property on a tracked subject changes, it triggers a re-render, even if that property was never displayed.

```razor
<TrackingScope Context="@context">
    <p>@motor.Speed RPM</p>           @* Speed is recorded *@
</TrackingScope>
```

With subject-level fallback: if `motor.Temperature` changes (not displayed), TrackingScope still re-renders because `motor` was accessed. In an industrial dashboard with hundreds of properties per device, this means re-rendering on every property update instead of only when `Speed` changes.

The fallback also requires parent-child graph traversal (BFS) on every property change event to check if the changed subject is a descendant of a tracked subject. This adds CPU overhead proportional to graph depth.

## Root Cause Analysis

### How AsyncLocal works in Blazor rendering

`AsyncLocal<T>` values flow through synchronous calls and are inherited by child async contexts. Within a single render batch, Blazor processes the render queue synchronously:

```csharp
// Renderer.cs - ProcessRenderQueue()
while (_batchBuilder.ComponentRenderQueue.Count > 0)
{
    var nextToRender = _batchBuilder.ComponentRenderQueue.Dequeue();
    RenderInExistingBatch(nextToRender);  // synchronous
}
```

Each component's `BuildRenderTree` runs synchronously within this loop. So if TrackingScope sets an AsyncLocal value in `ShouldRender()`, all components rendering in the same batch can see it.

### Where context is lost

The context is lost between batches. A new render batch is triggered by:
- User interaction (click, scroll, keyboard) via JS interop
- Timer/interval callbacks
- External state changes (SignalR, service updates)

Each of these enters the Blazor dispatcher as a new work item with a fresh `ExecutionContext`. The AsyncLocal values from the previous batch are not present.

### Why RenderFragment delegates don't preserve context

In .NET, delegates are plain function pointers with captured variables (closures). They do NOT capture `ExecutionContext`. This is by design for performance: most delegates don't need execution context preservation.

Compare with `Task.Run()`, which explicitly captures `ExecutionContext` via `ExecutionContext.Capture()` and restores it via `ExecutionContext.Run()` when the task executes. RenderFragment delegates have no such mechanism.

### Where in Blazor's code the preservation could happen

The key locations in the Blazor source (dotnet/aspnetcore, `src/Components/Components/src/`):

1. **RenderTreeBuilder.AddAttribute()** (multiple overloads) where RenderFragment values are stored as component attributes:
   ```csharp
   // RenderTreeBuilder.cs - AddAttribute(int, string, MulticastDelegate?)
   public void AddAttribute(int sequence, string name, MulticastDelegate? value)
   {
       AssertCanAddAttribute();
       if (value != null || _lastNonAttributeFrameType == RenderTreeFrameType.Component)
       {
           _entries.AppendAttribute(sequence, name, value);  // stored as-is
       }
       // ...
   }
   ```

2. **RenderTreeBuilder.AddContent()** where RenderFragments are invoked during rendering:
   ```csharp
   // RenderTreeBuilder.cs - AddContent(int, RenderFragment?)
   public void AddContent(int sequence, RenderFragment? fragment)
   {
       if (fragment != null)
       {
           OpenRegion(sequence);
           fragment(this);  // invoked with current (child's) context
           CloseRegion();
       }
   }
   ```

3. **ComponentState.RenderIntoBatch()** where a component's render fragment is invoked:
   ```csharp
   // ComponentState.cs - RenderIntoBatch()
   renderFragment(_nextRenderTree);  // invoked with current batch's context
   ```

Blazor already uses `ExecutionContext.Capture()/Run()` for hot reload (Renderer.cs line 248-252), proving the pattern is known and accepted within the codebase.

## Solutions Considered

### Solution A: Wrap RenderFragments at storage time in RenderTreeBuilder

When a `RenderFragment` or `RenderFragment<T>` is stored as a component attribute (parameter), wrap it to capture the current `ExecutionContext` and restore it on invocation.

**Intervention point:** `RenderTreeBuilder.AddAttribute()` overloads, specifically when `_lastNonAttributeFrameType == RenderTreeFrameType.Component` (meaning the attribute is being set on a component, not a DOM element).

**Sketch:**
```csharp
public void AddAttribute(int sequence, string name, MulticastDelegate? value)
{
    AssertCanAddAttribute();
    if (value != null || _lastNonAttributeFrameType == RenderTreeFrameType.Component)
    {
        if (_lastNonAttributeFrameType == RenderTreeFrameType.Component)
        {
            value = WrapRenderFragmentWithContext(value);
        }
        _entries.AppendAttribute(sequence, name, value);
    }
    else
    {
        TrackAttributeName(name);
    }
}

private static MulticastDelegate? WrapRenderFragmentWithContext(MulticastDelegate? value)
{
    if (value is RenderFragment fragment)
    {
        var ctx = ExecutionContext.Capture();
        if (ctx != null)
        {
            return new RenderFragment(builder =>
                ExecutionContext.Run(ctx, _ => fragment(builder), null));
        }
    }
    // RenderFragment<T> handling requires type checking
    // since it's generic: delegate RenderFragment RenderFragment<T>(T value)
    return value;
}
```

**Handling RenderFragment\<T\>:**
`RenderFragment<T>` is `delegate RenderFragment RenderFragment<T>(T value)`. It returns a `RenderFragment`, so there are two invocation points: the outer call `template(item)` and the inner `resultFragment(builder)`. The context should be captured once (at storage time) and restored at the inner invocation (when the actual rendering happens and property reads occur).

Since `RenderFragment<T>` is generic, detecting it requires a runtime type check:
```csharp
var type = value.GetType();
if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(RenderFragment<>))
{
    // Wrap using reflection or a cached generic method
}
```

This is a performance concern. Options:
- Use `TypeNameParser` or cached type checks to avoid repeated reflection
- Only wrap known types (`RenderFragment`, `RenderFragment<T>` for common T values)
- Add a marker interface or attribute that opts into wrapping

**Pros:**
- Transparent to component authors (no code changes needed)
- Works regardless of how the child component invokes the fragment
- Fixes the problem for ALL AsyncLocal-based scenarios, not just property tracking

**Cons:**
- Performance: one `ExecutionContext.Capture()` + closure allocation per RenderFragment parameter per render cycle
- `RenderFragment<T>` handling is complex due to generics
- Behavioral change: fragments now see the definer's AsyncLocal values instead of the invoker's. Could theoretically break code that depends on seeing the invoker's context (unlikely but possible)

**Performance estimate:**
- `ExecutionContext.Capture()`: ~15ns on modern .NET
- Closure allocation: ~32-48 bytes, one per RenderFragment parameter
- In a page with 100 components each with ChildContent: ~100 extra allocations per render, ~1.5us extra CPU
- Negligible for most applications, but measurable in extreme scenarios (thousands of virtualized items)

### Solution B: Wrap at invocation time in RenderTreeBuilder.AddContent()

Instead of wrapping when stored, wrap when invoked.

**Problem:** At invocation time, we don't have the definer's context. The whole point is that the definer's context is lost. We would need to store the context alongside the fragment, which brings us back to Solution A.

**Verdict:** Not viable on its own. Only works if combined with Solution A's storage-time capture.

### Solution C: Preserve ExecutionContext in the render queue

Capture `ExecutionContext` when a component is added to the render queue (via `StateHasChanged()`), and restore it when the component renders.

```csharp
// Renderer.cs - AddToRenderQueue()
var ctx = ExecutionContext.Capture();
_batchBuilder.ComponentRenderQueue.Enqueue(
    new RenderQueueEntry(componentState, renderFragment, ctx));
```

**Problem:** This preserves the context of the component that called `StateHasChanged()`, which is the CHILD component (e.g., DataGrid). We need the PARENT's context (TrackingScope's). The child's context doesn't have TrackingScope's AsyncLocal values.

**Verdict:** Does not solve the problem. The context that needs preservation is the one active when the RenderFragment was created (in the parent), not when the child decides to render.

### Solution D: New Blazor API for ambient render context

Add a new API to `RenderTreeBuilder` that allows pushing/popping ambient values that flow through component boundaries:

```csharp
builder.PushAmbientValue<TrackingContext>(myContext);
// ... child components rendered here inherit the ambient value
builder.PopAmbientValue<TrackingContext>();
```

This would be similar to `CascadingValue` but at the render tree level, without requiring a component.

**Pros:**
- Clean, explicit API
- No performance cost for components that don't use it
- Framework-supported pattern

**Cons:**
- Large API surface addition
- Requires Blazor team buy-in for a new concept
- Doesn't solve the problem for existing third-party components that don't use the API
- Components that directly invoke `fragment(builder)` (without going through `AddContent`) would still miss the ambient value

**Verdict:** Elegant but high-effort and doesn't solve deferred rendering (the ambient value would only be active during the parent's render, same as AsyncLocal).

### Solution E: Use CascadingValue instead of AsyncLocal (our side)

Replace AsyncLocal-based recording with Blazor's CascadingValue to propagate the recording scope to descendant components.

**Problem:** The `ReadPropertyRecorder` interceptor operates at the Namotion.Interceptor level, not the Blazor level. It intercepts property getters on C# objects. It has no access to Blazor's component tree or CascadingValue system. There's no way for the interceptor to read a CascadingValue when a property getter is called, because property getters are plain C# method calls with no Blazor context.

**Verdict:** Not viable. The interception happens at a layer below Blazor.

### Solution F: Improve subject-level fallback (our side, current approach)

Make the subject-level tracking smarter to reduce unnecessary rerenders:
- Track which properties exist on each subject and only rerender for properties that COULD be displayed
- Use property metadata to distinguish "display" properties from "internal" properties
- Cache the ancestor check results more aggressively

**Problem:** This is inherently conservative. Without knowing which specific properties were read inside deferred fragments, we can only guess. Any improvement is heuristic, not correct.

**Verdict:** Incremental improvement, not a solution. Already implemented as the current workaround.

## Recommended Approach

**Solution A (wrap at storage time)** is the most viable Blazor-level fix. It is:
- Minimal in scope (changes only `RenderTreeBuilder.AddAttribute()`)
- Transparent to component authors
- Consistent with existing .NET patterns (`Task.Run`, `async/await` all preserve ExecutionContext)
- Already precedented in the Blazor codebase (hot reload uses the same Capture/Run pattern)

### Addressing the RenderFragment\<T\> challenge

The main implementation challenge is `RenderFragment<T>`. Approaches:

**Option 1: Handle only non-generic RenderFragment initially.** This covers `ChildContent` (the most common case). `RenderFragment<T>` support can follow in a separate PR.

**Option 2: Use a cached open-generic wrapper.** Create a static generic method and invoke it via `MakeGenericMethod` with caching:
```csharp
private static readonly ConcurrentDictionary<Type, Func<MulticastDelegate, ExecutionContext, MulticastDelegate>> _wrappers = new();

private static MulticastDelegate WrapGenericRenderFragment<T>(MulticastDelegate value, ExecutionContext ctx)
{
    var fragment = (RenderFragment<T>)value;
    return new RenderFragment<T>(item =>
    {
        var inner = fragment(item);
        return builder => ExecutionContext.Run(ctx, _ => inner(builder), null);
    });
}
```
The `MakeGenericMethod` call is cached per T, so the reflection cost is paid once per type argument.

**Option 3: Wrap at the `object?` overload level.** The `AddAttribute(int, string, object?)` overload is the catch-all. When `_lastNonAttributeFrameType == RenderTreeFrameType.Component`, check if the value's type is `RenderFragment<>` and wrap accordingly.

### Handling all delegate types that could carry property reads

Beyond `RenderFragment` and `RenderFragment<T>`, other delegate parameters could theoretically access tracked properties:
- `Func<string>` that reads a property when invoked
- `Action` callbacks that have side effects involving property reads
- `EventCallback` / `EventCallback<T>`

However, these are distinct from RenderFragments:
- `Func<T>` / `Action` are general-purpose delegates. Wrapping all of them would be too broad.
- `EventCallback` is about handling user events, not rendering. Property reads during event handling happen in a different lifecycle phase (event dispatch, not render).

**Recommendation:** Focus the PR on `RenderFragment` and `RenderFragment<T>` only. These are the rendering delegates that build UI and access properties for display. Other delegate types that read properties during rendering are extremely rare and can be handled as follow-up work if needed.

### Edge cases

1. **Fragment wrapping is idempotent:** If a RenderFragment passes through multiple components (component A passes it to B, B passes it to C), each pass through `AddAttribute` would wrap it again. Each wrapping captures the current context, so the innermost (original definer's) context is preserved through `ExecutionContext.Run` nesting. This is correct but adds overhead. Could be optimized by marking wrapped delegates (e.g., checking for a specific wrapper type).

2. **Fragments invoked multiple times:** A stored RenderFragment may be invoked many times (e.g., each time a virtualized item scrolls into view). The captured `ExecutionContext` must be valid for the lifetime of the fragment. `ExecutionContext.Capture()` returns a copy, so this is safe.

3. **Fragments invoked after the definer is disposed:** If TrackingScope is disposed but a child component still holds a reference to the RenderFragment, invoking it with the captured context would restore the AsyncLocal scope. The `ReadPropertyRecorderScope` handles this via its `_disposed` flag, ignoring property adds after disposal.

4. **Server-side Blazor with multiple circuits:** Each circuit has its own dispatcher and execution flow. `ExecutionContext.Capture()` captures per-circuit AsyncLocal values, which is correct.

5. **WebAssembly (single-threaded):** `ExecutionContext` operations are lighter on WASM since there's no thread synchronization. The wrapping overhead would be even lower.

6. **Nested TrackingScopes:** Multiple TrackingScopes can be nested. The `ReadPropertyRecorder` maintains a list of active scopes in the AsyncLocal, and property reads are recorded to all of them. Context preservation would correctly maintain all ancestor scopes' recording.

## Existing Blazor Issues

Related issues on dotnet/aspnetcore (both closed, neither addresses the root cause):

- [#51140](https://github.com/dotnet/aspnetcore/issues/51140): "ComponentBase.BuildRenderTree cannot be overridden" (closed, resolved: it IS overridable via Razor compiler). The discussion touched on ambient context needs but was resolved as a question about API surface, not the underlying ExecutionContext problem.
- [#62527](https://github.com/dotnet/aspnetcore/issues/62527): "Blazor logging scopes" (closed, resolved via `CircuitHandler.CreateInboundActivityHandler()`). This workaround addresses circuit-level logging scope but does NOT solve the RenderFragment deferred invocation problem.

Neither issue resulted in a framework-level fix for ExecutionContext preservation across RenderFragment boundaries. A new issue would need to be filed specifically describing the RenderFragment delegate context loss problem.

## Next Steps

1. Open an issue on dotnet/aspnetcore describing the problem and proposed Solution A
2. Reference existing issues #51140 and #62527
3. Implement a proof-of-concept PR against the aspnetcore repo
4. Benchmark the performance impact (with/without wrapping, varying component counts)

## Appendix: Blazor Rendering Pipeline

For reference, the relevant Blazor source files (dotnet/aspnetcore `main` branch, .NET 11 preview):

| File | Key Lines | Role |
|------|-----------|------|
| `Rendering/RenderTreeBuilder.cs` | 108-119, 248-259 | Fragment invocation and attribute storage |
| `Rendering/ComponentState.cs` | 103-143 | Component rendering into batch |
| `RenderTree/Renderer.cs` | 815-890 | Render queue processing |
| `RenderTree/Renderer.cs` | 248-252 | Existing ExecutionContext usage (hot reload) |
| `RenderTree/RenderTreeDiffBuilder.cs` | 662-718, 998-1008 | Parameter assignment to child components |

### Blazor render batch lifecycle

```
User interaction / StateHasChanged()
  |
  v
Renderer.AddToRenderQueue(componentId, renderFragment)
  |
  v
Renderer.ProcessRenderQueue()  [synchronous loop]
  |
  +---> ComponentState.RenderIntoBatch()
  |       |
  |       v
  |     renderFragment(_nextRenderTree)  [BuildRenderTree runs here]
  |       |
  |       +---> RenderTreeBuilder.AddAttribute(name, RenderFragment)
  |       |       [child component's ChildContent/template stored here]
  |       |
  |       +---> RenderTreeBuilder.AddContent(seq, fragment)
  |               [inline fragments invoked here]
  |
  +---> RenderTreeDiffBuilder.ComputeDiff()
  |       |
  |       v
  |     ComponentState.SetDirectParameters()
  |       [child component receives parameters including RenderFragments]
  |
  +---> [next component in queue...]
  |
  v
UpdateDisplayAsync(batch)  [send diff to browser]
```

### How AsyncLocal is typically consumed during rendering

Any code that checks `AsyncLocal<T>.Value` during a property getter, method call, or render expression is affected. The general pattern:

```
Component.BuildRenderTree() or RenderFragment invocation
  |
  v
Some code reads AsyncLocal<T>.Value
  |
  +---> [within same batch as setter?]
  |       yes -> value is present, works correctly
  |       no  -> value is null/default (LOST)
```

For Namotion.Interceptor specifically, the `ReadPropertyRecorder` interceptor checks `AsyncLocal<List<ReadPropertyRecorderScope>>.Value` on every property getter call. When a RenderFragment is deferred, the scope list is null and property reads go unrecorded.
