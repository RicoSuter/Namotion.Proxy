# Source Generator

The `Namotion.Interceptor.Generator` package is a C# 13 source generator that transforms classes marked with `[InterceptorSubject]` into fully trackable interceptor subjects. All interception logic is generated at compile-time, resulting in zero runtime reflection overhead.

## Getting Started

Add both packages to your project:

```xml
<ItemGroup>
    <PackageReference Include="Namotion.Interceptor" Version="*" />
    <PackageReference Include="Namotion.Interceptor.Generator" Version="*"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
```

Mark your class with `[InterceptorSubject]` and declare properties as `partial`:

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }
}
```

See the [Subject Design Guidelines](subject-guidelines.md) for detailed patterns, best practices, and examples.

## What Gets Generated

For each `[InterceptorSubject]` class, the generator creates a partial class implementation with:

### Interface Implementations

- `IInterceptorSubject` - Core interception infrastructure
- `INotifyPropertyChanged` - Property change notifications (if not inherited from base class)
- `IRaisePropertyChanged` - Internal interface for raising change events

### Constructors

If no constructors exist, the generator creates:

```csharp
public Person() { }

public Person(IInterceptorSubjectContext context) : this()
{
    ((IInterceptorSubject)this).Context.AddFallbackContext(context);
}
```

If a parameterless constructor already exists, only the context constructor is generated.

### Property Implementations

For each partial property, the generator creates:

- A backing field (`_PropertyName`)
- Getter that routes through the interception pipeline
- Setter that routes through the interception pipeline, with hooks and change notification
- Partial method hooks for customization

### Static Metadata

A `DefaultProperties` dictionary containing metadata for all properties:

```csharp
public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
```

> **Note:** `DefaultProperties` is used internally by other Namotion.Interceptor libraries and may change in future versions. For runtime property discovery, use `IInterceptorSubject.Properties` on the instance instead.

## Supported Features

### Partial Properties

Only properties marked with the `partial` keyword are intercepted:

```csharp
[InterceptorSubject]
public partial class Sensor
{
    public partial double Temperature { get; set; }  // Intercepted
    public string TransientData { get; set; }        // Not intercepted
}
```

### Property Hooks

The generator creates partial method hooks for each property with a setter:

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string Name { get; set; }

    // Called before the value is written - can cancel or modify the value
    partial void OnNameChanging(ref string newValue, ref bool cancel)
    {
        if (string.IsNullOrWhiteSpace(newValue))
        {
            cancel = true;  // Reject the change
            return;
        }
        newValue = newValue.Trim();  // Coerce the value
    }

    // Called after the value is written successfully
    partial void OnNameChanged(string newValue)
    {
        Console.WriteLine($"Name changed to: {newValue}");
    }
}
```

### Derived Properties

Properties marked with `[Derived]` are included in the metadata as calculated properties (can be read-only or writable):

```csharp
[InterceptorSubject]
public partial class Rectangle
{
    public partial double Width { get; set; }
    public partial double Height { get; set; }

    [Derived]
    public double Area => Width * Height;
}
```

When `Width` or `Height` changes, `Area` automatically raises a change notification (requires `WithDerivedPropertyChangeDetection()` on the context).

### Interface Default Properties

The generator automatically discovers and includes interface default implementations in the property metadata:

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
    // Fahrenheit is automatically included in DefaultProperties
}
```

**Supported scenarios:**
- Read-only default properties (`string Status => ...`)
- Writable default properties (`string Label { get => ...; set { } }`)
- `[Derived]` attribute on interface properties
- Multiple interface inheritance
- Interface hierarchies (base interfaces)
- Generic interfaces
- Diamond inheritance (deduplicated)

Explicit interface implementations are also supported and are keyed by the member's simple name:

```csharp
public enum Gender { Male, Female }

public interface IHuman
{
    Gender Gender { get; }
}

public interface IMale : IHuman
{
    Gender IHuman.Gender => Gender.Male;
}

[InterceptorSubject]
public partial class John : IMale
{
    // "Gender" is included in DefaultProperties and reads as Gender.Male
}
```

The property is reached by casting to the interface that declares the member (`IHuman` here), so it always resolves through the normal dispatch rules for interface default implementations. A property reached this way is not intercepted, because an explicitly implemented member cannot be routed through the interception pipeline. C# forbids `partial` on an explicit interface implementation (CS0754, joined by CS9248 for the implementation part that can then never exist), so no explicit implementation can be intercepted whatever its accessors look like. Implement the interface implicitly when the property has to be tracked.

For a property that comes from an interface, whether you implement it explicitly or adopt its default implementation, it becomes a subject property only if the generated code can call at least one accessor. An `init` accessor does not count: C# allows calling it from an object initializer only, never from the generated getter and setter.

What happens when nothing is callable depends on who owns the declaration:

| The member is declared | Result |
|---|---|
| In your subject class, as an explicit implementation | Skipped and reported as NI0040, since you wrote it and can change it |
| In the interface, as a default implementation | Skipped silently, since the interface may not be yours to change |

Written in your own class, so you get told:

```csharp
public interface IProbe { string Probe { protected get; init; } }

