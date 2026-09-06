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
    private readonly RootManager _rootManager;
    private readonly SubjectPathResolver _pathResolver;
    private readonly ILogger<OpcUaServer> _logger;
    private IOpcUaSubjectServer? _serverService;

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
    public bool Stop_IsEnabled => Status is ServiceStatus.Running or ServiceStatus.Starting; // TODO: Should check state of _serverService

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
                if (_serverService is { } service)
                {
                    var diagnostics = service.Diagnostics;
                    IncomingChangesPerSecond = diagnostics.Throughput.IncomingPerSecond;
                    OutgoingChangesPerSecond = diagnostics.Throughput.OutgoingPerSecond;
                    ActiveSessionCount = diagnostics.ActiveSessionCount;
                }

                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await StopServerAsync(CancellationToken.None);
    }

    public async Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        await StopServerAsync(cancellationToken);
        await StartServerAsync(cancellationToken);
    }

    private async Task StartServerAsync(CancellationToken cancellationToken)
    {
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

            // Resolve the target subject from path
            var targetSubject = _pathResolver.ResolveSubject(Path, PathStyle.Canonical);
            if (targetSubject == null)
            {
                Status = ServiceStatus.Error;
                StatusMessage = $"Could not resolve subject at path: {Path}";
                return;
            }

            // Build configuration with defaults
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

            _serverService = targetSubject.CreateOpcUaServer(configuration, _logger);
            await this.AttachHostedServiceAsync(_serverService, cancellationToken);

            Status = ServiceStatus.Running;
            _logger.LogInformation("OPC UA server started for path: {Path}", Path);
        }
        catch (Exception ex)
        {
            Status = ServiceStatus.Error;
            StatusMessage = ex.Message;
            _logger.LogError(ex, "Failed to start OPC UA server");
        }
    }

    private async Task StopServerAsync(CancellationToken cancellationToken)
    {
        if (_serverService != null)
        {
            try
            {
                Status = ServiceStatus.Stopping;
                await this.DetachHostedServiceAsync(_serverService, cancellationToken);
                _logger.LogInformation("OPC UA server stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop OPC UA server");
            }
            finally
            {
                _serverService = null;
                Status = ServiceStatus.Stopped;
                IncomingChangesPerSecond = null;
                OutgoingChangesPerSecond = null;
                ActiveSessionCount = null;
            }
        }
    }

}
