using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor;

public static class PropertyReferenceExtensions
{
    public static PropertyReference GetPropertyReference(this IInterceptorSubject subject, string propertyName)
    {
        return new PropertyReference(subject, propertyName);
    }

    /// <summary>
    /// Cascade re-entry path: invokes the write chain with a pre-resolved raw timestamp so the
    /// new <see cref="PropertyWriteContext{TProperty}"/>'s cache is seeded directly. Bypasses
    /// the <see cref="SubjectChangeContext.WithChangedTimestamp(DateTimeOffset?)"/> scope-push
    /// dance the derived-cascade would otherwise need to share the trigger's captured time.
    /// </summary>
    internal static void SetPropertyValueWithInterception(this PropertyReference property, object? newValue,
        object? currentValue, Action<IInterceptorSubject, object?> writeValue, long rawTimestamp)
    {
        // Hard cast: IInterceptorExecutor is not independently implementable (the chain terminals
        // and the commit revision live on the built-in executor), and silently skipping this write
        // would drop a derived-property cascade.
        var executor = (InterceptorExecutor)property.Subject.Executor;
        executor.SetPropertyValue(property.Name, newValue, currentValue, writeValue, rawTimestamp);
    }
}
