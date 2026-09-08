using Xunit;
using HomeBlaze.Components.Abstractions.TimeZones;

namespace HomeBlaze.Components.Tests.TimeZones;

public class TimeZoneResolverTests
{
    [Fact]
    public void WhenIdIsValidIana_ThenResolvesThatZone()
    {
        // Arrange
        var januaryNoon = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Unspecified);

        // Act
        var zone = TimeZoneResolver.Resolve("Europe/Zurich");

        // Assert
        Assert.Equal(TimeSpan.FromHours(1), zone.GetUtcOffset(januaryNoon));
    }

    [Fact]
    public void WhenIdIsUnknown_ThenFallsBackToUtc()
    {
        // Act
        var zone = TimeZoneResolver.Resolve("Totally/Invalid");

        // Assert
        Assert.Equal(TimeZoneInfo.Utc, zone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenIdIsMissing_ThenFallsBackToUtc(string? id)
    {
        // Act
        var zone = TimeZoneResolver.Resolve(id);

        // Assert
        Assert.Equal(TimeZoneInfo.Utc, zone);
    }
}
