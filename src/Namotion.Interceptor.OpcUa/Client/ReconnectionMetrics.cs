using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.OpcUa.Client;

internal sealed class ReconnectionMetrics : IResettableMetrics
{
    private long _totalAttempts;
    private long _successful;
    private long _failed;
    private long _abandoned;
    private long _lastConnectedAtTicks;

    public long TotalAttempts => Interlocked.Read(ref _totalAttempts);

    public long Successful => Interlocked.Read(ref _successful);

    public long Failed => Interlocked.Read(ref _failed);

    public long Abandoned => Interlocked.Read(ref _abandoned);

    public DateTimeOffset? LastConnectedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastConnectedAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void RecordAttemptStart()
    {
        Interlocked.Increment(ref _totalAttempts);
    }

    public void RecordInitialConnection()
    {
        Interlocked.Exchange(ref _lastConnectedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void RecordSuccess()
    {
        Interlocked.Increment(ref _successful);
        Interlocked.Exchange(ref _lastConnectedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void RecordFailure()
    {
        Interlocked.Increment(ref _failed);
    }

    public void RecordAbandoned()
    {
        Interlocked.Increment(ref _abandoned);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="LastConnectedAt"/> is deliberately preserved: it records a past event rather than
    /// an amount accumulated during the run.
    /// </remarks>
    public void Reset()
    {
        Interlocked.Exchange(ref _totalAttempts, 0);
        Interlocked.Exchange(ref _successful, 0);
        Interlocked.Exchange(ref _failed, 0);
        Interlocked.Exchange(ref _abandoned, 0);
    }
}
