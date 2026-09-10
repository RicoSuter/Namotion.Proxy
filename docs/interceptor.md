# Interceptors and Contexts

The `InterceptorSubjectContext` is the central coordination hub in the core `Namotion.Interceptor` package. It manages service registration, resolution, and orchestrates the interception pipeline. Every interceptor subject requires a context to function.

## Creating a Context

```csharp
var context = InterceptorSubjectContext.Create();

var person = new Person(context);
```

The context is typically created once at application startup and shared across all subjects in an object graph.

## Adding Services

Services are registered using the fluent API. Services can be interceptors, handlers, or any custom service type:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithService<IMyService>(() => new MyService())
    .WithService(() => new MyWriteInterceptor());
```

**Common service interfaces:**

- `IReadInterceptor` - Intercepts property reads
- `IWriteInterceptor` - Intercepts property writes
- `IMethodInterceptor` - Intercepts method invocations
- `ILifecycleHandler` - Handles subject attach/detach events

Extension methods like `WithFullPropertyTracking()` or `WithRegistry()` register multiple related services at once.

## Service Resolution

Services are resolved by interface type. Multiple services of the same type are returned in registration order (unless ordering attributes are used):

```csharp
// Get all services of a type
var interceptors = context.GetServices<IWriteInterceptor>();

// Get a single service (throws if multiple exist)
var registry = context.TryGetService<SubjectRegistry>();
```

Services are cached after first resolution. The cache is invalidated when services or fallback contexts change.

## Fallback Contexts

Contexts can be linked in a hierarchy where child contexts inherit services from parent contexts:

```csharp
var parentContext = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking();

var childContext = InterceptorSubjectContext.Create();
childContext.AddFallbackContext(parentContext);

// childContext now has access to all services from parentContext
```

This is used internally by `WithContextInheritance()` to automatically assign the parent's context to child subjects.

**Resolution order:**
1. Services registered directly on the context
2. Services from fallback contexts (recursively)
3. Results are deduplicated and ordered

**Lifecycle callbacks on subject contexts:**

When the context belongs to a subject, adding and removing a fallback also notifies the `ILifecycleInterceptor` services of the fallback context:

- The set notified on removal is the set that was resolved when the fallback was added. An interceptor registered on the parent afterwards is not notified, and one unregistered in between still is. If an attach callback throws, the removal notifies only the interceptors up to and including the one that threw.
- `RemoveFallbackContext` returning `true` means the removal is committed. If an add is still running its attach callbacks, the removal completes on that thread, so the fallback may still be visible for a moment after the call returns.
- A detach callback that throws does not block the removal. The remaining invoked interceptors are still notified, the fallback is removed, and the first failure is rethrown afterwards.
- Adding a fallback that closes a delegation cycle succeeds. The cycle is reported by the next service resolution, and the fallback can be removed to break it. The removal itself can still throw, because a recorded interceptor that resolves through the now cyclic chain raises, but the fallback is gone either way.

## Service Ordering

When multiple handlers or interceptors are registered, their execution order can be controlled using ordering attributes. This is important when services have dependencies on each other.

**Available Attributes:**

```csharp
using Namotion.Interceptor.Attributes;

// Run before specific types
[RunsBefore(typeof(OtherHandler))]
public class MyHandler : ILifecycleHandler { }

// Run after specific types
[RunsAfter(typeof(OtherHandler))]
public class MyHandler : ILifecycleHandler { }

// Run before all services without [RunsFirst]
[RunsFirst]
public class EarlyHandler : IWriteInterceptor { }

// Run after all services without [RunsLast]
[RunsLast]
public class LateHandler : IWriteInterceptor { }
```

**Ordering Rules:**

- Services are partitioned into three groups: `[RunsFirst]` → Middle → `[RunsLast]`
- Within each group, `[RunsBefore]` and `[RunsAfter]` define the topological order
- A reference to a type with multiple registered instances binds against every instance, for example when a context aggregates fallback contexts that each register the same service type
- Instances of the same type keep their registration order relative to each other
- Without ordering attributes, registration order is preserved
- Missing dependency types are silently ignored (supports optional dependencies)
- Circular dependencies throw `InvalidOperationException`
- A service cannot have both `[RunsFirst]` and `[RunsLast]`
- A `[RunsFirst]` service cannot have `[RunsAfter]` referencing Middle or Last group services
- A `[RunsLast]` service cannot have `[RunsBefore]` referencing First or Middle group services

## Interceptor Pipeline

Property reads and writes flow through a configurable chain of interceptors. Each interceptor receives a `next` delegate and can run code **before** and **after** calling it. The "after" code runs in reverse order, creating a nested pipeline.

### Write Pipeline (`IWriteInterceptor`)

```
person.Name = "John"
    │
    ▼
┌─ Interceptor 1 ─────────────────────────────┐
│  (before next)  validate, transform, etc.   │
│      │                                      │
│      ▼                                      │
│  ┌─ Interceptor 2 ───────────────────────┐  │
│  │  (before next)  equality check        │  │
│  │      │                                │  │
│  │      ▼                                │  │
│  │  ┌─ Interceptor 3 ─────────────────┐  │  │
│  │  │  (before next)                  │  │  │
│  │  │      │                          │  │  │
│  │  │      ▼                          │  │  │
│  │  │    _name = "John"  ← field set  │  │  │
│  │  │      │                          │  │  │
│  │  │      ▼                          │  │  │
│  │  │  (after next)                   │  │  │
│  │  └────────────────────────────────-┘  │  │
│  │      │                                │  │
│  │      ▼                                │  │
│  │  (after next)  fire change event      │  │
│  └───────────────────────────────────────┘  │
│      │                                      │
│      ▼                                      │
│  (after next)  notify observers             │
└─────────────────────────────────────────────┘
```

### Read Pipeline (`IReadInterceptor`)

```
var name = person.Name
    │
    ▼
┌─ Interceptor 1 ─────────────────────────────┐
│  (before next)  record access, etc.         │
│      │                                      │
│      ▼                                      │
│  ┌─ Interceptor 2 ───────────────────────┐  │
│  │  (before next)                        │  │
│  │      │                                │  │
│  │      ▼                                │  │
│  │    return _name  ← field read         │  │
│  │      │                                │  │
│  │      ▼                                │  │
│  │  (after next)  transform value        │  │
│  └───────────────────────────────────────┘  │
│      │                                      │
│      ▼                                      │
│  (after next)                               │
└─────────────────────────────────────────────┘
    │
    ▼
  "John"
```

### Implementing an Interceptor

Each interceptor can:
- Modify the value before passing to the next interceptor
- Skip calling the next interceptor (blocking the operation)
- Perform side effects (logging, validation, change tracking)

```csharp
public class LoggingInterceptor : IWriteInterceptor
{
    public void WriteProperty<T>(ref PropertyWriteContext<T> context, WriteInterceptionDelegate<T> next)
    {
        Console.WriteLine($"Before: Writing {context.Property.Name} = {context.NewValue}");
        next(ref context); // Call next interceptor or actual write
        Console.WriteLine($"After: Wrote {context.Property.Name}");
    }
}
```

The pipeline is built once per property type and cached for performance.
