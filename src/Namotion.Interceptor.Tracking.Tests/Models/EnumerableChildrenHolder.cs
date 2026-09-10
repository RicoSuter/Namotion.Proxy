using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// Holds its children behind a bare <see cref="IEnumerable{T}"/> declaration, so a test can supply
/// a user implementation that runs its own code while the lifecycle scans the value. Neither
/// <see cref="System.Collections.ICollection"/> nor <see cref="System.Collections.IDictionary"/>
/// is involved, which is what routes the scan through the enumerating fallback arm.
/// </summary>
[InterceptorSubject]
public partial class EnumerableChildrenHolder
{
    public partial IEnumerable<Person>? Children { get; set; }
}
