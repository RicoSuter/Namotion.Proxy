using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Ads.Attributes;

namespace Namotion.Interceptor.Ads.Tests.Models;

[InterceptorSubject]
public partial class TestPlcModel
{
    [AdsVariable("GVL.Temperature")]
    public partial double Temperature { get; set; }

    [AdsVariable("GVL.Timestamp")]
    public partial DateTimeOffset Timestamp { get; set; }

    [AdsVariable("GVL.Name")]
    public partial string? Name { get; set; }

    [AdsVariable("GVL.Counter")]
    public partial int Counter { get; set; }

    [AdsVariable("GVL.Pressure")]
    public partial float Pressure { get; set; }

    [AdsVariable("GVL.Values")]
    public partial int[]? Values { get; set; }

    [AdsVariable("GVL.Mode")]
    public partial TestMode Mode { get; set; }

    [AdsVariable("GVL.UnsignedMode")]
    public partial TestUnsignedMode UnsignedMode { get; set; }

    [AdsVariable("GVL.OptionalMode")]
    public partial TestMode? OptionalMode { get; set; }
}

public enum TestMode
{
    Idle = 0,
    Running = 1,
}

/// <summary>Unsigned underlying type: unboxing a signed integer into this throws.</summary>
public enum TestUnsignedMode : ushort
{
    Idle = 0,
    Disabled = 40000,
}
