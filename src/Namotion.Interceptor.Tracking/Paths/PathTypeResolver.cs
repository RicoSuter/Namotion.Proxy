using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>
/// Pure type-reflection helpers used by <see cref="PathExpressionDecomposer"/> to resolve the
/// collection/dictionary interface and element/key/value types of an indexed path segment. These
/// only inspect <see cref="Type"/>s; they do not walk expression trees or evaluate index arguments.
/// </summary>
internal static class PathTypeResolver
{
    internal static Type GetCollectionElementType(Type collectionType)
    {
        if (collectionType.IsArray)
        {
            return collectionType.GetElementType()!;
        }

        var enumerable = FindClosedInterface(collectionType, typeof(IEnumerable<>));
        if (enumerable is not null)
        {
            return enumerable.GenericTypeArguments[0];
        }

        // A non-generic collection (e.g. ArrayList) exposes no static element type; fall back to
        // object, which IsSubjectReferenceType() accepts (object can hold a subject), so such an
        // intermediate passes the check and is resolved by name against the runtime subject instead.
        return typeof(object);
    }

    internal static (Type? interfaceType, Type? elementType) ResolveCollectionTypes(Type collectionType)
    {
        if (IsClosedImmutableArray(collectionType))
        {
            return (null, collectionType.GenericTypeArguments[0]);
        }

        if (collectionType.IsArray)
        {
            var elementType = collectionType.GetElementType()!;
            return (typeof(IList<>).MakeGenericType(elementType), elementType);
        }

        if (collectionType.IsGenericType)
        {
            var definition = collectionType.GetGenericTypeDefinition();
            if (definition == typeof(IList<>) || definition == typeof(IReadOnlyList<>))
            {
                return (collectionType, collectionType.GenericTypeArguments[0]);
            }
        }

        var listInterface =
            FindClosedInterface(collectionType, typeof(IList<>)) ??
            FindClosedInterface(collectionType, typeof(IReadOnlyList<>));

        // No generic list interface: not a subscribe-time error (runtime validity is lenient); both
        // types stay null so the walk indexes the boxed collection via IndexReferenceCollection.
        return listInterface is null
            ? (null, null)
            : (listInterface, listInterface.GenericTypeArguments[0]);
    }

    internal static (Type interfaceType, Type keyType, Type valueType) ResolveDictionaryTypes(
        PropertyInfo property, Type dictionaryType, ParameterExpression parameter)
    {
        // Prefer the exact declared interface when the property is typed directly as the dictionary interface.
        if (dictionaryType.IsGenericType)
        {
            var definition = dictionaryType.GetGenericTypeDefinition();
            if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
            {
                var declared = dictionaryType.GenericTypeArguments;
                return (dictionaryType, declared[0], declared[1]);
            }
        }

        var dictionaryInterface =
            FindClosedInterface(dictionaryType, typeof(IDictionary<,>)) ??
            FindClosedInterface(dictionaryType, typeof(IReadOnlyDictionary<,>));

        if (dictionaryInterface is null)
        {
            throw new ArgumentException(
                $"The subscription path on '{parameter.Name}' is not supported: " +
                $"the dictionary property '{property.Name}' of type '{dictionaryType.Name}' does not expose " +
                "a generic IDictionary<,> or IReadOnlyDictionary<,> interface.",
                "path");
        }

        var arguments = dictionaryInterface.GenericTypeArguments;
        return (dictionaryInterface, arguments[0], arguments[1]);
    }

    internal static Type? FindClosedInterface(Type type, Type genericDefinition)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == genericDefinition)
        {
            return type;
        }

        foreach (var candidate in type.GetInterfaces())
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == genericDefinition)
            {
                return candidate;
            }
        }

        return null;
    }

    internal static bool IsClosedImmutableArray(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>);
}
