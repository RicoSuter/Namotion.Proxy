using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Ads.Attributes;

namespace Namotion.Interceptor.Ads.Tests.Integration.Models;

/// <summary>
/// A property mapped to a WSTRING, which holds two bytes per character. The any-type notification
/// path marshals single-byte text, so it must fall back to polling rather than register and decode
/// the payload as one character.
/// </summary>
[InterceptorSubject]
public partial class WideStringIntegrationTestModel
{
    [AdsVariable("GVL.WideName")]
    public partial string? WideName { get; set; }
}
