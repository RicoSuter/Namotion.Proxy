using Namotion.Interceptor.Ads.Mapping;
using Namotion.Interceptor.Ads.Tests.Models;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests.Mapping;

public class AdsPathProviderMapperTests
{
    [Fact]
    public void WhenAttributeBasedProviderMatches_ThenSegmentIsTheAttributePath()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new Machine(context);
        var property = TestHelpers.GetProperty(model, nameof(Machine.Name));
        var mapper = new AdsPathProviderMapper(new AttributeBasedPathProvider("ads", '.'));

        // Act
        var found = mapper.TryGetMapping(property, model, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("GVL.Machine.Name", mapping!.Segment);
        Assert.Null(mapping.ReadMode);
    }

    [Fact]
    public void WhenProviderNameDoesNotMatch_ThenReturnsFalse()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new Machine(context);
        var property = TestHelpers.GetProperty(model, nameof(Machine.Name));
        var mapper = new AdsPathProviderMapper(new AttributeBasedPathProvider("opcua", '.'));

        // Act
        var found = mapper.TryGetMapping(property, model, out var mapping);

        // Assert
        Assert.False(found);
        Assert.Null(mapping);
    }
}