[InterceptorSubject]
public partial class Sensor : IProbe
{
    // NI0040: the protected getter is unreachable and init cannot be called from
    // the generated accessors, so nothing callable is left.
    string IProbe.Probe { get => "x"; init { } }

    public partial string Name { get; set; }
}
```

Adopted from the interface, so it is dropped in silence and the key is simply absent:

```csharp
public interface IProbe { string Probe { protected get => "x"; init { } } }

[InterceptorSubject]
public partial class Sensor : IProbe
{
    public partial string Name { get; set; }
}

// ((IInterceptorSubject)sensor).Properties holds "Name" only; "Probe" is absent
// rather than present and inert.
```

This applies to interface members. A plain `init`-only property declared in the subject class itself is a known gap: it still produces an entry, and reading or writing that entry through the metadata does nothing.

Attributes such as `[Derived]` must be declared on the interface member rather than on the explicit implementation, because the property metadata reflects the interface member. Any attribute on the implementation reports NI0020 (see [Diagnostics](#diagnostics)), including an implementation-local one such as `[SuppressMessage]`, which keeps its usual meaning but is simply not part of the metadata.

### Property Precedence Across a Hierarchy

Precedence, highest first: the subject's own declarations, then everything it inherits from its base subject, then the interface default implementations it adopts. An adopted default is a fallback, so a real property declared anywhere in the chain beats it, including one declared above the subject that adopted the interface.

All three tiers are in play in this hierarchy, and they resolve differently at each level:

```csharp
public interface IHasLocation
{
    string Location => "unknown";
}

[InterceptorSubject]
public partial class Device : IHasLocation
{
    // Adopts IHasLocation and declares no Location of its own.
}

[InterceptorSubject]
public partial class Pump : Device, IHasLocation
{
    // Re-listing IHasLocation is what makes this declaration the interface implementation.
    [Required]
    public partial string Location { get; set; }
}

[InterceptorSubject]
public partial class BoosterPump : Pump
{
    // Declares nothing of its own.
}
```

`Device` has only the adopted default, so that is what it reports. `Pump` declares `Location` itself, which is its own top tier and wins there. `BoosterPump` is the level that makes the ordering visible: it adopts `IHasLocation` through `Pump`, so its own lowest tier offers the default again, and the entry it inherits from `Pump` still beats it.

| `Properties["Location"]` reports | on `Device` | on `Pump` | on `BoosterPump` |
|---|---|---|---|
| declared by | `IHasLocation.Location` | `Pump.Location` | `Pump.Location` |
| `IsIntercepted` | `false` | `true` | `true` |
| `SetValue` | `null` | writes through the interceptor chain | writes through the interceptor chain |
| `[Required]` among `Attributes` | absent | present | present |

Attributes travel with the entry the metadata resolves to, which is what the rest of the library reads: the `[Required]` above is enforced by data annotation validation on `Pump` and `BoosterPump`, while an entry contributed by an interface default carries only the attributes the interface member itself declares.

### Method Interception

Methods ending with `WithoutInterceptor` get public wrapper methods:

```csharp
[InterceptorSubject]
public partial class Calculator
{
    protected int SumWithoutInterceptor(int a, int b)
    {
        return a + b;
    }
}

// Generator creates:
// public int Sum(int a, int b) { ... }
```

The generated method routes through the interception pipeline, enabling cross-cutting concerns.

Parameters are forwarded by value, so `in` and `ref readonly` parameters are supported, while a plain `ref` or an `out` parameter is not and makes the method skipped with NI0040. A by-reference return type is skipped the same way: no wrapper is generated, so a caller that relied on one fails to compile with CS1061 rather than silently losing the ref semantics.

### Virtual and Override Properties

```csharp
[InterceptorSubject]
public partial class Animal
{
    public virtual partial string Name { get; set; }
}

[InterceptorSubject]
public partial class Dog : Animal
{
    public override partial string Name { get; set; }
}
```

An override keeps the overriding declaration’s `PropertyInfo` and attributes. If it omits a getter or setter, the metadata retains that inherited accessor when it is callable from the subject’s generated code. This includes protected accessors, but excludes inaccessible ancestor accessors and init-only setters. A partial override still generates only the accessors written in its declaration.

### New and Sealed Properties

`new` and `sealed` are supported on partial properties. Both modifiers are repeated on the generated half of the property automatically, so the hand-written declaration only needs to carry them once:

```csharp
public interface IHuman { string Origin { get; } }
public class BaseSubject : IHuman { public string Origin => "base"; }

[InterceptorSubject]
public partial class DerivedSubject : BaseSubject, IHuman
{
    // "new" hides BaseSubject.Origin and silences CS0108. Re-listing IHuman is what makes this
    // property the interface implementation; without it the generator reports NI0060.
    public new partial string Origin { get; set; }
}

