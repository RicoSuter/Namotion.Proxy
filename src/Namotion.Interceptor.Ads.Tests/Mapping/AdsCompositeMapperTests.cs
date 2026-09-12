using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Ads.Mapping;
using Namotion.Interceptor.Ads.Tests.Models;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests.Mapping;

public class AdsCompositeMapperTests
{
    [Fact]
    public void WhenDefaultComposite_ThenMergesSegmentFromPathAndKnobsFromAttribute()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new NotificationOnlyModel(context);
        var property = TestHelpers.GetProperty(model, nameof(NotificationOnlyModel.Value));
        var mapper = AdsCompositeMapper.CreateDefault();

        // Act
        var found = mapper.TryGetMapping(property, model, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("GVL.Value", mapping!.Segment);
        Assert.Equal(AdsReadMode.Notification, mapping.ReadMode);
        Assert.Equal(50, mapping.CycleTime);
    }

    [Fact]
    public void WhenLaterMapperSetsField_ThenItWinsOverEarlier()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new AutoModeModel(context);
        var property = TestHelpers.GetProperty(model, nameof(AutoModeModel.Value));
        var earlier = new StubMapper(new AdsPropertyMapping(Segment: "X", ReadMode: AdsReadMode.Notification));
        var later = new StubMapper(new AdsPropertyMapping(ReadMode: AdsReadMode.Polled));
        var composite = new AdsCompositeMapper(earlier, later);

        // Act
        var found = composite.TryGetMapping(property, model, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("X", mapping!.Segment);
        Assert.Equal(AdsReadMode.Polled, mapping.ReadMode);
    }

    [Fact]
    public void WhenNoMapperMatches_ThenReturnsFalse()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new AutoModeModel(context);
        var property = TestHelpers.GetProperty(model, nameof(AutoModeModel.Value));
        var composite = new AdsCompositeMapper(new StubMapper(mapping: null));

        // Act
        var found = composite.TryGetMapping(property, model, out var mapping);

        // Assert
        Assert.False(found);
        Assert.Null(mapping);
    }

    [Fact]
    public void WhenConstructedWithNullMember_ThenThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new AdsCompositeMapper(new StubMapper(null), null!));
    }

    private sealed class StubMapper : IPropertyMapper<AdsPropertyMapping>
    {
        private readonly AdsPropertyMapping? _mapping;
        public StubMapper(AdsPropertyMapping? mapping) => _mapping = mapping;

        public bool TryGetMapping(
            RegisteredSubjectProperty property,
            IInterceptorSubject rootSubject,
            [NotNullWhen(true)] out AdsPropertyMapping? mapping)
        {
            mapping = _mapping;
            return _mapping is not null;
        }
    }
}
