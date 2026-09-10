using System;
using System.Diagnostics;
using System.Threading;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>
/// Owns the subscribe-before-read segment chain for one <see cref="SubjectPathSubscription{TValue}"/>: the
/// installed per-segment listeners, the observer recorded per position (slot identity), the subject each
/// segment reads on, and the cached per-segment accessors. It creates the
/// <see cref="PathSegmentObserver{TValue}"/> instances, installs and tears down listeners, walks the graph,
/// and reports divergence, but holds NO lock of its own.
/// </summary>
/// <remarks>
/// LOCK CONTRACT: every method is called under the coordinator's lock, EXCEPT the <c>_resolvedSegments</c>
/// reads that <see cref="Walk"/> performs when it is called from the lock-free
/// <see cref="SubjectPathSubscription{TValue}.Current"/>; those reads go through <see cref="Volatile"/>. The
/// root and segment array are captured into a local before use because dispose nulls them (via
/// <see cref="Volatile"/>) to release the graph and any captured key/index objects; a null capture reads as
/// unresolved, and the array reference is swapped, never mutated in place. Subscribe-before-read ordering in
/// <see cref="BuildFrom"/> (install the listener, THEN read to resolve the next subject) is load-bearing for
/// the missed-write guarantee and must be preserved exactly.
/// </remarks>
internal sealed class PathSubscriptionChain<TValue>
{
    private readonly SubjectPathSubscription<TValue> _coordinator;
    // Nulled by ReleaseReferences on dispose (Volatile.Write) so a retained handle does not pin the graph
    // root; Walk reads it into a local first. Mirrors PropertyChangeSubscription's _subject handling.
    private IInterceptorSubject? _root;
    // Nulled by ReleaseReferences on dispose (Volatile.Write) so a retained handle does not pin the decomposed
    // segments and any evaluated dictionary-key or index objects they captured; Walk reads it into a local
    // first. The length is cached separately so the lock-free Length stays valid after the array is released.
    private PathSegment[]? _segments;
    private readonly int _length;

    // All arrays are indexed by segment position and are mutated only under the coordinator's lock.
    // _segmentHandles[p] is the installed listener for position p; _segmentObservers[p] is the CURRENT
    // observer for position p (the slot-identity record a late callback is matched against);
    // _resolvedSubjects[p] is the subject segment p is read on. Entries beyond the resolved prefix stay null.
    private readonly IDisposable?[] _segmentHandles;
    private readonly PathSegmentObserver<TValue>?[] _segmentObservers;
    private readonly IInterceptorSubject?[] _resolvedSubjects;
    // Immutable subject/accessor pairs used by the hot validating walk. Entries are atomically replaced
    // for lock-free Current reads and cleared with the same suffix as the corresponding subject/listener.
    private readonly ResolvedPathSegment<TValue>?[] _resolvedSegments;

    internal PathSubscriptionChain(SubjectPathSubscription<TValue> coordinator, IInterceptorSubject root, PathSegment[] segments)
    {
        _coordinator = coordinator;
        _root = root;
        _segments = segments;
        _length = segments.Length;

        var length = _length;
        _segmentHandles = new IDisposable?[length];
        _segmentObservers = new PathSegmentObserver<TValue>?[length];
        _resolvedSubjects = new IInterceptorSubject?[length];
        _resolvedSegments = new ResolvedPathSegment<TValue>?[length];
    }

    /// <summary>The number of segments in the path. Immutable; safe to read without the lock even after dispose.</summary>
    internal int Length => _length;

    /// <summary>
    /// Walks the segments from the root into <paramref name="buffer"/> and returns the observed leaf value.
    /// Reads only the immutable root/segments and the volatile <c>_resolvedSegments</c> entries, so it is
    /// safe either under the coordinator's lock (the seed and every event/retrack walk, sharing the
    /// coordinator's scratch buffer) or lock-free (<see cref="SubjectPathSubscription{TValue}.Current"/>
    /// with a rented buffer). The caller supplies the buffer.
    /// </summary>
    internal SubjectPathValue<TValue> Walk(IInterceptorSubject?[] buffer)
    {
        // _root and _segments are read lock-free by Current and may be nulled by ReleaseReferences on a racing
        // dispose; capture both into locals (the array reference is swapped atomically, never mutated in place,
        // so a captured array is always fully valid) and treat either being null as unresolved, the documented
        // post-dispose Current result.
        var root = Volatile.Read(ref _root);
        var segments = Volatile.Read(ref _segments);
        return root is null || segments is null
            ? SubjectPathValue<TValue>.Unresolved
            : PathWalker.Walk(segments, root, buffer, _resolvedSegments);
    }