[InterceptorSubject]
public partial class SealedDog : Animal
{
    public sealed override partial string Name { get; set; }
}
```

`new` over a plain base class, as above, is supported. Re-declaring an **ancestor subject's** property is not, with or without `new` and whether or not the new declaration is `partial`: two members would then share one property key, so the metadata can reach only one of them while both stay writable through a differently typed reference. The generator rejects it as NI0065, shown with its fix under [Fixing NI0060, NI0061 and NI0065](#fixing-ni0060-ni0061-and-ni0065). Use `virtual` and `override` across a chain of subjects instead. Adding `new` is not an alternative to either remedy, because it only silences the CS0108 warning about hiding and neither moves the interface slot nor satisfies NI0060 or NI0065.

### Access Modifiers

All C# access modifiers are supported:

```csharp
[InterceptorSubject]
public partial class Entity
{
    public partial string Public { get; set; }
    protected partial string Protected { get; set; }
    internal partial string Internal { get; set; }
    private partial string Private { get; set; }
    protected internal partial string ProtectedInternal { get; set; }
    private protected partial string PrivateProtected { get; set; }

    // Accessor-level modifiers
    public partial string Name { get; private set; }
}
```

### Init-Only and Required Properties

```csharp
[InterceptorSubject]
public partial class Config
{
    public required partial string ConnectionString { get; set; }
    public partial string Environment { get; init; }

    public Config()
    {
        Environment = "Development";
    }
}
```

An `init` property is assignable from the parameterless constructor, which the generated context constructor chains to, so it can be initialized the same way every other property is. A `required` property is not, because the language demands it at every construction site: `new Config(context) { ConnectionString = "..." }` compiles and `new Config(context)` is CS9035.

### Nested Classes

```csharp
public partial class Outer
{
    [InterceptorSubject]
    public partial class Nested
    {
        public partial string Name { get; set; }
    }

    public partial class Level2
    {
        [InterceptorSubject]
        public partial class DeepNested
        {
            public partial int Value { get; set; }
        }
    }
}
```

The containing type does not need to be a class. It can also be a record, a record struct, a struct, or an interface, as long as every containing type is `partial`:

```csharp
public partial record Outer
{
    [InterceptorSubject]
    public partial class Nested
    {
        public partial string Name { get; set; }
    }
}
```

### Inheritance

Child classes can also be `[InterceptorSubject]`:

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

The `DefaultProperties` of `Employee` includes properties from both classes, and properties declared on `PersonBase` are intercepted like any other: reads and writes go through the interceptor chain, so change tracking records them and connectors see them. The per instance interception members (the context, the property table, the sync root and the helper methods the generated accessors call) are emitted once, in the class at the root of the hierarchy, and every subject below it inherits them.

Note that `PropertyChanged` firing is not evidence that a property is intercepted. A subject with no context still raises it, because the setter calls `RaisePropertyChanged` directly rather than through the chain. If you are testing whether interception reaches a property, assert on an interceptor.

The hierarchy does not have to be made only of subjects. A plain class with no attribute can sit between two subjects, a subject can be `sealed` at any level, and a base class written by hand can host generated subclasses as long as it provides the members the generated code calls. See [Hand-written base classes](#hand-written-base-classes-and-subclasses) for that contract, and [Hierarchy Hazards](#hierarchy-hazards) for what a derived class must avoid declaring.

### Partial Class Spanning

Properties can be declared across multiple files:

```csharp
// Person.cs
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }
}

