using System.Collections.Immutable;
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.History.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// Priority-50 SQLite history store. A <see cref="BackgroundService"/> [InterceptorSubject]
/// that records recordable [State] scalar property changes into partitioned SQLite database files and
/// answers raw and bucketed history queries through <see cref="IHistoryStore"/>. Storage concerns are
/// delegated to the graph-free <see cref="SqliteHistoryStore"/> engine; this subject owns the change-queue
/// glue, path resolution, move detection, the periodic flush plus sweep loop, and the shutdown flush.
/// </summary>
[Category("History")]
[Description("Persists [State] history to partitioned SQLite files (priority 50).")]
[InterceptorSubject]
public partial class SqliteHistoryStoreSubject :
    BackgroundService, IConfigurable, ITitleProvider, IHistoryStore, ILifecycleHandler
{
    private readonly ILogger<SqliteHistoryStoreSubject> _logger;

    private HistoryChangeRecorder? _recorder;
    private SqliteHistoryStore? _engine;

    public SqliteHistoryStoreSubject(ILogger<SqliteHistoryStoreSubject> logger)
    {
        _logger = logger;

        Priority = 50;
        MaxAgeDays = 365;
        FlushIntervalSeconds = 10;
        BufferTimeMilliseconds = 250;
        PartitionInterval = PartitionInterval.Weekly;
        DatabasePath = string.Empty;
        MaxJsonSize = 8192;
        IsEnabled = true;

        Status = "Stopped";
    }

    /// <inheritdoc />
    public string? Title => "SQLite History";

    // Configuration properties (persisted to JSON)

    /// <summary>
    /// Store priority. Higher values are preferred for overlapping ranges (SQLite is the persistent tier).
    /// </summary>
    [Configuration]
    public partial int Priority { get; set; }

    /// <summary>
    /// Retention window in days. Partition files whose range ends before now minus this are deleted on sweep.
    /// </summary>
    [Configuration]
    public partial int MaxAgeDays { get; set; }

    /// <summary>
    /// Interval in seconds between flushes of the pending sample batch to the partition files.
    /// </summary>
    [Configuration]
    public partial int FlushIntervalSeconds { get; set; }

    /// <summary>
    /// Change-queue buffer time in milliseconds before a batch is flushed to the recorder.
    /// </summary>
    [Configuration]
    public partial int BufferTimeMilliseconds { get; set; }

    /// <summary>
    /// The time span a single partition database file covers.
    /// </summary>
    [Configuration]
    public partial PartitionInterval PartitionInterval { get; set; }

    /// <summary>
    /// Directory that holds the partition database files. Relative paths resolve under the application data
    /// directory (outside the HomeBlaze Data folder); absolute paths are used as-is; empty uses the default
    /// "History" folder.
    /// </summary>
    [Configuration]
    public partial string DatabasePath { get; set; }

    /// <summary>
    /// Maximum JSON value size in characters; larger string values are recorded as an oversize placeholder.
    /// </summary>
    [Configuration]
    public partial int MaxJsonSize { get; set; }

    /// <summary>
    /// Whether the store is enabled and should auto-start on application startup.
    /// </summary>
    [Configuration]
    public partial bool IsEnabled { get; set; }

    // State properties (runtime only)

    /// <summary>
    /// Current store status.
    /// </summary>
    [State]
    public partial string Status { get; set; }

    /// <summary>
    /// Total number of samples recorded since start.
    /// </summary>
    [State]
    public partial long RecordedCount { get; set; }

    /// <summary>
    /// Number of oversize string values replaced with a placeholder.
    /// </summary>
    [State]
    public partial long OversizeCount { get; set; }

    /// <summary>
    /// Number of samples currently queued (not yet flushed to the partition files).
    /// </summary>
    [State]
    public partial int QueueDepth { get; set; }

    /// <summary>
    /// Cumulative number of samples dropped after the pending persistence queue reached its limit.
    /// </summary>
    [State]
    public partial long DropCount { get; set; }

    /// <summary>
    /// Timestamp of the last successful flush, or null before the first flush.
    /// </summary>
    [State]
    public partial DateTimeOffset? LastFlushUtc { get; set; }

    /// <summary>
    /// Message of the last error encountered during flush or sweep, or null when healthy.
    /// </summary>
    [State]
    public partial string? LastError { get; set; }

    /// <summary>
    /// Estimated on-disk storage used by the partition and metadata database files in bytes.
    /// </summary>
    [State(Unit = StateUnit.Byte)]
    public partial long EstimatedStorageSize { get; set; }

    /// <summary>
    /// Average incoming changes per second (eligible [State] changes observed).
    /// </summary>
    [State]
    public partial double IncomingChangesPerSecond { get; set; }

    /// <summary>
    /// Average recorded changes per second (samples written to the engine).
    /// </summary>
    [State]
    public partial double RecordedChangesPerSecond { get; set; }

    // IHistoryStore

    /// <inheritdoc />
    public ImmutableArray<HistoryCoverage> CoverageRanges =>
        _engine?.CoverageRanges ?? ImmutableArray<HistoryCoverage>.Empty;

    /// <inheritdoc />
    public IReadOnlySet<string> SupportedAggregations => SqliteHistoryStore.AllAggregations;

    /// <inheritdoc />
    public Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_engine is null)
        {
            return Task.FromResult(
                new HistorySeries(
                    query.PropertyPath,
                    ImmutableArray<HistoryPoint>.Empty,
                    false,
                    ImmutableArray<HistoryCoverage>.Empty));
        }

        return _engine.QueryAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(
        string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<HistoryPoint?>(_engine?.GetSampleAtOrBefore(propertyPath, asOf));
    }

    /// <summary>
    /// Test-only hook that forces the engine to flush its pending samples immediately, so queued
    /// changes become queryable without waiting for the interval flush. Returns a completed task
    /// (no-op) before the engine is built.
    /// </summary>
    internal Task FlushNowAsync(CancellationToken cancellationToken = default)
    {
        return _engine?.FlushAsync(cancellationToken) ?? Task.CompletedTask;
    }

    // BackgroundService

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled)
        {
            Status = "Disabled";
            return;
        }

        var context = ((IInterceptorSubject)this).GetContext();

        var resolver = context.TryGetService<ISubjectPathResolver>();
        if (resolver is null)
        {
            Status = "Error";
            LastError = "No subject path resolver is registered; cannot record history.";
            _logger.LogError("No ISubjectPathResolver is registered in the context; cannot record history.");
            return;
        }

        var directory = SqliteDatabaseLocation.Resolve(DatabasePath, SqliteDatabaseLocation.DefaultBaseDirectory());

        SqliteHistoryStore engine;
        try
        {
            engine = new SqliteHistoryStore(
                priority: Priority,
                databaseDirectory: directory,
                partitionInterval: PartitionInterval,
                maxAge: TimeSpan.FromDays(MaxAgeDays),
                maxJsonSize: MaxJsonSize,
                getUtcNow: () => DateTimeOffset.UtcNow,
                logger: _logger);
        }
        catch (Exception exception)
        {
            Status = "Error";
            LastError = exception.Message;
            _logger.LogError(exception, "Failed to open the SQLite history directory '{Directory}'.", directory);
            return;
        }

        // The change-queue subscription is live from construction, before the first await
        // (BackgroundService.StartAsync returns at that point). The coverage session starts only
        // afterwards, so no change can fall inside claimed coverage without reaching the engine.
        var recorder = new HistoryChangeRecorder(engine, resolver);
        _recorder = recorder;

        // A recorder is not a sink that can fall behind the model, so the settled condition never holds
        // for it. Under the other rule a source-applied value does not retire an older commit, which is
        // what keeps both points in the series.
        using var processor = new ChangeQueueProcessor(
            this,
            context,
            HistoryChangeRecorder.IsEligible,
            (changes, _) => recorder.RecordBatch(changes),
            ChangeDeliveryRule.SourceValuesMayBeStale,
            bufferTime: TimeSpan.FromMilliseconds(BufferTimeMilliseconds),
            maxQueueDepth: null,
            logger: _logger);

        engine.BeginCoverageSession();
        _engine = engine;

        _logger.LogInformation("Recording SQLite history to {Directory}.", directory);

        LastError = null;
        Status = "Running";

        var flushTask = RunFlushLoopAsync(engine, stoppingToken);
        try
        {
            await processor.ProcessAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await flushTask.ConfigureAwait(false);

            // Final shutdown flush so the un-flushed tail is persisted. The stopping token is already
            // cancelled here, so a fresh bounded token gives the flush a chance to complete (the engine
            // keeps pending samples on failure, so a timeout simply leaves them for the next start).
            using var shutdownCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await engine.FlushAsync(shutdownCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Final history flush on shutdown failed; pending samples were not persisted.");
            }

            RefreshMetrics(engine);
            engine.Dispose();
            Status = "Stopped";
        }
    }

    private async Task RunFlushLoopAsync(SqliteHistoryStore engine, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await engine.FlushAsync(stoppingToken).ConfigureAwait(false);
                    engine.Sweep();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    // The engine keeps pending samples on a flush failure, so the next tick retries; the
                    // loop must not crash and stop recording.
                    _logger.LogError(exception, "Periodic history flush or sweep failed; will retry on the next tick.");
                }

                RefreshMetrics(engine);

                await Task.Delay(EffectiveFlushInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            RefreshMetrics(engine);
        }
    }

    /// <summary>
    /// The flush interval actually used, clamped into a range <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// accepts. The configured value reaches here from a settings form and from a hand-editable file, and
    /// anything past <see cref="int.MaxValue"/> milliseconds throws out of the flush loop, which faults the
    /// task that <c>ExecuteAsync</c> awaits in its finally and stops the whole host.
    /// </summary>
    private TimeSpan EffectiveFlushInterval =>
        TimeSpan.FromSeconds(Math.Clamp(FlushIntervalSeconds, 1, (int)TimeSpan.FromDays(1).TotalSeconds));

    private void RefreshMetrics(SqliteHistoryStore engine)
    {
        try
        {
            RefreshMetricsCore(engine);
        }
        catch (Exception exception)
        {
            // Diagnostics only, and one of them walks the database directory, so a permissions change
            // under it must not take down a loop that is otherwise recording correctly. Two of the three
            // call sites are inside a finally, where a throw would also replace the real outcome.
            _logger.LogDebug(exception, "Could not refresh history diagnostics.");
        }
    }

    private void RefreshMetricsCore(SqliteHistoryStore engine)
    {
        RecordedCount = engine.RecordedCount;
        OversizeCount = engine.OversizeCount;
        QueueDepth = engine.QueueDepth;
        DropCount = engine.DropCount;
        EstimatedStorageSize = engine.EstimatedStorageBytes;
        LastFlushUtc = engine.LastFlushUtc;
        LastError = engine.LastError;
        IncomingChangesPerSecond = _recorder?.IncomingChangesPerSecond ?? 0;
        RecordedChangesPerSecond = _recorder?.RecordedChangesPerSecond ?? 0;
    }

    // ILifecycleHandler

    /// <inheritdoc />
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextDetach)
        {
            _recorder?.Forget(change.Subject);
        }
    }

    // IConfigurable

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        // Size knobs (MaxAgeDays, MaxJsonSize, PartitionInterval, DatabasePath) and BufferTime and
        // FlushInterval are read once when the engine and change-queue processor are built in ExecuteAsync.
        // Like OpcUaServer, configuration changes take effect on the next start; the host restarts the
        // background service to apply them.
        return Task.CompletedTask;
    }
}