    /// <summary>
    /// The first position where <paramref name="walkedSubjects"/> read on a different subject (reference
    /// identity) than the subscribed chain (<c>_resolvedSubjects</c>), or -1 when the chain is intact. This
    /// one comparison covers every structural case: a reassigned intermediate, a heal (null -> subject) and
    /// a break (subject -> null) each first differ at the affected position. Callers must hold the lock.
    /// </summary>
    internal int FindDivergence(IInterceptorSubject?[] walkedSubjects)
    {
        Debug.Assert(_coordinator.IsLockHeldByCurrentThread, "FindDivergence must be called under the coordinator lock.");
        for (var position = 0; position < _length; position++)
        {
            if (!ReferenceEquals(walkedSubjects[position], _resolvedSubjects[position]))
            {
                return position;
            }
        }

        return -1;
    }

    /// <summary>
    /// Re-reads only the resolved leaf on its cached subject (the <see cref="SubjectPathValidation.LeafOnly"/>
    /// leaf-write head), with no from-root walk, divergence check, or retrack. Defensive null checks return
    /// unresolved; when the leaf observer legitimately fires, both the cached accessor and the resolved leaf
    /// subject are set. The read shares the walk's never-throw contract: a throwing getter resolves as
    /// unresolved rather than propagating out of the under-lock computation. Callers must hold the lock.
    /// </summary>
    internal SubjectPathValue<TValue> ReadResolvedLeaf()
    {
        Debug.Assert(_coordinator.IsLockHeldByCurrentThread, "ReadResolvedLeaf must be called under the coordinator lock.");
        var leafPosition = _length - 1;
        var resolvedSegment = _resolvedSegments[leafPosition];
        var leafSubject = _resolvedSubjects[leafPosition];
        if (resolvedSegment is null || leafSubject is null)
        {
            return SubjectPathValue<TValue>.Unresolved;
        }

        try
        {
            return resolvedSegment.ReadLeaf(leafSubject);
        }
        catch
        {
            return SubjectPathValue<TValue>.Unresolved;
        }
    }

