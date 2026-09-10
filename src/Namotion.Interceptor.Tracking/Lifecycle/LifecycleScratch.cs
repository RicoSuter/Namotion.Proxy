using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Thread-static scratch pools for the lifecycle's traversals. Every graph operation needs a handful
/// of short-lived lists, sets and stacks, and reentrancy (a release descent re-entered from a
/// removal callback, or an admission from a lifecycle callback) means several can be live on one
/// thread at once, so they are pooled rather than held as fields.
///
/// The pools are unbounded and never trimmed, so a thread retains buffers sized to the largest
/// graph operation it ever performed, which for discovery, release and reachability state is a
/// whole component.
///
/// Subject-keyed sets and maps use reference equality; see <see cref="OwnershipGraph"/> for why
/// graph membership is identity.
/// </summary>
internal static class LifecycleScratch
{
    // One closed generic per buffer type, so the thread-static and the rent/return pair are written
    // once rather than once per type; the named entry points below only carry each buffer's own
    // capacity hint and comparer.
    private static class Pool<T> where T : class
    {
        [ThreadStatic]
        internal static Stack<T>? Buffers;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryRent<T>(out T buffer) where T : class
    {
        var buffers = Pool<T>.Buffers;
        if (buffers is { Count: > 0 })
        {
            buffer = buffers.Pop();
            return true;
        }

        buffer = null!;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Recycle<T>(T buffer) where T : class
    {
        (Pool<T>.Buffers ??= new Stack<T>()).Push(buffer);
    }

    public static List<SubjectOccurrence> RentOccurrenceList()
        => TryRent<List<SubjectOccurrence>>(out var list) ? list : new List<SubjectOccurrence>(8);

    public static List<IncomingEdge> RentEdgeList()
        => TryRent<List<IncomingEdge>>(out var list) ? list : new List<IncomingEdge>(4);

    public static List<object?> RentIndexList()
        => TryRent<List<object?>>(out var list) ? list : new List<object?>(4);

    public static List<IInterceptorSubject> RentSubjectList()
        => TryRent<List<IInterceptorSubject>>(out var list) ? list : new List<IInterceptorSubject>(8);

    public static List<(PropertyReference Property, SubjectOccurrence Occurrence)> RentChildList()
        => TryRent<List<(PropertyReference, SubjectOccurrence)>>(out var list) ? list : new List<(PropertyReference, SubjectOccurrence)>(8);

    public static List<(IInterceptorSubject Subject, long Revision)> RentConsumedAnchorList()
        => TryRent<List<(IInterceptorSubject, long)>>(out var list) ? list : new List<(IInterceptorSubject, long)>(4);

    public static HashSet<IInterceptorSubject> RentSubjectSet()
        => TryRent<HashSet<IInterceptorSubject>>(out var set) ? set : new HashSet<IInterceptorSubject>(8, ReferenceEqualityComparer.Instance);

    public static Stack<IInterceptorSubject> RentSubjectStack()
        => TryRent<Stack<IInterceptorSubject>>(out var stack) ? stack : new Stack<IInterceptorSubject>(8);

    public static Dictionary<IInterceptorSubject, int> RentSubjectCounter()
        => TryRent<Dictionary<IInterceptorSubject, int>>(out var counter) ? counter : new Dictionary<IInterceptorSubject, int>(8, ReferenceEqualityComparer.Instance);

    public static Dictionary<IInterceptorSubject, List<object?>> RentIndexGroups()
        => TryRent<Dictionary<IInterceptorSubject, List<object?>>>(out var groups) ? groups : new Dictionary<IInterceptorSubject, List<object?>>(8, ReferenceEqualityComparer.Instance);

    public static void Return<TItem>(List<TItem> list)
    {
        list.Clear();
        Recycle(list);
    }

    public static void Return<TItem>(HashSet<TItem> set)
    {
        set.Clear();
        Recycle(set);
    }

    public static void Return<TItem>(Stack<TItem> stack)
    {
        stack.Clear();
        Recycle(stack);
    }

    public static void Return<TKey>(Dictionary<TKey, int> counter) where TKey : notnull
    {
        counter.Clear();
        Recycle(counter);
    }

    /// <summary>Returns the group map and every index list it handed out.</summary>
    public static void Return(Dictionary<IInterceptorSubject, List<object?>> groups)
    {
        foreach (var group in groups)
        {
            Return(group.Value);
        }

        groups.Clear();
        Recycle(groups);
    }
}
