using System.Text.Json;

namespace HomeBlaze.History.InMemory;

/// <summary>
/// One typed in-memory sample: the timestamp plus exactly one populated value column
/// (or all-null for an explicitly recorded null). Mirrors the value_long / value_double /
/// value_json column triple used by the persistent stores so query results stay identical.
///
/// <c>valueBytes</c> is the approximate heap cost of <c>json</c> (its raw text plus the backing
/// JsonDocument), zero for the numeric columns which live inside the struct. It is carried on the
/// sample so the store can report retained memory without re-serializing every retained value.
/// </summary>
internal readonly struct Sample(
    DateTimeOffset timestamp, long? longValue, double? doubleValue, JsonElement? json, int valueBytes = 0)
{
    public DateTimeOffset Timestamp { get; } = timestamp;
    public long? Long { get; } = longValue;
    public double? Double { get; } = doubleValue;
    public JsonElement? Json { get; } = json;
    public int ValueBytes { get; } = valueBytes;

    /// <summary>True when every value column is null (an explicitly recorded null value).</summary>
    public bool IsNull => Long is null && Double is null && Json is null;
}
