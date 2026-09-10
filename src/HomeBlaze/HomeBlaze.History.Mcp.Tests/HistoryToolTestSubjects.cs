using System.Collections.Immutable;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.History.Abstractions;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.History.Mcp.Tests;

/// <summary>Root subject for the tool tests: one recordable [State] property and one that is not.</summary>
[InterceptorSubject]
public partial class HistoryToolTestSubject
{
    [State]
    public partial string Name { get; set; }

    [State]
    public partial HistoryToolTestSubject? Child { get; set; }
}

/// <summary>
/// History store subject for the tool tests: covers the whole timeline and answers every path with an
/// empty series, except <see cref="UnsupportedPath"/>, which throws the way a real store does when a
/// numeric aggregation is asked of a value_json column.
/// </summary>
[InterceptorSubject]
public partial class TestHistoryStore : IHistoryStore
{
    public string? UnsupportedPath { get; set; }

    public int Priority => 10;

    public ImmutableArray<HistoryCoverage> CoverageRanges =>
        [new HistoryCoverage(DateTimeOffset.MinValue, DateTimeOffset.MaxValue)];

    public IReadOnlySet<string> SupportedAggregations =>
        new HashSet<string>(HistoryAggregations.All, StringComparer.Ordinal);

    public Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        if (string.Equals(query.PropertyPath, UnsupportedPath, StringComparison.Ordinal))
        {
            throw new HistoryAggregationNotSupportedException(
                query.Aggregation,
                new HashSet<string>(StringComparer.Ordinal) { HistoryAggregations.Last });
        }

        return Task.FromResult(new HistorySeries(query.PropertyPath, [], false, CoverageRanges));
    }

    public ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(
        string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken) =>
        new((HistoryPoint?)null);
}
