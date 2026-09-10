using HomeBlaze.History.Abstractions;
using Xunit;

namespace HomeBlaze.History.Mcp.Tests;

public class HistoryToolParsingTests
{
    [Fact]
    public void WhenAggregationOmitted_ThenDefaultsToLast()
    {
        // Arrange & Act
        var result = HistoryToolParsing.NormalizeAggregation(null);

        // Assert
        Assert.Equal(HistoryAggregations.Last, result);
    }

    [Theory]
    [InlineData("last", "Last")]
    [InlineData("TIMEWEIGHTEDAVERAGE", "TimeWeightedAverage")]
    [InlineData("StandardDeviation", "StandardDeviation")]
    public void WhenAggregationVariesByCase_ThenNormalizedToCanonical(string input, string expected)
    {
        // Act
        var result = HistoryToolParsing.NormalizeAggregation(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenAggregationUnknown_ThenReturnsNull()
    {
        // Act
        var result = HistoryToolParsing.NormalizeAggregation("bogus");

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData("5m", 0, 5, 0)]
    [InlineData("30s", 0, 0, 30)]
    [InlineData("1h", 1, 0, 0)]
    [InlineData("00:05:00", 0, 5, 0)]
    public void WhenBucketHasUnitOrTimeSpan_ThenParsed(string input, int hours, int minutes, int seconds)
    {
        // Act
        var result = HistoryToolParsing.ParseBucket(input);

        // Assert
        Assert.Equal(new TimeSpan(hours, minutes, seconds), result);
    }

    [Fact]
    public void WhenBucketOmitted_ThenNull()
    {
        // Act & Assert
        Assert.Null(HistoryToolParsing.ParseBucket(null));
        Assert.Null(HistoryToolParsing.ParseBucket("  "));
    }

    [Theory]
    [InlineData("5x")]   // unknown unit
    [InlineData("5")]    // bare number (would silently be 5 days)
    [InlineData("0s")]   // zero
    [InlineData("-5m")]  // negative
    public void WhenBucketInvalid_ThenThrowsFormat(string input)
    {
        // Act & Assert
        Assert.Throws<FormatException>(() => HistoryToolParsing.ParseBucket(input));
    }

    [Theory]
    [InlineData(typeof(double), "number")]
    [InlineData(typeof(int), "number")]
    [InlineData(typeof(decimal), "number")]
    [InlineData(typeof(long?), "number")]
    [InlineData(typeof(bool), "boolean")]
    [InlineData(typeof(string), "string")]
    [InlineData(typeof(DayOfWeek), "enum")]
    public void WhenDerivingValueType_ThenMapsByType(Type type, string expected)
    {
        // Act
        var result = HistoryToolParsing.ValueType(type);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenBareTimestamp_ThenParsedAsUtc()
    {
        // Act
        var result = HistoryToolParsing.ParseTimestamp("2026-06-24T10:00:00");

        // Assert
        Assert.Equal(TimeSpan.Zero, result.Offset);
        Assert.Equal(new DateTimeOffset(2026, 6, 24, 10, 0, 0, TimeSpan.Zero), result);
    }
}
