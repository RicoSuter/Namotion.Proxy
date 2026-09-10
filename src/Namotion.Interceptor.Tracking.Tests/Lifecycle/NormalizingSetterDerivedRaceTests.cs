using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The write protocol claims the proposed value, then lets the terminal store, then reconciles the
/// authoritative getter output. A normalizing setter that stores something other than the proposed
/// value therefore leaves a subject attached to no context in the backing field for the length of
/// that window, where a derived getter on another thread can read it.
/// </summary>
public class NormalizingSetterDerivedRaceTests
{
    /// <summary>
    /// Reproduces the reported defect that a derived recalculation convicts a subject a normalizing
    /// setter stored before the reconcile attached it. The store-to-reconcile window is held open
    /// artificially; the window itself is real and the write is legal, so the recalculating thread
    /// must not report the transient exposure as a contract violation.
    ///
    /// The park point is the authoritative getter the lifecycle rereads between its own
    /// <c>next</c> and its reconcile. Do not move it into a write interceptor and do not move it
    /// into the stored setter. A write interceptor's position in the chain is decided by ordering
    /// attributes and registration order, so any change to how chains are partitioned can move the
    /// park out of the window and turn this test green without the defect being fixed. The stored
    /// setter runs inside the terminal's per-subject lock, which the reading thread's own chain
    /// terminal also takes, so parking there blocks the reader instead of racing it, and the test
    /// then fails on the join rather than on the defect. The getter reread is invoked by the
    /// lifecycle itself, after the terminal lock is released, and is immune to both.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenANormalizingSetterHasStoredButNotReconciled_ThenAConcurrentRecalculationDoesNotConvictTheSubject()
    {
        // Arrange: the terminal substitutes a subject the write never proposed, so the substituted
        // subject is attached to nothing until the reconcile claims it.
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithDerivedPropertyChangeDetection();

        var parent = new SubstitutingDevice();
        ((IInterceptorSubject)parent).AttachToContext(context);
        var substitute = new SubstitutingDevice();
        parent.Substitute = substitute;

        var probe = new DerivedProjectionProbe { Projection = () => parent.Child };
        ((IInterceptorSubject)probe).AttachToContext(context);

        var parked = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var storedValueAtPark = 0;
        parent.OnAuthoritativeValueRead = storedValue =>
        {
            parent.OnAuthoritativeValueRead = null;
            Volatile.Write(ref storedValueAtPark, ReferenceEquals(storedValue, substitute) ? 1 : 0);
            parked.Set();
            release.Wait(WriteProtocolAcceptance.RendezvousTimeout);
        };

        Exception? writeException = null;
        var writer = new Thread(
            () => writeException = Record.Exception(() => parent.Child = new SubstitutingDevice()))
        {
            IsBackground = true
        };

        // Act: the writer parks inside the window, then an unrelated scalar write on another
        // subject recalculates a derived property that projects the stored value.
        writer.Start();
        var reachedPark = parked.Wait(WriteProtocolAcceptance.RendezvousTimeout);

        Exception? recalculationException = null;
        var recalculator = new Thread(
            () => recalculationException = Record.Exception(() => probe.Name = "trigger"))
        {
            IsBackground = true
        };
        recalculator.Start();
        var recalculatorCompleted = recalculator.Join(WriteProtocolAcceptance.RendezvousTimeout);

        release.Set();
        var writerCompleted = writer.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert: the window was actually open while the recalculation ran, so the repro cannot
        // pass by the two writes serializing or by the park landing outside the window.
        Assert.True(reachedPark, "the normalizing write never parked inside the store-to-reconcile window");
        Assert.True(Volatile.Read(ref storedValueAtPark) == 1,
            "the park did not land between the terminal store and the reconcile: the backing field " +
            "did not hold the substituted subject when the authoritative getter was reread");
        Assert.True(recalculatorCompleted, "the recalculating thread never finished");
        Assert.True(writerCompleted, "the parked writer never finished");
        Assert.Null(writeException);

        // The scalar write is innocent and the structural write is legal, so neither may observe a
        // contract violation for a subject that is merely mid-publication.
        Assert.True(recalculationException is null,
            "an unrelated scalar write was rejected because a derived getter observed a subject " +
            "that a normalizing setter had stored but the reconcile had not attached yet: " +
            $"{recalculationException?.GetType().Name}: {recalculationException?.Message}");
        Assert.Same(context, ((IInterceptorSubject)substitute).TryGetContext());

        // The withheld value is not lost. Withholding is only safe because the exposure came from a
        // dependency, so the write that opened the window ends by cascading to this property, and
        // the value that replaces the withheld one arrives on the writing thread.
        Assert.Same(substitute, probe.Projected);
    }

