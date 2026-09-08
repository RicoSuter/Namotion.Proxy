using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>A computed projection of a plain field, recalculated by a scalar dependency.</summary>
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
