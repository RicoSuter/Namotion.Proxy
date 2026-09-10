using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// Handles WebSocket client connections and broadcasts subject updates.
/// Used by both standalone server and embedded endpoint modes.
/// </summary>
public sealed class WebSocketSubjectHandler
{
    private const int SupportedProtocolVersion = WebSocketProtocol.Version;

    private int _connectionCount;
    private long _sequence;

    private readonly IInterceptorSubject _subject;
    private readonly WebSocketServerConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly ISubjectUpdateProcessor[] _processors;
    private readonly JsonWebSocketSerializer _serializer = JsonWebSocketSerializer.Instance;
    private readonly ConcurrentDictionary<string, WebSocketClientConnection> _connections = new();
    private readonly Lock _applyUpdateLock = new();

    public IInterceptorSubjectContext Context { get; }
    
    public TimeSpan BufferTime => _configuration.BufferTime;

    /// <summary>
    /// Gets the number of currently connected WebSocket clients.
    /// </summary>
    /// <remarks>
    /// For embedded mode. With the standalone server, read
    /// <see cref="WebSocketServerDiagnostics.ConnectionCount"/> instead.
    /// </remarks>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>
    /// Gets the sequence number most recently assigned to an outgoing message.
    /// </summary>
    /// <remarks>
    /// For embedded mode. With the standalone server, read
    /// <see cref="WebSocketServerDiagnostics.CurrentSequence"/> instead.
    /// </remarks>
    public long CurrentSequence => Volatile.Read(ref _sequence);

    public WebSocketSubjectHandler(
        IInterceptorSubject subject,
        WebSocketServerConfiguration configuration,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _subject = subject;
        _configuration = configuration;
        _logger = logger;
        Context = subject.Context;
        _processors = configuration.Processors;
    }
    
    public async Task HandleClientAsync(System.Net.WebSockets.WebSocket webSocket, CancellationToken stoppingToken)
    {
        // Atomically check and increment connection count
        var newCount = Interlocked.Increment(ref _connectionCount);
        if (newCount > _configuration.MaxConnections)
        {
            Interlocked.Decrement(ref _connectionCount);
            _logger.LogWarning("Maximum connections ({MaxConnections}) reached, rejecting client", _configuration.MaxConnections);
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await webSocket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation, "Server at capacity", closeCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to close WebSocket gracefully, aborting");
                webSocket.Abort();
            }
            return;
        }

        var connection = new WebSocketClientConnection(
            webSocket,
            _logger,
            _configuration.MaxMessageSize,
            _configuration.HelloTimeout,
            _configuration.SendLockTimeout);

