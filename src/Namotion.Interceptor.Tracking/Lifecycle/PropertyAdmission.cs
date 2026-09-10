using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Admits an <see cref="IInterceptorSubject.AddProperties"/> batch on an owned subject: one atomic
/// publication of metadata, property callbacks and initial ownership edges, or nothing. Runs under
/// the lifecycle's topology gate, entered by
/// <see cref="LifecycleInterceptor.TryAddProperties"/>.
/// </summary>
/// <remarks>
/// The order is the load-bearing part. The batch is materialized and duplicate-validated first,
/// then every qualifying getter is invoked exactly once and its result captured, then the complete
/// prospective component is discovered and claimed, and only then does anything publish: the
/// metadata swap, the property callbacks in input order, and finally the captured values as
/// ordinary structural assignments. Everything before the metadata swap can fail, and failing
/// there publishes nothing and releases the provisional claims. Everything after it is
/// exception-free by contract, and a violating callback propagates without rollback, like every
/// other lifecycle callback.
/// </remarks>
internal sealed class PropertyAdmission(OwnershipGraph graph, StructuralReconciler reconciler, AttachTraversal attach)
{
    public void Admit(SubjectPropertyRegistration registration)
    {
        var subject = registration.Subject;
        var batch = registration.GetProperties();
        if (batch.Count == 0)
        {
            return;
        }

        if (!graph.AreBaselinesSeeded(subject))
        {
            // Owned but not yet seeded: an edge-driven attach records ownership before the descent
            // seeds, so a handler that adds properties lands in that window. Committing a baseline
            // here would decide the pending seeding by name, because AreBaselinesSeeded answers
            // from whichever structural property enumerates first. The descent reads every
            // structural getter, the ones this batch adds included, so it publishes these edges
            // itself; only the property callbacks belong to this call.
            registration.Publish();
            InvokePropertyAttachCallbacks(subject, batch);
            return;
        }

        var captured = CaptureStructuralValues(subject, batch);
        if (captured is null)
        {
            registration.Publish();
            InvokePropertyAttachCallbacks(subject, batch);
            return;
        }

        var visited = LifecycleScratch.RentSubjectSet();
        var claimed = LifecycleScratch.RentSubjectList();
        try
        {
            ClaimCapturedComponents(captured, visited, claimed);

            registration.Publish();
            InvokePropertyAttachCallbacks(subject, batch);

            // Commit the captured values as ordinary assignments: the reconciler sees no baseline,
            // so every occurrence becomes a fresh edge, and the value becomes the baseline later
            // writes diff against. A null value writes the entry directly, because every structural
            // property of an owned subject must carry one, present or null.
            foreach (var (metadata, value) in captured)
            {
                var property = new PropertyReference(subject, metadata.Name);
                if (value is null)
                {
                    // Same guard the reconciler applies at entry: a baseline written for a subject
                    // an earlier entry's reconcile released mid-batch is never removed again.
                    if (!graph.IsOwned(subject))
                    {
                        return;
                    }

                    graph.SetBaseline(property, null);
                }
                else
                {
                    reconciler.Reconcile(property, metadata, value);
                }
            }
        }
        finally
        {
            // Claims that never became ownership are handed back; see
            // OwnershipGraph.ReleaseUnusedClaims for what leaves them behind.
            graph.ReleaseUnusedClaims(claimed);
            LifecycleScratch.Return(visited);
            LifecycleScratch.Return(claimed);
        }
    }

