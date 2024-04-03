﻿using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.ChangeTracking;

// experimental
// TODO: Add lots of tests!

internal class ReadPropertyRecorder : IProxyReadHandler
{
    internal static AsyncLocal<IDictionary<IProxyContext, List<HashSet<ProxyPropertyReference>>>> Scopes { get; } = new();

    public object? ReadProperty(ReadProxyPropertyContext context, Func<ReadProxyPropertyContext, object?> next)
    {
        if (Scopes.Value is not null)
        {
            lock (typeof(ReadPropertyRecorder))
            {
                if (Scopes.Value is not null &&
                    Scopes.Value.TryGetValue(context.Context, out var scopes))
                {
                    foreach (var scope in scopes)
                    {
                        scope.Add(context.Property);
                    }
                }
            }
        }

        return next(context);
    }
}
