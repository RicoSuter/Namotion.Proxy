using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// Standalone WebSocket server that exposes subject updates to connected clients.
/// Uses Kestrel for cross-platform support without elevation.
/// On Kill, restarts both the HTTP listener and the processing layer (matching real crash behavior).
/// A Kill that arrives between attempts, such as during the restart backoff, has no attempt to cancel
/// and does nothing.
/// For embedding in an existing ASP.NET app, use MapWebSocketSubjectHandler extension instead.
/// </summary>
public sealed class WebSocketSubjectServer : SubjectConnectorBase, IFaultInjectable, IAsyncDisposable
{
    // Matches the MQTT broker's restart delay.
    private static readonly TimeSpan RestartBackoff = TimeSpan.FromSeconds(5);

    private readonly WebSocketSubjectHandler _handler;
    private readonly WebSocketServerConfiguration _configuration;
    private readonly ILogger _logger;

    private WebApplication? _app;
    private int _disposed;

    /// <inheritdoc />
    public override IInterceptorSubject RootSubject { get; }

    /// <inheritdoc cref="SubjectConnectorBase.Diagnostics" />
    public override WebSocketServerDiagnostics Diagnostics { get; }

    internal int ConnectionCount => _handler.ConnectionCount;

    internal long CurrentSequence => _handler.CurrentSequence;

    public WebSocketSubjectServer(
        IInterceptorSubject subject,
        WebSocketServerConfiguration configuration,
        ILogger<WebSocketSubjectServer> logger)
        : base(new ConnectorMetrics())
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        configuration.Validate();

        RootSubject = subject;
        Diagnostics = new WebSocketServerDiagnostics(this, Metrics);
        _handler = new WebSocketSubjectHandler(subject, configuration, logger);
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    async Task IFaultInjectable.InjectFaultAsync(FaultType faultType, CancellationToken cancellationToken)
    {
        switch (faultType)
        {
            case FaultType.Kill:
                await ForceKillCurrentAttemptAsync().ConfigureAwait(false);
                break;

            case FaultType.Disconnect:
                await _handler.CloseAllConnectionsAsync().ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(faultType), faultType, null);
        }
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Set by the catch below only: a force-kill and a processing layer that ended without
            // throwing both restart at once, because neither repeats fast enough to need a throttle.
            var restartBackoff = TimeSpan.Zero;

