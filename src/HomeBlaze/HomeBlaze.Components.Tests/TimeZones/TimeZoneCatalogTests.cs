using Xunit;
using HomeBlaze.Components.Abstractions.TimeZones;

namespace HomeBlaze.Components.Tests.TimeZones;

public class TimeZoneCatalogTests
{
    [Fact]
    public void WhenZonesRequested_ThenReturnsNonEmptySortedUniqueList()
    {
        // Act
        var zones = TimeZoneCatalog.GetZones();

        // Assert
        Assert.NotEmpty(zones);

        var ids = zones.Select(zone => zone.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(ids.OrderBy(id => id, StringComparer.Ordinal), ids);
    }

    [Fact]
    public void WhenZoneListed_ThenIdsResolveWithoutSilentlyFallingBackToUtc()
    {
        // Act
        var zones = TimeZoneCatalog.GetZones();

        // Assert
        foreach (var option in zones)
        {
            var resolved = TimeZoneResolver.Resolve(option.Id);
            if (resolved.Equals(TimeZoneInfo.Utc))
            {
                // The only catalog id allowed to resolve to UTC is the genuine UTC entry.
                Assert.Equal("UTC", option.Id);
            }
        }
    }

    [Fact]
    public void WhenZoneListed_ThenNoRawWindowsZoneNamesRemain()
    {
        // Act
        var zones = TimeZoneCatalog.GetZones();

        // Assert
        // Windows zone ids look like "Mid-Atlantic Standard Time"; only canonical IANA ids should remain.
        Assert.DoesNotContain(zones, zone =>
            zone.Id.Contains("Standard Time", StringComparison.OrdinalIgnoreCase) ||
            zone.Id.Contains("Daylight Time", StringComparison.OrdinalIgnoreCase));
    }
}
