using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// Measures the cost of committing a transaction that writes a batch of property changes.
/// The <see cref="Mode"/> parameter selects a local-only commit, a commit where every change is
/// bound to a single source, or one spread across multiple sources (the grouped fallback path).
/// </summary>
[MemoryDiagnoser]
public class SubjectTransactionBenchmark
{
    private const int PropertyCount = 50;
    private const int SourceCount = 2;

    private IInterceptorSubjectContext _context;
    private Car _car;
    private Tire[] _tires;
    private int _counter;

    public enum CommitMode
    {
        Local,
        SingleSource,
        SingleSourceWithLocal,
        MultiSource
    }

    [Params(CommitMode.Local, CommitMode.SingleSource, CommitMode.SingleSourceWithLocal, CommitMode.MultiSource)]
    public CommitMode Mode;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithTransactions();

        if (Mode != CommitMode.Local)
        {
            context.WithSourceTransactions();
        }

        _context = context;
        _car = new Car(_context);

        _tires = Enumerable.Range(0, PropertyCount).Select(_ => new Tire()).ToArray();
        _car.Tires = _tires;

        if (Mode == CommitMode.SingleSource)
        {
            var source = new BenchmarkSource(_car);
            foreach (var tire in _tires)
            {
                new PropertyReference(tire, nameof(Tire.Pressure_Minimum)).SetSource(source);
            }
        }
        else if (Mode == CommitMode.SingleSourceWithLocal)
        {
            // Half the changes target one source, half are local (no source) - exercises the
            // single-source path's mixed branch where source-bound and local changes are separated.
            var source = new BenchmarkSource(_car);
            for (var i = 0; i < _tires.Length; i++)
            {
                if (i % 2 == 0)
                {
                    new PropertyReference(_tires[i], nameof(Tire.Pressure_Minimum)).SetSource(source);
                }
            }
        }
        else if (Mode == CommitMode.MultiSource)
        {
            var sources = Enumerable.Range(0, SourceCount).Select(_ => new BenchmarkSource(_car)).ToArray();
            for (var i = 0; i < _tires.Length; i++)
            {
                new PropertyReference(_tires[i], nameof(Tire.Pressure_Minimum)).SetSource(sources[i % SourceCount]);
            }
        }
    }

    [Benchmark]
    public async Task CommitChanges()
    {
        var value = Interlocked.Increment(ref _counter);

        using var transaction = await _context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        for (var i = 0; i < _tires.Length; i++)
        {
            _tires[i].Pressure_Minimum = value + i;
        }

        await transaction.CommitAsync(CancellationToken.None);
    }

    private sealed class BenchmarkSource : ISubjectSource
    {
        public BenchmarkSource(IInterceptorSubject rootSubject)
        {
            RootSubject = rootSubject;
        }

        public IInterceptorSubject RootSubject { get; }

        public int WriteBatchSize => 0;

        public ValueTask<WriteResult> WriteChangesAsync(
            ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        {
            return new ValueTask<WriteResult>(WriteResult.Success);
        }

        public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<Action?>(null);
        }

        public SourceState State => SourceState.Synchronizing;

        public DateTimeOffset StateChangeTime { get; } = DateTimeOffset.UtcNow;

        public DateTimeOffset? LastSynchronizedAt => null;

        public SourceDiagnostics Diagnostics { get; } = new(new SourceMetrics());

        ConnectorDiagnostics ISubjectConnector.Diagnostics => Diagnostics;

        public event EventHandler<SourceEvent>? StateChanged
        {
            add { }
            remove { }
        }
    }
}
