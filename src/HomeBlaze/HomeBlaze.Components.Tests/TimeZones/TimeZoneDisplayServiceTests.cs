using Xunit;
using HomeBlaze.Components.Abstractions.TimeZones;

namespace HomeBlaze.Components.Tests.TimeZones;

public class TimeZoneDisplayServiceTests
{
    private static TimeZoneInfo PlusTwo =>
        TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test +2", "Test +2");

    [Fact]
    public void WhenUnresolved_ThenFormatReturnsPlaceholder()
    {
        // Arrange
        var service = new TimeZoneDisplayService();

        // Act & Assert
        Assert.False(service.IsResolved);
        Assert.Equal(service.Placeholder, service.Format(DateTimeOffset.UtcNow));
        Assert.Equal(service.Placeholder, service.Format(DateTime.UtcNow));
    }

    [Fact]
    public void WhenResolved_ThenFormatConvertsToZoneWithOffset()
    {
        // Arrange
        var service = new TimeZoneDisplayService();
        service.SetResolved(TimeZonePreference.Specific("Test+2"), PlusTwo);

        // Act
        var result = service.Format(new DateTimeOffset(2026, 6, 26, 10, 0, 0, TimeSpan.Zero));

        // Assert: the offset is formatted invariantly, so ':' holds under any current culture. The
        // 12:00 wall-clock value is verified by WhenResolved_ThenToZonedReturnsWallClockInZone.
        Assert.Contains("+02:00", result);
    }

    /// <summary>
    /// A zone with real transitions, unlike the fixed-offset fixture the other tests use. Skipped when
    /// the host has no tz database entry for it rather than failing for an unrelated reason.
    /// </summary>
    private static TimeZoneInfo? TryGetZurich()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
    }

    [Fact]
    public void WhenWallClockTimeNeverHappened_ThenToUtcResolvesForwardInsteadOfLosingAnHour()
    {
        // Arrange: 2026-03-29 02:30 does not exist in Zurich; the clock jumps 02:00 to 03:00.
        if (TryGetZurich() is not { } zurich)
        {
            return;
        }

        var service = new TimeZoneDisplayService();
        service.SetResolved(TimeZonePreference.Specific("Europe/Zurich"), zurich);

        // Act
        var result = service.ToUtc(new DateTime(2026, 3, 29, 2, 30, 0));

        // Assert: resolved through the pre-transition (standard) offset, so the instant stays inside
        // the day the user picked instead of silently sliding by an hour.
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void WhenWallClockTimeHappenedTwice_ThenToUtcTakesTheEarlierOccurrence()
    {
        // Arrange: 2026-10-25 02:30 occurs twice in Zurich, at +02:00 and again at +01:00.
        if (TryGetZurich() is not { } zurich)
        {
            return;
        }

        var service = new TimeZoneDisplayService();
        service.SetResolved(TimeZonePreference.Specific("Europe/Zurich"), zurich);

        // Act
        var result = service.ToUtc(new DateTime(2026, 10, 25, 2, 30, 0));

        // Assert: the earlier one, so a range built from wall-clock dates covers the whole
        // ambiguous hour rather than only its second half.
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void WhenDateTimeIsLocalOrUnspecified_ThenFormatStillConvertsToTheViewerZone()
    {
        // Arrange
        var service = new TimeZoneDisplayService();
        service.SetResolved(TimeZonePreference.Specific("Test+2"), PlusTwo);
        var instant = new DateTime(2026, 6, 26, 10, 0, 0, DateTimeKind.Utc);

        // Act
        var fromUtc = service.Format(instant);
        var fromUnspecified = service.Format(DateTime.SpecifyKind(instant, DateTimeKind.Unspecified));

        // Assert: an Unspecified timestamp is treated as UTC, matching the storage invariant, so the
        // same value cannot render differently depending on which provider produced it.
        Assert.Equal(fromUtc, fromUnspecified);
    }

    [Fact]
    public void WhenResolved_ThenToZonedReturnsWallClockInZone()
    {
        // Arrange
        var service = new TimeZoneDisplayService();
        service.SetResolved(TimeZonePreference.Specific("Test+2"), PlusTwo);

        // Act
        var zoned = service.ToZoned(new DateTimeOffset(2026, 6, 26, 10, 0, 0, TimeSpan.Zero));

        // Assert
        Assert.Equal(new DateTime(2026, 6, 26, 12, 0, 0), zoned);
    }

    [Fact]
    public void WhenResolved_ThenToUtcInterpretsInputInZone()
    {
        // Arrange
        var service = new TimeZoneDisplayService();
        service.SetResolved(TimeZonePreference.Specific("Test+2"), PlusTwo);

        // Act
        var utc = service.ToUtc(new DateTime(2026, 6, 26, 12, 0, 0));

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 6, 26, 10, 0, 0, TimeSpan.Zero), utc);
    }

    [Fact]
    public void WhenResolved_ThenChangedEventFires()
    {
        // Arrange
        var service = new TimeZoneDisplayService();
        var raised = 0;
        service.Changed += () => raised++;

        // Act
        service.SetResolved(TimeZonePreference.Specific("Test+2"), PlusTwo);

        // Assert
        Assert.Equal(1, raised);
    }
}
