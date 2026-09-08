using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

/// <summary>
/// Test model whose derived getter returns a fresh instance on every call, so its value is never
/// reference-equal to the value a change carries. Used to pin that a change to such a property is
/// delivered rather than mistaken for one the model has moved past.
/// </summary>
[InterceptorSubject]
public partial class DerivedCollectionDevice
{
    public partial int First { get; set; }

    public partial int Second { get; set; }

    [Derived]
    public int[] Pair => [First, Second];
}
