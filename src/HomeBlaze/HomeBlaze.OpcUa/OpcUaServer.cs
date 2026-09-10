using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.OpcUa;

/// <summary>
/// OPC UA server subject that exposes other subjects via OPC UA protocol.
/// </summary>
[Category("Servers")]
[Description("Exposes subjects via OPC UA protocol")]
[InterceptorSubject]
public partial class OpcUaServer : BackgroundService, IConfigurable, ITitleProvider, IIconProvider, IServerSubject
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RootLoadWaitTimeout = TimeSpan.FromSeconds(10);

    private readonly RootManager _rootManager;
    private readonly SubjectPathResolver _pathResolver;
    private readonly ILogger<OpcUaServer> _logger;

    /// <summary>
    /// Serializes the start path, the stop path and the diagnostics poll against each other, and is
    /// what publishes <see cref="_attachment"/> between the threads that touch it. Held across the
    /// whole of each path, because the guard on the attachment spans the attach's own await. Never
    /// waited for from this subject's own StopAsync or from the ExecuteAsync unwind, so it can never
    /// park inside the handler's stop transition for this subject.
    /// </summary>
    private readonly SemaphoreSlim _attachmentGate = new(1, 1);

    /// <summary>
    /// The single attachment this wrapper owns, or null when nothing is attached. Read and written
    /// only under <see cref="_attachmentGate"/>. Two servers bound to the same subject would race over
    /// the same endpoint, and the second would be unreachable from this wrapper and so could never be
    /// stopped from here.
    /// </summary>
    private IHostedServiceAttachment<IOpcUaSubjectServer>? _attachment;

    // Configuration properties (persisted to JSON)

    /// <summary>
    /// Display name of the server.
    /// </summary>
    [Configuration]
    public partial string Name { get; set; }

    /// <summary>
    /// Subject path to expose via OPC UA (e.g., "/" or "/Children[demo]").
    /// </summary>
    [Configuration]
    public partial string Path { get; set; }

    /// <summary>
    /// OPC UA application name. Uses default if not specified.
    /// </summary>
    [Configuration]
    public partial string? ApplicationName { get; set; }

    /// <summary>
    /// OPC UA namespace URI. Uses default if not specified.
    /// </summary>
    [Configuration]
    public partial string? NamespaceUri { get; set; }

    /// <summary>
    /// OPC UA root folder name. Uses default if not specified.
    /// </summary>
    [Configuration]
    public partial string? RootName { get; set; }

    /// <summary>
    /// OPC UA server base address (e.g., "opc.tcp://localhost:4840/"). Uses default if not specified.
    /// </summary>
    [Configuration]
    public partial string? BaseAddress { get; set; }

    /// <summary>
    /// Whether to clean the certificate store on start. Uses default if not specified.
    /// </summary>
    [Configuration]
    public partial bool? CleanCertificateStore { get; set; }

    /// <summary>
    /// Change buffer time in milliseconds. Uses default if not specified.
    /// </summary>
    [Configuration]
    public partial int? BufferTimeMs { get; set; }

    /// <summary>
    /// Whether the server is enabled and should auto-start on application startup.
    /// When stopped manually, this is set to false to prevent auto-restart.
    /// </summary>
    [Configuration]
    [State(Position = 0)]
    public partial bool IsEnabled { get; set; }

    // State properties (runtime only)

    /// <summary>
    /// Current server status.
    /// </summary>
    [State]
    public partial ServiceStatus Status { get; set; }

    /// <summary>
    /// Error message when Status is Error.
    /// </summary>
    [State]
    public partial string? StatusMessage { get; set; }

    /// <summary>
    /// Average incoming changes per second (client writes to server). Null when not running.
    /// </summary>
    [State]
    public partial double? IncomingChangesPerSecond { get; set; }

    /// <summary>
    /// Average outgoing changes per second (subject changes pushed to OPC UA nodes). Null when not running.
    /// </summary>
    [State]
    public partial double? OutgoingChangesPerSecond { get; set; }

    /// <summary>
    /// Number of active OPC UA client sessions. Null when not running.
    /// </summary>
    [State]
    public partial int? ActiveSessionCount { get; set; }

    // Operations

    /// <summary>
    /// Starts the OPC UA server and enables auto-start on next application startup.
    /// </summary>
    [Operation(Title = "Start", Position = 1, Icon = "Start", RequiresConfirmation = true)]
    public Task StartAsync()
    {
        IsEnabled = true;
        return StartServerAsync(CancellationToken.None);
    }

    [Derived]
    [PropertyAttribute("Start", KnownAttributes.IsEnabled)]
    public bool Start_IsEnabled => Status == ServiceStatus.Stopped || Status == ServiceStatus.Error;

    /// <summary>
    /// Stops the OPC UA server and disables auto-start on next application startup.
    /// </summary>
    [Operation(Title = "Stop", Position = 2, Icon = "Stop", RequiresConfirmation = true)]
    public Task StopAsync()
    {
        IsEnabled = false;
        return StopServerAsync(CancellationToken.None);
    }

    [Derived]
    [PropertyAttribute("Stop", KnownAttributes.IsEnabled)]
    public bool Stop_IsEnabled => Status is ServiceStatus.Running or ServiceStatus.Starting;

    // Interface implementations

    [Derived]
    public bool IsServerRunning => Status == ServiceStatus.Running;

    public string? Title => Name;

    public string? IconName => "Dns";

    [Derived]
    public string? IconColor => Status == ServiceStatus.Running ? "Success" : null;

    public OpcUaServer(
        RootManager rootManager,
        SubjectPathResolver pathResolver,
        ILogger<OpcUaServer> logger)
    {
        _rootManager = rootManager;
        _pathResolver = pathResolver;
        _logger = logger;

        Name = string.Empty;
        Path = string.Empty;
        Status = ServiceStatus.Stopped;
        IsEnabled = true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (IsEnabled)
        {
            await StartServerAsync(stoppingToken);
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TryUpdateFromAttachment();
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // Deliberately does NOT detach, and deliberately does not take the gate: this unwind runs inside
        // the handler's own stop transition for this subject, and either one would wait on that
        // transition. See docs/hosting.md#do-not-detach-from-your-own-stop-path. The handler owns the
        // detach on graph events; the explicit detach lives on the Stop operation and
        // ApplyConfigurationAsync, neither of which is reached through StopAsync.
        Status = ServiceStatus.Stopped;
        ResetDiagnostics();
    }

    public async Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        await StopServerAsync(cancellationToken);

        // Guarded here rather than left to ExecuteAsync's caller-side check: without it an edit that
        // disables the server stops it and starts it again in the same call.
        if (IsEnabled)
        {
            await StartServerAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Polls the attachment from the diagnostics loop. Skips the round rather than waiting when a start
    /// or a stop holds the gate: that caller reconciles the state itself before it releases, and a poll
    /// that waited here would sit in the way of the shutdown that cancels it. Ungated, a poll preempted
    /// inside <see cref="UpdateFromAttachment"/> resumes after a completed stop and writes Running over
    /// Stopped, which no later poll corrects because the attachment is null by then.
    /// </summary>
    private void TryUpdateFromAttachment()
    {
        if (!_attachmentGate.Wait(0))
        {
            return;
        }

        try
        {
            UpdateFromAttachment();
        }
        finally
        {
            _attachmentGate.Release();
        }
    }

    /// <summary>
    /// Reconciles the reported status and the diagnostics with what the attachment actually holds. The
    /// handler creates, faults and disposes the instance on its own chain (a context re-attach re-invokes
    /// the factory without going through this wrapper), so polling the handle is the only way those
    /// outcomes reach the UI. Must be called with <see cref="_attachmentGate"/> held.
    /// </summary>
    private void UpdateFromAttachment()
    {
        if (_attachment is not { } attachment || Status is ServiceStatus.Stopping or ServiceStatus.Stopped)
        {
            return;
        }

        if (attachment.Fault is { } fault)
        {
            Status = ServiceStatus.Error;
            StatusMessage = fault.Message;
            ResetDiagnostics();
            return;
        }

        // Cleared here and not only on the start path: the handler clears a stale fault on the next
        // successful transition, so a recovered attachment would otherwise keep reporting Running beside
        // the error text of the transition that failed.
        StatusMessage = null;

        if (attachment.Current is not { } server)
        {
            // Attached but not yet created: the handler's start transition has not run, or it has just
            // disposed the previous instance on a re-attach.
            Status = ServiceStatus.Starting;
            return;
        }

        Status = ServiceStatus.Running;

        var diagnostics = server.Diagnostics;
        IncomingChangesPerSecond = diagnostics.Throughput.IncomingPerSecond;
        OutgoingChangesPerSecond = diagnostics.Throughput.OutgoingPerSecond;
        ActiveSessionCount = diagnostics.ActiveSessionCount;
    }

    private void ResetDiagnostics()
    {
        IncomingChangesPerSecond = null;
        OutgoingChangesPerSecond = null;
        ActiveSessionCount = null;
    }

    /// <summary>
    /// Waits for the attachment gate. Returns false when the wait was cancelled, in which case the caller
    /// holds nothing, must not release it and must report nothing: whoever holds the gate is maintaining
    /// the reported state. The caller's token is honoured so the wait can never stand in the way of the
    /// stop that cancels it, which is what keeps ExecuteAsync's own start off the handler's stop chain.
    /// </summary>
    private async Task<bool> TryEnterAttachmentGateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _attachmentGate.WaitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task StartServerAsync(CancellationToken cancellationToken)
    {
        if (!await TryEnterAttachmentGateAsync(cancellationToken))
        {
            return;
        }

        try
        {
            Status = ServiceStatus.Starting;
            StatusMessage = null;

            if (string.IsNullOrEmpty(Path))
            {
                Status = ServiceStatus.Error;
                StatusMessage = "Path is not configured";
                return;
            }

            // Awaited here rather than in the factory, which is a synchronous Func<T> and cannot await.
            // This is the awaited start path, reached from ExecuteAsync, the Start operation and
            // ApplyConfigurationAsync, and never through this subject's own StopAsync, so parking here
            // cannot sit inside the handler's stop transition for this subject.
            try
            {
                // WaitAsync does not observe the token when the task is already complete, so the
                // caller's cancellation has to be checked in its own right.
                cancellationToken.ThrowIfCancellationRequested();
                await _rootManager.RootLoaded.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _rootManager.RootLoaded.IsCanceled)
            {
                Status = ServiceStatus.Stopped;
                return;
            }

            // The attachment survives a context detach, so on re-attach the handler re-invokes the
            // factory itself. Without this guard a restarted ExecuteAsync would attach a second server
            // alongside the one the handler just re-created.
            if (_attachment is null)
            {
                // The awaited overload, and CancellationToken.None rather than the caller's token. The
                // returned handle is the only record of the attachment, and the transition runs to
                // completion whatever the token does, so a cancelled wait would strand a live
                // attachment with nothing pointing at it and let the next start attach a second server.
                // The wait is bounded: the server is a BackgroundService whose StartAsync returns at its
                // first await, and a start appended during shutdown returns without creating anything.
                var attachment = await this.AttachHostedServiceAsync(CreateServer, CancellationToken.None);
                if (attachment.Current is null)
                {
                    // The awaited overload appends nothing when the context has no handler, when the
                    // subject is not in the graph and when the host is draining, and it throws rather
                    // than returning when a start faulted, so no instance here means nothing was
                    // started and nothing will be before a context re-attach. Reported as an error and
                    // dropped rather than kept, which would report Starting forever.
                    this.DetachHostedService(attachment);

                    Status = ServiceStatus.Error;
                    StatusMessage = "Not attached to a running host, so nothing was started";
                    _logger.LogWarning(
                        "OPC UA server for path {Path} was not started: the subject is not attached to a running host.",
                        Path);
                    return;
                }

                _attachment = attachment;
            }

            UpdateFromAttachment();
            _logger.LogInformation("OPC UA server started for path: {Path}", Path);
        }
        catch (Exception exception)
        {
            // No OperationCanceledException filter: nothing inside this try awaits the caller's token,
            // so the filter could only catch a genuine start failure that happens to surface as one and
            // would report it as a clean stop. A cancelled wait leaves through the gate helper instead.
            Status = ServiceStatus.Error;
            StatusMessage = exception.Message;
            _logger.LogError(exception, "Failed to start OPC UA server");
        }
        finally
        {
            _attachmentGate.Release();
        }
    }

    /// <summary>
    /// Builds the server. Invoked by the handler on every attach, so it re-resolves the path and reads
    /// the configuration each time rather than capturing a snapshot: the target is a lookup into the
    /// graph, which may have replaced the subject at that path since the previous attach.
    /// </summary>
    private IOpcUaSubjectServer CreateServer()
    {
        // Blocks rather than awaits, because a synchronous Func<T> cannot await and the handler invokes
        // this directly on a re-attach, which never passes through the awaited wait in the start path.
        // Returns at once in every reachable case: the root manager publishes the root before it attaches
        // the graph this subject belongs to, so no attach of this subject can precede the load. Bounded
        // so that a wait that is somehow not satisfied fails the start rather than pinning a thread.
        if (!SpinWait.SpinUntil(() => _rootManager.IsLoaded, RootLoadWaitTimeout))
        {
            throw new InvalidOperationException(
                $"The root manager did not load within {RootLoadWaitTimeout.TotalSeconds:F0} seconds, so the path could not be resolved: {Path}");
        }

        // A synchronous factory can only signal a failed lookup by throwing. AttachHostedServiceAsync
        // rethrows it, and the catch on the start path turns it into a StatusMessage.
        var targetSubject = _pathResolver.ResolveSubject(Path, PathStyle.Canonical)
            ?? throw new InvalidOperationException($"Could not resolve subject at path: {Path}");

        var defaults = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter()
        };

        var configuration = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter(),
            Mapper = new OpcUaCompositeMapper(
                new OpcUaPathProviderMapper(new StateAttributeOpcUaPathProvider()),
                new OpcUaAttributeMapper()),
            ApplicationName = ApplicationName ?? defaults.ApplicationName,
            NamespaceUri = NamespaceUri ?? defaults.NamespaceUri,
            RootName = RootName,
            BaseAddress = BaseAddress ?? defaults.BaseAddress,
            CleanCertificateStore = CleanCertificateStore ?? defaults.CleanCertificateStore,
            BufferTime = BufferTimeMs.HasValue ? TimeSpan.FromMilliseconds(BufferTimeMs.Value) : defaults.BufferTime,
        };

        return targetSubject.CreateOpcUaServer(configuration, _logger);
    }

    private async Task StopServerAsync(CancellationToken cancellationToken)
    {
        if (!await TryEnterAttachmentGateAsync(cancellationToken))
        {
            return;
        }

        try
        {
            if (_attachment is not { } attachment)
            {
                return;
            }

            Status = ServiceStatus.Stopping;

            try
            {
                // CancellationToken.None, symmetrically with the attach: the detach removes the
                // attachment before it stops anything, so a cancelled wait would return while the
                // instance is still stopping with nothing left pointing at it, and the next start would
                // create a second one alongside it. The wait is bounded by the stop the handler runs.
                await this.DetachHostedServiceAsync(attachment, CancellationToken.None);
                _logger.LogInformation("OPC UA server stopped");
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to stop OPC UA server");
            }

            // Cleared from the subject's own attachment set rather than from the detach having
            // returned: the field must never read null while an attachment is still live, or the guard
            // in the start path attaches a second server over it. A detach that threw before removing
            // the attachment did not stop anything.
            if (!this.GetHostedServiceAttachments().Contains(attachment))
            {
                _attachment = null;
            }

            Status = ServiceStatus.Stopped;
            ResetDiagnostics();
        }
        finally
        {
            _attachmentGate.Release();
        }
    }
}
