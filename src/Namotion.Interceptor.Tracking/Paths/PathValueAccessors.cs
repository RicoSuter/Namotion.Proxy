using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>
/// Compiled typed accessors and lenient container lookups used by the path walk. Every read routes
/// through the intercepted property getter (via <see cref="Expression.Property(Expression, PropertyInfo)"/>),
/// so reads honor the subject sync root, the derived read hook and an ambient transaction's staged view.
/// The typed accessors read value-typed intermediates without boxing to keep the walk allocation-free.
/// </summary>
internal static class PathValueAccessors
{
    private static readonly ConcurrentDictionary<(Type DeclaringType, string PropertyName, Type ValueType), Delegate> LeafAccessorCache = new();
    private static readonly ConcurrentDictionary<(Type DeclaringType, string PropertyName), Func<IInterceptorSubject, int, IInterceptorSubject?>> ImmutableArrayIndexerCache = new();
    private static readonly ConcurrentDictionary<(Type DeclaringType, string PropertyName, Type InterfaceType), Func<IInterceptorSubject, int, IInterceptorSubject?>> GenericListIndexerCache = new();
    private static readonly ConcurrentDictionary<(Type DeclaringType, string PropertyName), Func<IInterceptorSubject, object, IInterceptorSubject?>> DictionaryLookupCache = new();
    private static readonly ConcurrentDictionary<Type, Func<object, bool>?> ImmutableArrayIsDefaultCache = new();

    /// <summary>
    /// Gets a delegate that reads the leaf property typed (no boxing for a value-typed leaf), routed
    /// through the intercepted getter. Only valid when the property type is assignable to
    /// <typeparamref name="TValue"/>. Compiled once per (declaring type, property name, value type).
    /// </summary>
    public static Func<IInterceptorSubject, TValue> GetLeafAccessor<TValue>(Type declaringType, PropertyInfo propertyInfo)
    {
        var accessor = LeafAccessorCache.GetOrAdd(
            (declaringType, propertyInfo.Name, typeof(TValue)),
            static (_, state) => BuildLeafAccessor<TValue>(state.declaringType, state.propertyInfo),
            (declaringType, propertyInfo));

        return (Func<IInterceptorSubject, TValue>)accessor;
    }

    /// <summary>
    /// Gets a delegate that reads the <see cref="ImmutableArray{T}"/>-typed property (no boxing) and
    /// returns the element at the index as an <see cref="IInterceptorSubject"/>, or null when the array
    /// is default or the index is out of range. Compiled once per (declaring type, property name).
    /// </summary>
    public static Func<IInterceptorSubject, int, IInterceptorSubject?> GetImmutableArrayIndexer(Type declaringType, PropertyInfo propertyInfo, Type elementType)
        => ImmutableArrayIndexerCache.GetOrAdd(
            (declaringType, propertyInfo.Name),
            static (_, state) => BuildImmutableArrayIndexer(state.declaringType, state.propertyInfo, state.elementType),
            (declaringType, propertyInfo, elementType));

    /// <summary>
    /// Gets a delegate that reads a reference-typed generic list property and invokes the declared
    /// <see cref="IList{T}"/> or <see cref="IReadOnlyList{T}"/> Count and indexer directly. This supports
    /// generic-only implementations without falling back to enumeration. A throwing Count or indexer
    /// surfaces to the caller so the enclosing path walk can resolve it as unreachable.
    /// </summary>
    public static Func<IInterceptorSubject, int, IInterceptorSubject?> GetGenericListIndexer(
        Type declaringType, PropertyInfo propertyInfo, Type interfaceType, Type elementType)
        => GenericListIndexerCache.GetOrAdd(
            (declaringType, propertyInfo.Name, interfaceType),
            static (_, state) => BuildGenericListIndexer(
                state.declaringType, state.propertyInfo, state.interfaceType, state.elementType),
            (declaringType, propertyInfo, interfaceType, elementType));

