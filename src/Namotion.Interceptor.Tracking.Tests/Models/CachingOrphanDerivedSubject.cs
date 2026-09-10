using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class CachingOrphanDerivedSubject
{
    private Person? _cache;

    public partial Person? Stored { get; set; }

    /// <summary>Counts getter evaluations so a test can prove the retry bound was exercised.</summary>
    public int EvaluationCount { get; private set; }

    /// <summary>
    /// Caches the projected subject in a plain field, so once the stored edge is cleared every
    /// re-evaluation keeps returning a subject the graph no longer owns: a genuine orphan that
    /// no amount of retrying converges away.
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
