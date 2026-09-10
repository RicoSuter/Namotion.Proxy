using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// The committed ownership state of one context: which subjects it owns, the last reconciled value
/// of every structural property, and the primitives that claim executors for this context or hand
/// them back.
/// </summary>
/// <remarks>
/// Committed outgoing edges are not stored separately. The property baselines are the outgoing
/// truth: a subject commits an edge to a child exactly when the baseline of one of its structural
/// properties still contains that child. One representation instead of two removes the whole class
/// of bugs where the two disagree, and it is what makes the release descent and the reachability
/// walk read the same relation.
///
/// The claim primitives (claiming, releasing and re-anchoring executors) take the executor's
/// attachment monitor through <c>TryUpdateAttachment</c>, so they require the topology gate to
/// already be held; see the lock order note on the executor's attachment monitor.
///
/// The owned map is a <see cref="ConcurrentDictionary{TKey,TValue}"/> with exactly one writer (the
/// lifecycle, under its topology lock). It is concurrent for the readers: <c>GetParents</c> and
/// <c>GetReferenceCount</c> must not take that lock, so they need a lock-free way to find a
/// subject's record.
/// </remarks>
internal sealed class OwnershipGraph(IInterceptorSubjectContext context)
{
    // Reference equality, explicitly: graph membership is identity, and a hand-written subject
    // may override Equals/GetHashCode, which under default equality could merge distinct nodes or
    // strand a subject whose hash mutates while it is owned.
    private readonly ConcurrentDictionary<IInterceptorSubject, SubjectOwnership> _owned = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PropertyReference, object?> _baselines = new(PropertyReference.Comparer);

    // Written only by the release descent, under the topology lock, and read only by the
    // admission path; a set rather than a field because a release can nest inside a callback.
    private readonly HashSet<IInterceptorSubject> _releasing = new(ReferenceEqualityComparer.Instance);

    public IInterceptorSubjectContext Context { get; } = context;

