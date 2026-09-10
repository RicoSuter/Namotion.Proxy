# POCO Design Guidelines

## Introduction

This guide helps you design POCOs (Plain Old CLR Objects) that work correctly with Namotion.Interceptor. The library uses C# 13 partial properties and source generation to add property interception at compile-time.

**The golden rule**: Mark all stored properties as `partial` and initialize them in constructors. Most C# patterns work naturally - this guide focuses on **what to watch out for** and **what doesn't work**.

## Quick Start

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }
    public partial string LastName { get; set; }
    public partial int Age { get; set; }
    
    [Derived]
    public string FullName => $"{FirstName} {LastName}";
    
    public Person()
    {
        // Must initialize in constructor (no field initializers on partial properties)
        FirstName = string.Empty;
        LastName = string.Empty;
        Age = 0;
    }
}
```

## ⚠️ Critical: Collections Must Be Replaced, Not Mutated

**This is the most common mistake.** Property interceptors only fire when you **assign** to a property. Mutating a collection in-place doesn't call the setter.

```csharp
[InterceptorSubject]
public partial class Team
{
    public partial Person[] Members { get; set; }
    public partial Dictionary<string, Person> Roles { get; set; }
    
    public Team()
    {
        Members = [];
        Roles = new Dictionary<string, Person>();
    }
}

var team = new Team(context);

// ❌ WRONG - In-place mutations not tracked
team.Members[0] = newPerson;        // Doesn't call setter
team.Roles["leader"] = person;      // Doesn't call setter
team.Roles.Add("member", person);   // Doesn't call setter

// ✅ CORRECT - Replace entire collection
team.Members = [person1, person2];
team.Members = [..team.Members, person3];  // Spread into new array
team.Roles = new Dictionary<string, Person>(team.Roles) { ["leader"] = person };
```

**Why?** Property interceptors hook into the setter. Collection mutations bypass the setter entirely.

All of the following work when replaced entirely (assigning a new value to the property):

**Supported collection types:**
- `T[]`
- `List<T>`
- `ICollection<T>` / `IReadOnlyCollection<T>` / `IReadOnlyList<T>`
- `IEnumerable<T>`
- `ImmutableArray<T>`
- `ArrayList`

**Supported dictionary types:**
- `Dictionary<K, V>`
- `IDictionary<K, V>` / `IReadOnlyDictionary<K, V>`
- `Hashtable`

### Lifecycle Tracking for Nested Subjects

When properties contain other `[InterceptorSubject]` instances, attach/detach is automatic:

```csharp
dept.Employees = [person1, person2];  // person1, person2 attached to graph
dept.Employees = [person3];           // person1, person2 detached; person3 attached
```

With `WithContextInheritance()`, attached subjects inherit the parent's context.

## ⚠️ Initialize All Properties in Constructors

Partial properties **cannot** have field initializers. Initialize in the constructor.

```csharp
[InterceptorSubject]
public partial class Entity
{
    public partial Guid Id { get; set; }
    public partial string Name { get; set; }
    
    // ❌ Won't compile
    // public partial Guid Id { get; set; } = Guid.NewGuid();
    
    // ✅ Initialize in constructor
    public Entity()
    {
        Id = Guid.NewGuid();
        Name = string.Empty;
    }
}
```

**Why initialize everything?** Uninitialized properties get default values (`null`, `Guid.Empty`, etc.) which can cause issues with registry and change tracking.

The generator creates a context constructor that chains to your parameterless constructor:
```csharp
// Generated:
public Entity(IInterceptorSubjectContext context) : this() { /* setup */ }
```

## What Doesn't Work

### Intercepted Explicit Interface Implementation

C# doesn't allow `partial` on explicit interface implementations, so an explicitly implemented property can never be intercepted.

```csharp
public interface INamed { string Name { get; set; } }

[InterceptorSubject]
public partial class Entity : INamed
{
    // ❌ Won't compile (CS0754)
    // partial string INamed.Name { get; set; }
    
    // ✅ Use implicit implementation to get interception
    public partial string Name { get; set; }
}
```

A **non-partial** explicit implementation is supported and does appear in the subject's property metadata, keyed by the member's simple name, but it is not intercepted. Use it for values that are fixed or computed rather than tracked:

```csharp
public interface IHuman { Gender Gender { get; } }
public interface IMale : IHuman { Gender IHuman.Gender => Gender.Male; }

