using System.Collections;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>One subject occurrence inside a structural property value, with its ordinal or key.</summary>
internal readonly struct SubjectOccurrence(IInterceptorSubject subject, object? index)
{
    public readonly IInterceptorSubject Subject = subject;
    public readonly object? Index = index;
}

/// <summary>
/// Captures the subject occurrences of structural property values. Seeding and reconciliation use
/// this interpretation; release and reachability read the committed capture without invoking user code.
/// </summary>
internal static class StructuralValueScanner
{
    /// <summary>
    /// Appends every subject occurrence of the value, in enumeration order, with the ordinal or key
    /// that identifies it.
    /// </summary>
    /// <remarks>
    /// Dictionary values retain their keys even when they also implement <see cref="ICollection"/>.
    /// The declared type is supplied separately so scans during AddProperties admission can run
    /// before the property's metadata is published.
    /// </remarks>
    public static void CollectOccurrences(Type declaredType, object? value, List<SubjectOccurrence> occurrences)
    {
        switch (value)
        {
            case null:
                return;

            case IInterceptorSubject subject:
                occurrences.Add(new SubjectOccurrence(subject, null));
                return;

            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is IInterceptorSubject subjectItem)
                    {
                        occurrences.Add(new SubjectOccurrence(subjectItem, entry.Key));
                    }
                }

                return;

            case string:
                return;

            case IEnumerable enumerable:
            {
                // A pair carries its key, but a dictionary type is free to enumerate as its values
                // instead (IReadOnlyDictionary<string, Device> plus IEnumerable<Device> hides the pair
                // enumerator behind an explicit implementation), so the keyed arm has to be total: an
                // item that is itself a subject is recorded at its position rather than dropped.
                var isKeyed = HasKeyedEntries(declaredType, enumerable);
                var index = 0;
                foreach (var item in enumerable)
                {
                    if (isKeyed && item is not null &&
                        SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var keyedItem))
                    {
                        occurrences.Add(new SubjectOccurrence(keyedItem, key));
                    }
                    else if (item is IInterceptorSubject subjectItem)
                    {
                        occurrences.Add(new SubjectOccurrence(subjectItem, index));
                    }

                    index++;
                }

                return;
            }
        }
    }

    // The declared type answers first because a typed property is the common case and needs no
    // GetType call, but a property declared object or IEnumerable can still carry a read-only
    // dictionary, and only the value's own type reveals that.
    private static bool HasKeyedEntries(Type declaredType, object value)
    {
        return declaredType.IsSubjectDictionaryType() || value.GetType().IsSubjectDictionaryType();
    }

    /// <summary>
    /// Whether the value could hold subjects at all. Mirrors the check the reconcile short circuit
    /// uses to skip values that are neither a subject nor a container.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanHoldSubjects(object? value)
    {
        return value is (null or IInterceptorSubject or IEnumerable) && value is not string;
    }
}
