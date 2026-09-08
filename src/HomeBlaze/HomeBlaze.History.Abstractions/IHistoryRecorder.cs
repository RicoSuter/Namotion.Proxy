namespace HomeBlaze.History.Abstractions;

/// <summary>
/// The write half of a history engine: the contract the recording glue drives. Kept separate from
/// <see cref="IHistoryStore"/> because a store can be queryable without being writable (a read-only
/// archive), and like <see cref="IHistoryStore"/> it speaks only canonical path strings and typed
/// values, so the engine stays free of object-graph coupling.
/// </summary>
public interface IHistoryRecorder
{
    /// <summary>
    /// Records one sample. Returns false when the engine refused it (for example a full pending
    /// queue), so the caller can tell an accepted change from a dropped one.
    /// </summary>
    bool TryRecord(string propertyPath, DateTimeOffset timestamp, object? value, Type propertyType);

    /// <summary>
    /// Records that the property at <paramref name="fromPath"/> is henceforth found at
    /// <paramref name="toPath"/>, so a later query against the new path also reads the samples
    /// recorded under the old one.
    /// </summary>
    void RecordMove(DateTimeOffset timestamp, string fromPath, string toPath);
}
