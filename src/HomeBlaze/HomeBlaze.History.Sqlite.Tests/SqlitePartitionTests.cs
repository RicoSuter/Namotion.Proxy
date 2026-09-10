using System.Globalization;
using HomeBlaze.History.Sqlite;

namespace HomeBlaze.History.Sqlite.Tests;

public sealed class SqlitePartitionTests
{
    [Fact]
    public void WhenWeeklyKey_ThenAnchorsOnMonday()
    {
        // Arrange - Wednesday 2026-06-24 (the ISO week starts Monday 2026-06-22)
        var wednesday = new DateTimeOffset(2026, 6, 24, 9, 30, 0, TimeSpan.Zero);

        // Act
        var key = SqlitePartition.PartitionKey(wednesday, PartitionInterval.Weekly);

        // Assert
        Assert.Equal("2026-06-22", key);
    }

    [Fact]
    public void WhenWeeklyKeyOnSunday_ThenAnchorsOnPrecedingMonday()
    {
        // Arrange - Sunday 2026-06-28 still belongs to the week starting Monday 2026-06-22
        var sunday = new DateTimeOffset(2026, 6, 28, 23, 59, 0, TimeSpan.Zero);

        // Act
        var key = SqlitePartition.PartitionKey(sunday, PartitionInterval.Weekly);

        // Assert
        Assert.Equal("2026-06-22", key);
    }

    [Fact]
    public void WhenMonthlyKey_ThenAnchorsOnMonthStart()
    {
        // Arrange
        var timestamp = new DateTimeOffset(2026, 6, 24, 14, 0, 0, TimeSpan.Zero);

        // Act
        var key = SqlitePartition.PartitionKey(timestamp, PartitionInterval.Monthly);

        // Assert
        Assert.Equal("2026-06", key);
    }

    [Fact]
    public void WhenKeyIsAMonday_ThenInferredRangeSpansTheWeek()
    {
        // Arrange - a Monday could have been written by either Daily or Weekly, so the wider reading wins
        const string key = "2026-06-22";

        // Act
        var (start, end) = SqlitePartition.InferredRange(key);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 6, 29, 0, 0, 0, TimeSpan.Zero), end);
    }

    [Fact]
    public void WhenKeyIsNotAMonday_ThenInferredRangeSpansOneDay()
    {
        // Arrange - only Daily produces a non-Monday key, so there is nothing to be conservative about
        const string key = "2026-06-24";

        // Act
        var (start, end) = SqlitePartition.InferredRange(key);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 6, 24, 0, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 6, 25, 0, 0, 0, TimeSpan.Zero), end);
    }

    [Fact]
    public void WhenKeyIsAMonth_ThenInferredRangeSpansTheMonth()
    {
        // Arrange
        const string key = "2026-06";

        // Act
        var (start, end) = SqlitePartition.InferredRange(key);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), end);
    }

    [Theory]
    [InlineData("2026-06-22")]
    [InlineData("2026-06-24")]
    [InlineData("2026-06")]
    public void WhenKeyWasWrittenByAnyInterval_ThenItIsRecognised(string key)
    {
        // Act & Assert - reconfiguring the interval must not make existing partition files invisible
        Assert.True(SqlitePartition.IsPartitionKey(key));
    }

    [Theory]
    [InlineData("metadata")] // the metadata database shares the directory and must never enumerate as a partition
    [InlineData("2026")]
    [InlineData("2026-13")]
    public void WhenKeyIsNotAPartition_ThenItIsRejected(string key)
    {
        // Act & Assert
        Assert.False(SqlitePartition.IsPartitionKey(key));
    }

    [Theory]
    [InlineData("th-TH")]  // Buddhist calendar: year 2026 formats as 2569
    [InlineData("ar-SA")]  // Umm al-Qura calendar: a different year, month and day
    public void WhenTheCultureUsesANonGregorianCalendar_ThenKeysStillRoundTrip(string cultureName)
    {
        // Arrange - keys are written by one culture and parsed by another (a background thread, a
        // different request). A key that does not round-trip is silent and total: queries return
        // nothing while coverage still claims the range, and the sweep either spares every file or
        // deletes all of them.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            var timestamp = new DateTimeOffset(2026, 6, 24, 14, 0, 0, TimeSpan.Zero);

            foreach (var interval in Enum.GetValues<PartitionInterval>())
            {
                // Act
                var key = SqlitePartition.PartitionKey(timestamp, interval);

                // Assert
                Assert.True(SqlitePartition.IsPartitionKey(key), $"{interval} key '{key}' unrecognised");
                var (start, end) = SqlitePartition.InferredRange(key);
                Assert.True(start <= timestamp && timestamp < end,
                    $"{interval} key '{key}' covers [{start:o}, {end:o}), which excludes its own timestamp");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(PartitionInterval.Daily)]
    [InlineData(PartitionInterval.Weekly)]
    [InlineData(PartitionInterval.Monthly)]
    public void WhenATimestampIsAtTheEndOfTime_ThenItsKeyIsStillUsable(PartitionInterval interval)
    {
        // Arrange - a device reporting an uninitialised DateTime.MaxValue is reachable (OPC UA source
        // timestamps come off the wire). An unguarded key range threw out of IsPartitionKey, which
        // poisoned every read and the sweep that would have removed the file.
        var key = SqlitePartition.PartitionKey(DateTimeOffset.MaxValue, interval);

        // Act & Assert
        Assert.True(SqlitePartition.IsPartitionKey(key));
        var (start, end) = SqlitePartition.InferredRange(key);
        Assert.True(start < end);
    }
}
