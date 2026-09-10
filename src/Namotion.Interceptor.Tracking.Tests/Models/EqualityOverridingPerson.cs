using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// A hand-shaped worst case for subject equality: every instance compares equal and hashes
/// identically. Graph membership is identity, so the lifecycle's subject-keyed state must key on
/// references; under default equality this model would merge distinct nodes.
/// </summary>
[InterceptorSubject]
public partial class EqualityOverridingPerson
{
    public EqualityOverridingPerson()
    {
        Friends = [];
    }

    public partial string? Name { get; set; }

    public partial EqualityOverridingPerson? Partner { get; set; }

    public partial EqualityOverridingPerson[] Friends { get; set; }

    public override bool Equals(object? obj) => obj is EqualityOverridingPerson;

    public override int GetHashCode() => 0;
}