// Person.Extended.cs
public partial class Person
{
    public partial string LastName { get; set; }
}
```

Both properties are included in the generated code.

### Namespaces and Accessibility

Subjects can be declared inside a namespace, inside a file-scoped namespace, or directly in the global namespace. The subject class does not need to be public either; the generator honors whatever accessibility it declares:

```csharp
[InterceptorSubject]
internal partial class InternalSubject
{
    public partial string Name { get; set; }
}
```

## Limitations

| Limitation | Workaround |
|------------|------------|
| Only partial properties are intercepted | Mark properties with `partial` keyword |
| Records cannot be subjects | Use a class. See NI0003 in [Diagnostics](#diagnostics) |
| Structs and interfaces cannot be subjects | Use a class. The compiler itself rejects a plain struct or interface as CS0592, because `InterceptorSubjectAttribute` only targets classes. A record struct is reported by NI0003 as well |
| Generic subjects, or subjects nested in a generic containing type, are not supported | Use non-generic types. See NI0005 in [Diagnostics](#diagnostics) |
| File-local subjects are not supported | Remove the `file` modifier. See NI0006 in [Diagnostics](#diagnostics) |
| Attributes on an explicit interface implementation are not part of the property metadata | Declare an attribute the library reads on the interface member. See NI0020 in [Diagnostics](#diagnostics) |
| Abstract properties not supported | A partial member cannot carry `abstract` (CS0750), so declare the property `virtual` on the base subject and `override` it below |
| Init-only properties cannot be set after construction | Design constraint of C# |
| Partial properties cannot have field initializers | Initialize in constructor |
| A `WithoutInterceptor` method whose stripped name collides with an existing method fails with CS0111 | Rename one of the two. No `NI` diagnostic is reported for this, unless the stripped name is one the generated half occupies, which is reported as NI0040 |

## Diagnostics

The generator reports the following diagnostics, all in the `Namotion.Interceptor` category. IDs are grouped by the effect on generation: NI0001–NI0019 stop generation, NI0020–NI0039 are advisory, NI0040–NI0059 skip a member, and NI0060–NI0079 report semantic problems while generation continues. Unassigned IDs in each range are reserved for future rules:

| ID | Severity | Cause | Fix |
|----|----------|-------|-----|
| NI0001 | Error | The subject class is not declared `partial` | Add the `partial` modifier |
| NI0002 | Error | A containing type of the subject is not declared `partial` | Add `partial` to every containing type |
| NI0003 | Error | `[InterceptorSubject]` is placed on a record or a record struct. A plain struct or interface never reaches this diagnostic; the compiler already rejects those with CS0592, because the attribute only targets classes | Use a class |
| NI0004 | Error | The generator threw an unhandled exception while processing the subject | Report the issue. The full stack trace is embedded in the generated source, which only reaches disk if the project sets `EmitCompilerGeneratedFiles` |
| NI0005 | Error | The subject itself is generic, or the subject is nested inside a generic containing type | Remove the type parameters from the subject or its containing type |
| NI0006 | Error | The subject is declared `file`-local | Remove the `file` modifier |
| NI0007 | Error | The nearest base class that is a subject has no usable static `DefaultProperties` of type `IReadOnlyDictionary<string, SubjectPropertyMetadata>`, which leaves nothing for the subject's own property set to concatenate with. The message also lists whatever else is missing, such as `IInterceptorSubject` or the helper members, but a base missing only those still generates and gets NI0062 instead | Put `[InterceptorSubject]` on the base class, or make it satisfy the [subject base class contract](#hand-written-base-classes-and-subclasses). If the base class only exists to add properties at runtime, drop it and call `AddProperties` on the subject instead |
| NI0020 | Warning | Any attribute, not only `[Derived]`, is placed on an explicit interface implementation. The emitted metadata reflects the interface member, so the attribute is not part of the subject's property metadata | Move an attribute the library reads, such as `[Derived]` or a validation attribute, to the interface member. An implementation-local attribute such as `[SuppressMessage]` or `[ExcludeFromCodeCoverage]` keeps its usual meaning where it is and can be suppressed |
| NI0040 | Warning | A member the author plausibly offered as a subject property could not be supported. **A `*WithoutInterceptor` method** is skipped when it has no name before the suffix, is static or generic, takes a plain `ref` or an `out` parameter, has a by-reference return type, is itself an explicit interface implementation, or when its stripped name is one the generated half occupies (`GetPropertyValue`, `SetPropertyValue`, `InvokeMethod`, `GetInstanceProperties`, `PropertyChanged`, `RaisePropertyChanged`, `DefaultProperties`, `Context`, `Data`, `SyncRoot` or `AddProperties`), which is matched on the name alone at any arity. **An explicit interface implementation declared in the subject class** is skipped when the implemented member has no accessor reachable from generated code, or when the reachable accessors narrow to `init` alone, which leaves nothing the generated lambdas can call: an `init` accessor is reachable but can only be called from an object initializer, never from the emitted getter or setter. **Never reported:** a static member, an indexer (class-declared or an interface default), any other interface default member that is unreachable from generated code, and an explicit implementation declared in an interface are never candidates for a subject property and stay silent | Rename the `*WithoutInterceptor` method so the stripped name is free, remove it, adjust its signature, widen the implemented member's accessibility, give the implemented member a reachable `get` or `set` accessor, or drop the explicit implementation |
| NI0060 | Error | A derived subject re-declares a property whose interface implementation is already provided by a base class, so reading through the subject and reading through the interface return different values forever | Re-list the interface in the subject's base list so the property takes the slot; rename the property when its type differs from the interface member's |
| NI0061 | Error | More than one member provides the same simple property name. A class-declared property always takes the name; between colliding interface members, the first one the generator reaches takes it. Explicit overrides of the same interface slot are one member for this purpose, so they do not collide. One error is reported per interface member that ends up unreachable when two or more interface members compete for one name, naming both the member that took the name and the member that was dropped. A class-declared property beating a single explicit implementation, or an interface default of the same name, is resolved silently. The interface scan re-runs at every level of a hierarchy that inherits the colliding interfaces, so one modelling mistake is reported once per subject in the chain, not once overall | Rename one of the colliding members, or suppress the rule to accept the resolution |
| NI0062 | Warning | The base class is recognized as a subject but does not expose the shared interception members, either because it was built by an older version of the generator or because it is a hand-written class that provides only `DefaultProperties`. The message lists the members that are missing, which is what separates a stale base from one that lacks a single clause. The subject falls back to emitting its own interception members, so it compiles, and properties declared on that base class stay unintercepted | Rebuild the base assembly against the current package version, or make the base class satisfy the contract. Suppressing the rule accepts the fallback and the unintercepted base properties with it. Note that under `TreatWarningsAsErrors` this warning fails the build |
| NI0063 | Error | The subject, or a class between the subject and its base subject, declares a member named `GetPropertyValue`, `SetPropertyValue`, `InvokeMethod` or `GetInstanceProperties`. The generated bodies call those by simple name, so the declared member can capture the call. The rule matches on the name alone, for any member kind and any signature, because a `new` annotated member of the same shape captures the call with no compiler warning at all | Rename the member. On a class between the two subjects, a `private` member of that name is not reported, because it neither hides nor binds |
| NI0064 | Error | A class anywhere in the subject's base chain declares a public member that implements `IInterceptorSubject.Context`, `Data`, `SyncRoot` or `AddProperties`, or implements one of those explicitly. Every subject re-lists `IInterceptorSubject`, which recomputes the interface map, so that member takes the slot from the base class implementation. Below the subject's base subject the report is unconditional; at that class and above it, the member is only reported when a class further up already implements the same member, which is what keeps a hand-written subject root deriving from `object` quiet. A same-named member that does not match the interface member's type and signature is not reported, and neither is an `override`, which occupies the slot it already had | Rename the member, or remove the explicit implementation and let the inherited one stand. See [Hierarchy Hazards](#hierarchy-hazards) |
| NI0065 | Error | A subject declares a property, or an explicit interface implementation, whose name an ancestor subject already exposes. Two members then share one key: the metadata can read only one, both stay writable through a differently typed reference, and both raise changes under that name. Not reported for an `override`, which shares one slot, nor when the hidden member is on a class that does not carry `[InterceptorSubject]`, including a hand-written base that satisfies the subject base contract and does contribute a property set. A declaration that also fails to take an interface slot reports NI0065 alone; NI0060 stands down on that property name, including when NI0065 is suppressed | Rename one of the two, or, where the declaration can carry `override`, make the ancestor property `virtual` and `override` it instead. Several shapes cannot, an explicit interface implementation among them, and renaming is then the only remedy |

Suppress a rule at the point of use with `#pragma warning disable NI0060`, or project-wide with `<NoWarn>NI0060;NI0061;NI0065</NoWarn>` in the project file. Both suppress a generator diagnostic at `Error` severity exactly as they do at `Warning`, so they are the only escape hatch from NI0060, NI0061 and NI0065, and `<NoWarn>` is what a consumer needs to stage an upgrade across a solution that reports many of them at once. What suppression actually buys differs by group:

