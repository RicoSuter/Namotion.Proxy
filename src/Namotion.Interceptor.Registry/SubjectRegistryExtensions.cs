using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Registry;

public static class SubjectRegistryExtensions
{
    internal const string SubjectIdKey = "Namotion.Interceptor.SubjectId";

    /// <summary>
    /// Performance optimization: guards <see cref="TryGetSubjectId"/> to avoid
    /// per-subject dictionary lookups on every lifecycle event (attach/detach)
    /// when no subject ID has ever been assigned. Set to true on the first call to
    /// <see cref="SetSubjectId"/> or <see cref="GetOrAddSubjectId"/> and never reset.
    /// </summary>
    internal static volatile bool HasSubjectIds;

    internal static string GenerateSubjectId()
    {
        const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        // GUID = 128 bits = 16 bytes. We treat these as a big-endian unsigned
        // 128-bit integer and repeatedly divide by 62 to produce 22 base62 digits.
        // Manual long division avoids the BigInteger heap allocation.
        Span<byte> bytes = stackalloc byte[16];
        Guid.NewGuid().TryWriteBytes(bytes);

        // Convert to big-endian so the most-significant byte is first,
        // which is what our long-division loop expects.
        bytes.Reverse();

        Span<char> result = stackalloc char[22];
        for (var i = 21; i >= 0; i--)
        {
            // Long division of bytes[] (big-endian) by 62.
            uint remainder = 0;
            for (var j = 0; j < 16; j++)
            {
                uint dividend = (remainder << 8) | bytes[j];
                bytes[j] = (byte)(dividend / 62);
                remainder = dividend % 62;
            }

            result[i] = chars[(int)remainder];
        }

        return new string(result);
    }

    /// <summary>
    /// Gets all registered properties of the subject and child subjects.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <returns>The update.</returns>
    public static IEnumerable<RegisteredSubjectProperty> GetAllProperties(this RegisteredSubject subject)
    {
        foreach (var registeredSubjectProperty in InnerGetAllProperties(subject, []))
        {
            yield return registeredSubjectProperty;
        } 

        IEnumerable<RegisteredSubjectProperty> InnerGetAllProperties(
            RegisteredSubject innerSubject, HashSet<RegisteredSubject> registeredSubjects)
        {
            if (!registeredSubjects.Add(innerSubject))
            {
                yield break;
            }

            // TODO(perf): Implement directly on subject to avoid accessing Properties property
            foreach (var property in innerSubject.Properties)
            {
                yield return property;

                foreach (var childProperty in property.Children
                    .Select(c => c.Subject.TryGetRegisteredSubject())
                    .Where(s => s is not null)
                    .SelectMany(s => InnerGetAllProperties(s!, registeredSubjects)))
                {
                    yield return childProperty;
                }
            }
        }
    }
    
    /// <summary>
    /// Gets a registered property by name.
    /// </summary>
    /// <param name="subject">The subject with the property.</param>
    /// <param name="propertyName">The property name to find.</param>
    /// <param name="registry">The optional registry, otherwise the registry is resolved dynamically.</param>
    /// <returns>The registered property, or <see langword="null"/> when the context has no registry or the property is not registered.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RegisteredSubjectProperty? TryGetRegisteredProperty(this IInterceptorSubject subject, string propertyName, ISubjectRegistry? registry = null)
    {
        registry ??= subject.TryGetContext()?.TryGetService<ISubjectRegistry>();
        return registry?
            .TryGetRegisteredSubject(subject)?
            .TryGetProperty(propertyName);
    }
    
    /// <summary>
    /// Gets a registered property by name.
    /// </summary>
    /// <param name="propertyReference">The property to find.</param>
    /// <returns>The registered property.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RegisteredSubjectProperty? TryGetRegisteredProperty(this PropertyReference propertyReference)
    {
        return propertyReference.Subject
            .TryGetRegisteredSubject()?
            .TryGetProperty(propertyReference.Name);
    }
    
    /// <summary>
    /// Gets a registered property by name.
    /// </summary>
    /// <param name="propertyReference">The property to find.</param>
    /// <returns>The registered property.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RegisteredSubjectProperty GetRegisteredProperty(this PropertyReference propertyReference)
    {
        return propertyReference.Subject
            .TryGetRegisteredSubject()?
            .TryGetProperty(propertyReference.Name) 
            ?? throw new InvalidOperationException($"Property '{propertyReference.Name}' not found.");
    }

    /// <summary>
    /// Gets a registered subject.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <returns>The registered subject.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RegisteredSubject? TryGetRegisteredSubject(this IInterceptorSubject subject)
    {
        // TODO(perf): Replace calls of TryGetRegisteredSubject with registry.TryGetRegisteredSubject to avoid multiple registry resolves
        var registry = subject.TryGetContext()?.TryGetService<ISubjectRegistry>();
        return registry?.TryGetRegisteredSubject(subject);
    }
    
