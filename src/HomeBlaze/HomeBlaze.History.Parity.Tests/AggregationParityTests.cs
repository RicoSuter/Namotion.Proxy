using HomeBlaze.History.Abstractions;
using Xunit;

namespace HomeBlaze.History.Parity.Tests;

public class AggregationParityTests
{
    private static readonly DateTimeOffset Base = ParityClock.Base;
    private static readonly TimeSpan Bucket = TimeSpan.FromSeconds(10);

    private static async Task<IParityStore> WithSamplesAsync(ParityStoreFactory factory)
    {
        var store = factory.Create();
        store.Record("/a/Value", Base.AddSeconds(1), 10d, typeof(double));
        store.Record("/a/Value", Base.AddSeconds(3), 20d, typeof(double));
        store.Record("/a/Value", Base.AddSeconds(5), 30d, typeof(double));
        await store.FlushAsync();
        return store;
    }

    private static HistoryQuery Bucketed(string aggregation) =>
        new("/a/Value", Base.AddSeconds(0), Base.AddSeconds(10), Bucket, aggregation, MaxPoints: 1000);

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenFirst_ThenTenAcrossStores(ParityStoreFactory factory)
    {
        // Arrange
        using var store = await WithSamplesAsync(factory);

        // Act
        var point = store.Query(Bucketed(HistoryAggregations.First)).Points.Single();

        // Assert
        Assert.Equal(10d, point.Number!.Value, 6);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenLast_ThenThirtyAcrossStores(ParityStoreFactory factory)
    {
        // Arrange
        using var store = await WithSamplesAsync(factory);

        // Act
        var point = store.Query(Bucketed(HistoryAggregations.Last)).Points.Single();

        // Assert
        Assert.Equal(30d, point.Number!.Value, 6);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenMinimum_ThenTenAcrossStores(ParityStoreFactory factory)
    {
        // Arrange
        using var store = await WithSamplesAsync(factory);

        // Act
        var point = store.Query(Bucketed(HistoryAggregations.Minimum)).Points.Single();

        // Assert
        Assert.Equal(10d, point.Number!.Value, 6);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenMaximum_ThenThirtyAcrossStores(ParityStoreFactory factory)
    {
        // Arrange
        using var store = await WithSamplesAsync(factory);

        // Act
        var point = store.Query(Bucketed(HistoryAggregations.Maximum)).Points.Single();

        // Assert
        Assert.Equal(30d, point.Number!.Value, 6);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenSum_ThenSixtyAcrossStores(ParityStoreFactory factory)
    {
        // Arrange
        using var store = await WithSamplesAsync(factory);

        // Act
        var point = store.Query(Bucketed(HistoryAggregations.Sum)).Points.Single();

        // Assert
        Assert.Equal(60d, point.Number!.Value, 6);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenSampleAverage_ThenTwentyAcrossStores(ParityStoreFactory factory)
    {
        // Arrange
        using var store = await WithSamplesAsync(factory);

        // Act
        var point = store.Query(Bucketed(HistoryAggregations.SampleAverage)).Points.Single();

        // Assert
        Assert.Equal(20d, point.Number!.Value, 6);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenCount_ThenThreeAcrossStores(ParityStoreFactory factory)
    {
        // Arrange
        using var store = await WithSamplesAsync(factory);

        // Act
        var point = store.Query(Bucketed(HistoryAggregations.Count)).Points.Single();

        // Assert
        Assert.Equal(3d, point.Number!.Value, 6);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenStandardDeviation_ThenSampleStdDevAcrossStores(ParityStoreFactory factory)
    {
        // Arrange - sample stddev of {10,20,30} = sqrt(((10-20)^2+(20-20)^2+(30-20)^2)/(3-1)) = sqrt(200/2) = 10.
        using var store = await WithSamplesAsync(factory);

        // Act
        var point = store.Query(Bucketed(HistoryAggregations.StandardDeviation)).Points.Single();

        // Assert
        Assert.Equal(10d, point.Number!.Value, 6);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenCountOnEmptyBucket_ThenZeroAcrossStores(ParityStoreFactory factory)
    {
        // Arrange - samples only in bucket0; bucket1 [10,20) empty. Count of empty bucket is 0 (a fact, not a gap).
        using var store = await WithSamplesAsync(factory);

        // Act
        var series = store.Query(new HistoryQuery(
            "/a/Value", Base.AddSeconds(0), Base.AddSeconds(20), Bucket, HistoryAggregations.Count, MaxPoints: 1000));

        // Assert
        ParityAssert.NumbersEqual(new double?[] { 3d, 0d }, series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenSamplesPredateUnixEpoch_ThenBucketsFloorAcrossStores(ParityStoreFactory factory)
    {
        // Arrange
        using var store = factory.Create();
        var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(-1);
        store.Record("/a/Value", timestamp, 42d, typeof(double));
        await store.FlushAsync();

        // Act
        var series = store.Query(new HistoryQuery(
            "/a/Value",
            DateTimeOffset.UnixEpoch.AddSeconds(-10),
            DateTimeOffset.UnixEpoch,
            Bucket,
            HistoryAggregations.Last,
            MaxPoints: 10));

        // Assert
        var point = Assert.Single(series.Points);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(-10), point.Timestamp);
        Assert.Equal(42d, point.Number);
    }
}
