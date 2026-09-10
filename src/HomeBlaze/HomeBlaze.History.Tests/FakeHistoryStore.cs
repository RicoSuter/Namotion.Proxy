using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Tests;

/// <summary>
/// Configurable in-memory <see cref="IHistoryStore"/> for deterministic merger tests. Holds a flat
/// list of samples; serves raw queries as the newest-N samples in range and bucketed queries by
/// emitting one point per epoch-aligned bucket (using the latest in-bucket sample's value, or the
/// carried value for empty buckets when the aggregation is carry-dependent).
/// </summary>
public sealed class FakeHistoryStore : IHistoryStore
{
    private readonly List<HistoryPoint> _samples = new();

    public int Priority { get; set; }

    // Convenience for the existing single-range routing tests.
    public HistoryCoverage CurrentCoverage { get; set; }

    private ImmutableArray<HistoryCoverage> _coverageRanges;

    public ImmutableArray<HistoryCoverage> CoverageRanges
    {
        get => _coverageRanges.IsDefault
            ? ImmutableArray.Create(CurrentCoverage)
            : _coverageRanges;
        set => _coverageRanges = value;
    }

    public IReadOnlySet<string> SupportedAggregations { get; set; } =
        new HashSet<string>(StringComparer.Ordinal)
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

    /// <summary>Records the queries this store received, for routing assertions.</summary>
    public List<HistoryQuery> ReceivedQueries { get; } = new();

    /// <summary>When true, both query methods throw, simulating a misconfigured store.</summary>
    public bool ThrowOnQuery { get; set; }

    public FakeHistoryStore AddSample(DateTimeOffset timestamp, double value)
    {
        _samples.Add(new HistoryPoint(timestamp, value, null));
        _samples.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return this;
    }

    /// <summary>
    /// Adds a Json-valued sample (string or enum). Mirrors <see cref="AddSample(DateTimeOffset, double)"/>
    /// for the <c>value_json</c> column so carry-forward of non-numeric properties can be exercised.
    /// </summary>
    public FakeHistoryStore AddJsonSample(DateTimeOffset timestamp, JsonElement value)
    {
        _samples.Add(new HistoryPoint(timestamp, null, value));
        _samples.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return this;
    }

    public FakeHistoryStore AddNullSample(DateTimeOffset timestamp)
    {
        _samples.Add(new HistoryPoint(timestamp, null, null));
        _samples.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return this;
    }

    public Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ThrowOnQuery)
        {
            throw new InvalidOperationException("Fake store failure.");
        }

        ReceivedQueries.Add(query);

        var points = query.Bucket is null
            ? QueryRaw(query)
            : QueryBucketed(query);

        var truncated = points.Count > query.MaxPoints;
        if (truncated)
        {
            // Keep the newest N (the newest-N contract).
            points = points.Skip(points.Count - query.MaxPoints).ToList();
        }

        return Task.FromResult(new HistorySeries(
            query.PropertyPath,
            points.ToImmutableArray(),
            truncated,
            CoverageRanges));
    }

    public ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(
        string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        if (ThrowOnQuery)
        {
            throw new InvalidOperationException("Fake store failure.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<HistoryPoint?>(SampleAtOrBefore(asOf));
    }

    private HistoryPoint? SampleAtOrBefore(DateTimeOffset asOf)
    {
        HistoryCoverage? containingRange = null;
        for (var index = CoverageRanges.Length - 1; index >= 0; index--)
        {
            var range = CoverageRanges[index];
            if (range.From <= asOf && range.To >= asOf)
            {
                containingRange = range;
                break;
            }
        }

        if (containingRange is null)
        {
            return null;
        }

        // Restricted to the containing range, matching both real engines: a sample from before the
        // range this store vouches for must not seed a carry, which is what stops a held value being
        // synthesized across a restart or a drop gap.
        HistoryPoint? result = null;
        foreach (var sample in _samples)
        {
            if (sample.Timestamp >= containingRange.Value.From && sample.Timestamp <= asOf)
            {
                result = sample;
            }
        }

        return result;
    }

    private List<HistoryPoint> QueryRaw(HistoryQuery query)
    {
        return _samples
            .Where(sample => sample.Timestamp >= query.From && sample.Timestamp < query.To)
            .OrderBy(sample => sample.Timestamp)
            .ToList();
    }

    private List<HistoryPoint> QueryBucketed(HistoryQuery query)
    {
        var bucket = query.Bucket!.Value;
        var carryDependent = query.Aggregation is HistoryAggregations.Last or HistoryAggregations.TimeWeightedAverage;

        // The held value carries both the numeric and the Json column so carry-forward works for
        // value_json properties (string or enum) exactly as it does for numeric ones.
        var carriedNumber = carryDependent ? query.CarrySeed?.Number : null;
        var carriedJson = carryDependent ? query.CarrySeed?.Json : null;

        // Both real engines fall back to their own held value when the merger supplied no seed, so a
        // double without it models a store that does not exist. The fallback goes through the same
        // coverage-gated lookup they use: reading raw samples instead would carry a value straight
        // across a gap this store does not vouch for.
        if (carryDependent && query.CarrySeed is null)
        {
            var prior = SampleAtOrBefore(BucketAlignment.BucketStart(query.From, bucket));
            if (prior is not null)
            {
                carriedNumber = prior.Number;
                carriedJson = prior.Json;
            }
        }

        var points = new List<HistoryPoint>();
        var bucketStart = BucketAlignment.BucketStart(query.From, bucket);
        while (bucketStart < query.To)
        {
            var bucketEnd = bucketStart + bucket;

            // Clipped to the query window, matching the real engines: a trailing bucket that runs past
            // To must not aggregate samples from after To into its point.
            var sliceEnd = bucketEnd < query.To ? bucketEnd : query.To;
            var inBucket = _samples
                .Where(sample => sample.Timestamp >= bucketStart && sample.Timestamp < sliceEnd)
                .ToList();

            if (inBucket.Count > 0)
            {
                carriedNumber = inBucket[^1].Number;
                carriedJson = inBucket[^1].Json;
                points.Add(new HistoryPoint(bucketStart, carriedNumber, carriedJson));
            }
            else if (carryDependent)
            {
                // Empty bucket: carry the held value forward for carry-dependent aggregations.
                points.Add(new HistoryPoint(bucketStart, carriedNumber, carriedJson));
            }
            else
            {
                // Empty bucket for a non-carry aggregation: an explicit gap.
                points.Add(new HistoryPoint(bucketStart, null, null));
            }

            bucketStart = bucketEnd;
        }

        return points;
    }
}
