using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Generator.Tests;

#region Test Interfaces and Classes

public interface ITemperatureSensorInterface
{
    double TemperatureCelsius { get; set; }

    [Derived]
    double TemperatureFahrenheit => TemperatureCelsius * 9 / 5 + 32;

    bool IsFreezing => TemperatureCelsius <= 0;
}

[InterceptorSubject]
public partial class SensorWithInterfaceDefaults : ITemperatureSensorInterface
{
    public partial double TemperatureCelsius { get; set; }
}

public partial class NestedInterfaceContainer
{
    public interface INestedSensor
    {
        double Value { get; set; }

        string Status => Value > 0 ? "Active" : "Inactive";
    }
}

[InterceptorSubject]
public partial class SensorWithNestedInterface : NestedInterfaceContainer.INestedSensor
{
    public partial double Value { get; set; }
}

public interface IWritableDefaultInterface
{
    double Temperature { get; set; }

    string Label { get => $"Temp: {Temperature}"; set { } }
}

[InterceptorSubject]
public partial class SensorWithWritableDefault : IWritableDefaultInterface
{
    public partial double Temperature { get; set; }
}

public interface IInitOnlyInterface
{
    string Id { get; init; }

    string DisplayId => $"ID: {Id}";
}

[InterceptorSubject]
public partial class SensorWithInitOnly : IInitOnlyInterface
{
    public partial string Id { get; init; }
}

#endregion

public class InterfaceDefaultPropertyBehaviorTests
{
    [Fact]
    public void InterfaceDefaultProperty_RegisteredAndWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var sensor = new SensorWithInterfaceDefaults(context) { TemperatureCelsius = 25.0 };

        // Assert: properties appear in registry
        var registeredSubject = sensor.TryGetRegisteredSubject();
        Assert.NotNull(registeredSubject);
        var propertyNames = registeredSubject.Properties.Select(p => p.Name).ToList();
        Assert.Contains("TemperatureCelsius", propertyNames);
        Assert.Contains("TemperatureFahrenheit", propertyNames);
        Assert.Contains("IsFreezing", propertyNames);

        // Assert: getter works
        var fahrenheitProp = registeredSubject.TryGetProperty("TemperatureFahrenheit");
        Assert.NotNull(fahrenheitProp);
        Assert.Equal(77.0, fahrenheitProp.GetValue());

        // Assert: default property metadata
        var defaultProp = SensorWithInterfaceDefaults.DefaultProperties["TemperatureFahrenheit"];
        Assert.False(defaultProp.IsIntercepted);
        Assert.Null(defaultProp.SetValue);
    }

    [Fact]
    public void NestedInterface_RegisteredAndWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var sensor = new SensorWithNestedInterface(context) { Value = 42.0 };

        // Assert
        var registeredSubject = sensor.TryGetRegisteredSubject();
        Assert.NotNull(registeredSubject);
        var propertyNames = registeredSubject.Properties.Select(p => p.Name).ToList();
        Assert.Contains("Value", propertyNames);
        Assert.Contains("Status", propertyNames);

        var statusProp = SensorWithNestedInterface.DefaultProperties["Status"];
        Assert.False(statusProp.IsIntercepted);
        Assert.Equal("Active", statusProp.GetValue!(sensor));
    }

    [Fact]
    public void WritableInterfaceDefault_RegisteredAndWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var sensor = new SensorWithWritableDefault(context) { Temperature = 25.0 };

        // Assert
        var registeredSubject = sensor.TryGetRegisteredSubject();
        Assert.NotNull(registeredSubject);
        var propertyNames = registeredSubject.Properties.Select(p => p.Name).ToList();
        Assert.Contains("Temperature", propertyNames);
        Assert.Contains("Label", propertyNames);

        var labelProp = SensorWithWritableDefault.DefaultProperties["Label"];
        Assert.False(labelProp.IsIntercepted);
        Assert.Equal("Temp: 25", labelProp.GetValue!(sensor));
        Assert.NotNull(labelProp.SetValue); // Interface default setters are now supported
    }

    [Fact]
    public void InitOnlyInterface_RegisteredAndWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var sensor = new SensorWithInitOnly(context) { Id = "ABC123" };

        // Assert
        var registeredSubject = sensor.TryGetRegisteredSubject();
        Assert.NotNull(registeredSubject);
        var propertyNames = registeredSubject.Properties.Select(p => p.Name).ToList();
        Assert.Contains("Id", propertyNames);
        Assert.Contains("DisplayId", propertyNames);

        var displayIdProp = SensorWithInitOnly.DefaultProperties["DisplayId"];
        Assert.False(displayIdProp.IsIntercepted);
        Assert.Equal("ID: ABC123", displayIdProp.GetValue!(sensor));
    }

    [Fact]
    public void WhenInterfaceDefaultHasOnlyAnInitAccessorReachable_ThenItIsNotASubjectProperty()
    {
        // Arrange: the property-level check passes because init is accessible, but generated code
        // cannot call an init accessor from a lambda and cannot read a protected getter, so the entry
        // would carry two null accessors and do nothing.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IProbe
    {
        string Probe { protected get => ""x""; init { } }
    }

    [InterceptorSubject]
    public partial class Subject : IProbe
    {
        public partial string Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Empty(generated.GeneratorDiagnostics);
        var generatedSource = generated.Sources.Single().SourceText.ToString();
        Assert.Contains("\"Name\"", generatedSource);
        Assert.DoesNotContain("\"Probe\"", generatedSource);
    }

    [Fact]
    public void WhenExplicitImplementationHasOnlyAnInitAccessorReachable_ThenItIsReportedAndSkipped()
    {
        // Arrange: same degenerate shape reached through CollectProperties instead. Reported rather
        // than silent, because the author opted in by writing the implementation in their own file.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IProbe
    {
        string Probe { protected get; init; }
    }

    [InterceptorSubject]
    public partial class Subject : IProbe
    {
        string IProbe.Probe { get => ""x""; init { } }

        public partial string Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Single(generated.GeneratorDiagnostics, diagnostic => diagnostic.Id == "NI0040");
        var generatedSource = generated.Sources.Single().SourceText.ToString();
        Assert.Contains("\"Name\"", generatedSource);
        Assert.DoesNotContain("\"Probe\"", generatedSource);
    }
}
