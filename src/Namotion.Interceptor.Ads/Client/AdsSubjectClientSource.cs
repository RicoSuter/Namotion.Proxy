using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.SumCommand;
using TwinCAT.TypeSystem;

namespace Namotion.Interceptor.Ads.Client;

/// <summary>
/// Connects a subject graph to a Beckhoff TwinCAT PLC via ADS protocol.
/// Thin orchestrator composing <see cref="AdsConnectionManager"/> and <see cref="AdsSubscriptionManager"/>.
/// </summary>
public sealed class AdsSubjectClientSource : SubjectSourceBase, IAsyncDisposable
{
    private readonly IInterceptorSubject _subject;
    private readonly AdsClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly SourceOwnershipManager _ownership;
    private readonly AdsSubjectLoader _subjectLoader;
    private readonly AdsConnectionManager _connectionManager;
    private readonly AdsSubscriptionManager _subscriptionManager;
    private readonly SemaphoreSlim _rescanSignal = new(0, 1);
    private readonly SemaphoreSlim _rescanLock = new(1, 1);

    private volatile SubjectPropertyWriter? _propertyWriter;
    private long _lastRescanRequestedAtTicks; // DateTimeOffset.UtcNow.UtcTicks, 0 = no pending request

    private int _disposed; // 0 = false, 1 = true

    /// <summary>
    /// Initializes a new instance of the <see cref="AdsSubjectClientSource"/> class.
    /// </summary>
    /// <param name="subject">The root subject to synchronize with the PLC.</param>
    /// <param name="configuration">The ADS client configuration.</param>
    /// <param name="logger">The logger instance.</param>
    public AdsSubjectClientSource(
        IInterceptorSubject subject,
        AdsClientConfiguration configuration,
        ILogger logger)
        : base(
            (subject ?? throw new ArgumentNullException(nameof(subject))).Context,
            logger ?? throw new ArgumentNullException(nameof(logger)),
            (configuration ?? throw new ArgumentNullException(nameof(configuration))).BufferTime,
            configuration.RetryTime,
            configuration.WriteRetryQueueSize)
    {
        configuration.Validate();

        _subject = subject;
        _configuration = configuration;
        _logger = logger;

        _connectionManager = new AdsConnectionManager(configuration, logger);
        _subscriptionManager = new AdsSubscriptionManager(configuration, logger);
        _subjectLoader = new AdsSubjectLoader(configuration.Mapper);

        Metrics.RegisterResettable(_subscriptionManager.PollingMetrics);
        Diagnostics = new AdsClientDiagnostics(this, Metrics);

        _ownership = new SourceOwnershipManager(
            this,
            onReleasing: _subscriptionManager.OnPropertyReleasing);

        // Wire connection events to request debounced rescan via ExecuteAsync loop
        _connectionManager.ConnectionRestored += () => RequestRescan("ADS connection restored.");
        _connectionManager.ConnectionLost += () => _propertyWriter?.StartBuffering();
        _connectionManager.AdsStateEnteredRun += () => RequestRescan("PLC entered Run state.");
        _connectionManager.SymbolVersionChanged += () => RequestRescan("Symbol version changed.");
    }

    /// <summary>
    /// Gets the ADS client configuration (internal for testing).
    /// </summary>
    internal AdsClientConfiguration Configuration => _configuration;

    /// <summary>
    /// Gets the connection manager (internal for testing and diagnostics).
    /// </summary>
    internal AdsConnectionManager ConnectionManager => _connectionManager;

    /// <summary>
    /// Gets the subscription manager (internal for diagnostics).
    /// </summary>
    internal AdsSubscriptionManager SubscriptionManager => _subscriptionManager;

    /// <inheritdoc />
    public override IInterceptorSubject RootSubject => _subject;

    /// <inheritdoc />
    public override int WriteBatchSize => 0; // No limit - sequential writes

