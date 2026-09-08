using System.Text.Json;
using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Sqlite;

namespace HomeBlaze.History.Sqlite.Tests;

public sealed class SqliteHistoryStoreCoreRawTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-" + Guid.NewGuid().ToString("N"));

    private SqliteHistoryStore NewCore(DateTimeOffset now, int maxAgeSeconds = 3600) =>
        new(priority: 50, databaseDirectory: _directory, PartitionInterval.Weekly, TimeSpan.FromSeconds(maxAgeSeconds), maxJsonSize: 8192, () => now);

    private SqliteHistoryStore NewCore(Func<DateTimeOffset> getUtcNow, int maxAgeSeconds = 3600) =>
        new(priority: 50, databaseDirectory: _directory, PartitionInterval.Weekly,
            TimeSpan.FromSeconds(maxAgeSeconds), maxJsonSize: 8192, getUtcNow);

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { /* best effort temp cleanup */ }
    }

    [Fact]
    public async Task WhenDoublesRecordedAndFlushed_ThenRawQueryReturnsNumbersAscending()
    {
        // Arrange
        using var core = NewCore(Base.AddSeconds(10));
        core.Record("/a/Value", Base.AddSeconds(0), 1.5d, typeof(double));
        core.Record("/a/Value", Base.AddSeconds(1), 2.5d, typeof(double));
        await core.FlushAsync(CancellationToken.None);

        // Act
        var series = core.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(10)));

        // Assert
        Assert.False(series.Truncated);
        Assert.Equal(new double?[] { 1.5, 2.5 }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenNotYetFlushed_ThenQueryReturnsEmpty()
    {
        // Arrange - flushed data only; pending samples are InMemory's responsibility at the live edge
        using var core = NewCore(Base.AddSeconds(10));
        core.Record("/a/Value", Base.AddSeconds(0), 1.5d, typeof(double));

        // Act
        var series = core.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(10)));

        // Assert
        Assert.Empty(series.Points);
    }

    [Fact]
    public async Task WhenDecimalRecorded_ThenStoredAsNumberForCharting()
    {
        // Arrange - decimal routes to the numeric (double) column so the chart and aggregations see a number.
        using var core = NewCore(Base.AddSeconds(10));
        core.Record("/a/Temperature", Base.AddSeconds(0), 0.1m, typeof(decimal));
        await core.FlushAsync(CancellationToken.None);

        // Act
        var point = core.Query(new HistoryQuery("/a/Temperature", Base, Base.AddSeconds(10))).Points.Single();

        // Assert - surfaced as Number (graphable); the exact decimal is archived in value_json but not surfaced.
        Assert.Equal((double)0.1m, point.Number);
        Assert.Null(point.Json);
    }

    [Fact]
    public async Task WhenLongBoolStringEnumNullRecorded_ThenColumnsRoutedLikeInMemory()
    {
        // Arrange
        using var core = NewCore(Base.AddSeconds(10));
        core.Record("/a/Count", Base.AddSeconds(0), 42L, typeof(long));
        core.Record("/a/Flag", Base.AddSeconds(0), true, typeof(bool));
        core.Record("/a/Name", Base.AddSeconds(0), "hello", typeof(string));
        core.Record("/a/Mode", Base.AddSeconds(0), DayOfWeek.Friday, typeof(DayOfWeek));
        core.Record("/a/Maybe", Base.AddSeconds(0), null, typeof(double?));
        await core.FlushAsync(CancellationToken.None);

        // Act
        HistoryPoint Q(string path) => core.Query(new HistoryQuery(path, Base, Base.AddSeconds(10))).Points.Single();

        // Assert
        Assert.Equal(42d, Q("/a/Count").Number);
        Assert.Equal(1d, Q("/a/Flag").Number);
        Assert.Equal("hello", Q("/a/Name").Json!.Value.GetString());
        Assert.Equal("Friday", Q("/a/Mode").Json!.Value.GetString());
        var nullPoint = Q("/a/Maybe");
        Assert.Null(nullPoint.Number);
        Assert.Null(nullPoint.Json);
    }

    [Fact]
    public async Task WhenMorePointsThanBudget_ThenNewestKeptAndTruncatedSet()
    {
        // Arrange
        using var core = NewCore(Base.AddSeconds(100));
        for (var i = 0; i < 5; i++) core.Record("/a/Value", Base.AddSeconds(i), (double)i, typeof(double));
        await core.FlushAsync(CancellationToken.None);

        // Act
        var series = core.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(100), MaxPoints: 2));

        // Assert
        Assert.True(series.Truncated);
        Assert.Equal(new double?[] { 3, 4 }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenGetSampleAtOrBefore_ThenReturnsHeldValueOrNull()
    {
        // Arrange
        var now = Base;
        using var core = NewCore(() => now);
        core.Record("/a/Value", Base.AddSeconds(0), 7d, typeof(double));
        core.Record("/a/Value", Base.AddSeconds(5), 9d, typeof(double));
        now = Base.AddSeconds(10);
        await core.FlushAsync(CancellationToken.None);

        // Act
        var held = core.GetSampleAtOrBefore("/a/Value", Base.AddSeconds(3));
        var beforeAll = core.GetSampleAtOrBefore("/a/Value", Base.AddSeconds(-1));

        // Assert
        Assert.Equal(7d, held!.Number);
        Assert.Null(beforeAll);
    }

    [Fact]
    public async Task WhenFlushed_ThenCoverageToTracksLastCommittedAndQueueDepthZero()
    {
        // Arrange
        var now = Base.AddSeconds(80);
        using var core = NewCore(() => now);
        core.Record("/a/Value", Base.AddSeconds(90), 1d, typeof(double));
        now = Base.AddSeconds(120);

        // Act
        Assert.Equal(1, core.QueueDepth);
        await core.FlushAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, core.QueueDepth);
        var coverage = Assert.Single(core.CoverageRanges);
        Assert.Equal(Base.AddSeconds(80), coverage.From);
        Assert.Equal(Base.AddSeconds(120).AddTicks(1), coverage.To);
        Assert.True(core.LastFlushUtc is not null);
    }

    [Fact]
    public async Task WhenStoreReopened_ThenCoverageIsRestoredFromPersistedSamples()
    {
        // Arrange
        var now = Base.AddSeconds(80);
        using (var writer = NewCore(() => now))
        {
            writer.Record("/a/Value", Base.AddSeconds(90), 1d, typeof(double));
            now = Base.AddSeconds(120);
            await writer.FlushAsync(CancellationToken.None);
        }

        // Act
        using var reader = NewCore(Base.AddSeconds(180));
        var coverage = Assert.Single(reader.CoverageRanges);
        var series = reader.Query(new HistoryQuery("/a/Value", coverage.From, coverage.To));

        // Assert
        Assert.Equal(Base.AddSeconds(80), coverage.From);
        Assert.Equal(Base.AddSeconds(120).AddTicks(1), coverage.To);
        Assert.Equal(1d, series.Points.Single().Number);
    }
}
