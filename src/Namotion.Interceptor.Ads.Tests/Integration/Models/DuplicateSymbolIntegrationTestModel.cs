using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Ads.Attributes;

namespace Namotion.Interceptor.Ads.Tests.Integration.Models;

/// <summary>
/// Two properties mapped to the same PLC symbol, plus an unrelated one. The handle bag the reactive
/// extension builds keys by symbol in a plain dictionary, so a group containing the same symbol
/// twice fails to register and takes every property in it down with it.
/// </summary>
[InterceptorSubject]
public partial class DuplicateSymbolIntegrationTestModel
{
    [AdsVariable("GVL.Temperature")]
    public partial double Temperature { get; set; }

    [AdsVariable("GVL.Temperature")]
    public partial double TemperatureMirror { get; set; }

    [AdsVariable("GVL.Counter")]
    public partial int Counter { get; set; }
}
