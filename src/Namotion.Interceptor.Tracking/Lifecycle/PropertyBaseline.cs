namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>A committed value and its captured desired occurrences, independent of installed edges.</summary>
// Immutable storage survives nested writes that replace or drop an enclosing operation's baseline.
internal readonly struct PropertyBaseline(object? value, long revision, SubjectOccurrence[]? occurrences)
{
    public object? Value { get; } = value;
    public long Revision { get; } = revision;

    public void CopyTo(List<SubjectOccurrence> target)
    {
        if (Value is IInterceptorSubject subject) target.Add(new SubjectOccurrence(subject, null));
        else if (occurrences is not null) target.AddRange(occurrences);
    }

    public bool Contains(IInterceptorSubject target)
    {
        if (Value is IInterceptorSubject subject) return ReferenceEquals(subject, target);
        if (occurrences is not null)
        {
            foreach (var occurrence in occurrences)
                if (ReferenceEquals(occurrence.Subject, target)) return true;
        }

        return false;
    }
}