- **Generation stops: NI0001 through NI0007.** Suppressing one of these silences the message, but the class still never becomes an interceptor subject, leaving an inert type with none of the generated members and no further compiler feedback pointing at why. Fix the underlying shape instead.
- **Generation succeeds and the member is emitted as declared: NI0020.** Suppression is a real fix here, because it only silences advice about a shape the author has chosen to accept and nothing about the generated code changes.
- **Generation succeeds and the member the rule names is skipped: NI0040.** It is a warning, but no wrapper and no metadata entry is emitted for that member, so suppressing it does not accept a shape, it hides the fact that a `WithoutInterceptor` opt-in the author wrote is being ignored and no wrapper exists at all. Rename or reshape the member instead.
- **Generation succeeds and the generated code carries a member that silently does not work as declared: NI0060 through NI0065.** All but NI0062 are errors, and none of them stops generation. Suppressing one leaves the member in place: unreachable where the declaration says it should exist (NI0060, NI0061, NI0065), capturing a generated call (NI0063), or hijacking an interface slot (NI0064). NI0062 is the warning of the group, and suppressing it accepts a hierarchy in which base-declared properties are not intercepted. Each of these then fails silently at runtime instead of loudly at build time, so they are worth fixing rather than silencing. One case of NI0061 is the exception, and there suppression is the intended end state rather than a deferral: when both colliding members are declared in interfaces the consumer does not own, no rename is available to them, so suppressing NI0061 to accept the name the generator resolved is the fix.

### Fixing NI0060, NI0061 and NI0065

These three are errors, so each one is met at a broken build. Their remedies are in the table above; below is the smallest shape that triggers each one, with the version that builds.

**NI0060** is reported when the subject's declaration does not take the interface slot, because the slot is bound at the class that lists the interface:

```csharp
public interface IHasOrigin { string Origin { get; } }

public class Hardware : IHasOrigin
{
    public string Origin => "hardware";
}

[InterceptorSubject]
public partial class Sensor : Hardware   // NI0060 on Origin
{
    public new partial string Origin { get; set; }
}
```

```csharp
[InterceptorSubject]
public partial class Sensor : Hardware, IHasOrigin   // builds: the slot moves to this class
{
    public new partial string Origin { get; set; }
}
```

**NI0061** is reported when two members reach the subject under one simple name. Here both interfaces carry a `Label`, and the one the generator reaches second is dropped:

```csharp
public interface IHasLabel { string Label => "unnamed"; }
public interface IHasLegend { string Label => "none"; }

[InterceptorSubject]
public partial class Gauge : IHasLabel, IHasLegend   // NI0061: IHasLegend.Label is unreachable
{
    public partial double Value { get; set; }
}
```

```csharp
public interface IHasLegend { string Legend => "none"; }   // builds: each member has its own key
```

**NI0065** is reported when a subject declares a name an ancestor subject already exposes, because one metadata key cannot reach both backing fields:

