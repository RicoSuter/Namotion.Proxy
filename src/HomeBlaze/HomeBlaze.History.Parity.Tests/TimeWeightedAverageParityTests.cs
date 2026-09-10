using HomeBlaze.History.Abstractions;
using Xunit;

namespace HomeBlaze.History.Parity.Tests;

public class TimeWeightedAverageParityTests
{
    private static readonly DateTimeOffset Base = ParityClock.Base;
    private static readonly TimeSpan Bucket = TimeSpan.FromSeconds(10);

    private static HistoryQuery Twa(int fromSecond, int toSecond, HistoryPoint? carrySeed = null) =>
        new("/a/Value", Base.AddSeconds(fromSecond), Base.AddSeconds(toSecond), Bucket,
            HistoryAggregations.TimeWeightedAverage, MaxPoints: 1000, CarrySeed: carrySeed);

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenStepValueHeldHalfThenOther_ThenDurationWeightedMeanIs15(ParityStoreFactory factory)
    {
        // Arrange - bucket [0,10): 10 holds [0,5), 20 holds [5,10): TWA = (10*5 + 20*5)/10 = 15.
        using var store = factory.Create();
        store.Record("/a/Value", Base.AddSeconds(0), 10d, typeof(double));
        store.Record("/a/Value", Base.AddSeconds(5), 20d, typeof(double));
        await store.FlushAsync();

        // Act
        var point = store.Query(Twa(0, 10)).Points.Single();

        // Assert
        Assert.Equal(15d, point.Number!.Value, 6);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenCarrySeedHoldsAcrossWholeBucket_ThenEqualsSeedValue(ParityStoreFactory factory)
    {
        // Arrange - no samples; the merger-supplied carry seed (value 7) holds the whole bucket.
        using var store = factory.Create();
        await store.FlushAsync();
        var seed = new HistoryPoint(Base.AddSeconds(-3), 7d, null);

        // Act
        var point = store.Query(Twa(0, 10, seed)).Points.Single();

        // Assert
        Assert.Equal(7d, point.Number!.Value, 6);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenStoreOwnLookBackHoldsValue_ThenEmptyBucketUsesIt(ParityStoreFactory factory)
    {
        // Arrange - a sample at t=-5 (value 4) before From; bucket [0,10) empty; the store's own
        // look-back seeds the carry, so the empty bucket reads 4 (no merger CarrySeed).
        using var store = factory.Create();
        store.Record("/a/Value", Base.AddSeconds(-5), 4d, typeof(double));
        await store.FlushAsync();

        // Act
        var point = store.Query(Twa(0, 10)).Points.Single();

        // Assert
        Assert.Equal(4d, point.Number!.Value, 6);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenLastHasNoSeedAndTheHeldValuePredatesFrom_ThenBothStoresLookBack(ParityStoreFactory factory)
    {
        // Arrange - the Last twin of WhenStoreOwnLookBackHoldsValue_ThenEmptyBucketUsesIt. Last carries
        // forward exactly like TimeWeightedAverage, so a store asked directly (no merger CarrySeed) must
        // fall back to its own held value rather than reporting the bucket as empty. One engine did and
        // the other did not, so the same query answered differently depending on which store served it.
        using var store = factory.Create();
        store.Record("/a/Value", Base.AddSeconds(-5), 4d, typeof(double));
        await store.FlushAsync();

        // Act
        var point = store.Query(new HistoryQuery(
            "/a/Value", Base, Base.AddSeconds(10), TimeSpan.FromSeconds(10),
            HistoryAggregations.Last, MaxPoints: 1000)).Points.Single();

        // Assert
        Assert.Equal(4d, point.Number!.Value, 6);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenTwoBuckets_ThenHeldValueThreadsAcross(ParityStoreFactory factory)
    {
        // Arrange - one sample (value 6) at t=2 in bucket0 [0,10); bucket1 [10,20) empty carries 6.
        using var store = factory.Create();
        store.Record("/a/Value", Base.AddSeconds(2), 6d, typeof(double));
        await store.FlushAsync();

        // Act
        var series = store.Query(Twa(0, 20));

        // Assert
        ParityAssert.NumbersEqual(new double?[] { 6d, 6d }, series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenBucketEmptyAndNoCarry_ThenNull(ParityStoreFactory factory)
    {
        // Arrange - nothing recorded, no carry: the bucket is a genuine null (gap), not zero.
        using var store = factory.Create();
        await store.FlushAsync();

        // Act
        var point = store.Query(Twa(0, 10)).Points.Single();

        // Assert
        Assert.Null(point.Number);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenExplicitNullRecorded_ThenHeldValueIsCleared(ParityStoreFactory factory)
    {
        // Arrange - 10 is known for the first half of bucket 0, then explicit null clears it.
        // Bucket 1 has no event and must remain a gap rather than carrying 10.
        using var store = factory.Create();
        store.Record("/a/Value", Base, 10d, typeof(double?));
        store.Record("/a/Value", Base.AddSeconds(5), null, typeof(double?));
        await store.FlushAsync();

        // Act
        var series = store.Query(Twa(0, 20));

        // Assert
        ParityAssert.NumbersEqual(new double?[] { 10d, null }, series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenExplicitNullCarrySeedProvided_ThenStoreLookBackDoesNotRestoreOlderValue(
        ParityStoreFactory factory)
    {
        // Arrange
        using var store = factory.Create();
        store.Record("/a/Value", Base.AddSeconds(-5), 7d, typeof(double?));
        await store.FlushAsync();
        var explicitNullSeed = new HistoryPoint(Base.AddSeconds(-1), null, null);

        // Act
        var point = store.Query(Twa(0, 10, explicitNullSeed)).Points.Single();

        // Assert
        Assert.Null(point.Number);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenBucketStraddlesPartitionBoundary_ThenTwaMatchesAcrossStores(ParityStoreFactory factory)
    {
        // Arrange - a 3-day bucket whose interior contains the SQLite weekly partition split (Monday).
        // InMemory has a single buffer (no split); SQLite must sum weighted_sum/total_duration across two
        // partition files. Both must agree: 10 over [Sun,Mon)=1d, 30 over [Mon,Wed)=2d -> (10*1+30*2)/3 = 70/3.
        var bucket = TimeSpan.FromDays(3);
        var from = new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero); // Sunday 00:00 == bucketStart
        var to = from.AddDays(3);                                          // Wednesday 00:00 == bucketEnd

        using var store = factory.Create();
        store.Record("/a/Value", from, 10d, typeof(double));            // value 10 at bucketStart (week 06-15)
        store.Record("/a/Value", from.AddDays(1), 30d, typeof(double)); // value 30 at Monday 00:00 (week 06-22)
        await store.FlushAsync();

        // Act
        var point = store.Query(new HistoryQuery("/a/Value", from, to, bucket,
            HistoryAggregations.TimeWeightedAverage, MaxPoints: 1000)).Points.Single();

        // Assert
        Assert.Equal(70d / 3d, point.Number!.Value, 6);
    }
}
