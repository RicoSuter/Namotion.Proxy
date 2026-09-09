using Namotion.Interceptor.Registry;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Reconciliation;

// Experimental conservative reader strategy. Notifications request verification; they never certify freshness.
internal sealed class SourcePropertyReconciler : IDisposable
{
    private readonly SubjectSourceBase _source;
    private readonly ISourcePropertyReader _reader;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<PropertyReference, Entry> _entries = new(PropertyReference.Comparer);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _cancellation = new();
    private readonly PropertyChangeQueueSubscription _changes;
    private readonly Task _worker;
    private readonly Task _observer;
    private volatile bool _running;
    private volatile bool _disposed;
    private int _disposeStarted;
    private long _epoch;
    private long _readCount;

    public SourcePropertyReconciler(SubjectSourceBase source, ISourcePropertyReader reader, ILogger logger)
    {
        _source = source;
        _reader = reader;
        _logger = logger;
        _changes = source.RootSubject.Context.CreatePropertyChangeQueueSubscription();
        // Neither background flow may inherit a caller's ambient transaction.
        using (ExecutionContext.SuppressFlow())
        {
            _worker = Task.Run(RunAsync);
            _observer = Task.Factory.StartNew(ObserveChanges, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    public long ReadCount => Interlocked.Read(ref _readCount);
    public bool HasPending => _changes.Count != 0 || _entries.Values.Any(entry => { lock (entry.Gate) return entry.Dirty || entry.InFlight || entry.Operations != 0; });

    public void Register(PropertyReference property)
    {
        if (property.TryGetRegisteredProperty()?.CanContainSubjects == true) return;
        // Called while ownership is established under the same subject lock as property commits.
        property.TryGetWriteState(false, out var revision, out _);
        _entries.TryAdd(property, new Entry(property, revision, Interlocked.Read(ref _epoch)));
    }

    public void Unregister(PropertyReference property)
    {
        if (!_entries.TryRemove(property, out var entry)) return;
        lock (entry.Gate)
        {
            entry.Version++;
            entry.Dirty = false;
        }
    }

    public void Observe(PropertyReference property)
    {
        if (_disposed || property.TryGetRegisteredProperty()?.CanContainSubjects == true) return;
        if (!_entries.TryGetValue(property, out var entry)) return;
        lock (entry.Gate)
        {
            entry.Version++;
            entry.Dirty = true;
        }
        Signal();
    }

    public void RequestRefresh(PropertyReference property)
    {
        if (_disposed || property.TryGetRegisteredProperty()?.CanContainSubjects == true) return;
        if (!_entries.TryGetValue(property, out var entry)) return;
        lock (entry.Gate)
        {
            entry.Dirty = true;
            if (entry.InFlight) entry.RefreshAfterRead = true;
        }
        Signal();
    }

    public void Resume()
    {
        _running = true;
        Signal();
    }

    public void Invalidate()
    {
        _running = false;
        var epoch = Interlocked.Increment(ref _epoch);
        foreach (var entry in _entries.Values)
        {
            lock (entry.Gate)
            {
                entry.Epoch = Math.Max(entry.Epoch, epoch);
                entry.Version++;
                entry.Dirty = true;
            }
        }
    }

    public IDisposable BeginOperation(ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        var entries = new List<(Entry Entry, long Revision)>(changes.Length);
        foreach (var change in changes.Span)
        {
            if (change.Property.TryGetRegisteredProperty()?.CanContainSubjects == true) continue;
            if (!change.Property.TryGetSource(out var owner) || !ReferenceEquals(owner, _source)) continue;
            if (!_entries.TryGetValue(change.Property, out var entry)) continue;
            lock (entry.Gate)
            {
                entry.Operations++;
                entry.Version++;
                entry.Dirty = true;
            }
            entries.Add((entry, change.Revision));
        }
        return new Operation(this, entries);
    }

    private void ObserveChanges()
    {
        try
        {
            while (_changes.TryDequeue(out var change, _cancellation.Token))
            {
                ProcessChange(change);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
    }

    internal void ProcessChange(SubjectPropertyChange change)
    {
        if (change.Property.TryGetRegisteredProperty()?.CanContainSubjects == true) return;
        if (!change.Property.TryGetSource(out var owner) || !ReferenceEquals(owner, _source)) return;
        if (change.Origin.Kind == ChangeOriginKind.FromSource && ReferenceEquals(change.Origin.Source, _source)) return;
        if (!_entries.TryGetValue(change.Property, out var entry)) return;
        lock (entry.Gate)
        {
            if (change.Revision <= entry.CoveredRevision) return;
            // Confirmed is stamped by local transaction application after its source accepted the write.
            if (change.Origin.Kind == ChangeOriginKind.Confirmed && ReferenceEquals(change.Origin.Source, _source))
                entry.CoveredRevision = Math.Max(entry.CoveredRevision, change.Revision);
            entry.Dirty = true;
        }
        Signal();
    }

    private void Signal() => _wake.Writer.TryWrite(true);

    private async Task RunAsync()
    {
        try
        {
            await foreach (var unused in _wake.Reader.ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
            {
                if (!_running || _disposed) continue;
                var tickets = new List<ReadTicket>();
                foreach (var entry in _entries.Values)
                {
                    lock (entry.Gate)
                    {
                        if (!entry.Dirty || entry.InFlight || entry.Operations != 0) continue;
                        if (!entry.Property.TryGetSource(out var owner) || !ReferenceEquals(owner, _source))
                        {
                            entry.Dirty = false;
                            continue;
                        }
                        entry.Property.TryGetWriteState(false, out var revision, out _);
                        if (revision > entry.CoveredRevision) continue;
                        entry.InFlight = true;
                        entry.RefreshAfterRead = false;
                        tickets.Add(new ReadTicket(entry, revision, entry.Version, entry.Epoch));
                    }
                }
                if (tickets.Count == 0) continue;
                var retry = false;
                try
                {
                    Interlocked.Increment(ref _readCount);
                    var properties = tickets.Select(ticket => ticket.Entry.Property).ToArray();
                    var values = await _reader.ReadPropertiesAsync(properties, _cancellation.Token).ConfigureAwait(false);
                    var byProperty = values.ToDictionary(value => value.Property, PropertyReference.Comparer);
                    foreach (var ticket in tickets)
                    {
                        if (!byProperty.TryGetValue(ticket.Entry.Property, out var value)) { retry = true; continue; }
                        var guard = new ReadGuard(this, ticket);
                        try
                        {
                            var unchanged = false;
                            lock (value.Property.Subject.SyncRoot)
                            {
                                if (Equals(value.Property.Metadata.GetValue?.Invoke(value.Property.Subject), value.Value) && guard.TryEnter(value.Property))
                                {
                                    guard.Exit(true);
                                    unchanged = true;
                                }
                            }
                            if (!unchanged) value.Property.SetValueFromSource(_source, value.Timestamp, value.ReceivedTimestamp, value.Value, guard);
                        }
                        catch (Exception exception)
                        {
                            _logger.LogWarning(exception, "Experimental reconciliation apply failed for {Property}.", value.Property.Name);
                        }
                        if (!guard.Committed) retry = true;
                    }
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    retry = true;
                    _logger.LogWarning(exception, "Experimental source verification failed; pending values will be retried.");
                }
                finally
                {
                    foreach (var ticket in tickets)
                    {
                        lock (ticket.Entry.Gate)
                        {
                            ticket.Entry.InFlight = false;
                            if (ticket.Entry.Dirty) retry = true;
                        }
                    }
                }
                if (retry)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), _cancellation.Token).ConfigureAwait(false);
                    Signal();
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _disposed = true;
        Invalidate();
        _cancellation.Cancel();
        _changes.Dispose();
        _wake.Writer.TryComplete();
        _ = Task.WhenAll(_worker, _observer).ContinueWith(_ => _cancellation.Dispose(), TaskScheduler.Default);
    }

    private sealed class Entry(PropertyReference property, long coveredRevision, long epoch)
    {
        public readonly object Gate = new();
        public readonly PropertyReference Property = property;
        public long CoveredRevision = coveredRevision;
        public long Epoch = epoch;
        public long Version;
        public int Operations;
        public bool Dirty;
        public bool InFlight;
        public bool RefreshAfterRead;
    }

    private readonly record struct ReadTicket(Entry Entry, long Revision, long Version, long Epoch);

    private sealed class ReadGuard(SourcePropertyReconciler owner, ReadTicket ticket) : IPropertyWriteGuard
    {
        public bool Committed { get; private set; }
        public bool TryEnter(PropertyReference property)
        {
            Monitor.Enter(ticket.Entry.Gate);
            property.TryGetWriteState(false, out var revision, out _);
            if (owner._disposed || !owner._running || !owner._entries.TryGetValue(property, out var currentEntry) ||
                !ReferenceEquals(currentEntry, ticket.Entry) || Interlocked.Read(ref owner._epoch) != ticket.Epoch || ticket.Entry.Epoch != ticket.Epoch ||
                ticket.Entry.Version != ticket.Version || ticket.Entry.Operations != 0 || revision != ticket.Revision ||
                !property.TryGetSource(out var source) || !ReferenceEquals(source, owner._source))
            {
                Monitor.Exit(ticket.Entry.Gate);
                return false;
            }
            return true;
        }
        public void Exit(bool committed)
        {
            Committed = committed;
            if (committed) ticket.Entry.Dirty = ticket.Entry.RefreshAfterRead;
            Monitor.Exit(ticket.Entry.Gate);
        }
    }

    private sealed class Operation(SourcePropertyReconciler owner, List<(Entry Entry, long Revision)> entries) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var (entry, revision) in entries)
            {
                lock (entry.Gate)
                {
                    entry.CoveredRevision = Math.Max(entry.CoveredRevision, revision);
                    entry.Operations--;
                    entry.Dirty = true;
                }
            }
            owner.Signal();
        }
    }
}
