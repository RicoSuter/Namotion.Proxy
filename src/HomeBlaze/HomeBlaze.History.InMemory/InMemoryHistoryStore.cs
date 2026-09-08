using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.InMemory;

/// <summary>
/// The graph-free in-memory history engine. Operates only on canonical path strings and typed values:
/// per-path ring buffers, raw and bucketed queries, look-back, coverage, and metrics. It implements
/// <see cref="IHistoryStore"/> directly so a future generic host can drive it without graph coupling;
/// the <see cref="InMemoryHistoryStoreSubject"/> [InterceptorSubject] adapter delegates to it.
/// </summary>
public sealed class InMemoryHistoryStore : IHistoryStore, IHistoryRecorder
{
    /// <summary>
    /// The aggregations every in-memory store path supports (the full set, independent of column type).
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

    private readonly int _maxPointsPerProperty;
    private readonly TimeSpan _maxAge;
    private readonly int _maxJsonSize;
    private readonly Func<DateTimeOffset> _getUtcNow;

    // Session bounds as ticks: written from the owner's lifecycle and read by query threads, and a
    // DateTimeOffset is too wide to read atomically. Zero end means "still recording".
    private long _startTimeUtcTicks;
    private long _coverageEndUtcTicks;

    private readonly ConcurrentDictionary<string, PropertyBuffer> _buffers = new(StringComparer.Ordinal);

    private readonly List<HistoryMove> _moves = new();
    private readonly Lock _movesLock = new();

    private long _recordedCount;
    private long _oversizeCount;

    // Evictions accumulated by buffers that have since been retired. Without this the cumulative
    // count would go backwards the moment a path is reclaimed.
    private long _retiredEvictedCount;
    private long _evictionCoverageFromUtcTicks = DateTimeOffset.MinValue.UtcTicks;

    public InMemoryHistoryStore(
        int priority, int maxPointsPerProperty, TimeSpan maxAge, int maxJsonSize, Func<DateTimeOffset> getUtcNow)
    {
        Priority = priority;
        _maxPointsPerProperty = maxPointsPerProperty;
        _maxAge = maxAge;
        _maxJsonSize = maxJsonSize;
        _getUtcNow = getUtcNow;
        _startTimeUtcTicks = getUtcNow().UtcTicks;
    }

    /// <summary>
    /// Restarts the coverage session at the current instant. The constructor already starts one, so
    /// this only narrows what the store claims: the owner calls it once its change subscription is
    /// live, so no change can fall inside claimed coverage without reaching this engine.
    /// </summary>
    internal void BeginCoverageSession()
    {
        Interlocked.Exchange(ref _startTimeUtcTicks, _getUtcNow().UtcTicks);
        Interlocked.Exchange(ref _coverageEndUtcTicks, 0);
    }

    /// <summary>
    /// Freezes coverage at the last instant this store was recording. Nothing observes it after the
    /// owner stops, so without this it keeps claiming "up to now" forever: at priority 100 the merger
    /// would route the live edge here and get empty buckets instead of falling back to a durable store.
    /// </summary>
    internal void EndCoverageSession() =>
        Interlocked.Exchange(ref _coverageEndUtcTicks, _getUtcNow().UtcTicks);

    public int Priority { get; }

    public IReadOnlySet<string> SupportedAggregations => AllAggregations;

    public long RecordedCount => Interlocked.Read(ref _recordedCount);
    public long OversizeCount => Interlocked.Read(ref _oversizeCount);
    public long EvictedCount =>
        Interlocked.Read(ref _retiredEvictedCount) + _buffers.Values.Sum(buffer => buffer.EvictedCount);
    public int TrackedPropertyCount => _buffers.Count;
    public long TotalSampleCount => _buffers.Values.Sum(buffer => (long)buffer.Count);

    // Per path: the PropertyBuffer and its Lock, the dictionary entry, and the key string's object
    // header. The key's characters are counted separately.
    private const int PerPathOverheadBytes = 160;