[InterceptorSubject]
public partial class John : IMale
{
    // "Gender" is in the metadata and reads as Gender.Male. IHuman.Gender has no setter here;
    // even a writable explicit implementation would not be intercepted, since C# does not allow
    // "partial" on an explicit interface implementation.
}
```

Attributes belong on the interface member, not the implementation. See [Interface Default Properties](generator.md#interface-default-properties) in the generator documentation.

### Abstract Properties

Abstract properties can't be partial.

```csharp
public abstract class Base
{
    // ❌ Won't compile
    // public abstract partial string Name { get; set; }
    
    // ✅ Use virtual instead
    public virtual partial string Name { get; set; }
}
```

## Patterns That Work

### Virtual and Override

```csharp
[InterceptorSubject]
public partial class Animal
{
    public virtual partial string Name { get; set; }
}

[InterceptorSubject]
public partial class Dog : Animal
{
    // The override repeats the base modifiers
    public override partial string Name { get; set; }
}
```

C# does not allow an override to change accessor visibility, so a restricted setter has to be declared on the base and repeated on the override:

```csharp
[InterceptorSubject]
public partial class Bird
{
    public virtual partial string Name { get; protected set; }
}

[InterceptorSubject]
public partial class Parrot : Bird
{
    public override partial string Name { get; protected set; }
}
```

`new` and `sealed` are also supported on a partial property, and are repeated on the generated half automatically. `new` is the fix for the CS0108 warning that accompanies NI0005 (see [Diagnostics](generator.md#diagnostics) in the generator documentation).

### Interface Default Properties

Interface default implementations are automatically included in property tracking. Mark computed properties with `[Derived]` for change notification:

```csharp
public interface ITemperatureSensor
{
    double Celsius { get; set; }

    [Derived]
    double Fahrenheit => Celsius * 9.0 / 5.0 + 32;
}

[InterceptorSubject]
public partial class Sensor : ITemperatureSensor
{
    public partial double Celsius { get; set; }
    // Fahrenheit is automatically tracked from the interface
}
```

### Required and Init

```csharp
[InterceptorSubject]
public partial class Config
{
    public required partial string ConnectionString { get; set; }
    public partial string Environment { get; init; }
    
    public Config() { Environment = "Development"; }
}
```

### Nullable Reference Types

```csharp
#nullable enable

[InterceptorSubject]
public partial class Employee
{
    public partial string FirstName { get; set; }      // Non-nullable
    public partial string? MiddleName { get; set; }    // Nullable
    
    public Employee()
    {
        FirstName = string.Empty;
        MiddleName = null;
    }
}
```

### Derived Properties

Mark computed properties with `[Derived]` for change tracking:

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }
    public partial string LastName { get; set; }
    
    [Derived]
    public string FullName => $"{FirstName} {LastName}";
    // When FirstName or LastName changes, FullName change is also fired
}
```

### Data Annotations

```csharp
[InterceptorSubject]
public partial class User
{
    [Required, MaxLength(50)]
    public partial string Username { get; set; }
    
    [EmailAddress]
    public partial string Email { get; set; }
    
    public User()
    {
        Username = string.Empty;
        Email = string.Empty;
    }
}

// Enable: context.WithDataAnnotationValidation();
```

### Property Attributes (Registry Feature)

```csharp
[InterceptorSubject]
public partial class Sensor
{
    public partial double Temperature { get; set; }
    
    [PropertyAttribute(nameof(Temperature), "Unit")]
    public partial string Temperature_Unit { get; set; }
    
    public Sensor()
    {
        Temperature = 20.0;
        Temperature_Unit = "°C";
    }
}
```

## Base Classes and Subclasses

A subject can derive from another subject, and properties declared anywhere in the hierarchy are intercepted. The set of members that interception needs (the context, the property table, the sync root and the helper methods the generated accessors call) is emitted once, in the class at the root of the hierarchy, and every subject below it inherits it.

```csharp
[InterceptorSubject]
public partial class PersonBase
{
    public partial string Name { get; set; }
}

[InterceptorSubject]
public partial class Employee : PersonBase
{
    public partial string Department { get; set; }
}
```

Writing `Name` on an `Employee` goes through the interceptor chain exactly like writing `Department`. A plain class with no attribute may sit between two subjects, and a subject may be `sealed` at any level. Nothing below is needed for this case.

