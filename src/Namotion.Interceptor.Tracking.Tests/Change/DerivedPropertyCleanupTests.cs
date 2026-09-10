using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyCleanupTests
{
    [Fact]
    public void WhenDerivedPropertyIsDetached_ThenItIsRemovedFromDependenciesUsedByProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithLifecycle();

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        // Verify initial state - FullName depends on FirstName and LastName
        var fullNameProperty = new PropertyReference(person, nameof(Person.FullName));
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProperty = new PropertyReference(person, nameof(Person.LastName));

        // FullName should be in FirstName's UsedByProperties
        Assert.Contains(fullNameProperty, firstNameProperty.GetUsedByProperties().Items.ToArray());
        Assert.Contains(fullNameProperty, lastNameProperty.GetUsedByProperties().Items.ToArray());

        // Act - Detach the FullName property (simulating subject detachment)
        person.DetachSubjectProperty(fullNameProperty);

        // Assert - FullName should be removed from both dependencies' UsedByProperties
        Assert.DoesNotContain(fullNameProperty, firstNameProperty.GetUsedByProperties().Items.ToArray());
        Assert.DoesNotContain(fullNameProperty, lastNameProperty.GetUsedByProperties().Items.ToArray());
    }

    [Fact]
    public void WhenSourcePropertyIsDetachedStandalone_ThenItIsRemovedFromDerivedPropertiesRequiredProperties()
    {
        // Standalone detach (not inside WriteProperty) uses immediate Remove() for Case 2.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithLifecycle();

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        var fullNameProperty = new PropertyReference(person, nameof(Person.FullName));
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));

        // FullName depends on FirstName
        Assert.Contains(firstNameProperty, fullNameProperty.GetRequiredProperties().ToArray());

        // Act - Standalone detach (not inside a WriteProperty call)
        person.DetachSubjectProperty(firstNameProperty);

        // Assert - FirstName should be immediately removed from FullName's RequiredProperties
        Assert.DoesNotContain(firstNameProperty, fullNameProperty.GetRequiredProperties().ToArray());
    }

    [Fact]
    public void WhenCrossSubjectSourceIsReplaced_ThenRequiredPropertiesIsCleanedViaRecalculation()
    {
        // Write-triggered detach defers Case 2 removals. After recalculation (TryReplace),
        // the deferred Remove() calls find nothing and exit cleanly (no allocation).

        // Arrange - Car.AveragePressure depends on each tire's Pressure
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithLifecycle();

        var car = new Car(context);
        var oldTire = car.Tires[0];

        var averagePressureProperty = new PropertyReference(car, nameof(Car.AveragePressure));
        var oldTirePressureProperty = new PropertyReference(oldTire, nameof(Tire.Pressure));

        // AveragePressure depends on old tire's Pressure
        Assert.Contains(oldTirePressureProperty, averagePressureProperty.GetRequiredProperties().ToArray());

        // Act - Replace tires: deferred Case 2 + recalculation + flush
        var newTires = new[] { new Tire(context), car.Tires[1], car.Tires[2], car.Tires[3] };
        car.Tires = newTires;

        // Assert - Old tire's Pressure is no longer in RequiredProperties
        Assert.DoesNotContain(oldTirePressureProperty, averagePressureProperty.GetRequiredProperties().ToArray());

        // New tire's Pressure should now be tracked
        var newTirePressureProperty = new PropertyReference(newTires[0], nameof(Tire.Pressure));
        Assert.Contains(newTirePressureProperty, averagePressureProperty.GetRequiredProperties().ToArray());
    }

    [Fact]
    public void DerivedPropertyDependingOnDerivedProperty_HasFlattenedDependencies()
    {
        // Derived-to-derived dependencies are flattened: FullNameWithPrefix calls FullName's
        // getter, which reads FirstName and LastName through the interceptor chain. The recorder
        // captures FirstName and LastName as direct dependencies of FullNameWithPrefix.
        // FullName itself does not appear as a dependency (it's not an intercepted property).
        // Recalculation still works correctly because both FullName and FullNameWithPrefix
        // are in FirstName.UsedByProperties / LastName.UsedByProperties.

        // Arrange - FullNameWithPrefix depends on FullName (another derived property)
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithLifecycle();

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        var fullNameProperty = new PropertyReference(person, nameof(Person.FullName));

        // Assert - FullName.UsedByProperties is empty because the dependency is flattened
        // (FullNameWithPrefix depends on FirstName/LastName directly, not on FullName)
        Assert.Empty(fullNameProperty.GetUsedByProperties().Items.ToArray());
    }

    [Fact]
    public async Task WhenMultiplePropertiesDetachedConcurrently_ThenCleanupRemainsConsistent()
    {
        // Exercises the CAS retry paths in DetachProperty when multiple threads
        // detach different properties of the same subject simultaneously.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithLifecycle();

        var cars = Enumerable.Range(0, 10).Select(_ => new Car(context)).ToArray();

        // Verify initial state: all AveragePressure properties track tire dependencies
        foreach (var car in cars)
        {
            var averagePressureProperty = new PropertyReference(car, nameof(Car.AveragePressure));
            Assert.True(averagePressureProperty.GetRequiredProperties().Length > 0);
        }

        // Act - Detach AveragePressure from all cars concurrently
        var tasks = cars.Select(car => Task.Run(() =>
        {
            var averagePressureProperty = new PropertyReference(car, nameof(Car.AveragePressure));
            car.DetachSubjectProperty(averagePressureProperty);
        }));

        await Task.WhenAll(tasks);

        // Assert - All tire pressures should have empty UsedByProperties (no dangling refs)
        foreach (var car in cars)
        {
            foreach (var tire in car.Tires)
            {
                var tirePressureProperty = new PropertyReference(tire, nameof(Tire.Pressure));
                Assert.Equal(0, tirePressureProperty.GetUsedByProperties().Count);
            }
        }
    }

    [Fact]
    public void WhenDerivedPropertyIsDetached_ThenItRemovesItselfFromDependenciesUsedByProperties()
    {
        // This is Case 1: When a derived property is detached, it removes itself from
        // all its dependencies' UsedByProperties to break backward references.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithLifecycle();

        var car = new Car(context);
        var firstTire = car.Tires[0];

        var averagePressureProperty = new PropertyReference(car, nameof(Car.AveragePressure));
        var firstTirePressureProperty = new PropertyReference(firstTire, nameof(Tire.Pressure));

        // Verify initial state - AveragePressure is in Tire[0].Pressure.UsedByProperties
        Assert.Contains(averagePressureProperty, firstTirePressureProperty.GetUsedByProperties().Items.ToArray());

        // Also verify RequiredProperties
        Assert.Contains(firstTirePressureProperty, averagePressureProperty.GetRequiredProperties().ToArray());

        // Act - Detach just the AveragePressure property (simulates derived property detachment)
        car.DetachSubjectProperty(averagePressureProperty);

        // Assert - AveragePressure should be removed from tire's UsedByProperties (Case 1)
        Assert.DoesNotContain(averagePressureProperty, firstTirePressureProperty.GetUsedByProperties().Items.ToArray());
    }

    [Fact]
    public async Task WhenDerivedPropertyDetachedDuringConcurrentWrites_ThenNoZombieBacklinks()
    {
        // Exercises Finding 3: concurrent detach + write should not resurrect backlinks.

        const int iterations = 100;

        for (var i = 0; i < iterations; i++)
        {
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking();

            var car = new Car(context);
            var firstTire = car.Tires[0];

            var averagePressureProperty = new PropertyReference(car, nameof(Car.AveragePressure));
            var firstTirePressureProperty = new PropertyReference(firstTire, nameof(Tire.Pressure));

            // Verify initial backlink exists
            Assert.Contains(averagePressureProperty, firstTirePressureProperty.GetUsedByProperties().Items.ToArray());

            var barrier = new Barrier(2);

            var detachTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                car.DetachSubjectProperty(averagePressureProperty);
            });

            var writeTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                firstTire.Pressure = i + 1;
            });

            await Task.WhenAll(detachTask, writeTask);

            // After detach completes, AveragePressure must NOT be in any tire's UsedByProperties
            Assert.DoesNotContain(averagePressureProperty, firstTirePressureProperty.GetUsedByProperties().Items.ToArray());
        }
    }

    [Fact]
    public void WhenDerivedPropertyDetached_ThenSubsequentSourceWriteDoesNotResurrectBacklinks()
    {
        // Deterministic version: detach first, then write.
        // Without IsAttached check, the write would trigger recalculation
        // of the detached property (if snapshot was taken before detach).

        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithLifecycle();

        var car = new Car(context);
        var firstTire = car.Tires[0];

        var averagePressureProperty = new PropertyReference(car, nameof(Car.AveragePressure));
        var firstTirePressureProperty = new PropertyReference(firstTire, nameof(Tire.Pressure));

        // Detach the derived property
        car.DetachSubjectProperty(averagePressureProperty);

        // Write to a former dependency
        firstTire.Pressure = 42;

        // Verify no zombie backlinks were created
        Assert.DoesNotContain(averagePressureProperty, firstTirePressureProperty.GetUsedByProperties().Items.ToArray());
    }
}
