using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking;

/// <summary>
/// Cached type checks for determining whether a property type can contain interceptor subjects.
/// Used as a fast pre-filter to avoid unnecessary work on types that cannot hold subjects
/// (e.g., primitives, strings, simple value types).
/// </summary>
public static class SubjectPropertyTypeExtensions
{
    // Cross-cache reads are safe under concurrent GetOrAdd because every classifier is a pure
    // function of Type. The dependency graph (Reference -> Collection, Dictionary; Collection ->
    // Dictionary; Dictionary -> nothing) is acyclic, so racing factory invocations on different
    // Types converge to the same result without deadlock or inconsistency.
    // Nullable<T> is normalised to T inside the factories rather than at the entry points, so a
    // cache hit stays a single dictionary lookup; the nullable and underlying types simply get
    // one entry each.
    private static readonly ConcurrentDictionary<Type, bool> CanContainSubjectsCache = new();
    private static readonly ConcurrentDictionary<Type, bool> IsSubjectReferenceTypeCache = new();
    private static readonly ConcurrentDictionary<Type, bool> IsSubjectCollectionTypeCache = new();
    private static readonly ConcurrentDictionary<Type, bool> IsSubjectDictionaryTypeCache = new();

    /// <summary>
    /// Checks whether a property can contain subjects, using both a JIT-constant fast path
    /// via <typeparamref name="TProperty"/> and a runtime fallback via the declared
    /// <paramref name="type"/>. <typeparamref name="TProperty"/> is a hint: it may be
    /// <c>object</c> when values are boxed through non-generic paths (this applies to
    /// <c>TProperty</c> throughout the interceptor interfaces, not just here).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanContainSubjects<TProperty>(this Type type)
    {
        if (typeof(TProperty).IsPrimitive ||
            typeof(TProperty) == typeof(decimal) ||
            typeof(TProperty) == typeof(string) ||
            typeof(TProperty) == typeof(DateTime) ||
            typeof(TProperty) == typeof(DateTimeOffset) ||
            typeof(TProperty) == typeof(TimeSpan) ||
            typeof(TProperty) == typeof(Guid))
        {
            return false;
        }

        return type.CanContainSubjects();
    }

    /// <summary>
    /// Returns true if the given type could potentially contain or be an <see cref="IInterceptorSubject"/>.
    /// Results are cached per type for O(1) lookups after the first call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanContainSubjects(this Type type)
    {
        return CanContainSubjectsCache.TryGetValue(type, out var result)
            ? result
            : CanContainSubjectsSlow(type);
    }

    /// <summary>
    /// Returns true if the given type is a single subject reference: an <see cref="IInterceptorSubject"/>,
    /// <see cref="object"/>, or a plain interface that could carry a subject. Generic interfaces over
    /// non-subject content (e.g. <c>IList&lt;int&gt;</c>) and enumerable subjects are excluded.
    /// Mutually exclusive with <see cref="IsSubjectCollectionType"/> and <see cref="IsSubjectDictionaryType"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSubjectReferenceType(this Type type)
    {
        return IsSubjectReferenceTypeCache.TryGetValue(type, out var result)
            ? result
            : IsSubjectReferenceTypeSlow(type);
    }

    /// <summary>
    /// Returns true if the given type is a collection of subject references.
    /// Mutually exclusive with <see cref="IsSubjectReferenceType"/> and <see cref="IsSubjectDictionaryType"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSubjectCollectionType(this Type type)
    {
        return IsSubjectCollectionTypeCache.TryGetValue(type, out var result)
            ? result
            : IsSubjectCollectionTypeSlow(type);
    }

    /// <summary>
    /// Returns true if the given type is a dictionary with subject reference values.
    /// Mutually exclusive with <see cref="IsSubjectReferenceType"/> and <see cref="IsSubjectCollectionType"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSubjectDictionaryType(this Type type)
    {
        return IsSubjectDictionaryTypeCache.TryGetValue(type, out var result)
            ? result
            : IsSubjectDictionaryTypeSlow(type);
    }

    private static bool CanContainSubjectsSlow(Type type)
    {
        return CanContainSubjectsCache.GetOrAdd(type, static t =>
            t.IsSubjectReferenceType() ||
            t.IsSubjectCollectionType() ||
            t.IsSubjectDictionaryType());
    }

