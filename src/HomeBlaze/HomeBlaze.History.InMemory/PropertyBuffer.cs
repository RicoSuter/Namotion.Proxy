using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.InMemory;

internal sealed class PropertyBuffer
{
    private readonly Lock _lock = new();
    // Grown on demand rather than allocated at full size. A path that only ever holds a handful of
    // samples used to cost the whole configured capacity from its first change, and nothing ever
    // reclaims a path: a collection reorder renames every subject after the removed index, so each
    // reorder mints a fresh set of buffers and abandons the old ones. Growing, and shrinking again
    // once a sweep empties the ring, makes an abandoned path cost a few hundred bytes instead of the
    // full array.
    private Sample[] _items;
    private readonly int _maxCapacity;

    private const int InitialCapacity = 4;
    private int _start;   // index of the oldest sample
    private int _count;
    private long _evictedCount;
    private long _retainedValueBytes;

    // Set under _lock when the store retires this buffer, so an append that raced the retirement is
    // refused rather than landing in a buffer nobody can read any more.
    private bool _isRetired;

    public PropertyBuffer(int capacity, ValueColumn column, bool isUlong)
    {
        _maxCapacity = Math.Max(1, capacity);
        _items = new Sample[Math.Min(InitialCapacity, _maxCapacity)];
        Column = column;
        IsUlong = isUlong;
    }

    public ValueColumn Column { get; }

    public bool IsUlong { get; }

    // The ring's current allocation, which is what the buffer actually costs right now.
    public int Capacity
    {
        get { lock (_lock) { return _items.Length; } }
    }

    /// <summary>The configured ceiling; the ring never grows past it and evicts instead.</summary>
    public int MaxCapacity => _maxCapacity;

    // Heap held by the retained samples' JSON values, which live outside the Sample struct and so are
    // invisible to a per-sample size estimate. Maintained incrementally: recomputing it would mean
    // re-serializing every retained value on every metrics refresh.
    public long RetainedValueBytes
    {
        get { lock (_lock) { return _retainedValueBytes; } }
    }

    public long EvictedCount
    {
        get { lock (_lock) { return _evictedCount; } }
    }

    public int Count
    {
        get { lock (_lock) { return _count; } }
    }

    public Sample? Oldest
    {
        get { lock (_lock) { return _count == 0 ? null : _items[_start]; } }
    }

    // Timestamp of the oldest retained sample, or null when empty. Cheaper than reading Oldest when
    // only the timestamp is needed (no Sample copy).
    public DateTimeOffset? OldestTimestamp
    {
        get { lock (_lock) { return _count == 0 ? null : _items[_start].Timestamp; } }
    }

    public Sample? Newest
    {
        get { lock (_lock) { return _count == 0 ? null : _items[Index(_count - 1)]; } }
    }

    // Appends (or replaces at an identical timestamp) and reports whether a sample was evicted to make
    // room. The resulting oldest retained timestamp comes out under the same lock: reading it afterwards
    // let a concurrent age sweep run in between, so the store latched its coverage floor at the sweep's
    // cutoff instead of this eviction's boundary and under-claimed the samples still held.
    public bool Append(Sample sample) => Append(sample, out _);

    /// <inheritdoc cref="Append(Sample)"/>
    public bool Append(Sample sample, out DateTimeOffset? oldestRetained)
    {
        TryAppend(sample, out var evicted, out oldestRetained);
        return evicted;
    }

    /// <summary>
    /// Appends unless this buffer has been retired, in which case the caller must take a fresh one and
    /// retry. Retirement is checked under the same lock as the append, so a sample can never land in a
    /// buffer the store has already dropped: losing it there would be invisible, and the in-memory
    /// store claims the live edge at the highest priority, so the merger would serve the gap from here
    /// rather than falling back to a durable store.
    /// </summary>
    public bool TryAppend(Sample sample, out bool evicted, out DateTimeOffset? oldestRetained)
    {
        lock (_lock)
        {
            if (_isRetired)
            {
                evicted = false;
                oldestRetained = null;
                return false;
            }

            evicted = AppendCore(sample);
            oldestRetained = _count == 0 ? null : _items[_start].Timestamp;
            return true;
        }
    }

    /// <summary>
    /// Retires the buffer if it holds nothing, reporting the evictions it accumulated so the store can
    /// keep its cumulative count monotonic. Returns false when a sample arrived after the sweep
    /// emptied it, which leaves the buffer in service.
    /// </summary>
    public bool TryRetire(out long evictedCount)
    {
        lock (_lock)
        {
            evictedCount = 0;
            if (_isRetired || _count > 0)
            {
                return false;
            }

            _isRetired = true;

            // Handed over rather than copied. Folding a copy into the store while the buffer is still
            // in the dictionary double counts, and folding after removal leaves a window where the
            // count is in neither, which is how the total went backwards. Transferring under the same
            // lock that retires means a reader sees it exactly once, wherever it looks.
            evictedCount = _evictedCount;
            _evictedCount = 0;
            return true;
        }
    }