Writing either side of that relationship by hand is possible but demanding. It needs a contract this page does not cover; see [Hand-written base classes and subclasses](generator.md#hand-written-base-classes-and-subclasses) in the generator reference.

## Property Change Hooks

The source generator creates optional partial method hooks for each partial property, allowing you to execute custom logic before or after property changes.

### Generated Methods

For each partial property `PropertyName`, the generator creates:
- `partial void OnPropertyNameChanging(ref TProperty newValue, ref bool cancel)` - Called before setter runs
- `partial void OnPropertyNameChanged(TProperty newValue)` - Called after successful write

### Execution Order

```
Setter: OnChanging → (if not cancelled) Interceptors → Field Update → OnChanged → PropertyChanged event
Getter: Field Read → Interceptors → Return
```

See [Interceptor Pipeline](interceptor.md#interceptor-pipeline) for how interceptors work.

### Cancellation Example

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }

    partial void OnFirstNameChanging(ref string newValue, ref bool cancel)
    {
        if (string.IsNullOrWhiteSpace(newValue))
        {
            cancel = true;  // Reject empty names
            return;
        }
        newValue = newValue.Trim();  // Or coerce the value
    }
}
```

### Post-Change Side Effects

```csharp
[InterceptorSubject]
public partial class Sensor
{
    public partial double Temperature { get; set; }

    partial void OnTemperatureChanged(double newValue)
    {
        if (newValue > 100)
        {
            Logger.LogWarning("High temperature: {Temp}", newValue);
        }
    }
}
```

### When Hooks Are Called

- `OnChanging` is always called when the setter is invoked
- `OnChanged` is only called if:
  - The change was not cancelled (`cancel` remained `false`)
  - The interceptor chain performed the write (interceptors can skip writes)
- If `OnChanged` throws, the property value is already written but `PropertyChanged` won't fire

**Use property hooks when:**
- Logic is specific to a single property
- You need access to instance members
- You want to validate, transform, or cancel changes
- You need to react to successful property changes

**Use Interceptors when:**
- Logic applies to many properties/classes
- You need cross-cutting concerns (logging, validation)
- Logic should be configurable at runtime

## INotifyPropertyChanged Support

All generated classes automatically implement `INotifyPropertyChanged` for data binding compatibility with WPF, MAUI, Blazor, and other UI frameworks.

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }
}

// Usage - no extra code needed
var person = new Person(context);
person.PropertyChanged += (s, e) => Console.WriteLine($"{e.PropertyName} changed");
person.FirstName = "Rico";  // Fires PropertyChanged event
```

### Performance

The `PropertyChanged?.Invoke(...)` pattern ensures zero overhead when no handlers are subscribed - only a null check occurs. The `PropertyChangedEventArgs` is not allocated unless the event has subscribers.

### When PropertyChanged Fires

The event fires only when a property actually changes:
- Not fired if `OnChanging` cancels the change
- Not fired if an interceptor skips the write

## Summary

1. **Mark all stored properties `partial`** - Tracking everything is safer
2. **Initialize in constructors** - No field initializers on partial properties
3. **Replace collections, don't mutate** - `arr = newArray`, not `arr[0] = x`
4. **Use `[Derived]`** for computed properties
5. **Explicit interfaces don't work** - Use implicit implementation
6. **Abstract doesn't work** - Use `virtual` instead

Most other C# patterns (nullable, required, init, virtual, override, data annotations) work naturally.

## Constructor Dependency Injection

Subjects can receive DI-injected services via constructor parameters alongside `IInterceptorSubjectContext`. When you define a constructor that accepts additional parameters, the source generator detects the user-defined constructor and does not generate an additional one.

### Pattern

```csharp
[InterceptorSubject]
public partial class ShellyDevice
{
    public ShellyDevice(
        IHttpClientFactory httpClientFactory,
        ILogger<ShellyDevice> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }
}
```

### How It Works

1. **ActivatorUtilities resolution**: When the subject is instantiated via DI (e.g., through `AddSubject`), `ActivatorUtilities.CreateInstance` resolves all constructor parameters from the service provider. Services like `IHttpClientFactory`, `ILogger<T>`, and any other registered services are injected automatically.

