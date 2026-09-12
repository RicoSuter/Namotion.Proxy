using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Ads.Attributes;

namespace Namotion.Interceptor.Ads.Tests.Integration.Models;

/// <summary>
/// Two notification properties on the default cycle time and one on its own, so notification
/// grouping has to produce two subscriptions rather than one or three.
/// </summary>
[InterceptorSubject]
public partial class MixedCycleTimeIntegrationTestModel
{
    [AdsVariable("GVL.Temperature")]
    public partial double Temperature { get; set; }

    [AdsVariable("GVL.IsRunning")]
    public partial bool IsRunning { get; set; }

    [AdsVariable("GVL.Counter", CycleTime = 500)]
    public partial int Counter { get; set; }
}
