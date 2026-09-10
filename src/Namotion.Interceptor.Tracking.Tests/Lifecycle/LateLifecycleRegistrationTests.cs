using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Registering the lifecycle behind a subject that is already attached is not a supported
/// configuration and is rejected. A root anchored while the context had no lifecycle never enters
/// the ownership graph that arrives after it, so it stays attached and unowned forever, and every
/// structural write on it takes the graph's unowned arm: no claim, no validation, no reconcile.
/// The two sequences that arm permits are pinned below as the reason the rejection is worth a throw.
/// </summary>
public class LateLifecycleRegistrationTests
{
    [Fact]
    public void WhenALifecycleIsRegisteredAfterASubjectWasAttached_ThenTheRegistrationIsRejected()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var garage = new Garage();
        garage.AttachToContext(context);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => context.WithLifecycle());

        // Assert
        Assert.Contains("before attaching", exception.Message);
        Assert.Null(context.TryGetService<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenTheLifecycleArrivesThroughAnotherFeature_ThenTheRegistrationIsRejectedThereToo()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var garage = new Garage();
        garage.AttachToContext(context);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => context.WithFullPropertyTracking());
        Assert.Throws<InvalidOperationException>(() => context.WithDerivedPropertyChangeDetection());
    }

    [Fact]
    public void WhenTheSequenceWouldInstallAForeignSubject_ThenTheLifecycleRegistrationStopsItFirst()
    {
        // Arrange: the sequence that used to commit a subject owned by another context into a field
        // of this one, with no claim, no validation, no reconcile and no exception.
        var foreignContext = InterceptorSubjectContext.Create().WithLifecycle();
        var foreignCar = new Car();
        foreignCar.AttachToContext(foreignContext);

        var lateContext = InterceptorSubjectContext.Create();
        var lateGarage = new Garage();
        lateGarage.AttachToContext(lateContext);

        // Act
        Assert.Throws<InvalidOperationException>(() => lateContext.WithLifecycle());

        // Assert: the graph that would have taken the foreign subject in was never registered, and
        // the supported ordering refuses the same write at the property instead.
        Assert.Null(lateContext.TryGetService<ILifecycleInterceptor>());

        var orderedContext = InterceptorSubjectContext.Create().WithLifecycle();
        var orderedGarage = new Garage();
        orderedGarage.AttachToContext(orderedContext);

        Assert.Throws<InvalidOperationException>(() => orderedGarage.Cars = [foreignCar]);
        Assert.Empty(orderedGarage.Cars);
        Assert.Same(foreignContext, foreignCar.TryGetContext());
    }

    [Fact]
    public void WhenTheSequenceWouldLeaveAChildUnattached_ThenTheLifecycleRegistrationStopsItFirst()
    {
        // Arrange: the same-context shape, where the child used to be stored without ever being
        // attached, registered or given a single lifecycle callback.
        var lateContext = InterceptorSubjectContext.Create();
        var lateGarage = new Garage();
        lateGarage.AttachToContext(lateContext);

        // Act
        Assert.Throws<InvalidOperationException>(() => lateContext.WithLifecycle());

        // Assert: no graph was registered behind the attach, and the supported ordering attaches the
        // child and runs its callbacks.
        Assert.Null(lateContext.TryGetService<ILifecycleInterceptor>());

        var orderedContext = InterceptorSubjectContext.Create().WithLifecycle();
        var orderedGarage = new Garage();
        orderedGarage.AttachToContext(orderedContext);
        var car = new Car();

        orderedGarage.Cars = [car];

        Assert.Same(orderedContext, car.TryGetContext());
        Assert.NotEmpty(car.Attachements);
    }

    [Fact]
    public void WhenTheLifecycleWasRegisteredBeforeTheAttach_ThenRegisteringItAgainStaysIdempotent()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var garage = new Garage();
        garage.AttachToContext(context);

        // Act
        context.WithLifecycle();
        context.WithFullPropertyTracking();

        // Assert
        Assert.Single(context.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenNothingWasAttachedYet_ThenTheLifecycleStillRegisters()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();

        // Act
        context.WithLifecycle();

        // Assert
        Assert.NotNull(context.TryGetService<ILifecycleInterceptor>());
    }
}
