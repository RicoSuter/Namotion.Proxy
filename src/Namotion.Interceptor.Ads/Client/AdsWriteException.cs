namespace Namotion.Interceptor.Ads.Client;

/// <summary>
/// Exception for ADS write batch failures with error classification.
/// </summary>
public class AdsWriteException : Exception
{
    /// <summary>
    /// Creates a new ADS write exception.
    /// </summary>
    /// <param name="transientCount">The number of transient failures.</param>
    /// <param name="permanentCount">The number of permanent failures.</param>
    /// <param name="totalCount">The total count of changes attempted.</param>
    public AdsWriteException(int transientCount, int permanentCount, int totalCount)
        : base($"ADS write failed: {transientCount} transient, {permanentCount} permanent out of {totalCount} total.")
    {
        TransientCount = transientCount;
        PermanentCount = permanentCount;
        TotalCount = totalCount;
    }

    /// <summary>
    /// Gets the number of transient failures.
    /// </summary>
    public int TransientCount { get; }

    /// <summary>
    /// Gets the number of permanent failures.
    /// </summary>
    public int PermanentCount { get; }

    /// <summary>
    /// Gets the total count of changes attempted.
    /// </summary>
    public int TotalCount { get; }
}
