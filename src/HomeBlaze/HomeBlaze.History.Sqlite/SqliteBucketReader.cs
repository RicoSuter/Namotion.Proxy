using System.Collections.Immutable;
using HomeBlaze.History.Abstractions;
using Microsoft.Data.Sqlite;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// Pure bucketed-aggregation read SQL for the SQLite history engine: the bucketed query orchestration
/// plus the four partial readers (first/last edge, count, numeric reductions, and the
/// time-weighted-average ordered event scan). It reuses <see cref="SqliteHistoryReader"/> for move-chain
/// resolution and column metadata, <see cref="SqliteValueRouting"/> for value mapping, and
/// <see cref="BucketAssembler"/> for final assembly. Every method takes a <see cref="SqliteReadContext"/>
/// (the engine's open-connection delegates plus partition layout); these helpers never lock and never
/// touch the engine's connection cache. The engine calls them while holding its connection lock.
/// </summary>
internal static class SqliteBucketReader
{
    public static HistorySeries QueryBucketed(
        SqliteReadContext context,
        HistoryQuery query,
        Func<string, DateTimeOffset, HistoryPoint?> getSampleAtOrBefore,
        ImmutableArray<HistoryCoverage> coverageRanges,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var bucket = query.Bucket!.Value;
        var aggregation = query.Aggregation;
        var bucketTicks = bucket.Ticks;

        // Resolve the move chain once: each leg owns its [ValidFrom, ValidTo) slice of time and stores its
        // samples under its own path. With no moves this is a single unbounded leg under query.PropertyPath.
        var chain = SqliteHistoryReader.ResolveChain(context, query.PropertyPath);

        // Resolve the stored column kind and ulong flag from the paths table along the chain (the SQLite
        // equivalent of the InMemory buffer's Column/IsUlong, which uses the first buffer in the chain).
        // A numeric aggregation on a json-stored, non-ulong property (string/enum) is not
        // supported, mirroring InMemoryHistoryStore.QueryBucketed.
        var meta = SqliteHistoryReader.ResolveColumnMeta(context, chain);
        var isUlong = meta?.IsUlong ?? false;

        if (meta is { Column: ValueColumn.Json } && !isUlong && IsNumericAggregation(aggregation))
        {
            throw new HistoryAggregationNotSupportedException(
                aggregation,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    HistoryAggregations.Last, HistoryAggregations.First, HistoryAggregations.Count
                });
        }

        // The aligned bucket range, intersected per leg below. A bucket straddling a move boundary draws its
        // samples from whichever leg owns each instant, exactly like InMemory's RangeAcrossChain.
        var alignedFrom = BucketAlignment.FirstBucketStart(
            query.From, query.To, bucket, query.MaxPoints);

        // Expand the chain into concrete (path, tickWindow) segments over existing partition files.
        var segments = BuildChainSegments(context, chain, alignedFrom, query.To);

