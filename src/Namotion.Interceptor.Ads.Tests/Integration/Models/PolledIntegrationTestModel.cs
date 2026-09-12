using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Ads.Attributes;

namespace Namotion.Interceptor.Ads.Tests.Integration.Models;

[InterceptorSubject]
public partial class PolledIntegrationTestModel
{
    [AdsVariable("GVL.PolledCounter", ReadMode = AdsReadMode.Polled)]
    public partial int PolledCounter { get; set; }
}
