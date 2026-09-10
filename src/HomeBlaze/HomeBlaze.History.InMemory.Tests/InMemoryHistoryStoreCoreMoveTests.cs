using HomeBlaze.History.Abstractions;
using HomeBlaze.History.InMemory;

namespace HomeBlaze.History.InMemory.Tests;

public class InMemoryHistoryStoreCoreMoveTests
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private static InMemoryHistoryStore NewCore() =>
        new(priority: 100, maxPointsPerProperty: 1000, maxAge: TimeSpan.FromHours(1),
            maxJsonSize: 8192, getUtcNow: () => Base.AddHours(1));

    private static InMemoryHistoryStore NewCore(Func<DateTimeOffset> getUtcNow) =>
        new(priority: 100, maxPointsPerProperty: 1000, maxAge: TimeSpan.FromHours(1),
            maxJsonSize: 8192, getUtcNow);

    [Fact]
    public void WhenMovesArriveOutOfOrder_ThenSweepKeepsTheOnesInsideRetention()
    {
        // Arrange - move timestamps come from the recorded change, not from arrival, so a device
        // reporting late appends an older move after a newer one. The retention prune used to walk the
        // list from the front assuming it was sorted, so it stopped at the first entry above the cutoff
        // and deleted every newer move sitting before it.
        var core = NewCore(); // now is Base + 1h against a 1h retention, so the cutoff is Base
        core.Record("/a/Value", Base.AddMinutes(10), 1d, typeof(double));
        core.RecordMove(Base.AddMinutes(30), "/a/Value", "/b/Value");  // inside retention, must survive
        core.RecordMove(Base.AddMinutes(-30), "/x/Value", "/y/Value"); // late arrival from before it

        // Act
        core.Sweep();

        // Assert - the surviving move still routes /b back to its pre-move samples at /a.
        var series = core.Query(new HistoryQuery("/b/Value", Base, Base.AddHours(1)));
        Assert.Equal(new double?[] { 1d }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public void WhenPropertyMovedOnce_ThenRawQueryFollowsChainAcrossPaths()
    {
        // Arrange - recorded at /old until t=10, moved to /new at t=10, recorded at /new after.
        var core = NewCore();
        core.Record("/old/Value", Base.AddSeconds(2), 1d, typeof(double));
        core.Record("/old/Value", Base.AddSeconds(8), 2d, typeof(double));
        core.RecordMove(Base.AddSeconds(10), "/old/Value", "/new/Value");
        core.Record("/new/Value", Base.AddSeconds(12), 3d, typeof(double));

        // Act - query the current path; chain resolution pulls the pre-move samples too
        var series = core.Query(new HistoryQuery("/new/Value", Base, Base.AddSeconds(20)));

        // Assert - all three samples, ascending, under the queried path
        Assert.Equal("/new/Value", series.PropertyPath);
        Assert.Equal(new double?[] { 1, 2, 3 }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public void WhenMovedTwice_ThenChainResolvesAllLegs()
    {
        // Arrange - /a -> /b at t=10 -> /c at t=20
        var core = NewCore();
        core.Record("/a/V", Base.AddSeconds(5), 1d, typeof(double));
        core.RecordMove(Base.AddSeconds(10), "/a/V", "/b/V");
        core.Record("/b/V", Base.AddSeconds(15), 2d, typeof(double));
        core.RecordMove(Base.AddSeconds(20), "/b/V", "/c/V");
        core.Record("/c/V", Base.AddSeconds(25), 3d, typeof(double));

        // Act
        var series = core.Query(new HistoryQuery("/c/V", Base, Base.AddSeconds(30)));

        // Assert
        Assert.Equal(new double?[] { 1, 2, 3 }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public void WhenChainHasCycle_ThenResolutionTerminates()
    {
        // Arrange - pathological A->B->A; visited set must stop the walk
        var core = NewCore();
        core.RecordMove(Base.AddSeconds(10), "/a/V", "/b/V");
        core.RecordMove(Base.AddSeconds(20), "/b/V", "/a/V");
        core.Record("/a/V", Base.AddSeconds(25), 9d, typeof(double));

        // Act - must not loop forever
        var series = core.Query(new HistoryQuery("/a/V", Base, Base.AddSeconds(30)));

        // Assert - terminates and returns the post-cycle sample
        Assert.Contains(series.Points, point => point.Number == 9d);
    }

    [Fact]
    public void WhenGetSampleAtOrBeforeAcrossMove_ThenFollowsChain()
    {
        // Arrange - value recorded only at the old path, then moved
        var now = Base;
        var core = NewCore(() => now);
        core.Record("/old/V", Base.AddSeconds(5), 42d, typeof(double));
        core.RecordMove(Base.AddSeconds(10), "/old/V", "/new/V");
        now = Base.AddSeconds(20);

        // Act - ask the current path for the value held at t=8 (only the old path has it)
        var held = core.GetSampleAtOrBefore("/new/V", Base.AddSeconds(8));

        // Assert
        Assert.Equal(42d, held!.Number);
    }

    [Fact]
    public void WhenNoMoves_ThenQueryUnaffected()
    {
        // Arrange
        var core = NewCore();
        core.Record("/a/V", Base.AddSeconds(1), 5d, typeof(double));

        // Act
        var series = core.Query(new HistoryQuery("/a/V", Base, Base.AddSeconds(10)));

        // Assert
        Assert.Equal(new double?[] { 5 }, series.Points.Select(point => point.Number).ToArray());
    }
}
