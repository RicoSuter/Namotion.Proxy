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
/// Property baselines describe the committed outgoing relation. While publication is incomplete,
/// a property journal also records its installed incoming occurrences: a nested write or release
/// must reconcile those occurrences rather than assuming every desired edge was already installed.
/// Reachability still validates incoming candidates against the committed baselines.
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
    // strand a subject whose hash mutates while it is owned. Releasing records stay in this map
    // as identity tokens until final notification cleanup; ownership queries filter those records.
    private readonly ConcurrentDictionary<IInterceptorSubject, SubjectOwnership> _owned = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PropertyReference, (object? Value, long Revision)> _baselines = new(PropertyReference.Comparer);

    // A nested write can replace a value and restore the exact same instance before returning.
    private long _nextBaselineRevision;
    private PropertyEdgeJournal? _firstPropertyJournal;
    private Dictionary<PropertyReference, PropertyEdgeJournal>? _additionalPropertyJournals;
    private (PropertyReference Property, SubjectOwnership? Ownership) _activeSeedingGetter;
    private Dictionary<(PropertyReference Property, SubjectOwnership? Ownership), int>? _suspendedSeedingGetters;

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
        return _owned.TryGetValue(subject, out var ownership) && !ownership.IsReleasing ? ownership : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsOwned(IInterceptorSubject subject)
    {
        return TryGetOwnership(subject) is not null;
    }

    public SubjectOwnership AddOwnership(IInterceptorSubject subject)
    {
        var ownership = new SubjectOwnership();
        _owned[subject] = ownership;
        return ownership;
    }

    /// <summary>
    /// Whether the subject is between losing graph ownership and having its executor handed
    /// back, which is the window its detach callbacks run in.
    /// </summary>
    /// <remarks>
    /// In that window the subject is attached but unowned, which is also the shape of a subject an
    /// attach descent has claimed but not published yet. The two need opposite admission
    /// behaviour, so the release marks its own; nothing else can tell them apart.
    /// </remarks>
    public bool IsReleasing(IInterceptorSubject subject)
    {
        return _owned.TryGetValue(subject, out var ownership) && ownership.IsReleasing;
    }

    /// <summary>Queries the release identity while the caller holds the topology gate.</summary>
    public bool IsCurrentRelease(IInterceptorSubject subject, SubjectOwnership ownership)
    {
        return ownership.IsReleasing && _owned.TryGetValue(subject, out var current) && ReferenceEquals(current, ownership);
    }

    /// <inheritdoc cref="IsReleasing"/>
    public void ClearReleasing(IInterceptorSubject subject, SubjectOwnership ownership)
    {
        // A failure before marking must leave the live record intact. Reattachment replaces this
        // entry, so an older queued release must not remove a newer ownership lifetime.
        if (IsCurrentRelease(subject, ownership))
        {
            _owned.TryRemove(subject, out _);
        }
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
        return _baselines.GetValueOrDefault(property).Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetBaseline(PropertyReference property, object? value)
    {
        _baselines[property] = (value, ++_nextBaselineRevision);
    }

    public long GetBaselineRevision(PropertyReference property)
    {
        return _baselines.GetValueOrDefault(property).Revision;
    }

    public PropertyEdgeJournal? GetPropertyJournal(PropertyReference property, SubjectOwnership? ownership)
    {
        var first = _firstPropertyJournal;
        if (first is not null && first.Property == property && ReferenceEquals(first.Ownership, ownership)) return first;
        return _additionalPropertyJournals is { Count: > 0 } &&
               _additionalPropertyJournals.TryGetValue(property, out var journal) &&
               ReferenceEquals(journal.Ownership, ownership) ? journal : null;
    }

    public PropertyEdgeJournal BeginPropertyJournal(PropertyReference property, SubjectOwnership ownership, List<SubjectOccurrence>? installed = null)
    {
        var journal = GetPropertyJournal(property, ownership);
        if (journal is not null)
        {
            journal.Users++;
            return journal;
        }

        journal = LifecycleScratch.RentPropertyJournal();
        journal.Initialize(property, ownership, installed);
        if (_firstPropertyJournal is null) _firstPropertyJournal = journal;
        else (_additionalPropertyJournals ??= new(PropertyReference.Comparer))[property] = journal;
        return journal;
    }

    public void EndPropertyJournal(PropertyEdgeJournal journal)
    {
        if (--journal.Users > 0) return;
        // A failed descent can leave desired edges unpublished. Keep its actual occurrences until
        // a later write settles the property or releasing the owner drains what was installed.
        if (!journal.IsComplete && ReferenceEquals(TryGetOwnership(journal.Property.Subject), journal.Ownership)) return;
        if (ReferenceEquals(_firstPropertyJournal, journal)) _firstPropertyJournal = null;
        else if (_additionalPropertyJournals is not null &&
                 _additionalPropertyJournals.TryGetValue(journal.Property, out var current) && ReferenceEquals(current, journal))
        {
            _additionalPropertyJournals.Remove(journal.Property);
        }

        LifecycleScratch.Return(journal);
    }

    public void RecordIncomingAdded(PropertyReference property, IInterceptorSubject child, object? index)
    {
        if (_firstPropertyJournal is null && _additionalPropertyJournals is not { Count: > 0 }) return;
        GetPropertyJournal(property, TryGetOwnership(property.Subject))?.Add(child, index);
    }

    public void RecordIncomingRemoved(PropertyReference property, IInterceptorSubject child)
    {
        if (_firstPropertyJournal is null && _additionalPropertyJournals is not { Count: > 0 }) return;
        GetPropertyJournal(property, TryGetOwnership(property.Subject))?.RemoveLast(child);
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
               StructuralValueScanner.Contains(property, value.Value, target);
    }

    /// <summary>
    /// Walks the subject's structural properties and appends the occurrences their values contain, in
    /// property enumeration order and then value order, which is the order the release descent visits
    /// children in. Seeding reads the current getter output and commits it as the baseline;
    /// collecting reads the committed baseline. That one difference is why both callers exist.
    /// </summary>
    public void CollectStructuralChildren(
        IInterceptorSubject subject,
        List<(PropertyReference Property, SubjectOccurrence Occurrence, long BaselineRevision)> children,
        bool seed,
        List<(PropertyEdgeJournal Journal, long Revision)>? seedingJournals = null)
    {
        var ownership = TryGetOwnership(subject);
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
                if (!seed && GetPropertyJournal(property, ownership) is { } installedJournal)
                {
                    occurrences.Clear();
                    installedJournal.CopyTo(occurrences);
                    foreach (var occurrence in occurrences) children.Add((property, occurrence, GetBaselineRevision(property)));
                    continue;
                }

                var hadBaseline = _baselines.TryGetValue(property, out var previousBaseline);
                if (seed && hadBaseline)
                {
                    // A nested write to another property already committed and published it.
                    continue;
                }

                object? value = previousBaseline.Value;
                if (seed)
                {
                    if (!IsSeedOwnerCurrent(subject, ownership))
                    {
                        return;
                    }

                    value = ReadSeedingGetter(property, metadata, ownership);
                    if (!IsSeedOwnerCurrent(subject, ownership))
                    {
                        return;
                    }

                    if (previousBaseline.Revision != GetBaselineRevision(property))
                    {
                        continue;
                    }

                }
                else if (!hadBaseline)
                {
                    continue;
                }

                occurrences.Clear();
                StructuralValueScanner.CollectOccurrences(metadata.Type, value, occurrences);
                if (seed)
                {
                    // Getters and enumerators can release this owner or publish a newer property
                    // before returning. Neither continuation may recreate their obsolete edges.
                    if (!IsSeedOwnerCurrent(subject, ownership))
                    {
                        return;
                    }

                    if (previousBaseline.Revision != GetBaselineRevision(property))
                    {
                        continue;
                    }

                    if (occurrences.Count > 0)
                    {
                        var journal = BeginPropertyJournal(property, ownership!);
                        SetBaseline(property, value);
                        journal.IsComplete = false;
                        seedingJournals!.Add((journal, GetBaselineRevision(property)));
                    }
                    else
                    {
                        SetBaseline(property, value);
                    }
                }

                var baselineRevision = seed ? GetBaselineRevision(property) : previousBaseline.Revision;
                foreach (var occurrence in occurrences)
                {
                    children.Add((property, occurrence, baselineRevision));
                }
            }
        }
        finally
        {
            LifecycleScratch.Return(occurrences);
        }
    }

    public bool IsEvaluatingSeedingGetter(PropertyReference property)
    {
        if (_activeSeedingGetter.Property.Equals(property))
        {
            return ReferenceEquals(_activeSeedingGetter.Ownership, TryGetOwnership(property.Subject));
        }

        return _suspendedSeedingGetters is { Count: > 0 } &&
               _suspendedSeedingGetters.ContainsKey((property, TryGetOwnership(property.Subject)));
    }

    private object? ReadSeedingGetter(PropertyReference property, SubjectPropertyMetadata metadata, SubjectOwnership? ownership)
    {
        var preceding = _activeSeedingGetter;
        var isNested = preceding.Property.Subject is not null;
        if (isNested)
        {
            // Only reentrant getter evaluation needs a lookup for older frames.
            _suspendedSeedingGetters ??= new();
            _suspendedSeedingGetters[preceding] = _suspendedSeedingGetters.GetValueOrDefault(preceding) + 1;
        }

        _activeSeedingGetter = (property, ownership);
        try
        {
            return metadata.GetValue?.Invoke(property.Subject);
        }
        finally
        {
            _activeSeedingGetter = preceding;
            if (isNested)
            {
                var remaining = _suspendedSeedingGetters![preceding] - 1;
                if (remaining == 0) _suspendedSeedingGetters.Remove(preceding);
                else _suspendedSeedingGetters[preceding] = remaining;
            }
        }
    }

    public bool IsSeedOwnerCurrent(IInterceptorSubject subject, SubjectOwnership? ownership)
    {
        return ownership is not null
            ? ReferenceEquals(ownership, TryGetOwnership(subject))
            : IsAnchored(subject) && !IsReleasing(subject);
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
                var property = new PropertyReference(subject, entry.Key);
                _baselines.Remove(property);
                var first = _firstPropertyJournal;
                if (first is not null && first.Property == property)
                {
                    _firstPropertyJournal = null;
                    if (first.Users == 0) LifecycleScratch.Return(first);
                }

                if (_additionalPropertyJournals is not null &&
                    _additionalPropertyJournals.Remove(property, out var journal) && journal.Users == 0)
                {
                    LifecycleScratch.Return(journal);
                }
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
