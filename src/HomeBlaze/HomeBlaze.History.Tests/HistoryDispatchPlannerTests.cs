using System.Collections.Immutable;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Tests;

public sealed class HistoryDispatchPlannerTests
{
    /// <summary>
    /// The carry-threaded executor serves every planned segment without budget accounting, which is
    /// only safe because the bucketed plan cannot exceed the point budget: PlanBucketed walks the grid
    /// from BucketAlignment.FirstBucketStart, already clipped to the newest MaxPoints buckets. If that
    /// ever stops holding, the executor would silently over-serve and blow the budget, so the
    /// invariant is asserted here rather than left as a comment.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    public void WhenTheRangeHasMoreBucketsThanTheBudget_ThenThePlanStaysWithinTheBudget(int maxPoints)
    {
        // Arrange - a day at one-minute buckets is 1440 buckets, split across two alternating owners
        // so the plan is many segments rather than one.
        var from = new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(1);
        var query = new HistoryQuery(
            "/Sensor/Temperature", from, to, TimeSpan.FromMinutes(1), HistoryAggregations.Last, maxPoints);

        var high = new FakeHistoryStore { Priority = 100, CoverageRanges = Alternating(from, to, offset: 0) };
        var low = new FakeHistoryStore { Priority = 50, CoverageRanges = Alternating(from, to, offset: 1) };
        var snapshots = new[]
        {
            new StoreCoverageSnapshot(high, high.CoverageRanges),
            new StoreCoverageSnapshot(low, low.CoverageRanges)
        };

        // Act
        var segments = HistoryDispatchPlanner.PlanBucketed(snapshots, query);

        // Assert
        Assert.True(segments.Count > 0);
        Assert.True(segments.Sum(segment => segment.BucketCount) <= maxPoints);
    }

    // Coverage over every other 10-minute slot, so neither store owns the whole range.
    private static ImmutableArray<HistoryCoverage> Alternating(DateTimeOffset from, DateTimeOffset to, int offset)
    {
        var slot = TimeSpan.FromMinutes(10);
        var builder = ImmutableArray.CreateBuilder<HistoryCoverage>();
        var index = 0;
        for (var start = from; start < to; start += slot, index++)
        {
            if (index % 2 == offset)
            {
                builder.Add(new HistoryCoverage(start, start + slot));
            }
        }

        return builder.ToImmutable();
    }
}
