using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Resilience;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.Configuration;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace Namotion.Interceptor.Ads.Client;

/// <summary>
/// Manages the ADS connection lifecycle including client creation, circuit breaker,
/// event subscriptions, reconnection tracking, and ADS state monitoring.
/// </summary>
internal sealed class AdsConnectionManager : IAsyncDisposable
{
    private readonly AdsClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly CircuitBreaker _circuitBreaker;

    // ADS connection objects - uses AdsClient directly (not AdsSession) for reliable dispose in 7.0.x
    private AdsClient? _client;
    private volatile ISymbolLoader? _symbolLoader;

    // Embedded-router lease, acquired once and reused across reconnects, released on dispose.
    private EmbeddedRouterPool.Lease? _embeddedLease;

    // First-occurrence logging state (Warning first, then Debug until cleared)
    private readonly ConcurrentDictionary<string, bool> _loggedErrors = new();

    // Reconnection tracking
    private long _totalReconnectionAttempts;
    private long _successfulReconnections;
    private long _failedReconnections;
    private long _lastConnectedAtTicks;

    // ADS state tracking (-1 = not set, otherwise cast to AdsState)
    private int _currentAdsState = -1;

    private int _disposed; // 0 = false, 1 = true

    /// <summary>
    /// Fired when a previously-lost connection is restored.
    /// </summary>
    internal event Action? ConnectionRestored;

    /// <summary>
    /// Fired when the connection is lost.
    /// </summary>
    internal event Action? ConnectionLost;

    /// <summary>
    /// Fired when the PLC enters the Run state.
    /// </summary>
    internal event Action? AdsStateEnteredRun;

    /// <summary>
    /// Fired when the PLC symbol version changes.
    /// </summary>
    internal event Action? SymbolVersionChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdsConnectionManager"/> class.
    /// </summary>
    public AdsConnectionManager(AdsClientConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _configuration = configuration;
        _logger = logger;

        _circuitBreaker = new CircuitBreaker(
            configuration.CircuitBreakerFailureThreshold,
            configuration.CircuitBreakerCooldown);
    }

    /// <summary>
    /// Gets the current ADS connection, or null if not connected.
    /// </summary>
    internal IAdsConnection? Connection => Volatile.Read(ref _client);

    /// <summary>
    /// Gets the current symbol loader, or null if not loaded.
    /// </summary>
    internal ISymbolLoader? SymbolLoader => _symbolLoader;

    /// <summary>
    /// Sets the symbol loader directly (e.g. for injecting a mock in tests).
    /// </summary>
    internal void SetSymbolLoader(ISymbolLoader symbolLoader)
    {
        _symbolLoader = symbolLoader;
    }

    /// <summary>
    /// Gets the current PLC ADS state, or null if not yet known.
    /// </summary>
    internal AdsState? CurrentAdsState
    {
        get
        {
            var value = Volatile.Read(ref _currentAdsState);
            return value == -1 ? null : (AdsState)value;
        }
    }

    /// <summary>
    /// Gets whether the ADS client is currently connected.
    /// </summary>
    internal bool IsConnected =>
        Volatile.Read(ref _client) is IConnection { IsConnected: true };

    internal long TotalReconnectionAttempts =>
        Interlocked.Read(ref _totalReconnectionAttempts);

    internal long SuccessfulReconnections =>
        Interlocked.Read(ref _successfulReconnections);

    internal long FailedReconnections =>
        Interlocked.Read(ref _failedReconnections);

    internal DateTimeOffset? LastConnectedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastConnectedAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    internal bool IsCircuitBreakerOpen => _circuitBreaker.IsOpen;

    internal long CircuitBreakerTripCount => _circuitBreaker.TripCount;

    /// <summary>
    /// Connects to the PLC with retry and circuit breaker logic.
    /// If already connected, returns immediately. If a previous client exists
    /// but is disconnected, it is disposed before creating a new one.
    /// </summary>
    internal async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        // If already connected, reuse the existing connection
        if (IsConnected)
        {
            return;
        }

