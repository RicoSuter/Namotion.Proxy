using System.ComponentModel;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests;

public class PlainOriginBase
{
    public string Origin => "base";
}

[InterceptorSubject]
public partial class NarrowedOriginSubject : PlainOriginBase
{
    public new partial int Origin { get; set; }
}

public interface IHasLevel
{
    int Level => 0;
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class PrecedenceMarkerAttribute : Attribute;

[InterceptorSubject]
public partial class PrecedenceMachine : IHasLevel
{
    public partial string Name { get; set; }
}

[InterceptorSubject]
public partial class PrecedencePump : PrecedenceMachine, IHasLevel
{
    [PrecedenceMarker]
    public partial int Level { get; set; }
}

[InterceptorSubject]
public partial class PrecedenceSmartPump : PrecedencePump
{
    public partial string Label { get; set; }
}

[InterceptorSubject]
public partial class PrecedenceGauge
{
    [Description("declared on the base")]
    public virtual partial string Reading { get; set; }
}

[InterceptorSubject]
public partial class PrecedenceCalibratedGauge : PrecedenceGauge
{
    [PrecedenceMarker]
    public override partial string Reading { get; set; }
}

public class PropertyPrecedenceTests
{
    [Fact]
    public void WhenNewPropertyTypeDiffersFromTheHiddenOne_ThenPropertiesDoesNotThrow()
    {
        // Arrange
        var subject = new NarrowedOriginSubject();

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert
        Assert.Equal(typeof(int), properties["Origin"].Type);
        Assert.Equal(typeof(NarrowedOriginSubject), properties["Origin"].PropertyInfo?.DeclaringType);
    }

    [Fact]
    public void WhenLeafDeclaresAnInheritedInterfaceDefault_ThenTheLeafDeclarationWins()
    {
        // Arrange
        var pump = new PrecedencePump();
        pump.Level = 42;

        // Act
        var metadata = ((IInterceptorSubject)pump).Properties["Level"];

        // Assert
        Assert.Equal(typeof(PrecedencePump), metadata.PropertyInfo?.DeclaringType);
        Assert.True(metadata.IsIntercepted);
        Assert.NotNull(metadata.SetValue);
        Assert.Equal(42, metadata.GetValue?.Invoke(pump));
        Assert.Contains(metadata.Attributes, attribute => attribute is PrecedenceMarkerAttribute);
    }

    [Fact]
    public void WhenASubjectBelowTheDeclarationAddsNothing_ThenTheDeclarationStillWins()
    {
        // Arrange
        var smartPump = new PrecedenceSmartPump();
        smartPump.Level = 7;

        // Act
        var metadata = ((IInterceptorSubject)smartPump).Properties["Level"];

        // Assert
        Assert.Equal(typeof(PrecedencePump), metadata.PropertyInfo?.DeclaringType);
        Assert.True(metadata.IsIntercepted);
        Assert.Equal(7, metadata.GetValue?.Invoke(smartPump));
    }

    [Fact]
    public void WhenAnInterfaceDefaultIsNotDeclaredByAnySubject_ThenItIsStillExposed()
    {
        // Arrange
        var machine = new PrecedenceMachine();

        // Act
        var properties = ((IInterceptorSubject)machine).Properties;

        // Assert
        Assert.Equal(0, properties["Level"].GetValue?.Invoke(machine));
        Assert.False(properties["Level"].IsIntercepted);
    }

    [Fact]
    public void WhenLeafOverridesABasePartialProperty_ThenTheOverrideDeclarationWinsAndKeepsBothAttributes()
    {
        // Arrange
        var gauge = new PrecedenceCalibratedGauge();

        // Act
        var metadata = ((IInterceptorSubject)gauge).Properties["Reading"];

        // Assert
        Assert.Equal(typeof(PrecedenceCalibratedGauge), metadata.PropertyInfo?.DeclaringType);
        Assert.Contains(metadata.Attributes, attribute => attribute is PrecedenceMarkerAttribute);
        Assert.Contains(metadata.Attributes, attribute => attribute is DescriptionAttribute);
    }
}
