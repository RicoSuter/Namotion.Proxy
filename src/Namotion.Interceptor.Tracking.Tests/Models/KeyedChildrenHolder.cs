using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>Holds children under stable keys, so occurrences are matched by key rather than by ordinal.</summary>
[InterceptorSubject]
public partial class KeyedChildrenHolder
{
    public partial IReadOnlyDictionary<string, Person>? Children { get; set; }

    public override string ToString() => "H";
}
