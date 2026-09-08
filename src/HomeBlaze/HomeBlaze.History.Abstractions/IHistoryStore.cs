using System.Collections.Immutable;
using System.Text.Json;

namespace HomeBlaze.History.Abstractions;

/// <summary>
/// Plain query interface for a time-series history store. Deliberately not an
/// <c>IInterceptorSubject</c>, so the recording and query engine stays free of graph
/// coupling and a future generic engine can implement it directly. Stores are consumed as
/// an <see cref="IEnumerable{T}"/> of <see cref="IHistoryStore"/>; HomeBlaze supplies that
/// set from the registry's known subjects (its store subjects implement this interface).
/// </summary>
public interface IHistoryStore
{
    /// <summary>
    /// Gets the store priority. Higher values are preferred for overlapping ranges.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Gets the ordered, non-overlapping, store-wide half-open time ranges [From, To) for which
    /// this store guarantees complete recording across every eligible property. The immutable
    /// snapshot can change between reads as the store records, restarts, loses continuity, or
    /// evicts data.
    /// </summary>
    ImmutableArray<HistoryCoverage> CoverageRanges { get; }

    /// <summary>
    /// Gets the aggregation identifiers (see <see cref="HistoryAggregations"/>) this store supports.
    /// </summary>
    IReadOnlySet<string> SupportedAggregations { get; }

    /// <summary>
    /// Queries the store for raw samples (when <see cref="HistoryQuery.Bucket"/> is null)
    /// or bucketed aggregates. Returns at most <see cref="HistoryQuery.MaxPoints"/> points,
    /// ascending by timestamp.
    /// </summary>
    Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the most recent sample at or before <paramref name="asOf"/> for the property path
    /// (following move chains), or null if none. Used by TimeWeightedAverage integration
    /// and Last LOCF gap-fill.
    /// </summary>
    ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(
        string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken);
}

/// <summary>
/// The store-wide, half-open time window [From, To) for which a store guarantees complete history.
/// </summary>
public readonly record struct HistoryCoverage(DateTimeOffset From, DateTimeOffset To)
{
    /// <summary>
    /// Gets a value indicating whether this coverage fully contains <paramref name="other"/>.
    /// </summary>
    public bool Contains(HistoryCoverage other) => other.From >= From && other.To <= To;

    /// <summary>
    /// Returns the intersection with <paramref name="other"/>, or null when they do not overlap.
    /// </summary>
    public HistoryCoverage? Intersect(HistoryCoverage other)
    {
        var from = From > other.From ? From : other.From;
        var to = To < other.To ? To : other.To;
        return from < to ? new HistoryCoverage(from, to) : null;
    }

    /// <summary>
    /// Returns the ordered, non-overlapping form of <paramref name="ranges"/>: empty ranges are
    /// dropped and touching or overlapping ranges are merged. This is the shape every
    /// <see cref="IHistoryStore.CoverageRanges"/> snapshot must have.
    /// </summary>
    public static ImmutableArray<HistoryCoverage> Normalize(IEnumerable<HistoryCoverage> ranges)
    {
        var builder = ImmutableArray.CreateBuilder<HistoryCoverage>();
        foreach (var range in ranges.Where(range => range.From < range.To).OrderBy(range => range.From))
        {
            if (builder.Count > 0 && range.From <= builder[^1].To)
            {
                var previous = builder[^1];
                builder[^1] = previous with { To = range.To > previous.To ? range.To : previous.To };
            }
            else
            {
                builder.Add(range);
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Clips <paramref name="ranges"/> to <paramref name="window"/> and normalizes the result.
    /// </summary>
    public static ImmutableArray<HistoryCoverage> Clip(
        IEnumerable<HistoryCoverage> ranges, HistoryCoverage window) =>
        Normalize(ranges
            .Select(range => range.Intersect(window))
            .Where(range => range is not null)
            .Select(range => range!.Value));
}

/// <summary>
/// A single history query. A null <see cref="Bucket"/> requests raw samples.
/// </summary>
public record HistoryQuery(
    string PropertyPath,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan? Bucket = null,
    string Aggregation = HistoryAggregations.Last,
    int MaxPoints = 10_000,
    HistoryPoint? CarrySeed = null)
{
    /// <summary>
    /// Validates the query invariants required by every history store and the cross-store merger.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PropertyPath))
        {
            throw new ArgumentException("A property path is required.", nameof(PropertyPath));
        }

        if (From >= To)
        {
            throw new ArgumentException("The query start must be earlier than its end.", nameof(From));
        }

        if (Bucket is { } bucket && bucket <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Bucket), "The bucket size must be positive.");
        }

        if (string.IsNullOrWhiteSpace(Aggregation))
        {
            throw new ArgumentException("An aggregation is required.", nameof(Aggregation));
        }

        if (MaxPoints <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPoints), "The point limit must be positive.");
        }
    }
}

/// <summary>
/// A single point in a history series. <see cref="Number"/> carries numeric values,
/// and <see cref="Json"/> carries string and enum values. Both null encodes an explicit
/// null sample or a bucket with no known value.
/// </summary>
public record HistoryPoint(DateTimeOffset Timestamp, double? Number, JsonElement? Json);

/// <summary>
/// The result of a history query: the points for a property path, ascending by timestamp,
/// whether the result was truncated to fit the point cap, and the effective store coverage
/// within the requested range.
/// </summary>
public record HistorySeries(
    string PropertyPath,
    ImmutableArray<HistoryPoint> Points,
    bool Truncated,
    ImmutableArray<HistoryCoverage> CoverageRanges);
