using Namotion.Interceptor.Ads;
using Namotion.Interceptor.Ads.Mapping;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests.Mapping;

public class AdsPropertyMappingTests
{
    [Fact]
    public void WhenMerging_ThenPrimaryNonNullFieldsWin()
    {
        // Arrange
        var primary = new AdsPropertyMapping(Segment: "A", ReadMode: AdsReadMode.Polled, CycleTime: 200);
        var fallback = new AdsPropertyMapping(Segment: "B", ReadMode: AdsReadMode.Notification, CycleTime: 50, MaxDelay: 10, Priority: 5);

        // Act
        var merged = AdsPropertyMapping.Merge(primary, fallback);

        // Assert
        Assert.Equal("A", merged.Segment);
        Assert.Equal(AdsReadMode.Polled, merged.ReadMode);
        Assert.Equal(200, merged.CycleTime);
        Assert.Equal(10, merged.MaxDelay);   // fallback fills where primary is null
        Assert.Equal(5, merged.Priority);
    }

    [Fact]
    public void WhenPrimaryFieldIsNull_ThenFallbackValueIsUsed()
    {
        // Arrange
        var primary = new AdsPropertyMapping(CycleTime: 100);
        var fallback = new AdsPropertyMapping(Segment: "GVL.X", ReadMode: AdsReadMode.Polled, MaxDelay: 7, Priority: 3);

        // Act
        var merged = AdsPropertyMapping.Merge(primary, fallback);

        // Assert
        Assert.Equal("GVL.X", merged.Segment);
        Assert.Equal(AdsReadMode.Polled, merged.ReadMode);
        Assert.Equal(100, merged.CycleTime);
        Assert.Equal(7, merged.MaxDelay);
        Assert.Equal(3, merged.Priority);
    }
}
