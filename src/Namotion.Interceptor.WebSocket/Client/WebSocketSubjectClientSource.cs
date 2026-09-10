using System;
using System.Buffers;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.IO;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Resilience;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.WebSocket.Internal;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;

namespace Namotion.Interceptor.WebSocket.Client;

/// <summary>
/// WebSocket client source that connects to a WebSocket server and synchronizes subjects.
/// </summary>
public sealed class WebSocketSubjectClientSource : SubjectSourceBase, IFaultInjectable, IAsyncDisposable
{
    private const int SendBufferShrinkThreshold = 256 * 1024;

    private static RecyclableMemoryStreamManager MemoryStreamManager => WebSocketMessageReader.MemoryStreamManager;

    private readonly IInterceptorSubject _subject;
    private readonly WebSocketClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly ISubjectUpdateProcessor[] _processors;
    private readonly IWebSocketSerializer _serializer = JsonWebSocketSerializer.Instance;
    private ArrayBufferWriter<byte> _sendBuffer = new(4096);

    private volatile ClientWebSocket? _webSocket;
    private volatile SubjectPropertyWriter? _propertyWriter;
    private volatile SubjectUpdate? _initialState;
    private volatile CancellationTokenSource? _receiveCts;
    private volatile TaskCompletionSource? _receiveLoopCompleted;
    private TaskCompletionSource? _receiveLoopLivenessOwner;
    private ConnectorCommitLease? _receiveLoopCommitLease;
    private readonly SourceOwnershipManager _ownership;
    private readonly CircuitBreaker? _circuitBreaker;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    // A receive loop may finish while its replacement connects. Keep owner publication and both
    // liveness transitions ordered without putting a lock on the message receive path.
    private readonly Lock _receiveLoopLivenessLock = new();

    private int _disposed;

    /// <inheritdoc />
    public override IInterceptorSubject RootSubject => _subject;

    /// <inheritdoc />
    public override int WriteBatchSize => _configuration.WriteBatchSize;

    public WebSocketSubjectClientSource(
        IInterceptorSubject subject,
        WebSocketClientConfiguration configuration,
        ILogger<WebSocketSubjectClientSource> logger)
        : base(
            (subject ?? throw new ArgumentNullException(nameof(subject))).GetContext(),
            logger ?? throw new ArgumentNullException(nameof(logger)),
            (configuration ?? throw new ArgumentNullException(nameof(configuration))).BufferTime,
            configuration.RetryTime,
            configuration.WriteRetryQueueSize)
    {
        _subject = subject;
        _configuration = configuration;
        _logger = logger;
        _processors = configuration.Processors;
        _ownership = new SourceOwnershipManager(this);

        Metrics.RegisterClaimedProperties(() => _ownership.Count);

        if (configuration.CircuitBreakerFailureThreshold > 0)
        {
            _circuitBreaker = new CircuitBreaker(
                configuration.CircuitBreakerFailureThreshold,
                configuration.CircuitBreakerCooldown);
        }

        configuration.Validate();
    }

    internal SourceOwnershipManager Ownership => _ownership;

    /// <inheritdoc />
    protected override async Task<IAsyncDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        _propertyWriter = propertyWriter;

