﻿using Namotion.Interceptor;
using Opc.Ua.Server;

namespace Namotion.Proxy.OpcUa.Server;

internal class ProxyOpcUaServer<TProxy> : StandardServer
    where TProxy : IInterceptorSubject
{
    public ProxyOpcUaServer(TProxy proxy, OpcUaServerTrackableSource<TProxy> source, string? rootName)
    {
        AddNodeManager(new CustomNodeManagerFactory<TProxy>(proxy, source, rootName));
    }
}
