using HomeBlaze.Abstractions;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

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
    private string? _configurationPath;

    /// <summary>
    /// The root subject loaded from configuration.
    /// </summary>
    /// <remarks>Available during attachment for path resolution. Await <see cref="LoadingCompleted"/> before browsing the graph.</remarks>
    public IInterceptorSubject? Root { get; internal set; }

    /// <summary>
    /// Whether root loading, attachment and service registration completed successfully.
    /// </summary>
    public bool IsLoaded => ExecuteTask?.IsCompletedSuccessfully == true;

    /// <summary>
    /// Completes after root loading, attachment and service registration finish, or reports the loading failure or cancellation.
    /// </summary>
    /// <exception cref="InvalidOperationException">The hosted service has not been started.</exception>
    public Task LoadingCompleted => ExecuteTask ?? throw new InvalidOperationException("Root loading has not started.");

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadAsync(stoppingToken);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (Root != null)
        {
            return;
        }

        var configFileName = _configuration?["HomeBlaze:RootConfigFile"] ?? "root.json";
        _configurationPath = Path.GetFullPath(configFileName);
        _logger?.LogInformation("Loading root configuration from: {Path}", _configurationPath);

        if (!File.Exists(_configurationPath))
        {
            throw new FileNotFoundException($"Root configuration file not found: {_configurationPath}", _configurationPath);
        }

        var json = await File.ReadAllTextAsync(_configurationPath, cancellationToken);
        var root = _serializer.Deserialize(json);

        // All IConfigurable implementations are also IInterceptorSubject (via [InterceptorSubject] attribute).
        Root = root as IInterceptorSubject ?? throw new InvalidOperationException("Failed to deserialize root configuration");

        // The deserializer builds subjects through dependency injection, but a type whose only
        // constructor takes dependencies gets no generated context-taking constructor, so the root
        // can arrive here detached. Attach explicitly either way: the application root must survive
        // every reachability decision, and an explicit anchor also promotes a constructor-attached
        // root without repeating its attach callbacks.
        Root.AttachToContext(_context);

        _logger?.LogInformation("Root loaded: {Type}", Root.GetType().FullName);
        _context.AddService(Root);
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
