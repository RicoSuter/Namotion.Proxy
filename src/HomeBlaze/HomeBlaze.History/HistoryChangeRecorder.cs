using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions;
using HomeBlaze.History.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.History;

/// <summary>
/// The graph-side glue between a change queue and a history engine: eligibility filtering, canonical
/// path resolution, move detection and throughput counting.
///
/// Shared by every store subject rather than reimplemented per store. The two copies this replaced
/// were identical in intent but had to stay in lockstep by hand, and move detection is the subtle
/// part: it decides which samples a later query can still reach.
/// </summary>
public sealed class HistoryChangeRecorder(IHistoryRecorder engine, ISubjectPathResolver resolver)
{
    // Last canonical subject path seen per subject and property. Comparing the path the resolver
    // returns against the stored one detects a move without this class subscribing to anything; it
    // inherits whatever freshness the resolver's own cache has (that cache is invalidated from
    // lifecycle changes, so a reordering that attaches and detaches nothing can leave it stale). The
    // per-property inner map lets each history property of a renamed subject detect the move
    // independently, so the first property to change does not consume the rename for its siblings.
    //
    // Weak keys: a detached subject's entry disappears when it becomes unreachable. Lifecycle detach is
    // dispatched through the detaching subject's own context, which never reaches a sibling store, so
    // this cannot rely on being told when a subject goes away.
    private readonly ConditionalWeakTable<IInterceptorSubject, Dictionary<string, string>> _lastSubjectPath = new();
    private readonly Lock _pathCacheLock = new();

    private readonly ThroughputCounter _incomingThroughput = new();
    private readonly ThroughputCounter _recordedThroughput = new();

    /// <summary>Average incoming changes per second (eligible [State] changes observed).</summary>
    public double IncomingChangesPerSecond => _incomingThroughput.CurrentRate;

    /// <summary>Average recorded changes per second (samples the engine accepted).</summary>
    public double RecordedChangesPerSecond => _recordedThroughput.CurrentRate;

    /// <summary>
    /// The change-queue filter: only recordable scalar [State] properties are worth queueing.
    /// </summary>
    public static bool IsEligible(PropertyReference propertyReference) =>
        propertyReference.TryGetRegisteredProperty() is { } registered && registered.HasHistory();

    /// <summary>
    /// Resolves and records one flushed batch of changes.
    /// </summary>
    public ValueTask RecordBatch(ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        var span = changes.Span;
        for (var index = 0; index < span.Length; index++)
        {
            var change = span[index];

            var registered = change.Property.TryGetRegisteredProperty();
            if (registered is null || !registered.HasHistory())
            {
                continue;
            }

            _incomingThroughput.Add(1);

            var subject = change.Property.Subject;
            var subjectPath = resolver.GetPath(subject, PathStyle.Canonical);
            if (subjectPath is null)
            {
                // Subject is no longer reachable (detached between change and flush); skip.
                continue;
            }

            var propertyName = change.Property.Name;
            var fullPath = JoinPath(subjectPath, propertyName);

            lock (_pathCacheLock)
            {
                var pathsByProperty = _lastSubjectPath.GetValue(
                    subject, _ => new Dictionary<string, string>(StringComparer.Ordinal));

                if (pathsByProperty.TryGetValue(propertyName, out var previousSubjectPath) &&
                    !string.Equals(previousSubjectPath, subjectPath, StringComparison.Ordinal))
                {
                    engine.RecordMove(
                        change.ChangedTimestamp,
                        JoinPath(previousSubjectPath, propertyName),
                        fullPath);
                }

                pathsByProperty[propertyName] = subjectPath;
            }

            if (engine.TryRecord(fullPath, change.ChangedTimestamp, change.GetNewValue<object>(), registered.Type))
            {
                _recordedThroughput.Add(1);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Drops a detached subject's cached paths. The weak table would release them anyway once the
    /// subject became unreachable; this just does not wait for that.
    /// </summary>
    public void Forget(IInterceptorSubject subject)
    {
        lock (_pathCacheLock)
        {
            _lastSubjectPath.Remove(subject);
        }
    }

    // Joins a canonical subject path with a property name. The root subject path is "/", so a root
    // property is "/Temperature" (not "//Temperature"); a child at "/Child" yields "/Child/Pressure".
    private static string JoinPath(string subjectPath, string propertyName) =>
        subjectPath == "/" ? "/" + propertyName : subjectPath + "/" + propertyName;
}
