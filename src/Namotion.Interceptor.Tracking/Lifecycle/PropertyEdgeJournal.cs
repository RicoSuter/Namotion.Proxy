namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>Tracks the installed occurrences of a property while its desired baseline can run ahead.</summary>
internal sealed class PropertyEdgeJournal
{
    private readonly List<SubjectOccurrence> _occurrences = [];

    public PropertyReference Property { get; private set; }
    public SubjectOwnership Ownership { get; private set; } = null!;
    public int Users { get; set; }
    public bool IsComplete { get; set; }

    public void Initialize(PropertyReference property, SubjectOwnership ownership, List<SubjectOccurrence>? installed)
    {
        Property = property;
        Ownership = ownership;
        Users = 1;
        if (installed is not null) _occurrences.AddRange(installed);
    }

    public void Add(IInterceptorSubject subject, object? index)
    {
        _occurrences.Add(new SubjectOccurrence(subject, index));
    }

    /// <summary>
    /// Drops the trailing occurrence of the subject, matching the reverse order in which the
    /// reconciler removes surplus edges.
    /// </summary>
    public void RemoveLast(IInterceptorSubject subject)
    {
        for (var index = _occurrences.Count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(_occurrences[index].Subject, subject))
            {
                _occurrences.RemoveAt(index);
                return;
            }
        }
    }

    public void CopyTo(List<SubjectOccurrence> target)
    {
        target.AddRange(_occurrences);
    }

    public void Complete(List<SubjectOccurrence> desired)
    {
        // An enclosing operation can re-enter again after this setter returns. Its installed
        // snapshot must then carry the new property order and refreshed occurrence indices.
        if (Users > 1)
        {
            _occurrences.Clear();
            _occurrences.AddRange(desired);
        }

        IsComplete = true;
    }

    public void Reset()
    {
        _occurrences.Clear();
        Property = default;
        Ownership = null!;
        Users = 0;
        IsComplete = false;
    }
}
