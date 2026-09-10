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
    public void Reconcile(PropertyReference property, SubjectPropertyMetadata metadata, object? newValue)
    {
        var oldValue = graph.GetBaseline(property);
        if (ReferenceEquals(oldValue, newValue))
        {
            return;
        }

        if (!StructuralValueScanner.CanHoldSubjects(oldValue) && !StructuralValueScanner.CanHoldSubjects(newValue))
        {
            return;
        }

        var oldOccurrences = LifecycleScratch.RentOccurrenceList();
        var newOccurrences = LifecycleScratch.RentOccurrenceList();
        try
        {
            StructuralValueScanner.CollectOccurrences(metadata.Type, oldValue, oldOccurrences);
            StructuralValueScanner.CollectOccurrences(metadata.Type, newValue, newOccurrences);

            if (!graph.IsOwned(property.Subject))
            {
                // Code running downstream of the lifecycle at callback depth zero (a third-party
                // write interceptor, a hand-written terminal, a dynamic getter reread, or a
                // side-effecting user collection enumerated just above) holds the gate reentrantly
                // and can release the writing parent before this point. That release already
                // collected this property's children through the old baseline, so nothing may
                // continue on the parent's behalf: committing the new baseline would recreate an
                // entry that no later release ever removes, and the addition loop would attach
                // occurrences to a released owner.
                return;
            }

            if (!ReferenceEquals(graph.GetBaseline(property), oldValue))
            {
                // That same user code reentered the write protocol on this very property and
                // committed a newer baseline while the scans above ran. Its value reached the
                // backing field after this one did and the graph already agrees with it, so
                // committing this one would publish edges the property no longer holds.
                return;
            }

            // Commit the outgoing edges before the incoming records are touched.
            graph.SetBaseline(property, newValue);

            ReconcileOccurrences(property, newValue, oldOccurrences, newOccurrences);
        }
        finally
        {
            LifecycleScratch.Return(oldOccurrences);
            LifecycleScratch.Return(newOccurrences);
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
        List<SubjectOccurrence> newOccurrences)
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
                if (!graph.IsOwned(parent))
                {
                    // Side-effecting user code invoked by this loop at callback depth zero (a
                    // dictionary-key Equals, a user collection implementation) can run the write
                    // protocol reentrantly and release the writing parent mid-publication, and the
                    // remaining edges would then be published on behalf of a released owner.
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
                    continue;
                }

                attach.AttachEdge(occurrence.Subject, property, occurrence.Index);
                if (!graph.IsOwned(parent))
                {
                    return;
                }
            }

            if (!graph.IsOwned(parent))
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
