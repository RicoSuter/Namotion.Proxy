﻿using Namotion.Proxy.Abstractions;
using System.Collections.Immutable;

namespace Namotion.Proxy.Registry;

// TODO: Add lots of tests!

internal class ProxyRegistry : IProxyRegistry, IProxyLifecycleHandler
{
    private Dictionary<IProxy, ProxyMetadata> _knownProxies = new();

    public IReadOnlyDictionary<IProxy, ProxyMetadata> KnownProxies
    {
        get
        {
            lock (_knownProxies)
                return _knownProxies.ToImmutableDictionary();
        }
    }

    public void OnProxyAttached(ProxyLifecycleContext context)
    {
        lock (_knownProxies)
        {
            if (!_knownProxies.TryGetValue(context.Proxy, out var metadata))
            {
                metadata = new ProxyMetadata
                {
                    Properties = context.Proxy
                        .Properties
                        .ToDictionary(p => p.Key,
                            p => new ProxyProperty
                            {
                                GetValue = () => p.Value.GetValue(context.Proxy)
                            })
                };

                _knownProxies[context.Proxy] = metadata;
            }

            if (context.ParentProxy is not null)
            {
                var parents = metadata.Parents as HashSet<ProxyPropertyReference>;
                if (parents is not null)
                {
                    parents.Add(new ProxyPropertyReference(context.ParentProxy, context.PropertyName));
                }

                var children = _knownProxies[context.ParentProxy]
                    .Properties[context.PropertyName]
                    .Children as HashSet<ProxyPropertyChild>;

                if (children is not null)
                {
                    children.Add(new ProxyPropertyChild 
                    { 
                        Proxy = context.Proxy,
                        Index = context.Index
                    });
                }
            }
        }
    }

    public void OnProxyDetached(ProxyLifecycleContext context)
    {
        lock (_knownProxies)
        {
            if (context.ReferenceCount == 0)
            {
                if (context.ParentProxy is not null)
                {
                    var metadata = _knownProxies[context.Proxy];
                    
                    var parents = metadata.Parents as HashSet<ProxyPropertyReference>;
                    if (parents is not null)
                    {
                        parents.Remove(new ProxyPropertyReference(context.ParentProxy, context.PropertyName));
                    }

                    var children = _knownProxies[context.ParentProxy]
                       .Properties[context.PropertyName]
                       .Children as HashSet<ProxyPropertyChild>;

                    if (children is not null)
                    {
                        children.Remove(new ProxyPropertyChild
                        {
                            Proxy = context.Proxy,
                            Index = context.Index
                        });
                    }
                }

                _knownProxies.Remove(context.Proxy);
            }
        }
    }
}