        Dictionary<long, BucketPartial> partials;
        if (aggregation == HistoryAggregations.TimeWeightedAverage)
        {
            partials = ReadTimeWeightedAveragePartials(
                context, segments, isUlong, bucketTicks, cancellationToken);
        }
        else
        {
            partials = new Dictionary<long, BucketPartial>();
            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var connection = context.OpenPartition(segment.PartitionKey);
                if (connection is null || SqliteHistoryReader.ResolvePathId(connection, segment.Path) is not { } pathId)
                {
                    continue; // unreadable partition, or one that never saw the path
                }

                foreach (var partial in ReadPartials(connection, pathId, aggregation, isUlong,
                             bucketTicks, segment.FromTicks, segment.ToTicks))
                {
                    partials[partial.BucketStartTicks] = partials.TryGetValue(partial.BucketStartTicks, out var existing)
                        ? BucketPartial.Combine(existing, partial)
                        : partial;
                }
            }
        }

        var isCarryDependent = aggregation is
            HistoryAggregations.Last or HistoryAggregations.TimeWeightedAverage;
        var carrySeedNumber = isCarryDependent ? query.CarrySeed?.Number : null;
        var carrySeedJson = aggregation == HistoryAggregations.Last ? query.CarrySeed?.Json : null;
        var originalAlignedFrom = BucketAlignment.BucketStart(query.From, bucket);

        // When MaxPoints clipped older buckets, advance the held value to the clipped boundary.
        // Otherwise fall back to this store's own held value when the merger supplied no seed, for Last
        // as well as TimeWeightedAverage: both carry forward, and restricting the look-back to one of
        // them made a direct Last query answer differently here than in the in-memory engine, which the
        // two are meant to be interchangeable for.
        if (isCarryDependent &&
            (alignedFrom > originalAlignedFrom || query.CarrySeed is null))
        {
            var prior = getSampleAtOrBefore(query.PropertyPath, alignedFrom);
            if (prior is not null)
            {
                carrySeedNumber = prior.Number;
                carrySeedJson = prior.Json;
            }
        }

        return BucketAssembler.Assemble(
            query, partials, carrySeedNumber, carrySeedJson, coverageRanges);
    }

    // Expands a chain into concrete (path, partitionKey, tickWindow) segments over EXISTING partition files.
    // For each leg, the query window [from, to) is intersected with the leg's [ValidFrom, ValidTo); the
    // intersection is split across the partition files it overlaps. With no moves this is the single-path,
    // multi-partition segment set used before move routing.
    private static List<ChainSegment> BuildChainSegments(
        SqliteReadContext context, List<HistoryChainLeg> chain, DateTimeOffset from, DateTimeOffset to)
    {
        var segments = new List<ChainSegment>();
        foreach (var leg in chain)
        {
            var legFrom = from > leg.ValidFrom ? from : leg.ValidFrom;
            var legTo = to < leg.ValidTo ? to : leg.ValidTo;
            if (legFrom >= legTo)
            {
                continue;
            }

            var fromTicks = EpochTicks.ToEpochTicks(legFrom);
            var toTicks = EpochTicks.ToEpochTicks(legTo);
            foreach (var key in context.PartitionKeysOverlapping(legFrom, legTo))
            {
                if (context.PartitionFileExists(key))
                {
                    segments.Add(new ChainSegment(leg.Path, key, fromTicks, toTicks));
                }
            }
        }

        return segments;
    }

    private static bool IsNumericAggregation(string aggregation) =>
        aggregation is HistoryAggregations.SampleAverage or HistoryAggregations.TimeWeightedAverage
            or HistoryAggregations.Minimum or HistoryAggregations.Maximum
            or HistoryAggregations.Sum or HistoryAggregations.StandardDeviation;

    // One grouped query per partition producing the partials for the requested aggregation. Only the
    // columns the aggregation needs are fetched. The bucket key is (ts/@b)*@b on epoch ticks, which equals
    // BucketAlignment.BucketStart for the same bucket size.
    private static IEnumerable<BucketPartial> ReadPartials(
        SqliteConnection connection, long pathId, string aggregation, bool isUlong,
        long bucketTicks, long fromTicks, long toTicks)
    {
        if (aggregation is HistoryAggregations.First or HistoryAggregations.Last)
        {
            return ReadEdgePartials(connection, pathId, aggregation, isUlong, bucketTicks, fromTicks, toTicks);
        }

        if (aggregation == HistoryAggregations.Count)
        {
            return ReadCountPartials(connection, pathId, bucketTicks, fromTicks, toTicks);
        }

        return ReadNumericPartials(connection, pathId, isUlong, bucketTicks, fromTicks, toTicks);
    }

    // Time-weighted average: per bucket, the IN-BUCKET integral only. Each recorded event's value is held
    // over [ts, min(nextTs, bucketEnd)). Explicit null events remain in the ordered set: they terminate
    // the held numeric value and contribute a gap until a later numeric event. The leading interval
    // [bucketStart, firstEventTs) and empty-bucket carry are supplied by BucketAssembler, which also needs
    // FirstTicks (the leading-interval boundary), LastTicks, and LastNumber (including null) to advance carry.
    //
    // Unlike the other aggregations, TWA must see one ascending event stream across partition files and
    // move legs. The segments are disjoint time slices, so ordering them and streaming each SQL reader in
    // timestamp order reconstructs that stream with constant sample memory and no SQLite ATTACH limit.
    //
    // The value is read as REAL so value * duration is floating point: tick products are huge (value ~tens
    // times ~10^8 ticks per 10s) and an integer sum could overflow; the weightedSum/totalDuration ratio is
    // unit-free.
    private static Dictionary<long, BucketPartial> ReadTimeWeightedAveragePartials(
        SqliteReadContext context,
        IReadOnlyList<ChainSegment> segments,
        bool isUlong,
        long bucketTicks,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, BucketPartial>();
        if (segments.Count == 0)
        {
            return result;
        }

        var numeric = isUlong
            ? "CAST(COALESCE(value_double, value_long, CAST(value_json AS REAL)) AS REAL)"
            : "CAST(COALESCE(value_double, value_long) AS REAL)";

        (long Ticks, double? Value)? previous = null;
        foreach (var segment in segments
                     .OrderBy(segment => segment.FromTicks)
                     .ThenBy(segment => segment.PartitionKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connection = context.OpenPartition(segment.PartitionKey);
            if (connection is null || SqliteHistoryReader.ResolvePathId(connection, segment.Path) is not { } pathId)
            {
                continue; // unreadable partition, or one that never saw the path
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT ts, " + numeric + " AS v FROM history " +
                "WHERE path_id = @path_id AND ts >= @from AND ts < @to ORDER BY ts;";
            command.Parameters.AddWithValue("@path_id", pathId);
            command.Parameters.AddWithValue("@from", segment.FromTicks);
            command.Parameters.AddWithValue("@to", segment.ToTicks);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = (Ticks: reader.GetInt64(0), Value: reader.IsDBNull(1) ? (double?)null : reader.GetDouble(1));
                if (previous is { } pending)
                {
                    AccumulateTimeWeightedSample(result, pending, current.Ticks, bucketTicks);
                }

                previous = current;
            }
        }

        if (previous is { } final)
        {
            var bucketStart = AlignBucketStart(final.Ticks, bucketTicks);
            AccumulateTimeWeightedSample(result, final, bucketStart + bucketTicks, bucketTicks);
        }

        return result;
    }

    private static void AccumulateTimeWeightedSample(
        Dictionary<long, BucketPartial> result,
        (long Ticks, double? Value) sample,
        long nextTicks,
        long bucketTicks)
    {
        var (ticks, value) = sample;
        var bucketStart = AlignBucketStart(ticks, bucketTicks);
        var bucketEnd = bucketStart + bucketTicks;
        var intervalEnd = nextTicks < bucketEnd ? nextTicks : bucketEnd;
        var duration = (double)Math.Max(0, intervalEnd - ticks);
        var weightedContribution = value is { } number ? number * duration : 0;
        var knownDuration = value is not null ? duration : 0;

        if (result.TryGetValue(bucketStart, out var partial))
        {
            result[bucketStart] = partial with
            {
                WeightedSum = partial.WeightedSum + weightedContribution,
                TotalDuration = partial.TotalDuration + knownDuration,
                LastTicks = ticks,
                LastNumber = value
            };
        }
        else
        {
            result[bucketStart] = new BucketPartial(
                bucketStart, 0, null, null, null, null,
                ticks, null, null, ticks, value, null, weightedContribution, knownDuration);
        }
    }

    private static long AlignBucketStart(long ticks, long bucketTicks)
    {
        var quotient = Math.DivRem(ticks, bucketTicks, out var remainder);
        return (remainder < 0 ? quotient - 1 : quotient) * bucketTicks;
    }

    // First/Last: the earliest (MIN ts) or latest (MAX ts) row per bucket, with its raw value columns.
    private static List<BucketPartial> ReadEdgePartials(
        SqliteConnection connection, long pathId, string aggregation, bool isUlong,
        long bucketTicks, long fromTicks, long toTicks)
    {
        var isFirst = aggregation == HistoryAggregations.First;
        var edge = isFirst ? "MIN(ts)" : "MAX(ts)";

        // SQLite's bare-column rule: with a single MIN or MAX, the other selected columns come from the
        // row that produced it. That gives one grouped pass over the range. The correlated-subquery form
        // this replaces re-ran an unindexable per-row lookup for every candidate row, so a Last query
        // (the default aggregation) over a large partition was quadratic, and it runs while the engine's
        // connection lock is held, which stalls the flush loop and grows the unbounded change queue.
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT " + FloorBucketExpression("ts") + " AS bucket, " + edge + " AS ts, " +
            "value_long, value_double, value_json FROM history " +
            "WHERE path_id = @path_id AND ts >= @from AND ts < @to " +
            "GROUP BY bucket;";
        command.Parameters.AddWithValue("@path_id", pathId);
        command.Parameters.AddWithValue("@b", bucketTicks);
        command.Parameters.AddWithValue("@from", fromTicks);
        command.Parameters.AddWithValue("@to", toTicks);

        var result = new List<BucketPartial>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var bucketStart = reader.GetInt64(0);
            var ts = reader.GetInt64(1);
            long? longValue = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            double? doubleValue = reader.IsDBNull(3) ? null : reader.GetDouble(3);
            string? jsonValue = reader.IsDBNull(4) ? null : reader.GetString(4);

            // The numeric projection for an edge sample mirrors ToPoint via SqliteValueRouting.Numeric:
            // double/long, plus a ulong-overflow JSON number folded in when the property is ulong.
            var number = SqliteValueRouting.Numeric(new RawRow(ts, longValue, doubleValue, jsonValue), isUlong);

            // A decimal writes both value_double and its exact text into value_json. ToPoint suppresses
            // the JSON when a numeric column is present, so the edge readers must too: otherwise the same
            // decimal property comes back numeric from a raw query and JSON-valued from a bucketed one,
            // and a consumer that dispatches on Json renders it as a discrete state.
            if (doubleValue is not null || longValue is not null)
            {
                jsonValue = null;
            }

            if (isFirst)
            {
                result.Add(new BucketPartial(
                    bucketStart, 0, null, null, null, null,
                    ts, number, jsonValue, null, null, null, 0, 0));
            }
            else
            {
                result.Add(new BucketPartial(
                    bucketStart, 0, null, null, null, null,
                    null, null, null, ts, number, jsonValue, 0, 0));
            }
        }

        return result;
    }

    // Count: total number of samples per bucket (COUNT(*)), matching InMemory's samples.Count, which
    // includes non-numeric and explicit-null samples (Count is allowed on any column type).
    private static List<BucketPartial> ReadCountPartials(
        SqliteConnection connection, long pathId, long bucketTicks, long fromTicks, long toTicks)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT " + FloorBucketExpression("ts") + " AS bucket, COUNT(*) AS cnt FROM history " +
            "WHERE path_id = @path_id AND ts >= @from AND ts < @to GROUP BY bucket ORDER BY bucket;";
        command.Parameters.AddWithValue("@path_id", pathId);
        command.Parameters.AddWithValue("@b", bucketTicks);
        command.Parameters.AddWithValue("@from", fromTicks);
        command.Parameters.AddWithValue("@to", toTicks);

        var result = new List<BucketPartial>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new BucketPartial(
                reader.GetInt64(0), reader.GetInt64(1), null, null, null, null,
                null, null, null, null, null, null, 0, 0));
        }

        return result;
    }

    // Sum/Min/Max/SampleAverage/StandardDeviation: grouped numeric reductions over COALESCE(value_double, value_long).
    // When the property is ulong, value_json numbers (ulong overflow) also count as numeric values; SQLite's
    // COALESCE includes value_json (numeric text parses to a number) so the reductions fold it in too.
    private static List<BucketPartial> ReadNumericPartials(
        SqliteConnection connection, long pathId, bool isUlong,
        long bucketTicks, long fromTicks, long toTicks)
    {
        // The numeric expression: for ulong properties also fold value_json (a JSON number stored as text).
        var numeric = isUlong
            ? "COALESCE(value_double, value_long, CAST(value_json AS REAL))"
            : "COALESCE(value_double, value_long)";

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT " + FloorBucketExpression("ts") + " AS bucket, " +
            "COUNT(" + numeric + ") AS cnt, " +
            "SUM(" + numeric + ") AS sum_num, " +
            "MIN(" + numeric + ") AS min_num, " +
            "MAX(" + numeric + ") AS max_num, " +
            "SUM(" + numeric + " * " + numeric + ") AS sumsq_num " +
            "FROM history WHERE path_id = @path_id AND ts >= @from AND ts < @to " +
            "GROUP BY bucket ORDER BY bucket;";
        command.Parameters.AddWithValue("@path_id", pathId);
        command.Parameters.AddWithValue("@b", bucketTicks);
        command.Parameters.AddWithValue("@from", fromTicks);
        command.Parameters.AddWithValue("@to", toTicks);

        var result = new List<BucketPartial>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var bucketStart = reader.GetInt64(0);
            var count = reader.GetInt64(1); // COUNT(numeric) = number of non-null numeric values
            double? sum = reader.IsDBNull(2) ? null : reader.GetDouble(2);
            double? min = reader.IsDBNull(3) ? null : reader.GetDouble(3);
            double? max = reader.IsDBNull(4) ? null : reader.GetDouble(4);
            double? sumSquares = reader.IsDBNull(5) ? null : reader.GetDouble(5);

            result.Add(new BucketPartial(
                bucketStart, count, sum, min, max, sumSquares,
                null, null, null, null, null, null, 0, 0));
        }

        return result;
    }

    private static string FloorBucketExpression(string timestampExpression) =>
        "((" + timestampExpression + " / @b) - CASE WHEN " + timestampExpression +
        " < 0 AND " + timestampExpression + " % @b <> 0 THEN 1 ELSE 0 END) * @b";
}
