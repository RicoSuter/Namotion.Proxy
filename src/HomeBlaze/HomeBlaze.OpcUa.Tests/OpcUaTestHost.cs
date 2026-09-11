using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Validation;

namespace HomeBlaze.OpcUa.Tests;

/// <summary>
/// The host, context and graph these tests share. The context is the application's own,
/// <see cref="SubjectContextFactory"/>, so nothing here can agree with the wrappers about a contract
/// the application does not have. The one exception, and why it is one, is on
/// <see cref="StartReAttachableAsync"/>.
/// </summary>
internal sealed class OpcUaTestHost : IAsyncDisposable
{
    /// <summary>
    /// A port nothing listens on. The client source retries a refused connection rather than failing its
    /// start, so every state the wrapper can reach is reachable without an OPC UA server. Deliberately
    /// not 4840: a stray server on the default port would make these tests talk to it.
    /// </summary>
    public const string DeadServerUrl = "opc.tcp://127.0.0.1:14841/";

    private readonly IHost _host;
    private readonly ServiceProvider _serializerServices;
    private readonly SubjectPathResolver _pathResolver;
    private readonly string _rootConfigurationPath;
    private bool _hostStopped;

    private OpcUaTestHost(
        IHost host,
        IInterceptorSubjectContext context,
        WrapperContainer container,
        RootManager rootManager,
        SubjectPathResolver pathResolver,
        PropertyWriteSeam writeSeam,
        ServiceProvider serializerServices,
        string rootConfigurationPath)
    {
        _host = host;
        _pathResolver = pathResolver;
        _serializerServices = serializerServices;
        _rootConfigurationPath = rootConfigurationPath;

        Context = context;
        Container = container;
        RootManager = rootManager;
        WriteSeam = writeSeam;
    }

    public IInterceptorSubjectContext Context { get; }

    /// <summary>The subject that owns the graph slot a wrapper is put into and taken out of.</summary>
    public WrapperContainer Container { get; }

    public RootManager RootManager { get; }

    public PropertyWriteSeam WriteSeam { get; }

    /// <summary>
    /// Starts a host over the application's context.
    /// </summary>
    public static Task<OpcUaTestHost> StartAsync(Action<IInterceptorSubjectContext>? configureContext = null)
    {
        return StartCoreAsync(SubjectContextFactory.Create, configureContext);
    }

    /// <summary>
    /// Starts a host over a context a wrapper can leave and re-enter.
    /// </summary>
    /// <remarks>
    /// It is <see cref="SubjectContextFactory.Create"/> without <see cref="MethodPropertyInitializer"/>,
    /// which adds a registry property per [Operation] on every context attach and does not tolerate
    /// adding one twice: with it, putting any subject that has an operation back into the graph throws
    /// out of the assignment that does it, so both wrappers are unmovable in the application today and
    /// the re-attach their factories are written for is unreachable there. Nothing else differs, and
    /// nothing the hosting layer or either wrapper touches is missing.
    /// </remarks>
    public static Task<OpcUaTestHost> StartReAttachableAsync(Action<IInterceptorSubjectContext>? configureContext = null)
    {
        return StartCoreAsync(CreateContextWithoutMethodProperties, configureContext);
    }

    /// <summary>
    /// Completes <see cref="RootManager.RootLoaded"/> the only way the application can: by running the
    /// manager's own <c>ExecuteAsync</c> over a configuration file. The server wrapper awaits that task
    /// from its start path and spins on <see cref="RootManager.IsLoaded"/> from its factory, and both
    /// are driven by one private completion source, so a stand-in would have to re-implement the very
    /// ordering the wrapper depends on.
    /// </summary>
    public async Task LoadRootAsync()
    {
        await RootManager.StartAsync(CancellationToken.None);
        await RootManager.RootLoaded;
    }

    public OpcUaClient CreateClient(string? serverUrl = DeadServerUrl, bool isEnabled = true)
    {
        return new OpcUaClient(NullLogger<OpcUaClient>.Instance)
        {
            Name = "Test client",
            ServerUrl = serverUrl ?? string.Empty,
            IsEnabled = isEnabled
        };
    }

