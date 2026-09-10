using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// A derived property that projects whatever <see cref="Projection"/> returns and depends on
/// <see cref="Name"/>, so a scalar write on this subject recalculates the projection. The
/// projection delegate is a plain field, so arming it triggers no write and no recalculation;
/// assign it before attaching the probe.
/// </summary>
[InterceptorSubject]
public partial class DerivedProjectionProbe
{
    public Func<object?>? Projection;

    public int EvaluationCount;

    public partial string? Name { get; set; }

    [Derived]
    public object? Projected
    {
        get
        {
            _ = Name;
            Interlocked.Increment(ref EvaluationCount);
            return Projection?.Invoke();
        }
    }
}
