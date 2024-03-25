﻿using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Lifecycle;

internal class ParentsHandler : IProxyLifecycleHandler
{
    public void OnProxyAttached(ProxyLifecycleContext context)
    {
        if (context.Property != default)
        {
            context.Proxy.AddParent(context.Property.Proxy);
        }
    }

    public void OnProxyDetached(ProxyLifecycleContext context)
    {
        if (context.Property != default)
        {
            context.Proxy.RemoveParent(context.Property.Proxy);
        }
    }
}
