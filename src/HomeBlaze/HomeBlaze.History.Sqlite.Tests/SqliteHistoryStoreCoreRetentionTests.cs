using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Sqlite;

namespace HomeBlaze.History.Sqlite.Tests;

public sealed class SqliteHistoryStoreCoreRetentionTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-" + Guid.NewGuid().ToString("N"));

    private SqliteHistoryStore NewCore(DateTimeOffset now, int maxAgeSeconds = 3600) =>
        new(priority: 50, databaseDirectory: _directory, PartitionInterval.Weekly, TimeSpan.FromSeconds(maxAgeSeconds), maxJsonSize: 8192, () => now);

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { /* best effort temp cleanup */ }
    }

    [Fact]
    public async Task WhenSamplesSpanTwoPartitions_ThenRawQueryUnionsThemAscending()
    {
        // Arrange - two timestamps in different weeks
        using var core = NewCore(now: Base.AddDays(20), maxAgeSeconds: 60 * 60 * 24 * 365);
        core.Record("/a/V", Base.AddDays(0), 1d, typeof(double));
        core.Record("/a/V", Base.AddDays(10), 2d, typeof(double)); // a later week
        await core.FlushAsync(CancellationToken.None);

        // Act
        var series = core.Query(new HistoryQuery("/a/V", Base.AddDays(-1), Base.AddDays(11)));

        // Assert - both partitions contribute, ascending
        Assert.Equal(new double?[] { 1, 2 }, series.Points.Select(p => p.Number).ToArray());
    }

    [Fact]
    public async Task WhenPartitionOlderThanMaxAge_ThenSweepDeletesFileAndAdvancesCoverageFrom()
    {
        // Arrange - one old-week sample, one recent; MaxAge ~ 8 days
        var now = Base;
        using var core = new SqliteHistoryStore(
            priority: 50,
            databaseDirectory: _directory,
            PartitionInterval.Weekly,
            TimeSpan.FromDays(8),
            maxJsonSize: 8192,
            getUtcNow: () => now);
        core.Record("/a/V", Base.AddDays(0), 1d, typeof(double));   // older than now-8d -> swept
        core.Record("/a/V", Base.AddDays(18), 2d, typeof(double));  // retained
        now = Base.AddDays(20);
        await core.FlushAsync(CancellationToken.None);

        // Act
        core.Sweep();
        var series = core.Query(new HistoryQuery("/a/V", Base.AddDays(-1), now));

        // Assert - old partition file gone; only the recent sample remains; coverage advanced past the old week
        Assert.Equal(new double?[] { 2 }, series.Points.Select(p => p.Number).ToArray());
        var coverage = Assert.Single(core.CoverageRanges);
        Assert.Equal(Base.AddDays(12), coverage.From);

        // A later flush must extend the active range without restoring its pre-retention start.
        core.Record("/a/V", Base.AddDays(20).AddHours(1), 3d, typeof(double));
        now = Base.AddDays(21);
        await core.FlushAsync(CancellationToken.None);
        Assert.Equal(Base.AddDays(12), Assert.Single(core.CoverageRanges).From);

        using var reopened = new SqliteHistoryStore(
            priority: 50,
            databaseDirectory: _directory,
            PartitionInterval.Weekly,
            TimeSpan.FromDays(8),
            maxJsonSize: 8192,
            getUtcNow: () => now);
        Assert.Equal(Base.AddDays(12), Assert.Single(reopened.CoverageRanges).From);
    }
}
