using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Abstract base for every connector, client or server, owning the diagnostics lifecycle so that a
/// connector cannot forget to report that it stopped serving.
/// </summary>
/// <remarks>
/// <see cref="ExecuteAsync"/> is sealed and derived classes override <see cref="RunAsync"/> instead.
/// </remarks>
public abstract class SubjectConnectorBase : BackgroundService, ISubjectConnector
{
    private int _executionActive;
    private volatile ConnectorRunAttempt? _currentAttempt;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubjectConnectorBase"/> class.
    /// </summary>
    /// <param name="metrics">
    /// The metrics this connector writes to. Passed in rather than created here, so a derived class can
    /// supply a richer type and still hand the same instance to its own diagnostics view.
    /// </param>
    protected SubjectConnectorBase(ConnectorMetrics metrics)
    {
        Metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    /// <summary>
    /// Gets the write side of this connector's diagnostics. Never exposed through
    /// <see cref="ISubjectConnector"/>.
    /// </summary>
    protected ConnectorMetrics Metrics { get; }

    /// <inheritdoc cref="ISubjectConnector.RootSubject" />
    public abstract IInterceptorSubject RootSubject { get; }

    /// <summary>
    /// Gets what this connector reports about its transport.
    /// </summary>
    public abstract ConnectorDiagnostics Diagnostics { get; }

    /// <summary>
    /// Runs the connector until cancellation. Replaces <see cref="ExecuteAsync"/>, which this class
    /// seals so that the diagnostics lifecycle is applied uniformly.
    /// </summary>
    protected abstract Task RunAsync(CancellationToken stoppingToken);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The previous execution is still running.
    /// </exception>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // BackgroundService permits StartAsync after a timed-out StopAsync even while its previous
        // ExecuteTask is still live. Starting then would overwrite the task and cancellation source,
        // and the old execution could stop the new diagnostics epoch when it eventually exits.
        if (Interlocked.CompareExchange(ref _executionActive, 1, 0) != 0)
        {
            throw new InvalidOperationException("Cannot start a connector while its previous execution is still running.");
        }

        try
        {
            return base.StartAsync(cancellationToken);
        }
        catch
        {
            Volatile.Write(ref _executionActive, 0);
            throw;
        }
    }

    /// <inheritdoc />
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Inside the try, because it calls Reset on every registered resettable and that
            // registration is public: a third-party Reset that throws must still be recorded.
            Metrics.MarkStarted();

            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // An expected shutdown is not a fault, and recording it would overwrite the genuine error
            // that made the connector fail, which stays sticky until the next MarkStarted.
            throw;
        }
        catch (Exception exception)
        {
            // A cancellation the stopping token did not cause is a genuine fault and is recorded here.
            Metrics.ReportError(exception);
            throw;
        }
        finally
        {
            try
            {
                Metrics.MarkStopped();
            }
            finally
            {
                Volatile.Write(ref _executionActive, 0);
            }
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        // BackgroundService.Dispose cancels the token but does not await ExecuteAsync, so the finally
        // above runs at an unspecified later time.
        Metrics.MarkStopped();
        base.Dispose();
    }

    /// <summary>
    /// Runs one iteration of a restart loop under a fresh <see cref="ConnectorRunAttempt"/>,
    /// publishing it for <see cref="ForceKillCurrentAttemptAsync"/> while the body runs and
    /// releasing it afterwards.
    /// </summary>
    /// <remarks>
    /// Exception filters that read <see cref="ConnectorRunAttempt.WasForceKilled"/> must stay inside
    /// <paramref name="body"/>: filters run before the cleanup here disposes the attempt, and the
    /// flag is unreliable once the attempt is disposed.
    /// </remarks>
    protected async Task RunAttemptAsync(CancellationToken stoppingToken, Func<ConnectorRunAttempt, Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var attempt = new ConnectorRunAttempt(stoppingToken);
        _currentAttempt = attempt;
        try
        {
            await body(attempt).ConfigureAwait(false);
        }
        finally
        {
            // Cleared before the attempt is disposed, so a kill arriving from here on finds no
            // attempt rather than a disposed one.
            _currentAttempt = null;
            attempt.Dispose();
        }
    }

    /// <summary>
    /// Force-kills the attempt currently running under <see cref="RunAttemptAsync"/>. No current
    /// attempt means the loop is between attempts, where the teardown the kill stands for is already
    /// under way, and the call does nothing.
    /// </summary>
    protected async Task ForceKillCurrentAttemptAsync()
    {
        var attempt = _currentAttempt;
        if (attempt is not null)
        {
            await attempt.ForceKillAsync().ConfigureAwait(false);
        }
    }
}
