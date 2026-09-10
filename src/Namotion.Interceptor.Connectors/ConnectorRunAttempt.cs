namespace Namotion.Interceptor.Connectors;

/// <summary>
/// One iteration of a connector's restart loop: the cancellation that iteration's work runs under,
/// linked to the connector's stopping token, together with the flag that says an injected
/// <see cref="FaultType.Kill"/> cancelled it.
/// </summary>
/// <remarks>
/// The flag belongs to the iteration rather than to the connector because a kill can arrive after the
/// iteration it was meant for has torn down: a connector-level flag would stay set across that
/// boundary and let the next iteration swallow a genuine fault as an injected one.
/// <para>
/// A loop creates one attempt per iteration, runs its work under <see cref="Token"/>, and disposes it
/// in a <c>finally</c>. <c>SubjectConnectorBase.RunAttemptAsync</c> owns that lifecycle: it publishes
/// the current attempt so that <see cref="IFaultInjectable.InjectFaultAsync"/> can reach it through
/// <c>ForceKillCurrentAttemptAsync</c>, and clears it before the disposal so a later kill finds no
/// attempt rather than a disposed one.
/// </para>
/// </remarks>
public sealed class ConnectorRunAttempt : IDisposable
{
    private readonly CancellationTokenSource _cancellation;

    private volatile bool _wasForceKilled;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectorRunAttempt"/> class whose cancellation is
    /// linked to the connector's stopping token.
    /// </summary>
    /// <param name="stoppingToken">The connector's stopping token.</param>
    public ConnectorRunAttempt(CancellationToken stoppingToken)
    {
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    }

    /// <summary>
    /// Gets the token this attempt's work runs under. Read it once at the start of the iteration: the
    /// attempt is disposed when the iteration ends and reading it afterwards throws.
    /// </summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>
    /// Gets a value indicating whether <see cref="ForceKillAsync"/> cancelled this attempt. Use it to
    /// tell an injected cancellation from a genuine failure, after the connector's stopping token has
    /// been ruled out.
    /// </summary>
    public bool WasForceKilled => _wasForceKilled;

    /// <summary>
    /// Marks this attempt as force-killed and cancels it, in that order, so the loop cannot reach its
    /// kill check before the mark is visible. An attempt that is already disposed is left unmarked: the
    /// loop is then between attempts and this kill reached nothing.
    /// </summary>
    /// <remarks>
    /// The mark is set and cleared without a compare-exchange, so two kills that overlap can leave it
    /// clear: the second unmarks after finding the attempt disposed, even though the first one cancelled
    /// it. A loop must therefore read <see cref="WasForceKilled"/> before it disposes the attempt, which
    /// an exception filter does by construction, since filters run before the <c>finally</c> that
    /// disposes it.
    /// </remarks>
    public async Task ForceKillAsync()
    {
        _wasForceKilled = true;
        try
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            _wasForceKilled = false;
        }
    }

    /// <summary>
    /// Cancels this attempt without marking it as force-killed, for a loop that ends its own iteration.
    /// </summary>
    /// <remarks>
    /// Call it only while the attempt is live: unlike <see cref="ForceKillAsync"/> it does not tolerate a
    /// disposed attempt, and that failure arrives as a faulted task which a caller that does not await it
    /// never observes.
    /// </remarks>
    public Task CancelAsync() => _cancellation.CancelAsync();

    /// <inheritdoc />
    public void Dispose() => _cancellation.Dispose();
}
