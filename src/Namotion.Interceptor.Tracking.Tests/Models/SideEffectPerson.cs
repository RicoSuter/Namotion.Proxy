using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// Test model with a derived property getter that has side effects:
/// writing to a subject-typed property during evaluation.
/// Used to verify that RecalculateDerivedProperty evaluates the getter
/// outside lock(data), preventing deadlock with LifecycleInterceptor.
/// </summary>
[InterceptorSubject]
public partial class SideEffectPerson
{
    private int _successfulCompanionWrites;

    public partial string? Name { get; set; }

    public partial Person? Companion { get; set; }

    /// <summary>
    /// Counts Companion writes that actually landed. The absorption below eats a write the topology
    /// gate contract refuses, so without this count the deadlock regression test cannot tell a live
    /// recalculation path from one whose writes are all silently absorbed.
    /// </summary>
    public int SuccessfulCompanionWriteCount => Volatile.Read(ref _successfulCompanionWrites);

    [Derived]
    public string Greeting => ComputeGreeting();

    private string ComputeGreeting()
    {
        // Side effect: writes to a subject-typed property during getter evaluation.
        // This triggers LifecycleInterceptor.WriteProperty → lock(_attachedSubjects).
        // Without the unlocked evaluation in RecalculateDerivedProperty, this would
        // deadlock when concurrent lifecycle operations acquire lock(data) for Greeting.
        //
        // A getter runs wherever the evaluation that needs it runs, including inside a thread that
        // already holds another context's topology transaction, which the gate contract refuses.
        // That violation is absorbed here so the tests keep driving the recalculation path, which
        // is the one under test.
        try
        {
            Companion = null;
            Interlocked.Increment(ref _successfulCompanionWrites);
        }
        catch (LifecycleContractViolationException)
        {
        }

        return $"Hello, {Name}";
    }
}
