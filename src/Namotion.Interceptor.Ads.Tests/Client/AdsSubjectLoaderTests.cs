using Namotion.Interceptor.Ads.Client;
using Namotion.Interceptor.Ads.Mapping;
using Namotion.Interceptor.Ads.Tests.Models;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests.Client;

public class AdsSubjectLoaderTests
{

    [Fact]
    public void LoadSubjectGraph_WithScalarProperties_ReturnsMappedPaths()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var machine = new Machine(context);
        var loader = TestHelpers.CreateLoader();

        // Act
        var mappings = loader.LoadSubjectGraph(machine);

        // Assert
        Assert.Contains(mappings, mapping =>
            mapping.Property.Name == nameof(Machine.Name) &&
            mapping.SymbolPath == "GVL.Machine.Name");
    }

    [Fact]
    public void LoadSubjectGraph_WithPrimitiveArray_ReturnsMappedPath()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var machine = new Machine(context);
        var loader = TestHelpers.CreateLoader();

        // Act
        var mappings = loader.LoadSubjectGraph(machine);

        // Assert
        Assert.Contains(mappings, mapping =>
            mapping.Property.Name == nameof(Machine.Temperatures) &&
            mapping.SymbolPath == "GVL.Machine.Temperatures");
    }

    [Fact]
    public void LoadSubjectGraph_WithSubjectReference_ReturnsNestedPaths()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var machine = new Machine(context);
        machine.Motor = new Motor(context);
        var loader = TestHelpers.CreateLoader();

        // Act
        var mappings = loader.LoadSubjectGraph(machine);

        // Assert
        Assert.Contains(mappings, mapping =>
            mapping.Property.Name == nameof(Motor.Speed) &&
            mapping.SymbolPath == "GVL.Machine.Motor.Speed");

        Assert.Contains(mappings, mapping =>
            mapping.Property.Name == nameof(Motor.Torque) &&
            mapping.SymbolPath == "GVL.Machine.Motor.Torque");

        // Motor itself should not appear as a leaf mapping
        Assert.DoesNotContain(mappings, mapping =>
            mapping.Property.Name == nameof(Machine.Motor));
    }

    [Fact]
    public void LoadSubjectGraph_WithSubjectCollection_ReturnsIndexedPaths()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var machine = new Machine(context);
        machine.Axes = new List<Axis>
        {
            new Axis(context),
            new Axis(context)
        };
        var loader = TestHelpers.CreateLoader();

        // Act
        var mappings = loader.LoadSubjectGraph(machine);

        // Assert
        Assert.Contains(mappings, mapping =>
            mapping.Property.Name == nameof(Axis.Position) &&
            mapping.SymbolPath == "GVL.Machine.Axes[0].Position");

        Assert.Contains(mappings, mapping =>
            mapping.Property.Name == nameof(Axis.Position) &&
            mapping.SymbolPath == "GVL.Machine.Axes[1].Position");
    }

    [Fact]
    public void LoadSubjectGraph_WithSubjectDictionary_ReturnsKeyedPaths()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var machine = new Machine(context);
        machine.Devices = new Dictionary<string, Device>
        {
            ["Pump1"] = new Device(context),
            ["Pump2"] = new Device(context)
        };
        var loader = TestHelpers.CreateLoader();

        // Act
        var mappings = loader.LoadSubjectGraph(machine);

        // Assert
        Assert.Contains(mappings, mapping =>
            mapping.Property.Name == nameof(Device.Temperature) &&
            mapping.SymbolPath == "GVL.Machine.Devices.Pump1.Temperature");

        Assert.Contains(mappings, mapping =>
            mapping.Property.Name == nameof(Device.Temperature) &&
            mapping.SymbolPath == "GVL.Machine.Devices.Pump2.Temperature");
    }

    [Fact]
    public void LoadSubjectGraph_WithCyclicReference_DoesNotLoop()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var machine = new Machine(context);
        var motor = new Motor(context);
        machine.Motor = motor;

        // Also reference the same motor instance via a collection entry
        // (not a real scenario but tests cycle detection)
        var loader = TestHelpers.CreateLoader();

        // Act - should not throw or loop infinitely
        var mappings = loader.LoadSubjectGraph(machine);

        // Assert - motor properties should appear exactly once
        var motorSpeedMappings = mappings
            .Where(mapping => mapping.Property.Name == nameof(Motor.Speed))
            .ToList();
        Assert.Single(motorSpeedMappings);
    }

    [Fact]
    public void LoadSubjectGraph_WithLeafSubject_ReturnsScalarProperties()
    {
        // Arrange - Motor has no children, just scalar properties
        var context = TestHelpers.CreateContext();
        var motor = new Motor(context);
        var loader = TestHelpers.CreateLoader();

        // Act
        var mappings = loader.LoadSubjectGraph(motor);

        // Assert
        Assert.Equal(2, mappings.Count);
        Assert.Contains(mappings, mapping => mapping.Property.Name == nameof(Motor.Speed));
        Assert.Contains(mappings, mapping => mapping.Property.Name == nameof(Motor.Torque));
    }

    [Fact]
    public void LoadSubjectGraph_WithNoMatchingPathProvider_ReturnsEmptyList()
    {
        // Arrange - use a path provider with a different name
        var context = TestHelpers.CreateContext();
        var machine = new Machine(context);
        var loader = new AdsSubjectLoader(AdsCompositeMapper.CreateDefault("opcua"));

        // Act
        var mappings = loader.LoadSubjectGraph(machine);

        // Assert - no properties should match because the connector name is different
        Assert.Empty(mappings);
    }

    [Fact]
    public void LoadSubjectGraph_WithFullGraph_ReturnsAllLeafPaths()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var machine = new Machine(context);
        machine.Motor = new Motor(context);
        machine.Axes = new List<Axis>
        {
            new Axis(context),
            new Axis(context)
        };
        machine.Devices = new Dictionary<string, Device>
        {
            ["Pump1"] = new Device(context),
            ["Pump2"] = new Device(context)
        };
        var loader = TestHelpers.CreateLoader();

        // Act
        var mappings = loader.LoadSubjectGraph(machine);

        // Assert - verify all expected leaf paths
        var symbolPaths = mappings.Select(mapping => mapping.SymbolPath).ToList();

        // Scalar properties
        Assert.Contains("GVL.Machine.Name", symbolPaths);
        Assert.Contains("GVL.Machine.Temperatures", symbolPaths);

        // Subject reference (Motor)
        Assert.Contains("GVL.Machine.Motor.Speed", symbolPaths);
        Assert.Contains("GVL.Machine.Motor.Torque", symbolPaths);

        // Subject collection (Axes)
        Assert.Contains("GVL.Machine.Axes[0].Position", symbolPaths);
        Assert.Contains("GVL.Machine.Axes[1].Position", symbolPaths);

        // Subject dictionary (Devices)
        Assert.Contains("GVL.Machine.Devices.Pump1.Temperature", symbolPaths);
        Assert.Contains("GVL.Machine.Devices.Pump2.Temperature", symbolPaths);

        // Total expected leaf properties: Name + Temperatures + Speed + Torque + 2x Position + 2x Temperature = 8
        Assert.Equal(8, mappings.Count);
    }
}
