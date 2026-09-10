using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// A release drops the subject's ownership before its detach callbacks run and hands the executor
/// back only after them, so a callback sees a subject that is attached but unowned. Property
/// admission from that state must not publish new edges under an owner the release already left
/// behind.
/// </summary>
public class DetachCallbackAdmissionTests
{
    private static SubjectPropertyMetadata CreateStructuralProperty(string name, IInterceptorSubject child)
    {
        return new SubjectPropertyMetadata(
            name, typeof(Person), [], _ => child, null, isIntercepted: true, isDynamic: true);
    }

    /// <summary>
    /// Reproduces the finding that <c>AddProperties</c> from a detach callback publishes ownership
    /// under a parent that is already being released. Reproduces on a single thread with no
    /// artificially held window: the release itself invokes the callback in that state. The
    /// departing subject is scalar-only, which is what makes the admission treat it as fully
    /// seeded and take the edge-publishing arm.
    /// </summary>
    [Fact]
    public void WhenADetachCallbackAddsAStructuralProperty_ThenTheNewChildIsNotAttached()
    {
        // Arrange
        Tire? departingTire = null;
        var orphan = new Person { FirstName = "orphan" };
        Exception? admissionException = null;
        var callbackRan = false;
        var handler = new DelegateLifecycleHandler(change =>
        {
            if (departingTire is null ||
                !change.IsContextDetach ||
                !ReferenceEquals(change.Subject, departingTire))
            {
                return;
            }

            callbackRan = true;
            admissionException = Record.Exception(
                () => change.Subject.AddProperties(CreateStructuralProperty("Orphan", orphan)));
        });

        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler, _ => false);

        var car = new Car(context) { Name = "C" };
        departingTire = car.Tires[0];

        // Precondition, not incidental: the admission arm this test is about is reachable only
        // because a subject with no structural property counts as fully seeded, so its baselines
        // being dropped by the release does not divert the admission into its early return. Giving
        // this model a structural property would take the other arm, publish no edge, and leave
        // every assertion below satisfied without anything being exercised.
        Assert.DoesNotContain(((IInterceptorSubject)departingTire).Properties.Values,
            property => OwnershipGraph.IsStructural(property));

        // Act: dropping every tire releases the departing one and runs its detach callback.
        car.Tires = [];

        // Assert: the callback ran and the admission was accepted, so the repro is not vacuous.
        Assert.True(callbackRan, "the detach callback for the departing tire never ran");
        Assert.Null(admissionException);
        Assert.Null(((IInterceptorSubject)departingTire).TryGetContext());

        // The chosen behaviour is metadata without edges, so the batch is visible on the subject.
        Assert.True(((IInterceptorSubject)departingTire).Properties.ContainsKey("Orphan"));

        // The owner the new edge was published under no longer exists, so nothing will ever
        // release the child it references.
        Assert.True(((IInterceptorSubject)orphan).TryGetContext() is null,
            "the admission published an ownership edge from a subject the release had already " +
            "removed from the graph, so the referenced child stayed attached to the context with " +
            "no owner that any later release can reach");
    }

    /// <summary>
    /// The same shape on a departing subject that does own structural properties. It takes the
    /// other admission arm, because the release dropped baselines it actually had, so this pins
    /// that arm's outcome as the one the scalar-only case has to match.
    /// </summary>
    [Fact]
    public void WhenADetachCallbackAddsAStructuralPropertyToASeededSubject_ThenTheNewChildIsNotAttached()
    {
        // Arrange
        Car? departingCar = null;
        var orphan = new Person { FirstName = "orphan" };
        Exception? admissionException = null;
        var callbackRan = false;
        var handler = new DelegateLifecycleHandler(change =>
        {
            if (departingCar is null ||
                !change.IsContextDetach ||
                !ReferenceEquals(change.Subject, departingCar))
            {
                return;
            }

            callbackRan = true;
            admissionException = Record.Exception(
                () => change.Subject.AddProperties(CreateStructuralProperty("Orphan", orphan)));
        });

        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler, _ => false);

        var garage = new Garage(context) { Name = "G" };
        departingCar = new Car { Name = "C" };
        garage.Cars = [departingCar];

        // The counterpart of the scalar-only precondition above: this subject does carry a
        // structural property, so the release removes baselines it really had.
        Assert.Contains(((IInterceptorSubject)departingCar).Properties.Values,
            property => OwnershipGraph.IsStructural(property));

        // Act
        garage.Cars = [];

        // Assert
        Assert.True(callbackRan, "the detach callback for the departing car never ran");
        Assert.Null(admissionException);
        Assert.Null(((IInterceptorSubject)departingCar).TryGetContext());

        // The chosen behaviour is metadata without edges, so the batch is visible on the subject.
        Assert.True(((IInterceptorSubject)departingCar).Properties.ContainsKey("Orphan"));
        Assert.True(((IInterceptorSubject)orphan).TryGetContext() is null,
            "the admission published an ownership edge from a subject the release had already " +
            "removed from the graph, so the referenced child stayed attached to the context with " +
            "no owner that any later release can reach");
    }
}