```csharp
[InterceptorSubject]
public partial class Device
{
    public partial string Name { get; set; }
}

[InterceptorSubject]
public partial class Pump : Device   // NI0065 on Name; the "new" below only silences CS0108
{
    public new partial string Name { get; set; }
}
```

```csharp
[InterceptorSubject]
public partial class Device
{
    public virtual partial string Name { get; set; }   // builds: one slot, one backing field
}

[InterceptorSubject]
public partial class Pump : Device
{
    public override partial string Name { get; set; }
}
```

## Hierarchy Hazards

Emitting the interception members once per hierarchy means a derived subject inherits members it does not declare itself. The first three consequences below follow directly from that; the two after them come from a different mechanism, the order in which constructors run and the way C# hides members by name, and are listed alongside because they are hierarchy hazards of the same shape. Most need a subject base class to reproduce at all, which is why none of this needs any action for an ordinary hierarchy of `[InterceptorSubject]` classes; where an item does not need a base class, its own section below says so.

### A member in a derived class can take an interface slot

C# only allows an explicit interface implementation in a class that lists the interface itself (CS0540), and each subject has to keep its own `IInterceptorSubject.Properties`, so every subject re-lists `IInterceptorSubject`. Re-listing recomputes the interface map for that class. A public member in a derived class, or in a plain class between two subjects, that matches `Context`, `Data`, `SyncRoot` or `AddProperties` therefore takes the slot away from the base class implementation. So does one in the base subject itself, or in any class above it, whenever a class further up already implements that member: the explicit implementation that would beat a public member only beats one in its own class, so it never protects a hijacker below it.

The explicit form counts too, and the check does not treat it as safer. Because CS0540 forces the class to list `IInterceptorSubject` itself, and listing it is exactly what makes that class the subject's base subject, an explicit `IInterceptorSubject.Context` on a hand-written class in the chain is the normal way this goes wrong rather than an exception to it.

Taking `Context` is the severe case: the inherited helpers keep reading the root's field, which nothing populates any more, so interception stops without an error and the property values still look correct. NI0064 turns the whole shape into a build error, so this is caught at compile time.

A derived subject declaring `public object SyncRoot { get; }` is therefore NI0064, even though the member it takes over is one it never declared.

### A base class can hijack a slot later, without the consuming assembly being rebuilt

NI0064 runs where the derived subject is compiled, so a member added to the base class afterwards is not seen. For that to matter, all four of the following have to hold:

1. the referenced assembly's subject hierarchy is more than one level deep;
2. a non-static, non-`override` member is added to a class in that hierarchy, either as a public member or as an explicit `IInterceptorSubject` implementation;
3. that member matches an `IInterceptorSubject` member by name and signature exactly;
4. the consuming assembly ships without being recompiled.