    /// <summary>
    /// True when <paramref name="cause"/> is a write to the chain's current resolved leaf (subject reference
    /// and name match), the condition that makes an intact-chain, resolved event a
    /// <see cref="SubjectPathChangeKind.ValueChange"/>. Callers must hold the lock.
    /// </summary>
    internal bool IsResolvedLeafWrite(in SubjectPropertyChange cause)
    {
        Debug.Assert(_coordinator.IsLockHeldByCurrentThread, "IsResolvedLeafWrite must be called under the coordinator lock.");
        // _segments is non-null here: this runs under the lock and dispose (which nulls it) also runs under the
        // lock, and a disposed subscription never reaches event computation.
        var leafSubject = _resolvedSubjects[_length - 1];
        var leafName = _segments![_length - 1].PropertyName;
        return leafSubject is not null
            && ReferenceEquals(cause.Property.Subject, leafSubject)
            && string.Equals(cause.Property.Name, leafName, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="observer"/> is still the current observer recorded for its position: a
    /// callback fired by a torn-down listener fails this check and is dropped rather than acting on a stale
    /// chain. Callers must hold the lock.
    /// </summary>
    internal bool IsCurrentObserver(PathSegmentObserver<TValue> observer)
    {
        Debug.Assert(_coordinator.IsLockHeldByCurrentThread, "IsCurrentObserver must be called under the coordinator lock.");
        return _segmentObservers[observer.Position] == observer;
    }

    /// <summary>
    /// Installs the subscribe-before-read chain from <paramref name="startPosition"/>, walking forward from
    /// <paramref name="startSubject"/>. For each segment the listener is installed BEFORE the read that
    /// resolves the next subject, so a write landing in that window is never missed. The build stops at the
    /// first unresolved intermediate, leaving the suffix (handles/observers/subjects past that position)
    /// null. Callers must hold the coordinator's lock.
    /// </summary>
    internal void BuildFrom(int startPosition, IInterceptorSubject startSubject)
    {
        Debug.Assert(_coordinator.IsLockHeldByCurrentThread, "BuildFrom must be called under the coordinator lock.");
        // _segments is non-null here: BuildFrom runs under the lock (ctor build and retrack), and dispose,
        // which nulls it, also runs under the lock, so a released array is never observed mid-build.
        var segments = _segments!;
        var subject = startSubject;
        for (var position = startPosition; position < _length; position++)
        {
            var segment = segments[position];

            // Record the current observer for this position (slot identity) and the subject this segment
            // reads on, then install the listener.
            var observer = new PathSegmentObserver<TValue>(_coordinator) { Position = position };
            _segmentObservers[position] = observer;
            _resolvedSubjects[position] = subject;

            // Subscribe FIRST, then resolve the next subject below: the install must precede the read.
            _segmentHandles[position] = PropertyChangeSubscription.Create(
                new PropertyReference(subject, segment.PropertyName), observer);

            var resolvedSegment = TryResolveSegment(subject, segment);
            Volatile.Write(ref _resolvedSegments[position], resolvedSegment);

            if (segment.IsLeaf)
            {
                // The leaf is subscribed but resolves no next subject.
                return;
            }

            IInterceptorSubject? child;
            try
            {
                child = resolvedSegment?.ResolveChild(subject, segment);
            }
            catch
            {
                child = null;
            }

            if (child is null)
            {
                // Unresolved intermediate: stop, leaving the suffix torn down (null).
                return;
            }

            subject = child;
        }
    }

    /// <summary>
    /// Resolves and caches the segment accessor by name against <paramref name="subject"/>. Name resolution
    /// and accessor construction are non-throwing: a missing or non-subscribable property returns null.
    /// </summary>
    private static ResolvedPathSegment<TValue>? TryResolveSegment(IInterceptorSubject subject, PathSegment segment)
    {
        if (!subject.Properties.TryGetValue(segment.PropertyName, out var metadata))
        {
            return null;
        }

        return ResolvedPathSegment<TValue>.TryCreate(subject, metadata, segment);
    }

    /// <summary>
    /// Tears down the chain from <paramref name="fromPosition"/> onward: disposes and clears each listener,
    /// its observer, and the subject it was read on. Entries the suffix owned become null so a later rebuild
    /// (or dispose) starts from a clean tail. Callers must hold the coordinator's lock.
    /// </summary>
    internal void DisposeSuffix(int fromPosition)
    {
        Debug.Assert(_coordinator.IsLockHeldByCurrentThread, "DisposeSuffix must be called under the coordinator lock.");
        for (var position = fromPosition; position < _length; position++)
        {
            _segmentHandles[position]?.Dispose();
            _segmentHandles[position] = null;
            _segmentObservers[position] = null;
            _resolvedSubjects[position] = null;
            Volatile.Write(ref _resolvedSegments[position], null);
        }
    }

    /// <summary>Tears down the entire chain (equivalent to <c>DisposeSuffix(0)</c>). Callers must hold the lock.</summary>
    internal void DisposeAll() => DisposeSuffix(0);

    /// <summary>
    /// Releases the references a retained disposed handle would otherwise pin: the graph root and the
    /// decomposed segment array (whose fixed dictionary-key or index arguments may be arbitrary objects, up to
    /// and including the root). Both are nulled via <c>Volatile.Write</c>, paired with the <c>Volatile.Read</c>
    /// in <see cref="Walk"/>, which then resolves to unresolved. The cached <see cref="Length"/> stays valid.
    /// Called after <see cref="DisposeAll"/> has cleared the per-position subject/accessor entries.
    /// </summary>
    internal void ReleaseReferences()
    {
        Volatile.Write(ref _root, null);
        Volatile.Write(ref _segments, null);
    }
}
