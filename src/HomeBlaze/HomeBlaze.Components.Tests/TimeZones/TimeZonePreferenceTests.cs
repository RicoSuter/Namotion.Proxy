using Xunit;
using HomeBlaze.Components.Abstractions.TimeZones;

namespace HomeBlaze.Components.Tests.TimeZones;

public class TimeZonePreferenceTests
{
    [Fact]
    public void WhenDefault_ThenIsAutomatic()
    {
        // Act
        var preference = default(TimeZonePreference);

        // Assert
        Assert.True(preference.IsAutomatic);
        Assert.Equal(TimeZonePreference.AutomaticToken, preference.ToCookieValue());
    }

    [Fact]
    public void WhenSpecific_ThenCookieValueIsTheZoneId()
    {
        // Act
        var preference = TimeZonePreference.Specific("Europe/Zurich");

        // Assert
        Assert.False(preference.IsAutomatic);
        Assert.Equal("Europe/Zurich", preference.ToCookieValue());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Automatic")]
    public void WhenCookieValueIsMissingOrAutomatic_ThenParsesAsAutomatic(string? value)
    {
        // Act
        var preference = TimeZonePreference.FromCookieValue(value);

        // Assert
        Assert.True(preference.IsAutomatic);
    }

    [Fact]
    public void WhenSpecificCookieValueRoundTrips_ThenZoneIdIsPreservedAndTrimmed()
    {
        // Act
        var preference = TimeZonePreference.FromCookieValue("  America/New_York  ");

        // Assert
        Assert.False(preference.IsAutomatic);
        Assert.Equal("America/New_York", preference.ZoneId);
        Assert.Equal("America/New_York", preference.ToCookieValue());
    }
}
