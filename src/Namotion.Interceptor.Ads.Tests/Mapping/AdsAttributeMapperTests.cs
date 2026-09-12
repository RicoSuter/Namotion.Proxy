using Namotion.Interceptor.Ads;
using Namotion.Interceptor.Ads.Mapping;
using Namotion.Interceptor.Ads.Tests.Models;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests.Mapping;

public class AdsAttributeMapperTests
{
    [Fact]
    public void WhenAttributeSetsKnobs_ThenMappingCarriesThemAndNoSegment()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new NotificationOnlyModel(context);
        var property = TestHelpers.GetProperty(model, nameof(NotificationOnlyModel.Value));
        var mapper = new AdsAttributeMapper();

        // Act
        var found = mapper.TryGetMapping(property, model, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Null(mapping!.Segment);
        Assert.Equal(AdsReadMode.Notification, mapping.ReadMode);
        Assert.Equal(50, mapping.CycleTime);
        Assert.Null(mapping.MaxDelay);
        Assert.Equal(0, mapping.Priority);
    }

    [Fact]
    public void WhenAttributeUsesAutoAndDefaults_ThenReadModeAndCycleTimeAreNull()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new AutoModeModel(context);
        var property = TestHelpers.GetProperty(model, nameof(AutoModeModel.Value));
        var mapper = new AdsAttributeMapper();

        // Act
        var found = mapper.TryGetMapping(property, model, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Null(mapping!.ReadMode);
        Assert.Null(mapping.CycleTime);
        Assert.Null(mapping.MaxDelay);
        Assert.Equal(0, mapping.Priority);
    }

    [Fact]
    public void WhenConnectorNameDoesNotMatch_ThenReturnsFalse()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new AutoModeModel(context);
        var property = TestHelpers.GetProperty(model, nameof(AutoModeModel.Value));
        var mapper = new AdsAttributeMapper("other");

        // Act
        var found = mapper.TryGetMapping(property, model, out var mapping);

        // Assert
        Assert.False(found);
        Assert.Null(mapping);
    }
}
