using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class ReadInterceptorFactory<TProperty>
{
    internal static readonly bool RequiresTerminalLock = RequiresReadLock(typeof(TProperty));
    internal static readonly bool CanBoxValueTypes = typeof(TProperty) == typeof(object) ||
        typeof(TProperty).IsInterface || typeof(TProperty) == typeof(ValueType) || typeof(TProperty) == typeof(Enum);

    private static bool RequiresReadLock(Type type) => type.IsValueType &&
        (!type.IsPrimitive || (IntPtr.Size == 4 &&
            (type == typeof(long) || type == typeof(ulong) || type == typeof(double))));

    public static ReadFunc<TProperty> Create(ImmutableArray<IReadInterceptor> interceptors)
    {
        if (interceptors.Length == 0)
        {
            if (RequiresTerminalLock)
            {
                return ReadUnderTerminalLock;
            }

            if (CanBoxValueTypes)
            {
                return static (ref PropertyReadContext<TProperty> context, Func<IInterceptorSubject, TProperty> innerReadValue) =>
                    context.Property.Subject.Properties.TryGetValue(context.Property.Name, out var metadata) &&
                    !RequiresReadLock(metadata.Type)
                        ? innerReadValue(context.Property.Subject)
                        : ReadUnderTerminalLock(ref context, innerReadValue);
            }

            return static (ref PropertyReadContext<TProperty> context, Func<IInterceptorSubject, TProperty> innerReadValue) => innerReadValue(context.Property.Subject);
        }

        var chain = new ReadInterceptorChain<TProperty>(
            interceptors,
            ReadUnderTerminalLock);
        return chain.Execute;
    }

    private static TProperty ReadUnderTerminalLock(
        ref PropertyReadContext<TProperty> context, Func<IInterceptorSubject, TProperty> innerReadValue)
    {
        // Non-atomic values must share the write terminal's lock, including a copy made before boxing.
        lock (context.Executor.SyncRoot)
        {
            return innerReadValue(context.Property.Subject);
        }
    }
}
