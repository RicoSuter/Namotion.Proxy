using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Sqlite;

namespace HomeBlaze.History.Sqlite.Tests;

public sealed class SqliteHistoryStoreCoreBucketedTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Bucket = TimeSpan.FromSeconds(10);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-" + Guid.NewGuid().ToString("N"));

    // Start before the test samples, then expose a far-future now so the complete query interval
    // is covered while nothing is swept.
    private SqliteHistoryStore NewCore()
    {
        var now = Base.AddHours(-1);
        var store = new SqliteHistoryStore(
            priority: 50,
            databaseDirectory: _directory,
            PartitionInterval.Weekly,
            TimeSpan.FromHours(2),
            maxJsonSize: 8192,
            getUtcNow: () => now);
        now = Base.AddHours(1);
        return store;
    }

    private static HistoryQuery BucketedQuery(string aggregation, int fromSecond, int toSecond,
        HistoryPoint? carrySeed = null) =>
        new("/a/Value", Base.AddSeconds(fromSecond), Base.AddSeconds(toSecond), Bucket, aggregation,
            MaxPoints: 1000, CarrySeed: carrySeed);

    private async Task<SqliteHistoryStore> WithDoublesAsync(params (int second, double value)[] samples)
    {
        var core = NewCore();
        foreach (var (second, value) in samples)
        {
            core.Record("/a/Value", Base.AddSeconds(second), value, typeof(double));
        }

        await core.FlushAsync(CancellationToken.None);
        return core;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { /* best effort temp cleanup */ }
    }

    [Fact]
    public async Task WhenDecimalRecorded_ThenNumericAggregationIsSupported()
    {
        // Arrange - decimals must support numeric aggregation (not be refused like a Json string/enum).
        using var core = NewCore();
        core.Record("/a/Value", Base.AddSeconds(1), 1.5m, typeof(decimal));
        core.Record("/a/Value", Base.AddSeconds(2), 3.5m, typeof(decimal));
        core.Record("/a/Value", Base.AddSeconds(3), 2.5m, typeof(decimal));
        await core.FlushAsync(CancellationToken.None);

        // Act - single [0,10) bucket, Maximum
        var point = core.Query(BucketedQuery(HistoryAggregations.Maximum, 0, 10)).Points.Single();

        // Assert
        Assert.Equal(3.5, point.Number);
    }

    [Fact]
    public async Task WhenCount_ThenEmptyBucketIsZero()
    {
        // Arrange - samples only in the first 10s bucket; second bucket empty
        using var core = await WithDoublesAsync((1, 5), (2, 6), (3, 7));

        // Act - two buckets: [0,10), [10,20)
        var series = core.Query(BucketedQuery(HistoryAggregations.Count, 0, 20));

        // Assert
        Assert.Equal(new double?[] { 3, 0 }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenLast_ThenEmptyBucketCarriesHeldValue()
    {
        // Arrange - bucket0 has samples (last = 7), bucket1 empty
        using var core = await WithDoublesAsync((1, 5), (3, 7));

        // Act
        var series = core.Query(BucketedQuery(HistoryAggregations.Last, 0, 20));

        // Assert - bucket0 -> 7, bucket1 empty -> carried 7
        Assert.Equal(new double?[] { 7, 7 }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenLastWithCarrySeed_ThenLeadingEmptyBucketUsesSeed()
    {
        // Arrange - no samples in bucket0; seed says value entering From is 3
        using var core = await WithDoublesAsync((12, 9)); // sample only in bucket1
        var seed = new HistoryPoint(Base.AddSeconds(-5), 3d, null);

        // Act
        var series = core.Query(BucketedQuery(HistoryAggregations.Last, 0, 20, carrySeed: seed));

        // Assert - bucket0 empty -> seed 3; bucket1 -> 9
        Assert.Equal(new double?[] { 3, 9 }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenFirst_ThenEmptyBucketIsNull()
    {
        // Arrange
        using var core = await WithDoublesAsync((1, 5), (3, 7));

        // Act
        var series = core.Query(BucketedQuery(HistoryAggregations.First, 0, 20));

        // Assert - bucket0 first = 5; bucket1 empty = null
        Assert.Equal(new double?[] { 5, null }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenSampleAverage_ThenCountWeightedMeanPerBucket()
    {
        // Arrange
        using var core = await WithDoublesAsync((1, 10), (2, 20), (3, 30));

        // Act
        var series = core.Query(BucketedQuery(HistoryAggregations.SampleAverage, 0, 10));

        // Assert
        Assert.Equal(20d, series.Points.Single().Number);
    }

    [Fact]
    public async Task WhenMinimumMaximumSum_ThenComputedPerBucket()
    {
        // Arrange
        using var core = await WithDoublesAsync((1, 10), (2, 30), (3, 20));

        // Act
        var minimum = core.Query(BucketedQuery(HistoryAggregations.Minimum, 0, 10)).Points.Single().Number;
        var maximum = core.Query(BucketedQuery(HistoryAggregations.Maximum, 0, 10)).Points.Single().Number;
        var sum = core.Query(BucketedQuery(HistoryAggregations.Sum, 0, 10)).Points.Single().Number;

        // Assert
        Assert.Equal(10d, minimum);
        Assert.Equal(30d, maximum);
        Assert.Equal(60d, sum);
    }

    [Fact]
    public async Task WhenStdDev_ThenSampleStandardDeviationAndNullForSingle()
    {
        // Arrange - bucket0 has {2,4,4,4,5,5,7,9} (sample stddev = 2.138...); bucket1 has a single sample
        using var core = await WithDoublesAsync(
            (1, 2), (2, 4), (3, 4), (4, 4), (5, 5), (6, 5), (7, 7), (8, 9), (12, 100));

        // Act
        var series = core.Query(BucketedQuery(HistoryAggregations.StandardDeviation, 0, 20));

        // Assert
        Assert.NotNull(series.Points[0].Number);
        Assert.Equal(2.138, series.Points[0].Number!.Value, 3);
        Assert.Null(series.Points[1].Number); // single sample -> undefined sample stddev
    }

    [Fact]
    public async Task WhenMoreBucketsThanBudget_ThenNewestBucketsKeptAndTruncated()
    {
        // Arrange - 5 buckets worth of samples
        using var core = await WithDoublesAsync((1, 1), (11, 2), (21, 3), (31, 4), (41, 5));

        // Act - budget 2 -> newest two buckets ([30,40),[40,50))
        var series = core.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(50), Bucket,
            HistoryAggregations.Last, MaxPoints: 2));

        // Assert
        Assert.True(series.Truncated);
        Assert.Equal(new double?[] { 4, 5 }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenLastClipsOlderBuckets_ThenHeldValueSeedsNewestEmptyBuckets()
    {
        // Arrange
        using var core = await WithDoublesAsync((1, 7));

        // Act
        var series = core.Query(new HistoryQuery(
            "/a/Value",
            Base,
            Base.AddSeconds(50),
            Bucket,
            HistoryAggregations.Last,
            MaxPoints: 2));

        // Assert
        Assert.True(series.Truncated);
        Assert.Equal(new double?[] { 7, 7 }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenNumericAggregationOnStringProperty_ThenThrows()
    {
        // Arrange
        using var core = NewCore();
        core.Record("/a/Name", Base.AddSeconds(1), "x", typeof(string));
        await core.FlushAsync(CancellationToken.None);

        // Act & Assert
        var exception = Assert.Throws<HistoryAggregationNotSupportedException>(() =>
            core.Query(new HistoryQuery("/a/Name", Base, Base.AddSeconds(10), Bucket, HistoryAggregations.SampleAverage)));
        Assert.Contains(HistoryAggregations.Last, exception.Available);
    }

    [Fact]
    public async Task WhenLastOnStringProperty_ThenReturnsJson()
    {
        // Arrange
        using var core = NewCore();
        core.Record("/a/Name", Base.AddSeconds(1), "open", typeof(string));
        await core.FlushAsync(CancellationToken.None);

        // Act
        var point = core.Query(new HistoryQuery("/a/Name", Base, Base.AddSeconds(10), Bucket, HistoryAggregations.Last))
            .Points.Single();

        // Assert
        Assert.Equal("open", point.Json!.Value.GetString());
    }
}
