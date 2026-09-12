using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

[InterceptorSubject]
public partial class ContainerHolder
{
    public partial HostedContainer? Container { get; set; }
}
