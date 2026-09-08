using System.Collections.Immutable;
using System.Globalization;
using HomeBlaze.History.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// The graph-free, SQL-backed history engine. Operates only on canonical path strings and typed
/// values: partition-file management, schema, batched write plus periodic flush, raw queries,
/// look-back, coverage, and metrics. It implements <see cref="IHistoryStore"/> directly so a future
/// generic host can drive it without graph coupling; the <see cref="SqliteHistoryStoreSubject"/>
/// [InterceptorSubject] adapter delegates to it. Mirrors the value routing and point mapping of
/// <c>InMemoryHistoryStore</c> so query results are identical, but persists rows into
/// partitioned SQLite database files with <c>value_json</c> stored as TEXT.
/// </summary>
public sealed class SqliteHistoryStore : IHistoryStore, IHistoryRecorder, IDisposable
{
    public const int DefaultMaxPendingSamples = 100_000;

    /// <summary>
    /// The aggregations every SQLite store path supports (the full set, independent of column type).
    /// </summary>
    public static readonly IReadOnlySet<string> AllAggregations = new HashSet<string>(StringComparer.Ordinal)
    {
        HistoryAggregations.Last,
        HistoryAggregations.First,
        HistoryAggregations.SampleAverage,
        HistoryAggregations.TimeWeightedAverage,
        HistoryAggregations.Minimum,
        HistoryAggregations.Maximum,
        HistoryAggregations.Sum,
        HistoryAggregations.Count,
        HistoryAggregations.StandardDeviation
    };

    private readonly int _priority;
    private readonly string _databaseDirectory;
    private readonly PartitionInterval _partitionInterval;
    private readonly TimeSpan _maxAge;
    private readonly int _maxJsonSize;
    private readonly int _maxPendingSamples;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly ILogger? _logger;

    // Whether the store has already reported that its coverage retracted to nothing. Under _pendingLock.
    private bool _reportedBlankedCoverage;

    // The store-wide database: move records and durable coverage ranges. Unlike a partition it is
    // never deleted by retention. "metadata" is never a valid partition key, so partition
    // enumeration skips it without a special case.
    private const string MetadataKey = "metadata";

    // The on-disk format version stamped into every database file. Bump it whenever the durable
    // shape changes, so a build that predates the change refuses the file instead of misreading it.
    private const long SchemaVersion = 1;

    // 'HBH1' in ASCII, stamped into the SQLite header so file(1) and external tooling recognise these
    // as HomeBlaze history databases rather than reporting a bare "SQLite 3.x database". This marks
    // the format family and never changes; SchemaVersion above carries the revision.
    private const long ApplicationId = 0x48424831;

    private readonly object _pendingLock = new();
    private readonly object _flushLock = new();
    private readonly List<PendingSample> _pending = new();
    private readonly List<HistoryMove> _pendingMoves = new();
    private int _inFlightSampleCount;
    private DateTimeOffset? _earliestPendingTimestamp;
    private DateTimeOffset? _earliestInFlightTimestamp;
    private bool _droppingNewSamples;
    private bool _pendingStartsNewCoverageRange = true;
    private DateTimeOffset _pendingCoverageStart;

    private DateTimeOffset? _firstDroppedTimestamp;

    // The earliest instant this store holds an uncommitted (pending, in-flight, or dropped) change for,
    // as UTC ticks, or long.MaxValue when there is none. Written under _pendingLock, read without any
    // lock so coverage reads never wait behind a flush.
    private long _uncommittedFromTicks = long.MaxValue;

    private readonly object _connectionLock = new();
    private readonly Dictionary<string, SqliteConnection> _connections = new(StringComparer.Ordinal);

    // Partitions this build refused to open, so a read skips them without retrying and logging per query.
    private readonly HashSet<string> _unreadablePartitions = new(StringComparer.Ordinal);

    private readonly SqliteCoverageStore _coverageStore;

    // The read/partition-layout context handed to SqliteHistoryReader. It captures this engine's
    // OpenPartition/OpenMetadata delegates (which take _connectionLock), so the reader runs entirely within
    // the engine's lock; the reader itself never locks and never touches _connections.
    private readonly SqliteReadContext _readContext;

    private long _recordedCount;
    private long _oversizeCount;
    private long _dropCount;

    private long _lastFlushUtcTicks;
    private volatile string? _lastError;
    private bool _reloadCoverageAfterFailure;

