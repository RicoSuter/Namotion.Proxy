using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Ads.Attributes;

namespace Namotion.Interceptor.Ads.Tests.Integration.Models;

/// <summary>
/// One property whose PLC value cannot be assigned to its .NET type, alongside a healthy one. The
/// initial-state apply runs unguarded inside the property writer, so without a per-property guard
/// the bad value aborts the whole apply and takes the source down with it.
/// </summary>
[InterceptorSubject]
public partial class UnassignableValueIntegrationTestModel
{
    /// <summary>Mapped to a DINT, which cannot be assigned to a Guid.</summary>
    [AdsVariable("GVL.Counter")]
    public partial Guid Identifier { get; set; }

    [AdsVariable("GVL.Temperature")]
    public partial double Temperature { get; set; }
}
