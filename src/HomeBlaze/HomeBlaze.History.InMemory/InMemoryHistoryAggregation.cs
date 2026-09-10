using System.Text.Json;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.InMemory;

/// <summary>
/// Stateless bucket aggregation and stored-sample mapping for the in-memory history store.
/// </summary>
internal static class InMemoryHistoryAggregation
{
    public static bool IsCarryDependent(string aggregation) =>
        aggregation is HistoryAggregations.Last or HistoryAggregations.TimeWeightedAverage;

    public static bool IsNumeric(string aggregation) =>
        aggregation is HistoryAggregations.SampleAverage or HistoryAggregations.TimeWeightedAverage
            or HistoryAggregations.Minimum or HistoryAggregations.Maximum
            or HistoryAggregations.Sum or HistoryAggregations.StandardDeviation;

    public static HistoryPoint AggregateBucket(
        string aggregation,
        DateTimeOffset bucketStart,
        DateTimeOffset bucketEnd,
        ReadOnlySpan<Sample> samples,
        bool isUlong,
        ref double? carriedNumber,
        ref JsonElement? carriedJson)
    {
        switch (aggregation)
        {
            case HistoryAggregations.Count:
                return new HistoryPoint(bucketStart, samples.Length, null);

            case HistoryAggregations.Last:
                if (samples.Length > 0)
                {
                    var lastPoint = ToPoint(samples[^1], isUlong);
                    carriedNumber = lastPoint.Number;
                    carriedJson = lastPoint.Json;
                }

                return new HistoryPoint(bucketStart, carriedNumber, carriedJson);

            case HistoryAggregations.First:
                return samples.Length > 0
                    ? ToPoint(samples[0], isUlong) with { Timestamp = bucketStart }
                    : new HistoryPoint(bucketStart, null, null);

            case HistoryAggregations.TimeWeightedAverage:
                return TimeWeightedAverage(
                    bucketStart, bucketEnd, samples, isUlong, ref carriedNumber);

            default:
                return AggregateNumeric(aggregation, bucketStart, samples, isUlong);
        }
    }

    public static HistoryPoint ToPoint(Sample sample, bool isUlong)
    {
        if (sample.Double is { } doubleValue)
        {
            return new HistoryPoint(sample.Timestamp, doubleValue, null);
        }

        if (sample.Long is { } longValue)
        {
            return new HistoryPoint(sample.Timestamp, longValue, null);
        }

        if (sample.Json is { } json)
        {
            var number = isUlong && json.ValueKind == JsonValueKind.Number
                ? json.GetDouble()
                : (double?)null;
            return new HistoryPoint(sample.Timestamp, number, json);
        }

        return new HistoryPoint(sample.Timestamp, null, null);
    }

    private static HistoryPoint TimeWeightedAverage(
        DateTimeOffset bucketStart,
        DateTimeOffset bucketEnd,
        ReadOnlySpan<Sample> samples,
        bool isUlong,
        ref double? carriedNumber)
    {
        double weightedSum = 0;
        double totalDuration = 0;
        var previousTimestamp = bucketStart;
        var previousValue = carriedNumber;

        foreach (var sample in samples)
        {
            var duration = (sample.Timestamp - previousTimestamp).TotalSeconds;
            if (previousValue is { } held && duration > 0)
            {
                weightedSum += held * duration;
                totalDuration += duration;
            }

            // An explicit null terminates LOCF until a later numeric event establishes a value.
            previousValue = Numeric(sample, isUlong);
            previousTimestamp = sample.Timestamp;
        }

        var tailDuration = (bucketEnd - previousTimestamp).TotalSeconds;
        if (previousValue is { } tailHeld && tailDuration > 0)
        {
            weightedSum += tailHeld * tailDuration;
            totalDuration += tailDuration;
        }

        carriedNumber = previousValue;
        return new HistoryPoint(
            bucketStart,
            totalDuration > 0 ? weightedSum / totalDuration : null,
            null);
    }

    // One pass over the bucket's samples, accumulating everything the numeric aggregations need. The
    // previous version materialized a List<double> of the numeric values for every bucket, which for a
    // 1000-bucket query is 1000 list allocations plus the LINQ chain that fills them.
    private static HistoryPoint AggregateNumeric(
        string aggregation,
        DateTimeOffset bucketStart,
        ReadOnlySpan<Sample> samples,
        bool isUlong)
    {
        var count = 0;
        var sum = 0d;
        var minimum = double.PositiveInfinity;
        var maximum = double.NegativeInfinity;

        // Welford: running mean plus the sum of squared deviations from it. Numerically steadier than
        // summing the squares and subtracting, and it needs no second pass over the samples.
        var mean = 0d;
        var sumOfSquaredDeviations = 0d;

        foreach (var sample in samples)
        {
            if (Numeric(sample, isUlong) is not { } value)
            {
                continue;
            }

            count++;
            sum += value;
            if (value < minimum) minimum = value;
            if (value > maximum) maximum = value;

            var delta = value - mean;
            mean += delta / count;
            sumOfSquaredDeviations += delta * (value - mean);
        }

        if (count == 0)
        {
            return new HistoryPoint(bucketStart, null, null);
        }

        double? result = aggregation switch
        {
            HistoryAggregations.SampleAverage => sum / count,
            HistoryAggregations.Minimum => minimum,
            HistoryAggregations.Maximum => maximum,
            HistoryAggregations.Sum => sum,
            HistoryAggregations.StandardDeviation => count < 2
                ? null
                : Math.Sqrt(sumOfSquaredDeviations / (count - 1)),
            _ => throw new HistoryAggregationNotSupportedException(
                aggregation,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    HistoryAggregations.Last,
                    HistoryAggregations.First,
                    HistoryAggregations.Count
                })
        };

        return new HistoryPoint(bucketStart, result, null);
    }

    private static double? Numeric(Sample sample, bool isUlong)
    {
        if (sample.Double is { } doubleValue)
        {
            return doubleValue;
        }

        if (sample.Long is { } longValue)
        {
            return longValue;
        }

        return isUlong && sample.Json is { ValueKind: JsonValueKind.Number } json
            ? json.GetDouble()
            : null;
    }
}