2. **Interaction with AddSubject**: `AddSubject<T>` applies the context unconditionally after construction, so the subject is attached regardless of its constructor shape. A constructor taking an `IInterceptorSubjectContext` is still used when one exists, but it confers no advantage: a subject with only DI parameters is attached just the same. The `contextResolver` parameter allows overriding which context is provided, and returning null from it registers the subject without a context.

   `configure` always runs before the attach `AddSubject` itself performs, and a startup scope spans construction and `configure`, so on every constructor shape the subject is fully configured before anything can start it. What differs is interception: a generated context constructor attaches during construction, so `configure` runs against an attached subject and its assignments are intercepted and tracked, while every shape that does not attach during construction, including one that declares an `IInterceptorSubjectContext` parameter and never attaches with it, is still unattached when `configure` runs and those assignments are not intercepted. See [Hosting](hosting.md#addsubjectt) for the full picture.

### Examples in the Codebase

- **ShellyDevice** (`Namotion.Devices.Shelly`): Injects `IHttpClientFactory` and `ILogger<ShellyDevice>` for HTTP communication with the device.
- **HueBridge** (`Namotion.Devices.Philips.Hue`): Injects `ILogger<HueBridge>` only. It creates its own `HttpClient` rather than taking an `IHttpClientFactory`, because the bridge needs a handler that accepts its self-signed certificate.
- **OpcUaSubjectServer** (`Namotion.Interceptor.OpcUa`): Injects OPC UA server configuration and telemetry services.

## Implementing Hosted Subjects for DI

> See [Hosting](hosting.md) for foundational concepts on hosted subjects and the hosting lifecycle.

When creating a subject library that extends `BackgroundService`, provide a DI extension method using `AddSubject<T>` from `Namotion.Interceptor.Hosting`.

`AddSubject<T>` registers the subject as a singleton, constructs it at host start and attaches it to the context. One instance per type: calling `AddShellyDevice()` twice throws, because the second call's `configure` and `contextResolver` could not take effect. To run several instances of one type, construct them yourself and attach them to the object graph rather than registering them, which is what an application managing devices from configuration does. When that context has hosting enabled, the handler on it starts the subject because the subject entered the graph, and host startup waits for that start. When the resolved context has no hosting handler, `AddSubject<T>` starts an `IHostedService` subject itself and stops that same instance at host shutdown, so the subject runs either way. Do not register the same subject with `AddHostedService<T>` as well, because that is a second owner and a second start.

### DI Extension Method

```csharp
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace MyLibrary;

public static class MySubjectServiceCollectionExtensions
{
    /// <summary>
    /// Registers MySubject and attaches it to the interceptor context.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure the subject.</param>
    /// <param name="contextResolver">
    /// Optional context resolver. When null, the context is resolved from DI.
    /// </param>
    public static IServiceCollection AddMySubject(
        this IServiceCollection services,
        Action<MySubject>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddSubject(configure, contextResolver);
}
```

### Required Project References

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="9.*" />
  <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="9.*" />
  <ProjectReference Include="..\Namotion.Interceptor.Hosting\Namotion.Interceptor.Hosting.csproj" />
</ItemGroup>
```

### Usage

```csharp
// Minimal
services.AddMySubject();

// With configuration
services.AddMySubject(subject =>
{
    subject.Name = "Sensor 1";
    subject.PollingInterval = TimeSpan.FromSeconds(5);
});
```

### Context Support (Optional)

No constructor parameter is needed for the context. `AddSubject` applies the resolved context after construction, so a subject whose constructor takes only DI services is attached just the same:

```csharp
public MySubject(IMyDriver driver, ILogger<MySubject> logger)
{
    // No context parameter. AddSubject attaches the subject after construction,
    // and runs its configure callback before that attach.
}
```

Declare an `IInterceptorSubjectContext` parameter only when the constructor genuinely needs the context, for example to build child subjects.

What changes if it does is what gets intercepted, not what a start can see. The generated context constructor attaches the subject itself, so by the time `AddSubject` runs `configure` the subject is already in the graph and its assignments are intercepted and tracked. Without such a constructor `AddSubject` attaches after `configure`, so those assignments are not intercepted. No start observes a half written subject either way, because `AddSubject` holds a startup scope across construction and `configure`. A constructor that declares the parameter but never calls `AddFallbackContext` with it behaves like one that never declared it.

### Restart Contract

A subject implementing `IHostedService` is restarted in place, on the same instance, whenever it leaves the graph and re-enters it, so `ExecuteAsync` must tolerate being run more than once. See [Subject as Hosted Service](hosting.md#subject-as-hosted-service) for what that requires of the implementation.
