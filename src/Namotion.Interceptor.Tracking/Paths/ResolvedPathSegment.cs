using System;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>
/// Cached accessor for one segment on its currently resolved subject. Immutable, and it stores only
/// the (subject, accessor) binding, never a resolved child or leaf value, so every walk re-reads the
/// live graph and a structural change is still detected by the next position's reference compare.
/// Accessor construction and invocation are the shared <see cref="PathWalker"/> helpers, so the cached
/// path and the by-name slow path cannot diverge.
/// </summary>
internal sealed class ResolvedPathSegment<TValue>
{
    private readonly Delegate? _accessor;
    private readonly bool _isTypedLeaf;

    private ResolvedPathSegment(IInterceptorSubject subject, Delegate? accessor, bool isTypedLeaf)
    {
        Subject = subject;
        _accessor = accessor;
        _isTypedLeaf = isTypedLeaf;
    }

    public IInterceptorSubject Subject { get; }

    internal static ResolvedPathSegment<TValue>? TryCreate(
        IInterceptorSubject subject, SubjectPropertyMetadata metadata, PathSegment segment)
    {
        if (!(metadata.IsIntercepted || metadata.IsDerived))
        {
            return null;
        }

        try
        {
            var isTypedLeaf = false;
            var accessor = segment.IsLeaf
                ? PathWalker.BuildLeafAccessor<TValue>(subject.GetType(), metadata, out isTypedLeaf)
                : PathWalker.BuildChildAccessor(subject.GetType(), metadata, segment);

            return new ResolvedPathSegment<TValue>(subject, accessor, isTypedLeaf);
        }
        catch
        {
            return null;
        }
    }

    internal IInterceptorSubject? ResolveChild(IInterceptorSubject subject, PathSegment segment)
        => PathWalker.ResolveChild(subject, _accessor, segment);

    internal SubjectPathValue<TValue> ReadLeaf(IInterceptorSubject subject)
        => PathWalker.ReadLeaf<TValue>(subject, _accessor, _isTypedLeaf);
}
