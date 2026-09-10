using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Client;

namespace HomeBlaze.OpcUa;

public class HomeBlazeOpcUaTypeResolver : OpcUaTypeResolver
{
    public HomeBlazeOpcUaTypeResolver(ILogger logger) : base(logger)
    {
    }

    public override Attribute[] GetAttributesForDynamicProperty(OpcUaDynamicPropertyContext property)
    {
        return [..base.GetAttributesForDynamicProperty(property), new StateAttribute()];
    }

    public override Attribute[] GetAttributesForDynamicAttribute(OpcUaDynamicAttributeContext attribute)
    {
        return [..base.GetAttributesForDynamicAttribute(attribute), new StateAttribute()];
    }
}
