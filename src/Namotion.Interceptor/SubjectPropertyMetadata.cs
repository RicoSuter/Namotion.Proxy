using System.Reflection;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor;

public readonly record struct SubjectPropertyMetadata
{
    /// <summary>
    /// Gets the name of the property.
    /// </summary>
    public string Name { get; }
    
    /// <summary>
    /// Gets the type of the property.
    /// </summary>
    public Type Type { get; }
    
    /// <summary>
    /// Gets the reflection attributes defined on the property.
    /// </summary>
    public IReadOnlyCollection<Attribute> Attributes { get; }
    
    // TODO(perf): Use generic instead of object? here?
    
    /// <summary>
    /// Gets the getter delegate for the property.
    /// </summary>
    public Func<IInterceptorSubject, object?>? GetValue { get; }
    
    /// <summary>
    /// Gets the setter delegate for the property.
    /// </summary>
    public Action<IInterceptorSubject, object?>? SetValue { get; }

    /// <summary>
    /// Gets a value indicating whether the property is intercepted (dynamic, manually handled or marked was partial).
    /// </summary>
    public bool IsIntercepted { get; }

    /// <summary>
    /// Gets a value indicating whether the property is dynamic (not backed by native property and backed by PropertyInfo).
    /// </summary>
    public bool IsDynamic { get; }

    /// <summary>
    /// Gets a value indicating whether the property is marked as derived (has DerivedAttribute).
    /// </summary>
    public bool IsDerived { get; }
    
    /// <summary>
    /// Gets a value indicating whether the getter or setter of the property is public (true for dynamic properties).
    /// </summary>
    public bool IsPublic { get; }

    /// <summary>
    /// Gets the PropertyInfo for the property, if available.
    /// </summary>
    public PropertyInfo? PropertyInfo { get; }

    // Generated intercepted properties have backing fields; dynamic getter-only derived values
    // are projections. Routing and ownership must use the same declared-property classification.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsStructural<TProperty>()
    {
        return IsIntercepted && !(IsDerived && IsDynamic && SetValue is null) &&
            (Type == typeof(TProperty) ? Type.CanContainSubjects<TProperty>() : Type.CanContainSubjects());
    }

    public SubjectPropertyMetadata(
        PropertyInfo propertyInfo, 
        Func<IInterceptorSubject, object?>? getValue, 
        Action<IInterceptorSubject, object?>? setValue, 
        bool isIntercepted, 
        bool isDynamic)
        : this(
            propertyInfo.Name,
            propertyInfo.PropertyType,
            propertyInfo.GetCustomAttributesIncludingInterfaces(),
            getValue,
            setValue,
            isIntercepted,
            isDynamic,
            propertyInfo)
    {
    }

    public SubjectPropertyMetadata(
        string name, 
        Type type, 
        IReadOnlyCollection<Attribute> attributes, 
        Func<IInterceptorSubject, object?>? getValue, 
        Action<IInterceptorSubject, object?>? setValue,
        bool isIntercepted,
        bool isDynamic) : this(
            name,
            type,
            attributes,
            getValue,
            setValue,
            isIntercepted,
            isDynamic,
            propertyInfo: null)
    {
    }
    
    private SubjectPropertyMetadata(
        string name, 
        Type type, 
        IReadOnlyCollection<Attribute> attributes, 
        Func<IInterceptorSubject, object?>? getValue, 
        Action<IInterceptorSubject, object?>? setValue,
        bool isIntercepted,
        bool isDynamic,
        PropertyInfo? propertyInfo)
    {
        Name = name;
        Type = type;
        Attributes = attributes;
        GetValue = getValue;
        SetValue = setValue;
        IsIntercepted = isIntercepted;
        IsDynamic = isDynamic;
        IsDerived = attributes.Any(a => a is DerivedAttribute);
        PropertyInfo = propertyInfo;
        IsPublic =
            PropertyInfo is null ||
            PropertyInfo.GetMethod?.IsPublic == true ||
            PropertyInfo.SetMethod?.IsPublic == true;
    }
}