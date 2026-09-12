using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Ads.Client;

/// <summary>
/// What a polling pass records. Only the individual read fallback is counted: a sum read is one
/// round trip for the whole batch, so its duration says nothing about a single symbol.
/// </summary>
internal sealed class AdsPollingMetrics : IResettableMetrics
{
    private long _passes;
    private long _failedReads;
    private long _lastPassDurationMilliseconds;
    private long _lastPassSymbolCount;

    /// <summary>Gets the number of individual-read passes completed.</summary>
    public long Passes => Interlocked.Read(ref _passes);

    /// <summary>Gets the reads that threw, across all passes.</summary>
    public long FailedReads => Interlocked.Read(ref _failedReads);

    /// <summary>Gets how long the last pass took, in milliseconds.</summary>
    public long LastPassDurationMilliseconds => Interlocked.Read(ref _lastPassDurationMilliseconds);

    /// <summary>Gets how many symbols the last pass read.</summary>
    public long LastPassSymbolCount => Interlocked.Read(ref _lastPassSymbolCount);

    /// <summary>Records a completed pass.</summary>
    public void RecordPass(int symbolCount, double durationMilliseconds, int failedReads)
    {
        Interlocked.Increment(ref _passes);
        Interlocked.Add(ref _failedReads, failedReads);
        Interlocked.Exchange(ref _lastPassDurationMilliseconds, (long)durationMilliseconds);
        Interlocked.Exchange(ref _lastPassSymbolCount, symbolCount);
    }

    /// <inheritdoc />
    public void Reset()
    {
        Interlocked.Exchange(ref _passes, 0);
        Interlocked.Exchange(ref _failedReads, 0);
        Interlocked.Exchange(ref _lastPassDurationMilliseconds, 0);
        Interlocked.Exchange(ref _lastPassSymbolCount, 0);
    }
}
