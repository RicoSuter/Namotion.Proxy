using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class ObjectDerivedStringSubject
{
    public partial string? Name { get; set; }

    /// <summary>
    /// An object-declared derived property cannot be excluded by its declared type, so the
    /// untracked-subject check must decide on the runtime type of what actually came back.
    /// </summary>
    [Derived]
    public object? Value => Name is null ? null : $"Hello, {Name}";
}