    /// <inheritdoc />
    protected override async Task<IAsyncDisposable?> StartListeningAsync(
        SubjectPropertyWriter propertyWriter,
        CancellationToken cancellationToken)
    {
        _propertyWriter = propertyWriter;

        // Start rescan + polling loops first so they can react to connection events
        // (e.g. ConnectionRestored) the moment ConnectWithRetryAsync establishes a session.
        // Both loops handle the no-connection case as a no-op, matching the legacy two-BG-service
        // layout where these loops ran independently of the connect lifecycle.
        var lifetime = BackgroundTaskLifetime.Start(
            cancellationToken,
            _logger,
            RunListenLoopsAsync,
            () =>
            {
                // ClearAll() keeps the underlying CompositeDisposable reusable for the next
                // listen attempt; BackgroundTaskLifetime takes care of cancelling and awaiting
                // the loop tasks before this cleanup runs.
                _subscriptionManager.ClearAll(_connectionManager.Connection);
                return ValueTask.CompletedTask;
            });

        try
        {
            await _connectionManager.ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false);
            await FullRescanAsync(cancellationToken).ConfigureAwait(false);
            return lifetime;
        }
        catch
        {
            await lifetime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task RunListenLoopsAsync(CancellationToken stoppingToken)
    {
        // Use a linked CTS so that if either loop faults, the other is torn down.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var rescanTask = RunRescanLoopAsync(linkedCts.Token);
        var pollingTask = RunPollingLoopAsync(linkedCts.Token);

        var firstCompleted = await Task.WhenAny(rescanTask, pollingTask).ConfigureAwait(false);
        if (firstCompleted.IsFaulted)
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(rescanTask, pollingTask).ConfigureAwait(false);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            // Reported here rather than left on the task. Nothing awaits it until disposal, so
            // without this both loops are gone, reads and rescans are dead, and the source keeps
            // reporting healthy while writes still work.
            Metrics.ReportError(exception);
            _logger.LogError(exception, "ADS listen loops stopped. Reads and rescans are no longer running.");
            throw;
        }
    }

    /// <inheritdoc />
    public override async Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        var connection = _connectionManager.Connection;
        if (connection is null)
        {
            return null;
        }

        var properties = new List<(RegisteredSubjectProperty Property, ISymbol Symbol, string SymbolPath)>();
        var symbols = new List<ISymbol>();

        foreach (var propertyReference in _ownership.Properties)
        {
            var registeredProperty = propertyReference.TryGetRegisteredProperty();
            if (registeredProperty is null)
            {
                continue;
            }

            var symbolPath = _subscriptionManager.GetSymbolPath(propertyReference);
            if (symbolPath is null)
            {
                continue;
            }

            var symbol = AdsSubscriptionManager.TryGetSymbol(_connectionManager.SymbolLoader, symbolPath);
            if (symbol is not null)
            {
                properties.Add((registeredProperty, symbol, symbolPath));
                symbols.Add(symbol);
            }
        }

        if (properties.Count == 0)
        {
            return null;
        }

        // Try batch read via SumSymbolRead first, fall back to individual reads
        var values = new (RegisteredSubjectProperty Property, object? Value)[properties.Count];

        try
        {
            var sumRead = new SumSymbolRead(connection, symbols);
            var readResult = await sumRead.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (readResult is { ErrorCode: AdsErrorCode.NoError, Values: not null })
            {
                // Cache array references to avoid repeated property access in the loop
                var resultValues = readResult.Values;
                var subErrors = readResult.SubErrors;
                for (var index = 0; index < properties.Count && index < resultValues.Length; index++)
                {
                    if (subErrors is not null && index < subErrors.Length && subErrors[index] != AdsErrorCode.NoError)
                    {
                        continue;
                    }

                    values[index] = (properties[index].Property,
                        _configuration.ValueConverter.ConvertToPropertyValue(resultValues[index], properties[index].Property));
                }

                _logger.LogInformation("Successfully batch-read {Count} ADS symbols from PLC.", properties.Count);
            }
            else
            {
                _logger.LogDebug("SumSymbolRead not supported (Error: {ErrorCode}), falling back to individual reads.", readResult.ErrorCode);
                await ReadIndividualValuesAsync(connection, properties, values, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "SumSymbolRead failed, falling back to individual reads.");
            await ReadIndividualValuesAsync(connection, properties, values, cancellationToken).ConfigureAwait(false);
        }

        return () =>
        {
            var now = DateTimeOffset.UtcNow;
            var applied = 0;
            var failed = 0;

            foreach (var (property, value) in values)
            {
                if (property is null)
                {
                    continue;
                }

                try
                {
                    property.SetValueFromSource(this, now, now, value);
                    applied++;
                }
                catch (Exception exception)
                {
                    // Guarded per property: this action runs unprotected inside the property
                    // writer, so one value the PLC cannot supply in the property's type would
                    // abort the whole apply, fail the listen attempt, and take the source with
                    // it. No property would hold a value and no write would ever reach the PLC.
                    failed++;

                    // Reported to the metrics as well as logged: without it the source keeps
                    // reporting healthy while the property holds its default value indefinitely.
                    Metrics.ReportError(exception);
                    _logger.LogError(exception,
                        "Failed to apply the PLC value to property '{Property}'.", property.Name);
                }
            }

            if (failed > 0)
            {
                _logger.LogWarning(
                    "Applied {AppliedCount} of {TotalCount} PLC values; {FailedCount} could not be assigned.",
                    applied, properties.Count, failed);
            }
            else
            {
                _logger.LogInformation("Updated {Count} properties with PLC values.", applied);
            }
        };
    }

