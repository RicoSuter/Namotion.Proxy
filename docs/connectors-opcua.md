# OPC UA

The `Namotion.Interceptor.OpcUa` package provides integration between Namotion.Interceptor and OPC UA (Open Platform Communications Unified Architecture), enabling bidirectional synchronization between C# objects and industrial automation systems. It supports both client and server modes.

Both built-in OPC UA connectors implement liveness monitoring. Before their first protocol-specific liveness observation, `IsOperational` can be `null`; after that observation, they publish explicit `true` or `false` values. The client registers the `ClaimedPropertyCount` gauge, so its count is measured, including zero.

- [OPC UA Client](connectors-opcua-client.md) - Configuration, authentication, monitoring, resilience, extensibility
- [OPC UA Server](connectors-opcua-server.md) - Configuration, security, companion specs, diagnostics
- [OPC UA Mapping](connectors-opcua-mapping.md) - Attributes, fluent configuration, companion spec patterns

## Key Features

- Bidirectional synchronization between C# objects and OPC UA nodes
- Dynamic property discovery from OPC UA servers
- Attribute-based mapping for precise control
- Subscription-based real-time updates
- Arrays, nested objects, and collections support

## Quick Start

### Client

Connect to an OPC UA server by configuring a client with `AddOpcUaSubjectClientSource`:

```csharp
[InterceptorSubject]
public partial class Machine
{
    [Path("opc", "Temperature")]
    public partial decimal Temperature { get; set; }

    [Path("opc", "Speed")]
    public partial decimal Speed { get; set; }
}

builder.Services.AddSingleton(machine);
builder.Services.AddOpcUaSubjectClientSource<Machine>(
    serverUrl: "opc.tcp://plc.factory.com:4840",
    connectorName: "opc",
    rootPath: ["MyMachine"]);

// ...
var host = builder.Build();
await host.StartAsync();
Console.WriteLine(machine.Temperature); // Synchronized with OPC UA server
machine.Speed = 100; // Writes to OPC UA server

// Access diagnostics via DI (unnamed singleton)
var source = serviceProvider.GetRequiredService<IOpcUaSubjectClientSource>();
Console.WriteLine(source.Diagnostics.IsOperational);
```

See [OPC UA Client](connectors-opcua-client.md) for configuration, authentication, monitoring, resilience, and extensibility, and [Source Monitoring](connectors-monitoring.md) for waiting until the model is in sync rather than merely connected.

### Server

Expose your C# objects as an OPC UA server with `AddOpcUaSubjectServer`:

```csharp
[InterceptorSubject]
public partial class Sensor
{
    [Path("opc", "Value")]
    public partial decimal Value { get; set; }
}

builder.Services.AddSingleton(sensor);
builder.Services.AddOpcUaSubjectServer<Sensor>(
    connectorName: "opc",
    rootName: "MySensor");

// ...
sensor.Value = 42.5m;
await host.StartAsync();

// Access diagnostics via DI (unnamed singleton)
var server = serviceProvider.GetRequiredService<IOpcUaSubjectServer>();
Console.WriteLine(server.Diagnostics.IsOperational);
```

See [OPC UA Server](connectors-opcua-server.md) for configuration, security, companion specifications, and diagnostics.

## Property Mapping

Both client and server configurations include a `Mapper` property that controls how C# properties map to OPC UA nodes. The client uses `IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>` (forward mapping plus reverse lookup from OPC UA node references), while the server uses `IPropertyMapper<OpcUaPropertyMapping>` (forward mapping only). The default for both is an `OpcUaCompositeMapper` combining `OpcUaPathProviderMapper` (maps `[Path]` attributes) and `OpcUaAttributeMapper` (maps `[OpcUaNode]` / `[OpcUaReference]` attributes). `OpcUaFluentMapperBuilder<TRoot>` is also available for code-based type-level configuration. Custom mappers can be added to the composite chain. See [Property Mappers](connectors.md#property-mappers) for the generic abstraction that these types implement.

For simple cases, use `[Path]`. For advanced OPC UA-specific configuration, use `[OpcUaNode]` and related attributes:

```csharp
[InterceptorSubject]
[OpcUaNode("Machine", TypeDefinition = "MachineType")]  // Class-level type definition
public partial class Machine
{
    // Simple property mapping
    [Path("opc", "Status")]
    public partial int Status { get; set; }

    // OPC UA-specific mapping with monitoring config
    [OpcUaNode("Temperature", SamplingInterval = 100, DeadbandType = DeadbandType.Absolute, DeadbandValue = 0.5)]
    public partial double Temperature { get; set; }

    // Child object with HasComponent reference
    [OpcUaReference("HasComponent")]
    [OpcUaNode("MainMotor")]
    public partial Motor? Motor { get; set; }
}
```

