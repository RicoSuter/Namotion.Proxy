using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking;

/// <summary>
/// Interceptor that checks if the new value is different from the current value
/// and only calls the next interceptor when the property has actually changed.
/// </summary>
[RunsFirst]
public class PropertyValueEqualityCheckHandler : IWriteInterceptor,
    ISingletonContextService<PropertyValueEqualityCheckHandler>
{
    /// <inheritdoc />
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        if (!EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue))
        {
            next(ref context);
        }
    }
}
