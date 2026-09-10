# Namotion.Interceptor.GraphQL

Real-time GraphQL integration using [HotChocolate](https://chillicream.com/docs/hotchocolate). Exposes interceptor subjects as GraphQL queries with automatic subscription support for property changes.

## Getting Started

### Installation

Add the `Namotion.Interceptor.GraphQL` package to your ASP.NET Core project:

```xml
<PackageReference Include="Namotion.Interceptor.GraphQL" Version="0.1.0" />
```

### Basic Usage

Configure GraphQL with your subject:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register your subject with change tracking
builder.Services.AddSingleton(sp =>
{
    var context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking()
        .WithRegistry();  // Required for selection-aware subscriptions
    return new Sensor(context);
});

// Add GraphQL with subject support
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL<Sensor>()
    .AddInMemorySubscriptions();

var app = builder.Build();

app.MapGraphQL();

app.Run();
```

This creates:
- A `root` query that returns the subject
- A `root` subscription that streams updates on property changes

## Configuration

### GraphQLSubjectConfiguration

```csharp
public class GraphQLSubjectConfiguration
{
    // Root field name for queries and subscriptions. Default: "root"
    public string RootName { get; init; } = "root";

    // Path provider for property-to-field mapping. Default: CamelCasePathProvider
    public IPathProvider PathProvider { get; init; } = CamelCasePathProvider.Instance;

    // Buffer time for batching rapid changes. Default: 50ms
    public TimeSpan BufferTime { get; init; } = TimeSpan.FromMilliseconds(50);
}
```

### Custom Root Name

```csharp
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL<Sensor>("sensor")  // Use "sensor" instead of "root"
    .AddInMemorySubscriptions();
```

Query:
```graphql
query { sensor { temperature } }
```

### Full Configuration

```csharp
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL(
        sp => sp.GetRequiredService<Sensor>(),
        sp => new GraphQLSubjectConfiguration
        {
            RootName = "mySensor",
            BufferTime = TimeSpan.FromMilliseconds(100),
            PathProvider = new AttributeBasedPathProvider("graphql", '.')
        })
    .AddInMemorySubscriptions();
```

### Subject Selector

Use a custom selector when the subject isn't registered in DI:

```csharp
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL(
        sp => CreateSensorFromConfiguration(sp),  // Custom factory
        "sensor")
    .AddInMemorySubscriptions();
```

## Features

### Query

Fetch the current subject state:

```graphql
query {
  root {
    temperature
    humidity
    location {
      building
      room
    }
  }
}
```

Response:
```json
{
  "data": {
    "root": {
      "temperature": 25.5,
      "humidity": 60,
      "location": {
        "building": "A",
        "room": "101"
      }
    }
  }
}
```

### Selection-Aware Subscriptions

Subscriptions only fire when properties within the client's selection set change.

```graphql
subscription {
  root {
    temperature  # Only notified when temperature changes
  }
}
```

If `humidity` changes but `temperature` doesn't, the subscription will **not** receive an update.

#### Nested Selections

Nested property changes are correctly matched:

```graphql
subscription {
  root {
    location {
      building  # Notified when location.building changes
    }
  }
}
```

### Buffering

Rapid changes are batched according to `BufferTime`:

```csharp
new GraphQLSubjectConfiguration
{
    BufferTime = TimeSpan.FromMilliseconds(100)  // Batch changes within 100ms
}
```

When multiple properties change rapidly within the buffer window, clients receive a single update with all changes rather than individual updates for each change.

## API Reference

### AddSubjectGraphQL Overloads

```csharp
// Use registered service with default configuration
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL<TSubject>();

// Use registered service with custom root name
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL<TSubject>("customRootName");

// Use custom selector with default configuration
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL<TSubject>(
        sp => sp.GetRequiredService<TSubject>());

// Use custom selector with custom root name
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL<TSubject>(
        sp => sp.GetRequiredService<TSubject>(),
        "customRootName");

// Use custom selector with full configuration
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL<TSubject>(
        sp => sp.GetRequiredService<TSubject>(),
        sp => new GraphQLSubjectConfiguration { ... });
```

## Requirements

### Change Tracking

The subject context must have property change tracking enabled:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()  // Required for subscriptions
    .WithRegistry();             // Required for selection-aware filtering
```

### In-Memory Subscriptions

For subscriptions to work in development:

```csharp
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL<Sensor>()
    .AddInMemorySubscriptions();  // Required for single-server deployment
```

For production with multiple server instances, use Redis or another distributed subscription provider.

## How It Works

1. When a subscription is established, the selection set is parsed to extract field paths
2. The system subscribes to `GetPropertyChangeObservable()` on the subject's context
3. Property changes are filtered against the client's selection paths using `CamelCasePathProvider`
4. Changes are buffered for the configured `BufferTime` to batch rapid updates
5. When a matching change occurs (or buffer flushes), the subject is sent to the client

## Example: Real-Time Dashboard

```csharp
[InterceptorSubject]
public partial class Dashboard
{
    public partial decimal CpuUsage { get; set; }
    public partial decimal MemoryUsage { get; set; }
    public partial int ActiveConnections { get; set; }

    [Derived]
    public string Status => CpuUsage > 90 ? "Critical" : "Normal";
}
```

```csharp
// Program.cs
builder.Services.AddSingleton(sp =>
{
    var context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking()
        .WithRegistry();
    return new Dashboard(context);
});

builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL<Dashboard>("dashboard")
    .AddInMemorySubscriptions();
```

Frontend subscription:
```graphql
subscription {
  dashboard {
    cpuUsage
    status
  }
}
```

This subscription will only receive updates when `cpuUsage` or `status` changes. Changes to `memoryUsage` or `activeConnections` will not trigger notifications.

## Limitations

- Single subject per type (multiple subjects of same type not yet supported)
- GraphQL fragments (`... on Type` and `...FragmentName`) are not supported in selection-aware filtering. When fragments are used, the subscription falls back to receiving all property changes and a warning is logged.
