using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class ObjectDerivedLazySubject
{
    private Person? _child;

    public partial string? Name { get; set; }

    /// <summary>
    /// Lazy initialization behind an object declaration does not create a structural edge.
    /// </summary>
    [Derived]
    public object? Value => _child ??= new Person { FirstName = "lazy" };
}