    /// <summary>
    /// Rough estimate of the heap the retained samples hold. Counted from each ring's current
    /// allocation rather than its sample count, because a ring holds whole slots and grows in steps.
    /// JSON values are counted from their tracked payload size, because the JsonDocument behind a
    /// JsonElement lives outside the Sample struct and a per-sample struct size misses it entirely.
    /// </summary>
    public long EstimatedMemoryBytes
    {
        get
        {
            var sampleSize = Unsafe.SizeOf<Sample>();
            var total = 0L;
            foreach (var entry in _buffers)
            {
                total += entry.Value.Capacity * (long)sampleSize
                    + entry.Value.RetainedValueBytes
                    + entry.Key.Length * sizeof(char)
                    + PerPathOverheadBytes;
            }

            return total;
        }
    }

    public ImmutableArray<HistoryCoverage> CoverageRanges
    {
        get
        {
            var now = _getUtcNow();
            var coverageEndTicks = Interlocked.Read(ref _coverageEndUtcTicks);
            if (coverageEndTicks != 0)
            {
                var frozen = new DateTimeOffset(coverageEndTicks, TimeSpan.Zero);
                if (frozen < now)
                {
                    now = frozen;
                }
            }

            var startTime = new DateTimeOffset(Interlocked.Read(ref _startTimeUtcTicks), TimeSpan.Zero);
            var ageFloor = now - _maxAge;
            var from = startTime > ageFloor ? startTime : ageFloor;

            // Coverage is store-wide and therefore all-or-nothing across property paths. Any actual
            // eviction advances a monotonic global floor. Capacity uses the affected buffer's oldest
            // retained timestamp; age eviction uses its cutoff. A buffer that has never overflowed
            // contributes no capacity floor because no earlier changes is complete history.
            var evictionFloorTicks = Interlocked.Read(ref _evictionCoverageFromUtcTicks);
            if (evictionFloorTicks > from.UtcTicks)
            {
                from = new DateTimeOffset(evictionFloorTicks, TimeSpan.Zero);
            }

            if (from > now)
            {
                from = now;
            }

            return from < now
                ? ImmutableArray.Create(new HistoryCoverage(from, now))
                : ImmutableArray<HistoryCoverage>.Empty;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always true: the engine never refuses a write. It can still discard the sample, when a late
    /// arrival is older than everything a full ring retains; that loss is reported through coverage
    /// rather than through this return value.
    /// </remarks>
    public bool TryRecord(string propertyPath, DateTimeOffset timestamp, object? value, Type propertyType)
    {
        Record(propertyPath, timestamp, value, propertyType);
        return true;
    }

    public void Record(string propertyPath, DateTimeOffset timestamp, object? value, Type propertyType)
    {
        var column = HistoryColumns.GetValueColumnFor(propertyType);
        var isUlong = HistoryColumns.IsUlongProperty(propertyType);

        var sample = CreateSample(timestamp, value, column, isUlong);

        while (true)
        {
            // Static factory over a struct state rather than a capturing lambda: the capturing form
            // built a closure object and a delegate on every recorded change, not just the first one
            // per path.
            var buffer = _buffers.GetOrAdd(
                propertyPath,
                static (_, state) => new PropertyBuffer(state.Capacity, state.Column, state.IsUlong),
                (Capacity: _maxPointsPerProperty, Column: column, IsUlong: isUlong));

            if (buffer.TryAppend(sample, out var evicted, out var oldest))
            {
                if (evicted && oldest is { } oldestRetained)
                {
                    // Capacity eviction: this buffer no longer has complete history before its oldest
                    // sample, and coverage is store-wide, so the global floor advances to that boundary.
                    AdvanceEvictionCoverageFrom(oldestRetained.UtcTicks);
                }

                break;
            }

            // A sweep retired this buffer between the lookup and the append. Drop it and take a fresh
            // one rather than losing the sample. Its count was already transferred by TryRetire.
            Retire(_buffers, propertyPath, buffer);
        }

        Interlocked.Increment(ref _recordedCount);
    }

    // Unmaps a retired buffer. The eviction count moved to the store inside TryRetire, which happens
    // exactly once per buffer, so this is a pure removal.
    private static void Retire(
        ConcurrentDictionary<string, PropertyBuffer> buffers, string propertyPath, PropertyBuffer buffer) =>
        buffers.TryRemove(new KeyValuePair<string, PropertyBuffer>(propertyPath, buffer));

    private void AdvanceEvictionCoverageFrom(long candidateUtcTicks)
    {
        var current = Interlocked.Read(ref _evictionCoverageFromUtcTicks);
        while (candidateUtcTicks > current)
        {
            var observed = Interlocked.CompareExchange(
                ref _evictionCoverageFromUtcTicks, candidateUtcTicks, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private Sample CreateSample(DateTimeOffset timestamp, object? value, ValueColumn column, bool isUlong)
    {
        if (value is null)
        {
            return new Sample(timestamp, null, null, null);
        }

        switch (column)
        {
            case ValueColumn.Double:
                return new Sample(timestamp, null, Convert.ToDouble(value, CultureInfo.InvariantCulture), null);

            case ValueColumn.Long:
                if (isUlong && value is ulong unsigned and > long.MaxValue)
                {
                    return new Sample(
                        timestamp, null, null, JsonSerializer.SerializeToElement(unsigned), JsonDocumentOverheadBytes);
                }

                return new Sample(timestamp, Convert.ToInt64(value, CultureInfo.InvariantCulture), null, null);

            case ValueColumn.Json:
            default:
                var json = SerializeJson(value, out var textLength);
                return new Sample(timestamp, null, null, json, JsonDocumentOverheadBytes + textLength);
        }
    }

    // Rough fixed cost of the JsonDocument each JsonElement sample keeps alive (the document object,
    // its metadata database and the pooled UTF-8 buffer), on top of the value's own text.
    private const int JsonDocumentOverheadBytes = 128;

    private JsonElement SerializeJson(object value, out int textLength)
    {
        // enum -> name; string -> native JSON; oversize string -> placeholder.
        JsonElement element = value is Enum
            ? JsonSerializer.SerializeToElement(value.ToString())
            : JsonSerializer.SerializeToElement(value);

        if (element.ValueKind == JsonValueKind.String)
        {
            var size = element.GetRawText().Length; // UTF-16 length is a safe upper-bound proxy for the cap
            if (size > _maxJsonSize)
            {
                Interlocked.Increment(ref _oversizeCount);
                var placeholder = JsonSerializer.SerializeToElement(new OversizePlaceholder(true, size));
                textLength = placeholder.GetRawText().Length;
                return placeholder;
            }

            textLength = size;
            return element;
        }

        textLength = element.GetRawText().Length;
        return element;
    }

    private readonly record struct OversizePlaceholder(
        [property: System.Text.Json.Serialization.JsonPropertyName("$oversize")] bool Oversize,
        [property: System.Text.Json.Serialization.JsonPropertyName("size")] int Size);

    public void RecordMove(DateTimeOffset timestamp, string fromPath, string toPath)
    {
        lock (_movesLock)
        {
            _moves.Add(new HistoryMove(timestamp, fromPath, toPath));
        }
    }

    private List<HistoryChainLeg> ResolveChain(string currentPath)
    {
        HistoryMove[] snapshot;
        lock (_movesLock)
        {
            snapshot = _moves.ToArray();
        }

        return HistoryMoveChain.Resolve(snapshot, currentPath);
    }

    // Column/IsUlong for a (possibly moved) property: the first buffer found along its chain.
    private PropertyBuffer? ResolveBuffer(string propertyPath)
    {
        foreach (var leg in ResolveChain(propertyPath))
        {
            if (_buffers.TryGetValue(leg.Path, out var buffer))
            {
                return buffer;
            }
        }

        return null;
    }

    private List<Sample> RangeAcrossChain(List<HistoryChainLeg> chain, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<Sample>();
        foreach (var leg in chain)
        {
            if (!_buffers.TryGetValue(leg.Path, out var buffer))
            {
                continue;
            }

            var legFrom = from > leg.ValidFrom ? from : leg.ValidFrom;
            var legTo = to < leg.ValidTo ? to : leg.ValidTo;
            if (legFrom < legTo)
            {
                result.AddRange(buffer.Range(legFrom, legTo));
            }
        }

        result.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return result;
    }

    public Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Query(query));
    }

    public ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(
        string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<HistoryPoint?>(GetSampleAtOrBefore(propertyPath, asOf));
    }

    public HistoryPoint? GetSampleAtOrBefore(string propertyPath, DateTimeOffset asOf)
    {
        var ranges = CoverageRanges;
        if (ranges.IsEmpty)
        {
            return null;
        }

        var coverage = ranges[0];
        if (asOf < coverage.From || asOf > coverage.To)
        {
            return null;
        }

        foreach (var leg in ResolveChain(propertyPath))
        {
            // Only legs whose validity starts at or before asOf can hold the value.
            if (leg.ValidFrom > asOf)
            {
                continue;
            }

            if (_buffers.TryGetValue(leg.Path, out var buffer))
            {
                var ceiling = asOf < leg.ValidTo ? asOf : leg.ValidTo - new TimeSpan(1);
                var sample = buffer.AtOrBefore(ceiling);
                if (sample is { } found &&
                    found.Timestamp >= leg.ValidFrom &&
                    found.Timestamp >= coverage.From)
                {
                    return InMemoryHistoryAggregation.ToPoint(found, buffer.IsUlong);
                }
            }
        }

        return null;
    }

    public HistorySeries Query(HistoryQuery query)
    {
        query.Validate();
        return query.Bucket is null ? QueryRaw(query) : QueryBucketed(query);
    }

    private HistorySeries QueryRaw(HistoryQuery query)
    {
        var samples = new List<(Sample Sample, bool IsUlong)>();
        foreach (var leg in ResolveChain(query.PropertyPath))
        {
            if (!_buffers.TryGetValue(leg.Path, out var buffer))
            {
                continue;
            }

            var from = query.From > leg.ValidFrom ? query.From : leg.ValidFrom;
            var to = query.To < leg.ValidTo ? query.To : leg.ValidTo;
            if (from >= to)
            {
                continue;
            }

            foreach (var sample in buffer.Range(from, to))
            {
                samples.Add((sample, buffer.IsUlong));
            }
        }

        samples.Sort((left, right) => left.Sample.Timestamp.CompareTo(right.Sample.Timestamp));

        var truncated = samples.Count > query.MaxPoints;
        var kept = truncated ? samples.Skip(samples.Count - query.MaxPoints) : samples; // newest-N
        var points = kept
            .Select(entry => InMemoryHistoryAggregation.ToPoint(entry.Sample, entry.IsUlong))
            .ToImmutableArray();
        return new HistorySeries(query.PropertyPath, points, truncated, GetQueryCoverage(query));
    }

    private HistorySeries QueryBucketed(HistoryQuery query)
    {
        var bucket = query.Bucket!.Value;
        var aggregation = query.Aggregation;

        var chain = ResolveChain(query.PropertyPath);
        var buffer = ResolveBuffer(query.PropertyPath);
        var isUlong = buffer?.IsUlong ?? false;
        var coverageRanges = CoverageRanges;

        if (buffer is not null && buffer.Column == ValueColumn.Json && !isUlong &&
            InMemoryHistoryAggregation.IsNumeric(aggregation))
        {
            throw new HistoryAggregationNotSupportedException(
                aggregation,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    HistoryAggregations.Last, HistoryAggregations.First, HistoryAggregations.Count
                });
        }

        var carriedNumber = InMemoryHistoryAggregation.IsCarryDependent(aggregation)
            ? query.CarrySeed?.Number
            : null;
        var carriedJson = InMemoryHistoryAggregation.IsCarryDependent(aggregation)
            ? query.CarrySeed?.Json
            : null;
        var alignedFrom = BucketAlignment.BucketStart(query.From, bucket);
        var firstBucketStart = BucketAlignment.FirstBucketStart(
            query.From, query.To, bucket, query.MaxPoints);
        var bucketStart = firstBucketStart;

        // When work was clipped to the newest buckets, advance carry to the clipped boundary. Otherwise
        // fall back to this store's own held value when the merger supplied no seed, for Last as well as
        // TimeWeightedAverage: both carry forward, and restricting the look-back to one of them left a
        // direct Last query returning nulls for a value the store was holding all along.
        if (InMemoryHistoryAggregation.IsCarryDependent(aggregation) &&
            (bucketStart > alignedFrom || query.CarrySeed is null))
        {
            var prior = GetSampleAtOrBefore(query.PropertyPath, bucketStart);
            if (prior is not null)
            {
                carriedNumber = prior.Number;
                carriedJson = prior.Json;
            }
        }

        // The whole window is read once, then walked with a cursor. Querying per bucket took the
        // buffer's lock and allocated a fresh list for every bucket, so a 1000-bucket chart cost 1000
        // lock acquisitions and 1000 allocations to read samples that one pass already has in order.
        var windowSamples = CollectionsMarshal.AsSpan(RangeAcrossChain(chain, firstBucketStart, query.To));
        var cursor = 0;

        var allPoints = new List<HistoryPoint>();
        while (bucketStart < query.To)
        {
            var bucketEnd = bucketStart + bucket;

            // Samples are ascending, so the bucket's slice starts at the cursor and runs to the first
            // sample at or after bucketEnd. The cursor advances past skipped buckets too, so an
            // uncovered stretch cannot leave older samples in the next bucket's slice.
            var sliceEnd = cursor;
            while (sliceEnd < windowSamples.Length && windowSamples[sliceEnd].Timestamp < bucketEnd)
            {
                sliceEnd++;
            }

            var bucketSamples = windowSamples[cursor..sliceEnd];
            cursor = sliceEnd;

            // Clipped to the query window: the newest bucket runs past To whenever To is not
            // bucket-aligned, and coverage cannot reach into the future (see HistoryDispatchPlanner).
            var coveredRange = new HistoryCoverage(bucketStart, bucketEnd < query.To ? bucketEnd : query.To);
            if (coverageRanges.IsEmpty || !coverageRanges[0].Contains(coveredRange))
            {
                carriedNumber = null;
                carriedJson = null;
                allPoints.Add(new HistoryPoint(bucketStart, null, null));
                bucketStart = bucketEnd;
                continue;
            }

            var point = InMemoryHistoryAggregation.AggregateBucket(
                aggregation,
                bucketStart,
                bucketEnd,
                bucketSamples,
                isUlong,
                ref carriedNumber,
                ref carriedJson);
            allPoints.Add(point);

            bucketStart = bucketEnd;
        }

        var truncated = firstBucketStart > alignedFrom;

        return new HistorySeries(
            query.PropertyPath,
            allPoints.ToImmutableArray(),
            truncated,
            GetQueryCoverage(query));
    }

    private ImmutableArray<HistoryCoverage> GetQueryCoverage(HistoryQuery query) =>
        HistoryCoverage.Clip(CoverageRanges, new HistoryCoverage(query.From, query.To));

    public void Sweep()
    {
        var cutoff = _getUtcNow() - _maxAge;
        var anyEvicted = false;
        foreach (var entry in _buffers)
        {
            if (entry.Value.EvictOlderThan(cutoff) > 0)
            {
                anyEvicted = true;
            }

            // A path whose samples have all aged out is reclaimed outright. Canonical paths embed
            // collection indices, so a reorder abandons one path per renamed subject and nothing else
            // would ever free them.
            if (entry.Value.TryRetire(out var retiredEvictions))
            {
                Interlocked.Add(ref _retiredEvictedCount, retiredEvictions);
                Retire(_buffers, entry.Key, entry.Value);
            }
        }

        if (anyEvicted)
        {
            AdvanceEvictionCoverageFrom(cutoff.UtcTicks);
        }

        // Moves describing samples that no longer exist only slow down chain resolution, which every
        // query and look-back performs. The newest move at or before the cutoff is kept, because that
        // is the one bounding the leg that covers the cutoff instant.
        lock (_movesLock)
        {
            // Found rather than walked from the front. Timestamps come from the recorded change, so a
            // device reporting out of order appends an older move after a newer one, and a prefix walk
            // then stopped at the first entry above the cutoff and deleted the newer moves before it.
            // Recording ts=5 and then ts=0 with a cutoff of 0 discarded the ts=5 move outright.
            DateTimeOffset? newestAtOrBeforeCutoff = null;
            foreach (var move in _moves)
            {
                if (move.Timestamp <= cutoff &&
                    (newestAtOrBeforeCutoff is null || move.Timestamp > newestAtOrBeforeCutoff))
                {
                    newestAtOrBeforeCutoff = move.Timestamp;
                }
            }

            if (newestAtOrBeforeCutoff is { } keepFrom)
            {
                _moves.RemoveAll(move => move.Timestamp < keepFrom);
            }
        }
    }
}