    private static bool IsSubjectReferenceTypeSlow(Type type)
    {
        return IsSubjectReferenceTypeCache.GetOrAdd(type, static t =>
        {
            t = Nullable.GetUnderlyingType(t) ?? t;

            if (typeof(IInterceptorSubject).IsAssignableFrom(t))
            {
                return true;
            }

            return CanDirectlyHoldSubject(t) &&
                   !t.IsSubjectDictionaryType() &&
                   !t.IsSubjectCollectionType();
        });
    }

    private static bool IsSubjectCollectionTypeSlow(Type type)
    {
        return IsSubjectCollectionTypeCache.GetOrAdd(type, static t =>
        {
            t = Nullable.GetUnderlyingType(t) ?? t;

            if (typeof(IInterceptorSubject).IsAssignableFrom(t))
                return false;

            if (t.IsSubjectDictionaryType())
                return false;

            if (!typeof(IEnumerable).IsAssignableFrom(t))
                return false;

            var genericEnumerables = GetEnumerablesIncludingSelf(t);

            if (genericEnumerables.Length > 0)
                return genericEnumerables.Any(static i => IsCandidateElementType(i.GenericTypeArguments[0]));

            // No generic type info (e.g. ArrayList)
            return typeof(ICollection).IsAssignableFrom(t);
        });
    }

    private static bool IsSubjectDictionaryTypeSlow(Type type)
    {
        return IsSubjectDictionaryTypeCache.GetOrAdd(type, static t =>
        {
            t = Nullable.GetUnderlyingType(t) ?? t;

            if (typeof(IInterceptorSubject).IsAssignableFrom(t))
                return false;

            // Require a real dictionary interface. Bare IEnumerable<KeyValuePair<,>> is not
            // classified as dict because the runtime handler dispatches via IDictionary; without
            // an actual dict interface a value like List<KVP<K, Subject>> would silently be
            // treated as a plain collection. Classifier and handler must agree.
            if (!typeof(IDictionary).IsAssignableFrom(t) &&
                !ImplementsGenericInterfaceDefinition(t, typeof(IDictionary<,>)) &&
                !ImplementsGenericInterfaceDefinition(t, typeof(IReadOnlyDictionary<,>)))
            {
                return false;
            }

            var genericEnumerables = GetEnumerablesIncludingSelf(t);

            if (genericEnumerables.Length > 0)
            {
                return genericEnumerables.Any(static i =>
                    i.GenericTypeArguments[0] is { IsGenericType: true } kvType &&
                    kvType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) &&
                    IsCandidateElementType(kvType.GenericTypeArguments[1]));
            }

            // No generic enumerable interface: the dict-interface check above only lets non-generic
            // IDictionary implementations (e.g. Hashtable) reach here, so this branch is true.
            return true;
        });
    }

    // Self-predicate: can a value of this exact type be assigned to a property and treated as a
    // single subject reference? Excludes IEnumerable so that container types route to the
    // collection/dictionary classifiers instead of being treated as references. The IIS check
    // is intentionally absent: callers (IsSubjectReferenceTypeSlow) handle IIS short-circuit
    // before invoking this helper.
    private static bool CanDirectlyHoldSubject(Type t) =>
        (t.IsInterface || t == typeof(object)) &&
        !typeof(IEnumerable).IsAssignableFrom(t);

    // Element-predicate: could an element of this type inside a collection/dictionary be a subject?
    // An IInterceptorSubject that also implements IEnumerable (hybrid container-subject) is still
    // a valid subject element, so IIS short-circuits before CanDirectlyHoldSubject's IEnumerable
    // exclusion. Used for List<Hybrid> classification.
    private static bool IsCandidateElementType(Type t) =>
        typeof(IInterceptorSubject).IsAssignableFrom(t) || CanDirectlyHoldSubject(t);

    private static bool ImplementsGenericInterfaceDefinition(Type type, Type genericInterfaceDefinition)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == genericInterfaceDefinition)
            return true;

        foreach (var i in type.GetInterfaces())
        {
            if (i.IsGenericType && i.GetGenericTypeDefinition() == genericInterfaceDefinition)
                return true;
        }

        return false;
    }

    // GetInterfaces() does not return the type itself, so for a bare IEnumerable<X>
    // property type we include it explicitly.
    private static Type[] GetEnumerablesIncludingSelf(Type type)
    {
        var fromInterfaces = Array.FindAll(type.GetInterfaces(),
            static i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(IEnumerable<>))
        {
            return fromInterfaces;
        }

        if (fromInterfaces.Length == 0)
            return [type];

        var enriched = new Type[fromInterfaces.Length + 1];
        Array.Copy(fromInterfaces, enriched, fromInterfaces.Length);
        enriched[fromInterfaces.Length] = type;
        return enriched;
    }
}
