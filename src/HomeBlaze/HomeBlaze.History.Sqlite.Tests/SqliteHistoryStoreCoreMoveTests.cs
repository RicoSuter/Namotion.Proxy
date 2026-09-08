using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Sqlite;

namespace HomeBlaze.History.Sqlite.Tests;

public sealed class SqliteHistoryStoreCoreMoveTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-move-" + Guid.NewGuid().ToString("N"));

    private SqliteHistoryStore NewCore() =>
        new(priority: 50, databaseDirectory: _directory, PartitionInterval.Weekly, TimeSpan.FromHours(1), maxJsonSize: 8192,
            () => Base.AddHours(1));

    private SqliteHistoryStore NewCore(Func<DateTimeOffset> getUtcNow) =>
        new(priority: 50, databaseDirectory: _directory, PartitionInterval.Weekly,
            TimeSpan.FromHours(1), maxJsonSize: 8192, getUtcNow);

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { /* best effort temp cleanup */ }
    }

    [Fact]
    public async Task WhenPropertyMovedOnce_ThenRawQueryFollowsChainAcrossPaths()
    {
        // Arrange - recorded at /old until t=10, moved to /new at t=10, recorded at /new after.
        using var core = NewCore();
        core.Record("/old/Value", Base.AddSeconds(2), 1d, typeof(double));
        core.Record("/old/Value", Base.AddSeconds(8), 2d, typeof(double));
        core.RecordMove(Base.AddSeconds(10), "/old/Value", "/new/Value");
        core.Record("/new/Value", Base.AddSeconds(12), 3d, typeof(double));
        await core.FlushAsync(CancellationToken.None);

        // Act - query the current path; chain resolution pulls the pre-move samples too
        var series = core.Query(new HistoryQuery("/new/Value", Base, Base.AddSeconds(20)));

        // Assert - all three samples, ascending, under the queried path
        Assert.Equal("/new/Value", series.PropertyPath);
        Assert.Equal(new double?[] { 1, 2, 3 }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenStoreReopens_ThenPersistedMoveChainIsResolved()
    {
        // Arrange
        var now = Base;
        using (var first = NewCore(() => now))
        {
            first.Record("/old/Value", Base.AddSeconds(5), 1d, typeof(double));
            first.RecordMove(Base.AddSeconds(10), "/old/Value", "/new/Value");
            first.Record("/new/Value", Base.AddSeconds(15), 2d, typeof(double));
            now = Base.AddSeconds(20);
            await first.FlushAsync(CancellationToken.None);
        }

        now = Base.AddSeconds(30);
        using var reopened = NewCore(() => now);

        // Act
        var series = reopened.Query(new HistoryQuery("/new/Value", Base, Base.AddSeconds(20)));

        // Assert
        Assert.Equal(new double?[] { 1, 2 }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenMovedTwice_ThenChainResolvesAllLegs()
    {
        // Arrange - /a -> /b at t=10 -> /c at t=20
        using var core = NewCore();
        core.Record("/a/V", Base.AddSeconds(5), 1d, typeof(double));
        core.RecordMove(Base.AddSeconds(10), "/a/V", "/b/V");
        core.Record("/b/V", Base.AddSeconds(15), 2d, typeof(double));
        core.RecordMove(Base.AddSeconds(20), "/b/V", "/c/V");
        core.Record("/c/V", Base.AddSeconds(25), 3d, typeof(double));
        await core.FlushAsync(CancellationToken.None);

        // Act
        var series = core.Query(new HistoryQuery("/c/V", Base, Base.AddSeconds(30)));

        // Assert
        Assert.Equal(new double?[] { 1, 2, 3 }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenChainHasCycle_ThenResolutionTerminates()
    {
        // Arrange - pathological A->B->A; visited set must stop the walk
        using var core = NewCore();
        core.RecordMove(Base.AddSeconds(10), "/a/V", "/b/V");
        core.RecordMove(Base.AddSeconds(20), "/b/V", "/a/V");
        core.Record("/a/V", Base.AddSeconds(25), 9d, typeof(double));
        await core.FlushAsync(CancellationToken.None);

        // Act - must not loop forever
        var series = core.Query(new HistoryQuery("/a/V", Base, Base.AddSeconds(30)));

        // Assert - terminates and returns the post-cycle sample
        Assert.Contains(series.Points, point => point.Number == 9d);
    }

    [Fact]
    public async Task WhenGetSampleAtOrBeforeAcrossMove_ThenFollowsChain()
    {
        // Arrange - value recorded only at the old path, then moved
        var now = Base;
        using var core = NewCore(() => now);
        core.Record("/old/V", Base.AddSeconds(5), 42d, typeof(double));
        core.RecordMove(Base.AddSeconds(10), "/old/V", "/new/V");
        now = Base.AddSeconds(20);
        await core.FlushAsync(CancellationToken.None);

        // Act - ask the current path for the value held at t=8 (only the old path has it)
        var held = core.GetSampleAtOrBefore("/new/V", Base.AddSeconds(8));

        // Assert
        Assert.Equal(42d, held!.Number);
    }

    [Fact]
    public async Task WhenNoMoves_ThenQueryUnaffected()
    {
        // Arrange
        using var core = NewCore();
        core.Record("/a/V", Base.AddSeconds(1), 5d, typeof(double));
        await core.FlushAsync(CancellationToken.None);

        // Act
        var series = core.Query(new HistoryQuery("/a/V", Base, Base.AddSeconds(10)));

        // Assert
        Assert.Equal(new double?[] { 5 }, series.Points.Select(point => point.Number).ToArray());
    }
}
