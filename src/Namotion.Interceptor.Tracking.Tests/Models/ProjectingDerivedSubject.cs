using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class ProjectingDerivedSubject
{
    public partial Person[]? Children { get; set; }

    /// <summary>A projection of an edge the stored property already owns.</summary>
    [Derived]
    public Person? FirstChild => Children is { Length: > 0 } children ? children[0] : null;
}