        try
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);

            return BackgroundTaskLifetime.Start(
                cancellationToken,
                _logger,
                RunMonitorLoopAsync,
                DisposeWebSocketConnectionAsync);
        }
        catch
        {
            await StopReceiveLoopAndDisposeSocketAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask DisposeWebSocketConnectionAsync()
    {
        await RetireReceiveLoopCommitsAsync().ConfigureAwait(false);

        var currentSocket = _webSocket;
        if (currentSocket?.State == WebSocketState.Open)
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await currentSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", closeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                currentSocket.Abort();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while closing WebSocket connection");
            }
        }

        await StopReceiveLoopAndDisposeSocketAsync().ConfigureAwait(false);
    }

    private async Task StopReceiveLoopAndDisposeSocketAsync()
    {
        await RetireReceiveLoopCommitsAsync().ConfigureAwait(false);

        var receiveCts = _receiveCts;
        if (receiveCts is not null)
        {
            try { await receiveCts.CancelAsync().ConfigureAwait(false); } catch { /* ignore */ }

            var receiveLoop = _receiveLoopCompleted?.Task;
            if (receiveLoop is not null && !receiveLoop.IsCompleted)
            {
                await WaitForReceiveLoopExitAsync(receiveLoop).ConfigureAwait(false);
            }

            try { receiveCts.Dispose(); } catch { /* ignore */ }
            _receiveCts = null;
        }

        var webSocket = _webSocket;
        if (webSocket is not null)
        {
            try { webSocket.Abort(); } catch { /* ignore */ }
            try { webSocket.Dispose(); } catch { /* ignore */ }
            _webSocket = null;
        }
    }

    private async Task WaitForReceiveLoopExitAsync(Task receiveLoop)
    {
        try
        {
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await receiveLoop.WaitAsync(waitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A loop that outlives this timeout is already excluded from committing: its lease was
            // retired before the wait started, so cleanup can proceed without it.
            _logger.LogWarning("Receive loop did not complete within timeout, continuing cleanup");
        }
    }

    /// <summary>
    /// Retires the current commit lease and waits for every admitted commit to finish.
    /// </summary>
    /// <remarks>
    /// The drain is deliberately unbounded, with no timeout or token, and disposal also passes
    /// through here after its capped stop has already returned: a commit that is already inside
    /// <c>ApplySubjectUpdate</c> must not land after a replacement connection has loaded its state,
    /// and capping the wait would reopen exactly that race. The cost is that teardown can block for
    /// as long as a single property commit takes.
    /// </remarks>
    private async ValueTask RetireReceiveLoopCommitsAsync()
    {
        var commitLease = Volatile.Read(ref _receiveLoopCommitLease);
        if (commitLease is null)
        {
            return;
        }

        var retirementTask = commitLease.RetireAsync();
        Interlocked.CompareExchange(ref _receiveLoopCommitLease, null, commitLease);
        await retirementTask.ConfigureAwait(false);
        AfterReceiveLoopCommitDrain?.Invoke();
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        await RetireReceiveLoopCommitsAsync().ConfigureAwait(false);

        // Clean up any previous connection
        if (_receiveCts is not null)
        {
            await _receiveCts.CancelAsync().ConfigureAwait(false);

            // Wait for receive loop to exit before disposing socket
            var previousReceiveLoop = _receiveLoopCompleted?.Task;
            if (previousReceiveLoop is not null)
            {
                await WaitForReceiveLoopExitAsync(previousReceiveLoop).ConfigureAwait(false);
            }

            var oldCts = _receiveCts;
            _receiveCts = null;
            oldCts.Dispose();
        }

        // Now safe to dispose socket
        _webSocket?.Dispose();

        var receiveLoopStarted = false;
        var receiveCancellation = new CancellationTokenSource();
        var receiveLoopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiveLoopCommitLease = new ConnectorCommitLease();

        // Per attempt rather than shared: a tracker surviving a reconnect would validate the new
        // connection's sequence numbers against the old connection's position.
        var sequenceTracker = new ClientSequenceTracker();
        _receiveCts = receiveCancellation;
        _receiveLoopCompleted = receiveLoopCompletion;

        try
        {
            _webSocket = new ClientWebSocket();

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(_configuration.ConnectTimeout);

            _logger.LogInformation("Connecting to WebSocket server at {Uri}", _configuration.ServerUri);
            await _webSocket.ConnectAsync(_configuration.ServerUri!, connectCts.Token).ConfigureAwait(false);

            // Send Hello using reusable buffer
            var hello = new HelloPayload { Format = WebSocketFormat.Json };
            _sendBuffer.Clear();
            _serializer.SerializeMessageTo(_sendBuffer, MessageType.Hello, hello);
            await _webSocket.SendAsync(_sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

            // Receive Welcome using shared utility
            using var readResult = await WebSocketMessageReader.ReadMessageAsync(
                _webSocket, _configuration.MaxMessageSize, cancellationToken).ConfigureAwait(false);

            if (readResult.IsCloseMessage)
            {
                throw new InvalidOperationException("Server closed connection during handshake");
            }

            if (readResult.ExceededMaxSize)
            {
                throw new InvalidOperationException($"Message exceeds maximum size of {_configuration.MaxMessageSize} bytes");
            }

            if (!readResult.Success)
            {
                throw new InvalidOperationException("Failed to receive Welcome message");
            }

            var (messageType, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(readResult.MessageBytes.Span);
            if (messageType == MessageType.Error)
            {
                var error = _serializer.Deserialize<ErrorPayload>(readResult.MessageBytes.Span.Slice(payloadStart, payloadLength));
                throw new InvalidOperationException($"Server returned error during handshake: [{error.Code}] {error.Message}");
            }

            if (messageType != MessageType.Welcome)
            {
                throw new InvalidOperationException($"Expected Welcome message, got {messageType}");
            }

            var welcome = _serializer.Deserialize<WelcomePayload>(readResult.MessageBytes.Span.Slice(payloadStart, payloadLength));
            if (welcome.Version != WebSocketProtocol.Version)
            {
                throw new InvalidOperationException($"Unsupported server protocol version {welcome.Version}. Client supports version {WebSocketProtocol.Version}.");
            }

            _initialState = welcome.State;
            sequenceTracker.InitializeFromWelcome(welcome.Sequence);

            Volatile.Write(ref _receiveLoopCommitLease, receiveLoopCommitLease);
            PublishReceiveLoopAndMarkOperational(receiveLoopCompletion);
            _logger.LogInformation("Connected to WebSocket server (sequence: {Sequence})", welcome.Sequence);

            // Start receive loop (signals _receiveLoopCompleted when done)
            _ = ReceiveLoopAsync(receiveCancellation.Token, receiveLoopCompletion, receiveLoopCommitLease, sequenceTracker);
            receiveLoopStarted = true;
        }
        finally
        {
            if (!receiveLoopStarted)
            {
                MarkNotOperationalAfterFailedConnection();

                // Dispose the socket to avoid holding resources during backoff delay
                _webSocket?.Dispose();
                _webSocket = null;

                // Signal completion to prevent the monitor loop from hanging
                receiveLoopCompletion.TrySetResult();
            }
        }
    }

    /// <inheritdoc />
    public override Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        if (_initialState is null)
        {
            return Task.FromResult<Action?>(null);
        }

        return Task.FromResult<Action?>(() =>
        {
            var factory = _configuration.SubjectFactory ?? DefaultSubjectFactory.Instance;
            _subject.ApplySubjectUpdate(_initialState, factory, ChangeOrigin.FromSource(this));

            // Claim ownership of all properties matching the path provider
            ClaimPropertyOwnership();

            _initialState = null;
        });
    }

    private void ClaimPropertyOwnership()
    {
        var pathProvider = _configuration.PathProvider;

        var registeredSubject = _subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            _logger.LogWarning("Subject is not registered. Cannot claim property ownership.");
            return;
        }

        var properties = registeredSubject
            .GetAllProperties()
            .Where(p => !p.CanContainSubjects && (pathProvider is null || pathProvider.IsPropertyIncluded(p)))
            .ToList();

        var claimedCount = 0;
        foreach (var property in properties)
        {
            if (_ownership.ClaimSource(property.Reference))
            {
                claimedCount++;
            }
            else
            {
                _logger.LogWarning(
                    "Property {Subject}.{Property} already owned by another source.",
                    property.Subject.GetType().Name, property.Name);
            }
        }

        _logger.LogInformation("Claimed ownership of {Count} properties for WebSocket sync.", claimedCount);
    }

    /// <inheritdoc />
    public override async ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        _logger.LogDebug("WriteChangesAsync called with {Count} changes", changes.Length);

        if (Volatile.Read(ref _disposed) == 1)
        {
            return WriteResult.Failure(changes, new ObjectDisposedException(nameof(WebSocketSubjectClientSource)));
        }

        try
        {
            await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return WriteResult.Failure(changes, new ObjectDisposedException(nameof(WebSocketSubjectClientSource)));
        }

        try
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return WriteResult.Failure(changes, new ObjectDisposedException(nameof(WebSocketSubjectClientSource)));
            }

            var webSocket = _webSocket;
            if (webSocket?.State != WebSocketState.Open)
            {
                return WriteResult.Failure(changes, new InvalidOperationException("WebSocket is not connected"));
            }

            var update = SubjectUpdate.CreatePartialUpdateFromChanges(_subject, changes.Span, _processors);
            _sendBuffer.Clear();
            _serializer.SerializeMessageTo(_sendBuffer, MessageType.Update, update);
            _logger.LogDebug("Sending {ByteCount} bytes ({SubjectCount} subjects) to server",
                _sendBuffer.WrittenCount, update.Subjects.Count);
            await webSocket.SendAsync(_sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            MaybeShrinkSendBuffer();
            _logger.LogDebug("Sent update successfully");
            return WriteResult.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send update to server");
            return WriteResult.Failure(changes, ex);
        }
        finally
        {
            try
            {
                _connectionLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // Lock was disposed during operation
            }
        }
    }

    private async Task ReceiveLoopAsync(
        CancellationToken cancellationToken,
        TaskCompletionSource completionSource,
        ConnectorCommitLease commitLease,
        ClientSequenceTracker sequenceTracker)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

        // Reusable CTS to reduce allocations (reset instead of recreate when possible)
        var timeoutCts = new CancellationTokenSource();
        CancellationTokenSource? linkedCts = null;
        var consecutiveErrors = 0;

        try
        {
            // Capture once — this receive loop is tied to a single connection.
            var webSocket = _webSocket;
            while (!cancellationToken.IsCancellationRequested && webSocket?.State == WebSocketState.Open)
            {
                try
                {
                    var messageStream = MemoryStreamManager.GetStream();
                    await using (messageStream.ConfigureAwait(false))
                    {
                        // Reset or recreate the timeout CTS for each message
                        if (!timeoutCts.TryReset())
                        {
                            timeoutCts.Dispose();
                            timeoutCts = new CancellationTokenSource();
                        }

                        timeoutCts.CancelAfter(_configuration.ReceiveTimeout);

                        // Create linked token for this message
                        linkedCts?.Dispose();
                        linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                        // Use shared utility for fragmented message receive
                        var readResult = await WebSocketMessageReader.ReadMessageIntoStreamAsync(
                            webSocket, buffer, messageStream, _configuration.MaxMessageSize, linkedCts.Token).ConfigureAwait(false);

                        if (readResult.IsCloseMessage)
                        {
                            _logger.LogInformation("Server closed connection");
                            return;
                        }

                        if (readResult.ExceededMaxSize)
                        {
                            _logger.LogWarning("Message exceeds maximum size of {MaxSize} bytes", _configuration.MaxMessageSize);
                            throw new InvalidOperationException($"Message exceeds maximum size of {_configuration.MaxMessageSize} bytes");
                        }

                        var messageBytes = new ReadOnlySpan<byte>(messageStream.GetBuffer(), 0, (int)messageStream.Length);
                        var (messageType, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(messageBytes);
                        var payloadBytes = messageBytes.Slice(payloadStart, payloadLength);

                        switch (messageType)
                        {
                            case MessageType.Update:
                                var update = _serializer.Deserialize<UpdatePayload>(payloadBytes);
                                if (update.Sequence is not null && !sequenceTracker.IsUpdateValid(update.Sequence.Value))
                                {
                                    _logger.LogWarning(
                                        "Sequence gap detected: expected {Expected}, received {Received}. Triggering reconnection.",
                                        sequenceTracker.ExpectedNextSequence, update.Sequence);
                                    return; // Exit receive loop -> triggers reconnection
                                }
                                HandleUpdate(update, commitLease);
                                break;

                            case MessageType.Heartbeat:
                                var heartbeat = _serializer.Deserialize<HeartbeatPayload>(payloadBytes);
                                if (!sequenceTracker.IsHeartbeatInSync(heartbeat.Sequence))
                                {
                                    _logger.LogWarning(
                                        "Heartbeat sequence gap: server at {ServerSequence}, client expects {Expected}. Triggering reconnection.",
                                        heartbeat.Sequence, sequenceTracker.ExpectedNextSequence);
                                    return; // Exit receive loop -> triggers reconnection
                                }
                                break;

                            case MessageType.Error:
                                var error = _serializer.Deserialize<ErrorPayload>(payloadBytes);
                                _logger.LogWarning("Received error from server: {Code} - {Message}", error.Code, error.Message);
                                break;
                        }

                        consecutiveErrors = 0;
                    }
                }
                catch (WebSocketException ex)
                {
                    _logger.LogWarning(ex, "WebSocket error in receive loop");
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Normal shutdown
                    break;
                }
                catch (OperationCanceledException)
                {
                    // Receive timeout - connection considered lost
                    _logger.LogWarning("Receive timeout exceeded ({Timeout}), connection considered lost", _configuration.ReceiveTimeout);
                    break;
                }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    _logger.LogError(ex, "Error processing received message (consecutive errors: {Count})", consecutiveErrors);

                    const int maxConsecutiveReceiveErrors = 5;
                    if (consecutiveErrors >= maxConsecutiveReceiveErrors)
                    {
                        _logger.LogError("Too many consecutive errors ({Count}), exiting receive loop", consecutiveErrors);
                        break;
                    }
                }
            }
        }
        finally
        {
            try
            {
                MarkReceiveLoopNotOperational(completionSource);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                timeoutCts.Dispose();
                linkedCts?.Dispose();

                // Signal that receive loop has completed (for reconnection handling)
                completionSource.TrySetResult();
            }
        }
    }

    private void PublishReceiveLoopAndMarkOperational(TaskCompletionSource completionSource)
    {
        BeforeReceiveLoopPublication?.Invoke();

        lock (_receiveLoopLivenessLock)
        {
            _receiveLoopLivenessOwner = completionSource;
            Metrics.MarkOperational();
        }
    }

    private void MarkReceiveLoopNotOperational(TaskCompletionSource completionSource)
    {
        lock (_receiveLoopLivenessLock)
        {
            if (ReferenceEquals(_receiveLoopLivenessOwner, completionSource))
            {
                BeforeReceiveLoopLivenessTransition?.Invoke();
                Metrics.MarkNotOperational();
            }
        }
    }

    private void MarkNotOperationalAfterFailedConnection()
    {
        lock (_receiveLoopLivenessLock)
        {
            Metrics.MarkNotOperational();
        }
    }

    private void HandleUpdate(SubjectUpdate update, ConnectorCommitLease commitLease)
    {
        BeforeUpdateCommitAdmission?.Invoke();

        var propertyWriter = _propertyWriter;
        if (propertyWriter is null) return;

        propertyWriter.Write(
            (update, subject: _subject, factory: _configuration.SubjectFactory ?? DefaultSubjectFactory.Instance, source: this, commitLease),
            static state =>
            {
                if (!state.commitLease.TryAcquireCommit())
                {
                    return;
                }

                try
                {
                    state.subject.ApplySubjectUpdate(
                        state.update,
                        state.factory,
                        ChangeOrigin.FromSource(state.source));
                }
                finally
                {
                    state.commitLease.ReleaseCommit();
                }
            });
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
                _webSocket?.Abort();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(faultType), faultType, null);
        }
    }

    /// <summary>
    /// Monitor connection and handle reconnection with exponential backoff.
    /// Spawned by <see cref="StartListeningAsync"/> via <see cref="BackgroundTaskLifetime.Start"/>.
    /// Cancellation of <paramref name="stoppingToken"/> (via lifetime disposal or host shutdown)
    /// breaks the outer loop.
    /// </summary>
    private async Task RunMonitorLoopAsync(CancellationToken stoppingToken)
    {
        // Monitor connection and handle reconnection with exponential backoff
        var reconnectDelay = _configuration.ReconnectDelay;
        var maxDelay = _configuration.MaxReconnectDelay;
        var forceReconnect = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            var stopMonitoring = false;

            await RunAttemptAsync(stoppingToken, async attempt =>
            {
                var linkedToken = attempt.Token;

                try
                {
                    if (forceReconnect)
                    {
                        forceReconnect = false;
                        reconnectDelay = await ReconnectAndResumeAsync(
                            "WebSocket reconnected after force-kill", reconnectDelay, maxDelay, linkedToken).ConfigureAwait(false);
                        return;
                    }

                    // Wait for receive loop to complete (connection dropped)
                    var receiveLoopTask = _receiveLoopCompleted?.Task;
                    if (receiveLoopTask is not null)
                    {
                        await receiveLoopTask.WaitAsync(linkedToken).ConfigureAwait(false);
                    }

                    // Connection dropped - check if we should reconnect
                    if (stoppingToken.IsCancellationRequested || Volatile.Read(ref _disposed) == 1)
                    {
                        stopMonitoring = true;
                        return;
                    }

                    _logger.LogWarning("WebSocket connection lost. Attempting reconnection in {Delay}...", reconnectDelay);

                    _propertyWriter?.StartBuffering();

                    // Circuit breaker: pause reconnection if too many consecutive failures
                    if (_circuitBreaker is not null && !_circuitBreaker.ShouldAttempt())
                    {
                        var cooldownRemaining = _circuitBreaker.GetCooldownRemaining();
                        _logger.LogWarning(
                            "Circuit breaker open after {TripCount} trips. Pausing reconnection attempts for {Cooldown}s.",
                            _circuitBreaker.TripCount,
                            (int)cooldownRemaining.TotalSeconds);

                        await Task.Delay(cooldownRemaining, linkedToken).ConfigureAwait(false);

                        // Reset backoff after cooldown so the first retry is fast
                        reconnectDelay = _configuration.ReconnectDelay;
                    }

                    await Task.Delay(reconnectDelay, linkedToken).ConfigureAwait(false);

                    reconnectDelay = await ReconnectAndResumeAsync(
                        "WebSocket reconnected successfully", reconnectDelay, maxDelay, linkedToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                }
                catch (OperationCanceledException) when (attempt.WasForceKilled)
                {
                    _logger.LogWarning("WebSocket client force-killed. Restarting...");
                    _webSocket?.Abort();
                    _propertyWriter?.StartBuffering();
                    forceReconnect = true;
                }
                catch (Exception ex)
                {
                    // Nothing outside this loop reports its failures, but a stop tears the socket down with
                    // a WebSocketException or ObjectDisposedException rather than a cancellation, so only
                    // the stopping token tells a shutdown apart from a genuine fault.
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        Metrics.ReportError(ex);
                    }

                    _logger.LogError(ex, "Error in WebSocket connection monitoring");
                }
            }).ConfigureAwait(false);

            if (stopMonitoring)
            {
                break;
            }
        }
    }

    private async Task<TimeSpan> ReconnectAndResumeAsync(
        string successMessage, TimeSpan reconnectDelay, TimeSpan maxDelay, CancellationToken cancellationToken)
    {
        try
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            _circuitBreaker?.RecordSuccess();
            _logger.LogInformation(successMessage);

            if (_propertyWriter is not null)
            {
                await _propertyWriter.LoadInitialStateAndResumeAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return _configuration.ReconnectDelay;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Cancellation may surface as a transport exception rather than an OperationCanceledException.
            cancellationToken.ThrowIfCancellationRequested();
            Metrics.ReportError(ex);

            _logger.LogError(ex, "Failed to reconnect to WebSocket server");

            if (_circuitBreaker is not null && _circuitBreaker.RecordFailure())
            {
                _logger.LogWarning(
                    "Circuit breaker tripped after {Threshold} consecutive failures. " +
                    "Pausing reconnection attempts for {Cooldown}s.",
                    _configuration.CircuitBreakerFailureThreshold,
                    (int)_configuration.CircuitBreakerCooldown.TotalSeconds);
            }

            // Exponential backoff with equal jitter (0.5 to 1.0) to decorrelate reconnection attempts
            var jitter = Random.Shared.NextDouble() * 0.5 + 0.5;
            return TimeSpan.FromMilliseconds(
                Math.Min(reconnectDelay.TotalMilliseconds * 2 * jitter, maxDelay.TotalMilliseconds));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // Stop the base ExecuteAsync first: this cancels the stoppingToken and asks the listen
        // lifetime (which owns the monitor task) to wind down. The stop is capped, so it can return
        // while that teardown is still in flight; the retirement drain below is what actually keeps
        // a straggling commit from landing after disposal.
        try
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await StopAsync(stopCts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort stop
        }

        // Belt-and-braces: retire the lease and cancel and dispose any straggler receive CTS / socket.
        await StopReceiveLoopAndDisposeSocketAsync().ConfigureAwait(false);

        // Clean up resources
        _ownership.Dispose();
        _connectionLock.Dispose();

        Dispose();
    }

    private void MaybeShrinkSendBuffer()
    {
        if (_sendBuffer is { Capacity: > SendBufferShrinkThreshold, WrittenCount: < SendBufferShrinkThreshold / 4 })
        {
            _sendBuffer = new ArrayBufferWriter<byte>(4096);
        }
    }

    // Test seams for interleavings that have no externally observable synchronization point: the
    // instant between a received update and its lease admission, the two sides of the liveness
    // lock, and the instant the commit drain releases the teardown. Always null in production; the
    // tests block inside them or sample ordering from them.
    internal Action? BeforeUpdateCommitAdmission { get; set; }

    internal Action? BeforeReceiveLoopLivenessTransition { get; set; }

    internal Action? BeforeReceiveLoopPublication { get; set; }

    internal Action? AfterReceiveLoopCommitDrain { get; set; }
}
