using System.Globalization;
using System.Text.Json;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// A typed row routed from a recorded value, ready to bind into the <c>history</c> insert. Exactly
/// one value column is populated for most non-null values (or all null for a recorded null).
/// Decimal values use value_double for queries and also archive exact text in value_json.
/// </summary>
internal readonly record struct Row(long? Long, double? Double, string? Json);

/// <summary>
/// The raw columns read back from a <c>history</c> row: the epoch-tick timestamp and the three value
/// columns. Mapped to a <see cref="HistoryPoint"/> by <see cref="SqliteValueRouting.ToPoint"/>.
/// </summary>
internal readonly record struct RawRow(long Ticks, long? Long, double? Double, string? Json);

/// <summary>
/// The result of routing a value into a <see cref="Row"/>: the row plus whether a string value was
/// replaced by the oversize placeholder. The engine owns the oversize counter and increments it when
/// <see cref="Oversized"/> is true (the routing helper is pure and never mutates engine state).
/// </summary>
internal readonly record struct RoutedRow(Row Row, bool Oversized);

/// <summary>
/// The placeholder stored in place of an oversize string value: a small JSON object recording that the
/// original was dropped for being too large, with its measured size.
/// </summary>
internal readonly record struct OversizePlaceholder(
    [property: System.Text.Json.Serialization.JsonPropertyName("$oversize")] bool Oversize,
    [property: System.Text.Json.Serialization.JsonPropertyName("size")] int Size);

/// <summary>
/// Pure value routing and point mapping for the SQLite history engine. Mirrors
/// <c>InMemoryHistoryStore</c> value handling so query results are identical, but serializes the JSON
/// column to its raw text representation for storage. These helpers hold no state, touch no connection,
/// and never lock; the engine calls them while holding its connection lock (or, for recording, off it).
/// </summary>
internal static class SqliteValueRouting
{
    // A JSON string rather than a bare NaN, which is not valid JSON. Same token System.Text.Json writes
    // under JsonNumberHandling.AllowNamedFloatingPointLiterals, so a later reader can parse it as one.
    private const string NotANumberJson = "\"NaN\"";

    // Routes a value into a row exactly like InMemoryHistoryStore.CreateSample, but serializes the JSON
    // column to its raw text representation for storage. Returns whether the value was oversized so the
    // engine can increment its own oversize counter (this helper never mutates engine state).
    public static RoutedRow CreateRow(object? value, ValueColumn column, bool isUlong, int maxJsonSize)
    {
        if (value is null)
        {
            return new RoutedRow(new Row(null, null, null), false);
        }

        switch (column)
        {
            case ValueColumn.Double:
                var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number))
                {
                    // SQLite binds NaN into a REAL column as SQL NULL, and a row with all three value
                    // columns null is exactly how an explicitly recorded null is stored, so the two would
                    // be indistinguishable on disk with no way to tell them apart afterwards. The named
                    // literal in value_json keeps them separate. Infinities need none of this: they
                    // round-trip through REAL unchanged.
                    return new RoutedRow(new Row(null, null, NotANumberJson), false);
                }

                // A decimal routes here so the chart/MCP/aggregations read value_double, but its exact text is
                // also archived in value_json so the original precision is recoverable later. ToPoint reads
                // value_double first, so the archive never affects query results. A real double has no exact
                // decimal text to preserve, so it leaves value_json empty.
                var archive = value is decimal decimalValue ? JsonSerializer.Serialize(decimalValue) : null;
                return new RoutedRow(new Row(null, number, archive), false);

            case ValueColumn.Long:
                if (isUlong && value is ulong unsigned && unsigned > long.MaxValue)
                {
                    return new RoutedRow(new Row(null, null, JsonSerializer.Serialize(unsigned)), false);
                }

                return new RoutedRow(new Row(Convert.ToInt64(value, CultureInfo.InvariantCulture), null, null), false);

            case ValueColumn.Json:
            default:
                var (json, oversized) = SerializeJson(value, maxJsonSize);
                return new RoutedRow(new Row(null, null, json), oversized);
        }
    }

    // Serializes a value to its JSON text. Returns the placeholder text (and oversized = true) when a
    // string value exceeds the cap; otherwise the verbatim JSON (and oversized = false).
    public static (string Json, bool Oversized) SerializeJson(object value, int maxJsonSize)
    {
        // enum -> name; string -> native JSON; oversize string -> placeholder.
        JsonElement element = value is Enum
            ? JsonSerializer.SerializeToElement(value.ToString())
            : JsonSerializer.SerializeToElement(value);

        if (element.ValueKind == JsonValueKind.String)
        {
            var size = element.GetRawText().Length; // UTF-16 length is a safe upper-bound proxy for the cap
            if (size > maxJsonSize)
            {
                return (JsonSerializer.Serialize(new OversizePlaceholder(true, size)), true);
            }
        }

        return (element.GetRawText(), false);
    }

    // Maps a stored row to the wire HistoryPoint, mirroring InMemoryHistoryStore.ToPoint:
    // value_double/value_long -> Number; value_json -> Json (parsed from text); a ulong-overflow
    // JSON number -> Number as well (COALESCE) so numeric aggregation works; all-null -> empty point.
    public static HistoryPoint ToPoint(RawRow row, bool isUlong)
    {
        var timestamp = EpochTicks.FromEpochTicks(row.Ticks);

        if (row.Double is { } doubleValue)
        {
            return new HistoryPoint(timestamp, doubleValue, null);
        }

        if (row.Long is { } longValue)
        {
            return new HistoryPoint(timestamp, longValue, null);
        }

        if (row.Json is { } jsonText)
        {
            using var document = JsonDocument.Parse(jsonText);
            var element = document.RootElement.Clone();
            double? number = isUlong && element.ValueKind == JsonValueKind.Number ? element.GetDouble() : null;
            return new HistoryPoint(timestamp, number, element);
        }

        return new HistoryPoint(timestamp, null, null); // explicit recorded null
    }

    // The numeric projection of a raw row for aggregation, mirroring ToPoint's numeric folding:
    // double/long, plus a ulong-overflow JSON number folded in when the property is ulong.
    public static double? Numeric(RawRow row, bool isUlong)
    {
        double? number = row.Double ?? (double?)row.Long;
        if (number is null && isUlong && row.Json is { } jsonText)
        {
            using var document = JsonDocument.Parse(jsonText);
            if (document.RootElement.ValueKind == JsonValueKind.Number)
            {
                number = document.RootElement.GetDouble();
            }
        }

        return number;
    }
}
