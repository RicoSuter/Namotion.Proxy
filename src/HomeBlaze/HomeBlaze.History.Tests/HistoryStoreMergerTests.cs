using System.Text.Json;
using HomeBlaze.History;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Tests;

public class HistoryStoreMergerTests
{
    private static readonly DateTimeOffset Origin = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double minutes) => Origin.AddMinutes(minutes);

    private static IReadOnlySet<string> Only(params string[] aggregations) =>
        new HashSet<string>(aggregations, StringComparer.Ordinal);

    // Raw planner -----------------------------------------------------------------------------

    [Fact]
    public async Task WhenStoresAreDisjoint_ThenEachServesItsSlice()
    {
        // Arrange
        var older = new FakeHistoryStore
        {
            Priority = 50,
            CurrentCoverage = new HistoryCoverage(At(0), At(30))
        }.AddSample(At(10), 1).AddSample(At(20), 2);
        var newer = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(30), At(60))
        }.AddSample(At(40), 3).AddSample(At(50), 4);
        var query = new HistoryQuery("temp", At(0), At(60), Aggregation: HistoryAggregations.Last);

        // Act
        var series = await new[] { older, newer }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert
        Assert.Equal(new double?[] { 1d, 2d, 3d, 4d }, series.Points.Select(point => point.Number).ToArray());
        Assert.False(series.Truncated);
        Assert.Single(older.ReceivedQueries);
        Assert.Single(newer.ReceivedQueries);
        Assert.Equal(At(0), older.ReceivedQueries[0].From);
        Assert.Equal(At(30), older.ReceivedQueries[0].To);
        Assert.Equal(At(30), newer.ReceivedQueries[0].From);
        Assert.Equal(At(60), newer.ReceivedQueries[0].To);
    }

    [Fact]
    public async Task WhenStoresOverlap_ThenHigherPriorityWinsTheOverlap()
    {
        // Arrange
        var persistent = new FakeHistoryStore
        {
            Priority = 50,
            CurrentCoverage = new HistoryCoverage(At(0), At(60))
        }.AddSample(At(10), 1).AddSample(At(50), 99);
        var live = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(40), At(60))
        }.AddSample(At(50), 5);
        var query = new HistoryQuery("temp", At(0), At(60), Aggregation: HistoryAggregations.Last);

        // Act
        var series = await new[] { persistent, live }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert: live (priority 100) claims [40,60); persistent gets only [0,40).
        Assert.Equal(At(0), persistent.ReceivedQueries[0].From);
        Assert.Equal(At(40), persistent.ReceivedQueries[0].To);
        Assert.Equal(At(40), live.ReceivedQueries[0].From);
        Assert.Equal(At(60), live.ReceivedQueries[0].To);

        // The overlap at 50 resolves to the live store's value (5), not the persistent value (99).
        var pointAt50 = series.Points.Single(point => point.Timestamp == At(50));
        Assert.Equal(5d, pointAt50.Number);
    }

    [Fact]
    public async Task WhenHigherPriorityStoreHasCoverageGap_ThenLowerPriorityStoreFillsIt()
    {
        // Arrange
        var highPriority = new FakeHistoryStore
        {
            Priority = 100,
            CoverageRanges =
            [
                new HistoryCoverage(At(0), At(20)),
                new HistoryCoverage(At(40), At(60))
            ]
        }.AddSample(At(10), 100).AddSample(At(50), 500);
        var lowPriority = new FakeHistoryStore
        {
            Priority = 50,
            CurrentCoverage = new HistoryCoverage(At(0), At(60))
        }.AddSample(At(10), 1).AddSample(At(30), 3).AddSample(At(50), 5);
        var query = new HistoryQuery("temp", At(0), At(60));

        // Act
        var series = await new[] { lowPriority, highPriority }
            .QueryHistoryAsync(query, CancellationToken.None);

        // Assert
        Assert.Equal(new double?[] { 100, 3, 500 }, series.Points.Select(point => point.Number));
        Assert.Equal(2, highPriority.ReceivedQueries.Count);
        var lowQuery = Assert.Single(lowPriority.ReceivedQueries);
        Assert.Equal(At(20), lowQuery.From);
        Assert.Equal(At(40), lowQuery.To);
        var coverage = Assert.Single(series.CoverageRanges);
        Assert.Equal(new HistoryCoverage(At(0), At(60)), coverage);
    }

    [Fact]
    public async Task WhenStoreThrows_ThenErrorPropagates()
    {
        // Arrange
        var store = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(0), At(60)),
            ThrowOnQuery = true
        };
        var query = new HistoryQuery("temp", At(0), At(60), Aggregation: HistoryAggregations.Last);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new[] { store }.QueryHistoryAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task WhenStoreSetIsEmpty_ThenUniversalAggregationReturnsEmptySeries()
    {
        // Arrange
        var query = new HistoryQuery("temp", At(0), At(60), Aggregation: HistoryAggregations.Last);

        // Act
        var series = await Array.Empty<IHistoryStore>().QueryHistoryAsync(query, CancellationToken.None);

        // Assert
        Assert.Empty(series.Points);
        Assert.False(series.Truncated);
        Assert.Equal("temp", series.PropertyPath);
    }

    [Fact]
    public async Task WhenRangePartIsUncovered_ThenItIsAnHonestGap()
    {
        // Arrange: store covers only [0,30); the query asks [0,60).
        var store = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(0), At(30))
        }.AddSample(At(10), 1);
        var query = new HistoryQuery("temp", At(0), At(60), Aggregation: HistoryAggregations.Last);

        // Act
        var series = await new[] { store }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert: only the covered slice was queried; [30,60) is simply absent.
        Assert.Equal(At(0), store.ReceivedQueries[0].From);
        Assert.Equal(At(30), store.ReceivedQueries[0].To);
        Assert.Equal(new double?[] { 1d }, series.Points.Select(point => point.Number).ToArray());
    }

    // Bucketed planner ------------------------------------------------------------------------

    [Fact]
    public async Task WhenBucketed_ThenOlderBucketsFromPersistentAndNewestFromLive()
    {
        // Arrange: 10-minute buckets across [0,60); persistent covers [0,40), live covers [40,60).
        var persistent = new FakeHistoryStore
        {
            Priority = 50,
            CurrentCoverage = new HistoryCoverage(At(0), At(40))
        }.AddSample(At(5), 1).AddSample(At(25), 2);
        var live = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(40), At(60))
        }.AddSample(At(45), 3).AddSample(At(55), 4);
        var query = new HistoryQuery("temp", At(0), At(60), TimeSpan.FromMinutes(10), HistoryAggregations.SampleAverage);

        // Act
        var series = await new[] { persistent, live }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert: persistent serves buckets in [0,40); live serves buckets in [40,60).
        Assert.Equal(At(0), persistent.ReceivedQueries[0].From);
        Assert.Equal(At(40), persistent.ReceivedQueries[0].To);
        Assert.Equal(At(40), live.ReceivedQueries[0].From);
        Assert.Equal(At(60), live.ReceivedQueries[0].To);

        // Six buckets total (one per 10-minute slot), each owned by exactly one store.
        Assert.Equal(6, series.Points.Length);
    }

    [Fact]
    public async Task WhenConsecutiveBucketsShareOwner_ThenTheyGroupIntoOneSubQuery()
    {
        // Arrange: a single store covering the whole range serves all buckets in one sub-query.
        var store = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(0), At(60))
        }.AddSample(At(5), 1);
        var query = new HistoryQuery("temp", At(0), At(60), TimeSpan.FromMinutes(10), HistoryAggregations.SampleAverage);

        // Act
        await new[] { store }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert: exactly one sub-query spanning the full range, not one per bucket.
        Assert.Single(store.ReceivedQueries);
        Assert.Equal(At(0), store.ReceivedQueries[0].From);
        Assert.Equal(At(60), store.ReceivedQueries[0].To);
    }

    [Fact]
    public async Task WhenBucketRangeExceedsBudget_ThenOnlyNewestBucketsArePlanned()
    {
        // Arrange
        var store = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(0), At(60))
        };
        var query = new HistoryQuery(
            "temp",
            At(0),
            At(60),
            TimeSpan.FromMinutes(10),
            HistoryAggregations.Count,
            MaxPoints: 2);

        // Act
        var series = await new[] { store }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert
        var received = Assert.Single(store.ReceivedQueries);
        Assert.Equal(At(40), received.From);
        Assert.Equal(At(60), received.To);
        Assert.Equal(2, series.Points.Length);
        Assert.True(series.Truncated);
    }

    [Fact]
    public async Task WhenRangeEndsMidBucket_ThenRightEdgeBucketIsStillOwned()
    {
        // Arrange: To at 25 minutes with 10-minute buckets, so the bucket [20,30) extends past To. The
        // sample at 27 is inside that bucket but outside the requested window, and it is far enough
        // from the one at 22 that averaging them in would be unmistakable.
        var store = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(0), At(30))
        }.AddSample(At(5), 1).AddSample(At(22), 2).AddSample(At(27), 100);
        var query = new HistoryQuery("temp", At(0), At(25), TimeSpan.FromMinutes(10), HistoryAggregations.SampleAverage);

        // Act
        var series = await new[] { store }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert: the right-edge bucket is still owned and served, but only up to To, so the sample at
        // 27 cannot reach the result. Fetching the full bucket made the trailing point an aggregate
        // over data the caller never asked for, silently.
        Assert.Single(store.ReceivedQueries);
        Assert.Equal(At(0), store.ReceivedQueries[0].From);
        Assert.Equal(At(25), store.ReceivedQueries[0].To);
        Assert.Equal(2d, series.Points.Last().Number);
    }

    [Fact]
    public async Task WhenNoStoreSupportsTheAggregation_ThenThrowsAndAvailableExcludesRequested()
    {
        // A genuine capability gap: no store can compute the requested aggregation at all. The only
        // store supports just SampleAverage, but the query asks Minimum. Eligibility must throw, and the
        // reported Available set must NOT contain the requested aggregation, so the message is no longer
        // self-contradictory ("Minimum is not supported. Available: ...Minimum...").
        //
        // Arrange
        var store = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(0), At(60)),
            SupportedAggregations = Only(HistoryAggregations.SampleAverage)
        };
        var query = new HistoryQuery("temp", At(0), At(60), TimeSpan.FromMinutes(10), HistoryAggregations.Minimum);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<HistoryAggregationNotSupportedException>(
            () => new[] { store }.QueryHistoryAsync(query, CancellationToken.None));
        Assert.Equal(HistoryAggregations.Minimum, exception.Aggregation);
        Assert.DoesNotContain(HistoryAggregations.Minimum, exception.Available);
        Assert.Contains(HistoryAggregations.SampleAverage, exception.Available);
        Assert.Empty(store.ReceivedQueries);
    }

    [Fact]
    public async Task WhenAggregationSupportingStoreCoversOnlyPart_ThenBucketsAreSplitByOwnership()
    {
        // Arrange: Minimum-capable store covers [0,30); the other store (Last only) covers [30,60).
        var minStore = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(0), At(30)),
            SupportedAggregations = Only(HistoryAggregations.Minimum)
        }.AddSample(At(5), 1);
        var lastStore = new FakeHistoryStore
        {
            Priority = 50,
            CurrentCoverage = new HistoryCoverage(At(30), At(60)),
            SupportedAggregations = Only(HistoryAggregations.Last)
        }.AddSample(At(45), 2);
        var query = new HistoryQuery("temp", At(0), At(30), TimeSpan.FromMinutes(10), HistoryAggregations.Minimum);

        // Act: query only the part the Minimum store covers, so eligibility passes.
        await new[] { minStore, lastStore }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert: only the Minimum-capable store was dispatched.
        Assert.Single(minStore.ReceivedQueries);
        Assert.Empty(lastStore.ReceivedQueries);
    }

    // Executor --------------------------------------------------------------------------------

    [Fact]
    public async Task WhenBudgetIsExhausted_ThenTruncatedAndNewestPointsKept()
    {
        // Arrange: two disjoint stores; budget only covers the newest store's points.
        var older = new FakeHistoryStore
        {
            Priority = 50,
            CurrentCoverage = new HistoryCoverage(At(0), At(30))
        }.AddSample(At(10), 1).AddSample(At(20), 2);
        var newer = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(30), At(60))
        }.AddSample(At(40), 3).AddSample(At(50), 4);
        var query = new HistoryQuery("temp", At(0), At(60), Aggregation: HistoryAggregations.Last, MaxPoints: 2);

        // Act
        var series = await new[] { older, newer }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert: only the two newest points survive; truncated because the older segment was dropped.
        Assert.Equal(new double?[] { 3d, 4d }, series.Points.Select(point => point.Number).ToArray());
        Assert.True(series.Truncated);
        Assert.Empty(older.ReceivedQueries);
    }

    [Fact]
    public async Task WhenSubQueryTruncates_ThenSeriesTruncatedIsTrue()
    {
        // Arrange: one store with more samples than the budget.
        var store = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(0), At(60))
        }.AddSample(At(10), 1).AddSample(At(20), 2).AddSample(At(30), 3);
        var query = new HistoryQuery("temp", At(0), At(60), Aggregation: HistoryAggregations.Last, MaxPoints: 2);

        // Act
        var series = await new[] { store }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert: newest two kept, truncated flagged.
        Assert.Equal(new double?[] { 2d, 3d }, series.Points.Select(point => point.Number).ToArray());
        Assert.True(series.Truncated);

        // And coverage retreats to the oldest point actually returned. The store covers [0,60) and was
        // asked about all of it, but the sample at t=10 was dropped, so nothing in this result stands
        // behind [0,20). Reporting the full window there tells a caller "covered, and nothing happened",
        // which a state timeline draws as a held value and an agent reads as fact.
        Assert.Equal(
            new[] { new HistoryCoverage(At(20), At(60)) },
            series.CoverageRanges.ToArray());
    }

    [Fact]
    public async Task WhenSampleSitsOnSegmentBoundary_ThenItIsOwnedOnceByTheOwningStore()
    {
        // The planners produce non-overlapping, half-open sub-ranges, so a sample on the boundary
        // between two adjacent segments belongs to exactly one of them. The merged series must not
        // duplicate it, and its value must come from the owning (higher-priority) store's sub-range.
        //
        // Arrange: both stores hold a sample at the shared boundary timestamp inside their overlap.
        var persistent = new FakeHistoryStore
        {
            Priority = 50,
            CurrentCoverage = new HistoryCoverage(At(0), At(60))
        }.AddSample(At(30), 99);
        var live = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(20), At(60))
        }.AddSample(At(30), 7);
        var query = new HistoryQuery("temp", At(0), At(60), Aggregation: HistoryAggregations.Last);

        // Act
        var series = await new[] { persistent, live }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert: live (priority 100) owns [20,60) and serves the boundary sample at 30; persistent is
        // asked only for [0,20), which excludes 30, so the timestamp appears once with the live value.
        Assert.Single(series.Points, point => point.Timestamp == At(30));
        var pointAt30 = series.Points.Single(point => point.Timestamp == At(30));
        Assert.Equal(7d, pointAt30.Number);
        Assert.Equal(At(0), persistent.ReceivedQueries[0].From);
        Assert.Equal(At(20), persistent.ReceivedQueries[0].To);
    }

    [Fact]
    public async Task WhenResultsSpanStores_ThenPointsAreAscendingByTimestamp()
    {
        // Arrange
        var older = new FakeHistoryStore
        {
            Priority = 50,
            CurrentCoverage = new HistoryCoverage(At(0), At(30))
        }.AddSample(At(25), 2).AddSample(At(5), 1);
        var newer = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(30), At(60))
        }.AddSample(At(55), 4).AddSample(At(35), 3);
        var query = new HistoryQuery("temp", At(0), At(60), Aggregation: HistoryAggregations.Last);

        // Act
        var series = await new[] { older, newer }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert
        var timestamps = series.Points.Select(point => point.Timestamp).ToArray();
        Assert.Equal(timestamps.OrderBy(timestamp => timestamp).ToArray(), timestamps);
    }

    // Carry resolution ------------------------------------------------------------------------

    [Fact]
    public async Task WhenLastChangePredatesLiveWindow_ThenHeldValueShowsAtLiveEdge()
    {
        // Arrange: a stable property changed at minute 5 and never again. The persistent store holds
        // that sample and covers [0,40). The live store covers [40,60) but holds nothing (evicted).
        var persistent = new FakeHistoryStore
        {
            Priority = 50,
            CurrentCoverage = new HistoryCoverage(At(0), At(40))
        }.AddSample(At(5), 42);
        var live = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(40), At(60))
        };
        var query = new HistoryQuery("temp", At(40), At(60), TimeSpan.FromMinutes(10), HistoryAggregations.Last);

        // Act
        var series = await new[] { persistent, live }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert: the live store's buckets carry the held value (42), not a gap.
        Assert.NotEmpty(series.Points);
        Assert.All(series.Points, point => Assert.Equal(42d, point.Number));

        // The carry seed entering the live segment was the held value resolved cross-store.
        Assert.Equal(42d, live.ReceivedQueries[0].CarrySeed?.Number);
    }

    [Fact]
    public async Task WhenCarrySeeded_ThenInitialSeedComesFromPriorityOrderedWalk()
    {
        // Arrange: two stores hold a prior sample before From; the higher-priority one wins the seed.
        var lowPriority = new FakeHistoryStore
        {
            Priority = 10,
            CurrentCoverage = new HistoryCoverage(At(0), At(60))
        }.AddSample(At(5), 1);
        var highPriority = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(0), At(60))
        }.AddSample(At(10), 9);
        var query = new HistoryQuery("temp", At(40), At(60), TimeSpan.FromMinutes(10), HistoryAggregations.Last);

        // Act
        await new[] { lowPriority, highPriority }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert: the high-priority store owns the live segment and its seed is its own prior sample (9).
        Assert.Equal(9d, highPriority.ReceivedQueries[0].CarrySeed?.Number);
    }

    [Fact]
    public async Task WhenCarryThreadsAcrossSegments_ThenLaterSegmentSeedsFromEarlierSegment()
    {
        // Arrange: persistent store covers [0,40) with a sample at 35; live store covers [40,60) empty.
        var persistent = new FakeHistoryStore
        {
            Priority = 50,
            CurrentCoverage = new HistoryCoverage(At(0), At(40))
        }.AddSample(At(35), 17);
        var live = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(40), At(60))
        };
        var query = new HistoryQuery("temp", At(0), At(60), TimeSpan.FromMinutes(10), HistoryAggregations.Last);

        // Act
        var series = await new[] { persistent, live }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert: the live segment's carry seed is the persistent segment's last non-null value (17).
        Assert.Equal(17d, live.ReceivedQueries[0].CarrySeed?.Number);

        // The live-edge buckets continue the held value rather than producing gaps.
        var liveEdgePoints = series.Points.Where(point => point.Timestamp >= At(40)).ToArray();
        Assert.NotEmpty(liveEdgePoints);
        Assert.All(liveEdgePoints, point => Assert.Equal(17d, point.Number));
    }

    [Fact]
    public async Task WhenCoverageHasGap_ThenCarryDoesNotCrossGap()
    {
        // Arrange
        var store = new FakeHistoryStore
        {
            Priority = 100,
            CoverageRanges =
            [
                new HistoryCoverage(At(0), At(20)),
                new HistoryCoverage(At(40), At(60))
            ]
        }.AddSample(At(10), 7);
        var query = new HistoryQuery(
            "temp", At(0), At(60), TimeSpan.FromMinutes(10), HistoryAggregations.Last);

        // Act
        var series = await new[] { store }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert
        Assert.Equal(new double?[] { null, 7, null, null, null, null },
            series.Points.Select(point => point.Number));
        Assert.Equal(2, store.ReceivedQueries.Count);
        Assert.Null(store.ReceivedQueries[1].CarrySeed);
        Assert.Equal(
            new[]
            {
                new HistoryCoverage(At(0), At(20)),
                new HistoryCoverage(At(40), At(60))
            },
            series.CoverageRanges.ToArray());
    }

    [Fact]
    public async Task WhenExplicitNullEndsEarlierSegment_ThenLaterSegmentReceivesClearedCarry()
    {
        // Arrange
        var persistent = new FakeHistoryStore
        {
            Priority = 50,
            CurrentCoverage = new HistoryCoverage(At(0), At(40))
        }.AddSample(At(5), 17).AddNullSample(At(35));
        var live = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(40), At(60))
        };
        var query = new HistoryQuery(
            "temp", At(0), At(60), TimeSpan.FromMinutes(10), HistoryAggregations.TimeWeightedAverage);

        // Act
        var series = await new[] { persistent, live }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert
        Assert.NotNull(live.ReceivedQueries[0].CarrySeed);
        Assert.Null(live.ReceivedQueries[0].CarrySeed!.Number);
        Assert.Null(live.ReceivedQueries[0].CarrySeed!.Json);
        Assert.All(
            series.Points.Where(point => point.Timestamp >= At(40)),
            point => Assert.Null(point.Number));
    }

    [Fact]
    public async Task WhenNoSampleExistsBeforeFrom_ThenInitialSeedIsNull()
    {
        // Arrange: no store has any sample before From.
        var store = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(0), At(60))
        }.AddSample(At(35), 5);
        var query = new HistoryQuery("temp", At(0), At(60), TimeSpan.FromMinutes(10), HistoryAggregations.Last);

        // Act
        await new[] { store }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert
        Assert.Null(store.ReceivedQueries[0].CarrySeed);
    }

    [Fact]
    public async Task WhenCarriedValueIsJson_ThenEmptyBucketsCarryTheHeldJsonValue()
    {
        // The carry-advance treats a point as non-null when Number OR Json is set, so a Json-valued
        // property (string or enum) under Last must carry forward across empty buckets just like
        // a numeric one, without rendering a spurious gap.
        //
        // Arrange: a string-valued property changed at minute 5 and never again. The persistent store
        // holds that Json sample and covers [0,40); the live store covers [40,60) but holds nothing.
        var held = JsonSerializer.SerializeToElement("active");
        var persistent = new FakeHistoryStore
        {
            Priority = 50,
            CurrentCoverage = new HistoryCoverage(At(0), At(40))
        }.AddJsonSample(At(5), held);
        var live = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(40), At(60))
        };
        var query = new HistoryQuery("temp", At(40), At(60), TimeSpan.FromMinutes(10), HistoryAggregations.Last);

        // Act
        var series = await new[] { persistent, live }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert: every live-edge bucket carries the held Json value forward (no gap), and the carry
        // seed entering the live segment was the held Json sample resolved cross-store.
        Assert.NotEmpty(series.Points);
        Assert.All(series.Points, point =>
        {
            Assert.Null(point.Number);
            Assert.Equal("active", point.Json?.GetString());
        });
        Assert.Equal("active", live.ReceivedQueries[0].CarrySeed?.Json?.GetString());
    }

    // Eligibility -----------------------------------------------------------------------------

    [Fact]
    public async Task WhenCoverageIsNarrowerThanRange_ThenCoveredTailReturnedWithoutThrow()
    {
        // The user's scenario: a single in-memory store with a narrow recent coverage window
        // ([now-60s, now]) is queried for a carry-dependent aggregation (TimeWeightedAverage) over a
        // far wider range ([now-24h, now]). A coverage shortfall must NOT be reported as an
        // aggregation-not-supported error. The covered recent tail is returned and the older,
        // uncovered range is simply absent (the documented offline-tail behavior).
        //
        // Arrange
        var now = At(24 * 60);
        var bucket = TimeSpan.FromSeconds(30);
        var coverageStart = now.AddSeconds(-60);
        var store = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(coverageStart, now)
        }.AddSample(now.AddSeconds(-45), 20).AddSample(now.AddSeconds(-15), 22);
        var query = new HistoryQuery(
            "temp", now.AddHours(-24), now, bucket, HistoryAggregations.TimeWeightedAverage);

        // Act: must not throw despite the 24h range exceeding the 60s coverage window.
        var series = await new[] { store }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert: only the covered recent tail was dispatched. The requested bucket grid is complete,
        // with explicit null gaps before the covered tail.
        Assert.NotEmpty(series.Points);
        Assert.Equal(2880, series.Points.Length);
        Assert.All(series.Points[..^2], point => Assert.Null(point.Number));
        Assert.Equal([20d, 22d], series.Points[^2..].Select(point => point.Number));
        Assert.Single(store.ReceivedQueries);
        Assert.Equal(coverageStart, store.ReceivedQueries[0].From);
        Assert.Equal(now, store.ReceivedQueries[0].To);
    }

    [Fact]
    public async Task WhenAggregationSupportedButCoverageFallsShort_ThenCoveredBucketsReturnedWithoutThrow()
    {
        // A store DOES support the requested aggregation but covers only part of the range. This is a
        // coverage shortfall, not a capability gap, so it must not throw: the supported-and-covered
        // buckets are returned and the rest is absent. Here the Minimum-capable store covers [0,30);
        // the other store covers [30,60) but supports only SampleAverage, so the [30,60) buckets are
        // unowned for Minimum (honest gaps).
        //
        // Arrange
        var minStore = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(0), At(30)),
            SupportedAggregations = Only(HistoryAggregations.Minimum, HistoryAggregations.Maximum)
        }.AddSample(At(5), 1);
        var otherStore = new FakeHistoryStore
        {
            Priority = 50,
            CurrentCoverage = new HistoryCoverage(At(30), At(60)),
            SupportedAggregations = Only(HistoryAggregations.SampleAverage)
        };
        var query = new HistoryQuery("temp", At(0), At(60), TimeSpan.FromMinutes(10), HistoryAggregations.Minimum);

        // Act: no throw; only the Minimum-capable covered part is served.
        var series = await new[] { minStore, otherStore }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert: the Minimum store served [0,30); the SampleAverage-only store was not dispatched.
        // The unowned [30,60) buckets remain explicit null gaps.
        Assert.Single(minStore.ReceivedQueries);
        Assert.Equal(At(0), minStore.ReceivedQueries[0].From);
        Assert.Equal(At(30), minStore.ReceivedQueries[0].To);
        Assert.Empty(otherStore.ReceivedQueries);
        Assert.Equal(6, series.Points.Length);
        Assert.All(series.Points[3..], point => Assert.Null(point.Number));
    }

    [Fact]
    public async Task WhenStoreSetIsEmpty_ThenNonUniversalAggregationThrows()
    {
        // Arrange
        var query = new HistoryQuery("temp", At(0), At(60), TimeSpan.FromMinutes(10), HistoryAggregations.Minimum);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<HistoryAggregationNotSupportedException>(
            () => Array.Empty<IHistoryStore>().QueryHistoryAsync(query, CancellationToken.None));
        Assert.Empty(exception.Available);
    }

    [Fact]
    public async Task WhenLeadingBucketStartsBeforeCoverage_ThenItIsAnHonestGapButTheTrailingOneIsServed()
    {
        // The bucketed planner enumerates the bucket-aligned span, not the raw [From,To). With 10-minute
        // buckets and From/To at minutes 5 and 25, the aligned buckets are [0,10), [10,20), [20,30).
        // A store edge-tight to [5,25) does not cover the leading bucket, which genuinely reaches back
        // before the store has data. The trailing bucket only spills past To, and both ownership and
        // the dispatched range are clipped to To, so it is served from the data that does exist.
        //
        // Arrange: a single Minimum-capable store covering exactly the non-aligned [5,25).
        var store = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(5), At(25)),
            SupportedAggregations = Only(HistoryAggregations.Minimum)
        }.AddSample(At(10), 1);
        var query = new HistoryQuery("temp", At(5), At(25), TimeSpan.FromMinutes(10), HistoryAggregations.Minimum);

        // Act: a coverage shortfall is not a capability gap, so this must not throw.
        var series = await new[] { store }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert: the two owned buckets coalesce into one sub-query; the leading bucket is a null gap.
        Assert.Single(store.ReceivedQueries);
        Assert.Equal(At(10), store.ReceivedQueries[0].From);
        Assert.Equal(At(25), store.ReceivedQueries[0].To);
        Assert.Equal(3, series.Points.Length);
        Assert.Null(series.Points[0].Number);
        Assert.Equal(At(10), series.Points[1].Timestamp);
        Assert.Equal(1d, series.Points[1].Number);
    }

    [Fact]
    public async Task WhenQueryEndsAtNow_ThenTheNewestBucketIsStillServed()
    {
        // The live edge: a chart asks for [now-3h, now) with 1-hour buckets, so the newest bucket runs
        // past To, and a live store's coverage ends at "now" and cannot reach into the future. Testing
        // ownership against the unclipped bucket left it unowned, blanking the current hour on every
        // chart even though its samples were recorded.
        //
        // Arrange
        var now = Origin.AddHours(13).AddMinutes(37).AddSeconds(12);
        var store = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(now.AddHours(-4), now.AddMilliseconds(5))
        }.AddSample(now.AddMinutes(-90), 1).AddSample(now.AddMinutes(-20), 2);
        var query = new HistoryQuery(
            "temp", now.AddHours(-3), now, TimeSpan.FromHours(1), HistoryAggregations.Last, MaxPoints: 100);

        // Act
        var series = await new[] { store }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert: the newest bucket carries the sample recorded 20 minutes ago.
        Assert.Equal(2d, series.Points[^1].Number);
    }

    [Fact]
    public async Task WhenBudgetDropsOlderSegments_ThenCoverageOnlyClaimsWhatWasServed()
    {
        // Arrange: two disjoint stores, budget enough for only the newest.
        var older = new FakeHistoryStore
        {
            Priority = 50,
            CurrentCoverage = new HistoryCoverage(At(0), At(30))
        }.AddSample(At(10), 1).AddSample(At(20), 2);
        var newer = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(30), At(60))
        }.AddSample(At(40), 3).AddSample(At(50), 4);
        var query = new HistoryQuery("temp", At(0), At(60), Aggregation: HistoryAggregations.Last, MaxPoints: 2);

        // Act
        var series = await new[] { older, newer }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert: the dropped older range must not be reported as covered, or a caller reads its
        // absent points as "the value did not change" rather than "this was never read".
        Assert.Empty(older.ReceivedQueries);
        Assert.Equal(new[] { new HistoryCoverage(At(30), At(60)) }, series.CoverageRanges.ToArray());
    }

    [Fact]
    public async Task WhenAggregationIsUniversal_ThenEligibilityCheckIsSkipped()
    {
        // Arrange: a store that supports NOTHING but is still asked for a universal aggregation (Count).
        var store = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(0), At(60)),
            SupportedAggregations = Only()
        }.AddSample(At(10), 1);
        var query = new HistoryQuery("temp", At(0), At(60), TimeSpan.FromMinutes(10), HistoryAggregations.Count);

        // Act: no eligibility exception is thrown even though the store advertises no aggregations.
        var series = await new[] { store }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert
        Assert.NotNull(series);
        Assert.Single(store.ReceivedQueries);
    }

    // Multi-path fan-out ----------------------------------------------------------------------

    [Fact]
    public async Task WhenQueryingMultiplePaths_ThenOneSeriesPerPathInOrder()
    {
        // Arrange
        var store = new FakeHistoryStore
        {
            Priority = 100,
            CurrentCoverage = new HistoryCoverage(At(0), At(60))
        }.AddSample(At(10), 1);
        var paths = new[] { "temperature", "humidity", "pressure" };

        // Act
        var seriesList = await new[] { store }.QueryHistoryAsync(
            paths, At(0), At(60), bucket: null, HistoryAggregations.Last, maxPoints: 1000, CancellationToken.None);

        // Assert
        Assert.Equal(3, seriesList.Count);
        Assert.Equal(new[] { "temperature", "humidity", "pressure" }, seriesList.Select(series => series.PropertyPath));
    }

    [Fact]
    public async Task WhenQueryingMultiplePathsViaEmptyStores_ThenReturnsEmptySeriesPerPath()
    {
        // Arrange
        var paths = new[] { "a", "b" };

        // Act
        var seriesList = await Array.Empty<IHistoryStore>().QueryHistoryAsync(
            paths, At(0), At(60), bucket: null, HistoryAggregations.Last, maxPoints: 1000, CancellationToken.None);

        // Assert
        Assert.Equal(2, seriesList.Count);
        Assert.All(seriesList, series => Assert.Empty(series.Points));
    }
}
