using Namotion.Interceptor.Ads.Attributes;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests.Attributes;

public class AdsVariableAttributeTests
{
    [Fact]
    public void Constructor_WithSymbolPath_SetsPropertiesCorrectly()
    {
        // Arrange & Act
        var attribute = new AdsVariableAttribute("GVL.Temperature");

        // Assert
        Assert.Equal("GVL.Temperature", attribute.SymbolPath);
        Assert.Equal("GVL.Temperature", attribute.Path);
        Assert.Equal(AdsConstants.DefaultConnectorName, attribute.Name);
    }

    [Fact]
    public void Constructor_WithNullSymbolPath_Throws()
    {
        // Arrange & Act & Assert - a null path would otherwise surface much later as a symbol
        // that never resolves, with nothing pointing back at the attribute
        Assert.Throws<ArgumentNullException>(() => new AdsVariableAttribute(null!));
    }

    [Fact]
    public void Constructor_WithCustomConnectorName_UsesCustomName()
    {
        // Arrange & Act
        var attribute = new AdsVariableAttribute("GVL.Temperature", "custom-ads");

        // Assert
        Assert.Equal("GVL.Temperature", attribute.SymbolPath);
        Assert.Equal("custom-ads", attribute.Name);
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var attribute = new AdsVariableAttribute("GVL.Temperature");

        // Assert
        Assert.Equal(AdsReadMode.Auto, attribute.ReadMode);
        Assert.Equal(int.MinValue, attribute.CycleTime);
        Assert.Equal(int.MinValue, attribute.MaxDelay);
        Assert.Equal(0, attribute.Priority);
    }

    [Fact]
    public void ReadMode_CanBeSetViaInit()
    {
        // Arrange & Act
        var attribute = new AdsVariableAttribute("GVL.Temperature")
        {
            ReadMode = AdsReadMode.Notification
        };

        // Assert
        Assert.Equal(AdsReadMode.Notification, attribute.ReadMode);
    }

    [Fact]
    public void CycleTime_CanBeSetViaInit()
    {
        // Arrange & Act
        var attribute = new AdsVariableAttribute("GVL.Temperature")
        {
            CycleTime = 50
        };

        // Assert
        Assert.Equal(50, attribute.CycleTime);
    }

    [Fact]
    public void MaxDelay_CanBeSetViaInit()
    {
        // Arrange & Act
        var attribute = new AdsVariableAttribute("GVL.Temperature")
        {
            MaxDelay = 10
        };

        // Assert
        Assert.Equal(10, attribute.MaxDelay);
    }

    [Fact]
    public void Priority_CanBeSetViaInit()
    {
        // Arrange & Act
        var attribute = new AdsVariableAttribute("GVL.Temperature")
        {
            Priority = 5
        };

        // Assert
        Assert.Equal(5, attribute.Priority);
    }

    [Fact]
    public void AllProperties_CanBeSetTogether()
    {
        // Arrange & Act
        var attribute = new AdsVariableAttribute("GVL.Motor.Speed", "plc1")
        {
            ReadMode = AdsReadMode.Polled,
            CycleTime = 200,
            MaxDelay = 50,
            Priority = 10
        };

        // Assert
        Assert.Equal("GVL.Motor.Speed", attribute.SymbolPath);
        Assert.Equal("plc1", attribute.Name);
        Assert.Equal(AdsReadMode.Polled, attribute.ReadMode);
        Assert.Equal(200, attribute.CycleTime);
        Assert.Equal(50, attribute.MaxDelay);
        Assert.Equal(10, attribute.Priority);
    }

    [Fact]
    public void Attribute_InheritsFromPathAttribute()
    {
        // Arrange & Act
        var attribute = new AdsVariableAttribute("GVL.Temperature");

        // Assert - verifies inheritance chain
        Assert.IsAssignableFrom<Namotion.Interceptor.Registry.Attributes.PathAttribute>(attribute);
    }
}
