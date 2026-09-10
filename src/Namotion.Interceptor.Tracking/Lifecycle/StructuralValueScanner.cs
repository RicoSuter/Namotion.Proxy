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
/// Reads structural property values: turns one into its subject occurrences, and answers whether it
/// still contains a given subject. The whole ownership model is defined over these occurrences, so
/// every reader (reconcile, attach seeding, release descent, committed-edge validation) goes
/// through this one interpretation of a value's shape.
/// </summary>
internal static class StructuralValueScanner
{
    /// <summary>
    /// Appends every subject occurrence of the value, in enumeration order, with the ordinal or key
    /// that identifies it.
    /// </summary>
    /// <remarks>
    /// Hot paths (<see cref="IDictionary"/>, <see cref="ICollection"/>) come before the
    /// string/<see cref="IEnumerable"/> arms so common writes do not pay extra type checks; the
    /// trailing arm handles read-only wrappers that implement neither. The declared type is a
    /// parameter rather than a metadata lookup so the scan also works during AddProperties
    /// admission, where the property's metadata is not published yet.
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

            case ICollection collection:
            {
                var index = 0;
                foreach (var item in collection)
                {
                    if (item is IInterceptorSubject subjectItem)
                    {
                        occurrences.Add(new SubjectOccurrence(subjectItem, index));
                    }

                    index++;
                }

                return;
            }

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

    /// <summary>Whether the value still contains the subject at all, at any occurrence.</summary>
    /// <remarks>
    /// Deliberately not <see cref="CollectOccurrences"/> followed by a scan: this runs once per
    /// candidate edge inside the reachability walk, where the indices are never used, so it neither
    /// fills a list nor enumerates past the first match.
    /// </remarks>
    public static bool Contains(PropertyReference property, object? value, IInterceptorSubject target)
    {
        switch (value)
        {
            case null:
                return false;

            case IInterceptorSubject subject:
                return ReferenceEquals(subject, target);

            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (ReferenceEquals(entry.Value, target))
                    {
                        return true;
                    }
                }

                return false;

            case ICollection collection:
                foreach (var item in collection)
                {
                    if (ReferenceEquals(item, target))
                    {
                        return true;
                    }
                }

                return false;

            case string:
                return false;

            case IEnumerable enumerable:
            {
                // Same total keyed arm as CollectOccurrences: a subject the scan records at a
                // position has to be found here too, or the reachability walk would release it.
                var isKeyed = HasKeyedEntries(property.Metadata.Type, enumerable);
                foreach (var item in enumerable)
                {
                    if (ReferenceEquals(item, target))
                    {
                        return true;
                    }

                    if (isKeyed && item is not null &&
                        SubjectLookup.TryGetSubjectFromKeyValuePair(item, out _, out var subjectItem) &&
                        ReferenceEquals(subjectItem, target))
                    {
                        return true;
                    }
                }

                return false;
            }

            default:
                return false;
        }
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
