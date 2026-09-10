using System.Collections.Immutable;
using HomeBlaze.History.Abstractions;
using Microsoft.Data.Sqlite;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// One leg's slice over a single existing partition file: the leg's path, the partition key, and the
/// intersection of the query window with the leg's validity, expressed in epoch ticks.
/// </summary>
internal readonly record struct ChainSegment(string Path, string PartitionKey, long FromTicks, long ToTicks);

/// <summary>The stored column kind and ulong flag for a path, read from a partition's <c>paths</c> table.</summary>
internal readonly record struct ColumnMeta(ValueColumn Column, bool IsUlong);

/// <summary>
/// The connection access the read helpers need, supplied by the engine: the open-partition and
/// metadata-connection delegates plus the partition layout (directory, interval, metadata key). The engine builds
/// this while holding its connection lock and passes it in; the delegates re-enter the engine lock when
/// they open or reuse a cached connection. This context never locks itself and never caches connections.
/// </summary>
internal readonly struct SqliteReadContext(
    string databaseDirectory,
    string metadataKey,
    Func<string, SqliteConnection?> openPartition,
    Func<SqliteConnection> openMetadata)
{
    public string MetadataKey => metadataKey;

    /// <summary>
    /// The partition's connection, or null when this build cannot read that file. Callers skip a null
    /// instead of failing, so an unreadable partition costs its own data rather than the whole query.
    /// </summary>
    public SqliteConnection? OpenPartition(string key) => openPartition(key);

    public SqliteConnection OpenMetadata() => openMetadata();

    public string PartitionFilePath(string key) => Path.Combine(databaseDirectory, key + ".db");

    public bool PartitionFileExists(string key) => File.Exists(PartitionFilePath(key));

    // Partition keys whose file already exists on disk (used by metadata and coverage queries).
    public IEnumerable<string> EnumeratePartitionFileKeys()
    {
        if (!Directory.Exists(databaseDirectory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(databaseDirectory, "*.db"))
        {
            var key = Path.GetFileNameWithoutExtension(file);
            if (SqlitePartition.IsPartitionKey(key))
            {
                yield return key; // skip non-partition files such as the metadata database
            }
        }
    }

    // Existing partition files whose range overlaps [from, to), in ascending time order.
    //
    // Driven by what is on disk rather than by generating the configured interval's keys across the
    // window: after the interval is reconfigured the generated keys no longer name the existing files,
    // so every earlier partition silently read as empty.
    public IEnumerable<string> PartitionKeysOverlapping(DateTimeOffset from, DateTimeOffset to) =>
        EnumeratePartitionFileKeys()
            .Select(key => (Key: key, Range: SqlitePartition.InferredRange(key)))
            .Where(entry => entry.Range.Start < to && entry.Range.End > from)
            .OrderBy(entry => entry.Range.Start)
            .Select(entry => entry.Key);

    // Existing partition files at or before asOf, newest first (look-back across files stops at the first hit).
    public IEnumerable<string> PartitionKeysAtOrBefore(DateTimeOffset asOf) =>
        EnumeratePartitionFileKeys()
            .Select(key => (Key: key, Range: SqlitePartition.InferredRange(key)))
            .Where(entry => entry.Range.Start <= asOf)
            .OrderByDescending(entry => entry.Range.Start)
            .Select(entry => entry.Key);
}

/// <summary>
/// Pure read SQL for the SQLite history engine: raw queries, at-or-before look-back, move-chain
/// resolution, and column-metadata lookup. Bucketed aggregation lives in <see cref="SqliteBucketReader"/>,
/// which reuses this class's <see cref="ResolveChain"/> and <see cref="ResolveColumnMeta"/>. Every method
/// takes a <see cref="SqliteReadContext"/> (the engine's open-connection delegates plus partition layout)
/// and uses <see cref="SqliteValueRouting"/> for value mapping. These helpers never lock and never touch
/// the engine's connection cache; the engine calls them while holding its connection lock.
/// </summary>
internal static class SqliteHistoryReader
{
    public static HistorySeries QueryRaw(
        SqliteReadContext context, HistoryQuery query, CancellationToken cancellationToken)
    {
        var limit = query.MaxPoints + 1; // +1 overflow probe to detect truncation

        // Route through the move chain: for each leg, read its own path over the intersection of the
        // query range with the leg's [ValidFrom, ValidTo), then merge. With no moves this is a single
        // unbounded leg, identical to the pre-move single-path read.
        var rows = new List<(RawRow Row, bool IsUlong)>();
        foreach (var leg in ResolveChain(context, query.PropertyPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var legFrom = query.From > leg.ValidFrom ? query.From : leg.ValidFrom;
            var legTo = query.To < leg.ValidTo ? query.To : leg.ValidTo;
            if (legFrom >= legTo)
            {
                continue;
            }

            var fromTicks = EpochTicks.ToEpochTicks(legFrom);
            var toTicks = EpochTicks.ToEpochTicks(legTo);
            var isUlong = ResolveIsUlong(context, leg.Path);

            foreach (var key in context.PartitionKeysOverlapping(legFrom, legTo))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!context.PartitionFileExists(key))
                {
                    continue;
                }

                var connection = context.OpenPartition(key);
                if (connection is null || ResolvePathId(connection, leg.Path) is not { } pathId)
                {
                    continue; // unreadable partition, or one that never saw the path
                }

                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT ts, value_long, value_double, value_json FROM history " +
                    "WHERE path_id = @path_id AND ts >= @from AND ts < @to ORDER BY ts DESC LIMIT @limit;";
                command.Parameters.AddWithValue("@path_id", pathId);
                command.Parameters.AddWithValue("@from", fromTicks);
                command.Parameters.AddWithValue("@to", toTicks);
                command.Parameters.AddWithValue("@limit", limit);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rows.Add((ReadRawRow(reader), isUlong));
                }
            }
        }

        // Order descending across the union, take newest (MaxPoints + 1), detect truncation, return ascending.
        rows.Sort((left, right) => right.Row.Ticks.CompareTo(left.Row.Ticks));
        var truncated = rows.Count > query.MaxPoints;
        var kept = truncated ? rows.GetRange(0, query.MaxPoints) : rows;
        kept.Sort((left, right) => left.Row.Ticks.CompareTo(right.Row.Ticks));

        var points = kept.Select(entry => SqliteValueRouting.ToPoint(entry.Row, entry.IsUlong)).ToImmutableArray();
        return new HistorySeries(
            query.PropertyPath,
            points,
            truncated,
            ImmutableArray<HistoryCoverage>.Empty);
    }

    public static HistoryPoint? GetSampleAtOrBefore(SqliteReadContext context, string propertyPath, DateTimeOffset asOf)
    {
        // Route through the move chain: walk legs from newest to oldest and return the first held value.
        // Only legs whose validity starts at or before asOf can hold the value; the older leg of a move is
        // capped at ValidTo - 1 tick (its half-open ceiling), mirroring InMemoryHistoryStore.
        foreach (var leg in ResolveChain(context, propertyPath))
        {
            if (leg.ValidFrom > asOf)
            {
                continue;
            }

            var ceiling = asOf < leg.ValidTo ? asOf : leg.ValidTo - new TimeSpan(1);
            var found = GetLegSampleAtOrBefore(context, leg.Path, ceiling);
            if (found is { } row && EpochTicks.FromEpochTicks(row.Ticks) >= leg.ValidFrom)
            {
                return SqliteValueRouting.ToPoint(row, ResolveIsUlong(context, leg.Path));
            }
        }

        return null;
    }

    // The stored column kind and ulong flag for a (possibly moved) property: the first path along its chain
    // that the paths table knows. The SQLite equivalent of InMemoryHistoryStore.ResolveBuffer (which returns the
    // first buffer in the chain), used for the numeric-on-json-non-ulong guard and ulong-overflow folding.
    public static ColumnMeta? ResolveColumnMeta(SqliteReadContext context, List<HistoryChainLeg> chain)
    {
        foreach (var leg in chain)
        {
            if (ResolveColumnMetaForPath(context, leg.Path) is { } meta)
            {
                return meta;
            }
        }

        return null;
    }

    // The queried (current) path's move chain, resolved from the moves in the metadata database by the
    // same walk the in-memory engine uses, so both stores answer a moved path identically.
    public static List<HistoryChainLeg> ResolveChain(SqliteReadContext context, string currentPath) =>
        HistoryMoveChain.Resolve(ReadMoves(context), currentPath);

    // Reads every move from the metadata database into memory (the move set is small). Empty when it has no
    // rows or does not exist yet.
    private static List<HistoryMove> ReadMoves(SqliteReadContext context)
    {
        var result = new List<HistoryMove>();
        if (!File.Exists(context.PartitionFilePath(context.MetadataKey)))
        {
            return result; // no moves recorded yet -> single unbounded leg in ResolveChain
        }

        var connection = context.OpenMetadata();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ts, from_path, to_path FROM moves;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new HistoryMove(
                EpochTicks.FromEpochTicks(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2)));
        }

        return result;
    }

    // Newest row at or before asOf for a single path across its partitions.
    private static RawRow? GetLegSampleAtOrBefore(SqliteReadContext context, string path, DateTimeOffset asOf)
    {
        var asOfTicks = EpochTicks.ToEpochTicks(asOf);

        // Search the partition holding asOf, then earlier partitions, newest match wins.
        foreach (var key in context.PartitionKeysAtOrBefore(asOf))
        {
            if (!context.PartitionFileExists(key))
            {
                continue;
            }

            var connection = context.OpenPartition(key);
            if (connection is null || ResolvePathId(connection, path) is not { } pathId)
            {
                continue; // unreadable partition, or one that never saw the path
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT ts, value_long, value_double, value_json FROM history " +
                "WHERE path_id = @path_id AND ts <= @asOf ORDER BY ts DESC LIMIT 1;";
            command.Parameters.AddWithValue("@path_id", pathId);
            command.Parameters.AddWithValue("@asOf", asOfTicks);

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return ReadRawRow(reader);
            }
        }

        return null;
    }

    // The id a partition interns a path to, or null when that partition never held the path.
    //
    // Ids are local to the file that assigned them, which is what keeps a partition self-describing and
    // independently deletable, so this is resolved per partition rather than once per query.
    public static long? ResolvePathId(SqliteConnection connection, string propertyPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM paths WHERE path = @path;";
        command.Parameters.AddWithValue("@path", propertyPath);
        return command.ExecuteScalar() is long id ? id : null;
    }

    // The stored column kind and ulong flag for a single path, read from the paths table (written at
    // flush time). Returns null when the path has never been written.
    //
    // Newest partition first, and stops at the first hit. Directory enumeration order is not specified,
    // so scanning in that order let an arbitrary partition answer: after a property's type changed, the
    // reader could pick up the superseded column kind and route a numeric read at the wrong column.
    // Newest-first also means the common case opens one partition instead of every one of them.
    private static ColumnMeta? ResolveColumnMetaForPath(SqliteReadContext context, string propertyPath)
    {
        foreach (var key in context.EnumeratePartitionFileKeys().OrderByDescending(key => key, StringComparer.Ordinal))
        {
            var connection = context.OpenPartition(key);
            if (connection is null)
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value_column, is_ulong FROM paths WHERE path = @path;";
            command.Parameters.AddWithValue("@path", propertyPath);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return new ColumnMeta((ValueColumn)reader.GetInt64(0), reader.GetInt64(1) != 0);
            }
        }

        return null;
    }

    private static bool ResolveIsUlong(SqliteReadContext context, string propertyPath) =>
        ResolveColumnMetaForPath(context, propertyPath)?.IsUlong ?? false;

    private static RawRow ReadRawRow(SqliteDataReader reader)
    {
        var ticks = reader.GetInt64(0);
        long? longValue = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        double? doubleValue = reader.IsDBNull(2) ? null : reader.GetDouble(2);
        string? jsonValue = reader.IsDBNull(3) ? null : reader.GetString(3);
        return new RawRow(ticks, longValue, doubleValue, jsonValue);
    }
}
