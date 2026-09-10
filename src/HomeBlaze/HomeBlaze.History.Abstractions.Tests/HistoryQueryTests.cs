using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Abstractions.Tests;

public class HistoryQueryTests
{
    private static readonly DateTimeOffset From =
        new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WhenPropertyPathIsBlank_ThenValidationThrows()
    {
        // Arrange
        var query = new HistoryQuery(" ", From, From.AddMinutes(1));

        // Act & Assert
        Assert.Throws<ArgumentException>(query.Validate);
    }

    [Fact]
    public void WhenRangeIsEmptyOrReversed_ThenValidationThrows()
    {
        // Arrange
        var query = new HistoryQuery("/a/Value", From, From);

        // Act & Assert
        Assert.Throws<ArgumentException>(query.Validate);
    }

    [Fact]
    public void WhenBucketIsNotPositive_ThenValidationThrows()
    {
        // Arrange
        var query = new HistoryQuery("/a/Value", From, From.AddMinutes(1), TimeSpan.Zero);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(query.Validate);
    }

    [Fact]
    public void WhenAggregationIsBlank_ThenValidationThrows()
    {
        // Arrange
        var query = new HistoryQuery("/a/Value", From, From.AddMinutes(1), Aggregation: " ");

        // Act & Assert
        Assert.Throws<ArgumentException>(query.Validate);
    }

    [Fact]
    public void WhenMaxPointsIsNotPositive_ThenValidationThrows()
    {
        // Arrange
        var query = new HistoryQuery("/a/Value", From, From.AddMinutes(1), MaxPoints: 0);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(query.Validate);
    }
}
