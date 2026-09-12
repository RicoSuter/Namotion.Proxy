using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

[InterceptorSubject]
public partial class HostedParent
{
    public partial CountingHostedSubject? Child { get; set; }
}
