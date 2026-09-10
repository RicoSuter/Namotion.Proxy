using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Context for applying a SubjectUpdate. Tracks processed subjects to prevent cycles.
/// Designed to be pooled and reused.
/// </summary>
internal sealed class SubjectUpdateApplyContext
{
    private readonly HashSet<string> _processedSubjectIds = [];
    private List<(RegisteredSubjectProperty Property, Exception Exception)>? _failures;

    public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; private set; } = null!;
    public ISubjectFactory SubjectFactory { get; private set; } = null!;
    public ChangeOrigin Origin { get; private set; }
    public Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? TransformValueBeforeApply { get; private set; }

    public void Initialize(
        Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> subjects,
        ISubjectFactory subjectFactory,
        ChangeOrigin origin,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply)
    {
        Subjects = subjects;
        SubjectFactory = subjectFactory;
        Origin = origin;
        TransformValueBeforeApply = transformValueBeforeApply;
    }

    /// <summary>
    /// Writes <paramref name="value"/> to <paramref name="property"/> under the update's origin, using
    /// the written value as the origin's sent-value evidence. See the overload taking a separate
    /// <c>sentValue</c> for the case where the applied value was locally transformed.
    /// </summary>
    public void SetPropertyValue(RegisteredSubjectProperty property, DateTimeOffset? changedTimestamp, object? value)
        => SetPropertyValue(property, changedTimestamp, value, value);

    /// <summary>
    /// Writes <paramref name="value"/> to <paramref name="property"/> under the update's origin. Local
    /// origins keep the unarmed write path (Local is the default and needs no stamp); for
    /// <see cref="ChangeOriginKind.FromSource"/> and <see cref="ChangeOriginKind.Confirmed"/> the write
    /// goes through <see cref="SubjectChangeContextExtensions.SetValueFromOrigin(PropertyReference, ChangeOrigin, DateTimeOffset?, DateTimeOffset?, object?, object?)"/> so the resulting
    /// change carries the source and echo suppression works. <paramref name="sentValue"/> is the value
    /// the source semantically sent, armed as the origin's survival evidence: when a transform corrects
    /// the applied value it differs from <paramref name="value"/>, so the origin demotes to Local and the
    /// correction is not echo-suppressed back to the source. In all cases <paramref name="changedTimestamp"/>
    /// is applied as the changed timestamp so the inbound timestamp is never replaced with capture-time now.
    /// </summary>
    public void SetPropertyValue(RegisteredSubjectProperty property, DateTimeOffset? changedTimestamp, object? value, object? sentValue)
    {
        if (Origin.Kind == ChangeOriginKind.Local)
        {
            using (SubjectChangeContext.WithChangedTimestamp(changedTimestamp))
            {
                property.SetValue(value);
            }
        }
        else
        {
            property.Reference.SetValueFromOrigin(Origin, changedTimestamp, null, value, sentValue);
        }
    }

    public bool TryMarkAsProcessed(string subjectId)
        => _processedSubjectIds.Add(subjectId);

    /// <summary>
    /// Records a property that could not be applied. The batch continues; the collected failures are
    /// thrown once by the caller when the whole update has been walked.
    /// </summary>
    public void RecordFailure(RegisteredSubjectProperty property, Exception exception)
        => (_failures ??= []).Add((property, exception));

    /// <summary>The failures recorded so far, or <c>null</c> when every property applied.</summary>
    public List<(RegisteredSubjectProperty Property, Exception Exception)>? Failures => _failures;

    /// <summary>
    /// Clears the context for reuse. Call before returning to pool.
    /// </summary>
    public void Clear()
    {
        _processedSubjectIds.Clear();
        _failures = null;
        Subjects = null!;
        SubjectFactory = null!;
        Origin = default;
        TransformValueBeforeApply = null;
    }
}
