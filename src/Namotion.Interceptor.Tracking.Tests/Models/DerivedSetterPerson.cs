using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class DerivedSetterPerson
{
    [Derived]
    public partial string? Nickname { get; set; }

    [Derived]
    public string NicknameWithPrefix => $"Mr. {Nickname}";
}
