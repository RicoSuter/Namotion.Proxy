namespace HomeBlaze.History.Abstractions;

/// <summary>
/// Epoch-anchored bucket alignment. All backends must produce buckets at identical timestamps
/// for the same (bucket size, sample timestamps), matching Postgres time_bucket, so the merger
/// never interleaves duplicates.
/// </summary>
public static class BucketAlignment
{
    /// <summary>
    /// Returns the start of the bucket containing <paramref name="ts"/> for the given
    /// <paramref name="bucket"/> size, anchored at the Unix epoch.
    /// </summary>
    public static DateTimeOffset BucketStart(DateTimeOffset ts, TimeSpan bucket)
    {
        if (bucket <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(bucket), "The bucket size must be positive.");
        }

        var ticksFromEpoch = (ts - DateTimeOffset.UnixEpoch).Ticks;
        var quotient = Math.DivRem(ticksFromEpoch, bucket.Ticks, out var remainder);
        if (remainder < 0)
        {
            quotient--;
        }

        return DateTimeOffset.UnixEpoch.AddTicks(quotient * bucket.Ticks);
    }

    /// <summary>
    /// Returns the first aligned bucket that can contribute to the newest
    /// <paramref name="maxBuckets"/> buckets in <paramref name="from"/>..<paramref name="to"/>.
    /// This bounds query planning and aggregation work to the output budget.
    /// </summary>
    public static DateTimeOffset FirstBucketStart(
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan bucket,
        int maxBuckets)
    {
        if (from >= to)
        {
            throw new ArgumentException("The start timestamp must be before the end timestamp.", nameof(from));
        }

        if (bucket <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(bucket), "The bucket size must be positive.");
        }

        if (maxBuckets <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBuckets), "The maximum bucket count must be positive.");
        }

        var first = BucketStart(from, bucket);
        var spanTicks = (to - first).Ticks;
        var totalBucketCount = 1L + (spanTicks - 1L) / bucket.Ticks;
        var skippedBucketCount = Math.Max(0L, totalBucketCount - maxBuckets);
        return first.AddTicks(skippedBucketCount * bucket.Ticks);
    }
}