    private async Task ReadIndividualValuesAsync(
        IAdsConnection connection,
        List<(RegisteredSubjectProperty Property, ISymbol Symbol, string SymbolPath)> properties,
        (RegisteredSubjectProperty Property, object? Value)[] values,
        CancellationToken cancellationToken)
    {
        // Same shape as the polling fallback: sum commands cannot resolve these symbols, so the
        // round trip count is fixed and only the number in flight is left to choose. Sequentially
        // this pass is the startup stall, one round trip per symbol before the model is usable.
        var successCount = 0;

        async Task ReadIntoAsync(int index)
        {
            try
            {
                // Same method the polling passes use, so a symbol whose PLC type will not resolve
                // is present in the model from startup rather than only after the first poll.
                var value = await _subscriptionManager.ReadSymbolValueAsync(
                    connection, properties[index].Symbol, properties[index].SymbolPath, cancellationToken)
                    .ConfigureAwait(false);

                if (value is not null)
                {
                    values[index] = (properties[index].Property,
                        _configuration.ValueConverter.ConvertToPropertyValue(value, properties[index].Property));
                    Interlocked.Increment(ref successCount);
                }
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Failed to read symbol '{SymbolPath}'.", properties[index].SymbolPath);
            }
        }

        var maxConcurrentReads = _configuration.MaxConcurrentReads;
        if (maxConcurrentReads > 0)
        {
            await Parallel.ForAsync(0, properties.Count,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxConcurrentReads,
                    CancellationToken = cancellationToken,
                },
                async (index, _) => await ReadIntoAsync(index).ConfigureAwait(false)).ConfigureAwait(false);
        }
        else
        {
            var reads = new Task[properties.Count];
            for (var index = 0; index < properties.Count; index++)
            {
                reads[index] = ReadIntoAsync(index);
            }

            await Task.WhenAll(reads).ConfigureAwait(false);
        }

