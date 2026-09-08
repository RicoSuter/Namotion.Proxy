namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Removes committed incoming edges and releases whatever a removal orphans, including closed
/// cycles.
/// </summary>
/// <remarks>
/// Release order is observable, so it is a traversal rather than a sweep: a mark-and-sweep yields an
/// unordered set and iterating the owned-subject map is nondeterministic. The descent starts at the
/// removed edge, follows committed outgoing edges, visits only subjects that lost their last
/// support, and reaches each of them exactly once in first-visit order. That makes detach callbacks
/// arrive top-down, so a parent stops before the children it uses.
///
/// A released subject is hidden from ownership queries before its callbacks run, which makes the
/// descent safe to re-enter: a reachability walk can no longer route through it. The ownership
/// record remains as a tombstone until its queued final release has delivered.
/// </remarks>
internal sealed class ReleaseTraversal(LifecycleNotifier notifier, OwnershipGraph graph, ReachabilityWalk reachability)
{
    /// <summary>
    /// Removes one committed incoming edge occurrence and releases the subject, and everything below
    /// it that this removal orphans, when nothing holds it anymore.
    /// </summary>
    public void RemoveEdge(IInterceptorSubject subject, PropertyReference property, object? index)
    {
        var ownership = graph.TryGetOwnership(subject);
        if (ownership is null || !ownership.RemoveIncoming(property, index))
        {
            // Already released, or the edge was drained by a reentrant descent.
            return;
        }

        graph.RecordIncomingRemoved(property, subject);
        var referenceCount = ownership.IncomingCount;
        ownership.RepublishParents();

        if (IsStillHeld(subject, ownership))
        {
            notifier.PublishEdgeRemoved(subject, property, index, referenceCount);
            return;
        }

        Release(subject, ownership, property, index);
    }

    /// <summary>
    /// Releases a subject that lost its root anchor, together with everything below it that only it
    /// held. Used by explicit detach.
    /// </summary>
    public void ReleaseRoot(IInterceptorSubject subject)
    {
        var ownership = graph.TryGetOwnership(subject);
        if (ownership is null)
        {
            return;
        }

        Release(subject, ownership, null, null);
    }

    /// <summary>
    /// Publishes a removal for every incoming edge the released subject still carries. Those edges
    /// come from inside the same unreachable component, typically the other half of a cycle. Without
    /// this they would never receive their removal notification, and the subject would report a
    /// nonzero reference count on the very change that takes it out of the graph.
    /// </summary>
    private void DrainRemainingEdges(IInterceptorSubject subject, SubjectOwnership ownership)
    {
        if (ownership.IncomingCount == 0)
        {
            return;
        }

        var remaining = LifecycleScratch.RentEdgeList();
        try
        {
            ownership.CopyIncomingEdges(remaining);
            foreach (var edge in remaining)
            {
                if (ownership.RemoveIncoming(edge.Property, edge.Index)) graph.RecordIncomingRemoved(edge.Property, subject);
                ownership.RepublishParents();
                notifier.PublishEdgeRemoved(subject, edge.Property, edge.Index, ownership.IncomingCount);
            }
        }
        finally
        {
            LifecycleScratch.Return(remaining);
        }
    }

    /// <summary>
    /// Whether anything still holds the subject: its own anchor, or a path from an anchored root.
    /// </summary>
    private bool IsStillHeld(IInterceptorSubject subject, SubjectOwnership ownership)
    {
        // The zero-edge short circuit is what keeps tree-shaped removals free of any walk; the walk
        // itself already answers the subject's own anchor.
        return ownership.IncomingCount > 0
            ? reachability.IsAnchorReachable(subject, null)
            : graph.IsAnchored(subject);
    }

    private void Release(IInterceptorSubject subject, SubjectOwnership ownership, PropertyReference? property, object? index)
    {
        var children = LifecycleScratch.RentChildList();
        var releaseQueued = false;
        try
        {
            graph.CollectStructuralChildren(subject, children, seed: false);

            // Hide the ownership record and drop the baselines first: from here on the subject is
            // released as far as every other query is concerned, which is what makes the callbacks
            // below safe to re-enter this descent from, and what makes them see no parents at all
            // rather than only the edge being removed.
            ownership.MarkReleasing();
            graph.RemoveBaselines(subject);

            foreach (var entry in subject.Properties)
            {
                notifier.QueueProperty(new PropertyReference(subject, entry.Key), attach: false);
            }

            DrainRemainingEdges(subject, ownership);

            var change = new SubjectLifecycleChange
            {
                Subject = subject,
                Property = property,
                Index = index,
                ReferenceCount = 0,
                IsPropertyReferenceRemoved = property.HasValue,
                IsContextDetach = true
            };

            notifier.RaiseSubjectDetaching(change);
            notifier.InvokeRemovedLifecycleHandlers(subject, change);

            // Final claim release stays behind this lifetime's teardown callbacks so they can
            // still resolve the context they are being torn down from.
            notifier.QueueRelease(subject, ownership);
            releaseQueued = true;

            foreach (var (childProperty, occurrence, _) in children)
            {
                RemoveEdge(occurrence.Subject, childProperty, occurrence.Index);
            }
        }
        finally
        {
            if (!releaseQueued) graph.ClearReleasing(subject, ownership);
            LifecycleScratch.Return(children);
        }
    }
}
