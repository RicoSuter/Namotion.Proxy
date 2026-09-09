using HomeBlaze.Abstractions;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace HomeBlaze.Services;

/// <summary>
/// Manages loading and access to the root subject.
/// Bootstraps the system from root.json configuration.
/// </summary>
public class RootManager : BackgroundService, IConfigurationWriter
{
    private readonly SubjectTypeRegistry _typeRegistry;
    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly IInterceptorSubjectContext _context;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<RootManager>? _logger;

    // Continuations run off the loading thread so a UI waiter cannot resume inline inside the load.
    private readonly TaskCompletionSource<IInterceptorSubject> _rootLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string? _configurationPath;

    /// <summary>
    /// The root subject loaded from configuration.
    /// </summary>
    /// <remarks>Available during attachment for path resolution. Await <see cref="RootLoaded"/> before browsing the graph.</remarks>
    public IInterceptorSubject? Root { get; internal set; }

    /// <summary>
    /// Whether root loading, attachment and service registration completed successfully.
    /// </summary>
    public bool IsLoaded => RootLoaded.IsCompletedSuccessfully;

    public RootManager(
        SubjectTypeRegistry typeRegistry,
        ConfigurableSubjectSerializer serializer,
        IInterceptorSubjectContext context,
        SubjectPathResolver pathResolver,
        IConfiguration? configuration = null,
        ILogger<RootManager>? logger = null,
        ILoggerFactory? loggerFactory = null)
    {
        _typeRegistry = typeRegistry;
        _serializer = serializer;
        _context = context;
        _configuration = configuration;
        _logger = logger;

        // Register self with context for subjects to access
        context.AddService(this);

        // Subjects loaded below resolve their own canonical path (the history stores do it on every
        // recorded change), so the resolver has to be in the context before the graph exists. Taking
        // it as a dependency is what guarantees that ordering.
        context.AddService<ISubjectPathResolver>(pathResolver);

        // Make host logging available to context services and lifecycle handlers, which are
        // constructed by the context factory before any provider exists and so cannot inject it
        // (for example PropertyAttributeInitializer warning on a [State]/secret conflict). Runs
        // before the root graph is built in ExecuteAsync, so handlers see it during attach.
        if (loggerFactory is not null)
        {
            context.AddService(loggerFactory);
        }
    }

    /// <summary>
    /// Completes with the root subject after loading, attachment, service registration and deferred startup release.
    /// Reports the loading failure or cancellation.
    /// Assigning <see cref="Root"/> directly does not complete it.
    /// </summary>
    public Task<IInterceptorSubject> RootLoaded => _rootLoaded.Task;

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var startup = base.StartAsync(cancellationToken);

        // BackgroundService can cancel its queued work before ExecuteAsync runs, bypassing its catch.
        _ = ExecuteTask!.ContinueWith(
            _ => _rootLoaded.TrySetCanceled(cancellationToken),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnCanceled | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return startup;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _rootLoaded.TrySetResult(await LoadAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Cancelled rather than faulted, so a waiting UI treats a normal shutdown as one.
            _rootLoaded.TrySetCanceled(stoppingToken);
            throw;
        }
        catch (Exception exception)
        {
            _rootLoaded.TrySetException(exception);
            throw;
        }
    }

    private async Task<IInterceptorSubject> LoadAsync(CancellationToken cancellationToken)
    {
        if (Root != null)
        {
            return Root;
        }

        var configFileName = _configuration?["HomeBlaze:RootConfigFile"] ?? "root.json";
        _configurationPath = Path.GetFullPath(configFileName);
        _logger?.LogInformation("Loading root configuration from: {Path}", _configurationPath);

        if (!File.Exists(_configurationPath))
        {
            throw new FileNotFoundException($"Root configuration file not found: {_configurationPath}", _configurationPath);
        }

        var json = await File.ReadAllTextAsync(_configurationPath, cancellationToken);
        using var startup = _context.DeferHostedServiceStartup();
        var root = _serializer.Deserialize(json);

        // All IConfigurable implementations are also IInterceptorSubject (via [InterceptorSubject] attribute).
        Root = root as IInterceptorSubject ?? throw new InvalidOperationException("Failed to deserialize root configuration");

        // The application root must survive every reachability decision. Promote a constructor's
        // provisional anchor without repeating its attach callbacks, or attach a detached root.
        Root.Executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
        if (!ReferenceEquals(attachedContext, _context) || anchor != SubjectAttachmentAnchorKind.Explicit)
        {
            Root.AttachToContext(_context);
        }

        _logger?.LogInformation("Root loaded: {Type}", Root.GetType().FullName);
        _context.AddService(Root);
        startup?.Complete();
        return Root;
    }

    /// <summary>
    /// Writes the root subject configuration to disk if this is the root subject.
    /// Called by ConfigurationManager when [Configuration] properties change.
    /// </summary>
    public async Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        if (subject != Root)
            return false;

        if (string.IsNullOrEmpty(_configurationPath))
            throw new InvalidOperationException("Cannot save: config path is not set");

        _logger?.LogInformation("Saving root configuration to: {Path}", _configurationPath);

        var json = _serializer.Serialize(Root);
        await File.WriteAllTextAsync(_configurationPath, json, cancellationToken);

        _logger?.LogInformation("Root configuration saved successfully");
        return true;
    }
}
