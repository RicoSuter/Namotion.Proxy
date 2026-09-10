namespace Namotion.Interceptor.Interceptors;

/// <summary>
/// Interceptor that can read and transform property values during property access.
/// </summary>
public interface IReadInterceptor
{
    /// <summary>
    /// Intercepts a property read operation.
    /// </summary>
    /// <typeparam name="TProperty">A hint for the property type. May be <c>object</c> when
    /// values are boxed through non-generic paths. Use <c>context.Property.Metadata.Type</c>
    /// for the actual declared property type.</typeparam>
    /// <param name="context">The read context containing the property reference.</param>
    /// <param name="next">The next interceptor in the chain to call. Always forward the context you
    /// received; a freshly constructed context loses the per-call state the chain threads through it
    /// (including the terminal read operation).</param>
    /// <returns>The property value.</returns>
    TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next);
}

public delegate TProperty ReadInterceptionDelegate<TProperty>(ref PropertyReadContext<TProperty> context);

/// <summary>
/// Context for a property read operation.
/// <typeparamref name="TProperty"/> is a hint. It may be <c>object</c> when values are
/// boxed through non-generic paths. Use <c>Property.Metadata.Type</c> for the actual
/// declared property type.
/// </summary>
public struct PropertyReadContext<TProperty>
{
    // The terminal read function for this call. Threaded through the per-call context (which already
    // flows by ref to the end of the chain) instead of a ThreadStatic on the shared chain instance:
    // per-call state belongs on the per-call context, which is also robust against reentrant reads.
    internal Func<IInterceptorSubject, TProperty>? Terminal;

    // The executor that started this read, and always the executor of Property.Subject. Threaded
    // through the context so the terminal can take the per-subject SyncRoot without resolving the
    // subject's executor (an interface dispatch plus a type test) on every locked read.
    internal readonly InterceptorExecutor Executor;

    /// <summary>
    /// Gets the property to read a value from.
    /// </summary>
    public PropertyReference Property { get; }

    // Internal so every meaningfully constructed context comes from the library's execution entry
    // points, which always thread the per-call chain state (such as the terminal) through it.
    internal PropertyReadContext(InterceptorExecutor executor, PropertyReference property)
    {
        Executor = executor;
        Property = property;
    }
}