    /// <summary>
    /// Gets a delegate that reads the dictionary property, casts it to the declared dictionary interface
    /// and calls <c>TryGetValue</c> (honoring the dictionary's own comparer), returning the value as an
    /// <see cref="IInterceptorSubject"/> or null on a missing key. Compiled once per (declaring type,
    /// property name). A throwing Count/indexer/TryGetValue/comparer surfaces to the caller by design.
    /// </summary>
    public static Func<IInterceptorSubject, object, IInterceptorSubject?> GetDictionaryLookup(Type declaringType, PropertyInfo propertyInfo, PathSegment segment)
        => DictionaryLookupCache.GetOrAdd(
            (declaringType, propertyInfo.Name),
            static (_, state) => BuildDictionaryLookup(state.declaringType, state.propertyInfo, state.segment),
            (declaringType, propertyInfo, segment));

    /// <summary>
    /// Lenient variant of <see cref="SubjectLookup.FindSubjectInCollection"/>: returns the subject at the
    /// index inside a reference-typed collection, or null for a negative or out-of-range index, a slot that
    /// is not a subject, or a boxed default <see cref="ImmutableArray{T}"/>. Never throws.
    /// </summary>
    public static IInterceptorSubject? IndexReferenceCollection(object collection, int index)
    {
        if (index < 0)
            return null;

        // A boxed default ImmutableArray<T> implements IList but throws on Count and the indexer; treat it as empty.
        if (IsDefaultImmutableArray(collection))
            return null;

        if (collection is IList list)
            return index < list.Count ? list[index] as IInterceptorSubject : null;

        return IndexReferenceCollectionSlow(collection, index);
    }

    private static IInterceptorSubject? IndexReferenceCollectionSlow(object collection, int index)
    {
        if (collection is IEnumerable enumerable)
        {
            var currentIndex = 0;
            foreach (var item in enumerable)
            {
                if (currentIndex == index)
                    return item as IInterceptorSubject;
                currentIndex++;
            }
        }

        return null;
    }

    private static Func<IInterceptorSubject, TValue> BuildLeafAccessor<TValue>(Type declaringType, PropertyInfo propertyInfo)
    {
        var subjectParameter = Expression.Parameter(typeof(IInterceptorSubject), "subject");

        Expression body = Expression.Property(Expression.Convert(subjectParameter, declaringType), propertyInfo);
        if (body.Type != typeof(TValue))
            body = Expression.Convert(body, typeof(TValue));

        return Expression.Lambda<Func<IInterceptorSubject, TValue>>(body, subjectParameter).Compile();
    }

    private static Func<IInterceptorSubject, int, IInterceptorSubject?> BuildImmutableArrayIndexer(Type declaringType, PropertyInfo propertyInfo, Type elementType)
    {
        var subjectParameter = Expression.Parameter(typeof(IInterceptorSubject), "subject");
        var indexParameter = Expression.Parameter(typeof(int), "index");

        var immutableArrayType = typeof(ImmutableArray<>).MakeGenericType(elementType);
        var arrayVariable = Expression.Variable(immutableArrayType, "array");

        var readArray = Expression.Property(Expression.Convert(subjectParameter, declaringType), propertyInfo);
        var isDefault = Expression.Property(arrayVariable, immutableArrayType.GetProperty(nameof(ImmutableArray<int>.IsDefault))!);
        var length = Expression.Property(arrayVariable, immutableArrayType.GetProperty(nameof(ImmutableArray<int>.Length))!);
        var getItem = immutableArrayType.GetMethod("get_Item", [typeof(int)])!;

        var isOutOfRange = Expression.OrElse(
            isDefault,
            Expression.OrElse(
                Expression.LessThan(indexParameter, Expression.Constant(0)),
                Expression.GreaterThanOrEqual(indexParameter, length)));

        var element = Expression.TypeAs(Expression.Call(arrayVariable, getItem, indexParameter), typeof(IInterceptorSubject));

        var body = Expression.Block(
            [arrayVariable],
            Expression.Assign(arrayVariable, readArray),
            Expression.Condition(
                isOutOfRange,
                Expression.Constant(null, typeof(IInterceptorSubject)),
                element));

        return Expression.Lambda<Func<IInterceptorSubject, int, IInterceptorSubject?>>(body, subjectParameter, indexParameter).Compile();
    }

