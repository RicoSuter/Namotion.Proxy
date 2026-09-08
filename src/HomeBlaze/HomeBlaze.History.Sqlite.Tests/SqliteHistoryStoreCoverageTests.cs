using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Sqlite;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.History.Sqlite.Tests;

public sealed class SqliteHistoryStoreCoverageTests : IDisposable
{
    private static readonly DateTimeOffset Base =
        new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-coverage-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // best effort temp cleanup
        }
    }

    [Fact]
    public async Task WhenStoreRestarts_ThenDowntimeIsPersistedAsCoverageGap()
    {
        // Arrange
        var now = Base;
        using (var first = NewStore(() => now))
        {
            first.Record("/a/Value", Base.AddSeconds(1), 1d, typeof(double));
            now = Base.AddSeconds(10);
            await first.FlushAsync(CancellationToken.None);
        }

        now = Base.AddSeconds(20);
        using (var second = NewStore(() => now))
        {
            Assert.Single(second.CoverageRanges);
            second.Record("/a/Value", Base.AddSeconds(21), 2d, typeof(double));
            now = Base.AddSeconds(30);
            await second.FlushAsync(CancellationToken.None);
        }

        // Act
        now = Base.AddSeconds(40);
        using var reopened = NewStore(() => now);

        // Assert
        Assert.Equal(
            new[]
            {
                new HistoryCoverage(Base, Base.AddSeconds(10).AddTicks(1)),
                new HistoryCoverage(Base.AddSeconds(20), Base.AddSeconds(30).AddTicks(1))
            },
            reopened.CoverageRanges.ToArray());

        var series = reopened.Query(new HistoryQuery(
            "/a/Value",
            Base,
            Base.AddSeconds(40),
            TimeSpan.FromSeconds(10),
            HistoryAggregations.Last,
            MaxPoints: 10));
        Assert.Equal(
            new double?[] { 1, null, 2, null },
            series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenOlderSampleIsPending_ThenVisibleCoverageRetractsUntilItIsDurable()
    {
        // Arrange
        var now = Base;
        using var store = NewStore(() => now);
        now = Base.AddSeconds(10);
        await store.FlushAsync(CancellationToken.None);

        // Act
        store.Record("/a/Value", Base.AddSeconds(5), 1d, typeof(double));

        // Assert
        Assert.Equal(Base.AddSeconds(5), Assert.Single(store.CoverageRanges).To);

        // Act
        now = Base.AddSeconds(20);
        await store.FlushAsync(CancellationToken.None);

        // Assert
        Assert.Equal(Base.AddSeconds(20).AddTicks(1), Assert.Single(store.CoverageRanges).To);
    }

    [Fact]
    public async Task WhenPendingLimitIsReached_ThenSamplesAreBoundedAndRecoveryStartsNewCoverageRange()
    {
        // Arrange
        var now = Base;
        using var store = NewStore(() => now, maxPendingSamples: 2);
        store.Record("/a/Value", Base.AddSeconds(1), 1d, typeof(double));
        store.Record("/a/Value", Base.AddSeconds(2), 2d, typeof(double));
        store.Record("/a/Value", Base.AddSeconds(3), 3d, typeof(double));

        // Act
        Assert.Equal(2, store.QueueDepth);
        now = Base.AddSeconds(10);
        await store.FlushAsync(CancellationToken.None);
        store.Record("/a/Value", Base.AddSeconds(11), 4d, typeof(double));
        now = Base.AddSeconds(20);
        await store.FlushAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, store.QueueDepth);
        Assert.Equal(1, store.DropCount);
        Assert.Equal(3, store.RecordedCount);
        Assert.Equal(
            new[]
            {
                new HistoryCoverage(Base, Base.AddSeconds(3)),
                new HistoryCoverage(Base.AddSeconds(10), Base.AddSeconds(20).AddTicks(1))
            },
            store.CoverageRanges.ToArray());
        var series = store.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(21)));
        Assert.Equal(new double?[] { 1, 2, 4 }, series.Points.Select(point => point.Number));
    }

    [Fact]
    public async Task WhenADropIsFollowedByAFlush_ThenTheGapIsPersistedAndNotHealedOver()
    {
        // Arrange: the flush that ends drop mode must write the range ending at the dropped change, and
        // the recovery range must start separately, or the next flush extends the pre-gap range straight
        // across the hole and the loss disappears from durable coverage.
        var now = Base;
        using var store = NewStore(() => now, maxPendingSamples: 1);
        store.Record("/a/Value", Base.AddSeconds(1), 1d, typeof(double));
        store.Record("/a/Value", Base.AddSeconds(5), 2d, typeof(double));   // dropped

        // Act
        now = Base.AddSeconds(10);
        await store.FlushAsync(CancellationToken.None);
        now = Base.AddSeconds(20);
        await store.FlushAsync(CancellationToken.None);

        // Assert: two ranges with the drop between them, not one range spanning it.
        Assert.Equal(
            new[]
            {
                new HistoryCoverage(Base, Base.AddSeconds(5)),
                new HistoryCoverage(Base.AddSeconds(10), Base.AddSeconds(20).AddTicks(1))
            },
            store.CoverageRanges.ToArray());
    }

    [Fact]
    public async Task WhenAStaleTimestampIsPending_ThenOnlyTheCurrentSessionRetracts()
    {
        // Arrange: durable coverage from an earlier session.
        var now = Base;
        using (var first = NewStore(() => now))
        {
            first.Record("/a/Value", Base.AddSeconds(1), 1d, typeof(double));
            now = Base.AddSeconds(10);
            await first.FlushAsync(CancellationToken.None);
        }

        // The second session must own a durable range of its own before the stale sample arrives.
        // Without this flush the only row on disk belongs to session one, whose start is strictly
        // below the current session's, so the case this test is named for never arises and every
        // assertion below passes for an unrelated reason.
        now = Base.AddSeconds(20);
        using var store = NewStore(() => now);
        store.Record("/a/Value", Base.AddSeconds(21), 2d, typeof(double));
        now = Base.AddSeconds(30);
        await store.FlushAsync(CancellationToken.None);

        var beforeStaleSample = store.CoverageRanges;
        Assert.Equal(2, beforeStaleSample.Length);

        // Act: a device reporting an uninitialized timestamp, which is reachable from OPC UA sources.
        store.Record("/a/Value", new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero), 3d, typeof(double));

        // Assert: a change that far outside the session claims nothing back, from either session.
        // Compared as arrays: ImmutableArray equality is backing-array identity, so comparing the
        // snapshots directly would pass on reference sameness rather than on the values.
        Assert.Equal(beforeStaleSample.ToArray(), store.CoverageRanges.ToArray());
    }

    [Fact]
    public async Task WhenAStaleAndARecentTimestampArePending_ThenCoverageStillStopsAtTheRecentOne()
    {
        // Arrange: one flush, so this session owns a durable range starting at its coverage start.
        var now = Base;
        using var store = NewStore(() => now);
        now = Base.AddSeconds(10);
        await store.FlushAsync(CancellationToken.None);

        // Act - the in-session change is recorded first on purpose. With the stale one first there is
        // no accumulated watermark for it to damage, so a filter that erased the running minimum
        // instead of ignoring the candidate would go unnoticed.
        store.Record("/a/Value", Base.AddSeconds(5), 2d, typeof(double));
        store.Record("/a/Value", new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero), 1d, typeof(double));

        // Assert: ignoring the stale change must not also hide the in-session one behind it. This is
        // the guard against "fixing" the stale case by letting the watermark claim past real pending
        // data, which would have the merger answer "covered, no data" for a change still in the queue.
        Assert.Equal(Base.AddSeconds(5), Assert.Single(store.CoverageRanges).To);
    }

    [Fact]
    public async Task WhenDroppedSamplesArriveOutOfOrder_ThenCoverageEndsAtEarliestTimestamp()
    {
        // Arrange
        var now = Base;
        using var store = NewStore(() => now, maxPendingSamples: 1);
        store.Record("/a/Value", Base.AddSeconds(1), 1d, typeof(double));
        store.Record("/a/Value", Base.AddSeconds(10), 2d, typeof(double));

        // Act
        store.Record("/a/Value", Base.AddSeconds(5), 3d, typeof(double));
        now = Base.AddSeconds(20);
        await store.FlushAsync(CancellationToken.None);

        // Assert
        Assert.Equal(Base.AddSeconds(5), Assert.Single(store.CoverageRanges).To);
    }

    [Fact]
    public async Task WhenQueryIsCancelled_ThenStoreStopsBeforeReading()
    {
        // Arrange
        var now = Base;
        using var store = NewStore(() => now);
        now = Base.AddSeconds(10);
        await store.FlushAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var query = new HistoryQuery("/a/Value", Base, now);

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.QueryAsync(query, cancellation.Token));
    }

    [Fact]
    public void WhenMaxPendingSamplesIsNotPositive_ThenConstructorThrows()
    {
        // Arrange, Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NewStore(() => Base, maxPendingSamples: 0));
    }

    [Fact]
    public async Task WhenAStaleChangeWasDropped_ThenCoverageStillRetracts()
    {
        // Arrange: a queue of one, so the second change is dropped rather than queued. A dropped
        // change is lost for good, unlike a queued one that is merely late, so it has to keep
        // constraining coverage however old it is. Filtering it by session start let the flush publish
        // a range over permanently missing data and then advance the session start past that range's
        // own start, after which changes inside the range stopped constraining it at all.
        var now = Base;
        using var store = NewStore(() => now, maxPendingSamples: 1);

        store.Record("/a/Value", Base.AddSeconds(1), 1d, typeof(double));
        store.Record("/a/Value", new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero), 2d, typeof(double));
        Assert.Equal(1, store.DropCount);

        now = Base.AddSeconds(30);
        await store.FlushAsync(CancellationToken.None);

        // Act: a later change, queued and unwritten.
        store.Record("/a/Value", Base.AddSeconds(25), 3d, typeof(double));

        // Assert: nothing may claim an instant still sitting in the queue.
        Assert.DoesNotContain(
            store.CoverageRanges,
            range => range.From <= Base.AddSeconds(25) && range.To > Base.AddSeconds(25));
    }

    [Fact]
    public async Task WhenAStaleChangeArrivesDuringAFlush_ThenDurableCoverageStillAdvances()
    {
        // Arrange: the flush swaps the queue out, then computes how far the new range may reach from
        // whatever is queued at that moment. A change arriving in that window is not in the batch being
        // written, so it is genuinely uncommitted -- but one from before the range's own start never
        // claimed that instant, so it must not limit the range. Taking the raw minimum drove the end
        // below the start, the write was skipped, and durable coverage froze while rows kept landing.
        //
        // The window is only reachable concurrently, hence the recorder thread.
        var now = Base;
        using var store = NewStore(() => now);
        var stale = new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Bounded well below the pending limit: overflowing it starts dropping, and a dropped change
        // constrains coverage from its own instant however old it is, which empties the snapshot for a
        // different reason and would mask what this test is measuring.
        var stop = false;
        var recorder = Task.Run(() =>
        {
            for (var written = 0; written < 20_000 && !Volatile.Read(ref stop); written++)
            {
                store.Record("/a/Value", stale, 1d, typeof(double));
                Thread.Yield();
            }
        });

        // Act: several flushes, each advancing the clock, with the stale recorder running throughout.
        var ends = new List<DateTimeOffset>();
        for (var step = 1; step <= 8; step++)
        {
            now = Base.AddSeconds(step * 10);
            await store.FlushAsync(CancellationToken.None);
            ends.Add(store.CoverageRanges.Length > 0 ? store.CoverageRanges[^1].To : Base);
        }

        Volatile.Write(ref stop, true);
        await recorder;

        // Assert: every flush moved the claim forward. A frozen end means a flush skipped its write.
        Assert.Equal(0, store.DropCount);
        for (var index = 1; index < ends.Count; index++)
        {
            Assert.True(
                ends[index] > ends[index - 1],
                $"durable coverage stalled at flush {index + 1}: {ends[index - 1]:o} -> {ends[index]:o}");
        }
    }

    [Fact]
    public async Task WhenTheSweepAdvancesTheCoverageStart_ThenTheTrimmedRangeIsGoneFirst()
    {
        // Arrange: retention narrows both the claim and the constraint that protects it. Advancing the
        // start before trimming lifts the constraint while the untrimmed range is still published, and
        // the connection lock the trim needs is held for the whole of every query, so the window is as
        // wide as the slowest read.
        var now = Base;
        using var store = NewStore(() => now);
        store.Record("/a/Value", Base.AddSeconds(1), 1d, typeof(double));
        now = Base.AddSeconds(10);
        await store.FlushAsync(CancellationToken.None);

        // Act: move well past the retention window so the sweep trims everything.
        now = Base.AddDays(400);
        store.Sweep();

        // Assert: nothing is left claiming the swept window.
        Assert.DoesNotContain(
            store.CoverageRanges,
            range => range.From <= Base.AddSeconds(1) && range.To > Base.AddSeconds(1));
    }

    [Fact]
    public async Task WhenCoverageRetractsToNothing_ThenItIsReportedOnceAndOnRecovery()
    {
        // Arrange: a dropped change older than every recorded range retracts all of them, and the store
        // then just stops appearing in query results. Silent, so it reads as "no history recorded"
        // rather than as a fault. It stays silent by design for now -- see the coverage-precision
        // follow-up -- so at least say so.
        var now = Base;
        var logger = new CapturingLogger();
        using var store = NewStore(() => now, maxPendingSamples: 1, logger: logger);

        store.Record("/a/Value", Base.AddSeconds(1), 1d, typeof(double));
        now = Base.AddSeconds(10);
        await store.FlushAsync(CancellationToken.None);
        Assert.NotEmpty(store.CoverageRanges);

        // Act: fill the queue, then drop a change predating every range.
        store.Record("/a/Value", Base.AddSeconds(11), 2d, typeof(double));
        store.Record("/a/Value", new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero), 3d, typeof(double));

        // Assert: reported exactly once, not once per recorded change.
        Assert.Empty(store.CoverageRanges);
        Assert.Contains("retracted to nothing", Assert.Single(logger.Errors));

        store.Record("/a/Value", new DateTimeOffset(1601, 1, 1, 0, 0, 1, TimeSpan.Zero), 4d, typeof(double));
        Assert.Single(logger.Errors);

        // And recovery is reported too, so the condition can be seen to have cleared.
        now = Base.AddSeconds(20);
        await store.FlushAsync(CancellationToken.None);
        Assert.NotEmpty(store.CoverageRanges);
        Assert.Contains(logger.Information, message => message.Contains("recovered"));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Errors { get; } = [];

        public List<string> Information { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var target = logLevel == LogLevel.Error ? Errors : Information;
            target.Add(formatter(state, exception));
        }
    }

    private SqliteHistoryStore NewStore(
        Func<DateTimeOffset> getUtcNow,
        int maxPendingSamples = SqliteHistoryStore.DefaultMaxPendingSamples,
        ILogger? logger = null) =>
        new(
            priority: 50,
            databaseDirectory: _directory,
            PartitionInterval.Weekly,
            TimeSpan.FromDays(365),
            maxJsonSize: 8192,
            getUtcNow,
            maxPendingSamples,
            logger);
}
