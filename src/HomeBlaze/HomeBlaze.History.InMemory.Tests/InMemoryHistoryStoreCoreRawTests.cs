using System.Text.Json;
using HomeBlaze.History.Abstractions;
using HomeBlaze.History.InMemory;

namespace HomeBlaze.History.InMemory.Tests;

public class InMemoryHistoryStoreCoreRawTests
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private static InMemoryHistoryStore NewCore(DateTimeOffset now, int maxPoints = 1000, int maxAgeSeconds = 60) =>
        new(priority: 100, maxPointsPerProperty: maxPoints, maxAge: TimeSpan.FromSeconds(maxAgeSeconds),
            maxJsonSize: 8192, getUtcNow: () => now);

    private static InMemoryHistoryStore NewCore(Func<DateTimeOffset> getUtcNow) =>
        new(priority: 100, maxPointsPerProperty: 1000, maxAge: TimeSpan.FromSeconds(60),
            maxJsonSize: 8192, getUtcNow);

    [Fact]
    public void WhenDoublesRecorded_ThenRawQueryReturnsNumbersAscending()
    {
        // Arrange
        var core = NewCore(Base.AddSeconds(10));
        core.Record("/a/Value", Base.AddSeconds(0), 1.5d, typeof(double));
        core.Record("/a/Value", Base.AddSeconds(1), 2.5d, typeof(double));

        // Act
        var series = core.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(10)));

        // Assert
        Assert.False(series.Truncated);
        Assert.Equal(new double?[] { 1.5, 2.5 }, series.Points.Select(point => point.Number).ToArray());
        Assert.All(series.Points, point => Assert.Null(point.Json));
    }

    [Fact]
    public void WhenLongAndBoolRecorded_ThenStoredInNumberColumn()
    {
        // Arrange
        var core = NewCore(Base.AddSeconds(10));
        core.Record("/a/Count", Base.AddSeconds(0), 42L, typeof(long));
        core.Record("/a/Flag", Base.AddSeconds(0), true, typeof(bool));

        // Act
        var counts = core.Query(new HistoryQuery("/a/Count", Base, Base.AddSeconds(10)));
        var flags = core.Query(new HistoryQuery("/a/Flag", Base, Base.AddSeconds(10)));

        // Assert
        Assert.Equal(42d, counts.Points.Single().Number);
        Assert.Equal(1d, flags.Points.Single().Number); // bool as 1/0
    }

    [Fact]
    public void WhenDecimalRecorded_ThenStoredAsNumberForCharting()
    {
        // Arrange - decimal routes to the numeric (double) column so the chart and aggregations see a number.
        var core = NewCore(Base.AddSeconds(10));
        core.Record("/a/Temperature", Base.AddSeconds(0), 0.1m, typeof(decimal));

        // Act
        var point = core.Query(new HistoryQuery("/a/Temperature", Base, Base.AddSeconds(10))).Points.Single();

        // Assert - surfaced as Number (graphable), not an opaque Json value
        Assert.Equal((double)0.1m, point.Number);
        Assert.Null(point.Json);
    }

    [Fact]
    public void WhenStringRecorded_ThenStoredInJsonColumn()
    {
        // Arrange
        var core = NewCore(Base.AddSeconds(10));
        core.Record("/a/Name", Base.AddSeconds(0), "hello", typeof(string));

        // Act
        var series = core.Query(new HistoryQuery("/a/Name", Base, Base.AddSeconds(10)));

        // Assert
        var point = series.Points.Single();
        Assert.Null(point.Number);
        Assert.Equal(JsonValueKind.String, point.Json!.Value.ValueKind);
        Assert.Equal("hello", point.Json!.Value.GetString());
    }

    [Fact]
    public void WhenEnumRecorded_ThenStoredAsName()
    {
        // Arrange
        var core = NewCore(Base.AddSeconds(10));
        core.Record("/a/Mode", Base.AddSeconds(0), DayOfWeek.Friday, typeof(DayOfWeek));

        // Act
        var point = core.Query(new HistoryQuery("/a/Mode", Base, Base.AddSeconds(10))).Points.Single();

        // Assert
        Assert.Equal("Friday", point.Json!.Value.GetString());
    }

    [Fact]
    public void WhenNullRecorded_ThenPointHasNoValueColumns()
    {
        // Arrange
        var core = NewCore(Base.AddSeconds(10));
        core.Record("/a/Value", Base.AddSeconds(0), null, typeof(double?));

        // Act
        var point = core.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(10))).Points.Single();

        // Assert - explicit recorded null: a real point, both columns null
        Assert.Equal(Base, point.Timestamp);
        Assert.Null(point.Number);
        Assert.Null(point.Json);
    }

    [Fact]
    public void WhenMorePointsThanBudget_ThenNewestKeptAndTruncatedSet()
    {
        // Arrange
        var core = NewCore(Base.AddSeconds(100));
        for (var i = 0; i < 5; i++)
        {
            core.Record("/a/Value", Base.AddSeconds(i), (double)i, typeof(double));
        }

        // Act - budget of 2 keeps the two newest, ascending
        var series = core.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(100), MaxPoints: 2));

        // Assert
        Assert.True(series.Truncated);
        Assert.Equal(new double?[] { 3, 4 }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public void WhenUnknownPath_ThenEmptyNotTruncated()
    {
        // Arrange
        var core = NewCore(Base.AddSeconds(10));

        // Act
        var series = core.Query(new HistoryQuery("/missing", Base, Base.AddSeconds(10)));

        // Assert
        Assert.Empty(series.Points);
        Assert.False(series.Truncated);
        Assert.Equal("/missing", series.PropertyPath);
    }

    [Fact]
    public void WhenGetSampleAtOrBefore_ThenReturnsHeldValueOrNull()
    {
        // Arrange
        var now = Base;
        var core = NewCore(() => now);
        core.Record("/a/Value", Base.AddSeconds(0), 7d, typeof(double));
        core.Record("/a/Value", Base.AddSeconds(5), 9d, typeof(double));
        now = Base.AddSeconds(10);

        // Act
        var held = core.GetSampleAtOrBefore("/a/Value", Base.AddSeconds(3));
        var beforeAll = core.GetSampleAtOrBefore("/a/Value", Base.AddSeconds(-1));

        // Assert
        Assert.Equal(7d, held!.Number);
        Assert.Null(beforeAll);
    }

    [Fact]
    public void WhenCoverageRequested_ThenSpansMaxAgeWindowToNow()
    {
        // Arrange
        var now = Base;
        var core = new InMemoryHistoryStore(
            priority: 100,
            maxPointsPerProperty: 1000,
            maxAge: TimeSpan.FromSeconds(60),
            maxJsonSize: 8192,
            getUtcNow: () => now);
        core.Record("/a/Value", Base.AddSeconds(70), 1d, typeof(double));
        now = Base.AddSeconds(120);

        // Act
        var coverage = Assert.Single(core.CoverageRanges);

        // Assert
        Assert.Equal(Base.AddSeconds(60), coverage.From);
        Assert.Equal(now, coverage.To);
    }

    [Fact]
    public void WhenCountEvictionDropsOldestSamples_ThenCoverageFromIsOldestRetainedSample()
    {
        // Arrange - a small per-property cap with a large max age, so count eviction (not age) bounds the
        // buffer. The store starts at Base; the clock is advanced before coverage is read.
        var clock = Base;
        var core = new InMemoryHistoryStore(
            priority: 100, maxPointsPerProperty: 2, maxAge: TimeSpan.FromHours(1),
            maxJsonSize: 8192, getUtcNow: () => clock);

        // Record three samples into a capacity-two ring; the oldest (t=1s) is evicted, leaving t=2s and t=3s.
        core.Record("/a/Value", Base.AddSeconds(1), 1d, typeof(double));
        core.Record("/a/Value", Base.AddSeconds(2), 2d, typeof(double));
        core.Record("/a/Value", Base.AddSeconds(3), 3d, typeof(double));

        // Advance the clock so now - maxAge sits far below the oldest retained sample.
        clock = Base.AddSeconds(30);

        // Act
        var coverage = Assert.Single(core.CoverageRanges);

        // Assert - From is the oldest sample actually retained (t=2s), not now - maxAge nor _startTime; the
        // store cannot honestly claim coverage back to a time whose samples were evicted by the count cap.
        Assert.Equal(Base.AddSeconds(2), coverage.From);
        Assert.Equal(clock, coverage.To);
    }

    [Fact]
    public void WhenOnePropertyOverflows_ThenStoreCoverageUsesWorstPropertyFloor()
    {
        // Arrange
        var clock = Base;
        var core = new InMemoryHistoryStore(
            priority: 100, maxPointsPerProperty: 2, maxAge: TimeSpan.FromHours(1),
            maxJsonSize: 8192, getUtcNow: () => clock);

        // A quiet property has older retained data, while the noisy property exceeds its capacity.
        core.Record("/quiet/Value", Base.AddSeconds(1), 1d, typeof(double));
        core.Record("/noisy/Value", Base.AddSeconds(2), 2d, typeof(double));
        core.Record("/noisy/Value", Base.AddSeconds(3), 3d, typeof(double));
        core.Record("/noisy/Value", Base.AddSeconds(4), 4d, typeof(double));
        clock = Base.AddSeconds(30);

        // Act
        var coverage = Assert.Single(core.CoverageRanges);

        // Assert
        Assert.Equal(Base.AddSeconds(3), coverage.From);
        Assert.Equal(clock, coverage.To);
    }

    [Fact]
    public void WhenAnotherPropertyAdvancesCoverageFloor_ThenDirectBucketsDoNotCarryOlderValue()
    {
        // Arrange
        var clock = Base;
        var core = new InMemoryHistoryStore(
            priority: 100,
            maxPointsPerProperty: 2,
            maxAge: TimeSpan.FromHours(1),
            maxJsonSize: 8192,
            getUtcNow: () => clock);
        core.Record("/quiet/Value", Base.AddSeconds(1), 7d, typeof(double));
        core.Record("/noisy/Value", Base.AddSeconds(2), 2d, typeof(double));
        core.Record("/noisy/Value", Base.AddSeconds(3), 3d, typeof(double));
        core.Record("/noisy/Value", Base.AddSeconds(4), 4d, typeof(double));
        clock = Base.AddSeconds(30);

        // Act
        var series = core.Query(new HistoryQuery(
            "/quiet/Value",
            Base,
            clock,
            TimeSpan.FromSeconds(10),
            HistoryAggregations.Last,
            MaxPoints: 10));

        // Assert
        Assert.Equal(
            new double?[] { null, null, null },
            series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public void WhenNoPropertyHasOverflowed_ThenFirstSampleDoesNotNarrowCoverage()
    {
        // Arrange
        var clock = Base;
        var core = new InMemoryHistoryStore(
            priority: 100, maxPointsPerProperty: 2, maxAge: TimeSpan.FromHours(1),
            maxJsonSize: 8192, getUtcNow: () => clock);
        core.Record("/quiet/Value", Base.AddSeconds(10), 1d, typeof(double));
        clock = Base.AddSeconds(30);

        // Act
        var coverage = Assert.Single(core.CoverageRanges);

        // Assert
        Assert.Equal(Base, coverage.From);
        Assert.Equal(clock, coverage.To);
    }

    [Fact]
    public void WhenNoSamplesRecorded_ThenCoverageFromKeepsStartTimeOrMaxAgeFloor()
    {
        // Arrange - start at Base, advance the clock, never record a sample.
        var clock = Base;
        var core = new InMemoryHistoryStore(
            priority: 100, maxPointsPerProperty: 1000, maxAge: TimeSpan.FromHours(1),
            maxJsonSize: 8192, getUtcNow: () => clock);
        clock = Base.AddSeconds(30);

        // Act
        var coverage = Assert.Single(core.CoverageRanges);

        // Assert - with no samples, From keeps the existing max(_startTime, now - maxAge) behavior. Here
        // now - maxAge (Base + 30s - 1h) is below _startTime (Base), so From == _startTime.
        Assert.Equal(Base, coverage.From);
        Assert.Equal(clock, coverage.To);
    }

    [Fact]
    public void WhenClockMovesBeforeStoreStart_ThenCoverageIsEmpty()
    {
        // Arrange
        var clock = Base;
        var core = new InMemoryHistoryStore(
            priority: 100, maxPointsPerProperty: 1000, maxAge: TimeSpan.FromHours(1),
            maxJsonSize: 8192, getUtcNow: () => clock);
        clock = Base.AddMinutes(-1);

        // Act
        var coverage = core.CoverageRanges;

        // Assert
        Assert.Empty(coverage);
    }

    [Fact]
    public void WhenClockMovesBackwardAfterAgeEviction_ThenCoverageDoesNotReclaimEvictedRange()
    {
        // Arrange
        var clock = Base;
        var core = new InMemoryHistoryStore(
            priority: 100, maxPointsPerProperty: 1000, maxAge: TimeSpan.FromHours(1),
            maxJsonSize: 8192, getUtcNow: () => clock);
        core.Record("/a/Value", Base, 1d, typeof(double));
        clock = Base.AddHours(2);
        core.Sweep();
        clock = Base.AddMinutes(30);

        // Act
        var coverage = core.CoverageRanges;

        // Assert
        Assert.Empty(coverage);
    }

    [Fact]
    public void WhenSwept_ThenSamplesOlderThanMaxAgeEvicted()
    {
        // Arrange - now is Base+120s, MaxAge 60s, so anything before Base+60s is stale
        var now = Base.AddSeconds(120);
        var core = NewCore(now, maxAgeSeconds: 60);
        core.Record("/a/Value", Base.AddSeconds(10), 1d, typeof(double)); // stale
        core.Record("/a/Value", Base.AddSeconds(90), 2d, typeof(double)); // fresh

        // Act
        core.Sweep();
        var series = core.Query(new HistoryQuery("/a/Value", Base, now.AddSeconds(1)));

        // Assert
        Assert.Equal(new double?[] { 2 }, series.Points.Select(point => point.Number).ToArray());
        Assert.Equal(1, core.EvictedCount);
    }
}
