using System.Collections.Concurrent;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors;

public static class SubjectFactoryExtensions
{
    private static readonly ConcurrentDictionary<(Type Type, bool Dictionary), (Type? Key, Type Element)> CollectionTypes = new();

    public static IInterceptorSubject CreateSubject(this ISubjectFactory subjectFactory, RegisteredSubjectProperty property)
    {
        var serviceProvider = property.Parent.Subject.TryGetContext()?.TryGetService<IServiceProvider>();
        return subjectFactory.CreateSubject(property.Type, serviceProvider);
    }

    public static IInterceptorSubject CreateCollectionSubject(this ISubjectFactory subjectFactory, RegisteredSubjectProperty property, object? index)
    {
        var serviceProvider = property.Parent.Subject.TryGetContext()?.TryGetService<IServiceProvider>();
        return CreateCollectionSubject(subjectFactory, property.Type, index, serviceProvider);
    }

    /// <summary>
    /// Creates a collection/dictionary item subject from a property type and optional index/key.
    /// Uses the property type to derive the element type (array element, dict value, or list element).
    /// </summary>
    internal static IInterceptorSubject CreateCollectionSubject(this ISubjectFactory subjectFactory, Type propertyType, object? index, IServiceProvider? serviceProvider)
    {
        Type? itemType;
        if (index is null)
        {
            itemType = propertyType;
        }
        else if (propertyType.IsArray)
        {
            itemType = propertyType.GetElementType();
        }
        else
        {
            itemType = GetCollectionTypes(propertyType, propertyType.IsSubjectDictionaryType()).Element;
        }

        return subjectFactory.CreateSubject(
            itemType ?? throw new InvalidOperationException("Unknown collection element type"),
            serviceProvider);
    }

    internal static (Type? Key, Type Element) GetCollectionTypes(Type propertyType, bool dictionary = false)
    {
        return CollectionTypes.GetOrAdd((propertyType, dictionary), static shape =>
        {
            var itemTypes = shape.Type.GetInterfaces().Append(shape.Type)
                .Where(type => type.IsGenericType && (shape.Dictionary
                    ? type.GetGenericTypeDefinition() == typeof(IDictionary<,>) || type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)
                    : type.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
                .Select(type => (Key: shape.Dictionary ? type.GenericTypeArguments[0] : null,
                    Element: type.GenericTypeArguments[shape.Dictionary ? 1 : 0]))
                .Distinct()
                .ToArray();

            return itemTypes.Length == 1
                ? itemTypes[0]
                : throw new NotSupportedException($"Cannot infer a unique collection element type from '{shape.Type}'. Declare a collection with a known element type.");
        });
    }
}