    private static Func<IInterceptorSubject, int, IInterceptorSubject?> BuildGenericListIndexer(
        Type declaringType, PropertyInfo propertyInfo, Type interfaceType, Type elementType)
    {
        var subjectParameter = Expression.Parameter(typeof(IInterceptorSubject), "subject");
        var indexParameter = Expression.Parameter(typeof(int), "index");
        var listVariable = Expression.Variable(interfaceType, "list");

        var readList = Expression.Convert(
            Expression.Property(Expression.Convert(subjectParameter, declaringType), propertyInfo),
            interfaceType);

        var interfaceDefinition = interfaceType.GetGenericTypeDefinition();
        var countInterfaceType = (interfaceDefinition == typeof(IList<>)
                ? typeof(ICollection<>)
                : typeof(IReadOnlyCollection<>))
            .MakeGenericType(elementType);

        var count = Expression.Property(
            Expression.Convert(listVariable, countInterfaceType),
            nameof(IReadOnlyCollection<int>.Count));
        var indexer = interfaceType.GetProperty("Item")!;
        var element = Expression.TypeAs(
            Expression.Property(listVariable, indexer, indexParameter),
            typeof(IInterceptorSubject));

        var isOutOfRange = Expression.OrElse(
            Expression.ReferenceEqual(listVariable, Expression.Constant(null, interfaceType)),
            Expression.OrElse(
                Expression.LessThan(indexParameter, Expression.Constant(0)),
                Expression.GreaterThanOrEqual(indexParameter, count)));

        var body = Expression.Block(
            [listVariable],
            Expression.Assign(listVariable, readList),
            Expression.Condition(
                isOutOfRange,
                Expression.Constant(null, typeof(IInterceptorSubject)),
                element));

        return Expression.Lambda<Func<IInterceptorSubject, int, IInterceptorSubject?>>(
            body, subjectParameter, indexParameter).Compile();
    }

    private static Func<IInterceptorSubject, object, IInterceptorSubject?> BuildDictionaryLookup(Type declaringType, PropertyInfo propertyInfo, PathSegment segment)
    {
        var interfaceType = segment.DictionaryInterfaceType!;
        var keyType = segment.DictionaryKeyType!;
        var valueType = segment.DictionaryValueType!;

        var subjectParameter = Expression.Parameter(typeof(IInterceptorSubject), "subject");
        var keyParameter = Expression.Parameter(typeof(object), "key");

        var readDictionary = Expression.Property(Expression.Convert(subjectParameter, declaringType), propertyInfo);
        var typedDictionary = Expression.Convert(readDictionary, interfaceType);
        var typedKey = Expression.Convert(keyParameter, keyType);

        var valueVariable = Expression.Variable(valueType, "value");
        var tryGetValueMethod = interfaceType.GetMethod("TryGetValue")!;

        var body = Expression.Block(
            [valueVariable],
            Expression.Condition(
                Expression.Call(typedDictionary, tryGetValueMethod, typedKey, valueVariable),
                Expression.TypeAs(valueVariable, typeof(IInterceptorSubject)),
                Expression.Constant(null, typeof(IInterceptorSubject))));

        return Expression.Lambda<Func<IInterceptorSubject, object, IInterceptorSubject?>>(body, subjectParameter, keyParameter).Compile();
    }

    private static bool IsDefaultImmutableArray(object collection)
    {
        var isDefault = ImmutableArrayIsDefaultCache.GetOrAdd(collection.GetType(), static type =>
        {
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(ImmutableArray<>))
                return null;

            var boxedParameter = Expression.Parameter(typeof(object), "boxed");
            var isDefaultProperty = Expression.Property(
                Expression.Convert(boxedParameter, type),
                type.GetProperty(nameof(ImmutableArray<int>.IsDefault))!);

            return Expression.Lambda<Func<object, bool>>(isDefaultProperty, boxedParameter).Compile();
        });

        return isDefault is not null && isDefault(collection);
    }
}
