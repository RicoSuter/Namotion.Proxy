using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// A derived property that returns whatever <see cref="Orphan"/> holds and depends on
/// <see cref="Name"/>, so a scalar write recalculates it. The orphan is a plain field, armed after
/// the subject is attached: arming it before would fail the attach-time evaluation instead, which
/// is a different check on a different path. A scalar trigger is what keeps the recalculating
/// thread out of the topology gate, so it can observe another thread that holds it.
/// </summary>
[InterceptorSubject]
public partial class ScalarTriggeredOrphanSubject
{
    public Person? Orphan;

    public int EvaluationCount;

    public partial string? Name { get; set; }

    [Derived]
    public Person? Current
    {
        get
        {
            _ = Name;
            EvaluationCount++;
            return Orphan;
        }
    }
}
