using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Sqlite;

namespace HomeBlaze.History.Sqlite.Tests;

public sealed class SqliteHistoryStoreCoreTimeWeightedAverageTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Bucket = TimeSpan.FromSeconds(10);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-" + Guid.NewGuid().ToString("N"));

    // Start before every test interval, then expose a future now so the complete interval is covered.
    private SqliteHistoryStore NewCore()
    {
        var now = Base.AddYears(-1);
        var store = new SqliteHistoryStore(
            priority: 50,
            databaseDirectory: _directory,
            PartitionInterval.Weekly,
            TimeSpan.FromDays(3650),
            maxJsonSize: 8192,
            getUtcNow: () => now);
        now = Base.AddYears(1);
        return store;
    }

    private static HistoryQuery Twa(int fromSecond, int toSecond, HistoryPoint? carrySeed = null) =>
        new("/a/Value", Base.AddSeconds(fromSecond), Base.AddSeconds(toSecond), Bucket,
            HistoryAggregations.TimeWeightedAverage, MaxPoints: 1000, CarrySeed: carrySeed);

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { /* best effort temp cleanup */ }
    }

    [Fact]
    public async Task WhenStepValueHeldHalfThenOther_ThenDurationWeightedMean()
    {
        // Arrange - bucket [0,10): value 10 from t=0 (sample at 0), value 20 from t=5.
        // Held entering bucket via the sample at t=0. 10 holds [0,5), 20 holds [5,10): TWA = (10*5 + 20*5)/10 = 15.
        using var core = NewCore();
        core.Record("/a/Value", Base.AddSeconds(0), 10d, typeof(double));
        core.Record("/a/Value", Base.AddSeconds(5), 20d, typeof(double));
        await core.FlushAsync(CancellationToken.None);

        // Act
        var point = core.Query(Twa(0, 10)).Points.Single();

        // Assert
        Assert.Equal(15d, point.Number!.Value, 6);
    }

    [Fact]
    public async Task WhenCarrySeedHoldsAcrossWholeBucket_ThenEqualsSeedValue()
    {
        // Arrange - no samples in [0,10); seed value 7 held entering From.
        using var core = NewCore();
        var seed = new HistoryPoint(Base.AddSeconds(-30), 7d, null);
        await core.FlushAsync(CancellationToken.None);

        // Act
        var point = core.Query(Twa(0, 10, carrySeed: seed)).Points.Single();

        // Assert - held the whole bucket -> 7
        Assert.Equal(7d, point.Number!.Value, 6);
    }

    [Fact]
    public async Task WhenNoCarryAndOneMidBucketSample_ThenIntegratesFromFirstSample()
    {
        // Arrange - no carry; single sample value 8 at t=5 in [0,10). Known only on [5,10): TWA = 8.
        using var core = NewCore();
        core.Record("/a/Value", Base.AddSeconds(5), 8d, typeof(double));
        await core.FlushAsync(CancellationToken.None);

        // Act
        var point = core.Query(Twa(0, 10)).Points.Single();

        // Assert
        Assert.Equal(8d, point.Number!.Value, 6);
    }

    [Fact]
    public async Task WhenBucketEmptyAndNoCarry_ThenNull()
    {
        // Arrange - nothing anywhere, no seed
        using var core = NewCore();
        await core.FlushAsync(CancellationToken.None);

        // Act
        var point = core.Query(Twa(0, 10)).Points.Single();

        // Assert
        Assert.Null(point.Number);
    }

    [Fact]
    public async Task WhenStoreOwnLookBackHoldsValue_ThenEmptyBucketUsesIt()
    {
        // Arrange - a sample before From at t=-5 (value 4) inside coverage; bucket [0,10) empty.
        // The store's own at-or-before look-back supplies the held value 4.
        var now = Base.AddSeconds(-10);
        using var core = new SqliteHistoryStore(
            priority: 50,
            databaseDirectory: _directory,
            PartitionInterval.Weekly,
            TimeSpan.FromDays(365),
            maxJsonSize: 8192,
            getUtcNow: () => now);
        core.Record("/a/Value", Base.AddSeconds(-5), 4d, typeof(double));
        now = Base.AddSeconds(10);
        await core.FlushAsync(CancellationToken.None);

        // Act
        var point = core.Query(Twa(0, 10)).Points.Single();

        // Assert
        Assert.Equal(4d, point.Number!.Value, 6);
    }

    [Fact]
    public async Task WhenTwoBuckets_ThenHeldValueThreadsAcross()
    {
        // Arrange - sample 6 at t=2 in bucket0; bucket1 [10,20) empty -> carries 6.
        using var core = NewCore();
        core.Record("/a/Value", Base.AddSeconds(2), 6d, typeof(double));
        await core.FlushAsync(CancellationToken.None);

        // Act
        var series = core.Query(Twa(0, 20));

        // Assert - bucket0 known only [2,10) at value 6 -> 6; bucket1 empty held 6 -> 6
        Assert.Equal(new double?[] { 6, 6 }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenTwaSpansMorePartitionsThanAttachLimit_ThenReadsWithoutAttachError()
    {
        // Arrange - 14 daily partition files (one sample per day at the day's UTC midnight). A single TWA
        // query over the whole range must read all 14 files. The old reader opened the first file as "main"
        // and ATTACHed every other distinct file, but SQLite caps attached databases at 10, so spanning 11+
        // partition files threw "too many attached databases". With a 1-day bucket anchored at the Unix epoch,
        // each bucket boundary falls on a UTC midnight (the daily partition boundary), and the single sample
        // sits at its bucket start, so each bucket's TWA equals that day's value.
        var dayStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); // UTC midnight == bucket boundary
        var dayBucket = TimeSpan.FromDays(1);
        const int dayCount = 14;
        var now = dayStart.AddDays(-1);
        using var core = new SqliteHistoryStore(
            priority: 50, databaseDirectory: _directory, PartitionInterval.Daily, TimeSpan.FromDays(3650),
            maxJsonSize: 8192, getUtcNow: () => now);
        now = dayStart.AddDays(dayCount + 5);
        for (var day = 0; day < dayCount; day++)
        {
            core.Record("/a/Value", dayStart.AddDays(day), 10d + day, typeof(double));
        }
        await core.FlushAsync(CancellationToken.None);

        // Act - one TWA point per day, each equal to that day's held value (10..23).
        var series = core.Query(new HistoryQuery("/a/Value", dayStart, dayStart.AddDays(dayCount), dayBucket,
            HistoryAggregations.TimeWeightedAverage, MaxPoints: 1000));

        // Assert
        Assert.Equal(
            Enumerable.Range(0, dayCount).Select(day => (double?)(10d + day)).ToArray(),
            series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenBucketStraddlesPartitionBoundary_ThenTwaMatchesSinglePartition()
    {
        // Arrange - a 3-day bucket whose interior contains the Monday partition split (weekly partitions
        // are ISO-week anchored on Monday). The aligned bucket is [Sun 2026-06-21 00:00, Wed 2026-06-24 00:00).
        // Sample A lands in partition week 2026-06-15 (Sunday), sample B in week 2026-06-22 (Monday), so the
        // single bucket's weighted_sum/total_duration must be summed across two partition files.
        var bucket = TimeSpan.FromDays(3);
        var from = new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero); // Sunday 00:00 == bucketStart
        var to = from.AddDays(3);                                          // Wednesday 00:00 == bucketEnd
        var sampleA = from;                                                // value 10 at bucketStart (week 06-15)
        var sampleB = from.AddDays(1);                                     // value 30 at Monday 00:00 (week 06-22)

        using var core = NewCore();
        core.Record("/a/Value", sampleA, 10d, typeof(double));
        core.Record("/a/Value", sampleB, 30d, typeof(double));
        await core.FlushAsync(CancellationToken.None);

        // Act
        var point = core.Query(new HistoryQuery("/a/Value", from, to, bucket,
            HistoryAggregations.TimeWeightedAverage, MaxPoints: 1000)).Points.Single();

        // Assert - leading interval is zero (A sits at bucketStart); 10 over [Sun,Mon)=1 day,
        // 30 over [Mon,Wed)=2 days: TWA = (10*1 + 30*2) / 3 = 70/3.
        Assert.Equal(70d / 3d, point.Number!.Value, 6);
    }
}