            await RunAttemptAsync(stoppingToken, async attempt =>
            {
                var linkedToken = attempt.Token;

                try
                {
                    try
                    {
                        // Build a new WebApplication each iteration because IHost doesn't support
                        // Start/Stop cycles. On Kill, the entire Kestrel instance is torn down and
                        // rebuilt, matching real crash behavior (like MQTT restarts its broker).
                        _app = BuildWebApplication(linkedToken, out var listenUrl);

                        _logger.LogInformation("WebSocket server starting on {Url}{Path}", listenUrl, _configuration.Path);
                        await _app.StartAsync(stoppingToken).ConfigureAwait(false);
                        Metrics.MarkOperational();

                        using var changeQueueProcessor = _handler.CreateChangeQueueProcessor(
                            _logger, Metrics.OutboundChanges.CreateDropReporter());

                        // Declared after the processor so it is released first, which is what lets the
                        // next restart register its own: a second Register while one is still live throws.
                        using var outboundRegistration = Metrics.OutboundChanges.Register(
                            () => changeQueueProcessor.QueueDepth, capacity: null);

                        var processorTask = changeQueueProcessor.ProcessAsync(linkedToken);
                        var heartbeatTask = _handler.RunHeartbeatLoopAsync(linkedToken);

                        // Cancelling the attempt makes a cancellation the normal exit path here, so the
                        // filter below cannot be narrowed to the force-kill and the exception is kept
                        // and judged afterwards instead.
                        OperationCanceledException? completionCancellation = null;
                        try
                        {
                            // When either task completes, cancel the other to prevent blocking forever.
                            await Task.WhenAny(processorTask, heartbeatTask).ConfigureAwait(false);
                            await attempt.CancelAsync().ConfigureAwait(false);
                            await Task.WhenAll(processorTask, heartbeatTask).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException exception) when (!stoppingToken.IsCancellationRequested)
                        {
                            // Kill or one task completed: linkedToken canceled
                            completionCancellation = exception;
                        }

                        // Both tasks completed, either normally (tasks catch OCE internally and
                        // return) or via caught OCE above. Check why we stopped:
                        if (stoppingToken.IsCancellationRequested)
                        {
                            return;
                        }

                        // linkedToken was canceled (Kill) or completed unexpectedly, so restart.
                        if (attempt.WasForceKilled)
                        {
                            // Not reported as an error: an injected fault the server recovers from by
                            // restarting.
                            _logger.LogWarning("WebSocket server force-killed. Restarting...");
                        }
                        else
                        {
                            // Nothing outside this loop reports its failures. The captured cancellation
                            // is normally null, because neither task surfaces the one raised above to
                            // stop its sibling.
                            var error = new InvalidOperationException(
                                "WebSocket server processing completed unexpectedly.", completionCancellation);

                            Metrics.ReportError(error);
                            _logger.LogWarning(error, "WebSocket server processing completed unexpectedly. Restarting...");
                        }
                    }
                    finally
                    {
                        // Inside the try the catches below guard: disposing the WebApplication disposes its
                        // whole service provider, and a singleton that throws on disposal would otherwise
                        // leave RunAsync and end the connector.
                        Metrics.MarkNotOperational();

                        await _handler.CloseAllConnectionsAsync().ConfigureAwait(false);

                        // Claimed atomically, because DisposeAsync also races for this app after a stop
                        // that timed out, and both winning would dispose it twice.
                        var app = Interlocked.Exchange(ref _app, null);
                        if (app is not null)
                        {
                            try
                            {
                                // Use a short timeout to avoid the default 30-second ASP.NET graceful
                                // shutdown. Connections are already closed above, so Kestrel should stop
                                // quickly. The timeout is just a safety net.
                                using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                                try
                                {
                                    await app.StopAsync(shutdownCts.Token).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException)
                                {
                                    // Shutdown timed out, so DisposeAsync will force-release the port.
                                }
                            }
                            finally
                            {
                                // In a finally, because a stop that fails must not skip the disposal: the
                                // app still holds the listening port and every later bind would fail.
                                await app.DisposeAsync().ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    // Nothing outside this loop reports its failures, but a stop tears the listener down
                    // with an arbitrary exception rather than a cancellation, so only the stopping token
                    // tells a shutdown apart from a genuine fault.
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        Metrics.ReportError(ex);
                    }

                    _logger.LogError(ex, "WebSocket server processing failed. Restarting...");
                    restartBackoff = RestartBackoff;
                }
            }).ConfigureAwait(false);

            // After the teardown above, so the port is free rather than held for the whole delay.
            if (restartBackoff > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(restartBackoff, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // The delay sits outside every catch above, so a stop landing here would otherwise
                    // leave RunAsync as a cancellation.
                    break;
                }
            }
        }
    }

    private WebApplication BuildWebApplication(CancellationToken requestHandlingToken, out string listenUrl)
    {
        var builder = WebApplication.CreateSlimBuilder();

        listenUrl = _configuration.BindAddress is not null
            ? $"http://{_configuration.BindAddress}:{_configuration.Port}"
            : $"http://localhost:{_configuration.Port}";

        builder.WebHost.UseUrls(listenUrl);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        });

        app.Map(_configuration.Path, async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                await _handler.HandleClientAsync(webSocket, requestHandlingToken).ConfigureAwait(false);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        });

        return app;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // Stop ExecuteAsync if called directly (not via hosting)
        try
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await StopAsync(stopCts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort stop
        }

        await _handler.CloseAllConnectionsAsync().ConfigureAwait(false);

        // Claimed atomically, because a stop that timed out can leave the run loop's own teardown
        // still racing for this app, and both winning would dispose it twice.
        var app = Interlocked.Exchange(ref _app, null);
        if (app is not null)
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }

        Dispose();
    }
}
