using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Ads.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using TwinCAT;
using System.Text;
using TwinCAT.Ads;
using TwinCAT.Ads.SumCommand;
using TwinCAT.TypeSystem;

namespace Namotion.Interceptor.Ads.Client;

/// <summary>
/// Manages ADS notification subscriptions, batch polling, symbol-property caches,
/// read mode demotion, and value processing.
/// </summary>
internal sealed class AdsSubscriptionManager : IAsyncDisposable
{
    private readonly AdsClientConfiguration _configuration;
    private readonly ILogger _logger;

    // Caches keyed by PropertyReference (stable) not RegisteredSubjectProperty (can become stale)
    private readonly ConcurrentDictionary<PropertyReference, string> _propertyToSymbol
        = new(PropertyReference.Comparer);
    /// <summary>Properties served by a notification, mapped to their symbol path. The handles
    /// themselves live in <see cref="_notificationHandles"/>, which is what releases them.</summary>
    private readonly ConcurrentDictionary<PropertyReference, string> _notificationProperties
        = new(PropertyReference.Comparer);
    private readonly ConcurrentDictionary<PropertyReference, string> _polledProperties
        = new(PropertyReference.Comparer);
    /// <summary>Bumped on every change to <see cref="_polledProperties"/>. Compared against
    /// <see cref="_pollingSnapshotVersion"/> rather than carried as a boolean, so a registration that
    /// lands while a rebuild is running is not erased by that rebuild clearing the flag afterwards.</summary>
    private int _pollingCollectionVersion;
    private int _pollingSnapshotVersion;

    /// <summary>Symbol paths whose PLC type will not resolve (enums), learned on first failed read.
    /// Keyed by path so a rescan keeps what was learned. Concurrent: polling reads in parallel.</summary>
    private readonly ConcurrentDictionary<string, byte> _rawIntegerSymbols = new();

    /// <summary>Handles for <see cref="_rawIntegerSymbols"/>, so polling does not create one per
    /// cycle. Dropped on a failed read to replace handles invalidated by a reconnect.</summary>
    private readonly ConcurrentDictionary<string, uint> _rawIntegerHandles = new();

    /// <summary>What the individual-read poll passes record, surfaced on the client diagnostics.</summary>
    internal AdsPollingMetrics PollingMetrics { get; } = new();

    // Polling snapshot — swapped atomically via volatile reference to avoid torn reads.
    // Only the polling thread mutates UseFallback; all other fields are set once during construction.
    private volatile PollingSnapshot _pollingSnapshot = PollingSnapshot.Empty;

    private sealed class PollingSnapshot
    {
        public static readonly PollingSnapshot Empty = new([], [], null);

        public readonly List<ISymbol> Symbols;
        public readonly List<(PropertyReference Reference, string SymbolPath)> Entries;
        public readonly SumSymbolRead? SumRead;
        /// <summary>Latched only for a failure the sum command can never recover from, so a
        /// transient one degrades a single pass rather than every pass for the snapshot's life.
        /// Only mutated by the polling thread.</summary>
        public volatile bool UseFallback;

        public PollingSnapshot(List<ISymbol> symbols, List<(PropertyReference, string)> entries, SumSymbolRead? sumRead)
        {
            Symbols = symbols;
            Entries = entries;
            SumRead = sumRead;
        }
    }

    /// <summary>
    /// Gets whether the polling collection has been marked dirty (for testing).
    /// </summary>
    internal bool IsPollingCollectionDirty =>
        Volatile.Read(ref _pollingCollectionVersion) != Volatile.Read(ref _pollingSnapshotVersion);

    private int _disposed; // 0 = false, 1 = true

