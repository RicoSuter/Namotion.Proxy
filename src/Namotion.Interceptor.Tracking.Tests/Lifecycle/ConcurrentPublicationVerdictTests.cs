using System.Collections;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// A derived value that keeps exposing a subject this context does not own is convicted at the
/// retry bound, unless a topology transaction is in flight that could still be publishing it. The
/// verdict is then withheld rather than dropped: the recalculation books itself with the lifecycle
/// and is re-run when that transaction ends, so a value that was merely mid-publication converges
/// and a genuine orphan comes back for judgement. These pin the second half, that withholding a
/// verdict never becomes losing it.
/// </summary>
public class ConcurrentPublicationVerdictTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithDerivedPropertyChangeDetection();
    }

    /// <summary>Parks the first time the lifecycle scans it, which is where user code runs inside the gate.</summary>
    private sealed class ParkingEnumerable(Action onFirstEnumeration) : IEnumerable<Person>
    {
        private int _enumerations;

        public IEnumerator<Person> GetEnumerator()
        {
            if (Interlocked.Increment(ref _enumerations) == 1)
            {
                onFirstEnumeration();
            }

            return Enumerable.Empty<Person>().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static ScalarTriggeredOrphanSubject ArmOrphan(IInterceptorSubjectContext context)
    {
        var subject = new ScalarTriggeredOrphanSubject();
        ((IInterceptorSubject)subject).AttachToContext(context);
        subject.Orphan = new Person { FirstName = "orphan" };
        return subject;
    }

    /// <summary>
    /// A booking that was made but never drained leaves the flag that records it set. That flag says
    /// a booking exists; it does not say a transaction is running, and withholding requires the
    /// second. If the flag alone were allowed to decide, one undrained booking would silently
    /// disable the untracked-subject check for that property for the rest of the process, on a
    /// settled graph with nothing open anywhere. The state is constructed directly rather than
    /// raced into, because the race that produces it is cold and the consequence is not.
    /// </summary>
    [Fact]
    public void WhenABookingIsOutstandingAndNothingIsInFlight_ThenAGenuineOrphanIsStillConvicted()
    {
        // Arrange
        var context = CreateContext();
        var subject = ArmOrphan(context);
        var data = new PropertyReference(subject, nameof(ScalarTriggeredOrphanSubject.Current)).GetDerivedPropertyData();
        lock (data)
        {
            data.HasWithheldRecalculation = true;
        }

        // Act
        var exception = Record.Exception(() => subject.Name = "trigger");

        // Assert
        Assert.IsType<LifecycleContractViolationException>(exception);
    }

    [Fact]
    public void WhenNoTransactionIsInFlight_ThenAGenuineOrphanIsConvicted()
    {
        // Arrange
        var context = CreateContext();
        var subject = ArmOrphan(context);
        var evaluationsBefore = subject.EvaluationCount;

        // Act
        var exception = Record.Exception(() => subject.Name = "trigger");

        // Assert
        Assert.IsType<LifecycleContractViolationException>(exception);
        Assert.True(subject.EvaluationCount - evaluationsBefore >= DerivedPropertyChangeHandler.MaxStabilizationIterations,
            "the conviction must come out of the bounded retry loop, not the first detection");
    }

    /// <summary>
    /// A transaction that is in flight while a genuine orphan is evaluated buys that orphan a
    /// deferral and nothing more. The write that ran into it is innocent and completes, and the
    /// orphan is judged once the transaction it was hiding behind has ended. Held open by parking
    /// inside the discovery scan, which is user code running under the topology gate, so the gate
    /// really is held for the whole recalculation.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAnUnrelatedAttachHoldsTheGate_ThenTheOrphanIsJudgedOnceItEnds()
    {
        // Arrange
        var context = CreateContext();
        var subject = ArmOrphan(context);

        var parked = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var unrelated = new EnumerableChildrenHolder
        {
            Children = new ParkingEnumerable(() =>
            {
                parked.Set();
                release.Wait(WriteProtocolAcceptance.RendezvousTimeout);
            })
        };

        var attaching = new Thread(() => ((IInterceptorSubject)unrelated).AttachToContext(context)) { IsBackground = true };
        attaching.Start();
        var reachedPark = parked.Wait(WriteProtocolAcceptance.RendezvousTimeout);

        // Act
        var exception = Record.Exception(() => subject.Name = "trigger");
        var evaluationsWhenWithheld = subject.EvaluationCount;

        release.Set();
        var attachCompleted = attaching.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert
        Assert.True(reachedPark, "the unrelated attach never parked inside the topology gate");
        Assert.True(attachCompleted, "the parked attach never finished");
        Assert.Null(exception);

        // An attach runs no recalculation cascade of any kind, so the only thing that could have
        // re-evaluated the withheld value is the booking the deferral made with the lifecycle.
        Assert.True(subject.EvaluationCount > evaluationsWhenWithheld,
            "the withheld value was never re-evaluated when the transaction it was waiting on ended");

        // The deferral is not an acquittal: with nothing in flight, the same value is convicted.
        Assert.IsType<LifecycleContractViolationException>(
            Record.Exception(() => subject.Name = "again"));
    }

    /// <summary>
    /// The same for a structural write, which is the transaction that can genuinely open the window
    /// this deferral exists for. The write here is on a subject the derived value never reads, so
    /// nothing about it will ever recalculate this property: the booking is the only thing that
    /// brings the value back, which is what this asserts.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAnUnrelatedStructuralWriteHoldsTheGate_ThenTheOrphanIsJudgedOnceItEnds()
    {
        // Arrange
        var context = CreateContext();
        var subject = ArmOrphan(context);
        var unrelated = new EnumerableChildrenHolder();
        ((IInterceptorSubject)unrelated).AttachToContext(context);

        var parked = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var writing = new Thread(() => unrelated.Children = new ParkingEnumerable(() =>
        {
            parked.Set();
            release.Wait(WriteProtocolAcceptance.RendezvousTimeout);
        }))
        {
            IsBackground = true
        };

        writing.Start();
        var reachedPark = parked.Wait(WriteProtocolAcceptance.RendezvousTimeout);

        // Act
        var exception = Record.Exception(() => subject.Name = "trigger");
        var evaluationsWhenWithheld = subject.EvaluationCount;

        release.Set();
        var writeCompleted = writing.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert
        Assert.True(reachedPark, "the unrelated write never parked inside the topology gate");
        Assert.True(writeCompleted, "the parked write never finished");
        Assert.Null(exception);

        // The write is on a subject this derived value never reads, so its cascade cannot reach
        // this property: the booking is the only thing that could have re-evaluated it.
        Assert.True(subject.EvaluationCount > evaluationsWhenWithheld,
            "the withheld value was never re-evaluated when the transaction it was waiting on ended");

        // The booking ran when the write ended: the re-evaluated orphan reached the same verdict
        // there, where it is traced rather than thrown because the thread it would have hit was
        // only ending an unrelated transaction. With nothing in flight it is thrown again.
        Assert.IsType<LifecycleContractViolationException>(
            Record.Exception(() => subject.Name = "again"));
    }
}
