using HomeBlaze.History.Abstractions;
using HomeBlaze.History.InMemory;

namespace HomeBlaze.History.InMemory.Tests;

public class InMemoryHistoryStoreCoreTimeWeightedAverageTests
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Bucket = TimeSpan.FromSeconds(10);

    private static InMemoryHistoryStore NewCore()
    {
        var now = Base.AddMinutes(-1);
        var store = new InMemoryHistoryStore(
            priority: 100,
            maxPointsPerProperty: 1000,
            maxAge: TimeSpan.FromHours(2),
            maxJsonSize: 8192,
            getUtcNow: () => now);
        now = Base.AddHours(1);
        return store;
    }

    private static HistoryQuery Twa(int fromSecond, int toSecond, HistoryPoint? carrySeed = null) =>
        new("/a/Value", Base.AddSeconds(fromSecond), Base.AddSeconds(toSecond), Bucket,
            HistoryAggregations.TimeWeightedAverage, MaxPoints: 1000, CarrySeed: carrySeed);

    [Fact]
    public void WhenStepValueHeldHalfThenOther_ThenDurationWeightedMean()
    {
        // Arrange - bucket [0,10): value 10 from t=0 (sample at 0), value 20 from t=5.
        // Held entering bucket via the sample at t=0. 10 holds [0,5), 20 holds [5,10): TWA = (10*5 + 20*5)/10 = 15.
        var core = NewCore();
        core.Record("/a/Value", Base.AddSeconds(0), 10d, typeof(double));
        core.Record("/a/Value", Base.AddSeconds(5), 20d, typeof(double));

        // Act
        var point = core.Query(Twa(0, 10)).Points.Single();

        // Assert
        Assert.Equal(15d, point.Number!.Value, 6);
    }

    [Fact]
    public void WhenCarrySeedHoldsAcrossWholeBucket_ThenEqualsSeedValue()
    {
        // Arrange - no samples in [0,10); seed value 7 held entering From.
        var core = NewCore();
        var seed = new HistoryPoint(Base.AddSeconds(-30), 7d, null);

        // Act
        var point = core.Query(Twa(0, 10, carrySeed: seed)).Points.Single();

        // Assert - held the whole bucket -> 7
        Assert.Equal(7d, point.Number!.Value, 6);
    }

    [Fact]
    public void WhenNoCarryAndOneMidBucketSample_ThenIntegratesFromFirstSample()
    {
        // Arrange - no carry; single sample value 8 at t=5 in [0,10). Known only on [5,10): TWA = 8.
        var core = NewCore();
        core.Record("/a/Value", Base.AddSeconds(5), 8d, typeof(double));

        // Act
        var point = core.Query(Twa(0, 10)).Points.Single();

        // Assert
        Assert.Equal(8d, point.Number!.Value, 6);
    }

    [Fact]
    public void WhenBucketEmptyAndNoCarry_ThenNull()
    {
        // Arrange - nothing anywhere, no seed
        var core = NewCore();

        // Act
        var point = core.Query(Twa(0, 10)).Points.Single();

        // Assert
        Assert.Null(point.Number);
    }

    [Fact]
    public void WhenStoreOwnLookBackHoldsValue_ThenEmptyBucketUsesIt()
    {
        // Arrange - a sample before From at t=-5 (value 4) inside coverage; bucket [0,10) empty.
        // The store's own at-or-before look-back supplies the held value 4.
        var now = Base.AddSeconds(-10);
        var core = new InMemoryHistoryStore(
            priority: 100,
            maxPointsPerProperty: 1000,
            maxAge: TimeSpan.FromHours(1),
            maxJsonSize: 8192,
            getUtcNow: () => now);
        core.Record("/a/Value", Base.AddSeconds(-5), 4d, typeof(double));
        now = Base.AddSeconds(10);

        // Act
        var point = core.Query(Twa(0, 10)).Points.Single();

        // Assert
        Assert.Equal(4d, point.Number!.Value, 6);
    }

    [Fact]
    public void WhenTwoBuckets_ThenHeldValueThreadsAcross()
    {
        // Arrange - sample 6 at t=2 in bucket0; bucket1 [10,20) empty -> carries 6.
        var core = NewCore();
        core.Record("/a/Value", Base.AddSeconds(2), 6d, typeof(double));

        // Act
        var series = core.Query(Twa(0, 20));

        // Assert - bucket0 known only [2,10) at value 6 -> 6; bucket1 empty held 6 -> 6
        Assert.Equal(new double?[] { 6, 6 }, series.Points.Select(point => point.Number).ToArray());
    }
}
