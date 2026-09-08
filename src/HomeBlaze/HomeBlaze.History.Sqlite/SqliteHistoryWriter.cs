using HomeBlaze.History.Abstractions;
using Microsoft.Data.Sqlite;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// A value sample queued for flush: its path, timestamp, the routed <see cref="Row"/>, and the column
/// kind plus ulong flag persisted into the partition's <c>paths</c> table.
/// </summary>
internal readonly record struct PendingSample(
    string Path, DateTimeOffset Timestamp, Row Row, ValueColumn Column, bool IsUlong);

/// <summary>
/// Pure write SQL for the SQLite history engine: the batched <c>INSERT OR REPLACE</c> into a partition
/// file (with the path interning it depends on) and the moves insert. These helpers operate on
/// connections the engine opens and passes in; they never lock, never open or cache connections, and
/// hold no state. The engine calls them while holding its connection lock, and owns the pending buffers
/// plus the re-queue-on-failure orchestration.
/// </summary>
internal static class SqliteHistoryWriter
{
    // Writes one partition's batch in a single transaction: each sample's row into history, keyed by
    // the integer id its path interns to in this same file.
    public static void WritePartition(SqliteConnection connection, IReadOnlyList<PendingSample> samples)
    {
        using var transaction = connection.BeginTransaction();

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT OR REPLACE INTO history (path_id, ts, value_long, value_double, value_json) " +
            "VALUES (@path_id, @ts, @long, @double, @json);";
        var pathIdParameter = insert.Parameters.Add("@path_id", SqliteType.Integer);
        var tsParameter = insert.Parameters.Add("@ts", SqliteType.Integer);
        var longParameter = insert.Parameters.Add("@long", SqliteType.Integer);
        var doubleParameter = insert.Parameters.Add("@double", SqliteType.Real);
        var jsonParameter = insert.Parameters.Add("@json", SqliteType.Text);

        // A batch carries far more samples than distinct paths, so resolve each path once per flush, and
        // resolve it from that path's NEWEST sample rather than from whichever arrived last.
        //
        // The pending list is in arrival order, so a device reporting late puts an older sample after a
        // newer one. Taking the kind from the last arrival then stored the superseded one, and a ulong
        // whose value had overflowed into value_json read back as JSON text instead of a number. Ordering
        // by timestamp makes the stored kind independent of the order the samples turned up in.
        var newestByPath = new Dictionary<string, PendingSample>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            if (!newestByPath.TryGetValue(sample.Path, out var newest) || sample.Timestamp > newest.Timestamp)
            {
                newestByPath[sample.Path] = sample;
            }
        }

        var pathIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (path, newest) in newestByPath)
        {
            pathIds[path] = InternPath(connection, transaction, newest);
        }

        foreach (var sample in samples)
        {
            pathIdParameter.Value = pathIds[sample.Path];
            tsParameter.Value = EpochTicks.ToEpochTicks(sample.Timestamp);
            longParameter.Value = (object?)sample.Row.Long ?? DBNull.Value;
            doubleParameter.Value = (object?)sample.Row.Double ?? DBNull.Value;
            jsonParameter.Value = (object?)sample.Row.Json ?? DBNull.Value;
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // The id this partition interns a path to, registering it on first sight and refreshing its stored
    // column kind when the property's type has changed.
    //
    // Read before write: after the first flush the path is already known with an unchanged kind, and
    // an unconditional upsert would dirty a page for every path on every flush purely to rewrite what
    // was already there.
    private static long InternPath(
        SqliteConnection connection, SqliteTransaction transaction, PendingSample sample)
    {
        long? existingId = null;
        var storedKindIsCurrent = false;

        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT id, value_column, is_ulong FROM paths WHERE path = @path;";
            lookup.Parameters.AddWithValue("@path", sample.Path);

            using var reader = lookup.ExecuteReader();
            if (reader.Read())
            {
                existingId = reader.GetInt64(0);
                storedKindIsCurrent =
                    (ValueColumn)reader.GetInt64(1) == sample.Column &&
                    (reader.GetInt64(2) != 0) == sample.IsUlong;
            }
        }

        if (existingId is { } id)
        {
            if (!storedKindIsCurrent)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    "UPDATE paths SET value_column = @column, is_ulong = @is_ulong WHERE id = @id;";
                update.Parameters.AddWithValue("@column", (int)sample.Column);
                update.Parameters.AddWithValue("@is_ulong", sample.IsUlong ? 1 : 0);
                update.Parameters.AddWithValue("@id", id);
                update.ExecuteNonQuery();
            }

            return id;
        }

        using var register = connection.CreateCommand();
        register.Transaction = transaction;
        register.CommandText =
            "INSERT INTO paths (path, value_column, is_ulong) VALUES (@path, @column, @is_ulong);";
        register.Parameters.AddWithValue("@path", sample.Path);
        register.Parameters.AddWithValue("@column", (int)sample.Column);
        register.Parameters.AddWithValue("@is_ulong", sample.IsUlong ? 1 : 0);
        register.ExecuteNonQuery();

        using var assigned = connection.CreateCommand();
        assigned.Transaction = transaction;
        assigned.CommandText = "SELECT last_insert_rowid();";
        return (long)assigned.ExecuteScalar()!;
    }

    // Persists queued moves into the metadata database in a single transaction. OR IGNORE because a
    // flush that fails re-queues its moves, so the same record can be offered twice.
    public static void WriteMoves(SqliteConnection movesConnection, IReadOnlyList<HistoryMove> moves)
    {
        using var transaction = movesConnection.BeginTransaction();
        using var insert = movesConnection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT OR IGNORE INTO moves (ts, from_path, to_path) VALUES (@ts, @from, @to);";
        var tsParameter = insert.Parameters.Add("@ts", SqliteType.Integer);
        var fromParameter = insert.Parameters.Add("@from", SqliteType.Text);
        var toParameter = insert.Parameters.Add("@to", SqliteType.Text);

        foreach (var move in moves)
        {
            tsParameter.Value = EpochTicks.ToEpochTicks(move.Timestamp);
            fromParameter.Value = move.FromPath;
            toParameter.Value = move.ToPath;
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