Recompiling the consuming assembly against the new base turns it into an NI0064 build error, so the window is exactly "shipped, not rebuilt". It stays open by design, for the reason under [Why not a virtual hook](#why-not-a-virtual-hook).

### Members added to IInterceptorSubject in future need the same review

Because derived subjects keep re-listing `IInterceptorSubject`, any member added to that interface can be hijacked the same way and has to be added to NI0064's list at the same time. This is a note for whoever evolves the interface, not something a consumer can act on.

### Writes before the context is published are not intercepted

The context is published inside the generated `Subject(IInterceptorSubjectContext context)` constructor, which chains to the parameterless constructor first and adds the context afterwards. Anything that runs before that point writes straight to the backing field:

- the subject's own parameterless constructor body, which the generated context constructor runs before it publishes anything;
- field initializers of a derived class, which the language runs before the base constructor;
- statements in a constructor body that run before the base constructor publishes the context.

A write in a hand-written subclass constructor body after `: base(context)` has run is on the other side of that line: the base constructor has published the context by then, so the write is intercepted, including a write to a property declared on the base class.

### Hiding `RaisePropertyChanged` swallows change notifications

Generated property setters call the inherited helper by simple name, and C# hides by name, so a derived subject declaring its own `RaisePropertyChanged` captures every call emitted in that class:

```csharp
[InterceptorSubject]
public partial class Swallows : Root
{
    public partial string LeafName { get; set; }

    protected new void RaisePropertyChanged(string propertyName)
    {
    }
}
```

`LeafName` then raises nothing, while properties declared on `Root` still raise, because their setters were emitted in `Root` and bound there. The values stay correct, so nothing looks wrong until a binding or a connector stops updating. A body that forwards to `base.RaisePropertyChanged(propertyName)` behaves correctly, which is why no diagnostic exists: the two differ by one line and separating them needs dataflow analysis. Do not hide this member.

### Why not a virtual hook

The first two hazards would both disappear if `IInterceptorSubject.Properties` were implemented once in the root behind a `protected virtual GetDefaultProperties()` hook, since derived subjects would then stop re-listing the interface and there would be no slot to take.

That alternative was measured rather than assumed. `IInterceptorSubject.Properties` is read on every intercepted write through `PropertyReference.Metadata`, which is deliberately uncached, so the hook adds a virtual call to a hot path. At a monomorphic call site the cost is flat, because the JIT devirtualizes it. At a polymorphic call site, which is the representative one since `PropertyReference.Metadata` is a single shared call site that every subject type passes through, the hook costs 0.133 ns per `Properties` read, or roughly 2 to 4 percent of an intercepted write.

The current design was kept with those numbers in hand. The cost would be paid by every subject forever, including the large majority that have no base class at all, while the hazard it removes is caught at compile time by NI0064 for every consumer that recompiles.

## Requirements

- **C# 13** with partial property support
- Class must be marked `partial`
- Properties to intercept must be marked `partial`
- IDE with source generator support (Visual Studio 2022, Rider, VS Code)

## Performance

The generator is optimized for performance:

- **Zero runtime reflection** - All metadata generated at compile-time
- **Static lambdas** - No closure allocations in property accessors
- **Fast-path optimization** - Direct field access when no context is set
- **FrozenDictionary** - Thread-safe, read-optimized property lookup
- **PropertyChangedEventArgs caching** - Avoids repeated allocations
- **AggressiveInlining** - Helper methods are inlined by the JIT

## Troubleshooting

### Generated code not appearing

1. Ensure the generator is added as an analyzer:
   ```xml
   <PackageReference Include="Namotion.Interceptor.Generator" Version="..."
                     OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
   ```

2. Rebuild the project completely

3. Check that your class is `partial` and has `[InterceptorSubject]`

### Compilation errors in generated code

1. Check the build output for an `NI####` diagnostic first. Where one is reported it names the cause directly; see [Diagnostics](#diagnostics). Not every generator problem has a diagnostic, so also check the [Limitations](#limitations) table for the compiler error you are seeing
2. Ensure you're using C# 13 or later
3. Check that property types are accessible from the generated code
4. Verify namespace imports are correct

### Changes not being tracked

1. Ensure properties are marked `partial`
2. Verify you're using a context with tracking enabled:
   ```csharp
   var context = InterceptorSubjectContext
       .Create()
       .WithFullPropertyTracking();
   ```

3. Create instances with the context:
   ```csharp
   var person = new Person(context);  // Not: new Person()
   ```

## Hand-written base classes and subclasses

Everything above this point is handled by the generator. This section covers the two hand-written directions: a base class you write yourself that hosts generated subclasses, and a subclass you write yourself under a generated base class. Both are advanced and demand a lot of ceremony, so reach for them only when a generated class on both sides is not an option. [Subject Design Guidelines](subject-guidelines.md) covers the ordinary cases.

### Writing a base class by hand

A class can host generated subclasses when it exposes all of the following. A generated subject satisfies this by construction, so this only matters for a base class you write yourself.

| Member | Needed by |
|--------|-----------|
| implements `IInterceptorSubject` | everything else |
| implements `IRaisePropertyChanged`, on the base class or on the subject | the subject not re-declaring `PropertyChanged` and `RaisePropertyChanged` |
| `protected TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)` | generated getters |
| `protected bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> setValue)` | generated setters |
| `protected object? InvokeMethod(string methodName, Func<IInterceptorSubject, object?[], object?> invokeMethod, params object?[] parameters)` | generated method wrappers |
| `protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties()` | the subject's own `IInterceptorSubject.Properties` |
| `public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties` | merging the subject's properties with the base class ones |

Details that are easy to get wrong:

- Members may be more accessible than listed, and a member the base class itself inherits from further up counts. A generic base class is checked with its type arguments substituted.
- `InvokeMethod`'s last parameter must really be `params`. The generated call site passes arguments in expanded form, and the check tests for `params` explicitly, so the same parameter types without it fail the contract. The subject then falls back to emitting its own interception members and NI0062 is reported, which `TreatWarningsAsErrors` turns into a build error.
- `DefaultProperties` may be a static property or a static field, but its type has to be `IReadOnlyDictionary<string, SubjectPropertyMetadata>` or something that implements it. A static of that name with any other type is reported rather than accepted.
- `GetInstanceProperties` may likewise return something that implements the dictionary interface, such as `FrozenDictionary<string, SubjectPropertyMetadata>?`, but it has to be a reference type. The generated code combines the two as `GetInstanceProperties() ?? DefaultProperties`, and `??` rejects a value type on its left, so a struct implementing the interface fails the contract even though the same struct is accepted for `DefaultProperties`, which is only concatenated.
- The `IRaisePropertyChanged` row is the only one that is not needed for the generated code to compile. A base class that satisfies everything else but not that one still produces code that compiles, with the subject declaring its own change notification members, but it fails the contract all the same: the subject re-emits the whole block and NI0062 is reported, which `TreatWarningsAsErrors` turns into a build error.

What happens when a base class does not satisfy the contract depends on `DefaultProperties`. If it is present and usable, the subject falls back to emitting its own interception members and the generator reports NI0062: the code compiles, and properties declared on that base class are not intercepted. If `DefaultProperties` is missing or unusable as well, the generator reports NI0007 and generates nothing for the subject.

Here is a base class that satisfies the whole contract:

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;

public class TrackedEntityBase : IInterceptorSubject, INotifyPropertyChanged, IRaisePropertyChanged
{
    private IInterceptorExecutor? _context;
    private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;

    public event PropertyChangedEventHandler? PropertyChanged;

    void IRaisePropertyChanged.RaisePropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    IInterceptorSubjectContext IInterceptorSubject.Context
        => InterceptorExecutor.GetOrCreate(ref _context, this);

    ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();

    object IInterceptorSubject.SyncRoot { get; } = new object();

    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties
        => GetInstanceProperties() ?? DefaultProperties;

    void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
        => _properties = ((IInterceptorSubject)this).Properties
            .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
            .ToFrozenDictionary();

    public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
        = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

    protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => _properties;

    protected TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
        => _context is not null ? _context.GetPropertyValue(propertyName, readValue)! : readValue(this)!;

    protected bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue,
        Action<IInterceptorSubject, TProperty> setValue)
    {
        if (_context is null)
        {
            setValue(this, newValue);
            return true;
        }

        return _context.SetPropertyValue(propertyName, newValue, currentValue, setValue);
    }

    protected object? InvokeMethod(string methodName, Func<IInterceptorSubject, object?[], object?> invokeMethod,
        params object?[] parameters)
        => _context is not null ? _context.InvokeMethod(methodName, parameters, invokeMethod) : invokeMethod(this, parameters);
}

[InterceptorSubject]
public partial class Machine : TrackedEntityBase
{
    public partial string SerialNumber { get; set; }
}
```

### Three things the compiler cannot check for you

The list above is checked by looking at member signatures, which cannot see what the members do. Three requirements are behavioural, and a base class that gets one of them wrong passes every check and then misbehaves at runtime.

1. **`AddProperties` must merge starting from `((IInterceptorSubject)this).Properties`**, not from its own `DefaultProperties` and not from its own backing field, and it must store the result in the field that `GetInstanceProperties()` returns. Merging from its own field drops the subclass's `DefaultProperties` on the first call, so the subject loses its own generated properties.
2. **The three helpers must route through the same executor that `IInterceptorSubject.Context` publishes for that instance.** A base class that keeps a second executor for the helpers still compiles, and then writes look fine while no interceptor ever sees them.
3. **`IInterceptorSubject.Context` must return an `IInterceptorExecutor` built for that instance.** `InterceptorExecutor` binds to its subject when it is constructed, and other parts of the library cast `Context` to `IInterceptorExecutor` without checking, so a borrowed or shared context misroutes every property reference.

### Writing a subclass by hand

A hand-written class can derive from a generated subject and implement intercepted properties itself by calling the same four protected members the generated code uses. They are generated implementation detail: they are documented so this scenario is usable, not as a stable API.

Such a class has no generated `DefaultProperties`, so it has to register its own property metadata by calling `((IInterceptorSubject)this).AddProperties(...)`. **That registration has to happen before the first intercepted write**, not merely somewhere in the class. The base class's generated `Subject(IInterceptorSubjectContext context)` constructor publishes the context before the subclass constructor body runs, so a write in that body already reaches the interceptor chain, and the chain throws `InvalidOperationException` when it looks up a property name that was never registered.

```csharp
using System;
using System.Collections.Generic;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class Device
{
    public partial string Name { get; set; }
}

public class CustomDevice : Device
{
    private string _location = string.Empty;

    public CustomDevice(IInterceptorSubjectContext context) : base(context)
    {
        // Register before the first write: the base constructor has already published the context.
        ((IInterceptorSubject)this).AddProperties(
            new SubjectPropertyMetadata(
                nameof(Location),
                typeof(string),
                [],
                subject => ((CustomDevice)subject).Location,
                (subject, value) => ((CustomDevice)subject).Location = (string)value!,
                isIntercepted: true,
                isDynamic: false));

        Location = "unknown";
    }

    public string Location
    {
        get => GetPropertyValue(nameof(Location), static subject => ((CustomDevice)subject)._location);
        set => SetPropertyValue(nameof(Location), value, _location,
            static (subject, newValue) => ((CustomDevice)subject)._location = newValue);
    }
}
```

One thing to avoid anywhere below a subject: do not declare a member named `GetPropertyValue`, `SetPropertyValue`, `InvokeMethod` or `GetInstanceProperties` for something else, and do not implement `IInterceptorSubject.Context`, `Data`, `SyncRoot` or `AddProperties` yourself. Either one takes over what the base class provides. Where a generated subject declares such a member, or sits below a class that does, the generator reports NI0063 or NI0064.

A hand-written class with no subject below it is not scanned by the generator at all, so no NI0063 reaches it. The compiler covers that case instead. The four helpers are `protected`, so a member that genuinely hides one of them is CS0108, and `TreatWarningsAsErrors` turns that into a build error: a method with the same signature as one of the four, or a field, property or event named `InvokeMethod` or `GetInstanceProperties`, the two helpers that are not generic. Add `new` where the hiding is intended, or rename the member. An overload that differs in signature stays silent, and there it is also harmless, because the generated calls sit in the subject's own file above such a class. See [Hierarchy Hazards](generator.md#hierarchy-hazards) for why it matters.
