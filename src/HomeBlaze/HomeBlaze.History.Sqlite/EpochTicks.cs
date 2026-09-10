namespace HomeBlaze.History.Sqlite;

/// <summary>
/// Conversions between a <see cref="DateTimeOffset"/> and the on-disk <c>ts</c> unit
/// (Unix-epoch ticks). Epoch anchoring keeps the SQL bucket expression identical to
/// <see cref="HomeBlaze.History.Abstractions.BucketAlignment.BucketStart"/>.
/// </summary>
internal static class EpochTicks
{
    public static long ToEpochTicks(DateTimeOffset timestamp) => (timestamp - DateTimeOffset.UnixEpoch).Ticks;

    public static DateTimeOffset FromEpochTicks(long ticks) => DateTimeOffset.UnixEpoch.AddTicks(ticks);
}
