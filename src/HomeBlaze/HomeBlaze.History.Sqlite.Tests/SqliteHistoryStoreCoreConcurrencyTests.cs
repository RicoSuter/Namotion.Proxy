using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Sqlite;

namespace HomeBlaze.History.Sqlite.Tests;

/// <summary>
/// Regression tests for the connection data race: cached SqliteConnection instances are not
/// thread-safe, yet the merger runs per-path queries concurrently and the flush loop runs
/// concurrently with queries. These tests
/// hammer the core with many concurrent reads, flushes, and a sweep, asserting no exception and
/// consistent results.
/// </summary>
public sealed class SqliteHistoryStoreCoreConcurrencyTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Bucket = TimeSpan.FromSeconds(10);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-" + Guid.NewGuid().ToString("N"));

    // now sits just after the recorded data and maxAge is long, so the concurrent Sweep keeps both
    // partition files (cutoff = now - maxAge falls long before either partition's range).
    private SqliteHistoryStore NewCore()
    {
        var now = Base.AddDays(-1);
        var store = new SqliteHistoryStore(
            priority: 50,
            databaseDirectory: _directory,
            PartitionInterval.Weekly,
            TimeSpan.FromDays(3650),
            maxJsonSize: 8192,
            getUtcNow: () => now);
        now = Base.AddDays(14);
        return store;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { /* best effort temp cleanup */ }
    }

    [Fact]
    public async Task WhenManyConcurrentReadsAndFlushes_ThenNoExceptionAndResultsConsistent()
    {
        // Arrange - record a few hundred samples for two paths, spread across two weekly partitions so
        // the multi-partition ordered TWA path is exercised. Path /a/Value steps
        // 0..199 across week A, path /b/Value steps 0..199 across the next week (week B). A constant
        // sampling cadence makes the raw counts and bucket counts deterministic.
        const int sampleCount = 200;
        var weekA = Base; // Monday 2026-06-22 12:00
        var weekB = Base.AddDays(7); // next ISO week -> a different weekly partition file

        using var core = NewCore();
        for (var index = 0; index < sampleCount; index++)
        {
            core.Record("/a/Value", weekA.AddSeconds(index), (double)index, typeof(double));
            core.Record("/b/Value", weekB.AddSeconds(index), (double)(index * 2), typeof(double));
        }

        await core.FlushAsync(CancellationToken.None);

        var rawAQuery = new HistoryQuery("/a/Value", weekA, weekA.AddSeconds(sampleCount), MaxPoints: 10_000);
        var rawBQuery = new HistoryQuery("/b/Value", weekB, weekB.AddSeconds(sampleCount), MaxPoints: 10_000);

        var bucketedAQuery = new HistoryQuery("/a/Value", weekA, weekA.AddSeconds(sampleCount), Bucket,
            HistoryAggregations.Count, MaxPoints: 10_000);

        // A TWA query whose range spans both weekly partitions, forcing the merged event scan.
        var twaSpanningQuery = new HistoryQuery("/a/Value", weekA, weekB.AddSeconds(sampleCount),
            TimeSpan.FromDays(1), HistoryAggregations.TimeWeightedAverage, MaxPoints: 10_000);

        // Act - run a large mix of concurrent operations. Reads dominate; a couple of tasks flush
        // (no-op, since nothing is pending) and one sweeps, all touching the shared connections.
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var rawACounts = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var rawBCounts = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var bucketTotals = new System.Collections.Concurrent.ConcurrentQueue<long>();

        var tasks = new List<Task>();
        for (var worker = 0; worker < 80; worker++)
        {
            var which = worker;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    switch (which % 6)
                    {
                        case 0:
                            rawACounts.Enqueue(core.Query(rawAQuery).Points.Length);
                            break;
                        case 1:
                            rawBCounts.Enqueue(core.Query(rawBQuery).Points.Length);
                            break;
                        case 2:
                            var total = core.Query(bucketedAQuery).Points
                                .Sum(point => (long)(point.Number ?? 0));
                            bucketTotals.Enqueue(total);
                            break;
                        case 3:
                            // Spanning TWA: just assert it runs without error and yields points.
                            _ = core.Query(twaSpanningQuery).Points.Length;
                            break;
                        case 4:
                            core.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
                            break;
                        default:
                            core.Sweep();
                            break;
                    }
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Assert - no concurrent operation threw.
        Assert.True(failures.IsEmpty,
            "Concurrent operations threw: " + string.Join(" | ", failures.Select(e => e.Message)));

        // Every raw query saw the full deterministic sample set (no torn/short reads).
        Assert.All(rawACounts, count => Assert.Equal(sampleCount, count));
        Assert.All(rawBCounts, count => Assert.Equal(sampleCount, count));

        // Count is the number of samples; summed across buckets it must equal the total sample count.
        Assert.All(bucketTotals, total => Assert.Equal(sampleCount, total));

        // A final single-threaded query returns the expected data unchanged after the concurrent storm.
        var finalA = core.Query(rawAQuery);
        Assert.Equal(sampleCount, finalA.Points.Length);
        Assert.Equal(0d, finalA.Points[0].Number);
        Assert.Equal(sampleCount - 1, finalA.Points[^1].Number);
    }

    [Fact]
    public async Task WhenAnotherStoreIsDisposed_ThenOpenStoreRemainsQueryable()
    {
        // Arrange
        var otherDirectory =
            Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var openStore = NewCore();
            openStore.Record("/a/Value", Base, 1d, typeof(double));
            await openStore.FlushAsync(CancellationToken.None);

            using var otherStore = new SqliteHistoryStore(
                priority: 50,
                databaseDirectory: otherDirectory,
                PartitionInterval.Weekly,
                TimeSpan.FromDays(3650),
                maxJsonSize: 8192,
                getUtcNow: () => Base.AddDays(14));
            otherStore.Record("/b/Value", Base, 2d, typeof(double));
            await otherStore.FlushAsync(CancellationToken.None);

            // Act
            otherStore.Dispose();
            var series = openStore.Query(
                new HistoryQuery("/a/Value", Base, Base.AddTicks(1)));

            // Assert
            Assert.Equal(1d, series.Points.Single().Number);
        }
        finally
        {
            try
            {
                if (Directory.Exists(otherDirectory))
                {
                    Directory.Delete(otherDirectory, recursive: true);
                }
            }
            catch
            {
                // best effort temp cleanup
            }
        }
    }
}
