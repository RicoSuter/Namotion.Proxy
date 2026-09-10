using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class LazyDerivedSubject
{
    private Person? _child;

    public partial string? Name { get; set; }

    /// <summary>Lazy initialisation inside a getter: the child is owned by nothing.</summary>
    [Derived]
    public Person Current => _child ??= new Person { FirstName = "lazy" };
}
