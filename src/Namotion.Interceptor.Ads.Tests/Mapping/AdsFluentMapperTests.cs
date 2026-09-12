using Namotion.Interceptor.Ads.Client;
using Namotion.Interceptor.Ads.Mapping;
using Namotion.Interceptor.Ads.Tests.Models;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests.Mapping;

public class AdsFluentMapperTests
{
    [Fact]
    public void WhenPropertyMappedFluently_ThenSegmentAndKnobsAreReturned()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new AutoModeModel(context);
        var property = TestHelpers.GetProperty(model, nameof(AutoModeModel.Value));
        var fluent = new AdsFluentMapperBuilder<AutoModeModel>()
            .ForType<AutoModeModel>()
                .Map(x => x.Value, c => c.WithSymbolPath("GVL.Fluent").WithReadMode(AdsReadMode.Polled).WithCycleTime(250).WithMaxDelay(30))
            .Build();

        // Act
        var found = fluent.TryGetMapping(property, model, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("GVL.Fluent", mapping!.Segment);
        Assert.Equal(AdsReadMode.Polled, mapping.ReadMode);
        Assert.Equal(250, mapping.CycleTime);
        Assert.Equal(30, mapping.MaxDelay);
    }

    [Fact]
    public void WhenPropertyNotMappedFluently_ThenReturnsFalse()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new NotificationOnlyModel(context);
        var property = TestHelpers.GetProperty(model, nameof(NotificationOnlyModel.Value));
        var fluent = new AdsFluentMapperBuilder<AutoModeModel>()
            .ForType<AutoModeModel>()
                .Map(x => x.Value, c => c.WithSymbolPath("GVL.Fluent"))
            .Build();

        // Act
        var found = fluent.TryGetMapping(property, model, out var mapping);

        // Assert
        Assert.False(found);
        Assert.Null(mapping);
    }

    [Fact]
    public void WhenComposedAfterAttributeMapper_ThenFluentWinsOnOverlap()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var model = new NotificationOnlyModel(context);
        var property = TestHelpers.GetProperty(model, nameof(NotificationOnlyModel.Value));
        var fluent = new AdsFluentMapperBuilder<NotificationOnlyModel>()
            .ForType<NotificationOnlyModel>()
                .Map(x => x.Value, c => c.WithSymbolPath("GVL.Value").WithReadMode(AdsReadMode.Polled))
            .Build();
        var composite = new AdsCompositeMapper(AdsCompositeMapper.CreateDefault(), fluent);

        // Act
        var found = composite.TryGetMapping(property, model, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal(AdsReadMode.Polled, mapping!.ReadMode);
        Assert.Equal(50, mapping.CycleTime);
    }

    [Fact]
    public void WhenLoaderUsesFluentMapper_ThenComposesFullSymbolPathAndKnobs()
    {
        // Arrange - flat model mapped entirely in code (no attributes needed for the fluent leaf)
        var context = TestHelpers.CreateContext();
        var model = new AutoModeModel(context);
        var fluent = new AdsFluentMapperBuilder<AutoModeModel>()
            .ForType<AutoModeModel>()
                .Map(x => x.Value, c => c.WithSymbolPath("GVL.Fluent").WithReadMode(AdsReadMode.Polled).WithCycleTime(250))
            .Build();
        var loader = new AdsSubjectLoader(fluent);

        // Act
        var mappings = loader.LoadSubjectGraph(model);

        // Assert
        var entry = Assert.Single(mappings);
        Assert.Equal(nameof(AutoModeModel.Value), entry.Property.Name);
        Assert.Equal("GVL.Fluent", entry.SymbolPath);
        Assert.Equal(AdsReadMode.Polled, entry.Mapping.ReadMode);
        Assert.Equal(250, entry.Mapping.CycleTime);
    }

    [Fact]
    public void WhenFluentComposedWithAttributes_ThenFluentOverridesAndAttributesRemainForOthers()
    {
        // Arrange - Machine has attribute-mapped members; fluent overrides only Machine.Name's segment
        var context = TestHelpers.CreateContext();
        var machine = new Machine(context);
        machine.Motor = new Motor(context);
        var fluent = new AdsFluentMapperBuilder<Machine>()
            .ForType<Machine>()
                .Map(x => x.Name, c => c.WithSymbolPath("GVL.Renamed"))
            .Build();
        var mapper = new AdsCompositeMapper(AdsCompositeMapper.CreateDefault(), fluent);
        var loader = new AdsSubjectLoader(mapper);

        // Act
        var symbolPaths = loader.LoadSubjectGraph(machine).Select(m => m.SymbolPath).ToList();

        // Assert
        Assert.Contains("GVL.Renamed", symbolPaths);                 // fluent override wins
        Assert.DoesNotContain("GVL.Machine.Name", symbolPaths);      // attribute segment replaced
        Assert.Contains("GVL.Machine.Motor.Speed", symbolPaths);     // unrelated attribute mapping intact
    }
}
