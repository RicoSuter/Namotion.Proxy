using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class TransientOrphanDerivedSubject
{
    /// <summary>
    /// Armed by the test; not intercepted, so arming it does not trigger a recalculation. Exactly
    /// one evaluation returns an unattached subject and every later one is clean, which is the
    /// deterministic single-threaded stand-in for a projected subject that a concurrent structural
    /// write detached between this thread's evaluation and its commit.
    /// </summary>
    public bool ReturnUnattachedSubjectOnce { get; set; }

    public partial string? Name { get; set; }

    [Derived]
    public Person? Current
    {
        get
        {
            _ = Name;
            if (ReturnUnattachedSubjectOnce)
            {
                ReturnUnattachedSubjectOnce = false;
                return new Person { FirstName = "temp" };
            }

            return null;
        }
    }
}
