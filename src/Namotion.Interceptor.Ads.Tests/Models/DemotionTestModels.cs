using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Ads.Attributes;

namespace Namotion.Interceptor.Ads.Tests.Models;

/// <summary>
/// Test model with explicit Notification read mode.
/// </summary>
[InterceptorSubject]
public partial class NotificationOnlyModel
{
    [AdsVariable("GVL.Value", ReadMode = AdsReadMode.Notification, CycleTime = 50)]
    public partial float Value { get; set; }
}

/// <summary>
/// Test model with explicit Polled read mode.
/// </summary>
[InterceptorSubject]
public partial class PolledOnlyModel
{
    [AdsVariable("GVL.Value", ReadMode = AdsReadMode.Polled)]
    public partial float Value { get; set; }
}

/// <summary>
/// Test model with Auto read mode (default).
/// </summary>
[InterceptorSubject]
public partial class AutoModeModel
{
    [AdsVariable("GVL.Value")]
    public partial float Value { get; set; }
}

/// <summary>
/// Test model with multiple Auto mode properties at different priorities and cycle times for demotion testing.
/// </summary>
[InterceptorSubject]
public partial class DemotionTestModel
{
    [AdsVariable("GVL.FastHighPriority", ReadMode = AdsReadMode.Auto, CycleTime = 10, Priority = -1)]
    public partial float FastHighPriority { get; set; }

    [AdsVariable("GVL.FastNormal", ReadMode = AdsReadMode.Auto, CycleTime = 10, Priority = 0)]
    public partial float FastNormal { get; set; }

    [AdsVariable("GVL.SlowNormal", ReadMode = AdsReadMode.Auto, CycleTime = 1000, Priority = 0)]
    public partial float SlowNormal { get; set; }

    [AdsVariable("GVL.SlowLowPriority", ReadMode = AdsReadMode.Auto, CycleTime = 1000, Priority = 10)]
    public partial float SlowLowPriority { get; set; }

    [AdsVariable("GVL.MediumLowPriority", ReadMode = AdsReadMode.Auto, CycleTime = 100, Priority = 10)]
    public partial float MediumLowPriority { get; set; }
}

/// <summary>
/// Test model with mixed read modes for demotion testing.
/// </summary>
[InterceptorSubject]
public partial class MixedReadModeModel
{
    [AdsVariable("GVL.NotificationVar", ReadMode = AdsReadMode.Notification, CycleTime = 10)]
    public partial float NotificationVariable { get; set; }

    [AdsVariable("GVL.PolledVar", ReadMode = AdsReadMode.Polled)]
    public partial float PolledVariable { get; set; }

    [AdsVariable("GVL.AutoVar1", ReadMode = AdsReadMode.Auto, CycleTime = 50, Priority = 0)]
    public partial float AutoVariable1 { get; set; }

    [AdsVariable("GVL.AutoVar2", ReadMode = AdsReadMode.Auto, CycleTime = 500, Priority = 5)]
    public partial float AutoVariable2 { get; set; }
}
