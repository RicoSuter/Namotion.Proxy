using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

[MemoryDiagnoser]
public class SubjectSourceBenchmark
{
    private TestSubjectSource _source;
    private IInterceptorSubjectContext _context;
    private CancellationTokenSource _cts;
    private Car _car;
    private string[] _propertyNames;

    private readonly AutoResetEvent _signal = new(false);
    private Action<object?>[] _updates;
    private SubjectPropertyWriter _propertyWriter;
    private long _stubRevision;

    [GlobalSetup]
    public async Task Setup()
    {
        _context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        _propertyNames = Enumerable
            .Range(1, 5000)
            .Select(i => $"Name{i}")
            .ToArray();

        _car = new Car(_context);
        _source = new TestSubjectSource(
            _car,
            _context,
            NullLogger.Instance,
            _propertyNames.Length,
            bufferTime: TimeSpan.FromMilliseconds(1),
            retryTime: TimeSpan.FromSeconds(1));

        var registeredSubject = _car.TryGetRegisteredSubject()!;
        foreach (var name in _propertyNames)
        {
            // Closure-backed so the getter returns what the setter stored, the way the OPC UA loader
            // registers dynamic properties. A constant getter with a no-op setter measures a write path
            // where nothing is ever stored, which is not the path production takes.
            object? value = null;
            var property = registeredSubject.AddProperty(name, typeof(string), _ => value, (_, newValue) => value = newValue);
            property.Reference.SetSource(_source);
        }

        _cts = new CancellationTokenSource();
        await _source.StartAsync(_cts.Token);
        _source.WaitForInitialization();

        _propertyWriter = _source.PropertyWriter!;

        _updates = Enumerable
            .Range(1, 1000000)
            .Select(c => c < 1000000
                ? new Action<object?>(static _ => { })
                : _ =>
                {
                    _signal.Set();
                })
            .ToArray();
    }

    [Benchmark]
    public void WriteToRegistrySubjects()
    {
        for (var i = 0; i < _updates.Length; i++)
        {
            _propertyWriter.Write(null, _updates[i]);
        }

        if (!_signal.WaitOne(TimeSpan.FromSeconds(30)))
        {
            throw new InvalidOperationException(
                "Timed out waiting for writes to reach the source: the connector delivered nothing.");
        }
    }

    [Benchmark]
    public void WriteToSource()
    {
        _source.Reset();

        var queue = _context.GetService<PropertyChangeInterceptor>();
        for (var i = 0; i < _propertyNames.Length; i++)
        {
            // The executor the chain would thread through; this benchmark stops at the stub terminal
            // below, so it is only carried, never used.
            var context = new PropertyWriteContext<int>(
                (InterceptorExecutor)((IInterceptorSubject)_car).Executor,
                new PropertyReference(_car, _propertyNames[i]),
                0,
                i);

            // The stub next models the terminal, which sets IsWritten and stamps a commit revision when
            // the value is stored. Stamping matters: a change carrying revision 0 short-circuits the
            // delivered-revision filter, so leaving it unstamped would measure the merge while skipping
            // the per-survivor supersession check that production always pays.
            queue.WriteProperty(ref context, (ref PropertyWriteContext<int> c) =>
            {
                c.IsWritten = true;
                c.Revision = ++_stubRevision;
            });
        }

        _source.Wait();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _cts.CancelAsync();
        await _source.StopAsync(CancellationToken.None);
        _cts.Dispose();
        _source.Dispose();
    }

    private class TestSubjectSource : SubjectSourceBase
    {
        private readonly IInterceptorSubject _subject;
        private readonly int _targetCount;
        private readonly AutoResetEvent _signal = new(false);
        private readonly ManualResetEventSlim _initialized = new(false);
        private SubjectPropertyWriter? _propertyWriter;
        private int _count;

        public TestSubjectSource(
            IInterceptorSubject subject,
            IInterceptorSubjectContext context,
            ILogger logger,
            int targetCount,
            TimeSpan? bufferTime = null,
            TimeSpan? retryTime = null)
            : base(context, logger, bufferTime, retryTime, writeRetryQueueSize: 0)
        {
            _subject = subject;
            _targetCount = targetCount;
        }

        public override IInterceptorSubject RootSubject => _subject;

        public override int WriteBatchSize => int.MaxValue;

        internal SubjectPropertyWriter? PropertyWriter => _propertyWriter;

        public void Reset()
        {
            _count = 0;
        }

        public void Wait()
        {
            if (!_signal.WaitOne(TimeSpan.FromSeconds(30)))
            {
                throw new InvalidOperationException(
                    "Timed out waiting for writes to reach the source: the connector delivered nothing.");
            }
        }

        protected override Task<IAsyncDisposable?> StartListeningAsync(
            SubjectPropertyWriter propertyWriter,
            CancellationToken cancellationToken)
        {
            _propertyWriter = propertyWriter;
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        public override Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        {
            _initialized.Set();
            return Task.FromResult<Action?>(null);
        }

        public void WaitForInitialization() => _initialized.Wait();

        public override ValueTask<WriteResult> WriteChangesAsync(
            ReadOnlyMemory<SubjectPropertyChange> changes,
            CancellationToken cancellationToken)
        {
            _count += changes.Length;

            if (_count >= _targetCount)
            {
                _signal.Set();
            }

            return new ValueTask<WriteResult>(WriteResult.Success);
        }
    }
}
