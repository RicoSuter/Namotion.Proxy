using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// One per bucketed query. Fed a map of <c>bucketStartTicks -&gt; combined <see cref="BucketPartial"/></c>
/// (already merged across partitions) and emits one <see cref="HistoryPoint"/> per aligned bucket in
/// <c>[BucketStart(from) .. &lt; to)</c>, applying the SAME empty-bucket and carry rules as
/// <c>InMemoryHistoryStore.AggregateBucket</c>/<c>AggregateNumeric</c>. Numeric partials combine across
/// partitions (Count sum, Sum sum, Min/Max min/max, SampleAverage=Sum/Count, StandardDeviation from Count+Sum+SumOfSquares);
/// First picks the smallest <c>FirstTicks</c>, Last the largest <c>LastTicks</c>; TWA sums weighted_sum and
/// total_duration (Task 5.4 owns the TWA value math).
/// </summary>
internal readonly record struct BucketPartial(
    long BucketStartTicks,
    long Count,
    double? Sum, double? Min, double? Max, double? SumOfSquares,   // numeric reductions
    long? FirstTicks, double? FirstNumber, string? FirstJson,       // earliest sample in bucket
    long? LastTicks, double? LastNumber, string? LastJson,          // latest sample in bucket
    double WeightedSum, double TotalDuration)                       // TWA partials
{
    /// <summary>
    /// Combines two partials for the same bucket (the per-partition reductions) into one.
    /// </summary>
    public static BucketPartial Combine(BucketPartial left, BucketPartial right)
    {
        var first = SmallerFirst(left, right);
        var last = LargerLast(left, right);

        return new BucketPartial(
            left.BucketStartTicks,
            left.Count + right.Count,
            AddNullable(left.Sum, right.Sum),
            MinNullable(left.Min, right.Min),
            MaxNullable(left.Max, right.Max),
            AddNullable(left.SumOfSquares, right.SumOfSquares),
            first.FirstTicks, first.FirstNumber, first.FirstJson,
            last.LastTicks, last.LastNumber, last.LastJson,
            left.WeightedSum + right.WeightedSum,
            left.TotalDuration + right.TotalDuration);
    }

    private static BucketPartial SmallerFirst(BucketPartial left, BucketPartial right)
    {
        if (left.FirstTicks is null) return right;
        if (right.FirstTicks is null) return left;
        return right.FirstTicks.Value < left.FirstTicks.Value ? right : left;
    }

    private static BucketPartial LargerLast(BucketPartial left, BucketPartial right)
    {
        if (left.LastTicks is null) return right;
        if (right.LastTicks is null) return left;
        return right.LastTicks.Value > left.LastTicks.Value ? right : left;
    }

    private static double? AddNullable(double? left, double? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left.Value + right.Value;
    }

    private static double? MinNullable(double? left, double? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return Math.Min(left.Value, right.Value);
    }

    private static double? MaxNullable(double? left, double? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return Math.Max(left.Value, right.Value);
    }
}

/// <summary>
/// Walks the aligned bucket range for a query, applies the InMemory empty-bucket and carry semantics,
/// and produces the final <see cref="HistoryPoint"/> list (newest-N over buckets).
/// </summary>
internal static class BucketAssembler
{
    public static HistorySeries Assemble(
        HistoryQuery query,
        IReadOnlyDictionary<long, BucketPartial> partials,
        double? carrySeedNumber,
        JsonElement? carrySeedJson,
        ImmutableArray<HistoryCoverage> coverageRanges)
    {
        var bucket = query.Bucket!.Value;
        var aggregation = query.Aggregation;

        // For Last, the carry threads the held value (Number AND Json) bucket to bucket, seeded for the
        // leading empty bucket by the CarrySeed supplied by the merger.
        var carriedNumber = IsCarryDependent(aggregation) ? carrySeedNumber : null;
        var carriedJson = IsCarryDependent(aggregation) ? carrySeedJson : null;

        var bucketTicks = bucket.Ticks;
        var alignedFrom = BucketAlignment.BucketStart(query.From, bucket);
        var firstBucketStart = BucketAlignment.FirstBucketStart(
            query.From, query.To, bucket, query.MaxPoints);
        var bucketStartTimestamp = firstBucketStart;
        var allPoints = new List<HistoryPoint>();
        var coverageIndex = 0;
        while (bucketStartTimestamp < query.To)
        {
            var bucketEndTimestamp = bucketStartTimestamp + bucket;
            while (coverageIndex < coverageRanges.Length &&
                   coverageRanges[coverageIndex].To <= bucketStartTimestamp)
            {
                coverageIndex++;
            }

            // Clipped to the query window: the newest bucket runs past To whenever To is not
            // bucket-aligned, and coverage cannot reach into the future (see HistoryDispatchPlanner).
            var coveredRange = new HistoryCoverage(
                bucketStartTimestamp,
                bucketEndTimestamp < query.To ? bucketEndTimestamp : query.To);

            if (coverageIndex >= coverageRanges.Length ||
                !coverageRanges[coverageIndex].Contains(coveredRange))
            {
                carriedNumber = null;
                carriedJson = null;
                allPoints.Add(new HistoryPoint(bucketStartTimestamp, null, null));
                bucketStartTimestamp = bucketEndTimestamp;
                continue;
            }

            var bucketStartTicks = EpochTicks.ToEpochTicks(bucketStartTimestamp);
            partials.TryGetValue(bucketStartTicks, out var partial);
            var hasPartial = partials.ContainsKey(bucketStartTicks);

            var point = AggregateBucket(
                aggregation, bucketStartTimestamp, bucketStartTicks, bucketTicks,
                hasPartial ? partial : null, ref carriedNumber, ref carriedJson);
            allPoints.Add(point);

            bucketStartTimestamp = bucketEndTimestamp;
        }

        return new HistorySeries(
            query.PropertyPath,
            allPoints.ToImmutableArray(),
            firstBucketStart > alignedFrom,
            ImmutableArray<HistoryCoverage>.Empty);
    }