    /// <summary>
    /// The same window, read through an accessor nothing intercepts. A subject that exposes state it
    /// also keeps in an intercepted property is an ordinary shape, and reading it records no
    /// dependency at all, so a design that decides whether to withhold by consulting the derived
    /// value's dependency set cannot see this one and convicts the innocent write. The window, the
    /// park and the assertions are the ones above; only the accessor differs.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenTheProjectionReadsThroughANonInterceptedAccessor_ThenAConcurrentRecalculationStillDoesNotConvict()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithDerivedPropertyChangeDetection();

        var parent = new SubstitutingDevice();
        ((IInterceptorSubject)parent).AttachToContext(context);
        var substitute = new SubstitutingDevice();
        parent.Substitute = substitute;

        var probe = new DerivedProjectionProbe { Projection = () => parent.ChildWithoutInterception };
        ((IInterceptorSubject)probe).AttachToContext(context);

        var parked = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var storedValueAtPark = 0;
        parent.OnAuthoritativeValueRead = storedValue =>
        {
            parent.OnAuthoritativeValueRead = null;
            Volatile.Write(ref storedValueAtPark, ReferenceEquals(storedValue, substitute) ? 1 : 0);
            parked.Set();
            release.Wait(WriteProtocolAcceptance.RendezvousTimeout);
        };

        Exception? writeException = null;
        var writer = new Thread(
            () => writeException = Record.Exception(() => parent.Child = new SubstitutingDevice()))
        {
            IsBackground = true
        };

        // Act
        writer.Start();
        var reachedPark = parked.Wait(WriteProtocolAcceptance.RendezvousTimeout);

        Exception? recalculationException = null;
        var recalculator = new Thread(
            () => recalculationException = Record.Exception(() => probe.Name = "trigger"))
        {
            IsBackground = true
        };
        recalculator.Start();
        var recalculatorCompleted = recalculator.Join(WriteProtocolAcceptance.RendezvousTimeout);
        var evaluationsWhenWithheld = probe.EvaluationCount;

        release.Set();
        var writerCompleted = writer.Join(WriteProtocolAcceptance.RendezvousTimeout);
        var evaluationsAfterTheWrite = probe.EvaluationCount;

        // Assert
        Assert.True(reachedPark, "the normalizing write never parked inside the store-to-reconcile window");
        Assert.True(Volatile.Read(ref storedValueAtPark) == 1,
            "the park did not land between the terminal store and the reconcile");
        Assert.True(recalculatorCompleted, "the recalculating thread never finished");

        // Nothing this write cascades to reaches the probe, because reading through a plain accessor
        // recorded no dependency on the property that was written. The value comes back only because
        // the withholding recalculation booked itself with the lifecycle.
        Assert.True(evaluationsAfterTheWrite > evaluationsWhenWithheld,
            "the withheld value was never re-evaluated when the write it was waiting on ended");
        Assert.True(writerCompleted, "the parked writer never finished");
        Assert.Null(writeException);
        Assert.True(recalculationException is null,
            "an unrelated scalar write was rejected because a derived getter read a mid-publication " +
            "subject through an accessor that records no dependency: " +
            $"{recalculationException?.GetType().Name}: {recalculationException?.Message}");
        Assert.Same(context, ((IInterceptorSubject)substitute).TryGetContext());
        Assert.Same(substitute, probe.Projected);
    }
}
