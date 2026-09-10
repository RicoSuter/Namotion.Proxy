using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// Adversarial review model: a root that carries a back-referencing companion (declared first, so
/// seeding publishes it before anything else) and a bare IEnumerable collection a test can park in
/// during the discovery scan.
/// </summary>
[InterceptorSubject]
public partial class BackEdgeHolder
{
    public partial BackEdgeChild? Companion { get; set; }

    public partial IEnumerable<Person>? Children { get; set; }
}

/// <summary>The other half of the back edge: it points at the holder that owns it.</summary>
[InterceptorSubject]
public partial class BackEdgeChild
{
    public partial BackEdgeHolder? Parent { get; set; }
}
