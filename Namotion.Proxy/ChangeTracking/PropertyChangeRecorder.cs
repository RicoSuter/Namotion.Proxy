﻿using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.ChangeTracking;

public static class PropertyChangeRecorderExtensions
{
    public static PropertyChangeRecorderScope BeginPropertyChangedRecording(this IProxyContext context)
    {
        PropertyChangeRecorder._scopes =
            PropertyChangeRecorder._scopes ??
            new Dictionary<IProxyContext, List<HashSet<ProxyPropertyReference>>>();

        var scope = new HashSet<ProxyPropertyReference>();
        PropertyChangeRecorder._scopes.TryAdd(context, new List<HashSet<ProxyPropertyReference>>());
        PropertyChangeRecorder._scopes[context].Add(scope);

        return new PropertyChangeRecorderScope(context, scope);
    }
}

public class PropertyChangeRecorderScope : IDisposable
{
    private readonly IProxyContext _context;
    private readonly HashSet<ProxyPropertyReference> _properties;

    public PropertyChangeRecorderScope(IProxyContext context, HashSet<ProxyPropertyReference> properties)
    {
        _context = context;
        _properties = properties;
    }

    public ProxyPropertyReference[] GetAndReset()
    {
        lock (typeof(PropertyChangeRecorder))
        {
            var properties = _properties.ToArray();
            _properties.Clear();
            return properties;
        }
    }

    public ProxyPropertyReference[] Properties
    {
        get
        {
            lock (typeof(PropertyChangeRecorder))
            {
                return _properties.ToArray();
            }
        }
    }

    public void Dispose()
    {
        lock (typeof(PropertyChangeRecorder))
            PropertyChangeRecorder._scopes?[_context]?.Remove(_properties);
    }
}

internal class PropertyChangeRecorder : IProxyReadHandler
{
    [ThreadStatic]
    internal static IDictionary<IProxyContext, List<HashSet<ProxyPropertyReference>>>? _scopes;

    public object? GetProperty(ReadProxyPropertyContext context, Func<ReadProxyPropertyContext, object?> next)
    {
        if (_scopes is not null)
        {
            lock (typeof(PropertyChangeRecorder))
            {
                if (_scopes is not null && 
                    _scopes.TryGetValue(context.Context, out var scopes))
                {
                    foreach (var scope in scopes)
                    {
                        scope.Add(new ProxyPropertyReference(context.Proxy, context.PropertyName));
                    }
                }
            }
        }

        return next(context);
    }
}