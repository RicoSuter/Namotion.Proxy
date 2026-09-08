using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Connectors.Monitoring;

/// <summary>
/// Decides which sources a branch-scoped wait must observe.
/// </summary>
internal static class SourceScope
{
    /// <summary>
    /// True when the source's root and the anchor lie on the same root-to-leaf path, in either
    /// direction: the source is rooted above the anchor and may claim into it, or rooted inside it.
    /// A source on a sibling branch is in neither set, which is what stops an unrelated failing
    /// connection from blocking a wait. Walks using caller-supplied scratch collections.
    /// </summary>
    /// <remarks>
    /// Every caller of this overload is <c>SourceMonitor.IsBranchSynchronized</c>, which already
    /// holds the monitor's lock and re-evaluates on every property-reference add/remove while any
    /// wait is pending - reusing scratch collections there turns a per-source, per-re-evaluation
    /// allocation into none. <paramref name="visitedScratch"/> and <paramref name="pendingScratch"/>
    /// are cleared before this method returns, so passing the same instances into a later,
    /// unrelated call is safe.
    /// </remarks>
    internal static bool IsInScope(
        ISubjectSource source, IInterceptorSubject anchor,
        HashSet<IInterceptorSubject> visitedScratch, Stack<IInterceptorSubject> pendingScratch)
    {
        var sourceRoot = source.RootSubject;
        return IsAncestorOrSelf(sourceRoot, anchor, visitedScratch, pendingScratch) ||
               IsAncestorOrSelf(anchor, sourceRoot, visitedScratch, pendingScratch);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="target"/> or reachable by walking
    /// up from it through tracked parents.
    /// </summary>
    /// <remarks>
    /// Nothing enforces that the parent graph is acyclic (two subjects can reference each other,
    /// directly or through a longer chain), so the walk tracks visited subjects and cannot loop.
    /// </remarks>
    private static bool IsAncestorOrSelf(
        IInterceptorSubject candidate, IInterceptorSubject target,
        HashSet<IInterceptorSubject> visitedScratch, Stack<IInterceptorSubject> pendingScratch)
    {
        if (ReferenceEquals(candidate, target))
        {
            return true;
        }

        return SearchGraph(candidate, target, visitedScratch, pendingScratch);
    }

    private static bool SearchGraph(
        IInterceptorSubject candidate, IInterceptorSubject start,
        HashSet<IInterceptorSubject> visited, Stack<IInterceptorSubject> pending)
    {
        try
        {
            pending.Push(start);

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visited.Add(current))
                {
                    continue;
                }

                if (ReferenceEquals(current, candidate))
                {
                    return true;
                }

                foreach (var parent in current.GetParents())
                {
                    pending.Push(parent.Property.Subject);
                }
            }

            return false;
        }
        finally
        {
            // Scratch collections may be reused by the caller across walks (including the second,
            // opposite-direction walk within the same IsInScope call) - never leak subjects from
            // this walk into the next one, on any return path.
            visited.Clear();
            pending.Clear();
        }
    }
}