        var registered = false;
        try
        {
            // Receive Hello
            var hello = await connection.ReceiveHelloAsync(stoppingToken).ConfigureAwait(false);
            if (hello is null)
            {
                _logger.LogWarning("Client {ConnectionId}: No Hello received, closing", connection.ConnectionId);
                await connection.CloseAsync("No Hello received").ConfigureAwait(false);
                return;
            }

            // Validate protocol version
            if (hello.Version != SupportedProtocolVersion)
            {
                _logger.LogWarning("Client {ConnectionId}: Protocol version mismatch (client: {ClientVersion}, server: {ServerVersion})",
                    connection.ConnectionId, hello.Version, SupportedProtocolVersion);
                await connection.SendErrorAsync(new ErrorPayload
                {
                    Code = ErrorCode.VersionMismatch,
                    Message = $"Unsupported protocol version {hello.Version}. Server supports version {SupportedProtocolVersion}."
                }, stoppingToken).ConfigureAwait(false);
                await connection.CloseAsync("Protocol version mismatch").ConfigureAwait(false);
                return;
            }

            _logger.LogInformation("Client {ConnectionId} connected (protocol v{Version}), sending Welcome...",
                connection.ConnectionId, hello.Version);

            // Register connection BEFORE Welcome (register-before-Welcome pattern)
            _connections[connection.ConnectionId] = connection;
            registered = true;

            // Build snapshot under _applyUpdateLock so the snapshot is consistent with its sequence number.
            // Trade-off: this blocks incoming updates for the duration of the snapshot, which is proportional
            // to graph size. Acceptable because new-client connections are infrequent relative to update rate.
            SubjectUpdate initialState;
            long welcomeSequence;
            lock (_applyUpdateLock)
            {
                welcomeSequence = Volatile.Read(ref _sequence);
                initialState = SubjectUpdate.CreateCompleteUpdate(_subject, _processors);
            }

            // Send Welcome (flushes queued updates under _sendLock)
            await connection.SendWelcomeAsync(initialState, welcomeSequence, stoppingToken).ConfigureAwait(false);

            _logger.LogInformation("Client {ConnectionId}: Welcome sent, waiting for updates...", connection.ConnectionId);

            // Handle incoming updates
            await ReceiveUpdatesAsync(connection, stoppingToken).ConfigureAwait(false);

            _logger.LogDebug("Client {ConnectionId}: ReceiveUpdatesAsync returned normally", connection.ConnectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling client {ConnectionId}", connection.ConnectionId);
        }
        finally
        {
            if (registered)
            {
                // Only decrement if we successfully removed (prevents double-decrement with zombie cleanup)
                if (_connections.TryRemove(connection.ConnectionId, out _))
                {
                    Interlocked.Decrement(ref _connectionCount);
                }
                // else: zombie cleanup in BroadcastUpdateAsync already removed and decremented
            }
            else
            {
                // Handshake failed before registration — release the slot
                Interlocked.Decrement(ref _connectionCount);
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            _logger.LogInformation("Client {ConnectionId} disconnected", connection.ConnectionId);
        }
    }

    private async Task ReceiveUpdatesAsync(WebSocketClientConnection connection, CancellationToken stoppingToken)
    {
        _logger.LogDebug("Client {ConnectionId}: Starting receive loop (IsConnected={IsConnected})",
            connection.ConnectionId, connection.IsConnected);

        while (!stoppingToken.IsCancellationRequested && connection.IsConnected)
        {
            SubjectUpdate? update;
            try
            {
                update = await connection.ReceiveUpdateAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
            {
                _logger.LogWarning(ex, "Client {ConnectionId}: Invalid message received", connection.ConnectionId);
                await connection.SendErrorAsync(new ErrorPayload
                {
                    Code = ErrorCode.InvalidFormat,
                    Message = "Invalid message format."
                }, stoppingToken).ConfigureAwait(false);
                break;
            }

            if (update is null)
            {
                _logger.LogWarning("Client {ConnectionId}: Received null update, exiting loop", connection.ConnectionId);
                break;
            }

            try
            {
                var factory = _configuration.SubjectFactory ?? DefaultSubjectFactory.Instance;
                // The lock serializes update application so concurrent client updates apply one at a time.
                lock (_applyUpdateLock)
                {
                    _subject.ApplySubjectUpdate(update, factory, ChangeOrigin.FromSource(connection));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying update from client {ConnectionId}", connection.ConnectionId);
                await connection.SendErrorAsync(new ErrorPayload
                {
                    Code = ErrorCode.InternalError,
                    Message = "An internal error occurred while processing the update."
                }, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogDebug("Client {ConnectionId}: Exited receive loop (Cancelled={Cancelled}, IsConnected={IsConnected})",
            connection.ConnectionId, stoppingToken.IsCancellationRequested, connection.IsConnected);
    }

    public async ValueTask BroadcastChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (changes.Length == 0 || _connections.IsEmpty) return;

        var batchSize = _configuration.WriteBatchSize;
        if (batchSize <= 0 || changes.Length <= batchSize)
        {
            // Single batch
            var update = SubjectUpdate.CreatePartialUpdateFromChanges(_subject, changes.Span, _processors);
            await BroadcastUpdateAsync(update, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Multiple batches
            for (var i = 0; i < changes.Length; i += batchSize)
            {
                var currentBatchSize = Math.Min(batchSize, changes.Length - i);
                var batch = changes.Slice(i, currentBatchSize);
                var update = SubjectUpdate.CreatePartialUpdateFromChanges(_subject, batch.Span, _processors);
                await BroadcastUpdateAsync(update, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <remarks>
    /// Must be called sequentially (not concurrently) to guarantee in-order
    /// sequence delivery to clients. This is ensured by ChangeQueueProcessor
    /// which calls BroadcastChangesAsync from a single flush thread.
    /// The sequence increment is guarded by _applyUpdateLock to prevent a Welcome
    /// snapshot from reading a mid-batch sequence number during multi-batch broadcasts.
    /// </remarks>
    private async Task BroadcastUpdateAsync(SubjectUpdate update, CancellationToken cancellationToken)
    {
        if (_connections.IsEmpty) return;

        long sequence;
        lock (_applyUpdateLock)
        {
            sequence = Interlocked.Increment(ref _sequence);
        }

        var updatePayload = new UpdatePayload
        {
            Root = update.Root,
            Subjects = update.Subjects,
            Sequence = sequence
        };

        var serializedMessage = _serializer.SerializeMessage(MessageType.Update, updatePayload);

        await BroadcastToAllAsync(
            connection => connection.SendUpdateAsync(serializedMessage, sequence, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Broadcasts a heartbeat to every connected client on
    /// <see cref="WebSocketServerConfiguration.HeartbeatInterval"/>, and runs until
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <remarks>
    /// The returned task never completes on its own, not even when heartbeats are disabled, because
    /// callers race it against the change processor and treat either one finishing as a reason to
    /// restart the server. Pass a token that is cancelled when the caller stops.
    /// </remarks>
    /// <param name="cancellationToken">Cancelled to end the loop.</param>
    public async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        var interval = _configuration.HeartbeatInterval;
        if (interval <= TimeSpan.Zero)
        {
            // Heartbeats are disabled, but this must not complete, for the reason in the remarks above.
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }

            return;
        }

        _logger.LogInformation("Heartbeat loop started (interval: {Interval})", interval);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

                try
                {
                    await BroadcastHeartbeatAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error broadcasting heartbeat");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }

        _logger.LogInformation("Heartbeat loop stopped");
    }

    private async Task BroadcastHeartbeatAsync(CancellationToken cancellationToken)
    {
        if (_connections.IsEmpty) return;

        var heartbeat = new HeartbeatPayload
        {
            Sequence = Volatile.Read(ref _sequence)
        };

        var serializedMessage = _serializer.SerializeMessage(MessageType.Heartbeat, heartbeat);

        await BroadcastToAllAsync(
            connection => connection.SendHeartbeatAsync(serializedMessage, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task BroadcastToAllAsync(Func<WebSocketClientConnection, Task> sendAsync, CancellationToken cancellationToken)
    {
        var tasks = new List<Task>(_connections.Count);
        foreach (var connection in _connections.Values)
        {
            tasks.Add(sendAsync(connection));
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(_configuration.BroadcastTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Broadcast to {Count} client(s) timed out after {Timeout}", _connections.Count, _configuration.BroadcastTimeout);
        }
        finally
        {
            await RemoveZombieConnectionsAsync().ConfigureAwait(false);
        }
    }

    private async Task RemoveZombieConnectionsAsync()
    {
        foreach (var (connectionId, connection) in _connections)
        {
            if (connection.HasRepeatedSendFailures && _connections.TryRemove(connectionId, out _))
            {
                _logger.LogWarning("Removing zombie connection {ConnectionId} due to repeated send failures", connectionId);
                Interlocked.Decrement(ref _connectionCount);
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public ChangeQueueProcessor CreateChangeQueueProcessor(ILogger logger) =>
        CreateChangeQueueProcessor(logger, dropHandler: null);

    internal ChangeQueueProcessor CreateChangeQueueProcessor(ILogger logger, Action<long>? dropHandler) =>
        new(source: this, Context,
            propertyFilter: propertyReference =>
                propertyReference.TryGetRegisteredProperty() is { } property &&
                (_configuration.PathProvider?.IsPropertyIncluded(property) ?? true),
            writeHandler: BroadcastChangesAsync,
            // Safe only because inbound updates are applied under the originating connection rather than
            // this handler, so none of them is skipped here as our own echo and every superseding value
            // is broadcast on. Applying them under this handler would break it.
            ChangeDeliveryRule.SourceValuesAreSettled,
            BufferTime, null, logger, dropHandler);

    public async ValueTask CloseAllConnectionsAsync()
    {
        // Snapshot current keys and drain connections
        var connectionsToClose = new List<WebSocketClientConnection>();
        foreach (var key in _connections.Keys.ToArray())
        {
            if (_connections.TryRemove(key, out var connection))
            {
                Interlocked.Decrement(ref _connectionCount);
                connectionsToClose.Add(connection);
            }
        }

        if (connectionsToClose.Count == 0)
        {
            return;
        }

        // Close all in parallel
        var closeTasks = new Task[connectionsToClose.Count];
        for (var i = 0; i < connectionsToClose.Count; i++)
        {
            var connection = connectionsToClose[i];
            closeTasks[i] = CloseConnectionAsync(connection);
        }

        await Task.WhenAll(closeTasks).ConfigureAwait(false);
    }

    private async Task CloseConnectionAsync(WebSocketClientConnection connection)
    {
        try
        {
            await connection.CloseAsync("Server shutting down").ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing connection {ConnectionId}", connection.ConnectionId);
        }
    }
}
