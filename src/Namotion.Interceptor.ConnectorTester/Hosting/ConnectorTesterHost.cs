using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Connectors;
using Namotion.Interceptor.ConnectorTester.Engine;
using Namotion.Interceptor.ConnectorTester.Engine.Chaos;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;
using Namotion.Interceptor.ConnectorTester.Logging;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.ConnectorTester.Performance;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.ConnectorTester.Hosting;

/// <summary>
/// Builds the connector tester host: registers connectors, mutation engines, chaos engines,
/// the optional verification engine, and creates performance profilers. Performance profilers
/// live as <see cref="IHostedService"/> in each participant's sub-SP so they stop before
/// the participant SP is torn down (preserving access to the property-change context).
/// </summary>
public sealed class ConnectorTesterHost
{
    public IHost Host { get; }
    public VerificationEngine? VerificationEngine { get; }
    public RunModeSelection RunModeSelection { get; }
    public string RunDirectory { get; }

    private ConnectorTesterHost(
        IHost host,
        VerificationEngine? verificationEngine,
        RunModeSelection runModeSelection,
        string runDirectory)
    {
        Host = host;
        VerificationEngine = verificationEngine;
        RunModeSelection = runModeSelection;
        RunDirectory = runDirectory;
    }

    public static ConnectorTesterHost Build(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        // Resolve the environment from the full host config chain (DOTNET_ENVIRONMENT,
        // ASPNETCORE_ENVIRONMENT, --environment CLI arg) instead of reading one env var.
        var runDirectory = Path.Combine("logs", $"{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ssZ}-{builder.Environment.EnvironmentName}");
        Directory.CreateDirectory(runDirectory);

        // Cycle logger provider doubles as ILoggerProvider and ICycleRecorder.
        var cycleLoggerProvider = new CycleLoggerProvider(runDirectory);
        builder.Services.AddSingleton(cycleLoggerProvider);
        builder.Services.AddSingleton<ICycleRecorder>(cycleLoggerProvider);
        builder.Logging.AddProvider(cycleLoggerProvider);

        // Shared logger factory feeds (a) engines created before DI build and (b) per-participant
        // tagging factories. Console + cycle log providers live here, so any logger created
        // through this factory (or a TaggingLoggerFactory wrapping it) writes to both sinks.
        var sharedLoggerFactory = LoggerFactory.Create(b =>
        {
            b.AddConsole();
            b.AddProvider(cycleLoggerProvider);
        });

        builder.Services.Configure<ConnectorTesterConfiguration>(
            builder.Configuration.GetSection("ConnectorTester"));

        var coordinator = new TestCycleCoordinator();
        builder.Services.AddSingleton(coordinator);

        var configuration = builder.Configuration
            .GetSection("ConnectorTester")
            .Get<ConnectorTesterConfiguration>() ?? new ConnectorTesterConfiguration();

        // Assign stable participant indices from config position.
        configuration.Server.Index = 0;
        for (var i = 0; i < configuration.Clients.Count; i++)
        {
            configuration.Clients[i].Index = i + 1;
        }

        configuration.Server.Chaos?.Validate();
        foreach (var client in configuration.Clients)
        {
            client.Chaos?.Validate();
        }

        var runModeSelection = RunModeSelector.Select(args, configuration);
        var connectorFactory = ConnectorFactoryRegistry.Resolve(configuration.ConnectorKind);

        var participants = new Dictionary<string, TestNode>();
        var mutationEngines = new List<MutationEngine>();
        var chaosEngines = new List<ChaosEngine>();
        var participantBundles = new List<ParticipantHostBundle>();

        var participantConfigurations = EnumerateActiveParticipants(configuration, runModeSelection).ToList();

        foreach (var (participantConfiguration, isServer) in participantConfigurations)
        {
            // Each participant owns a dedicated IServiceCollection / IServiceProvider so the
            // library's ILogger<T> resolution returns a participant-tagged logger. Mutation,
            // chaos, and verification engines stay in the main DI: they already create their
            // loggers with explicit per-participant categories (e.g. "MutationEngine/client").
            var participantServices = new ServiceCollection();
            participantServices.AddSingleton<ILoggerFactory>(
                new TaggingLoggerFactory(sharedLoggerFactory, participantConfiguration.Name));
            participantServices.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking()
                .WithRegistry()
                .WithLifecycle()
                .WithTransactions()
                .WithSourceTransactions()
                .WithHostedServices(participantServices);

            var root = TestNode.CreateWithGraph(context, configuration.CollectionCount, configuration.DictionaryCount);
            participants[participantConfiguration.Name] = root;

            if (isServer)
            {
                connectorFactory.RegisterServer(participantServices, root, connectorFactory.DefaultPort);
            }
            else
            {
                connectorFactory.RegisterClient(participantServices, root, participantConfiguration, connectorFactory.DefaultPort);
            }

            // PerformanceProfiler lives in the participant SP so its StopAsync runs before
            // the SP is disposed (which would invalidate the property-change subscription).
            participantServices.AddSingleton<IHostedService>(_ =>
                new PerformanceProfiler(context, participantConfiguration.Name,
                    configuration.MetricsReportingInterval, runDirectory, coordinator));

            var participantServiceProvider = participantServices.BuildServiceProvider();
            var bundle = new ParticipantHostBundle(participantConfiguration.Name, participantServiceProvider);
            participantBundles.Add(bundle);
            builder.Services.AddSingleton<IHostedService>(bundle);

            // Mutation engine stays in the main host so it shares the verification coordinator
            // graph; its logger category already encodes the participant name explicitly.
            var mutationLogger = sharedLoggerFactory.CreateLogger($"MutationEngine/{participantConfiguration.Name}");
            var mutationEngine = configuration.NumberOfBatches > 0
                ? MutationEngine.CreateBatch(root, participantConfiguration, coordinator, mutationLogger,
                    configuration.NumberOfBatches, participantConfiguration.Index)
                : MutationEngine.CreateRandom(root, participantConfiguration, coordinator, mutationLogger);
            mutationEngines.Add(mutationEngine);
            builder.Services.AddSingleton<IHostedService>(mutationEngine);
        }

        // Populated after host.Build() to avoid a resolution cycle: ChaosEngine (IHostedService)
        // depends on IFaultTargetResolver, and the resolver's input is IEnumerable<ISubjectConnector>
        // which lives on the IHostedService graph. Resolving connectors from inside the factory
        // would re-enter the ChaosEngine factory.
        var faultTargetResolver = new FaultTargetResolver();
        builder.Services.AddSingleton<IFaultTargetResolver>(faultTargetResolver);

        foreach (var (participantConfiguration, _) in participantConfigurations)
        {
            if (participantConfiguration.Chaos == null)
            {
                continue;
            }

            var chaosLogger = sharedLoggerFactory.CreateLogger($"ChaosEngine/{participantConfiguration.Name}");
            var capturedConfiguration = participantConfiguration;
            builder.Services.AddSingleton<IHostedService>(serviceProvider =>
            {
                var resolver = serviceProvider.GetRequiredService<IFaultTargetResolver>();
                var engine = new ChaosEngine(
                    capturedConfiguration.Name,
                    capturedConfiguration.Chaos!,
                    coordinator,
                    resolver,
                    chaosLogger);
                chaosEngines.Add(engine);
                return engine;
            });
        }

        VerificationEngine? verificationEngine = null;
        if (runModeSelection.Mode == RunMode.Verify)
        {
            builder.Services.AddSingleton(serviceProvider =>
            {
                verificationEngine = new VerificationEngine(
                    configuration,
                    coordinator,
                    participants,
                    mutationEngines,
                    chaosEngines,
                    cycleLoggerProvider,
                    serviceProvider.GetRequiredService<IHostApplicationLifetime>(),
                    serviceProvider.GetRequiredService<ILogger<VerificationEngine>>(),
                    runDirectory);
                return verificationEngine;
            });
            builder.Services.AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredService<VerificationEngine>());
        }

        var host = builder.Build();

        // Each ParticipantHostBundle already eagerly resolved its hosted services in its
        // constructor, so the connectors are constructed exactly once. Flatten them here for
        // the fault-target resolver.
        var allConnectors = participantBundles.SelectMany(b => b.Connectors).ToList();
        faultTargetResolver.Bind(participants, allConnectors);

        if (runModeSelection.Mode == RunMode.Verify)
        {
            verificationEngine = host.Services.GetRequiredService<VerificationEngine>();
        }

        return new ConnectorTesterHost(host, verificationEngine, runModeSelection, runDirectory);
    }

    private static IEnumerable<(ParticipantConfiguration Configuration, bool IsServer)> EnumerateActiveParticipants(
        ConnectorTesterConfiguration configuration,
        RunModeSelection runMode)
    {
        if (runMode.Mode == RunMode.Verify || runMode.ParticipantName == configuration.Server.Name)
        {
            yield return (configuration.Server, IsServer: true);
        }

        foreach (var client in configuration.Clients)
        {
            if (runMode.Mode == RunMode.Verify || runMode.ParticipantName == client.Name)
            {
                yield return (client, IsServer: false);
            }
        }
    }
}