    private static bool IsCarryDependent(string aggregation) =>
        aggregation is HistoryAggregations.Last or HistoryAggregations.TimeWeightedAverage;

    private static HistoryPoint AggregateBucket(
        string aggregation, DateTimeOffset bucketStart, long bucketStartTicks, long bucketTicks,
        BucketPartial? partial, ref double? carriedNumber, ref JsonElement? carriedJson)
    {
        switch (aggregation)
        {
            case HistoryAggregations.Count:
                return new HistoryPoint(bucketStart, partial?.Count ?? 0, null);

            case HistoryAggregations.Last:
                if (partial is { LastTicks: not null } lastPartial)
                {
                    carriedNumber = lastPartial.LastNumber;
                    carriedJson = ParseJson(lastPartial.LastJson);
                }

                return new HistoryPoint(bucketStart, carriedNumber, carriedJson);

            case HistoryAggregations.First:
                if (partial is { FirstTicks: not null } firstPartial)
                {
                    return new HistoryPoint(bucketStart, firstPartial.FirstNumber, ParseJson(firstPartial.FirstJson));
                }

                return new HistoryPoint(bucketStart, null, null);

            case HistoryAggregations.TimeWeightedAverage:
                return TimeWeightedAverage(bucketStart, bucketStartTicks, bucketTicks, partial, ref carriedNumber);

            default:
                return AggregateNumeric(aggregation, bucketStart, partial);
        }
    }

    // Time-weighted average for one bucket. The SQL partial covers only the IN-BUCKET integral
    // [firstEventTs, bucketEnd); the value held entering the bucket (carry / look-back / seed) is integrated
    // over the leading interval [bucketStart, firstEventTs) here, and over the WHOLE bucket when it is empty.
    // The carry then advances to the bucket's last event, including explicit null. Mirrors
    // InMemory.TimeWeightedAverageBucket;
    // ticks vs seconds does not matter because the ratio weightedSum/totalDuration is unit-free.
    private static HistoryPoint TimeWeightedAverage(
        DateTimeOffset bucketStart, long bucketStartTicks, long bucketTicks,
        BucketPartial? partial, ref double? carriedNumber)
    {
        if (partial is { FirstTicks: { } firstTicks } combined)
        {
            // Leading interval [bucketStart, firstEventTs): the held value (if any) over that gap.
            var weightedSum = combined.WeightedSum;
            var totalDuration = combined.TotalDuration;
            if (carriedNumber is { } held)
            {
                var leadingDuration = (double)(firstTicks - bucketStartTicks);
                if (leadingDuration > 0)
                {
                    weightedSum += held * leadingDuration;
                    totalDuration += leadingDuration;
                }
            }

            // Advance carry even when the last event is explicit null, which clears the held value.
            if (combined.LastTicks is not null)
            {
                carriedNumber = combined.LastNumber;
            }

            return new HistoryPoint(bucketStart, totalDuration > 0 ? weightedSum / totalDuration : null, null);
        }

        // Empty bucket (no events): the held value, if any, covers the whole bucket -> that value.
        if (carriedNumber is { } heldWhole && bucketTicks > 0)
        {
            return new HistoryPoint(bucketStart, heldWhole, null);
        }

        return new HistoryPoint(bucketStart, null, null);
    }

    private static HistoryPoint AggregateNumeric(string aggregation, DateTimeOffset bucketStart, BucketPartial? partial)
    {
        // Empty bucket (no samples or no numeric values) -> null for every numeric aggregation.
        if (partial is not { } combined || combined.Count == 0)
        {
            return new HistoryPoint(bucketStart, null, null);
        }

        double? result = aggregation switch
        {
            HistoryAggregations.SampleAverage => combined.Sum / combined.Count,
            HistoryAggregations.Minimum => combined.Min,
            HistoryAggregations.Maximum => combined.Max,
            HistoryAggregations.Sum => combined.Sum,
            HistoryAggregations.StandardDeviation => SampleStandardDeviation(combined),
            _ => throw new HistoryAggregationNotSupportedException(
                aggregation,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    HistoryAggregations.Last, HistoryAggregations.First, HistoryAggregations.Count
                })
        };

        return new HistoryPoint(bucketStart, result, null);
    }

    // Sample standard deviation from the combined Count, Sum, and SumOfSquares; null for n < 2.
    // Var = (SumOfSquares - Sum^2 / n) / (n - 1).
    private static double? SampleStandardDeviation(BucketPartial partial)
    {
        if (partial.Count < 2 || partial.Sum is not { } sum || partial.SumOfSquares is not { } sumSquares)
        {
            return null; // sample stddev is undefined for n < 2
        }

        var count = (double)partial.Count;
        var variance = (sumSquares - sum * sum / count) / (count - 1);
        if (variance < 0)
        {
            variance = 0; // guard against tiny negative rounding error
        }

        return Math.Sqrt(variance);
    }

    private static JsonElement? ParseJson(string? jsonText)
    {
        if (jsonText is null)
        {
            return null;
        }

        // Cloning detaches the element, but the document still has to be disposed or its pooled buffers
        // are never returned: this runs once per point, so a large query leaks thousands of rentals.
        using var document = JsonDocument.Parse(jsonText);
        return document.RootElement.Clone();
    }
}
