using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Host.Services.Display;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using HomeBlaze.Components.Abstractions.TimeZones;

namespace HomeBlaze.Host.Services.Tests.Display;

public class StateUnitExtensionsTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);
    }

    [Theory]
    [InlineData(StateUnit.Watt, 500, "500 W")]
    [InlineData(StateUnit.Watt, 1500, "1.5 kW")]
    [InlineData(StateUnit.Watt, 1000, "1 kW")]
    [InlineData(StateUnit.Watt, 1234, "1.23 kW")]
    [InlineData(StateUnit.Kilowatt, 0.5, "500 W")]
    [InlineData(StateUnit.Kilowatt, 0.001, "1 W")]
    [InlineData(StateUnit.Kilowatt, 5, "5 kW")]
    [InlineData(StateUnit.WattHour, 10500, "10.5 kWh")]
    [InlineData(StateUnit.WattHour, 500, "500 Wh")]
    [InlineData(StateUnit.KilowattHour, 0.8, "800 Wh")]
    [InlineData(StateUnit.KilowattHour, 5, "5 kWh")]
    [InlineData(StateUnit.Meter, 1500, "1.5 km")]
    [InlineData(StateUnit.Meter, 0.5, "500 mm")]
    [InlineData(StateUnit.Meter, 50, "50 m")]
    [InlineData(StateUnit.Millimeter, 1500, "1.5 m")]
    [InlineData(StateUnit.Millimeter, 500, "500 mm")]
    [InlineData(StateUnit.Kilometer, 0.3, "300 m")]
    [InlineData(StateUnit.Kilometer, 5, "5 km")]
    [InlineData(StateUnit.Ampere, 5, "5 A")]
    [InlineData(StateUnit.Ampere, 0.5, "500 mA")]
    [InlineData(StateUnit.Milliampere, 1500, "1.5 A")]
    [InlineData(StateUnit.Milliampere, 500, "500 mA")]
    [InlineData(StateUnit.Byte, 500, "500 B")]
    [InlineData(StateUnit.Byte, 1500, "1.5 kB")]
    [InlineData(StateUnit.Byte, 1500000, "1.5 MB")]
    [InlineData(StateUnit.Byte, 1500000000, "1.5 GB")]
    [InlineData(StateUnit.Kilobyte, 1500, "1.5 MB")]
    [InlineData(StateUnit.Kilobyte, 500, "500 kB")]
    [InlineData(StateUnit.KilobytePerSecond, 1500, "1.5 MB/s")]
    [InlineData(StateUnit.KilobytePerSecond, 500, "500 kB/s")]
    [InlineData(StateUnit.Volt, 230, "230 V")]
    [InlineData(StateUnit.DegreeCelsius, 23.5, "23.5°C")]
    public void WhenFormatWithUnit_ThenAutoScalesCorrectly(StateUnit unit, double value, string expected)
    {
        // Act
        var result = StateUnitExtensions.FormatWithUnit(Convert.ToDecimal(value), unit);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0.756, "75.6%")]
    [InlineData(0.75, "75%")]
    [InlineData(0.0, "0%")]
    [InlineData(1.0, "100%")]
    [InlineData(0.33333, "33.3%")]
    public void WhenDisplayValueIsPercent_ThenFormatsWithOneDecimalPlace(double value, string expected)
    {
        // Arrange
        var context = CreateContext();
        var subject = new DisplayTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(DisplayTestSubject.Ratio))!;

        // Act
        var result = property.GetPropertyDisplayValue(value);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(3.14159, "3.14")]
    [InlineData(5.0, "5")]
    [InlineData(0.1, "0.1")]
    [InlineData(100.0, "100")]
    [InlineData(1.005, "1.01")]
    public void WhenDisplayValueIsUnitlessDouble_ThenFormatsTwoDecimalPlaces(double value, string expected)
    {
        // Arrange
        var context = CreateContext();
        var subject = new DisplayTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(DisplayTestSubject.Rate))!;

        // Act
        var result = property.GetPropertyDisplayValue(value);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(1.5, "1500 ms")]
    [InlineData(0.1234, "123.4 ms")]
    [InlineData(60.0, "00:01:00")]
    [InlineData(3600.0, "1 h")]
    public void WhenDisplayValueIsTimeSpan_ThenFormatsCorrectly(double totalSeconds, string expected)
    {
        // Arrange
        var context = CreateContext();
        var subject = new DisplayTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(DisplayTestSubject.Rate))!;
        var value = TimeSpan.FromSeconds(totalSeconds);

        // Act
        var result = property.GetPropertyDisplayValue(value);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenDateTimeOffsetAndTimeZoneResolved_ThenFormatsInThatZone()
    {
        // Arrange
        var context = CreateContext();
        var subject = new DisplayTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(DisplayTestSubject.Rate))!;
        var timeZone = new TimeZoneDisplayService();
        timeZone.SetResolved(
            TimeZonePreference.Specific("Test+2"),
            TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test +2", "Test +2"));
        var value = new DateTimeOffset(2026, 6, 26, 10, 0, 0, TimeSpan.Zero);

        // Act
        var result = property.GetPropertyDisplayValue(value, timeZone);

        // Assert
        Assert.Contains("+02:00", result);
    }

    [Fact]
    public void WhenDateTimeOffsetAndTimeZoneUnresolved_ThenShowsPlaceholder()
    {
        // Arrange
        var context = CreateContext();
        var subject = new DisplayTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(DisplayTestSubject.Rate))!;
        var timeZone = new TimeZoneDisplayService();
        var value = new DateTimeOffset(2026, 6, 26, 10, 0, 0, TimeSpan.Zero);

        // Act
        var result = property.GetPropertyDisplayValue(value, timeZone);

        // Assert
        Assert.Equal(timeZone.Placeholder, result);
    }
}

[InterceptorSubject]
public partial class DisplayTestSubject
{
    [State(Unit = StateUnit.Percent)]
    public partial double Ratio { get; set; }

    [State]
    public partial double Rate { get; set; }
}
