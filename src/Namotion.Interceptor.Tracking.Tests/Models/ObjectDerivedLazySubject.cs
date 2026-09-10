using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class ObjectDerivedLazySubject
{
    private Person? _child;

    public partial string? Name { get; set; }

    /// <summary>
    /// Lazy initialisation behind an object declaration: the child is owned by nothing, and the
    /// runtime-type fast path must not let it slip past the untracked-subject check.
    /// </summary>
    [Derived]
    public object? Value => _child ??= new Person { FirstName = "lazy" };
}