    /// <summary>
    /// Initializes a new instance of the <see cref="AdsSubscriptionManager"/> class.
    /// </summary>
    public AdsSubscriptionManager(AdsClientConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Gets the number of properties with active notification subscriptions.
    /// </summary>
    internal int NotificationCount => _notificationProperties.Count;

    /// <summary>Device notification handles currently held. Exposed so a test can assert that a
    /// re-scan releases the previous registrations rather than stacking new ones on the PLC.</summary>
    internal int NotificationHandleCount => _notificationHandles.Count;

    /// <summary>Device notification handles this manager registered, mapped to the property that
    /// owns each. Held so the registrations can be released; nothing else frees them.</summary>
    private readonly ConcurrentDictionary<uint, (PropertyReference Reference, string SymbolPath)> _notificationHandles = new();

    // Captured for the notification handler, which the ADS client raises with no context of its own.
    // Reassigned on every listen attempt along with the registrations they serve.
    private SubjectPropertyWriter? _propertyWriter;
    private ISubjectSource? _source;
    private AdsConnectionManager? _connectionManager;
    private IAdsConnection? _notificationConnection;

    /// <summary>
    /// Gets the number of properties using batch polling.
    /// </summary>
    internal int PolledCount => _polledProperties.Count;

    /// <summary>
    /// Registers notification subscriptions and batch polling for the given property-symbol mappings.
    /// </summary>
    internal void RegisterSubscriptions(
        IReadOnlyList<(RegisteredSubjectProperty Property, string SymbolPath, AdsPropertyMapping Mapping)> mappings,
        IAdsConnection connection,
        ISymbolLoader? symbolLoader,
        SourceOwnershipManager ownership,
        SubjectPropertyWriter? propertyWriter,
        ISubjectSource source,
        AdsConnectionManager connectionManager)
    {
        var effectiveModes = DetermineEffectiveReadModes(
            mappings,
            _configuration.DefaultReadMode,
            _configuration.DefaultCycleTime,
            _configuration.MaxNotifications);

        // Captured before registering, because the handler can fire as soon as the first handle exists.
        _propertyWriter = propertyWriter;
        _source = source;
        _connectionManager = connectionManager;

        if (!ReferenceEquals(_notificationConnection, connection))
        {
            if (_notificationConnection is not null)
            {
                _notificationConnection.AdsNotificationEx -= OnAdsNotificationEx;
            }

            connection.AdsNotificationEx += OnAdsNotificationEx;
            _notificationConnection = connection;
        }

        for (var index = 0; index < effectiveModes.Count; index++)
        {
            var (property, symbolPath, effectiveMode) = effectiveModes[index];
            if (!ownership.ClaimSource(property.Reference))
            {
                continue;
            }

            // Register in bidirectional symbol-to-property lookups
            _propertyToSymbol[property.Reference] = symbolPath;

            if (effectiveMode == AdsReadMode.Notification)
            {
                var symbol = TryGetSymbol(symbolLoader, symbolPath);
                if (symbol is null)
                {
                    // Polling cannot read it either, so registering it as polled would only hide it.
                    connectionManager.LogFirstOccurrence("SymbolNotFound", null,
                        "Symbol '{SymbolPath}' not found in PLC. Skipping.", symbolPath);
                    continue;
                }

                var mapping = mappings[index].Mapping;
                if (!TryRegisterNotification(property, symbolPath, symbol, mapping, connection, connectionManager))
                {
                    // Keeps updating rather than silently freezing at its last value.
                    _polledProperties[property.Reference] = symbolPath;
                }
            }
            else
            {
                _polledProperties[property.Reference] = symbolPath;
            }
        }

        // Mark dirty so the next PollValuesAsync call rebuilds the polling snapshot
        Interlocked.Increment(ref _pollingCollectionVersion);

        _logger.LogInformation(
            "Registered {NotificationCount} notification and {PolledCount} polled variables.",
            _notificationProperties.Count, _polledProperties.Count);
    }

    /// <summary>
    /// Clears all caches, disposes subscriptions, and marks polling as dirty.
    /// </summary>
    internal void ClearAll(IAdsConnection? connection = null)
    {
        // Before the paths are forgotten: a rescan replaces the symbol set, and a handle the PLC
        // still holds for a symbol nothing reads again is leaked for the life of the connection.
        ReleaseRawIntegerHandles(connection);

        // Likewise: nothing else deletes a device notification, so a re-scan that only forgets the
        // handles leaves the previous registrations standing on the controller.
        ReleaseNotifications(connection ?? _notificationConnection);

        if (_notificationConnection is not null)
        {
            _notificationConnection.AdsNotificationEx -= OnAdsNotificationEx;
            _notificationConnection = null;
        }

        // Mark polling dirty and clear snapshot so in-flight polls stop immediately
        Interlocked.Increment(ref _pollingCollectionVersion);
        _pollingSnapshot = PollingSnapshot.Empty;

        _propertyToSymbol.Clear();
        _notificationProperties.Clear();
        _polledProperties.Clear();
    }

    /// <summary>
    /// Gets the ADS symbol path for a property reference, or null if not cached.
    /// </summary>
    internal string? GetSymbolPath(PropertyReference propertyReference)
    {
        return _propertyToSymbol.GetValueOrDefault(propertyReference);
    }

    /// <summary>
    /// Adds a symbol-path mapping to the cache.
    /// </summary>
    internal void SetSymbolPath(PropertyReference propertyReference, string symbolPath)
    {
        _propertyToSymbol[propertyReference] = symbolPath;
    }

    /// <summary>
    /// Tries to get an ADS symbol by path from the given symbol loader.
    /// </summary>
    internal static ISymbol? TryGetSymbol(ISymbolLoader? symbolLoader, string symbolPath)
    {
        if (symbolLoader is null)
        {
            return null;
        }

        try
        {
            if (symbolLoader.Symbols.TryGetInstance(symbolPath, out var symbol))
            {
                return symbol;
            }
        }
        catch (Exception)
        {
            // Symbol not found or loader error
        }

        return null;
    }

    /// <summary>
    /// Cleanup callback for when a property is being released from ownership.
    /// </summary>
    internal void OnPropertyReleasing(PropertyReference property)
    {
        // 1. Release this property's device notification. Nothing else deletes it, so leaving it
        // registered keeps the PLC delivering a value nothing consumes until the next re-scan.
        ReleaseNotificationFor(property);

        // 2. Remove from batch polling collection
        if (_polledProperties.TryRemove(property, out _))
        {
            Interlocked.Increment(ref _pollingCollectionVersion);
        }

        // 3. Remove from the symbol path lookup
        _propertyToSymbol.TryRemove(property, out _);
    }

    /// <summary>
    /// Determines the effective read mode for each property, applying the two-pass auto-demotion algorithm.
    /// Notification mode properties are never demoted. Auto mode properties are demoted to polling
    /// when the MaxNotifications limit is exceeded, with higher Priority values demoted first,
    /// then higher CycleTime as tiebreaker.
    /// </summary>
    /// <remarks>
    /// The result is index-aligned with <paramref name="mappings"/>: one entry is appended per input
    /// in order, and the demotion pass only rewrites entries in place. Callers rely on that to
    /// recover each property's mapping without a lookup.
    /// </remarks>
    internal static IReadOnlyList<(RegisteredSubjectProperty Property, string SymbolPath, AdsReadMode EffectiveMode)>
        DetermineEffectiveReadModes(
            IReadOnlyList<(RegisteredSubjectProperty Property, string SymbolPath, AdsPropertyMapping Mapping)> mappings,
            AdsReadMode defaultReadMode,
            int defaultCycleTime,
            int maxNotifications)
    {
        var result = new List<(RegisteredSubjectProperty, string, AdsReadMode)>(mappings.Count);

        // Pass 1: Collect all properties with their configured read modes
        var notificationCount = 0;
        var autoModeEntries = new List<(int Index, int Priority, int CycleTime)>();

        for (var index = 0; index < mappings.Count; index++)
        {
            var (property, symbolPath, mapping) = mappings[index];
            var readMode = mapping.ReadMode ?? defaultReadMode;
            var cycleTime = mapping.CycleTime ?? defaultCycleTime;
            var priority = mapping.Priority ?? 0;

            if (readMode == AdsReadMode.Notification)
            {
                // Protected - always notification
                result.Add((property, symbolPath, AdsReadMode.Notification));
                notificationCount++;
            }
            else if (readMode == AdsReadMode.Polled)
            {
                // Always polled
                result.Add((property, symbolPath, AdsReadMode.Polled));
            }
            else
            {
                // Auto - starts as notification, may be demoted
                result.Add((property, symbolPath, AdsReadMode.Notification));
                autoModeEntries.Add((index, priority, cycleTime));
                notificationCount++;
            }
        }

        // Pass 2: Demote Auto properties if over the limit
        if (notificationCount > maxNotifications)
        {
            var excessCount = notificationCount - maxNotifications;

            // Sort by Priority descending (higher demoted first), then CycleTime descending (slower demoted first)
            var sortedAutoEntries = autoModeEntries
                .OrderByDescending(entry => entry.Priority)
                .ThenByDescending(entry => entry.CycleTime)
                .ToList();

            var demotionCount = Math.Min(excessCount, sortedAutoEntries.Count);
            for (var index = 0; index < demotionCount; index++)
            {
                var entryIndex = sortedAutoEntries[index].Index;
                var original = result[entryIndex];
                result[entryIndex] = (original.Item1, original.Item2, AdsReadMode.Polled);
            }
        }

        return result;
    }

    /// <summary>
    /// Registers one device notification and records its handle. Returns false when the value cannot
    /// be marshalled or the PLC refuses it, so the caller can fall the property back to polling.
    /// </summary>
    /// <remarks>
    /// Registered through the raw ADS API rather than the reactive extension, for two reasons. That
    /// extension allocates a dedicated <c>EventLoopScheduler</c>, and therefore an OS thread, per
    /// call, and disposing a batched subscription does not send <c>DeleteDeviceNotification</c>, so
    /// every re-scan would leave the previous registrations standing on the controller. Holding the
    /// handle makes the release explicit, and a single
    /// <see cref="IAdsNotifications.AdsNotificationEx"/> handler serves every symbol.
    /// </remarks>
    private bool TryRegisterNotification(
        RegisteredSubjectProperty property,
        string symbolPath,
        ISymbol symbol,
        AdsPropertyMapping mapping,
        IAdsConnection connection,
        AdsConnectionManager connectionManager)
    {
        if (!TryResolveNotificationType(property.Type, symbol, out var marshalType, out var marshalArgs))
        {
            connectionManager.LogFirstOccurrence("NotificationMarshalling", null,
                "Cannot marshal '{PropertyType}' for symbol '{SymbolPath}' as an ADS notification. Polling it instead.",
                property.Type.Name, symbolPath);
            return false;
        }

        var notificationSettings = new NotificationSettings(
            AdsTransMode.OnChange,
            mapping.CycleTime ?? _configuration.DefaultCycleTime,
            mapping.MaxDelay ?? _configuration.DefaultMaxDelay);

        try
        {
            var errorCode = connection.TryAddDeviceNotificationEx(
                symbolPath, notificationSettings, null, marshalType, marshalArgs, out var handle);

            if (errorCode != AdsErrorCode.NoError)
            {
                connectionManager.LogFirstOccurrence("NotificationRegistration", null,
                    "The PLC refused a notification for '{SymbolPath}': {ErrorCode}. Polling it instead.",
                    symbolPath, errorCode);
                return false;
            }

            _notificationHandles[handle] = (property.Reference, symbolPath);
            _notificationProperties[property.Reference] = symbolPath;
            return true;
        }
        catch (Exception exception)
        {
            connectionManager.LogFirstOccurrence("NotificationRegistration", exception,
                "Failed to register a notification for '{SymbolPath}'. Polling it instead.", symbolPath);
            return false;
        }
    }

    /// <summary>
    /// Resolves the type and dimensions the ADS any-type marshaller needs for a property, and
    /// confirms they describe exactly what the PLC holds.
    /// </summary>
    /// <remarks>
    /// The property's own type is not usable as-is: an enum and a nullable are rejected by the
    /// marshaller, and a string or an array needs its length supplied separately. Those lengths come
    /// from the symbol's PLC data type rather than from its byte size, because a size comparison
    /// cannot validate them. <c>MarshalSize(string, [n])</c> is <c>n + 1</c> by definition, so
    /// deriving <c>n</c> from the byte size and then comparing the two is a tautology that accepts
    /// any symbol, including a two-byte-per-character WSTRING. The same holds for a
    /// <see cref="byte"/> array, whose element size of one makes every width divide evenly.
    /// </remarks>
    internal static bool TryResolveNotificationType(
        Type propertyType, ISymbol symbol, out Type marshalType, out int[]? marshalArgs)
    {
        marshalArgs = null;
        var isNullable = Nullable.GetUnderlyingType(propertyType) is not null;
        marshalType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (marshalType.IsEnum)
        {
            // The value arrives as the underlying integer. Unboxing it into an enum works, but
            // unboxing it into a nullable enum throws, and the property writer would swallow that
            // on every delivery, so this one stays on polling.
            if (isNullable)
            {
                return false;
            }

            marshalType = Enum.GetUnderlyingType(marshalType);
        }

        if (symbol is not IBitSize { ByteSize: > 0 } bitSize)
        {
            return false;
        }

        var byteSize = bitSize.ByteSize;
        var marshaler = new AnyTypeMarshaler();

        // Resolved, because a PLC alias such as `TYPE T_MaxString : STRING(255)` presents as an
        // AliasType that implements neither IStringType nor IArrayType. Matching on the unresolved
        // type would drop every aliased string and array to polling. A no-op for everything else.
        var dataType = symbol.DataType?.Resolve();

        try
        {
            if (marshalType == typeof(string))
            {
                // Single-byte strings only. A WSTRING holds two bytes per character, and the
                // any-type path marshals with the target's single-byte value encoding, so it would
                // decode the payload as one character and stop at the first NUL.
                if (dataType is not IStringType stringType ||
                    stringType.Length <= 0 ||
                    stringType.ByteSize != stringType.Length + 1)
                {
                    return false;
                }

                marshalArgs = [stringType.Length];
            }
            else if (marshalType.IsArray)
            {
                // Validated against the PLC array type, not against a byte count that divides evenly.
                if (dataType is not IArrayType { IsJagged: false } arrayType ||
                    arrayType.Dimensions.Count != 1)
                {
                    return false;
                }

                var elementType = marshalType.GetElementType();
                if (elementType is null || !marshaler.CanMarshal(elementType, null, Encoding.UTF8))
                {
                    return false;
                }

                var elementSize = marshaler.MarshalSize(elementType, null, Encoding.UTF8);
                if (elementSize <= 0 || arrayType.ElementType is null || elementSize != arrayType.ElementType.ByteSize)
                {
                    return false;
                }

                marshalArgs = [arrayType.Dimensions.ElementCount];
            }

            return marshaler.CanMarshal(marshalType, marshalArgs, Encoding.UTF8)
                   && marshaler.MarshalSize(marshalType, marshalArgs, Encoding.UTF8) == byteSize;
        }
        catch (Exception)
        {
            // The marshaller reports an unusable type by throwing as readily as by returning false.
            return false;
        }
    }

    /// <summary>
    /// Routes one device notification to the property that registered its handle. Raised on the ADS
    /// client's receive thread, for every registered symbol.
    /// </summary>
    private void OnAdsNotificationEx(object? sender, AdsNotificationExEventArgs args)
    {
        var source = _source;
        if (source is null || !_notificationHandles.TryGetValue(args.Handle, out var route))
        {
            // A notification already in flight when its handle was released, or after teardown.
            return;
        }

        try
        {
            OnValueReceived(route.Reference, args.Value, args.TimeStamp, _propertyWriter, source);
        }
        catch (Exception exception)
        {
            _connectionManager?.LogFirstOccurrence("NotificationCallback", exception,
                "Failed to process notification for symbol '{SymbolPath}'.", route.SymbolPath);
        }
    }

    /// <summary>
    /// Releases the device notification held for one property, if it has one.
    /// </summary>
    private void ReleaseNotificationFor(PropertyReference property)
    {
        if (!_notificationProperties.TryRemove(property, out _))
        {
            return;
        }

        foreach (var (handle, route) in _notificationHandles)
        {
            if (!route.Reference.Equals(property) || !_notificationHandles.TryRemove(handle, out _))
            {
                continue;
            }

            try
            {
                _notificationConnection?.TryDeleteDeviceNotification(handle);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception,
                    "Failed to release the notification for '{SymbolPath}'.", route.SymbolPath);
            }

            return;
        }
    }

