using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Abstractions.Tests;

public class BucketAlignmentTests
{
    [Fact]
    public void WhenTimestampIsInsideBucket_ThenFloorsToBucketBoundary()
    {
        // Arrange
        var bucket = TimeSpan.FromMinutes(10);
        var timestamp = new DateTimeOffset(2026, 6, 7, 12, 34, 56, TimeSpan.Zero);

        // Act
        var start = BucketAlignment.BucketStart(timestamp, bucket);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 6, 7, 12, 30, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void WhenTimestampIsOnBoundary_ThenReturnsSameTimestamp()
    {
        // Arrange
        var bucket = TimeSpan.FromHours(1);
        var boundary = new DateTimeOffset(2026, 6, 7, 12, 0, 0, TimeSpan.Zero);

        // Act
        var start = BucketAlignment.BucketStart(boundary, bucket);

        // Assert
        Assert.Equal(boundary, start);
    }

    [Fact]
    public void WhenAnchoredAtEpoch_ThenBucketsAlignToEpoch()
    {
        // Arrange
        var bucket = TimeSpan.FromMinutes(15);
        var timestamp = DateTimeOffset.UnixEpoch.AddMinutes(37);

        // Act
        var start = BucketAlignment.BucketStart(timestamp, bucket);

        // Assert
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(30), start);
    }

    [Fact]
    public void WhenAppliedTwice_ThenIsIdempotent()
    {
        // Arrange
        var bucket = TimeSpan.FromSeconds(30);
        var timestamp = new DateTimeOffset(2026, 6, 7, 12, 34, 56, 789, TimeSpan.Zero);

        // Act
        var once = BucketAlignment.BucketStart(timestamp, bucket);
        var twice = BucketAlignment.BucketStart(once, bucket);

        // Assert
        Assert.Equal(once, twice);
    }

    [Fact]
    public void WhenTimestampPredatesUnixEpoch_ThenFloorsTowardEarlierBoundary()
    {
        // Arrange
        var bucket = TimeSpan.FromSeconds(10);
        var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(-1);

        // Act
        var start = BucketAlignment.BucketStart(timestamp, bucket);

        // Assert
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(-10), start);
    }

    [Fact]
    public void WhenBucketIsNotPositive_ThenThrows()
    {
        // Arrange
        var timestamp = DateTimeOffset.UnixEpoch;

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BucketAlignment.BucketStart(timestamp, TimeSpan.Zero));
    }

    [Fact]
    public void WhenRangeContainsMoreThanMaximumBuckets_ThenFirstBucketSkipsOldest()
    {
        // Arrange
        var bucket = TimeSpan.FromSeconds(10);
        var from = DateTimeOffset.UnixEpoch.AddSeconds(1);
        var to = DateTimeOffset.UnixEpoch.AddSeconds(51);

        // Act
        var start = BucketAlignment.FirstBucketStart(from, to, bucket, maxBuckets: 2);

        // Assert
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(40), start);
    }
}
