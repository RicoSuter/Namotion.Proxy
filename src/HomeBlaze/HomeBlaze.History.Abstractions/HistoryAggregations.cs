using System.Collections.Immutable;

namespace HomeBlaze.History.Abstractions;

/// <summary>
/// Aggregation identifiers as PascalCase strings (not a closed enum), so per-store
/// specialization and additive growth need no abstraction churn. The MCP boundary accepts
/// case-insensitive input and normalizes; internal equality uses <see cref="StringComparer.Ordinal"/>.
/// </summary>
public static class HistoryAggregations
{
    /// <summary>Last value in the bucket, carried forward into empty buckets.</summary>
    public const string Last = "Last";

    /// <summary>First value in the bucket.</summary>
    public const string First = "First";

    /// <summary>Count-weighted sample mean.</summary>
    public const string SampleAverage = "SampleAverage";

    /// <summary>Duration-weighted average (UI default for numeric).</summary>
    public const string TimeWeightedAverage = "TimeWeightedAverage";

    /// <summary>Minimum value in the bucket.</summary>
    public const string Minimum = "Minimum";

    /// <summary>Maximum value in the bucket.</summary>
    public const string Maximum = "Maximum";

    /// <summary>Sum of values in the bucket.</summary>
    public const string Sum = "Sum";

    /// <summary>Number of samples in the bucket.</summary>
    public const string Count = "Count";

    /// <summary>Sample standard deviation.</summary>
    public const string StandardDeviation = "StandardDeviation";

    /// <summary>
    /// All aggregation identifiers in canonical display order (the order numeric columns offer them,
    /// time-weighted average first). The single source of truth for the full set: the MCP boundary and
    /// the chart gating derive their lists from this instead of re-listing the identifiers.
    /// </summary>
    public static readonly ImmutableArray<string> All =
    [
        TimeWeightedAverage, SampleAverage, Minimum, Maximum, Sum, StandardDeviation, Last, First, Count
    ];

    /// <summary>
    /// Aggregations every store supports regardless of column type. The global eligibility
    /// check skips these (see the cross-store merger).
    /// </summary>
    public static readonly IReadOnlySet<string> AlwaysAvailable =
        new HashSet<string>(StringComparer.Ordinal) { Last, Count };
}
