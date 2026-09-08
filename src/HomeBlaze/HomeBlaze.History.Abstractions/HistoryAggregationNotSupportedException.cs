namespace HomeBlaze.History.Abstractions;

/// <summary>
/// Thrown when a requested aggregation cannot be served, for example a numeric aggregation on a
/// JSON-stored property, or an aggregation no covering store supports over the queried range.
/// Carries the set of aggregations that are available.
/// </summary>
public class HistoryAggregationNotSupportedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryAggregationNotSupportedException"/> class.
    /// </summary>
    /// <param name="aggregation">The requested aggregation that is not supported.</param>
    /// <param name="available">The aggregations that are available instead.</param>
    public HistoryAggregationNotSupportedException(string aggregation, IReadOnlySet<string> available)
        : base($"Aggregation '{aggregation}' is not supported. Available: {string.Join(", ", available)}.")
    {
        Aggregation = aggregation;
        Available = available;
    }

    /// <summary>
    /// Gets the requested aggregation that is not supported.
    /// </summary>
    public string Aggregation { get; }

    /// <summary>
    /// Gets the aggregations that are available.
    /// </summary>
    public IReadOnlySet<string> Available { get; }
}