    /// <summary>
    /// Releases every device notification this manager registered. Without it a re-scan leaves the
    /// previous registrations standing on the controller.
    /// </summary>
    private void ReleaseNotifications(IAdsConnection? connection)
    {
        foreach (var handle in _notificationHandles.Keys)
        {
            if (!_notificationHandles.TryRemove(handle, out var route) || connection is null)
            {
                continue;
            }

            try
            {
                var errorCode = connection.TryDeleteDeviceNotification(handle);
                if (errorCode != AdsErrorCode.NoError)
                {
                    _logger.LogDebug(
                        "Failed to release the notification for '{SymbolPath}': {ErrorCode}.",
                        route.SymbolPath, errorCode);
                }
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception,
                    "Failed to release the notification for '{SymbolPath}'.", route.SymbolPath);
            }
        }
    }

    /// <summary>
    /// Performs a single polling cycle: reads all polled properties via batch or individual reads.
    /// Called periodically by the orchestrator's PeriodicTimer loop.
    /// </summary>
    internal async Task PollValuesAsync(
        AdsConnectionManager connectionManager,
        SubjectPropertyWriter? propertyWriter,
        ISubjectSource source,
        CancellationToken cancellationToken)
    {
        if (connectionManager.Connection is null || _polledProperties.IsEmpty)
        {
            return;
        }

        if (IsPollingCollectionDirty)
        {
            RebuildPollingSnapshot(connectionManager);
        }

        // Read snapshot reference once — safe even if another thread swaps it via ClearAll/rebuild.
        var snapshot = _pollingSnapshot;
        if (snapshot.SumRead is null || snapshot.Symbols.Count == 0)
        {
            return;
        }

        if (!snapshot.UseFallback)
        {
            try
            {
                var readResult = await snapshot.SumRead.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (readResult.ErrorCode != AdsErrorCode.NoError || readResult.Values is null)
                {
                    // Any failure falls back for this pass, as the initial state load already does.
                    // Only a capability failure latches: DeviceBusy or a timeout says nothing about
                    // whether the next sum read works, and latching on one would drop the whole
                    // snapshot to per-symbol round trips permanently.
                    connectionManager.LogFirstOccurrence("BatchPoll", null,
                        "Batch polling failed with {ErrorCode}, falling back to individual reads.", readResult.ErrorCode);
                    snapshot.UseFallback = readResult.ErrorCode == AdsErrorCode.DeviceServiceNotSupported;
                }
                else
                {
                    var resultValues = readResult.Values;
                    var subErrors = readResult.SubErrors;
                    for (var index = 0; index < snapshot.Entries.Count && index < resultValues.Length; index++)
                    {
                        if (subErrors is not null && index < subErrors.Length && subErrors[index] != AdsErrorCode.NoError)
                        {
                            continue;
                        }

                        OnValueReceived(snapshot.Entries[index].Reference, resultValues[index], null, propertyWriter, source);
                    }

                    return;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Includes the value-building throw for a symbol whose PLC type will not resolve,
                // which is not an AdsException at all and would otherwise escape the poll loop.
                // That one is a property of the symbol set, so it latches; a transient ADS error
                // thrown from the same call does not.
                connectionManager.LogFirstOccurrence("BatchPoll", exception,
                    "Batch polling threw, falling back to individual reads.");
                snapshot.UseFallback = !AdsErrorClassifier.IsTransientException(exception);
            }
        }

        // Individual read fallback. Sum commands cannot resolve these symbols, so the round trip
        // count is fixed and the only lever is how many are in flight. The work is IO, not CPU, so
        // every read is issued at once unless MaxConcurrentReads bounds it. Values are applied
        // afterwards on this thread: the property writer's threading contract is not ours to assume.
        var passStarted = Stopwatch.GetTimestamp();
        var readValues = new object?[snapshot.Symbols.Count];
        var failures = 0;

        async Task ReadIntoAsync(int index)
        {
            try
            {
                readValues[index] = await ReadSymbolValueAsync(
                    connectionManager.Connection!, snapshot.Symbols[index],
                    snapshot.Entries[index].SymbolPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref failures);
                connectionManager.LogFirstOccurrence("BatchPoll", exception,
                    "Failed to read polled symbol '{SymbolPath}'.", snapshot.Entries[index].SymbolPath);
            }
        }

        var maxConcurrentReads = _configuration.MaxConcurrentReads;
        if (maxConcurrentReads > 0)
        {
            await Parallel.ForAsync(0, snapshot.Symbols.Count,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxConcurrentReads,
                    CancellationToken = cancellationToken,
                },
                async (index, _) => await ReadIntoAsync(index).ConfigureAwait(false)).ConfigureAwait(false);
        }
        else
        {
            var reads = new Task[snapshot.Symbols.Count];
            for (var index = 0; index < snapshot.Symbols.Count; index++)
            {
                reads[index] = ReadIntoAsync(index);
            }

            await Task.WhenAll(reads).ConfigureAwait(false);
        }

        for (var index = 0; index < readValues.Length; index++)
        {
            if (readValues[index] is not null)
            {
                OnValueReceived(snapshot.Entries[index].Reference, readValues[index], null, propertyWriter, source);
            }
        }

        var elapsed = Stopwatch.GetElapsedTime(passStarted).TotalMilliseconds;
        PollingMetrics.RecordPass(snapshot.Symbols.Count, elapsed, failures);
        _logger.LogDebug("Poll pass: {Count} symbols in {Elapsed:N0} ms, {Failures} failed.",
            snapshot.Symbols.Count, elapsed, failures);
    }

    /// <summary>
    /// Returns false when the symbol's PLC data type cannot be resolved by the TwinCAT type system
    /// (e.g. an enum whose definition was not loaded). Used to choose the raw integer read path.
    /// </summary>
    internal static bool IsDataTypeResolvable(ISymbol symbol)
    {
        try
        {
            return symbol.DataType is not null;
        }
        catch (CannotResolveDataTypeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads one polled symbol, typed where the type system allows it and as a raw integer where it
    /// does not. Returns null when nothing should be published. Safe to call concurrently.
    /// </summary>
    /// <summary>
    /// Reads one symbol, typed where the type system allows it and as a raw integer where it does
    /// not. Shared by the initial state load and by every polling pass, so a symbol whose PLC type
    /// will not resolve behaves the same at startup as it does later. Returns null when nothing
    /// should be published. Safe to call concurrently.
    /// </summary>
    internal async Task<object?> ReadSymbolValueAsync(
        IAdsConnection connection,
        ISymbol symbol,
        string symbolPath,
        CancellationToken cancellationToken)
    {
        // An enum throws while the value is built, not while its DataType is inspected, so it can
        // only be recognised by trying. Remembered on first failure so the doomed typed read
        // happens once, not every cycle.
        if (!_rawIntegerSymbols.ContainsKey(symbolPath))
        {
            try
            {
                var readResult = await ((IValueSymbol)symbol)
                    .ReadValueAsync(cancellationToken).ConfigureAwait(false);

                var readErrorCode = (AdsErrorCode)readResult.ErrorCode;
                if (readErrorCode != AdsErrorCode.NoError)
                {
                    // Surfaced rather than returned as null: a null is indistinguishable from
                    // "nothing to publish", so the caller would count a wholly failing poll pass
                    // as a clean one and report zero failed reads.
                    throw new AdsErrorException(
                        $"Failed to read ADS symbol '{symbolPath}'.", readErrorCode);
                }

                return readResult.Value;
            }
            catch (CannotResolveDataTypeException)
            {
                if (_rawIntegerSymbols.TryAdd(symbolPath, 0))
                {
                    _logger.LogDebug(
                        "Symbol '{SymbolPath}' has an unresolvable PLC type. Polling it as a raw integer.",
                        symbolPath);
                }
            }
        }

        // Underlying integer; the converter rebuilds the enum. Polling and the initial state load
        // only: a notification carries a value the type system already had to build, so an
        // unresolvable type cannot be recovered here.
        return await ReadRawIntegerAsync(
            connection, symbolPath,
            ((IBitSize)symbol).ByteSize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a symbol whose PLC type will not resolve (an enum) as its underlying integer, picked
    /// by byte size. Null means the width has no integer counterpart. Signedness is irrelevant: the
    /// converter rebuilds the enum unchecked, so a UINT past 32767 such as Disabled still fits.
    /// </summary>
    private async Task<object?> ReadRawIntegerAsync(
        IAdsConnection connection,
        string symbolPath,
        int byteSize,
        CancellationToken cancellationToken)
    {
        // Not by instance path: that overload sizes its request buffer from T, so the path does not
        // fit. A handle carries it once, as the write side does.
        if (!_rawIntegerHandles.TryGetValue(symbolPath, out var handle))
        {
            var errorCode = connection.TryCreateVariableHandle(symbolPath, out handle);
            if (errorCode != AdsErrorCode.NoError)
            {
                throw new InvalidOperationException(
                    $"Failed to create ADS variable handle for '{symbolPath}': {errorCode}");
            }

            // Two properties on the same path can race here. A blind assign would orphan the loser's
            // handle in the PLC for the process lifetime, so keep whichever landed first.
            var stored = _rawIntegerHandles.GetOrAdd(symbolPath, handle);
            if (stored != handle)
            {
                var deleteResult = connection.TryDeleteVariableHandle(handle);
                if (deleteResult != AdsErrorCode.NoError)
                {
                    _logger.LogDebug(
                        "Failed to release the duplicate ADS handle for '{SymbolPath}': {ErrorCode}.",
                        symbolPath, deleteResult);
                }

                handle = stored;
            }
        }

        try
        {
            // ReadAnyAsync returns ResultValue<T>; unwrap it or the converter sees a non-integer
            // and passes it through untouched.
            var (value, errorCode) = byteSize switch
            {
                1 => Unwrap(await connection.ReadAnyAsync<byte>(handle, cancellationToken).ConfigureAwait(false)),
                2 => Unwrap(await connection.ReadAnyAsync<short>(handle, cancellationToken).ConfigureAwait(false)),
                4 => Unwrap(await connection.ReadAnyAsync<int>(handle, cancellationToken).ConfigureAwait(false)),
                8 => Unwrap(await connection.ReadAnyAsync<long>(handle, cancellationToken).ConfigureAwait(false)),
                _ => (null, AdsErrorCode.NoError),
            };

            if (errorCode != AdsErrorCode.NoError)
            {
                // The error arrives on the result, not as a throw, so this cannot be left to the
                // catch below. A handle does not survive a reconnect or a download, and a stale one
                // fails every cycle forever, so drop it and let the next pass create a new one.
                DropHandle(symbolPath);
                throw new AdsErrorException(
                    $"Failed to read ADS symbol '{symbolPath}' as a raw integer.", errorCode);
            }

            return value;

            static (object? Value, AdsErrorCode ErrorCode) Unwrap<T>(ResultValue<T> result) =>
                (result.ErrorCode == AdsErrorCode.NoError ? result.Value : null, result.ErrorCode);
        }
        catch
        {
            DropHandle(symbolPath);
            throw;
        }
    }

    /// <summary>
    /// Forgets a cached raw-integer handle so the next read creates a fresh one.
    /// </summary>
    private void DropHandle(string symbolPath)
    {
        _rawIntegerHandles.TryRemove(symbolPath, out _);
    }

    /// <summary>
    /// Releases every cached raw-integer handle on the PLC. Handles do not survive a reconnect or a
    /// download, and nothing else ever frees them, so without this each one is held for the life of
    /// the process and leaked on shutdown.
    /// </summary>
    private void ReleaseRawIntegerHandles(IAdsConnection? connection)
    {
        foreach (var symbolPath in _rawIntegerHandles.Keys)
        {
            if (_rawIntegerHandles.TryRemove(symbolPath, out var handle) && connection is not null)
            {
                try
                {
                    var deleteResult = connection.TryDeleteVariableHandle(handle);
                    if (deleteResult != AdsErrorCode.NoError)
                    {
                        _logger.LogDebug(
                            "Failed to release the ADS handle for '{SymbolPath}': {ErrorCode}.",
                            symbolPath, deleteResult);
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(exception,
                        "Failed to release the ADS handle for '{SymbolPath}'.", symbolPath);
                }
            }
        }
    }

    private void RebuildPollingSnapshot(AdsConnectionManager connectionManager)
    {
        var connection = connectionManager.Connection;
        // Read before building: anything registered while this runs advances the counter past it,
        // so the snapshot stays dirty and the next pass picks the new entries up.
        var version = Volatile.Read(ref _pollingCollectionVersion);

        if (connection is null)
        {
            _pollingSnapshot = PollingSnapshot.Empty;
            Volatile.Write(ref _pollingSnapshotVersion, version);
            return;
        }

        var newSymbols = new List<ISymbol>();
        var newEntries = new List<(PropertyReference Reference, string SymbolPath)>();

        foreach (var kvp in _polledProperties)
        {
            var symbol = TryGetSymbol(connectionManager.SymbolLoader, kvp.Value);
            if (symbol is not null)
            {
                newSymbols.Add(symbol);
                newEntries.Add((kvp.Key, kvp.Value));
            }
        }

        var sumRead = newSymbols.Count > 0
            ? new SumSymbolRead(connection, newSymbols)
            : null;

        // Assign snapshot atomically, then record the version it was built from.
        _pollingSnapshot = new PollingSnapshot(newSymbols, newEntries, sumRead);
        Volatile.Write(ref _pollingSnapshotVersion, version);
    }

    private void OnValueReceived(PropertyReference propertyReference, object? adsValue, DateTimeOffset? sourceTimestamp, SubjectPropertyWriter? propertyWriter, ISubjectSource source)
    {
        var registeredProperty = propertyReference.TryGetRegisteredProperty();
        if (registeredProperty is null)
        {
            return; // Subject was detached, skip
        }

        var convertedValue = _configuration.ValueConverter.ConvertToPropertyValue(adsValue, registeredProperty);

        propertyWriter?.Write(
            (propertyReference, convertedValue, source, sourceTimestamp),
            static state => state.propertyReference.SetValueFromSource(
                state.source,
                state.sourceTimestamp ?? DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                state.convertedValue));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // No connection to release against here; the PLC drops a session's handles and
            // notifications when the connection closes, which is what happens next.
            if (_notificationConnection is not null)
            {
                _notificationConnection.AdsNotificationEx -= OnAdsNotificationEx;
                _notificationConnection = null;
            }

            _notificationHandles.Clear();
            _rawIntegerHandles.Clear();
        }

        return ValueTask.CompletedTask;
    }
}