        // Dispose stale client before creating a new one
        DisconnectAndDisposeClient();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_circuitBreaker.ShouldAttempt())
                {
                    Interlocked.Increment(ref _totalReconnectionAttempts);

                    var amsNetId = _configuration.GetTargetAmsNetId();

                    var routerConfiguration = _configuration.RouterConfiguration;
                    if (_configuration.UseEmbeddedRouter)
                    {
                        _embeddedLease ??= AdsEmbeddedRouter.Shared.Acquire(
                            _configuration.LocalAmsNetId ?? AdsEmbeddedRouter.DefaultLocalNetId(),
                            // Named per target: the router is process-wide and rejects a duplicate
                            // name as readily as a duplicate net id, so a fixed name would make a
                            // second source pointing at a different PLC unroutable.
                            new Route($"plc-{amsNetId}", amsNetId, _configuration.Host!));

                        routerConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["AmsRouter:Name"] = "EmbeddedRouter",
                            ["AmsRouter:NetId"] = _embeddedLease.LocalNetId.ToString(),
                            ["AmsRouter:LoopbackPort"] = _embeddedLease.LoopbackPort.ToString(),
                        }).Build();
                    }

                    var client = routerConfiguration is not null
                        ? new AdsClient(routerConfiguration, null)
                        : new AdsClient();

                    // Set before connecting, or the configured timeout does not apply to the connect
                    // itself, which is the call most likely to hang against an unreachable PLC.
                    client.Timeout = (int)_configuration.Timeout.TotalMilliseconds;

                    try
                    {
                        await client.ConnectAsync(amsNetId, _configuration.AmsPort, cancellationToken);
                    }
                    catch
                    {
                        client.Dispose();
                        throw;
                    }

                    // Wire events before publishing _client so that no caller can read
                    // Connection and find a client whose events are not yet subscribed.
                    client.ConnectionStateChanged += OnConnectionStateChanged;
                    client.AdsStateChanged += OnAdsStateChanged;
                    client.AdsSymbolVersionChanged += OnSymbolVersionChanged;

                    // The initial connect does not raise ConnectionStateChanged, so re-arm here too.
                    ResetFirstOccurrenceLog();
                    Interlocked.Exchange(ref _lastConnectedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
                    Volatile.Write(ref _client, client);
                    _circuitBreaker.RecordSuccess();
                    _logger.LogInformation(
                        "Connected to TwinCAT PLC at {AmsNetId}:{Port}.",
                        amsNetId, _configuration.AmsPort);
                    return;
                }

                _logger.LogDebug(
                    "Circuit breaker open, skipping connection attempt. Cooldown remaining: {Remaining}",
                    _circuitBreaker.GetCooldownRemaining());
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _failedReconnections);
                LogFirstOccurrence("Connection", exception,
                    "Failed to connect to TwinCAT PLC. Retrying...");
                _circuitBreaker.RecordFailure();
            }

            await Task.Delay(_configuration.HealthCheckInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates a new symbol loader from the current connection.
    /// </summary>
    internal async Task RecreateSymbolLoaderAsync(CancellationToken cancellationToken)
    {
        var client = Volatile.Read(ref _client);
        if (client is null)
        {
            return;
        }

        var symbolLoader = SymbolLoaderFactory.Create(
            client,
            new SymbolLoaderSettings(SymbolsLoadMode.DynamicTree));

        // Pull the symbol table and the data types now. Both are otherwise fetched synchronously by
        // the first access to Symbols, on whichever thread happens to touch it, and DynamicTree
        // builds struct members, array elements and enum fields from the type information on the
        // fly. Beckhoff documents the async pair as the one to prefer for the first call.
        if (_configuration.PrefetchSymbols)
        {
            // ISymbolLoader extends ISymbolServer, so no capability check is needed. The results
            // carry the error rather than throwing, and a half-loaded symbol table resolves nothing
            // later, so a failure here has to abort rather than publish the loader.
            var dataTypes = await symbolLoader.GetDataTypesAsync(cancellationToken).ConfigureAwait(false);
            if (!dataTypes.Succeeded)
            {
                throw new AdsErrorException(
                    "Failed to prefetch ADS data types.", dataTypes.ErrorCode);
            }

            var symbols = await symbolLoader.GetSymbolsAsync(cancellationToken).ConfigureAwait(false);
            if (!symbols.Succeeded)
            {
                throw new AdsErrorException(
                    "Failed to prefetch ADS symbols.", symbols.ErrorCode);
            }
        }

        _symbolLoader = symbolLoader;
    }

    internal void LogFirstOccurrence(string category, Exception? exception, string message, params object[] arguments)
    {
        var level = _loggedErrors.TryAdd(category, true) ? LogLevel.Warning : LogLevel.Debug;
        _logger.Log(level, exception, message, arguments);
    }

    internal void ClearFirstOccurrenceLog(string category)
    {
        _loggedErrors.TryRemove(category, out _);
    }

    /// <summary>
    /// Re-arms every first-occurrence category, so the next failure of each is a warning again.
    /// </summary>
    /// <remarks>
    /// Called when a connection is established. Without it a category warns once per process: a
    /// benign message during startup, such as the sum-read capability probe falling back, consumes
    /// the one warning for its category, and a later total failure of the same area is logged only
    /// at Debug. That turns most of this connector's loud failures into silent ones.
    /// </remarks>
    internal void ResetFirstOccurrenceLog()
    {
        _loggedErrors.Clear();
    }

    internal void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs eventArgs)
    {
        if (eventArgs.NewState == ConnectionState.Connected &&
            eventArgs.OldState != ConnectionState.Connected)
        {
            ResetFirstOccurrenceLog();
            Interlocked.Increment(ref _successfulReconnections);
            ConnectionRestored?.Invoke();
        }
        else if (eventArgs.NewState != ConnectionState.Connected)
        {
            ClearFirstOccurrenceLog("Connection");
            ConnectionLost?.Invoke();
        }
    }

    internal void OnAdsStateChanged(object? sender, AdsStateChangedEventArgs eventArgs)
    {
        var newState = eventArgs.State.AdsState;
        var previousState = (AdsState)Interlocked.Exchange(ref _currentAdsState, (int)newState);

        if (newState == AdsState.Run && previousState != AdsState.Run)
        {
            AdsStateEnteredRun?.Invoke();
        }
        else if (newState != AdsState.Run)
        {
            LogFirstOccurrence("PlcState", null,
                "PLC left Run state: {State}. Writes paused.", newState);
        }
    }

    internal void OnSymbolVersionChanged(object? sender, AdsSymbolVersionChangedEventArgs eventArgs)
    {
        SymbolVersionChanged?.Invoke();
    }

    /// <summary>
    /// Unsubscribes event handlers, disconnects, and disposes the current ADS client.
    /// Safe to call when no client exists (no-op).
    /// </summary>
    private void DisconnectAndDisposeClient()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
        {
            client.ConnectionStateChanged -= OnConnectionStateChanged;
            client.AdsStateChanged -= OnAdsStateChanged;
            client.AdsSymbolVersionChanged -= OnSymbolVersionChanged;

            try
            {
                client.Disconnect();
                client.Dispose();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Error disposing ADS client.");
            }
        }

        _symbolLoader = null;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            DisconnectAndDisposeClient();
            _embeddedLease?.Dispose();
            _embeddedLease = null;
        }

        return ValueTask.CompletedTask;
    }
}
