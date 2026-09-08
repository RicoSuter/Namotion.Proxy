using Microsoft.Data.Sqlite;
using System.Text.Json;
using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Sqlite;

namespace HomeBlaze.History.Sqlite.Tests;

public sealed class SqliteHistoryStoreCoreOversizeAndMetricsTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-" + Guid.NewGuid().ToString("N"));

    private SqliteHistoryStore NewCore(int maxJsonSize = 8192) =>
        new(priority: 50, databaseDirectory: _directory, PartitionInterval.Weekly, TimeSpan.FromDays(365), maxJsonSize, () => Base.AddHours(1));

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { /* best effort temp cleanup */ }
        try { if (File.Exists(_directory)) File.Delete(_directory); }
        catch { /* the failure-injection test replaces the directory with a file */ }
    }

    [Fact]
    public async Task WhenStringExceedsMaxJsonSize_ThenPlaceholderStoredAndCounted()
    {
        // Arrange - cap at 16 chars; record a 100-char string
        using var core = NewCore(maxJsonSize: 16);
        var big = new string('x', 100);
        core.Record("/a/Name", Base.AddSeconds(1), big, typeof(string));
        await core.FlushAsync(CancellationToken.None);

        // Act
        var point = core.Query(new HistoryQuery("/a/Name", Base, Base.AddSeconds(10))).Points.Single();

        // Assert - placeholder object read back, OversizeCount incremented
        Assert.Equal(JsonValueKind.Object, point.Json!.Value.ValueKind);
        Assert.True(point.Json!.Value.GetProperty("$oversize").GetBoolean());
        Assert.True(point.Json!.Value.GetProperty("size").GetInt32() >= 100);
        Assert.Equal(1, core.OversizeCount);
    }

    [Fact]
    public async Task WhenStringWithinCap_ThenStoredVerbatimAndNotCounted()
    {
        // Arrange
        using var core = NewCore(maxJsonSize: 1024);
        core.Record("/a/Name", Base.AddSeconds(1), "small", typeof(string));
        await core.FlushAsync(CancellationToken.None);

        // Act
        var point = core.Query(new HistoryQuery("/a/Name", Base, Base.AddSeconds(10))).Points.Single();

        // Assert
        Assert.Equal("small", point.Json!.Value.GetString());
        Assert.Equal(0, core.OversizeCount);
    }

    [Fact]
    public async Task WhenSamplesRecorded_ThenCountMetricsReflectThem()
    {
        // Arrange
        using var core = NewCore();
        core.Record("/a/V", Base.AddSeconds(1), 1d, typeof(double));
        core.Record("/a/V", Base.AddSeconds(2), 2d, typeof(double));
        core.Record("/b/V", Base.AddSeconds(1), 3d, typeof(double));

        // Act & Assert - RecordedCount counts routed samples; QueueDepth reflects pending before flush
        Assert.Equal(3, core.RecordedCount);
        Assert.Equal(3, core.QueueDepth);

        await core.FlushAsync(CancellationToken.None);

        // QueueDepth drains to zero; a partition file exists so EstimatedStorageBytes is positive
        Assert.Equal(0, core.QueueDepth);
        Assert.True(core.EstimatedStorageBytes > 0);
    }

    [Fact]
    public async Task WhenFlushThrows_ThenPendingSamplesAreRetainedForRetry()
    {
        // Arrange - construct the core (its directory is created), then replace the directory with a
        // FILE at the same path. OpenPartition opens "Data Source=<file>\<key>.db", which cannot open
        // because its parent is a file, so the flush write throws deterministically on Windows.
        using var core = NewCore();
        core.Record("/a/V", Base.AddSeconds(1), 1d, typeof(double));
        core.Record("/a/V", Base.AddSeconds(2), 2d, typeof(double));
        Directory.Delete(_directory, recursive: true);
        await File.WriteAllTextAsync(_directory, "collision");

        // Act & Assert - the flush throws and records the error, but does NOT drop the batch
        await Assert.ThrowsAnyAsync<SqliteException>(() => core.FlushAsync(CancellationToken.None));
        Assert.NotNull(core.LastError);
        Assert.Equal(2, core.QueueDepth);

        // Replace the colliding file with a real directory so a subsequent flush can persist the batch
        File.Delete(_directory);
        Directory.CreateDirectory(_directory);
        await core.FlushAsync(CancellationToken.None);

        Assert.Equal(0, core.QueueDepth);
        Assert.Null(core.LastError);
        var series = core.Query(new HistoryQuery("/a/V", Base, Base.AddSeconds(10)));
        Assert.Equal(new double?[] { 1, 2 }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenFlushThrows_ThenPendingMovesAreRetainedForRetry()
    {
        // Arrange - queue a move and a sample, then collide the directory with a file (see above).
        using var core = NewCore();
        core.Record("/a/V", Base.AddSeconds(1), 1d, typeof(double));
        core.RecordMove(Base.AddSeconds(2), "/a/V", "/b/V");
        Directory.Delete(_directory, recursive: true);
        await File.WriteAllTextAsync(_directory, "collision");

        // Act & Assert - the flush throws; the move is not dropped, so the later read sees it
        await Assert.ThrowsAnyAsync<SqliteException>(() => core.FlushAsync(CancellationToken.None));

        File.Delete(_directory);
        Directory.CreateDirectory(_directory);
        await core.FlushAsync(CancellationToken.None);

        // The move re-routes the sample recorded under /a/V to the queried current path /b/V.
        Assert.Equal(0, core.QueueDepth);
        var series = core.Query(new HistoryQuery("/b/V", Base, Base.AddSeconds(10)));
        Assert.Equal(new double?[] { 1 }, series.Points.Select(point => point.Number).ToArray());
    }
}
