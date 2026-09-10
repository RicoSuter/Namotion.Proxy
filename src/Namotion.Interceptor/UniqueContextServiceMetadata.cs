using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Namotion.Interceptor;

internal static class UniqueContextServiceMetadata
{
    private static readonly ConcurrentDictionary<Type, ImmutableArray<Type>> Cache = new();

    internal static ImmutableArray<Type> GetContracts(Type serviceType)
    {
        return Cache.GetOrAdd(serviceType, static type => type
            .GetInterfaces()
            .Where(@interface =>
                @interface.IsGenericType &&
                @interface.GetGenericTypeDefinition() == typeof(IUniqueContextService<>))
            .Select(@interface => @interface.GetGenericArguments()[0])
            .Distinct()
            .ToImmutableArray());
    }
}
