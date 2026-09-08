namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>Tracks the installed occurrences of a property while its desired baseline can run ahead.</summary>
internal sealed class PropertyEdgeJournal
{
    private struct Entry
    {
        public IInterceptorSubject? Subject;
        public object? Index;
        public int Previous;
        public int Next;
        public int PreviousForSubject;
    }

    private readonly List<Entry> _entries = [];
    private readonly Dictionary<IInterceptorSubject, int> _lastForSubject = new(ReferenceEqualityComparer.Instance);
    private int _first = -1;
    private int _last = -1;
    private int _free = -1;

    public PropertyReference Property { get; private set; }
    public SubjectOwnership Ownership { get; private set; } = null!;
    public int Users { get; set; }
    public bool IsComplete { get; set; }

    public void Initialize(PropertyReference property, SubjectOwnership ownership, List<SubjectOccurrence> installed)
    {
        Property = property;
        Ownership = ownership;
        Users = 1;
        foreach (var occurrence in installed) Add(occurrence.Subject, occurrence.Index);
    }

    public void Add(IInterceptorSubject subject, object? index)
    {
        var entryIndex = _free;
        if (entryIndex >= 0)
        {
            _free = _entries[entryIndex].Next;
        }
        else
        {
            entryIndex = _entries.Count;
            _entries.Add(default);
        }

        _entries[entryIndex] = new Entry
        {
            Subject = subject,
            Index = index,
            Previous = _last,
            Next = -1,
            PreviousForSubject = _lastForSubject.GetValueOrDefault(subject, -1)
        };
        if (_last >= 0)
        {
            var previous = _entries[_last];
            previous.Next = entryIndex;
            _entries[_last] = previous;
        }
        else
        {
            _first = entryIndex;
        }

        _last = entryIndex;
        _lastForSubject[subject] = entryIndex;
    }

    public void RemoveLast(IInterceptorSubject subject)
    {
        if (!_lastForSubject.TryGetValue(subject, out var entryIndex)) return;
        var entry = _entries[entryIndex];
        if (entry.PreviousForSubject < 0) _lastForSubject.Remove(subject);
        else _lastForSubject[subject] = entry.PreviousForSubject;

        if (entry.Previous >= 0)
        {
            var previous = _entries[entry.Previous];
            previous.Next = entry.Next;
            _entries[entry.Previous] = previous;
        }
        else
        {
            _first = entry.Next;
        }

        if (entry.Next >= 0)
        {
            var next = _entries[entry.Next];
            next.Previous = entry.Previous;
            _entries[entry.Next] = next;
        }
        else
        {
            _last = entry.Previous;
        }

        _entries[entryIndex] = new Entry { Next = _free };
        _free = entryIndex;
    }

    public void CopyTo(List<SubjectOccurrence> target)
    {
        for (var index = _first; index >= 0; index = _entries[index].Next)
        {
            var entry = _entries[index];
            target.Add(new SubjectOccurrence(entry.Subject!, entry.Index));
        }
    }

    public void Complete(List<SubjectOccurrence> desired)
    {
        // An enclosing operation can re-enter again after this setter returns. Its installed
        // snapshot must then carry the new property order and refreshed occurrence indices.
        if (Users > 1)
        {
            ClearEntries();
            foreach (var occurrence in desired) Add(occurrence.Subject, occurrence.Index);
        }

        IsComplete = true;
    }

    public void Reset()
    {
        ClearEntries();
        Property = default;
        Ownership = null!;
        Users = 0;
        IsComplete = false;
    }

    private void ClearEntries()
    {
        _entries.Clear();
        _lastForSubject.Clear();
        _first = _last = _free = -1;
    }
}
