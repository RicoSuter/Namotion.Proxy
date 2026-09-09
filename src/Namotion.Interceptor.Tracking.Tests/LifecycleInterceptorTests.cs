using System.Collections.Immutable;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class LifecycleInterceptorTests
{
    [Fact]
    public Task WhenAssigningArray_ThenAllSubjectsAreAttached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler);

        // Act
        var mother = new Person(context) { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        mother.Children = [child1, child2];
        mother.Children = [child1]; // should only detach child2

        // Assert
        return Verify(handler.GetEvents());
    }

    [Fact]
    public Task WhenAddingInterceptorCollection_ThenArrayItemsAndParentAreAttached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler);

        // Act
        var mother = new Person { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        mother.Children = [child1, child2];

        ((IInterceptorSubject)mother).Context.AddFallbackContext(context);

        // Assert
        return Verify(handler.GetEvents());
    }

    [Fact]
    public void WhenAssigningSubject_ThenAllSubjectsAreAttached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        // Act
        var mother1 = new Person(context) { FirstName = "Mother1" };
        var mother2 = new Person { FirstName = "Mother2" };
        var mother3 = new Person { FirstName = "Mother3" };

        // Act & Assert
        mother1.Mother = mother2;
        Assert.Equal(2, handler.GetEvents().Count(e => e.Contains("attached")));

        mother2.Mother = mother3;
        Assert.Equal(3, handler.GetEvents().Count(e => e.Contains("attached")));

        mother1.Mother = null;
        Assert.Equal(2, handler.GetEvents().Count(e => e.Contains("detached")));
    }

    [Fact]
    public Task WhenAddingInterceptorCollection_ThenAllChildrenAreAlsoAttached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        // Act
        var mother1 = new Person { FirstName = "Mother1" };
        var mother2 = new Person { FirstName = "Mother2" };
        var mother3 = new Person { FirstName = "Mother3" };

        mother1.Mother = mother2;
        mother2.Mother = mother3;

        ((IInterceptorSubject)mother1).Context.AddFallbackContext(context);

        // Assert
        return Verify(handler.GetEvents());
    }

    [Fact]
    public Task WhenRemovingInterceptors_ThenAllArrayChildrenAreDetached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler);

        // Act
        var mother = new Person(context) { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        mother.Children = [child1, child2];
        ((IInterceptorSubject)mother).Context.RemoveFallbackContext(context);

        // Assert
        return Verify(handler.GetEvents());
    }

    [Fact]
    public Task WhenRemovingInterceptors_ThenAllChildrenAreDetached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        // Act
        var mother1 = new Person(context) { FirstName = "Mother1" };
        var mother2 = new Person { FirstName = "Mother2" };
        var mother3 = new Person { FirstName = "Mother3" };

        mother1.Mother = mother2;
        mother2.Mother = mother3;
        ((IInterceptorSubject)mother1).Context.RemoveFallbackContext(context);

        // Assert
        return Verify(handler.GetEvents());
    }
    
    [Fact]
    public void WhenChangingProperty_ThenSubjectAttachAndDetachAreCalled()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        // Act & Assert
        var car = new Car(context)
        {
            Name = "Test"
        };
        
        Assert.Single(car.Attachements);
        Assert.Empty(car.Detachements);

        var subject = (IInterceptorSubject)car;
        subject.Context.RemoveFallbackContext(context);
        Assert.Single(car.Attachements);
        Assert.Single(car.Detachements);
    }
    
    [Fact]
    public Task WhenSubjectIsAttachedThenAllPropertiesAreAttachedAndSameWithDetach()
    {
        // Arrange
        var handler = new TestLifecycleHandler(trackProperties: true);
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => handler, _ => false);

        // Act
        var person = new Person
        {
            FirstName = "John",
            LastName = "Doe"
        };

        ((IInterceptorSubject)person).Context.AddFallbackContext(context);

        var father = new Person
        {
            FirstName = "Robert",
            LastName = "Smith"
        };

        person.Father = father;
        father
            .TryGetRegisteredSubject()!
            .AddProperty("FooBar", typeof(string), _ => "MyValue", null);

        person.Father = null;

        // Assert
        return Verify(handler.GetEvents());
    }

    [Fact]
    public Task WhenAddingPropertyInLifecycleHandlerAttach_ThenItIsAttachedOnlyOnce()
    {
        // When a ILifecycleHandler adds a property in AttachSubjectToContext, the property should be attached only once

        // Arrange
        var handler = new TestLifecycleHandler(trackProperties: true);
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => handler, _ => false);

        context.AddService(new AddPropertyToSubjectHandler());

        // Act
        var person = new Person(context);

        // Assert
        Assert.Single(handler.GetEvents(), e => e == "prop+ NA.FooBar"); // should attach FooBar only once
        return Verify(handler.GetEvents());
    }

    [Fact]
    public void WhenAssigningReadOnlyList_ThenSubjectsAreAttached()
    {
        // Arrange: property typed IReadOnlyList<Car>
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        var garage = new Garage(context) { Name = "TestGarage" };
        var car1 = new Car { Name = "Car1" };
        var car2 = new Car { Name = "Car2" };

        // Act
        garage.Cars = [car1, car2];

        // Assert: both cars attached via the IReadOnlyList<Car> property
        var events = handler.GetEvents();
        Assert.Contains(events, e => e.Contains("Cars[0]") && e.Contains("Car1") && e.Contains("attached"));
        Assert.Contains(events, e => e.Contains("Cars[1]") && e.Contains("Car2") && e.Contains("attached"));
    }

    [Fact]
    public void WhenReplacingReadOnlyList_ThenOldSubjectsAreDetachedAndNewAttached()
    {
        // Arrange: property typed IReadOnlyList<Car>
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        var garage = new Garage(context) { Name = "TestGarage" };
        var car1 = new Car { Name = "Car1" };
        var car2 = new Car { Name = "Car2" };
        var car3 = new Car { Name = "Car3" };

        garage.Cars = [car1, car2];
        handler.Clear();

        // Act
        garage.Cars = [car2, car3];

        // Assert
        var events = handler.GetEvents();
        Assert.Contains(events, e => e.Contains("Car1") && e.Contains("detached"));
        Assert.Contains(events, e => e.Contains("Car3") && e.Contains("attached"));
    }

    [Fact]
    public void WhenAssigningReadOnlyDictionary_ThenSubjectsAreAttached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        var garage = new Garage(context) { Name = "TestGarage" };
        var car1 = new Car { Name = "Car1" };
        var car2 = new Car { Name = "Car2" };

        // Act: Dictionary<string, Car> is assigned to IReadOnlyDictionary<string, Car> property.
        // At runtime the value implements IDictionary, so the fast path handles it.
        garage.CarsByName = new Dictionary<string, Car>
        {
            ["first"] = car1,
            ["second"] = car2
        };

        // Assert
        var events = handler.GetEvents();
        Assert.Contains(events, e => e.Contains("CarsByName") && e.Contains("Car1") && e.Contains("attached"));
        Assert.Contains(events, e => e.Contains("CarsByName") && e.Contains("Car2") && e.Contains("attached"));
    }

    [Fact]
    public void WhenReplacingReadOnlyDictionary_ThenOldSubjectsAreDetachedAndNewAttached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        var garage = new Garage(context) { Name = "TestGarage" };
        var car1 = new Car { Name = "Car1" };
        var car2 = new Car { Name = "Car2" };
        var car3 = new Car { Name = "Car3" };

        garage.CarsByName = new Dictionary<string, Car>
        {
            ["first"] = car1,
            ["second"] = car2
        };
        handler.Clear();

        // Act
        garage.CarsByName = new Dictionary<string, Car>
        {
            ["second"] = car2,
            ["third"] = car3
        };

        // Assert
        var events = handler.GetEvents();
        Assert.Contains(events, e => e.Contains("Car1") && e.Contains("detached"));
        Assert.Contains(events, e => e.Contains("Car3") && e.Contains("attached"));
    }

    [Fact]
    public void WhenAssigningImmutableArray_ThenSubjectsAreAttached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        var garage = new Garage(context) { Name = "TestGarage" };
        var tire1 = new Tire { Pressure = 2.0m };
        var tire2 = new Tire { Pressure = 2.5m };

        // Act
        garage.SpareTires = [tire1, tire2];

        // Assert
        var events = handler.GetEvents();
        Assert.Equal(2, events.Count(e => e.Contains("SpareTires") && e.Contains("attached")));
    }

    [Fact]
    public void WhenReplacingImmutableArray_ThenOldSubjectsAreDetachedAndNewAttached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        var garage = new Garage(context) { Name = "TestGarage" };
        var tire1 = new Tire { Pressure = 2.0m };
        var tire2 = new Tire { Pressure = 2.5m };
        var tire3 = new Tire { Pressure = 3.0m };

        garage.SpareTires = [tire1, tire2];
        handler.Clear();

        // Act
        garage.SpareTires = [tire2, tire3];

        // Assert
        var events = handler.GetEvents();
        Assert.Single(events, e => e.Contains("SpareTires") && e.Contains("detached"));
        Assert.Single(events, e => e.Contains("SpareTires") && e.Contains("attached"));
    }

    [Fact]
    public void WhenAssigningTrueReadOnlyDictionary_ThenSubjectsAreAttached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        var garage = new Garage(context) { Name = "TestGarage" };
        var car1 = new Car { Name = "Car1" };
        var car2 = new Car { Name = "Car2" };

        // Act: assign a wrapper that only implements IReadOnlyDictionary (not IDictionary),
        // exercising the IEnumerable + KVP reflection fallback path.
        garage.CarsByName = new ReadOnlyDictionaryWrapper<string, Car>(new Dictionary<string, Car>
        {
            ["first"] = car1,
            ["second"] = car2
        });

        // Assert
        var events = handler.GetEvents();
        Assert.Contains(events, e => e.Contains("CarsByName") && e.Contains("Car1") && e.Contains("attached"));
        Assert.Contains(events, e => e.Contains("CarsByName") && e.Contains("Car2") && e.Contains("attached"));
    }

    [Fact]
    public void WhenReplacingTrueReadOnlyDictionary_ThenOldSubjectsAreDetachedAndNewAttached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        var garage = new Garage(context) { Name = "TestGarage" };
        var car1 = new Car { Name = "Car1" };
        var car2 = new Car { Name = "Car2" };
        var car3 = new Car { Name = "Car3" };

        garage.CarsByName = new ReadOnlyDictionaryWrapper<string, Car>(new Dictionary<string, Car>
        {
            ["first"] = car1,
            ["second"] = car2
        });
        handler.Clear();

        // Act: replace with a new wrapper; car1 should detach, car3 should attach.
        garage.CarsByName = new ReadOnlyDictionaryWrapper<string, Car>(new Dictionary<string, Car>
        {
            ["second"] = car2,
            ["third"] = car3
        });

        // Assert
        var events = handler.GetEvents();
        Assert.Contains(events, e => e.Contains("Car1") && e.Contains("detached"));
        Assert.Contains(events, e => e.Contains("Car3") && e.Contains("attached"));
    }

    [Fact]
    public void WhenAssigningNullableImmutableArray_ThenSubjectsAreAttached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        var holder = new NullableStructuralHolder(context);
        var car1 = new Car { Name = "Car1" };
        var car2 = new Car { Name = "Car2" };

        // Act
        holder.ImmutableCars = ImmutableArray.Create(car1, car2);

        // Assert
        var events = handler.GetEvents();
        Assert.Contains(events, e => e.Contains("ImmutableCars[0] -> Car1") && e.Contains("attached"));
        Assert.Contains(events, e => e.Contains("ImmutableCars[1] -> Car2") && e.Contains("attached"));
    }

    [Fact]
    public void WhenAssigningNullableReadOnlyStructCollection_ThenSubjectsAreAttached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        var holder = new NullableStructuralHolder(context);
        var car = new Car { Name = "Car1" };

        // Act
        holder.BagCars = new CarBag(car);

        // Assert
        var events = handler.GetEvents();
        Assert.Contains(events, e => e.Contains("BagCars[0] -> Car1") && e.Contains("attached"));
    }

    [Fact]
    public void WhenAssigningNullableReadOnlyStructDictionary_ThenSubjectsAreAttachedUnderTheirKey()
    {
        // Arrange: the boxed value is only an IEnumerable of key value pairs, so the keyed dispatch
        // arm has to come from the declared Nullable<CarMap> property type.
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        var holder = new NullableStructuralHolder(context);
        var car = new Car { Name = "Car1" };

        // Act
        holder.MappedCars = new CarMap(new Dictionary<string, Car> { ["first"] = car });

        // Assert
        var events = handler.GetEvents();
        Assert.Contains(events, e => e.Contains("MappedCars[first] -> Car1") && e.Contains("attached"));
    }

    [Fact]
    public void WhenClearingNullableStructuralProperty_ThenSubjectsAreDetached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        var holder = new NullableStructuralHolder(context);
        var car = new Car { Name = "Car1" };
        holder.ImmutableCars = ImmutableArray.Create(car);
        handler.Clear();

        // Act
        holder.ImmutableCars = null;

        // Assert
        var events = handler.GetEvents();
        Assert.Contains(events, e => e.Contains("ImmutableCars[0] -> Car1") && e.Contains("detached"));
    }

    public class AddPropertyToSubjectHandler : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach)
            {
                change.Subject.TryGetRegisteredSubject()!.AddProperty("FooBar", typeof(string), _ => "MyValue", null);
            }
        }
    }
}
