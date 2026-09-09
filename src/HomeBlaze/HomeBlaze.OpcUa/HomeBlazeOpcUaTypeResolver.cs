using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Client;
using Opc.Ua;
using Opc.Ua.Client;

namespace HomeBlaze.OpcUa;

public class HomeBlazeOpcUaTypeResolver : OpcUaTypeResolver
{
    public HomeBlazeOpcUaTypeResolver(ILogger logger) : base(logger)
    {
    }

    public override Attribute[] GetDynamicPropertyAttributes(ISession session, ReferenceDescription node)
    {
        return [..base.GetDynamicPropertyAttributes(session, node), new StateAttribute()];
    }
}
