using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

[InterceptorSubject]
public partial class Parent
{
    public partial Person? Child { get; set; }

    public partial Person? SecondChild { get; set; }
}
