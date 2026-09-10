using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Reconciliation runs a complete removal pass before its addition pass. A subject the new value
/// still holds, but under a different key, therefore loses its only support in the removal pass and
/// is handed back to no context until the addition pass re-claims it.
/// </summary>
public class ReconcileRetentionWindowTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    private static object? GetCommittedBaseline(IInterceptorSubjectContext context, IInterceptorSubject subject, string propertyName)
    {
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        return lifecycle.Graph.GetBaseline(new PropertyReference(subject, propertyName));
    }

    /// <summary>
    /// Reproduces the finding that a subject the write is about to re-commit is nonetheless released
    /// mid-transition, and is claimable by another context while it is. The contract asserted here is
    /// the remedy rather than the symptom: a subject the new value still holds stays attached for the
    /// whole transition, so a concurrent claim from another context is refused rather than won.
    ///
    /// The window is held open artificially by parking inside the detach callback of a second subject
    /// that the same removal pass genuinely drops. That callback is ordinary user code running inside
    /// the window, and the park is positioned by phase rather than by ordering: the guard below
    /// asserts that the committed baseline is already the new value when the park runs, which is what
    /// makes this the removal pass and not some earlier point.
    ///
    /// Suppressing the release of the retained subject makes this pass, and removing that suppression
    /// makes it fail again on the attachment assertion, which is the property the retention rule
    /// needs from a test.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAKeyMoveRetainsASubject_ThenItStaysAttachedAndAConcurrentClaimIsRefused()
    {
        // Arrange: the moving car keeps an occurrence in the new value under a different key; the
        // parking car is genuinely dropped, so its detach callback runs inside the removal pass.
        var context = CreateContext();
        var otherContext = CreateContext();

        var released = new ManualResetEventSlim(false);
        var claimAttempted = new ManualResetEventSlim(false);
        var parkObserved = false;
        object? baselineAtPark = null;
        Car? mover = null;
        Garage? garage = null;
        var handler = new DelegateLifecycleHandler(change =>
        {
            if (!change.IsContextDetach || change.Subject is not Car { Name: "parking" })
            {
                return;
            }

            baselineAtPark = GetCommittedBaseline(context, garage!, nameof(Garage.CarsByName));
            released.Set();
            parkObserved = claimAttempted.Wait(WriteProtocolAcceptance.RendezvousTimeout);
        });

        context.WithService(() => handler, _ => false);

        garage = new Garage(context) { Name = "G" };
        mover = new Car { Name = "moving" };
        var parker = new Car { Name = "parking" };
        garage.CarsByName = new Dictionary<string, Car> { ["parking"] = parker, ["moving"] = mover };
        var newValue = new Dictionary<string, Car> { ["moved"] = mover };

        var moverAttachedDuringWindow = false;
        Exception? claimException = null;
        var claimer = new Thread(() =>
        {
            if (!released.Wait(WriteProtocolAcceptance.RendezvousTimeout))
            {
                return;
            }

            moverAttachedDuringWindow = ((IInterceptorSubject)mover).TryGetContext() is not null;
            claimException = Record.Exception(() => ((IInterceptorSubject)mover).AttachToContext(otherContext));
            claimAttempted.Set();
        })
        {
            IsBackground = true
        };

        // Act
        claimer.Start();
        var assignmentException = Record.Exception(() => garage.CarsByName = newValue);
        var claimerCompleted = claimer.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert: the rendezvous happened, and it happened inside the removal pass. Either guard
        // failing means the instrument moved rather than the behaviour changing.
        Assert.True(claimerCompleted, "the claiming thread never finished");
        Assert.True(parkObserved, "the release descent never parked inside the reconcile window");
        Assert.Same(newValue, baselineAtPark);

        // A subject the new value still holds must not become claimable during the transition.
        Assert.True(moverAttachedDuringWindow,
            "the retained subject was handed back to no context inside the reconcile window, so " +
            "another context could claim a subject this write was about to re-commit");
        Assert.IsType<InvalidOperationException>(claimException);
        Assert.Null(assignmentException);

        // The transition committed the move rather than being defeated by it.
        Assert.Same(context, ((IInterceptorSubject)mover).TryGetContext());
        Assert.Equal("moved", ((IInterceptorSubject)mover).GetParents()[0].Index);
        Assert.Null(((IInterceptorSubject)parker).TryGetContext());
    }
}