        _logger.LogInformation("Read {SuccessCount}/{TotalCount} ADS symbols individually from PLC.", successCount, properties.Count);
    }

    /// <inheritdoc />
    public override async ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        var connection = _connectionManager.Connection;
        if (connection is null)
        {
            return WriteResult.Failure(changes, new InvalidOperationException("ADS connection is not established."));
        }

        return await WriteChangesAsyncCore(connection, changes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Core write logic. Separated from <see cref="WriteChangesAsync"/> so that the
    /// connection-null guard lives in the public entry point while the processing
    /// logic can be called directly with a mock <see cref="IAdsConnection"/> in tests.
    /// </summary>
    internal async ValueTask<WriteResult> WriteChangesAsyncCore(
        IAdsConnection connection,
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        try
        {
            // Materialized rather than iterated as a span: the unresolvable-type path below awaits,
            // and a span cannot be preserved across an await.
            var changesArray = changes.ToArray();
            var capacity = changesArray.Length;
            var symbols = new List<ISymbol>(capacity);
            var writeValues = new List<object>(capacity);
            var validChanges = new List<SubjectPropertyChange>(capacity);
            List<SubjectPropertyChange>? unresolvedChanges = null;
            List<SubjectPropertyChange>? permanentFailures = null;

            foreach (var change in changesArray)
            {
                var registeredProperty = change.Property.TryGetRegisteredProperty();
                if (registeredProperty is null)
                {
                    // Only properties this source claimed ownership of reach this method, and ownership
                    // is claimed through a registered property, so this is an anomaly rather than traffic
                    // for another source. Reported so it cannot be lost silently.
                    _logger.LogWarning("Property '{Property}' is no longer registered. Cannot write to ADS.",
                        change.Property.Name);
                    (permanentFailures ??= []).Add(change);
                    continue;
                }

                var symbolPath = _subscriptionManager.GetSymbolPath(change.Property);
                if (symbolPath is null)
                {
                    // Symbol path not cached — likely a rescan is in progress.
                    // Treat as transient so the retry queue picks it up.
                    (unresolvedChanges ??= []).Add(change);
                    continue;
                }

                var symbol = AdsSubscriptionManager.TryGetSymbol(_connectionManager.SymbolLoader, symbolPath);
                if (symbol is null)
                {
                    (unresolvedChanges ??= []).Add(change);
                    continue;
                }

                object? convertedValue;
                try
                {
                    convertedValue = _configuration.ValueConverter.ConvertToAdsValue(
                        change.GetNewValue<object?>(), registeredProperty);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Failed to convert value for ADS symbol '{SymbolPath}'.", symbolPath);
                    (permanentFailures ??= []).Add(change);
                    continue;
                }

                if (convertedValue is null)
                {
                    // ADS has no null: every symbol is a fixed-size value, so there is nothing to write.
                    // Deliberately not reported as a failure. Retrying could only produce the same
                    // outcome, and a later non-null assignment arrives as its own change.
                    _logger.LogDebug("Skipping write of null value to ADS symbol '{SymbolPath}'.", symbolPath);
                    continue;
                }

                // A symbol whose PLC type the TwinCAT type system cannot resolve (an enum) cannot
                // be marshalled by the typed value API. Write it through the any-type path, which
                // marshals from the .NET runtime type instead.
                if (!AdsSubscriptionManager.IsDataTypeResolvable(symbol))
                {
                    if (await WriteUnresolvedValueAsync(connection, symbolPath, convertedValue, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    // Reported rather than dropped: a caller that never learns a write failed
                    // cannot roll back or retry it.
                    (unresolvedChanges ??= []).Add(change);
                    continue;
                }

                symbols.Add(symbol);
                writeValues.Add(convertedValue);
                validChanges.Add(change);
            }

            if (symbols.Count == 0)
            {
                if (unresolvedChanges is not null)
                {
                    _logger.LogDebug("Deferring {Count} writes: symbol paths not available (rescan in progress?).", unresolvedChanges.Count);
                }

                return BuildWriteResult(null, null, unresolvedChanges, permanentFailures, validChangeCount: 0);
            }

            // Never SumSymbolWrite: it addresses by index group/offset, which a
            // {attribute 'monitoring' := 'call'} property does not have. It then writes raw bytes
            // past the variable and faults the PLC, without reporting an error. The per-symbol
            // value API calls the setter instead.
            return await WriteIndividualValuesAsync(
                symbols, writeValues, validChanges, unresolvedChanges, permanentFailures, cancellationToken).ConfigureAwait(false);
        }
        catch (AdsException exception)
        {
            var errorCode = AdsErrorClassifier.GetErrorCode(exception);
            var isTransient = AdsErrorClassifier.IsTransientException(exception);
            var error = new AdsWriteException(
                isTransient ? changes.Length : 0,
                isTransient ? 0 : changes.Length,
                changes.Length);

            if (!isTransient)
            {
                // Reported rather than dropped: FailedChanges has to stay complete or the retry queue
                // and the transaction writer both treat these as written. Classification only decides
                // the counts on the exception, as it does for OPC UA.
                _logger.LogWarning("Permanent ADS write error: {ErrorCode} on {Count} writes.",
                    errorCode, changes.Length);
            }

            return WriteResult.Failure(changes, error);
        }
        catch (Exception exception)
        {
            return WriteResult.Failure(changes, exception);
        }
    }

    /// <summary>
    /// Writes one value through the any-type path, for a symbol whose PLC data type will not
    /// resolve. Returns false when the write failed, so the caller can report it.
    /// </summary>
    private async ValueTask<bool> WriteUnresolvedValueAsync(
        IAdsConnection connection,
        string symbolPath,
        object value,
        CancellationToken cancellationToken)
    {
        try
        {
            var errorCode = connection.TryCreateVariableHandle(symbolPath, out var handle);
            if (errorCode != AdsErrorCode.NoError)
            {
                _logger.LogWarning(
                    "Failed to create ADS variable handle for '{SymbolPath}': {ErrorCode}.", symbolPath, errorCode);
                return false;
            }

            try
            {
                // As with WriteValueAsync, the error arrives on the result rather than as a throw.
                var writeResult = await connection.WriteAnyAsync(handle, value, cancellationToken).ConfigureAwait(false);
                if (writeResult.ErrorCode != AdsErrorCode.NoError)
                {
                    _logger.LogWarning(
                        "Failed to write ADS symbol '{SymbolPath}' through the any-type path: {ErrorCode}.",
                        symbolPath, writeResult.ErrorCode);
                    return false;
                }

                return true;
            }
            finally
            {
                var deleteResult = connection.TryDeleteVariableHandle(handle);
                if (deleteResult != AdsErrorCode.NoError)
                {
                    _logger.LogDebug(
                        "Failed to release the ADS handle for '{SymbolPath}': {ErrorCode}.", symbolPath, deleteResult);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Failed to write to ADS symbol '{SymbolPath}' through the any-type path.", symbolPath);
            return false;
        }
    }

    private async ValueTask<WriteResult> WriteIndividualValuesAsync(
        List<ISymbol> symbols,
        IReadOnlyList<object> writeValues,
        List<SubjectPropertyChange> validChanges,
        List<SubjectPropertyChange>? unresolvedChanges,
        List<SubjectPropertyChange>? permanentFailures,
        CancellationToken cancellationToken)
    {
        List<SubjectPropertyChange>? transientFailures = null;
        List<SubjectPropertyChange>? permanentWriteFailures = null;

        for (var index = 0; index < symbols.Count; index++)
        {
            try
            {
                // The result carries the error, it is not thrown: Beckhoff documents WriteValueAsync
                // as returning ResultWriteAccess with the ErrorCode on it. Discarding the result
                // reports a rejected write as a successful one.
                var writeResult = await ((IValueSymbol)symbols[index])
                    .WriteValueAsync(writeValues[index], cancellationToken).ConfigureAwait(false);

                var resultCode = (AdsErrorCode)writeResult.ErrorCode;
                if (resultCode != AdsErrorCode.NoError)
                {
                    Bucket(index, AdsErrorClassifier.IsTransientError(resultCode));
                }
            }
            catch (AdsException exception)
            {
                Bucket(index, AdsErrorClassifier.IsTransientException(exception));
            }
            catch (Exception)
            {
                Bucket(index, isTransient: false);
            }
        }

        return BuildWriteResult(
            transientFailures, permanentWriteFailures, unresolvedChanges, permanentFailures, validChanges.Count);

        void Bucket(int index, bool isTransient)
        {
            if (isTransient)
            {
                (transientFailures ??= []).Add(validChanges[index]);
            }
            else
            {
                (permanentWriteFailures ??= []).Add(validChanges[index]);
            }
        }
    }

    /// <summary>
    /// Builds a WriteResult from classified write failures. Every change that did not reach the PLC is
    /// reported, whether it failed transiently or permanently: <see cref="WriteResult.FailedChanges"/>
    /// has to stay complete or the retry queue and the transaction writer count it as written.
    /// Classification only decides the counts carried on <see cref="AdsWriteException"/>.
    /// </summary>
    /// <param name="transientFailures">Attempted writes that failed with a transient ADS error.</param>
    /// <param name="permanentWriteFailures">Attempted writes that failed with a permanent error.</param>
    /// <param name="unresolvedChanges">Changes never attempted because the symbol was unavailable, treated as transient.</param>
    /// <param name="permanentMappingFailures">Changes never attempted because they could not be mapped or converted.</param>
    /// <param name="validChangeCount">The number of changes actually attempted against the PLC.</param>
    private WriteResult BuildWriteResult(
        List<SubjectPropertyChange>? transientFailures,
        List<SubjectPropertyChange>? permanentWriteFailures,
        List<SubjectPropertyChange>? unresolvedChanges,
        List<SubjectPropertyChange>? permanentMappingFailures,
        int validChangeCount)
    {
        var retryChanges = MergeRetryChanges(
            transientFailures, permanentWriteFailures, unresolvedChanges, permanentMappingFailures);
        if (retryChanges is null)
        {
            return WriteResult.Success;
        }

        var transientCount = (transientFailures?.Count ?? 0) + (unresolvedChanges?.Count ?? 0);
        var permanentCount = (permanentWriteFailures?.Count ?? 0) + (permanentMappingFailures?.Count ?? 0);
        if (permanentCount > 0)
        {
            _logger.LogWarning("Reporting {Count} writes that failed permanently.", permanentCount);
        }

        var totalCount = validChangeCount
                         + (unresolvedChanges?.Count ?? 0)
                         + (permanentMappingFailures?.Count ?? 0);
        var error = new AdsWriteException(transientCount, permanentCount, totalCount);

        // Only attempted changes can have succeeded, so the unattempted lists stay out of this.
        var successCount = validChangeCount
                           - (transientFailures?.Count ?? 0)
                           - (permanentWriteFailures?.Count ?? 0);
        return successCount > 0
            ? WriteResult.PartialFailure(retryChanges, error)
            : WriteResult.Failure(retryChanges, error);
    }

    private static SubjectPropertyChange[]? MergeRetryChanges(
        params List<SubjectPropertyChange>?[] failureLists)
    {
        var totalCount = 0;
        foreach (var list in failureLists)
        {
            totalCount += list?.Count ?? 0;
        }

        if (totalCount == 0)
        {
            return null;
        }

        var result = new SubjectPropertyChange[totalCount];
        var offset = 0;
        foreach (var list in failureLists)
        {
            if (list is null)
            {
                continue;
            }

            list.CopyTo(result, offset);
            offset += list.Count;
        }

        return result;
    }

    /// <summary>
    /// Requests a debounced rescan. Multiple rapid calls are coalesced into a single rescan
    /// by the listen-time rescan loop after the configured debounce time elapses.
    /// </summary>
    internal void RequestRescan(string? reason = null)
    {
        if (reason is not null)
        {
            _logger.LogInformation("{Reason} Requesting rescan.", reason);
        }

        _propertyWriter?.StartBuffering();
        Interlocked.Exchange(ref _lastRescanRequestedAtTicks, DateTimeOffset.UtcNow.UtcTicks);

        // Reached from a TwinCAT event thread, which can still be dispatching an event raised before
        // disposal unhooked the handlers, so the semaphore may already be gone. SemaphoreFullException
        // means the loop is already signalled, which is equally harmless.
        try { _rescanSignal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Performs a full rescan: clears subscriptions, reloads symbols, re-registers.
    /// Returns true if the rescan was executed, false if skipped (e.g., no connection).
    /// Synchronized via <see cref="_rescanLock"/> to prevent concurrent execution
    /// from the SBBS thread (StartListeningAsync) and the TwinCAT ExecuteAsync thread.
    /// </summary>
    private async Task<bool> FullRescanAsync(CancellationToken cancellationToken)
    {
        await _rescanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = _connectionManager.Connection;
            if (connection is null)
            {
                _logger.LogDebug("Skipping rescan: ADS connection is not established.");
                return false;
            }

            _subscriptionManager.ClearAll(_connectionManager.Connection);
            await _connectionManager.RecreateSymbolLoaderAsync(cancellationToken).ConfigureAwait(false);

            // Load subject graph
            var graphMappings = _subjectLoader.LoadSubjectGraph(_subject);

            // Register subscriptions (determines read modes, registers notifications + polling)
            _subscriptionManager.RegisterSubscriptions(
                graphMappings,
                connection,
                _connectionManager.SymbolLoader,
                _ownership,
                _propertyWriter,
                this,
                _connectionManager);

            return true;
        }
        finally
        {
            _rescanLock.Release();
        }
    }

    private async Task RunRescanLoopAsync(CancellationToken stoppingToken)
    {
        // This loop handles debounced rescans and periodic health monitoring.
        // Event handlers (ConnectionRestored, AdsStateEnteredRun, SymbolVersionChanged)
        // signal _rescanSignal to wake the loop immediately. A debounce period ensures
        // that rapid successive events are coalesced into a single rescan.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for either a rescan signal or the health check interval
                await _rescanSignal.WaitAsync(_configuration.HealthCheckInterval, stoppingToken).ConfigureAwait(false);

                // Process pending rescan with debounce
                var requestedAtTicks = Interlocked.Read(ref _lastRescanRequestedAtTicks);
                if (requestedAtTicks > 0)
                {
                    await DebounceAndRescanAsync(requestedAtTicks, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _connectionManager.LogFirstOccurrence("Rescan", exception, "Rescan failed.");

                // Re-stamp the request so the debounce period acts as a retry backoff
                if (Interlocked.Read(ref _lastRescanRequestedAtTicks) > 0)
                {
                    Interlocked.Exchange(ref _lastRescanRequestedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
                }
            }
        }
    }

    private async Task RunPollingLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_configuration.PollingInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _subscriptionManager.PollValuesAsync(
                    _connectionManager, _propertyWriter, this, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _connectionManager.LogFirstOccurrence("BatchPoll", exception, "Batch polling failed.");
            }
        }
    }

    private async Task DebounceAndRescanAsync(long requestedAtTicks, CancellationToken stoppingToken)
    {
        // Wait until the debounce period has elapsed since the last request.
        // If new requests arrive during the wait, restart the debounce timer.
        while (!stoppingToken.IsCancellationRequested)
        {
            var requestedAt = new DateTimeOffset(requestedAtTicks, TimeSpan.Zero);
            var elapsed = DateTimeOffset.UtcNow - requestedAt;
            var remaining = _configuration.RescanDebounceTime - elapsed;

            if (remaining > TimeSpan.Zero)
            {
                await _rescanSignal.WaitAsync(remaining, stoppingToken).ConfigureAwait(false);

                // Check if a newer request arrived during the wait
                var newTicks = Interlocked.Read(ref _lastRescanRequestedAtTicks);
                if (newTicks > requestedAtTicks)
                {
                    requestedAtTicks = newTicks;
                    continue; // Restart debounce with the new timestamp
                }
            }

            break;
        }

        // Execute the rescan. Only clear the request after success so that
        // a transient failure (or missing connection) causes a retry on the next loop iteration.
        _logger.LogInformation("Executing debounced rescan.");
        if (await FullRescanAsync(stoppingToken).ConfigureAwait(false))
        {
            await (_propertyWriter?.LoadInitialStateAndResumeAsync(stoppingToken)
                ?? Task.CompletedTask).ConfigureAwait(false);

            // Compared, not overwritten: a request that arrived while this pass was running carries
            // a newer stamp, and zeroing it unconditionally drops that request on the floor.
            Interlocked.CompareExchange(ref _lastRescanRequestedAtTicks, 0, requestedAtTicks);
        }
    }

    /// <inheritdoc cref="SubjectSourceBase.Diagnostics" />
    public override AdsClientDiagnostics Diagnostics { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Cancel the base BackgroundService stopping token. The listen lifetime
        // (BackgroundTaskLifetime owning the rescan/polling loops) is awaited by
        // SubjectSourceBase.ExecuteAsync, so by the time StopAsync returns the
        // loops have already torn down.
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "StopAsync faulted during disposal.");
        }

        Dispose();

        _rescanLock.Dispose();
        await _subscriptionManager.DisposeAsync().ConfigureAwait(false);
        await _connectionManager.DisposeAsync().ConfigureAwait(false);
        _ownership.Dispose();
        _rescanSignal.Dispose();
    }
}
