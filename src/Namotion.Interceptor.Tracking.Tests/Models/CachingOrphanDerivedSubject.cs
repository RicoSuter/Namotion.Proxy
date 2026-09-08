using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class CachingOrphanDerivedSubject
{
    private Person? _cache;

    public partial Person? Stored { get; set; }

    /// <summary>Counts getter evaluations.</summary>
    public int EvaluationCount { get; private set; }

    /// <summary>
    /// Caches the projected subject in a plain field, so once the stored edge is cleared every
    /// re-evaluation keeps returning a subject the graph no longer owns.
    /// </summary>
    [Derived]
    public Person? Current
    {
        get
        {
            EvaluationCount++;
            return _cache ??= Stored;
        }
    }
}
