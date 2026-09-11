using System.Collections;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Turns a committed structural property value into edge additions and removals against the value
/// the lifecycle last reconciled.
/// </summary>
/// <remarks>
/// Every occurrence is one edge, so <c>[a, a, b]</c> gives <c>a</c> two edges. Retention is decided
/// by subject identity, never by the index the occurrence carries: the first <c>min(old, new)</c>
/// occurrences of a subject survive in enumeration order, only the surplus is removed, and the
/// survivors adopt their new indices afterwards. Matching a keyed value by its key instead would
/// make a rekey a removal plus an addition, and the removal pass runs to completion before the
/// addition pass, so the subject would lose its last support and become claimable by another
/// context in between.
///
/// The new value is committed as the property baseline before any edge is published. Incoming
/// records and committed outgoing edges therefore disagree for the duration of the publication,
/// which is exactly why every reader validates candidate edges against the baselines.
/// </remarks>
internal sealed class StructuralReconciler(LifecycleNotifier notifier, OwnershipGraph graph, AttachTraversal attach, ReleaseTraversal release)
{
    public void Reconcile(PropertyReference property, SubjectPropertyMetadata metadata, object? newValue, bool useCapturedBaseline = false)
    {
        var ownership = graph.TryGetOwnership(property.Subject);
        if (ownership is null) return;
        var existingJournal = graph.GetPropertyJournal(property, ownership);
        var baseline = graph.GetBaselineSnapshot(property);
        var oldValue = baseline.Value;
        if (useCapturedBaseline) newValue = baseline.Value;
        if (existingJournal is null && ReferenceEquals(oldValue, newValue)) return;
        if (existingJournal is null && !StructuralValueScanner.CanHoldSubjects(oldValue) && !StructuralValueScanner.CanHoldSubjects(newValue)) return;

        if (existingJournal is null && (oldValue is null or IInterceptorSubject) && (newValue is null or IInterceptorSubject))
        {
            ReconcileScalar(property, ownership, (IInterceptorSubject?)oldValue, (IInterceptorSubject?)newValue);
            return;
        }

        var previousRevision = baseline.Revision;
        var oldOccurrences = LifecycleScratch.RentOccurrenceList();
        var newOccurrences = LifecycleScratch.RentOccurrenceList();
        try
        {
            if (existingJournal is null) baseline.CopyTo(oldOccurrences);
            else existingJournal.CopyTo(oldOccurrences);
            if (useCapturedBaseline) baseline.CopyTo(newOccurrences);
            else StructuralValueScanner.CollectOccurrences(metadata.Type, newValue, newOccurrences);

            if (!ReferenceEquals(graph.TryGetOwnership(property.Subject), ownership) ||
                graph.GetBaselineRevision(property) != previousRevision) return;

            var journal = graph.BeginPropertyJournal(property, ownership, oldOccurrences);
            try
            {
                var revision = graph.SetBaseline(property, newValue, newOccurrences);
                journal.IsComplete = false;
                ReconcileOccurrences(property, newValue, oldOccurrences, newOccurrences, ownership, revision);
                if (ReferenceEquals(graph.TryGetOwnership(property.Subject), ownership) &&
                    graph.GetBaselineRevision(property) == revision)
                {
                    journal.Complete(newOccurrences);
                }
            }
            finally
            {
                graph.EndPropertyJournal(journal);
            }
        }
        finally
        {
            LifecycleScratch.Return(oldOccurrences);
            LifecycleScratch.Return(newOccurrences);
        }
    }

    private void ReconcileScalar(
        PropertyReference property,
        SubjectOwnership ownership,
        IInterceptorSubject? oldSubject,
        IInterceptorSubject? newSubject)
    {
        var journal = graph.BeginPropertyJournal(property, ownership);
        try
        {
            if (oldSubject is not null) journal.Add(oldSubject, null);
            var revision = graph.SetBaseline(property, newSubject);
            if (oldSubject is not null)
            {
                release.RemoveEdge(oldSubject, property, null);
                if (!ReferenceEquals(graph.TryGetOwnership(property.Subject), ownership) || graph.GetBaselineRevision(property) != revision)
                    return;
            }

            if (newSubject is not null)
            {
                attach.AttachEdge(newSubject, property, null);
                if (!ReferenceEquals(graph.TryGetOwnership(property.Subject), ownership) || graph.GetBaselineRevision(property) != revision)
                    return;
            }

            // No journal existed on entry; any nested write that shares this one changes the
            // revision checked above, so completion has no enclosing snapshot to refresh.
            journal.IsComplete = true;
        }
        finally
        {
            graph.EndPropertyJournal(journal);
        }
    }

