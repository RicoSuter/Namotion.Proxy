using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors;

public static class SubjectFactoryExtensions
{
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
        else if (propertyType.GenericTypeArguments.Length == 2)
        {
            // Dictionary<TKey, TValue> - use the value type (index 1)
            itemType = propertyType.GenericTypeArguments[1];
        }
        else
        {
            // List<T>, ICollection<T>, etc. - use the element type (index 0)
            itemType = propertyType.GenericTypeArguments[0];
        }

        return subjectFactory.CreateSubject(
            itemType ?? throw new InvalidOperationException("Unknown collection element type"),
            serviceProvider);
    }
}
