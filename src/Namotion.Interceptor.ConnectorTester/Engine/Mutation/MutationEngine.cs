using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Engine.Mutation;

/// <summary>
/// BackgroundService that drives one participant's value-mutation strategy and an
/// optional structural-mutation loop. Composes <see cref="KnownNodeGraph"/> +
/// <see cref="IValueMutationStrategy"/> + <see cref="StructuralMutator"/> +
/// <see cref="MutationCounters"/>. Honors <see cref="TestCycleCoordinator"/> pauses.
/// </summary>
public sealed class MutationEngine : BackgroundService
{
    private const int RebuildEveryStructuralMutations = 10;

    private readonly TestNode _root;
    private readonly KnownNodeGraph _graph;
    private readonly StructuralMutator _structuralMutator;
    private readonly IValueMutationStrategy _valueStrategy;
    private readonly TestCycleCoordinator _coordinator;
    private readonly MutationCounters _counters;
    private readonly ILogger _logger;
    private readonly int _structuralMutationRate;

    public string Name { get; }
    public int ValueMutationRate { get; }
    public int StructuralMutationRate => _structuralMutationRate;
    public long ValueMutationCount => _counters.ValueMutationCount;
    public long StructuralMutationCount => _counters.StructuralMutationCount;

    public void ResetCounters() => _counters.Reset();

    public MutationEngine(
        TestNode root,
        ParticipantConfiguration participantConfiguration,
        TestCycleCoordinator coordinator,
        IValueMutationStrategy valueStrategy,
        KnownNodeGraph graph,
        MutationCounters counters,
        ILogger logger)
    {
        _root = root;
        _graph = graph;
        _structuralMutator = new StructuralMutator(_graph);
        _valueStrategy = valueStrategy;
        _coordinator = coordinator;
        _counters = counters;
        _logger = logger;
        Name = participantConfiguration.Name;
        ValueMutationRate = participantConfiguration.ValueMutationRate;
        _structuralMutationRate = participantConfiguration.StructuralMutationRate;
    }

    public static MutationEngine CreateRandom(
        TestNode root,
        ParticipantConfiguration participantConfiguration,
        TestCycleCoordinator coordinator,
        ILogger logger)
    {
        var graph = new KnownNodeGraph();
        var counters = new MutationCounters();
        var context = ((IInterceptorSubject)root).GetContext();
        var strategy = new RandomValueMutationStrategy(graph, coordinator, context, counters, participantConfiguration);
        return new MutationEngine(root, participantConfiguration, coordinator, strategy, graph, counters, logger);
    }

    public static MutationEngine CreateBatch(
        TestNode root,
        ParticipantConfiguration participantConfiguration,
        TestCycleCoordinator coordinator,
        ILogger logger,
        int numberOfBatches,
        int participantIndex)
    {
        var graph = new KnownNodeGraph();
        var counters = new MutationCounters();
        var context = ((IInterceptorSubject)root).GetContext();
        var strategy = new BatchValueMutationStrategy(graph, coordinator, context, counters, participantConfiguration, numberOfBatches, participantIndex);
        return new MutationEngine(root, participantConfiguration, coordinator, strategy, graph, counters, logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "MutationEngine [{Name}] started at {Rate} value mutations/sec, {StructuralRate} structural mutations/sec",
            Name, ValueMutationRate, _structuralMutationRate);

        _graph.Rebuild(_root);

        var tasks = new List<Task> { _valueStrategy.RunAsync(stoppingToken) };

        if (_structuralMutationRate > 0)
        {
            tasks.Add(RunStructuralLoopAsync(stoppingToken));
        }

        await Task.WhenAll(tasks);
    }

    private async Task RunStructuralLoopAsync(CancellationToken cancellationToken)
    {
        var (batchSize, delayMs) = TickPlan.From(_structuralMutationRate);
        var rebuildCounter = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _coordinator.WaitIfPaused(cancellationToken);

                for (var i = 0; i < batchSize; i++)
                {
                    _structuralMutator.PerformMutation();
                    _counters.IncrementStructural();
                }

                rebuildCounter += batchSize;
                if (rebuildCounter >= RebuildEveryStructuralMutations)
                {
                    _graph.Rebuild(_root);
                    rebuildCounter = 0;
                }

                await Task.Delay(delayMs, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Structural mutation failed, continuing");
            }
        }
    }
}
