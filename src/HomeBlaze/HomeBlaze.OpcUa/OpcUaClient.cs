using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.OpcUa.Client;
using System.Text;
using Namotion.Interceptor.Registry.Attributes;
using Opc.Ua;

namespace HomeBlaze.OpcUa;

/// <summary>
/// OPC UA client subject that connects to an OPC UA server and discovers its address space dynamically.
/// </summary>
[Category("Clients")]
[Description("Connects to an OPC UA server and discovers properties dynamically")]
[InterceptorSubject]
public partial class OpcUaClient : BackgroundService, IConfigurable, ITitleProvider, IIconProvider
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly ILogger<OpcUaClient> _logger;

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
    /// only under <see cref="_attachmentGate"/>. Each attach builds its own root, so two sources
    /// would claim disjoint property sets and never conflict over ownership. The hazard is that both
    /// OPC UA sessions would be live with <see cref="Root"/> bound to one of them, while the other
    /// is unreachable from this wrapper and so can never be stopped from here.
    /// </summary>
    private IHostedServiceAttachment<IOpcUaSubjectClientSource>? _attachment;

    // Configuration properties

    /// <summary>
    /// Display name of the client.
    /// </summary>
    [Configuration]
    public partial string Name { get; set; }

    /// <summary>
    /// OPC UA server endpoint URL (e.g., "opc.tcp://localhost:4840").
    /// </summary>
    [Configuration]
    public partial string ServerUrl { get; set; }

    /// <summary>
    /// Optional root path to start browsing from under the Objects folder (use / as delimiter, e.g. "Machines/MyMachine").
    /// </summary>
    [Configuration]
    public partial string? RootPath { get; set; }

    /// <summary>
    /// Optional username for OPC UA server authentication. When empty, anonymous authentication is used.
    /// </summary>
    [Configuration]
    public partial string? Username { get; set; }

    /// <summary>
    /// Optional password for OPC UA server authentication.
    /// </summary>
    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

    /// <summary>
    /// Default sampling interval in milliseconds for monitored items.
    /// Null uses the server default. 0 enables exception-based monitoring (immediate reporting).
    /// </summary>
    [Configuration]
    public partial int? SamplingInterval { get; set; }

    /// <summary>
    /// Whether the client is enabled and should auto-start on application startup.
    /// </summary>
    [Configuration]
    [State(Position = 0)]
    public partial bool IsEnabled { get; set; }

    // State properties

    /// <summary>
    /// Current client status.
    /// </summary>
    [State]
    public partial ServiceStatus Status { get; set; }

    /// <summary>
    /// Error message when Status is Error.
    /// </summary>
    [State]
    public partial string? StatusMessage { get; set; }

    /// <summary>
    /// Whether the client is currently connected. Null when not running.
    /// </summary>
    [State]
    public partial bool? IsConnected { get; set; }

    /// <summary>
    /// Average incoming changes per second (server to client). Null when not running.
    /// </summary>
    [State]
    public partial double? IncomingChangesPerSecond { get; set; }

    /// <summary>
    /// Average outgoing changes per second (client to server). Null when not running.
    /// </summary>
    [State]
    public partial double? OutgoingChangesPerSecond { get; set; }

    /// <summary>
    /// Number of monitored items in the client. Null when not running.
    /// </summary>
    [State]
    public partial double? MonitoredItemCount { get; set; }

    /// <summary>
    /// Number of items using polling fallback. Null when not running.
    /// </summary>
    [State]
    public partial int? PollingItemCount { get; set; }

    /// <summary>
    /// Number of writes queued for retry during disconnection. Null when not running.
    /// </summary>
    [State]
    public partial int? PendingWriteCount { get; set; }

    /// <summary>
    /// Total number of reconnections since start. Null when not running.
    /// </summary>
    [State(IsCumulative = true)]
    public partial long? TotalReconnections { get; set; }

    /// <summary>
    /// Dynamic root subject containing discovered OPC UA properties.
    /// Recreated on each connection to provide a clean slate.
    /// </summary>
    [State]
    public partial DynamicSubject? Root { get; set; }

    // Operations

    /// <summary>
    /// Starts the OPC UA client and enables auto-start on next application startup.
    /// </summary>
    [Operation(Title = "Start", Position = 1, Icon = "Start", RequiresConfirmation = true)]
    public Task StartAsync()
    {
        IsEnabled = true;
        return StartClientAsync(CancellationToken.None);
    }

    [Derived]
    [PropertyAttribute("Start", KnownAttributes.IsEnabled)]
    public bool Start_IsEnabled => Status == ServiceStatus.Stopped || Status == ServiceStatus.Error;

    /// <summary>
    /// Stops the OPC UA client and disables auto-start on next application startup.
    /// </summary>
    [Operation(Title = "Stop", Position = 2, Icon = "Stop", RequiresConfirmation = true)]
    public Task StopAsync()
    {
        IsEnabled = false;
        return StopClientAsync(CancellationToken.None);
    }

    [Derived]
    [PropertyAttribute("Stop", KnownAttributes.IsEnabled)]
    public bool Stop_IsEnabled => Status is ServiceStatus.Running or ServiceStatus.Starting;

    // Interface implementations

    public string? Title => Name;

    public string? IconName => "Cable";

    [Derived]
    public string? IconColor => Status == ServiceStatus.Running ? "Success" : null;

    public OpcUaClient(ILogger<OpcUaClient> logger)
    {
        _logger = logger;

        Name = string.Empty;
        ServerUrl = string.Empty;
        Status = ServiceStatus.Stopped;
        IsEnabled = true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (IsEnabled)
        {
            await StartClientAsync(stoppingToken);
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
        //
        // Root is left alone for the same reason: the source is still running here and is stopped only
        // after this unwind returns, so clearing it would pull the tree out from under a live source. It
        // is dropped in UpdateFromAttachment instead, on the first poll or restart that sees the
        // attachment holding no instance.
        Status = ServiceStatus.Stopped;
        ResetDiagnostics();
    }

    public async Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        await StopClientAsync(cancellationToken);

        // Guarded here rather than left to ExecuteAsync's caller-side check: without it an edit that
        // disables the client stops it and starts it again in the same call.
        if (IsEnabled)
        {
            await StartClientAsync(cancellationToken);
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
            Root = null;
            return;
        }

        // Cleared here and not only on the start path: the handler clears a stale fault on the next
        // successful transition, so a recovered attachment would otherwise keep reporting Running beside
        // the error text of the transition that failed.
        StatusMessage = null;

        if (attachment.Current is not { } source)
        {
            // Attached but not yet created: the handler's start transition has not run, or it has just
            // disposed the previous instance on a re-attach. Dropping the tree the disposed source filled
            // belongs here rather than in the unwind, which runs while that source is still live.
            Root = null;
            Status = ServiceStatus.Starting;
            return;
        }

        Status = ServiceStatus.Running;

        var diagnostics = source.Diagnostics;
        IsConnected = diagnostics.IsOperational;
        IncomingChangesPerSecond = diagnostics.Throughput.IncomingPerSecond;
        OutgoingChangesPerSecond = diagnostics.Throughput.OutgoingPerSecond;
        MonitoredItemCount = diagnostics.MonitoredItemCount;
        PollingItemCount = diagnostics.Polling?.ItemCount ?? 0;
        PendingWriteCount = diagnostics.OutboundRetries.Depth;
        TotalReconnections = diagnostics.Reconnects.TotalAttempts;
    }

    private void ResetDiagnostics()
    {
        IsConnected = null;
        IncomingChangesPerSecond = null;
        OutgoingChangesPerSecond = null;
        MonitoredItemCount = null;
        PollingItemCount = null;
        PendingWriteCount = null;
        TotalReconnections = null;
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

    private async Task StartClientAsync(CancellationToken cancellationToken)
    {
        if (!await TryEnterAttachmentGateAsync(cancellationToken))
        {
            return;
        }

        try
        {
            Status = ServiceStatus.Starting;
            StatusMessage = null;

            if (string.IsNullOrEmpty(ServerUrl))
            {
                Status = ServiceStatus.Error;
                StatusMessage = "Server URL is not configured";
                return;
            }

            // The attachment survives a context detach, so on re-attach the handler re-invokes the
            // factory itself. Without this guard a restarted ExecuteAsync would attach a second source
            // alongside the one the handler just re-created.
            if (_attachment is null)
            {
                // The awaited overload, and CancellationToken.None rather than the caller's token. The
                // returned handle is the only record of the attachment, and the transition runs to
                // completion whatever the token does, so a cancelled wait would strand a live
                // attachment with nothing pointing at it and let the next start attach a second source.
                // The wait is bounded: the source is a BackgroundService whose StartAsync returns at its
                // first await, and a start appended during shutdown returns without creating anything.
                var attachment = await this.AttachHostedServiceAsync(CreateClientSource, CancellationToken.None);
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
                        "OPC UA client for server {ServerUrl} was not started: the subject is not attached to a running host.",
                        ServerUrl);
                    return;
                }

                _attachment = attachment;
            }

            UpdateFromAttachment();
            _logger.LogInformation("OPC UA client started for server: {ServerUrl}", ServerUrl);
        }
        catch (Exception exception)
        {
            // No OperationCanceledException filter: nothing inside this try awaits the caller's token,
            // so the filter could only catch a genuine start failure that happens to surface as one and
            // would report it as a clean stop. A cancelled wait leaves through the gate helper instead.
            Status = ServiceStatus.Error;
            StatusMessage = exception.Message;
            _logger.LogError(exception, "Failed to start OPC UA client");
        }
        finally
        {
            _attachmentGate.Release();
        }
    }

    /// <summary>
    /// Builds the client source. Invoked by the handler on every attach, so it reads the configuration
    /// and builds a fresh root each time rather than capturing a snapshot: a re-attach must produce a
    /// new instance, because the handler has already disposed the previous one.
    /// </summary>
    private IOpcUaSubjectClientSource CreateClientSource()
    {
        var rootPathSegments = RootPath?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var root = new OpcUaDynamicSubject(rootPathSegments is { Length: > 0 } ? rootPathSegments[^1] : "Root");
        Root = root;

        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = ServerUrl,
            RootPath = rootPathSegments,
            DefaultSamplingInterval = SamplingInterval,
            TypeResolver = new HomeBlazeOpcUaTypeResolver(_logger),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new HomeBlazeOpcUaSubjectFactory(),
            CreateUserIdentity = !string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password)
                ? _ => Task.FromResult(new UserIdentity(Username, Encoding.UTF8.GetBytes(Password)))
                : null,
        };

        return root.CreateOpcUaClientSource(configuration, _logger);
    }

    private async Task StopClientAsync(CancellationToken cancellationToken)
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
                _logger.LogInformation("OPC UA client stopped");
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to stop OPC UA client");
            }

            // Cleared from the subject's own attachment set rather than from the detach having
            // returned: the field must never read null while an attachment is still live, or the guard
            // in the start path attaches a second source over it. A detach that threw before removing
            // the attachment did not stop anything.
            if (!this.GetHostedServiceAttachments().Contains(attachment))
            {
                _attachment = null;
                Root = null;
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