    /// <summary>
    /// A server whose address is deliberately not the 4840 default. No test here resolves a path, so
    /// none of them reaches the bind at all, but the first one that does must not take the port the
    /// OPC UA integration suite hardcodes: a stray listener there fails that suite in a way that looks
    /// like everything except a port conflict.
    /// </summary>
    public OpcUaServer CreateServer(string path, bool isEnabled = true)
    {
        return new OpcUaServer(RootManager, _pathResolver, NullLogger<OpcUaServer>.Instance)
        {
            Name = "Test server",
            Path = path,
            IsEnabled = isEnabled,
            BaseAddress = "opc.tcp://127.0.0.1:14842/"
        };
    }

    /// <summary>
    /// Waits for a wrapper to report <paramref name="expected"/>. Both wrappers reconcile from a
    /// transition on another chain, so a status has to be waited for rather than read straight after
    /// the write that triggers it.
    /// </summary>
    public static Task WaitForStatusAsync(Func<ServiceStatus> status, ServiceStatus expected)
    {
        return AsyncTestHelpers.WaitUntilAsync(
            () => status() == expected,
            message: $"The wrapper did not reach {expected}.");
    }

    /// <summary>
    /// Stops the host. Tests that need the drained host behaviour call this themselves; disposal is
    /// idempotent against it.
    /// </summary>
    public async Task StopHostAsync()
    {
        if (!_hostStopped)
        {
            _hostStopped = true;
            await _host.StopAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Before the host stop: anything still armed on the seam would hold or fail a write the
        // shutdown is waiting for.
        WriteSeam.ArmBeforeWrite(null);
        WriteSeam.ArmAfterWrite(null);

        await StopHostAsync();
        await RootManager.StopAsync(CancellationToken.None);
        RootManager.Dispose();
        _host.Dispose();
        await _serializerServices.DisposeAsync();

        File.Delete(_rootConfigurationPath);
    }

    private static async Task<OpcUaTestHost> StartCoreAsync(
        Func<IServiceCollection, IInterceptorSubjectContext> contextFactory,
        Action<IInterceptorSubjectContext>? configureContext)
    {
        // Defaults off for the reason the hosting suite gives: they watch appsettings.json, and a host
        // per test accumulates those watchers until the operating system refuses another one.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddLogging();

        var rootConfigurationPath = WriteRootConfigurationFile();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["HomeBlaze:RootConfigFile"] = rootConfigurationPath });

        var context = contextFactory(builder.Services);

        var writeSeam = new PropertyWriteSeam();
        context.WithService<IWriteInterceptor>(() => writeSeam, _ => false);
        configureContext?.Invoke(context);

        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(TestRoot)]);

        var serializerServices = new ServiceCollection().BuildServiceProvider();
        var rootManager = default(RootManager);
        var pathResolver = new SubjectPathResolver(() => rootManager?.Root);
        rootManager = new RootManager(
            new SubjectTypeRegistry(typeProvider),
            new ConfigurableSubjectSerializer(typeProvider, serializerServices),
            context,
            pathResolver,
            builder.Configuration);

        var host = builder.Build();
        await host.StartAsync();

        // After the host, so the container's attach runs against a handler that is already open for
        // business rather than queueing behind host startup.
        var container = new WrapperContainer(context);

        return new OpcUaTestHost(
            host, context, container, rootManager, pathResolver, writeSeam, serializerServices, rootConfigurationPath);
    }

    private static IInterceptorSubjectContext CreateContextWithoutMethodProperties(IServiceCollection services)
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithReadPropertyRecorder()
            .WithRegistry()
            .WithParents()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer)
            .WithDataAnnotationValidation()
            .WithHostedServices(services);
    }

    private static string WriteRootConfigurationFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"homeblaze-opcua-root-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
            {
              "$type": "{{typeof(TestRoot).FullName}}",
              "name": "root"
            }
            """);

        return path;
    }
}
