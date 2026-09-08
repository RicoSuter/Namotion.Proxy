using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Validates and admits an <see cref="IInterceptorSubject.AddProperties"/> batch on an owned subject. Runs under
/// the lifecycle's topology gate, entered by
/// <see cref="LifecycleInterceptor.TryAddProperties"/>.
/// </summary>
/// <remarks>
/// The order is the load-bearing part. The batch is materialized and duplicate-validated first,
/// then every qualifying getter is invoked exactly once and its result captured, then the complete
/// prospective component is discovered and claimed, and only then does anything publish: the
/// metadata swap, the property callbacks in input order, and finally the captured values as
/// ordinary structural assignments. Everything before the metadata swap can fail, and failing
/// there publishes nothing and releases the provisional claims. After the metadata swap, queued
/// callback failures propagate after notification cleanup without rolling back the admitted batch.
/// </remarks>
internal sealed class PropertyAdmission(OwnershipGraph graph, StructuralReconciler reconciler)
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