For comprehensive mapping documentation including companion spec support, VariableTypes, fluent configuration, and composite mappers, see [OPC UA Mapping Guide](connectors-opcua-mapping.md).

## Type Conversions

OPC UA doesn't natively support C# `decimal`, so automatic conversion is applied:

- `decimal` <-> `double`
- `decimal[]` <-> `double[]`

All other C# primitive types map directly to OPC UA built-in types.

### Custom Value Converter

Both client and server configurations use `OpcUaValueConverter` for type conversions. The converter handles three concerns: converting OPC UA values to C# property values (`ConvertToPropertyValue`), converting C# values back to OPC UA (`ConvertToNodeValue`), and resolving OPC UA type information for C# types (`GetNodeTypeInfo`, used by the server to declare correct data types in the address space). Extend it to add custom conversions:

```csharp
public class CustomValueConverter : OpcUaValueConverter
{
    public override object? ConvertToPropertyValue(
        object? nodeValue, RegisteredSubjectProperty property)
    {
        if (property.Type == typeof(MyCustomType) && nodeValue is string str)
            return MyCustomType.Parse(str);
        return base.ConvertToPropertyValue(nodeValue, property);
    }

    public override object? ConvertToNodeValue(
        object? propertyValue, RegisteredSubjectProperty property)
    {
        if (propertyValue is MyCustomType custom)
            return custom.ToString();
        return base.ConvertToNodeValue(propertyValue, property);
    }
}
```

## Custom Application Configuration

Both client and server configurations support overriding `CreateApplicationInstanceAsync()` for full control over OPC UA application settings (certificates, transport quotas, security policies). See [Client](connectors-opcua-client.md#custom-application-configuration) and [Server](connectors-opcua-server.md#custom-application-configuration) for side-specific examples.

## Lifecycle Limitations

The OPC UA integration takes a snapshot of the object model at startup. Both client and server share these limitations:

- Does NOT dynamically add new subjects to OPC UA after initialization
- Does NOT update the OPC UA address space when subjects are attached
- New subjects added after startup require a restart to appear in OPC UA

For side-specific cleanup behavior, see [Client Lifecycle](connectors-opcua-client.md#lifecycle) and [Server Lifecycle](connectors-opcua-server.md#lifecycle).

## Performance

The library includes optimizations:

- Batched read/write operations respecting server limits
- Object pooling for change buffers
- Fast data change callbacks bypassing caches
- Buffered updates batching rapid changes

## Benchmark Results

Intel(R) Core(TM) Ultra 7 258V

```
Server Benchmark - 1 minute - [2026-04-08 21:20:22.783]

Total received changes:          1192428
Total published changes:         1197644
Process memory:                  693.08 MB (429.11 MB in .NET heap)
Avg allocations over last 60s:   514.93 MB/s

Metric                               Avg        P50        P90        P95        P99      P99.9        Max        Min     StdDev      Count
-------------------------------------------------------------------------------------------------------------------------------------------
Received (changes/s)            19864.43   19854.86   20608.93   21045.00   24794.77   24794.77   24794.77   15362.84    1146.70          -
Processing latency (ms)             0.03       0.00       0.02       0.06       0.29       5.97      18.52       0.00       0.35    1192428
End-to-end latency (ms)            29.95      25.51      42.64      54.74     187.44     278.54     298.14       0.35      27.88    1192428
```

```
Client Benchmark - 1 minute - [2026-04-08 21:17:23.974]

Total received changes:          1197975
Total published changes:         1188810
Process memory:                  510.89 MB (258.39 MB in .NET heap)
Avg allocations over last 60s:   27.25 MB/s

Metric                               Avg        P50        P90        P95        P99      P99.9        Max        Min     StdDev      Count
-------------------------------------------------------------------------------------------------------------------------------------------
Received (changes/s)            19955.91   20037.53   20749.58   20854.92   21974.69   21974.69   21974.69   17305.18     673.79          -
Processing latency (ms)             1.68       0.82       1.70       2.11      26.43      36.48      37.82       0.00       4.54    1197975
End-to-end latency (ms)            52.77      50.21      81.95      90.78     134.55     219.26     277.27       0.99      24.47    1197975
```
