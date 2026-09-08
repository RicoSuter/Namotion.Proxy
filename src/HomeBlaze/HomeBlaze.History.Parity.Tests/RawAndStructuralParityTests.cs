using System.Text.Json;
using HomeBlaze.History.Abstractions;
using Xunit;

namespace HomeBlaze.History.Parity.Tests;

public class RawAndStructuralParityTests
{
    private static readonly DateTimeOffset Base = ParityClock.Base;

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenRawExceedsMaxPoints_ThenNewestKeptAndTruncatedAcrossStores(ParityStoreFactory factory)
    {
        // Arrange - five samples, MaxPoints 3: newest three (values 2,3,4 ascending) retained, Truncated true.
        using var store = factory.Create();
        for (var second = 0; second < 5; second++)
        {
            store.Record("/a/Value", Base.AddSeconds(second), (double)second, typeof(double));
        }
        await store.FlushAsync();

        // Act
        var series = store.Query(new HistoryQuery(
            "/a/Value", Base.AddSeconds(-1), Base.AddSeconds(10), Bucket: null,
            Aggregation: HistoryAggregations.Last, MaxPoints: 3));

        // Assert
        Assert.True(series.Truncated);
        ParityAssert.NumbersEqual(new double?[] { 2d, 3d, 4d }, series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenEpochAnchoredBucketing_ThenBucketTimestampsAlignAcrossStores(ParityStoreFactory factory)
    {
        // Arrange - two 10s buckets; assert the exact bucket-start timestamps (epoch-anchored) match.
        using var store = factory.Create();
        store.Record("/a/Value", Base.AddSeconds(3), 1d, typeof(double));
        store.Record("/a/Value", Base.AddSeconds(13), 2d, typeof(double));
        await store.FlushAsync();

        // Act
        var series = store.Query(new HistoryQuery(
            "/a/Value", Base, Base.AddSeconds(20), TimeSpan.FromSeconds(10),
            HistoryAggregations.First, MaxPoints: 1000));

        // Assert
        Assert.Equal(new[] { Base, Base.AddSeconds(10) },
            series.Points.Select(point => point.Timestamp).ToArray());
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenPathMovedAndQueriedByNewPath_ThenOldSamplesResolveAcrossStores(ParityStoreFactory factory)
    {
        // Arrange - record under /a/Value, then move /a/Value -> /b/Value at t=5; query /b/Value over the
        // full range. Both engines resolve move chains on the exact recorded property path (the move's
        // from/to are full leaf paths, not parent subtrees), so the move records the full /a/Value -> /b/Value.
        using var store = factory.Create();
        store.Record("/a/Value", Base.AddSeconds(1), 10d, typeof(double));
        store.RecordMove(Base.AddSeconds(5), "/a/Value", "/b/Value");
        store.Record("/b/Value", Base.AddSeconds(7), 20d, typeof(double));
        await store.FlushAsync();

        // Act - querying the new path must surface the pre-move sample via chain resolution.
        var series = store.Query(new HistoryQuery(
            "/b/Value", Base, Base.AddSeconds(10), Bucket: null, Aggregation: HistoryAggregations.Last, MaxPoints: 1000));

        // Assert
        ParityAssert.NumbersEqual(new double?[] { 10d, 20d }, series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenPathMovedAwayAndBack_ThenBothOfItsWindowsResolveAcrossStores(ParityStoreFactory factory)
    {
        // Arrange - /a/Value -> /b/Value at t=5 and back at t=10, so /a/Value owns two disjoint windows.
        // A chain walk that stops when it revisits a path drops the earlier one and silently loses the
        // samples recorded in it, even though they are still stored and inside coverage.
        using var store = factory.Create();
        store.Record("/a/Value", Base.AddSeconds(1), 10d, typeof(double));
        store.RecordMove(Base.AddSeconds(5), "/a/Value", "/b/Value");
        store.Record("/b/Value", Base.AddSeconds(7), 20d, typeof(double));
        store.RecordMove(Base.AddSeconds(10), "/b/Value", "/a/Value");
        store.Record("/a/Value", Base.AddSeconds(12), 30d, typeof(double));
        await store.FlushAsync();

        // Act
        var series = store.Query(new HistoryQuery(
            "/a/Value", Base, Base.AddSeconds(15), Bucket: null, Aggregation: HistoryAggregations.Last, MaxPoints: 1000));

        // Assert
        ParityAssert.NumbersEqual(new double?[] { 10d, 20d, 30d }, series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenStringExceedsMaxJsonSize_ThenOversizePlaceholderAcrossStores(ParityStoreFactory factory)
    {
        // Arrange - a string longer than the 8 KB cap records a {"$oversize":true,"size":n} placeholder row.
        // Both engines measure size as the serialized JSON raw-text length (element.GetRawText().Length),
        // which for a 20000-character string is 20002 (the two surrounding JSON quotes are included).
        const int characterCount = 20_000;
        const int serializedJsonLength = characterCount + 2; // raw JSON text adds a leading and trailing quote
        using var store = factory.Create();
        var big = new string('x', characterCount);
        store.Record("/a/Note", Base.AddSeconds(1), big, typeof(string));
        await store.FlushAsync();

        // Act
        var series = store.Query(new HistoryQuery(
            "/a/Note", Base, Base.AddSeconds(10), Bucket: null, Aggregation: HistoryAggregations.Last, MaxPoints: 1000));

        // Assert - the timeline keeps the point; its JSON is the oversize placeholder, not the raw string.
        var point = Assert.Single(series.Points);
        Assert.NotNull(point.Json);
        Assert.True(point.Json!.Value.TryGetProperty("$oversize", out var flag) && flag.GetBoolean());
        Assert.Equal(serializedJsonLength, point.Json!.Value.GetProperty("size").GetInt32());
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenNumericAggregationOnJsonProperty_ThenThrowsAcrossStores(ParityStoreFactory factory)
    {
        // Arrange - a string property lives in value_json; numeric aggregation must be refused identically.
        using var store = factory.Create();
        store.Record("/a/Note", Base.AddSeconds(1), "hello", typeof(string));
        await store.FlushAsync();

        // Act & Assert
        Assert.Throws<HistoryAggregationNotSupportedException>(() =>
            store.Query(new HistoryQuery("/a/Note", Base, Base.AddSeconds(10), TimeSpan.FromSeconds(10),
                HistoryAggregations.SampleAverage, MaxPoints: 1000)));
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenUlongOverflowValue_ThenNumericAggregationCoalescesAcrossStores(ParityStoreFactory factory)
    {
        // Arrange - a ulong above long.MaxValue spills to value_json; Sum must COALESCE it back into numeric aggregation.
        using var store = factory.Create();
        var overflow = (ulong)long.MaxValue + 10UL;
        store.Record("/a/Counter", Base.AddSeconds(1), overflow, typeof(ulong));
        await store.FlushAsync();

        // Act
        var point = store.Query(new HistoryQuery("/a/Counter", Base, Base.AddSeconds(10), TimeSpan.FromSeconds(10),
            HistoryAggregations.Maximum, MaxPoints: 1000)).Points.Single();

        // Assert - the overflow value round-trips exactly within rounding at this magnitude (precision 0).
        Assert.Equal((double)overflow, point.Number!.Value, 0);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenGetSampleAtOrBefore_ThenHeldValueResolvesAcrossStores(ParityStoreFactory factory)
    {
        // Arrange - value 10 held from t=1s, value 30 held from t=5s.
        using var store = factory.Create();
        store.Record("/a/Value", Base.AddSeconds(1), 10d, typeof(double));
        store.Record("/a/Value", Base.AddSeconds(5), 30d, typeof(double));
        await store.FlushAsync();

        // Act
        var atFour = store.GetSampleAtOrBefore("/a/Value", Base.AddSeconds(4));
        var atTen = store.GetSampleAtOrBefore("/a/Value", Base.AddSeconds(10));
        var beforeFirst = store.GetSampleAtOrBefore("/a/Value", Base.AddSeconds(-1));

        // Assert - the held value at each instant, and null before the first sample.
        Assert.Equal(10d, atFour!.Number!.Value, 6);
        Assert.Equal(30d, atTen!.Number!.Value, 6);
        Assert.Null(beforeFirst);
    }
}