    private bool AppendCore(Sample sample)
    {
        if (_count > 0)
        {
            var newestIndex = Index(_count - 1);
            var newestTimestamp = _items[newestIndex].Timestamp;
            if (sample.Timestamp < newestTimestamp)
            {
                return InsertOrdered(sample);
            }

            if (sample.Timestamp == newestTimestamp)
            {
                Replace(newestIndex, sample);
                return false;
            }
        }

        var capacityEvicted = _count == _maxCapacity;
        if (capacityEvicted)
        {
            DropOldest(); // overwrite oldest
            _evictedCount++;
        }
        else
        {
            GrowIfFull();
        }

        Store(Index(_count), sample);
        _count++;
        return capacityEvicted;
    }

    public int EvictOlderThan(DateTimeOffset cutoff)
    {
        lock (_lock)
        {
            var dropped = 0;
            while (_count > 0 && _items[_start].Timestamp < cutoff)
            {
                DropOldest();
                dropped++;
            }

            _evictedCount += dropped;
            if (_count == 0 && _items.Length > InitialCapacity)
            {
                _items = new Sample[Math.Min(InitialCapacity, _maxCapacity)];
                _start = 0;
            }

            return dropped;
        }
    }

    // Doubles the ring, re-linearizing it so the logical order starts at index 0. Only called with
    // room left below the ceiling, so it never has to drop a sample.
    private void GrowIfFull()
    {
        if (_count < _items.Length)
        {
            return;
        }

        var grown = new Sample[Math.Min(_maxCapacity, Math.Max(InitialCapacity, _items.Length * 2))];
        for (var logical = 0; logical < _count; logical++)
        {
            grown[logical] = _items[Index(logical)];
        }

        _items = grown;
        _start = 0;
    }

    private void Store(int index, Sample sample)
    {
        _items[index] = sample;
        _retainedValueBytes += sample.ValueBytes;
    }

    private void Replace(int index, Sample sample)
    {
        _retainedValueBytes -= _items[index].ValueBytes;
        Store(index, sample);
    }

    private void DropOldest()
    {
        _retainedValueBytes -= _items[_start].ValueBytes;
        _items[_start] = default; // release the JsonDocument the evicted sample was holding
        _start = (_start + 1) % _items.Length;
        _count--;
    }

    public List<Sample> Range(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            var result = new List<Sample>();
            var lower = LowerBound(from);             // first index with Timestamp >= from
            for (var logical = lower; logical < _count; logical++)
            {
                var sample = _items[Index(logical)];
                if (sample.Timestamp >= to)
                {
                    break;
                }

                result.Add(sample);
            }

            return result;
        }
    }

    public Sample? AtOrBefore(DateTimeOffset asOf)
    {
        lock (_lock)
        {
            var upper = UpperBound(asOf);             // count of samples with Timestamp <= asOf
            return upper == 0 ? null : _items[Index(upper - 1)];
        }
    }

    private int Index(int logical) => (_start + logical) % _items.Length;

    // first logical index whose Timestamp >= target (binary search over the logical order)
    private int LowerBound(DateTimeOffset target)
    {
        var low = 0;
        var high = _count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_items[Index(mid)].Timestamp < target)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    // count of logical samples whose Timestamp <= target
    private int UpperBound(DateTimeOffset target)
    {
        var low = 0;
        var high = _count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_items[Index(mid)].Timestamp <= target)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private bool InsertOrdered(Sample sample)
    {
        // Rare late-arrival path. Keep the newest _maxCapacity timestamps, matching the ring's
        // normal in-order behavior. A late sample older than everything retained is discarded
        // rather than evicting a newer retained sample to make room for it.
        var position = LowerBound(sample.Timestamp); // first index with Timestamp >= sample.Timestamp
        if (position < _count && _items[Index(position)].Timestamp == sample.Timestamp)
        {
            Replace(Index(position), sample); // same (path, timestamp) replaces, matching SQLite
            return false;
        }

        var capacityEvicted = _count == _maxCapacity;
        if (capacityEvicted)
        {
            _evictedCount++;
            if (position == 0)
            {
                return true; // the late sample itself is the one dropped
            }

            DropOldest();
            position--;
        }
        else
        {
            GrowIfFull();
        }

        for (var logical = _count; logical > position; logical--)
        {
            _items[Index(logical)] = _items[Index(logical - 1)];
        }

        Store(Index(position), sample);
        _count++;
        return capacityEvicted;
    }
}
