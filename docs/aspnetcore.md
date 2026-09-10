# Namotion.Interceptor.AspNetCore

ASP.NET Core integration for exposing interceptor subjects as REST APIs with automatic JSON serialization and validation.

## Getting Started

### Installation

Add the `Namotion.Interceptor.AspNetCore` package to your ASP.NET Core project:

```xml
<PackageReference Include="Namotion.Interceptor.AspNetCore" />
```

### Basic Usage

Map your subject to REST endpoints using `MapSubjectWebApis`:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Create the context with required extensions
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithDataAnnotationValidation();

// Create and register your subject
var car = new Car(context);
builder.Services.AddSingleton(car);
builder.Services.AddSingleton(context);

var app = builder.Build();

// Map REST endpoints
app.MapSubjectWebApis<Car>("api/car");

app.Run();
```

**Required context extensions:**
- `WithFullPropertyTracking()` - Enables property change detection
- `WithRegistry()` - Enables object graph navigation and dynamic properties in JSON output

Both install `WithLifecycle()`, which owns parent tracking. `GetJsonPath` walks a property's parent chain to build its path, so it needs a lifecycle and gets one through either extension above.

**Optional extensions:**
- `WithDataAnnotationValidation()` - Enables validation via data annotations
- `WithHostedServices(builder.Services)` - Registers subjects implementing `BackgroundService`

This creates three endpoints:

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/car` | Returns the subject as JSON |
| `POST` | `/api/car` | Updates properties via JSON path |
| `GET` | `/api/car/structure` | Returns the subject structure as `SubjectUpdate` |

## Features

### GET - Read Subject State

Returns the entire subject graph as JSON, using camelCase property names:

```http
GET /api/car
```

Response:
```json
{
  "name": "My Car",
  "tires": [
    { "pressure": 2.5 },
    { "pressure": 2.4 },
    { "pressure": 2.6 },
    { "pressure": 2.5 }
  ],
  "averagePressure": 2.5
}
```

Nested subjects and collections are serialized recursively.

### POST - Update Properties

Update one or more properties using JSON paths:

```http
POST /api/car
Content-Type: application/json

{
  "name": "Updated Car",
  "tires[0].pressure": 2.8
}
```

The endpoint:
1. Resolves JSON paths to properties (supports dot notation and array indexing)
2. Validates that paths exist and properties are writable
3. Runs registered `IPropertyValidator` validators
4. Applies all updates atomically

#### Error Responses

**Unknown paths:**
```json
{
  "detail": "Unknown property paths.",
  "unknownPaths": ["invalidProperty"]
}
```

**Read-only properties:**
```json
{
  "detail": "Attempted to change read only property.",
  "readOnlyPaths": ["averagePressure"]
}
```

**Validation errors:**
```json
{
  "detail": "Property updates have invalid values.",
  "errors": {
    "tires[0].pressure": ["Value must be between 0 and 10"]
  }
}
```

### GET /structure - Subject Metadata

Returns a `SubjectUpdate` with complete type information and structure:

```http
GET /api/car/structure
```

Useful for clients that need to discover the subject schema dynamically.

## API Reference

### MapSubjectWebApis

```csharp
// Use registered service (subject must be registered with exact type TSubject)
app.MapSubjectWebApis<TSubject>(string path);

// Use custom selector (for more control over subject resolution)
app.MapSubjectWebApis<TSubject>(
    Func<IServiceProvider, IInterceptorSubject> subjectSelector,
    string path);
```

**Example with custom selector:**
```csharp
// Expose a nested subject
app.MapSubjectWebApis<Tire>(
    sp => sp.GetRequiredService<Car>().Tires[0],
    "api/tire/front-left");
```

### Extension Methods

#### ToJsonObject

Converts a subject to a `JsonObject`, including both static and dynamic properties:

```csharp
var json = subject.ToJsonObject(jsonSerializerOptions);
```

Requires `WithRegistry()` for dynamic properties to be included.

#### FindPropertyFromJsonPath

Resolves a JSON path to a property:

```csharp
var (subject, property) = rootSubject.FindPropertyFromJsonPath("tires[0].pressure");
// subject: the Tire instance at index 0
// property: the Pressure property metadata
```

Returns `(null, default)` if the path cannot be resolved.

## Validation Integration

The POST endpoint automatically validates incoming updates using registered `IPropertyValidator` implementations.

**Recommended approach** - use the context extension:
```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithDataAnnotationValidation();  // Registers DataAnnotationsValidator
```

**Alternative** - register validators via DI:
```csharp
builder.Services.AddSingleton<IPropertyValidator, DataAnnotationsValidator>();
```

The `DataAnnotationsValidator` from `Namotion.Interceptor.Validation` validates against data annotations like `[Range]`, `[Required]`, etc.

**Example subject with validation:**
```csharp
[InterceptorSubject]
public partial class Tire
{
    [Range(0, 10)]
    public partial decimal Pressure { get; set; }
}
```

## JSON Serialization

Property names are converted using the configured `JsonSerializerOptions.PropertyNamingPolicy`. By default, ASP.NET Core uses camelCase.

Configure custom serialization:

```csharp
builder.Services.Configure<JsonOptions>(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});
```

## OpenAPI Integration

Endpoints are automatically tagged with the subject type name for Swagger/OpenAPI grouping.

**Setup OpenAPI/Swagger:**
```csharp
var builder = WebApplication.CreateBuilder(args);

// Add OpenAPI services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApiDocument();

var app = builder.Build();

// Map subject endpoints (tagged as "Car")
app.MapSubjectWebApis<Car>("api/car");

// Enable OpenAPI UI
app.UseOpenApi();
app.UseSwaggerUi();

app.Run();
```

**OpenAPI behavior:**
- All endpoints are tagged with `typeof(TSubject).Name` (e.g., "Car")
- The GET endpoint declares `.Produces<TSubject>()` so Swagger displays the subject's schema
- The POST endpoint documents `Dictionary<string, JsonElement>` as input and `ProblemDetails` for errors

## Complete Example

Here's a complete example based on the `Namotion.Interceptor.SampleWeb` project:

**Subject definitions:**
```csharp
[InterceptorSubject]
public partial class Car
{
    public partial string Name { get; set; }
    public partial Tire[] Tires { get; set; }

    [Derived]
    public decimal AveragePressure => Tires.Average(t => t.Pressure);

    public Car()
    {
        Tires = Enumerable.Range(1, 4).Select(_ => new Tire()).ToArray();
        Name = "My Car";
    }
}

[InterceptorSubject]
public partial class Tire
{
    public partial decimal Pressure { get; set; }
}
```

**Program.cs:**
```csharp
var builder = WebApplication.CreateBuilder(args);

// Create context with all required extensions
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithDataAnnotationValidation();

// Create and register subject
var car = new Car(context);
builder.Services.AddSingleton(car);
builder.Services.AddSingleton(context);

// Add OpenAPI support
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApiDocument();

var app = builder.Build();

// Expose subject via REST API
app.MapSubjectWebApis<Car>("api/car");

// Enable Swagger UI
app.UseOpenApi();
app.UseSwaggerUi();

app.Run();
```

See also: `src/Namotion.Interceptor.SampleWeb` for a working example with OPC UA, MQTT, and GraphQL integration.
