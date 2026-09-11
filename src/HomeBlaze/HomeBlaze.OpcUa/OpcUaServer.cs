using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
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
public partial class OpcUaServer
    : BackgroundService, IConfigurable, ITitleProvider, IIconProvider, IServerSubject,
      IAttachmentOwner<IOpcUaSubjectServer>
{
    private static readonly TimeSpan RootLoadWaitTimeout = TimeSpan.FromSeconds(10);

    private readonly RootManager _rootManager;
    private readonly SubjectPathResolver _pathResolver;
    private readonly ILogger<OpcUaServer> _logger;

    /// <summary>
    /// The attachment this wrapper owns and every path that maintains it.
    /// </summary>
    private readonly SingleAttachmentHost<IOpcUaSubjectServer> _attachmentHost;

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
        return _attachmentHost.StartAsync(CancellationToken.None);
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
        return _attachmentHost.StopAsync(CancellationToken.None);
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

    /// <remarks>
    /// <paramref name="diagnosticsPollInterval"/> is how often the running server is reconciled with its
    /// attachment, and null takes the default.
    /// </remarks>
    public OpcUaServer(
        RootManager rootManager,
        SubjectPathResolver pathResolver,
        ILogger<OpcUaServer> logger,
        TimeSpan? diagnosticsPollInterval = null)
    {
        _rootManager = rootManager;
        _pathResolver = pathResolver;
        _logger = logger;
        _attachmentHost = new SingleAttachmentHost<IOpcUaSubjectServer>(this, logger, diagnosticsPollInterval);

        Name = string.Empty;
        Path = string.Empty;
        Status = ServiceStatus.Stopped;
        IsEnabled = true;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return _attachmentHost.RunAsync(stoppingToken);
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        return _attachmentHost.ApplyConfigurationAsync(cancellationToken);
    }

    // What the attachment host reads back from this wrapper. Status, StatusMessage and IsEnabled are
    // the generated partial properties above; the rest is implemented explicitly, so hosting an OPC UA
    // server adds nothing to what this subject publishes.

    string IAttachmentOwner<IOpcUaSubjectServer>.LogName => "OPC UA server";

    string IAttachmentOwner<IOpcUaSubjectServer>.LogTarget => Path;

    string? IAttachmentOwner<IOpcUaSubjectServer>.GetConfigurationError()
    {
        return string.IsNullOrEmpty(Path) ? "Path is not configured" : null;
    }

    async ValueTask<bool> IAttachmentOwner<IOpcUaSubjectServer>.WaitUntilStartableAsync(CancellationToken cancellationToken)
    {
        try
        {
            // WaitAsync does not observe the token when the task is already complete, so the caller's
            // cancellation has to be checked in its own right.
            cancellationToken.ThrowIfCancellationRequested();
            await _rootManager.RootLoaded.WaitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _rootManager.RootLoaded.IsCanceled)
        {
            Status = ServiceStatus.Stopped;
            return false;
        }
    }

    IOpcUaSubjectServer IAttachmentOwner<IOpcUaSubjectServer>.CreateInstance()
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
        // rethrows it, and the catch on the start path turns it into a StatusMessage. The path is
        // re-resolved on every attach rather than captured, because it is a lookup into the graph, which
        // may have replaced the subject at that path since the previous one.
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

    void IAttachmentOwner<IOpcUaSubjectServer>.ApplyDiagnostics(IOpcUaSubjectServer server)
    {
        var diagnostics = server.Diagnostics;
        IncomingChangesPerSecond = diagnostics.Throughput.IncomingPerSecond;
        OutgoingChangesPerSecond = diagnostics.Throughput.OutgoingPerSecond;
        ActiveSessionCount = diagnostics.ActiveSessionCount;
    }

    void IAttachmentOwner<IOpcUaSubjectServer>.ResetDiagnostics()
    {
        IncomingChangesPerSecond = null;
        OutgoingChangesPerSecond = null;
        ActiveSessionCount = null;
    }
}
