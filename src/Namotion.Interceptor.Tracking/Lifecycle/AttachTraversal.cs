namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Records committed incoming edges and publishes the attach transitions they cause, and seeds a
/// subject's own structural properties when it enters the graph.
/// </summary>
/// <remarks>
/// The recursive descent happens from inside the context handler list, so handlers ordered before
/// it observe a subject top-down and handlers after it, along with the <c>SubjectAttached</c>
/// event, observe it bottom-up.
/// </remarks>
internal sealed class AttachTraversal(LifecycleNotifier notifier, OwnershipGraph graph, ReachabilityWalk reachability)
{
    /// <summary>
    /// The provisional anchors consumed while an explicit attach is in flight, each with the
    /// attachment revision the consumption produced, or null when none is. A rejected attach
    /// drains the edges it published and has to hand back the anchors those edges consumed. It
    /// cannot tell those apart from one consumed by a nested write the seed invoked, so it records
    /// every consumption and re-checks after the drain which subjects an edge still supports.
    /// </summary>
    public List<(IInterceptorSubject Subject, long Revision)>? ConsumedAnchors { get; set; }

    public void SeedChildrenIfNeeded(IInterceptorSubject subject)
    {
        if (!graph.AreBaselinesSeeded(subject))
        {
            SeedAndAttachChildren(subject);
        }
    }

    public void SeedAndAttachChildren(IInterceptorSubject subject)
    {
        var children = LifecycleScratch.RentChildList();
        try
        {
            graph.CollectStructuralChildren(subject, children, seed: true);
            foreach (var (property, occurrence) in children)
            {
                AttachEdge(occurrence.Subject, property, occurrence.Index);
            }
        }
        finally
        {
            LifecycleScratch.Return(children);
        }
    }

    /// <summary>
    /// Records one incoming edge occurrence and publishes it, entering the subject into the graph
    /// when this is its first edge.
    /// </summary>
    public void AttachEdge(IInterceptorSubject subject, PropertyReference property, object? index)
    {
        var existing = graph.TryGetOwnership(subject);
        var isContextAttach = existing is null;
        SubjectOwnership ownership;
        if (existing is not null)
        {
            ownership = existing;
        }
        else
        {
            if (!graph.TryClaim(subject, SubjectAttachmentAnchorKind.None))
            {
                throw new InvalidOperationException(
                    $"The subject '{subject.GetType().Name}' is owned by a different context and cannot join this graph.");
            }

            ownership = graph.AddOwnership(subject);
        }

        ownership.AddIncoming(property, index);
        var referenceCount = ownership.IncomingCount;

        // Authoritative parent and anchor state before the first handler observes the change.
        ownership.RepublishParents();
        ConsumeProvisionalAnchor(subject, property);

        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            Property = property,
            Index = index,
            ReferenceCount = referenceCount,
            IsContextAttach = isContextAttach,
            IsPropertyReferenceAdded = true
        };

        Publish(subject, change, isContextAttach);
    }

    /// <summary>Publishes a subject entering the graph without an edge, as an anchored root.</summary>
    public void AttachRoot(IInterceptorSubject subject)
    {
        graph.AddOwnership(subject);

        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            ReferenceCount = 0,
            IsContextAttach = true
        };

        Publish(subject, change, isContextAttach: true);
    }

    /// <summary>
    /// Invokes the ordered handlers, and for a subject entering the graph also raises the event and
    /// attaches its properties.
    /// </summary>
    private void Publish(IInterceptorSubject subject, SubjectLifecycleChange change, bool isContextAttach)
    {
        // Snapshotted before the handlers run: a handler may add properties, and those are attached
        // by that call rather than a second time here.
        var properties = subject.Properties.Keys;
        notifier.InvokeAddedLifecycleHandlers(subject, change);

        if (!isContextAttach)
        {
            return;
        }

        notifier.RaiseSubjectAttached(change);
        foreach (var propertyName in properties)
        {
            subject.AttachSubjectProperty(new PropertyReference(subject, propertyName));
        }
    }

    /// <summary>
    /// Clears a provisional anchor once an edge supports the subject independently of that anchor,
    /// meaning the edge's parent has an anchored ancestor other than the subject itself.
    /// </summary>
    /// <remarks>
    /// Clearing on the first edge of any kind is unsound: the everyday back reference
    /// <c>child.Parent = root</c> would consume the root's own anchor, and the next removal anywhere
    /// would release the whole graph. A self edge fails the same test for the same reason.
    /// </remarks>
    private void ConsumeProvisionalAnchor(IInterceptorSubject subject, PropertyReference property)
    {
        subject.Executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
        if (anchor != SubjectAttachmentAnchorKind.Provisional || !ReferenceEquals(attachedContext, graph.Context))
        {
            return;
        }

        if (!reachability.IsAnchorReachable(property.Subject, subject))
        {
            return;
        }

        graph.SetAnchor(subject, SubjectAttachmentAnchorKind.None, onlyFrom: SubjectAttachmentAnchorKind.Provisional);
        if (ConsumedAnchors is not null)
        {
            subject.Executor.TryGetAttachment(out _, out var anchorAfterConsumption, out var consumedRevision);
            if (anchorAfterConsumption == SubjectAttachmentAnchorKind.None)
            {
                ConsumedAnchors.Add((subject, consumedRevision));
            }
        }
    }
}
