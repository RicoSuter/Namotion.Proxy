using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class StoringDerivedSubject
{
    /// <summary>
    /// A derived property with a generator-emitted backing field, so it is the sole store of
    /// whatever is assigned to it rather than a projection of another property. The declaration
    /// contradicts itself: [Derived] says the value is a function of other state, while the
    /// backing field makes this property the only thing holding it.
    /// </summary>
    [Derived]
    public partial Person? Current { get; set; }
}