    /// <summary>
    /// Per subject, the surplus old occurrences are removed from the end and the surplus new
    /// occurrences are added at the front-most free positions, then every retained edge adopts its
    /// new index.
    /// </summary>
    private void ReconcileOccurrences(
        PropertyReference property,
        object? newValue,
        List<SubjectOccurrence> oldOccurrences,
        List<SubjectOccurrence> newOccurrences,
        SubjectOwnership ownership,
        long revision)
    {
        var parent = property.Subject;
        var oldCounts = LifecycleScratch.RentSubjectCounter();
        var newCounts = LifecycleScratch.RentSubjectCounter();
        try
        {
            foreach (var occurrence in oldOccurrences)
            {
                oldCounts[occurrence.Subject] = oldCounts.GetValueOrDefault(occurrence.Subject) + 1;
            }

            foreach (var occurrence in newOccurrences)
            {
                newCounts[occurrence.Subject] = newCounts.GetValueOrDefault(occurrence.Subject) + 1;
            }

            // Removals run in reverse so the surviving occurrences are the leading ones, which is what
            // makes retained duplicates match in enumeration order. Reverse order is also what the
            // existing collection-child bookkeeping expects.
            for (var i = oldOccurrences.Count - 1; i >= 0; i--)
            {
                var occurrence = oldOccurrences[i];
                var remaining = oldCounts[occurrence.Subject];
                if (remaining <= newCounts.GetValueOrDefault(occurrence.Subject))
                {
                    continue;
                }

                oldCounts[occurrence.Subject] = remaining - 1;
                release.RemoveEdge(occurrence.Subject, property, occurrence.Index);
                if (!ReferenceEquals(graph.TryGetOwnership(parent), ownership) || graph.GetBaselineRevision(property) != revision)
                {
                    // Reachability and child discovery can invoke user code. A nested write owns
                    // the remaining publication once it replaces this baseline or ownership epoch.
                    return;
                }
            }

            // After the removal pass oldCounts holds min(old, new) per subject, which is exactly how
            // many leading new occurrences are already covered by a retained edge.
            foreach (var occurrence in newOccurrences)
            {
                var retained = oldCounts.GetValueOrDefault(occurrence.Subject);
                if (retained > 0)
                {
                    oldCounts[occurrence.Subject] = retained - 1;
                    if (attach.ResumeFailedSeed(occurrence.Subject) &&
                        (!ReferenceEquals(graph.TryGetOwnership(parent), ownership) || graph.GetBaselineRevision(property) != revision))
                        return;
                    continue;
                }

                attach.AttachEdge(occurrence.Subject, property, occurrence.Index);
                if (!ReferenceEquals(graph.TryGetOwnership(parent), ownership) || graph.GetBaselineRevision(property) != revision)
                {
                    return;
                }
            }

            if (!ReferenceEquals(graph.TryGetOwnership(parent), ownership) || graph.GetBaselineRevision(property) != revision)
            {
                return;
            }

            RefreshRetainedIndices(property, newValue, oldOccurrences, newOccurrences, newCounts);
        }
        finally
        {
            LifecycleScratch.Return(oldCounts);
            LifecycleScratch.Return(newCounts);
        }
    }

    /// <summary>
    /// Rewrites the occurrence indices of every subject in the new value, then lets property handlers
    /// refresh their own collection projections. A retained edge keeps its identity across a reorder
    /// or a rekey, so it changes index without an attach or detach transition.
    /// </summary>
    private void RefreshRetainedIndices(
        PropertyReference property,
        object? newValue,
        List<SubjectOccurrence> oldOccurrences,
        List<SubjectOccurrence> newOccurrences,
        Dictionary<IInterceptorSubject, int> newCounts)
    {
        if (newValue is not IEnumerable || newValue is string || newOccurrences.Count == 0)
        {
            return;
        }

        var hasRetained = false;
        foreach (var occurrence in oldOccurrences)
        {
            if (newCounts.ContainsKey(occurrence.Subject))
            {
                hasRetained = true;
                break;
            }
        }

        if (!hasRetained)
        {
            return;
        }

        var groups = LifecycleScratch.RentIndexGroups();
        try
        {
            foreach (var occurrence in newOccurrences)
            {
                if (!groups.TryGetValue(occurrence.Subject, out var indices))
                {
                    indices = LifecycleScratch.RentIndexList();
                    groups.Add(occurrence.Subject, indices);
                }

                indices.Add(occurrence.Index);
            }

            foreach (var group in groups)
            {
                var ownership = graph.TryGetOwnership(group.Key);
                if (ownership is null)
                {
                    continue;
                }

                ownership.SetIncomingIndices(property, group.Value);
                ownership.RepublishParents();
            }
        }
        finally
        {
            LifecycleScratch.Return(groups);
        }

        notifier.RefreshCollectionProperty(property, newValue);
    }
}