    /// <summary>
    /// Admits a batch on a subject that is claimed for this context but not owned by the graph,
    /// which is observable from inside this thread's own attach descent and from a detach callback.
    /// </summary>
    /// <remarks>
    /// Two shapes exist. When the descent has not seeded the subject yet, only metadata publishes:
    /// the descent will seed every baseline, including the new properties', and fan out the
    /// then-current property set when it attaches the subject. When the subject is already seeded
    /// (an explicit attach seeds the root before owning it, and a subject with no structural
    /// properties counts as seeded), the descent will not come back, so the new structural
    /// properties are seeded here the same way the descent would have. Property callbacks are not
    /// invoked on either shape, because the pending context-attach publication fans out the
    /// then-current property set, new properties included.
    ///
    /// A releasing subject presents the same attached-but-unowned shape from the opposite direction
    /// and takes the metadata-only arm too: it has no descent coming, and an edge published from it
    /// would name an owner the release already removed, so nothing would ever release the child.
    /// The marker is what extends that arm to a subject whose baselines were empty to begin with.
    /// </remarks>
    public void AdmitUnowned(SubjectPropertyRegistration registration)
    {
        var subject = registration.Subject;
        if (graph.IsReleasing(subject) || !graph.AreBaselinesSeeded(subject))
        {
            registration.Publish();
            return;
        }

        var batch = registration.GetProperties();
        if (batch.Count == 0)
        {
            return;
        }

        var captured = CaptureStructuralValues(subject, batch);
        if (captured is null)
        {
            registration.Publish();
            return;
        }

        var visited = LifecycleScratch.RentSubjectSet();
        var claimed = LifecycleScratch.RentSubjectList();
        try
        {
            ClaimCapturedComponents(captured, visited, claimed);

            registration.Publish();

            // Seed rather than reconcile: the reconciler's released-parent early exits read
            // IsOwned on the writing parent, which is legitimately false here, so it would stop
            // after the first occurrence. Seeding is the descent's own shape for exactly this
            // state: commit the outgoing baseline, then attach one edge per occurrence.
            var occurrences = LifecycleScratch.RentOccurrenceList();
            try
            {
                foreach (var (metadata, value) in captured)
                {
                    var property = new PropertyReference(subject, metadata.Name);
                    graph.SetBaseline(property, value);
                    if (value is null)
                    {
                        continue;
                    }

                    occurrences.Clear();
                    StructuralValueScanner.CollectOccurrences(metadata.Type, value, occurrences);
                    foreach (var occurrence in occurrences)
                    {
                        attach.AttachEdge(occurrence.Subject, property, occurrence.Index);
                    }
                }
            }
            finally
            {
                LifecycleScratch.Return(occurrences);
            }
        }
        finally
        {
            graph.ReleaseUnusedClaims(claimed);
            LifecycleScratch.Return(visited);
            LifecycleScratch.Return(claimed);
        }
    }

    /// <summary>
    /// Classifies the initial ownership candidates and invokes each qualifying getter exactly once,
    /// before anything publishes. The captured value is committed by the caller rather than re-read,
    /// so an unstable getter cannot commit edges for a graph nobody stored.
    /// </summary>
    private static List<(SubjectPropertyMetadata Metadata, object? Value)>? CaptureStructuralValues(
        IInterceptorSubject subject, IReadOnlyList<SubjectPropertyMetadata> batch)
    {
        List<(SubjectPropertyMetadata Metadata, object? Value)>? captured = null;
        foreach (var metadata in batch)
        {
            if (OwnershipGraph.IsStructural(metadata))
            {
                (captured ??= []).Add((metadata, metadata.GetValue?.Invoke(subject)));
            }
        }

        return captured;
    }

    /// <summary>
    /// Discovers the complete prospective component of every captured value and claims the
    /// unattached subjects as one batch. A foreign subject or a lost claim race throws before
    /// anything publishes, with this call's own claims released.
    /// </summary>
    private void ClaimCapturedComponents(
        List<(SubjectPropertyMetadata Metadata, object? Value)> captured,
        HashSet<IInterceptorSubject> visited,
        List<IInterceptorSubject> claimed)
    {
        foreach (var (metadata, value) in captured)
        {
            if (value is not null)
            {
                graph.DiscoverComponent(metadata.Type, value, visited, claimed);
            }
        }

        if (!graph.TryClaimDiscovered(claimed, null, SubjectAttachmentAnchorKind.None))
        {
            claimed.Clear();
            throw new InvalidOperationException(
                "Another context claimed a subject of the admitted graph while this call was " +
                "validating it. Nothing was published.");
        }
    }

    private static void InvokePropertyAttachCallbacks(IInterceptorSubject subject, IReadOnlyList<SubjectPropertyMetadata> batch)
    {
        for (var index = 0; index < batch.Count; index++)
        {
            subject.AttachSubjectProperty(new PropertyReference(subject, batch[index].Name));
        }
    }
}
