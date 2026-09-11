using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Dynamic;
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
public partial class OpcUaClient
    : BackgroundService, IConfigurable, ITitleProvider, IIconProvider, IAttachmentOwner<IOpcUaSubjectClientSource>
{
    private readonly ILogger<OpcUaClient> _logger;

    /// <summary>
    /// The attachment this wrapper owns and every path that maintains it.
    /// </summary>
    private readonly SingleAttachmentHost<IOpcUaSubjectClientSource> _attachmentHost;

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
        return _attachmentHost.StartAsync(CancellationToken.None);
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
        return _attachmentHost.StopAsync(CancellationToken.None);
    }

    [Derived]
    [PropertyAttribute("Stop", KnownAttributes.IsEnabled)]
    public bool Stop_IsEnabled => Status is ServiceStatus.Running or ServiceStatus.Starting;

    // Interface implementations

    public string? Title => Name;

    public string? IconName => "Cable";

    [Derived]
    public string? IconColor => Status == ServiceStatus.Running ? "Success" : null;

    /// <remarks>
    /// <paramref name="diagnosticsPollInterval"/> is how often the running client is reconciled with its
    /// attachment, and null takes the default.
    /// </remarks>
    public OpcUaClient(ILogger<OpcUaClient> logger, TimeSpan? diagnosticsPollInterval = null)
    {
        _logger = logger;
        _attachmentHost = new SingleAttachmentHost<IOpcUaSubjectClientSource>(this, logger, diagnosticsPollInterval);

        Name = string.Empty;
        ServerUrl = string.Empty;
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
    // client source adds nothing to what this subject publishes.

    string IAttachmentOwner<IOpcUaSubjectClientSource>.LogName => "OPC UA client";

    string IAttachmentOwner<IOpcUaSubjectClientSource>.LogTarget => ServerUrl;

    string? IAttachmentOwner<IOpcUaSubjectClientSource>.GetConfigurationError()
    {
        return string.IsNullOrEmpty(ServerUrl) ? "Server URL is not configured" : null;
    }

    IOpcUaSubjectClientSource IAttachmentOwner<IOpcUaSubjectClientSource>.CreateInstance()
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

    void IAttachmentOwner<IOpcUaSubjectClientSource>.ApplyDiagnostics(IOpcUaSubjectClientSource source)
    {
        var diagnostics = source.Diagnostics;
        IsConnected = diagnostics.IsOperational;
        IncomingChangesPerSecond = diagnostics.Throughput.IncomingPerSecond;
        OutgoingChangesPerSecond = diagnostics.Throughput.OutgoingPerSecond;
        MonitoredItemCount = diagnostics.MonitoredItemCount;
        PollingItemCount = diagnostics.Polling?.ItemCount ?? 0;
        PendingWriteCount = diagnostics.OutboundRetries.Depth;
        TotalReconnections = diagnostics.Reconnects.TotalAttempts;
    }

    void IAttachmentOwner<IOpcUaSubjectClientSource>.ResetDiagnostics()
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
    /// Drops the discovered tree, which belongs to the source that filled it and is built again by the
    /// next factory run.
    /// </summary>
    void IAttachmentOwner<IOpcUaSubjectClientSource>.DropInstanceState()
    {
        Root = null;
    }
}
