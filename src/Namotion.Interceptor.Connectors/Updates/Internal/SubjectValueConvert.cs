using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

internal static class SubjectValueConvert
{
    // Read-only singleton for the defensive "value is neither IDictionary nor a non-string
    // IEnumerable" branch in ToSubjectDictionary. Mutation attempts throw, which is safer than
    // sharing a mutable empty Dictionary.
    private static readonly IDictionary EmptyDictionary = ImmutableDictionary<object, IInterceptorSubject>.Empty;

    /// <summary>
    /// Returns the value as a read-only list of subjects, using the covariant
    /// <see cref="IReadOnlyList{T}"/> pass-through fast path with typed-enumerable
    /// and reflective fallbacks.
    /// </summary>
    /// <remarks>
    /// The pass-through fast path is split into its own tiny method body so the JIT can inline
    /// it at every call site (the common case for typed <c>List&lt;Subject&gt;</c>). The
    /// typed-enumerable <c>.ToList</c> and <see cref="CollectSubjects"/> fallbacks live behind
    /// a separate non-inlined method to keep the entry point under the inlining size budget.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IReadOnlyList<IInterceptorSubject> ToSubjectList(object value)
    {
        if (value is IReadOnlyList<IInterceptorSubject> readOnlyList)
            return readOnlyList;

        return ToSubjectListSlow(value);
    }

    private static IReadOnlyList<IInterceptorSubject> ToSubjectListSlow(object value)
    {
        if (value is IEnumerable<IInterceptorSubject> typedEnumerable)
            return typedEnumerable.ToList();

        return CollectSubjects(value) ?? (IReadOnlyList<IInterceptorSubject>)[];
    }

    /// <summary>
    /// Returns the value as a mutable list of subjects, with null and
    /// <see cref="IEnumerable{T}"/> of subject fast paths.
    /// </summary>
    /// <remarks>
    /// Null and typed-enumerable fast paths stay in the entry method body so the JIT can
    /// inline both at call sites. The <see cref="CollectSubjects"/> reflective fallback is
    /// extracted into a separate non-inlined method to keep the entry point small.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static List<IInterceptorSubject> ToSubjectMutableList(object? value)
    {
        if (value is null)
            return [];

        if (value is IEnumerable<IInterceptorSubject> typedEnumerable)
            return typedEnumerable.ToList();

        return ToSubjectMutableListSlow(value);
    }

    private static List<IInterceptorSubject> ToSubjectMutableListSlow(object value) =>
        CollectSubjects(value) ?? [];

    /// <summary>
    /// Returns the value as an <see cref="IDictionary"/> for keyed subject access.
    /// Fast path: the value is already an <see cref="IDictionary"/> and is returned as-is (zero allocation).
    /// Slow path: read-only types that implement only <see cref="IEnumerable{T}"/> of <c>KeyValuePair</c>
    /// are materialized into a fresh <see cref="Dictionary{TKey,TValue}"/>. Callers must filter values
    /// against <see cref="IInterceptorSubject"/> inline since the returned dictionary may contain
    /// non-subject entries on the passthrough path.
    /// </summary>
    /// <remarks>
    /// The IDictionary fast path is split into its own tiny method body so the JIT can inline
    /// it at every call site. The KVP materialization fallback is extracted into a separate
    /// non-inlined method to keep the entry point under the inlining size budget.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IDictionary ToSubjectDictionary(object value)
    {
        if (value is IDictionary dictionary)
            return dictionary;

        return ToSubjectDictionarySlow(value);
    }

    private static IDictionary ToSubjectDictionarySlow(object value)
    {
        if (value is not IEnumerable enumerable || value is string)
            return EmptyDictionary;

        var result = new Dictionary<object, IInterceptorSubject>((value as ICollection)?.Count ?? 0);
        foreach (var item in enumerable)
        {
            if (item is null) continue;
            if (SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var subject))
                result[key!] = subject;
        }
        return result;
    }

    // Fallback subject collector for collection-classified properties whose runtime value
    // doesn't satisfy IEnumerable<IInterceptorSubject> covariance (e.g. ArrayList, or a
    // List<object> whose elements happen to be subjects). Callers always reach this through
    // collection-classified property paths, so a bare IInterceptorSubject value cannot arrive
    // here: the type system would reject the assignment, and hybrid IIS+IEnumerable<Subject>
    // values are caught by the earlier IEnumerable<IInterceptorSubject> check.
    private static List<IInterceptorSubject>? CollectSubjects(object value)
    {
        List<IInterceptorSubject>? list = null;

        if (value is not string && value is IEnumerable enumerable)
        {
            if (value.GetType().IsSubjectDictionaryType())
            {
                throw new NotSupportedException(
                    "A dictionary cannot be encoded as a positional subject collection. Declare the property as a dictionary type to preserve its keys and values.");
            }

            foreach (var item in enumerable)
            {
                if (item is IInterceptorSubject subjectItem)
                {
                    list ??= [];
                    list.Add(subjectItem);
                }
            }
        }

        return list;
    }
}