    public SqliteHistoryStore(
        int priority,
        string databaseDirectory,
        PartitionInterval partitionInterval,
        TimeSpan maxAge,
        int maxJsonSize,
        Func<DateTimeOffset> getUtcNow,
        int maxPendingSamples = DefaultMaxPendingSamples,
        ILogger? logger = null)
    {
        if (maxPendingSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPendingSamples), "The maximum pending sample count must be positive.");
        }

        _priority = priority;
        _databaseDirectory = databaseDirectory;
        _partitionInterval = partitionInterval;
        _maxAge = maxAge;
        _maxJsonSize = maxJsonSize;
        _maxPendingSamples = maxPendingSamples;
        _getUtcNow = getUtcNow;
        _logger = logger;
        _readContext = new SqliteReadContext(
            _databaseDirectory, MetadataKey, TryOpenPartition, OpenMetadata);
        _coverageStore = new SqliteCoverageStore(OpenMetadata);

        // After the coverage store exists: publishing the watermark compares against its snapshot.
        SetPendingCoverageStart(getUtcNow());

        Directory.CreateDirectory(_databaseDirectory);
        lock (_connectionLock)
        {
            _coverageStore.Reload();
        }
    }

    /// <summary>
    /// Restarts the coverage session at the current instant. The constructor already starts one, so
    /// this only narrows what the store claims: the owner calls it once its change subscription is
    /// live, so no change can fall inside claimed coverage without reaching this engine.
    /// </summary>
    internal void BeginCoverageSession()
    {
        lock (_pendingLock)
        {
            SetPendingCoverageStart(_getUtcNow());
            _pendingStartsNewCoverageRange = true;
        }
    }

    public int Priority => _priority;

    public IReadOnlySet<string> SupportedAggregations => AllAggregations;

    public long RecordedCount => Interlocked.Read(ref _recordedCount);

    public long OversizeCount => Interlocked.Read(ref _oversizeCount);

    public long DropCount => Interlocked.Read(ref _dropCount);

    public int QueueDepth
    {
        get
        {
            lock (_pendingLock)
            {
                return PendingCount;
            }
        }
    }

    // Everything queued for the next flush, including the batch a flush currently has in flight.
    // Moves count: they are flushed in the same batch and held in memory until then, so leaving them
    // out under-reported the depth and let an unbounded move stream grow the queue without ever
    // reaching the drop guard.
    private int PendingCount => _pending.Count + _inFlightSampleCount + _pendingMoves.Count;

    public DateTimeOffset? LastFlushUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastFlushUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public string? LastError => _lastError;

    public ImmutableArray<HistoryCoverage> CoverageRanges => GetEffectiveCoverageSnapshot();

    public long EstimatedStorageBytes
    {
        get
        {
            var total = 0L;
            if (Directory.Exists(_databaseDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(_databaseDirectory))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch
                    {
                        // best effort: a WAL/SHM file may vanish between enumeration and stat
                    }
                }
            }

            return total;
        }
    }

    public void Record(string propertyPath, DateTimeOffset timestamp, object? value, Type propertyType)
    {
        TryRecord(propertyPath, timestamp, value, propertyType);
    }

    /// <inheritdoc />
    public bool TryRecord(string propertyPath, DateTimeOffset timestamp, object? value, Type propertyType)
    {
        var column = HistoryColumns.GetValueColumnFor(propertyType);
        var isUlong = HistoryColumns.IsUlongProperty(propertyType);
        var routed = SqliteValueRouting.CreateRow(value, column, isUlong, _maxJsonSize);

        lock (_pendingLock)
        {
            if (_droppingNewSamples || PendingCount >= _maxPendingSamples)
            {
                _droppingNewSamples = true;
                _firstDroppedTimestamp = Earlier(_firstDroppedTimestamp, timestamp);
                Interlocked.Increment(ref _dropCount);
                PublishUncommittedWatermark();
                return false;
            }

            _pending.Add(new PendingSample(propertyPath, timestamp, routed.Row, column, isUlong));
            _earliestPendingTimestamp = EarlierInSession(_earliestPendingTimestamp, timestamp);
            PublishUncommittedWatermark();
        }

        if (routed.Oversized)
        {
            Interlocked.Increment(ref _oversizeCount);
        }

        Interlocked.Increment(ref _recordedCount);
        return true;
    }

    public void RecordMove(DateTimeOffset timestamp, string fromPath, string toPath)
    {
        // Queue the move like a pending sample; FlushAsync persists it into the metadata database.
        lock (_pendingLock)
        {
            // Bounded like samples are. A lost move is worse than a lost sample (the queried path can
            // no longer reach the samples recorded under the old one), so it is recorded as a drop and
            // clamps coverage from its instant rather than being silently discarded.
            if (_droppingNewSamples || PendingCount >= _maxPendingSamples)
            {
                _droppingNewSamples = true;
                _firstDroppedTimestamp = Earlier(_firstDroppedTimestamp, timestamp);
                Interlocked.Increment(ref _dropCount);
                PublishUncommittedWatermark();
                return;
            }

            _pendingMoves.Add(new HistoryMove(timestamp, fromPath, toPath));
            _earliestPendingTimestamp = EarlierInSession(_earliestPendingTimestamp, timestamp);
            PublishUncommittedWatermark();
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_flushLock)
        {
            PendingSample[] batch;
            HistoryMove[] moveBatch;
            bool startsNewCoverageRange;
            DateTimeOffset coverageStart;
            lock (_pendingLock)
            {
                batch = _pending.ToArray();
                _pending.Clear();
                moveBatch = _pendingMoves.ToArray();
                _pendingMoves.Clear();
                _earliestInFlightTimestamp = _earliestPendingTimestamp;
                _earliestPendingTimestamp = null;
                // Left set until the flush actually writes the range it authorizes (see below), so a
                // skipped or failed coverage update cannot silently swallow a gap marker.
                startsNewCoverageRange = _pendingStartsNewCoverageRange;
                coverageStart = _pendingCoverageStart;
                _inFlightSampleCount = batch.Length + moveBatch.Length;
                PublishUncommittedWatermark();
            }

            try
            {
                var byPartition = new Dictionary<string, List<PendingSample>>(StringComparer.Ordinal);
                foreach (var sample in batch)
                {
                    var key = SqlitePartition.PartitionKey(sample.Timestamp, _partitionInterval);
                    if (!byPartition.TryGetValue(key, out var list))
                    {
                        list = new List<PendingSample>();
                        byPartition[key] = list;
                    }

                    list.Add(sample);
                }

                lock (_connectionLock)
                {
                    if (_reloadCoverageAfterFailure)
                    {
                        _coverageStore.Reload();
                        _reloadCoverageAfterFailure = false;
                    }

                    foreach (var (key, samples) in byPartition)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        SqliteHistoryWriter.WritePartition(OpenPartition(key), samples);
                    }

                    if (moveBatch.Length > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        SqliteHistoryWriter.WriteMoves(OpenMetadata(), moveBatch);
                    }
                }

                var successfulAt = _getUtcNow();
                lock (_pendingLock)
                {
                    var coverageEnd = successfulAt == DateTimeOffset.MaxValue
                        ? successfulAt
                        : successfulAt.AddTicks(1);

                    // A change from before this range's own start cannot limit how far the range
                    // reaches, because the range never claimed that instant. Taking the raw minimum
                    // let one backdated sample sitting in the queue during a flush drive the end below
                    // the start, so the guard below skipped the write and durable coverage silently
                    // stopped advancing while the rows themselves kept landing on disk.
                    foreach (var sample in _pending)
                    {
                        if (sample.Timestamp >= coverageStart && sample.Timestamp < coverageEnd)
                        {
                            coverageEnd = sample.Timestamp;
                        }
                    }

                    foreach (var move in _pendingMoves)
                    {
                        if (move.Timestamp >= coverageStart && move.Timestamp < coverageEnd)
                        {
                            coverageEnd = move.Timestamp;
                        }
                    }

                    // Clamp at the first dropped change unconditionally. Deferring this to the recovery
                    // flush let an interim flush persist coverage past the drop whenever samples newer
                    // than it were still pending, and a crash before recovery made that durable.
                    if (_firstDroppedTimestamp is { } droppedAt && droppedAt < coverageEnd)
                    {
                        coverageEnd = droppedAt;
                    }

                    var recoveredFromDrops = _droppingNewSamples && _pending.Count == 0 && _pendingMoves.Count == 0;

                    lock (_connectionLock)
                    {
                        var fromTicks = EpochTicks.ToEpochTicks(coverageStart);
                        var toTicks = EpochTicks.ToEpochTicks(coverageEnd);
                        if (fromTicks < toTicks)
                        {
                            _coverageStore.Update(fromTicks, toTicks, startsNewCoverageRange);

                            // Only consume the marker once the range it authorizes actually exists.
                            // Clearing it at swap time lost the gap whenever this guard skipped, and the
                            // next flush then extended the pre-gap range straight across the hole.
                            _pendingStartsNewCoverageRange = false;
                        }
                        else
                        {
                            _pendingStartsNewCoverageRange |= startsNewCoverageRange;
                        }
                    }

                    Interlocked.Exchange(ref _lastFlushUtcTicks, successfulAt.UtcTicks);
                    _lastError = null;

                    _inFlightSampleCount = 0;
                    _earliestInFlightTimestamp = null;
                    if (recoveredFromDrops)
                    {
                        _firstDroppedTimestamp = null;
                        _droppingNewSamples = false;
                        _pendingStartsNewCoverageRange = true;
                        SetPendingCoverageStart(successfulAt);
                    }

                    PublishUncommittedWatermark();
                }
            }
            catch (Exception exception)
            {
                _lastError = exception.Message;
                if (exception is not OperationCanceledException)
                {
                    lock (_connectionLock)
                    {
                        DisposeConnections();
                        _reloadCoverageAfterFailure = true;
                    }
                }

                // The write failed, so put the batch back at the front. Record() included the in-flight
                // count in its bound, so reinserting cannot exceed the pending sample limit.
                lock (_pendingLock)
                {
                    _inFlightSampleCount = 0;
                    _pending.InsertRange(0, batch);
                    _pendingMoves.InsertRange(0, moveBatch);
                    _earliestPendingTimestamp = Earlier(
                        _earliestPendingTimestamp,
                        _earliestInFlightTimestamp);
                    _earliestInFlightTimestamp = null;
                    PublishUncommittedWatermark();
                }

                throw;
            }
        }

        return Task.CompletedTask;
    }

    private void DisposeConnections()
    {
        foreach (var connection in _connections.Values)
        {
            connection.Close();
            connection.Dispose();
        }

        _connections.Clear();
    }

    public Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Query(query, cancellationToken));
    }

    public ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(
        string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<HistoryPoint?>(GetSampleAtOrBefore(propertyPath, asOf));
    }

    public HistorySeries Query(HistoryQuery query) => Query(query, CancellationToken.None);

    private HistorySeries Query(HistoryQuery query, CancellationToken cancellationToken)
    {
        query.Validate();
        var coverageRanges = GetEffectiveCoverageSnapshot();

        // A single SqliteConnection/SqliteCommand is not thread-safe. Serialize all connection use
        // under the re-entrant _connectionLock so concurrent queries and the flush loop cannot collide
        // on shared cached connections. The reader runs entirely within this lock (it opens connections
        // through the engine's OpenPartition/OpenMoves delegates, which re-enter this lock).
        lock (_connectionLock)
        {
            // The bucket reader's TWA carry-seed look-back reads directly through the same context; it runs
            // while this lock is already held, matching the original inline GetSampleAtOrBefore look-back.
            var result = query.Bucket is null
                ? SqliteHistoryReader.QueryRaw(_readContext, query, cancellationToken)
                : SqliteBucketReader.QueryBucketed(
                    _readContext, query,
                    (path, asOf) => GetSampleAtOrBeforeCore(path, asOf, coverageRanges),
                    coverageRanges,
                    cancellationToken);
            return result with
            {
                CoverageRanges = HistoryCoverage.Clip(
                    coverageRanges, new HistoryCoverage(query.From, query.To))
            };
        }
    }

    public HistoryPoint? GetSampleAtOrBefore(string propertyPath, DateTimeOffset asOf)
    {
        var coverageRanges = GetEffectiveCoverageSnapshot();

        // Serialize connection use under the re-entrant _connectionLock (see Query for the rationale).
        lock (_connectionLock)
        {
            return GetSampleAtOrBeforeCore(propertyPath, asOf, coverageRanges);
        }
    }

    private HistoryPoint? GetSampleAtOrBeforeCore(
        string propertyPath,
        DateTimeOffset asOf,
        ImmutableArray<HistoryCoverage>? coverageSnapshot = null)
    {
        var ranges = coverageSnapshot ?? _coverageStore.Snapshot;
        HistoryCoverage? containingRange = null;
        for (var index = ranges.Length - 1; index >= 0; index--)
        {
            var range = ranges[index];
            // Half-open, like every other coverage test: asOf exactly at To is outside the range.
            if (range.From <= asOf && range.To > asOf)
            {
                containingRange = range;
                break;
            }
        }

        if (containingRange is not { } coverage)
        {
            return null;
        }

        var sample = SqliteHistoryReader.GetSampleAtOrBefore(_readContext, propertyPath, asOf);
        return sample is not null && sample.Timestamp >= coverage.From ? sample : null;
    }

    // The durable ranges never claim an instant this store still holds an uncommitted change for.
    // Both inputs are published independently and read without a lock: the durable snapshot changes
    // once per flush, and the watermark can only be read stale in the conservative direction (it is
    // read after the ranges, so a concurrent flush yields the older, narrower ranges).
    private ImmutableArray<HistoryCoverage> GetEffectiveCoverageSnapshot()
    {
        var ranges = _coverageStore.Snapshot;
        var uncommittedFromTicks = Interlocked.Read(ref _uncommittedFromTicks);
        if (uncommittedFromTicks == long.MaxValue)
        {
            return ranges;
        }

        // Everything from the watermark onwards is dropped rather than merely clipped, because the
        // watermark is only the earliest uncommitted instant: later pending changes may fall anywhere
        // after it, so no later range can be vouched for. That is safe only because the watermark
        // never precedes this session's coverage start, which the fold sites guarantee; flooring it
        // here instead landed it exactly on the active range's own start and dropped that range.
        var uncommittedFrom = new DateTimeOffset(uncommittedFromTicks, TimeSpan.Zero);

        var builder = ImmutableArray.CreateBuilder<HistoryCoverage>(ranges.Length);
        foreach (var range in ranges)
        {
            if (range.From >= uncommittedFrom)
            {
                break;
            }

            builder.Add(range.To > uncommittedFrom ? range with { To = uncommittedFrom } : range);
        }

        return builder.ToImmutable();
    }

    // Recomputes the earliest uncommitted instant and publishes it for lock-free coverage reads.
    // Must be called under _pendingLock whenever any of the three inputs changes.
    private void PublishUncommittedWatermark()
    {
        var uncommittedFrom = Earlier(
            Earlier(_firstDroppedTimestamp, _earliestPendingTimestamp),
            _earliestInFlightTimestamp);

        Interlocked.Exchange(
            ref _uncommittedFromTicks, uncommittedFrom?.UtcTicks ?? long.MaxValue);

        // A change older than every recorded range retracts all of them at once, and the store then
        // simply stops appearing in query results. Nothing else reports that, so it reads to an
        // operator as "there is no history" rather than as a fault worth investigating. Reported on
        // the transition only, so a stuck device does not fill the log.
        var ranges = _coverageStore.Snapshot;
        var isBlanked = uncommittedFrom is { } from && !ranges.IsDefaultOrEmpty && ranges[0].From >= from;
        if (isBlanked == _reportedBlankedCoverage)
        {
            return;
        }

        _reportedBlankedCoverage = isBlanked;
        if (isBlanked)
        {
            _logger?.LogError(
                "History coverage retracted to nothing: an uncommitted change at {UncommittedFrom:o} predates " +
                "every recorded range, so this store cannot serve any query until that change is flushed. " +
                "A device reporting an uninitialised timestamp is the usual cause.",
                uncommittedFrom);
        }
        else
        {
            _logger?.LogInformation("History coverage recovered and this store is serving queries again.");
        }
    }

    // Advances this session's coverage start. Called under _pendingLock, except from the constructor,
    // where the store is not reachable yet.
    private void SetPendingCoverageStart(DateTimeOffset value)
    {
        _pendingCoverageStart = value;

        // Advancing the start can strand an instant the watermark accepted under the previous one,
        // which would blank the snapshot exactly as an out-of-session change does, so the minima are
        // recomputed from the queues. Every caller either runs before the first flush or holds
        // _flushLock, so no batch is in flight and _earliestInFlightTimestamp is null.
        if (_earliestPendingTimestamp < value)
        {
            _earliestPendingTimestamp = null;
            foreach (var sample in _pending)
            {
                _earliestPendingTimestamp = EarlierInSession(_earliestPendingTimestamp, sample.Timestamp);
            }

            foreach (var move in _pendingMoves)
            {
                _earliestPendingTimestamp = EarlierInSession(_earliestPendingTimestamp, move.Timestamp);
            }
        }

        PublishUncommittedWatermark();
    }

    // Folds a *queued* change's instant into a watermark input, ignoring anything before this session's
    // coverage start. Such a change cannot describe an instant this session claims, and letting it
    // through blanks the snapshot outright: the watermark is subtracted from the durable ranges
    // onwards, and this session's own range starts at exactly the coverage start. One device reporting
    // an uninitialised 1601 timestamp is enough to reach that.
    //
    // Deliberately not applied to a dropped change. A queued one will still be written, so ignoring it
    // only defers what coverage says; a dropped one is gone for good, so it has to keep constraining
    // coverage no matter how old it is. Filtering it let a flush publish a range over permanently
    // missing data, and drop recovery then advanced the session start past that range's own start,
    // after which every change inside it was filtered out while the range still claimed it.
    private DateTimeOffset? EarlierInSession(DateTimeOffset? current, DateTimeOffset candidate) =>
        candidate < _pendingCoverageStart ? current : Earlier(current, candidate);

    private static DateTimeOffset? Earlier(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
        {
            return right;
        }

        return right is not null && right < left ? right : left;
    }

    public void Sweep()
    {
        lock (_flushLock)
        {
            var cutoff = _getUtcNow() - _maxAge;

            lock (_connectionLock)
            {
                // Give up the claim before deleting the data behind it. Deleting first leaves the store
                // claiming a window whose partitions are gone if a delete throws (a locked file) or the
                // process dies mid-sweep, and inside claimed coverage the merger will not fall back to
                // another store, so those queries return empty rather than the other store's data.
                //
                // The claim is also trimmed before the coverage start is advanced below. Advancing it
                // first lifts the constraint that stops an untrimmed range being claimed, and this lock
                // is held for the whole of every query, so the window between the two is as wide as the
                // slowest read.
                _coverageStore.Trim(EpochTicks.ToEpochTicks(cutoff));

                foreach (var key in _readContext.EnumeratePartitionFileKeys().ToArray())
                {
                    var (_, end) = SqlitePartition.InferredRange(key);
                    if (end >= cutoff)
                    {
                        continue;
                    }

                    try
                    {
                        DeletePartition(key);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // One locked file must not abort the sweep: the rest still needs trimming, and
                        // this partition is already outside the coverage claimed above.
                        _lastError = exception.Message;
                    }
                }

                // Moves describing samples that no longer exist only slow down chain resolution, which
                // every query and look-back performs, and they accumulate in the one database retention
                // never deletes. The newest move at or before the cutoff is kept, because that is the one
                // bounding the leg covering the cutoff instant. Mirrors InMemoryHistoryStore.Sweep.
                try
                {
                    PruneMoves(EpochTicks.ToEpochTicks(cutoff));
                }
                catch (SqliteException exception)
                {
                    // Same reasoning as the delete loop above: a locked or failing metadata database
                    // must not abort the sweep before the partitions are checkpointed and before the
                    // coverage start advances below. The next sweep prunes what this one did not.
                    _lastError = exception.Message;
                }

                // Persist the WAL contents back into the surviving main database files so their on-disk
                // size reflects the data after the sweep.
                //
                // Only the files that actually have frames. A sweep runs after every flush, so at the
                // default year of weekly partitions this loop faced more than fifty cached connections
                // every ten seconds while a flush usually writes one of them. An idle TRUNCATE leaves
                // the database itself untouched, so the cost was not rewriting them: it was ~55
                // truncate-to-zero calls and inode updates every ten seconds, which still matters on
                // the SD cards and eMMC these installations run on.
                foreach (var (key, connection) in _connections)
                {
                    if (HasWalFrames(_readContext.PartitionFilePath(key)))
                    {
                        Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                    }
                }
            }

            lock (_pendingLock)
            {
                if (_pendingCoverageStart < cutoff)
                {
                    SetPendingCoverageStart(cutoff);
                }
            }
        }
    }

    // Closes and removes the cached connection (Windows holds WAL/SHM locks), then deletes the
    // partition's main file and its -wal/-shm siblings if present.
    private void DeletePartition(string key)
    {
        if (_connections.TryGetValue(key, out var connection))
        {
            connection.Close();
            connection.Dispose();
            _connections.Remove(key);
        }

        var path = _readContext.PartitionFilePath(key);
        DeleteFileIfExists(path);
        DeleteFileIfExists(path + "-wal");
        DeleteFileIfExists(path + "-shm");
    }

    // Drops moves that only describe samples already aged out, keeping the newest one at or before the
    // cutoff. Ties at that instant are all kept: an extra leg boundary older than every retained sample
    // costs a row and changes no answer, where dropping one too many would rewrite history.
    private void PruneMoves(long cutoffTicks)
    {
        using var command = OpenMetadata().CreateCommand();
        command.CommandText =
            "DELETE FROM moves WHERE ts < (SELECT MAX(ts) FROM moves WHERE ts <= @cutoff);";
        command.Parameters.AddWithValue("@cutoff", cutoffTicks);
        command.ExecuteNonQuery();
    }

    // Whether a database has WAL frames waiting to be folded back in. A successful TRUNCATE checkpoint
    // leaves the -wal file at zero bytes, so any content means the database was written since the last
    // one, or that its checkpoint could not complete and should be retried. Derived from the file rather
    // than tracked alongside the writes, so a future write path cannot forget to flag itself.
    private static bool HasWalFrames(string databaseFilePath)
    {
        var writeAheadLog = new FileInfo(databaseFilePath + "-wal");
        return writeAheadLog.Exists && writeAheadLog.Length > 0;
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Opens a partition for reading, or returns null when this build cannot read it.
    /// </summary>
    /// <remarks>
    /// A partition written by another schema version, or a foreign database sitting under a
    /// partition-shaped name, is refused by <see cref="InitializeDatabase"/>. A read must not turn that
    /// into a failed query: the sweep deletes by age and never by version, so one such file would
    /// otherwise break every history query for as long as retention keeps it, a year by default. The key
    /// is remembered so the refusal is logged once rather than on every query. The write path keeps
    /// throwing on the same file, because silently dropping samples is the worse outcome there.
    /// </remarks>
    private SqliteConnection? TryOpenPartition(string key)
    {
        lock (_connectionLock)
        {
            if (_unreadablePartitions.Contains(key))
            {
                return null;
            }
        }

        try
        {
            return OpenPartition(key);
        }
        catch (Exception exception)
        {
            lock (_connectionLock)
            {
                _unreadablePartitions.Add(key);
            }

            // Also on LastError, not only in the log: a query that quietly returns less than it was asked
            // for is its own hazard, and this is the store's one channel that reaches the UI.
            _lastError = exception.Message;

            _logger?.LogWarning(exception,
                "History partition '{PartitionKey}' cannot be read by this build and is skipped; " +
                "queries covering it return what the other partitions hold.", key);
            return null;
        }
    }

    private SqliteConnection OpenPartition(string key)
    {
        lock (_connectionLock)
        {
            if (_connections.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _readContext.PartitionFilePath(key),
                Pooling = false
            }.ToString());

            // Only cached once initialization succeeds, so the connection must be disposed on the way
            // out: pooling is off, so an abandoned one holds a real file handle until finalization, and
            // the flush loop retries a corrupt partition every interval.
            try
            {
                connection.Open();
                InitializeDatabase(connection, _readContext.PartitionFilePath(key));

                // Paths are interned per partition: the row key carries an integer id instead of the
                // full path string, which is otherwise more than half of every partition file. Keeping
                // the mapping inside the partition (rather than in the metadata database) leaves each
                // file self-describing, so it can be read, copied or deleted on its own, and lets a
                // path insert and the samples referencing it commit in one transaction.
                //
                // This table also carries what path_meta used to: the single lookup a read already
                // performs now answers the id and the column kind together.
                Execute(connection,
                    "CREATE TABLE IF NOT EXISTS paths (id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, " +
                    "value_column INTEGER NOT NULL, is_ulong INTEGER NOT NULL);");
                Execute(connection,
                    "CREATE TABLE IF NOT EXISTS history (path_id INTEGER NOT NULL, ts INTEGER NOT NULL, " +
                    "value_long INTEGER, value_double REAL, value_json TEXT, " +
                    "PRIMARY KEY (path_id, ts)) WITHOUT ROWID;");

                // Ids are local to each partition, so a hand-written query spanning several files cannot
                // union on them. This view costs nothing to store and keeps ad-hoc inspection as simple
                // as it was when the path was stored inline.
                Execute(connection,
                    "CREATE VIEW IF NOT EXISTS history_paths AS " +
                    "SELECT p.path, h.ts, h.value_long, h.value_double, h.value_json " +
                    "FROM history h JOIN paths p ON p.id = h.path_id;");
            }
            catch
            {
                connection.Dispose();
                throw;
            }

            _connections[key] = connection;
            return connection;
        }
    }

    // Opens the small metadata database that stores move records and durable coverage ranges.
    private SqliteConnection OpenMetadata()
    {
        lock (_connectionLock)
        {
            if (_connections.TryGetValue(MetadataKey, out var existing))
            {
                return existing;
            }

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _readContext.PartitionFilePath(MetadataKey),
                Pooling = false
            }.ToString());

            try
            {
                connection.Open();
                InitializeDatabase(connection, _readContext.PartitionFilePath(MetadataKey));

                // The key makes a re-insert idempotent, which matters because a failed flush re-queues
                // its moves, and it orders the table for the retention prune.
                Execute(connection,
                    "CREATE TABLE IF NOT EXISTS moves (ts INTEGER NOT NULL, from_path TEXT NOT NULL, " +
                    "to_path TEXT NOT NULL, PRIMARY KEY (ts, from_path, to_path)) WITHOUT ROWID;");

                Execute(connection,
                    "CREATE TABLE IF NOT EXISTS coverage_ranges (" +
                    "id INTEGER PRIMARY KEY AUTOINCREMENT, from_ts INTEGER NOT NULL, to_ts INTEGER NOT NULL, " +
                    "CHECK (from_ts < to_ts));");
            }
            catch
            {
                connection.Dispose();
                throw;
            }

            _connections[MetadataKey] = connection;
            return connection;
        }
    }

    // The pragmas and version handling every database file shares, applied before any table is created.
    //
    // page_size has to be set before the first table exists and before WAL is entered: from then on it
    // is fixed for the life of the file and only a full VACUUM can change it. 2048 costs about 1.3%
    // more file than the 4096 default and writes roughly a third less WAL per flush. Write volume is
    // what matters here, because PRIMARY KEY (path_id, ts) puts each path in its own region of the
    // b-tree, so a flush touching N paths dirties N pages however few bytes it actually carries.
    private static void InitializeDatabase(SqliteConnection connection, string filePath)
    {
        // Both checks run before any pragma: converting a file to WAL and only then declaring it
        // unreadable would modify the very file being refused.
        var version = QueryLong(connection, "PRAGMA user_version;");
        var applicationId = QueryLong(connection, "PRAGMA application_id;");
        var hasTables = QueryLong(connection, HasUserTablesSql) != 0;

        if (applicationId == ApplicationId && version > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"The history database '{filePath}' is at schema version {version}, but this build " +
                $"understands version {SchemaVersion}. Upgrade HomeBlaze, or move the file aside to " +
                "start a new one. Refusing it here rather than reading it as far as the columns happen " +
                "to line up.");
        }

        // Anything already holding tables must be exactly this build's format. An empty file is simply
        // new, and gets stamped below. Checking the version alone adopted any other SQLite file that
        // happened to sit here under a partition-shaped name: it was restamped as a history database
        // with its own tables left in place, and a read then failed with a bare "no such column" from
        // inside a query. There is no migration by design, so anything else is refused.
        if (hasTables && (applicationId != ApplicationId || version != SchemaVersion))
        {
            throw new InvalidOperationException(
                $"'{filePath}' is not a history database this build wrote (application id " +
                $"0x{applicationId:X8}, schema version {version}). Delete the history directory to " +
                "start fresh; these files hold best-effort history and are not migrated.");
        }

        Execute(connection, "PRAGMA page_size=2048;");
        Execute(connection, "PRAGMA journal_mode=WAL;");

        // Stamped only when they differ. Both pragmas write page 1 unconditionally, so re-stamping what
        // is already there dirtied every file a read merely opened, which put back the write volume the
        // checkpoint gate below exists to remove. Same argument InternPath makes for the paths table.
        if (QueryLong(connection, "PRAGMA application_id;") != ApplicationId)
        {
            Execute(connection, $"PRAGMA application_id={ApplicationId};");
        }

        if (version != SchemaVersion)
        {
            Execute(connection, $"PRAGMA user_version={SchemaVersion};");
        }
    }

    private const string HasUserTablesSql =
        "SELECT EXISTS (SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%');";

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long QueryLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        lock (_connectionLock)
        {
            DisposeConnections();
        }
    }
}
