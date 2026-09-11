namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>Tracks the continuous blocked observations that justify reporting a gate holder.</summary>
internal struct BlockedGateHolderWindow
{
    private Thread? _holder;
    private long _transactionRevision;
    private long _blockedSince;

    public bool Observe(Thread? holder, long transactionRevision, bool isBlocked, long timestamp, int threshold)
    {
        if (holder is null || !isBlocked)
        {
            _holder = null;
            return false;
        }

        if (!ReferenceEquals(holder, _holder) || transactionRevision != _transactionRevision)
        {
            _holder = holder;
            _transactionRevision = transactionRevision;
            _blockedSince = timestamp;
            return false;
        }

        return timestamp - _blockedSince >= threshold;
    }
}