    /// <summary>
    /// Gets the registered property selected by a member-access expression (e.g. <c>x =&gt; x.Child.Value</c>).
    /// Unwraps boxing conversions (so value-typed members work) and resolves intermediate subjects of any
    /// type via <see cref="IInterceptorSubject"/>. Pure member-access hops are resolved through the registry
    /// (null-safe, no compilation); segments that contain an index, dictionary key or method call are compiled
    /// and evaluated against the actual object graph. Returns <see langword="null"/> when the expression is not
    /// a member-access chain rooted at the lambda parameter (a selector on a captured variable returns
    /// <see langword="null"/>), a member hop is <see langword="null"/> at any depth, or the property is not
    /// registered.
    /// </summary>
    /// <param name="subject">The subject with the property.</param>
    /// <param name="propertyExpression">The property expression to find.</param>
    /// <typeparam name="T">The subject type to allow properly typed expression.</typeparam>
    /// <typeparam name="TValue">The selected property value type.</typeparam>
    /// <returns>The registered property, or <see langword="null"/>.</returns>
    public static RegisteredSubjectProperty? TryGetRegisteredProperty<T, TValue>(
        this T subject, Expression<Func<T, TValue>> propertyExpression)
        where T : IInterceptorSubject
    {
        var parameter = propertyExpression.Parameters[0];
        var body = propertyExpression.Body;

        // Unwrap boxing conversions (e.g. Expression<Func<T, object?>> over a value-typed member).
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression memberExpression)
        {
            return null;
        }

        var parentSubject = ResolveSubject(memberExpression.Expression, subject, parameter);
        return parentSubject?.TryGetRegisteredProperty(memberExpression.Member.Name);

        // Resolves the subject an intermediate expression evaluates to. Member-access hops are walked
        // through the registry (null-safe, no compilation). Any other node - array/list index, dictionary
        // key or method call - is compiled and invoked once against the root to read the actual graph value.
        static IInterceptorSubject? ResolveSubject(Expression? expression, T root, ParameterExpression rootParameter)
            => expression switch
            {
                null => null,
                ParameterExpression => root,
                MemberExpression member => ResolveSubject(member.Expression, root, rootParameter)?
                    .TryGetRegisteredProperty(member.Member.Name)?
                    .GetValue() as IInterceptorSubject,
                _ => TryEvaluate(expression, root, rootParameter),
            };

        // Compiles and invokes a non-member segment (index, dictionary key or method call) against the real
        // object graph. Navigation misses - a null subject before the index, an out-of-range index or a
        // missing dictionary key - return null to match the null-safe member-access path. Other exception
        // types propagate unchanged.
        static IInterceptorSubject? TryEvaluate(Expression expression, T root, ParameterExpression rootParameter)
        {
            try
            {
                return Expression
                    .Lambda<Func<T, object?>>(Expression.Convert(expression, typeof(object)), rootParameter)
                    .Compile()
                    .Invoke(root) as IInterceptorSubject;
            }
            catch (Exception exception) when (exception
                is NullReferenceException
                or IndexOutOfRangeException
                or ArgumentOutOfRangeException
                or KeyNotFoundException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Gets or lazily generates a subject ID for the given subject.
    /// The ID is stored in the subject's Data dictionary and auto-registered
    /// in the reverse index if an <see cref="ISubjectIdRegistryWriter"/> is available.
    /// Generated IDs are unique but not cryptographically secure.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <returns>The subject ID.</returns>
    public static string GetOrAddSubjectId(this IInterceptorSubject subject)
    {
        // Fast path: ID already assigned (lock-free ConcurrentDictionary read)
        if (subject.Data.TryGetValue((null, SubjectIdKey), out var existing) && existing is string existingId)
            return existingId;

        // Slow path: delegate to registry writer for atomic Data + reverse-index update
        var writer = subject.TryGetContext()?.TryGetService<ISubjectIdRegistryWriter>();
        if (writer is not null)
            return writer.GetOrAddSubjectId(subject);

        // No registry - ConcurrentDictionary.GetOrAdd is sufficient (no reverse index to corrupt)
        HasSubjectIds = true;
        return (string)subject.Data.GetOrAdd(
            (null, SubjectIdKey),
            static _ => GenerateSubjectId())!;
    }

    /// <summary>
    /// Gets the subject ID if one has been assigned, or null if none exists.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <returns>The subject ID, or null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string? TryGetSubjectId(this IInterceptorSubject subject)
    {
        if (!HasSubjectIds)
            return null;

        return subject.Data.TryGetValue((null, SubjectIdKey), out var value) && value is string id
            ? id
            : null;
    }

    /// <summary>
    /// Assigns a known subject ID (e.g., from an incoming update).
    /// When a registry is configured, both the subject's Data store and the
    /// reverse index are updated atomically under the registry's lock.
    /// Setting the same ID again is a no-op.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="id">The subject ID to assign.</param>
    /// <exception cref="InvalidOperationException">Thrown when the subject already has a different ID assigned.</exception>
    public static void SetSubjectId(this IInterceptorSubject subject, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        // Delegate to the registry writer for atomic Data + reverse-index update
        var writer = subject.TryGetContext()?.TryGetService<ISubjectIdRegistryWriter>();
        if (writer is not null)
        {
            writer.SetSubjectId(subject, id);
            return;
        }

        HasSubjectIds = true;

        var existing = subject.Data.GetOrAdd((null, SubjectIdKey), id);
        if (existing is string existingId && existingId != id)
        {
            throw new InvalidOperationException(
                $"Subject already has ID '{existingId}'; cannot reassign to '{id}'.");
        }
    }
}
