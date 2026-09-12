using Namotion.Interceptor.Attributes;

namespace HomeBlaze.OpcUa.Tests;

/// <summary>
/// The graph node the wrappers hang off. Assigning one of these properties puts the wrapper into the
/// graph and clearing it takes it out again, which is how the handler's attach and detach are driven
/// without going through either wrapper's own operations.
/// </summary>
[InterceptorSubject]
public partial class WrapperContainer
{
    public partial OpcUaClient? Client { get; set; }

    public partial OpcUaServer? Server { get; set; }
}