    /// <summary>
    /// Whether the property can carry graph edges: intercepted, so the lifecycle sees its writes,
    /// of a declared type that can contain subjects, and not a derived projection.
    /// </summary>
    /// <remarks>
    /// A [Derived] property carries an edge where it is the store of record. The generator gives
    /// every intercepted property a backing field, so there IsIntercepted already means the
    /// property is the store; a dynamic property is intercepted unconditionally, so its setter
    /// stands in instead, because a getter-only derived one can return nothing the properties it
    /// reads do not already own. A subject reachable only through a property that carries no edge
    /// is never tracked, and DerivedPropertyChangeHandler rejects it instead of letting it go
    /// silently unowned. See docs/design/tracking-lifecycle.md.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsStructural(in SubjectPropertyMetadata metadata)
    {
        return metadata is { IsIntercepted: true } and not { IsDerived: true, IsDynamic: true, SetValue: null } &&
               metadata.Type.CanContainSubjects();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SubjectOwnership? TryGetOwnership(IInterceptorSubject subject)
    {
        return _owned.TryGetValue(subject, out var ownership) ? ownership : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsOwned(IInterceptorSubject subject)
    {
        return _owned.ContainsKey(subject);
    }

    public SubjectOwnership AddOwnership(IInterceptorSubject subject)
    {
        var ownership = new SubjectOwnership();
        _owned[subject] = ownership;
        return ownership;
    }

    public void RemoveOwnership(IInterceptorSubject subject)
    {
        _owned.TryRemove(subject, out _);
    }

    /// <summary>
    /// Whether the subject is between losing its ownership record and having its executor handed
    /// back, which is the window its detach callbacks run in.
    /// </summary>
    /// <remarks>
    /// In that window the subject is attached but unowned, which is also the shape of a subject an
    /// attach descent has claimed but not published yet. The two need opposite admission
    /// behaviour, so the release marks its own; nothing else can tell them apart.
    /// </remarks>
    public bool IsReleasing(IInterceptorSubject subject)
    {
        return _releasing.Count > 0 && _releasing.Contains(subject);
    }

    /// <inheritdoc cref="IsReleasing"/>
    public void MarkReleasing(IInterceptorSubject subject)
    {
        _releasing.Add(subject);
    }

    /// <inheritdoc cref="IsReleasing"/>
    public void ClearReleasing(IInterceptorSubject subject)
    {
        _releasing.Remove(subject);
    }

    /// <summary>
    /// Gets the subject's occurrence-aware parents. Publication is lazily activated: the first call
    /// on a subject materializes its snapshot and marks it, and from then on every edge change
    /// republishes it, so a consumer that never asks pays one volatile read per edge change and
    /// allocates nothing.
    /// </summary>
    /// <remarks>
    /// This must not take the lifecycle's topology lock. <c>SourceMonitor</c> holds its own lock
    /// across a graph walk that calls it, and is also invoked from inside the topology lock through
    /// <c>HandleLifecycleChange</c>; a locking read would make those two orders opposite and
    /// deadlock. The lifecycle stays the sole writer; the owned-subject map is concurrent so the
    /// record can be found without the lock, and the per-subject monitor that guards materialization
    /// is a leaf that the topology lock is always taken before.
    /// </remarks>
    public ImmutableArray<SubjectParent> GetParents(IInterceptorSubject subject)
    {
        var ownership = TryGetOwnership(subject);
        if (ownership is null)
        {
            return [];
        }

        return ownership.TryGetPublishedParents(out var published) ? published : ownership.ActivateParents();
    }

    #region Property baselines, which are also the committed outgoing edges

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? GetBaseline(PropertyReference property)
    {
        return _baselines.GetValueOrDefault(property);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetBaseline(PropertyReference property, object? value)
    {
        _baselines[property] = value;
    }

    /// <summary>
    /// Whether a baseline entry exists at all: a committed null and a missing entry both read as
    /// null through <see cref="GetBaseline"/>, and the released-subject regression tests must tell
    /// them apart.
    /// </summary>
    public bool HasBaseline(PropertyReference property)
    {
        return _baselines.ContainsKey(property);
    }

    /// <summary>
    /// Whether the parent still commits an outgoing edge to the target through the given property.
    /// Every algorithm that reads incoming edges validates candidates through this: a reconcile
    /// commits the new property value before it updates the incoming records, so a stored incoming
    /// edge can name a parent that no longer references the subject.
    /// </summary>
    public bool CommitsEdgeTo(PropertyReference property, IInterceptorSubject target)
    {
        return _baselines.TryGetValue(property, out var value) &&
               StructuralValueScanner.Contains(property, value, target);
    }

    /// <summary>
    /// Walks the subject's structural properties and appends the occurrences their values contain, in
    /// property enumeration order and then value order, which is the order the release descent visits
    /// children in. Seeding reads the current getter output and commits it as the baseline;
    /// collecting reads the committed baseline. That one difference is why both callers exist.
    /// </summary>
    public void CollectStructuralChildren(
        IInterceptorSubject subject,
        List<(PropertyReference Property, SubjectOccurrence Occurrence)> children,
        bool seed)
    {
        var occurrences = LifecycleScratch.RentOccurrenceList();
        try
        {
            foreach (var entry in subject.Properties)
            {
                var metadata = entry.Value;
                if (!IsStructural(metadata))
                {
                    continue;
                }

                var property = new PropertyReference(subject, entry.Key);
                object? value;
                if (seed)
                {
                    value = metadata.GetValue?.Invoke(subject);
                    _baselines[property] = value;
                }
                else if (!_baselines.TryGetValue(property, out value))
                {
                    continue;
                }

                if (value is null)
                {
                    continue;
                }

                occurrences.Clear();
                StructuralValueScanner.CollectOccurrences(metadata.Type, value, occurrences);
                foreach (var occurrence in occurrences)
                {
                    children.Add((property, occurrence));
                }
            }
        }
        finally
        {
            LifecycleScratch.Return(occurrences);
        }
    }

    /// <summary>
    /// Whether the subject's structural properties already carry committed baselines, which is what
    /// tells an attach whether the subject's own component still has to be discovered. Checking the
    /// baselines rather than a flag keeps one source of truth: seeding is exactly what writes them.
    /// </summary>
    /// <remarks>
    /// The first structural property answers for all of them: seeding writes every baseline of a
    /// subject under the topology lock, so they are present or absent together.
    /// </remarks>
    public bool AreBaselinesSeeded(IInterceptorSubject subject)
    {
        foreach (var entry in subject.Properties)
        {
            if (IsStructural(entry.Value))
            {
                return _baselines.ContainsKey(new PropertyReference(subject, entry.Key));
            }
        }

        return true;
    }

    /// <summary>Drops every structural baseline of the subject; called when it leaves the graph.</summary>
    public void RemoveBaselines(IInterceptorSubject subject)
    {
        foreach (var entry in subject.Properties)
        {
            if (IsStructural(entry.Value))
            {
                _baselines.Remove(new PropertyReference(subject, entry.Key));
            }
        }
    }

    #endregion

    #region Anchors and executor claims

    /// <summary>
    /// Whether the subject carries a root anchor on this context. The anchor lives on the executor
    /// and is never mirrored into the graph state, so there is nothing to keep in sync.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsAnchored(IInterceptorSubject subject)
    {
        // One snapshot rather than the two getters: the anchor only means anything against the
        // context it anchors to, and the two getters can straddle a transition.
        subject.Executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
        return anchor != SubjectAttachmentAnchorKind.None && ReferenceEquals(attachedContext, Context);
    }

    /// <summary>
    /// Claims the subject for this context, or confirms an existing claim. Returns false when a
    /// competing context owns it, which is a lost race rather than a caller error and is answered by
    /// releasing this operation's own claims.
    /// </summary>
    public bool TryClaim(IInterceptorSubject subject, SubjectAttachmentAnchorKind anchor)
    {
        var executor = subject.Executor;
        while (true)
        {
            executor.TryGetAttachment(out var attachedContext, out var currentAnchor, out var revision);
            if (attachedContext is not null)
            {
                if (!ReferenceEquals(attachedContext, Context))
                {
                    return false;
                }

                // Already ours: never weaken the anchor a previous claim or an explicit attach set.
                if (anchor == SubjectAttachmentAnchorKind.None || currentAnchor == anchor ||
                    currentAnchor == SubjectAttachmentAnchorKind.Explicit)
                {
                    return true;
                }
            }

            if (executor.TryUpdateAttachment(revision, Context, anchor, out _))
            {
                return true;
            }
        }
    }

    /// <summary>Hands the subject's executor back, which is what makes it unattached again.</summary>
    public void ReleaseClaim(IInterceptorSubject subject)
    {
        var executor = subject.Executor;
        while (true)
        {
            executor.TryGetAttachment(out var attachedContext, out _, out var revision);
            if (!ReferenceEquals(attachedContext, Context))
            {
                return;
            }

            if (executor.TryUpdateAttachment(revision, null, SubjectAttachmentAnchorKind.None, out _))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Sets the subject's anchor: promoted by an explicit attach on an inherited subject, and
    /// cleared to <see cref="SubjectAttachmentAnchorKind.None"/> by an explicit detach. A non-null
    /// <paramref name="onlyFrom"/> writes the anchor only where the subject currently carries
    /// exactly that one; anchor adoption passes
    /// <see cref="SubjectAttachmentAnchorKind.Provisional"/>, because an explicit anchor that landed
    /// concurrently must survive rather than be degraded by the adoption.
    /// </summary>
    public void SetAnchor(IInterceptorSubject subject, SubjectAttachmentAnchorKind anchor, SubjectAttachmentAnchorKind? onlyFrom = null)
    {
        var executor = subject.Executor;
        while (true)
        {
            executor.TryGetAttachment(out var attachedContext, out var currentAnchor, out var revision);
            if (!ReferenceEquals(attachedContext, Context) ||
                (onlyFrom is null ? currentAnchor == anchor : currentAnchor != onlyFrom))
            {
                return;
            }

            if (executor.TryUpdateAttachment(revision, Context, anchor, out _))
            {
                return;
            }
        }
    }

    #endregion

    #region Component discovery

    /// <summary>
    /// Walks the structural component the value opens up, validating every subject against this
    /// context and collecting the unattached ones so they can be claimed as one batch before
    /// anything is published.
    /// </summary>
    /// <remarks>
    /// The walk descends into unattached subjects only. An attached same-context subject was
    /// validated when it was attached and its own component is already owned, so there is nothing
    /// below it that could be foreign. That bound is what keeps the cost proportional to the newly
    /// arriving subgraph rather than to the graph.
    /// </remarks>
    public void DiscoverComponent(
        Type declaredType,
        object? value,
        HashSet<IInterceptorSubject> visited,
        List<IInterceptorSubject> unattached)
    {
        var occurrences = LifecycleScratch.RentOccurrenceList();
        try
        {
            StructuralValueScanner.CollectOccurrences(declaredType, value, occurrences);
            foreach (var occurrence in occurrences)
            {
                DiscoverComponent(occurrence.Subject, visited, unattached);
            }
        }
        finally
        {
            LifecycleScratch.Return(occurrences);
        }
    }

    /// <inheritdoc cref="DiscoverComponent(Type,object?,HashSet{IInterceptorSubject},List{IInterceptorSubject})"/>
    public void DiscoverComponent(
        IInterceptorSubject start,
        HashSet<IInterceptorSubject> visited,
        List<IInterceptorSubject> unattached)
    {
        var pending = LifecycleScratch.RentSubjectStack();
        try
        {
            pending.Push(start);
            while (pending.Count > 0)
            {
                var subject = pending.Pop();
                if (!visited.Add(subject))
                {
                    continue;
                }

                var attachedContext = subject.Executor.AttachedContext;
                if (attachedContext is not null)
                {
                    if (!ReferenceEquals(attachedContext, Context))
                    {
                        throw new InvalidOperationException(
                            $"The subject '{subject.GetType().Name}' is owned by a different context and cannot " +
                            "join this graph. Detach it from that context first.");
                    }

                    continue;
                }

                unattached.Add(subject);

                foreach (var entry in subject.Properties)
                {
                    if (!IsStructural(entry.Value))
                    {
                        continue;
                    }

                    var childValue = entry.Value.GetValue?.Invoke(subject);
                    if (childValue is null)
                    {
                        continue;
                    }

                    var occurrences = LifecycleScratch.RentOccurrenceList();
                    try
                    {
                        StructuralValueScanner.CollectOccurrences(entry.Value.Type, childValue, occurrences);
                        foreach (var occurrence in occurrences)
                        {
                            pending.Push(occurrence.Subject);
                        }
                    }
                    finally
                    {
                        LifecycleScratch.Return(occurrences);
                    }
                }
            }
        }
        finally
        {
            LifecycleScratch.Return(pending);
        }
    }

    /// <summary>
    /// Claims every discovered unattached subject. A lost race releases the claims this call made
    /// and reports failure, so the caller can throw before touching the backing property.
    /// </summary>
    public bool TryClaimDiscovered(List<IInterceptorSubject> unattached, IInterceptorSubject? explicitRoot, SubjectAttachmentAnchorKind rootAnchor)
    {
        for (var i = 0; i < unattached.Count; i++)
        {
            var subject = unattached[i];
            var anchor = ReferenceEquals(subject, explicitRoot) ? rootAnchor : SubjectAttachmentAnchorKind.None;
            if (TryClaim(subject, anchor))
            {
                continue;
            }

            for (var j = 0; j < i; j++)
            {
                ReleaseClaim(unattached[j]);
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Hands back every claim that did not end up carrying ownership, which happens when the
    /// terminal or the authoritative getter reread throws, when a normalizing setter stores a
    /// different graph than the one that was validated, when a downstream write interceptor
    /// suppresses the continuation, and on the attach path when seeding commits something other
    /// than what discovery claimed.
    /// </summary>
    public void ReleaseUnusedClaims(List<IInterceptorSubject> claimed)
    {
        foreach (var subject in claimed)
        {
            if (!IsOwned(subject) && !IsAnchored(subject))
            {
                ReleaseClaim(subject);
            }
        }
    }

    #endregion
}
