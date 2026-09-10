# Dynamic

The `Namotion.Interceptor.Dynamic` package enables creating interceptor subjects from interfaces at runtime without requiring compile-time code generation. This is useful for:

- Creating trackable objects from external interfaces
- Plugin architectures with runtime-defined types
- Extending existing subjects with additional interfaces at runtime

Unlike the main interceptor approach that uses source generation with `[InterceptorSubject]` classes, the Dynamic package uses Castle DynamicProxy to create runtime implementations.

## Creating Dynamic Subjects from Interfaces

Define interfaces with the properties you need:

```csharp
public interface IMotor
{
    int Speed { get; set; }
}

public interface ISensor
{
    int Temperature { get; set; }
}
```

Create a dynamic subject that implements multiple interfaces:

```csharp
// Create a subject implementing both interfaces
var subject = DynamicSubjectFactory.CreateDynamicSubject(typeof(IMotor), typeof(ISensor));

// Cast to specific interfaces and use normally
var motor = (IMotor)subject;
var sensor = (ISensor)subject;

motor.Speed = 100;
sensor.Temperature = 25;

Console.WriteLine($"Speed: {motor.Speed}");           // Output: Speed: 100
Console.WriteLine($"Temperature: {sensor.Temperature}"); // Output: Temperature: 25
```

## Adding Context for Tracking and Interceptors

Dynamic subjects work standalone, but to enable tracking, registry, or interceptors, attach a context:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithRegistry()
    .WithFullPropertyTracking();

var subject = DynamicSubjectFactory.CreateDynamicSubject(typeof(IMotor), typeof(ISensor));
subject.AttachToContext(context);

// Now the subject participates in tracking and registry
var motor = (IMotor)subject;
motor.Speed = 100;  // Tracked, intercepted, etc.
```

## Extending Classes with Additional Interfaces

Use `CreateSubject<TSubject>()` to create a subject based on an existing class while adding runtime interfaces:

```csharp
[InterceptorSubject]
public partial class Motor : IMotor
{
    public Motor()
    {
        Speed = 100;  // Default value
    }

    public partial int Speed { get; set; }
}

// Create Motor with additional ISensor interface
var motor = DynamicSubjectFactory.CreateSubject<Motor>(typeof(ISensor));
var sensor = (ISensor)motor;

// Class properties work normally
Console.WriteLine(motor.Speed);  // Output: 100 (from class)

// Interface properties use dynamic storage
sensor.Temperature = 25;
Console.WriteLine(sensor.Temperature);  // Output: 25 (from dynamic store)
```

This is useful when you have a compile-time subject but need to add interfaces discovered at runtime.

## Registry Integration

Dynamic subjects automatically register their properties with the registry system:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithRegistry();

var subject = DynamicSubjectFactory.CreateDynamicSubject(typeof(IMotor), typeof(ISensor));
subject.AttachToContext(context);

// Access registry information
var registeredSubject = subject.TryGetRegisteredSubject()!;
var properties = registeredSubject.Properties;

// Properties from all interfaces are available
Assert.Contains(properties, p => p.Name == "Speed");
Assert.Contains(properties, p => p.Name == "Temperature");
```

## Full Interceptor Support

Dynamic subjects participate fully in the interceptor pipeline:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithService(() => new LoggingInterceptor());

var subject = DynamicSubjectFactory.CreateDynamicSubject(typeof(IMotor));
subject.AttachToContext(context);

var motor = (IMotor)subject;
motor.Speed = 100;  // Triggers write interceptors
var speed = motor.Speed;  // Triggers read interceptors
```

## Property Initialization

Dynamic subjects handle property initialization automatically:

- **Value types** get their default values (0, false, etc.)
- **Reference types** get null as initial values
- **Properties are lazy-initialized** when first accessed

```csharp
public interface IConfiguration
{
    string Name { get; set; }
    int Port { get; set; }
    bool IsEnabled { get; set; }
}

var config = (IConfiguration)DynamicSubjectFactory.CreateDynamicSubject(typeof(IConfiguration));

Console.WriteLine(config.Name);      // Output: (null)
Console.WriteLine(config.Port);      // Output: 0
Console.WriteLine(config.IsEnabled); // Output: False
```

## Performance Considerations

Dynamic subjects use Castle DynamicProxy for runtime implementation:

- **Property access** has additional overhead compared to compile-time subjects
- **Property metadata** is cached and reused across instances
- **Interface discovery** happens once during subject creation
- **Memory usage** is higher due to proxy generation and property value storage

For performance-critical scenarios, prefer compile-time subjects with `[InterceptorSubject]` when possible.
