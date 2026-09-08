using System.Collections.Immutable;
using System.Runtime.InteropServices;
using HomeBlaze.History.Abstractions;
using Microsoft.Data.Sqlite;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// Persists SQLite coverage rows and maintains their normalized immutable snapshot.
/// The owning history store serializes all mutating calls with its connection lock;
/// <see cref="Snapshot"/> is readable without it.
/// </summary>
internal sealed class SqliteCoverageStore(Func<SqliteConnection> openMetadata)
{
    private readonly List<CoverageRangeRow> _rows = [];
    private long? _activeRangeId;

    // An ImmutableArray<T> field cannot be volatile, so the normalized ranges are published through
    // their underlying array reference. Readers therefore never take the owner's connection lock,
    // which is held for the whole duration of a flush.
    private volatile HistoryCoverage[] _snapshot = [];

    public ImmutableArray<HistoryCoverage> Snapshot =>
        ImmutableCollectionsMarshal.AsImmutableArray(_snapshot);

    public void Reload()
    {
        var activeRangeId = _activeRangeId;
        _rows.Clear();

        using var command = openMetadata().CreateCommand();
        command.CommandText = "SELECT id, from_ts, to_ts FROM coverage_ranges ORDER BY from_ts, to_ts;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            _rows.Add(new CoverageRangeRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        _activeRangeId = activeRangeId is { } activeId && _rows.Any(row => row.Id == activeId)
            ? activeId
            : null;
        RebuildSnapshot();
    }

    public void Update(long fromTicks, long toTicks, bool startsNewRange)
    {
        var connection = openMetadata();
        if (startsNewRange || _activeRangeId is null)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO coverage_ranges (from_ts, to_ts) VALUES (@from, @to); SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("@from", fromTicks);
            insert.Parameters.AddWithValue("@to", toTicks);
            var id = (long)insert.ExecuteScalar()!;
            _activeRangeId = id;
            _rows.Add(new CoverageRangeRow(id, fromTicks, toTicks));
        }
        else
        {
            var index = _rows.FindIndex(row => row.Id == _activeRangeId.Value);
            if (index < 0)
            {
                throw new InvalidOperationException("The active SQLite history coverage range is missing.");
            }

            var existing = _rows[index];
            var updated = existing with
            {
                // Retention may already have advanced the durable lower bound.
                FromTicks = existing.FromTicks,
                // Retraction keeps coverage conservative when an older pending sample or drop appears.
                ToTicks = toTicks
            };

            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE coverage_ranges SET from_ts = @from, to_ts = @to WHERE id = @id;";
            update.Parameters.AddWithValue("@from", updated.FromTicks);
            update.Parameters.AddWithValue("@to", updated.ToTicks);
            update.Parameters.AddWithValue("@id", updated.Id);
            update.ExecuteNonQuery();
            _rows[index] = updated;
        }

        RebuildSnapshot();
    }

    public void Trim(long retainedFrom)
    {
        var connection = openMetadata();
        try
        {
            TrimRows(connection, retainedFrom);
        }
        finally
        {
            // Whatever was trimmed before a failure is already gone from the database and from _rows,
            // so the published snapshot has to follow or it keeps claiming ranges that no longer exist.
            RebuildSnapshot();
        }
    }

    private void TrimRows(SqliteConnection connection, long retainedFrom)
    {
        for (var index = _rows.Count - 1; index >= 0; index--)
        {
            var row = _rows[index];
            var from = Math.Max(row.FromTicks, retainedFrom);
            using var command = connection.CreateCommand();
            if (from >= row.ToTicks)
            {
                command.CommandText = "DELETE FROM coverage_ranges WHERE id = @id;";
                command.Parameters.AddWithValue("@id", row.Id);
                command.ExecuteNonQuery();
                _rows.RemoveAt(index);
                if (_activeRangeId == row.Id)
                {
                    _activeRangeId = null;
                }
            }
            else if (from != row.FromTicks)
            {
                command.CommandText = "UPDATE coverage_ranges SET from_ts = @from WHERE id = @id;";
                command.Parameters.AddWithValue("@from", from);
                command.Parameters.AddWithValue("@id", row.Id);
                command.ExecuteNonQuery();
                _rows[index] = row with { FromTicks = from };
            }
        }
    }

    private void RebuildSnapshot() =>
        _snapshot =
        [
            .. HistoryCoverage.Normalize(_rows.Select(row => new HistoryCoverage(
                EpochTicks.FromEpochTicks(row.FromTicks),
                EpochTicks.FromEpochTicks(row.ToTicks))))
        ];

    private readonly record struct CoverageRangeRow(long Id, long FromTicks, long ToTicks);
}
