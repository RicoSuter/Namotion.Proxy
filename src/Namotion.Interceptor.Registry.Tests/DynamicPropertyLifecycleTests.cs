using System.Reactive.Linq;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Tests that dynamic properties added via AddProperty/AddDerivedProperty
/// are correctly tracked by the lifecycle interceptor and registry.
/// </summary>
public class DynamicPropertyLifecycleTests
{
    [Fact]
    public void WhenWritingToDynamicDerivedPropertyWithSetter_ThenPropertyIsRecalculated()
    {
        // Arrange: Dynamic derived property with a setter.
        // The getter computes "FirstName (override)" or "FirstName" based on internal state.
        // The setter modifies internal state, then the handler should recalculate via the getter.
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "John" };
        var registeredRoot = root.TryGetRegisteredSubject()!;

        string? overrideValue = null;

        var property = registeredRoot.AddDerivedProperty<string>(
            "DisplayName",
            getValue: _ => overrideValue ?? root.FirstName ?? "NA",
            setValue: (_, value) => overrideValue = value);

        context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == "DisplayName")
            .Subscribe(changes.Add);

        // Verify initial value
        var propertyReference = property.Reference;
        var initialValue = propertyReference.Metadata.GetValue?.Invoke(root);
        Assert.Equal("John", initialValue);

        // Act - Write to the derived-with-setter property via the interceptor
        propertyReference.Metadata.SetValue?.Invoke(root, "Custom");

        // Assert - The override was applied
        Assert.Equal("Custom", overrideValue);

        // The getter should now return the override value
        var newValue = propertyReference.Metadata.GetValue?.Invoke(root);
        Assert.Equal("Custom", newValue);

        // The observable should have fired with the recalculated value
        Assert.NotEmpty(changes);
        Assert.Contains(changes, c =>
            c.GetNewValue<string?>() == "Custom");
    }

    [Fact]
    public void WhenSourceChanges_ThenDynamicDerivedPropertyIsRecalculated()
    {
        // Arrange: Dynamic derived property depending on FirstName.
        // When FirstName changes, the derived property should recalculate.
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "John" };
        var registeredRoot = root.TryGetRegisteredSubject()!;

        registeredRoot.AddDerivedProperty<string>(
            "Greeting",
            getValue: _ => $"Hello, {root.FirstName}!",
            setValue: (_, _) => { });

        context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == "Greeting")
            .Subscribe(changes.Add);

        // Act - Change the source property
        root.FirstName = "Jane";

        // Assert - Greeting should have been recalculated
        Assert.NotEmpty(changes);
        Assert.Contains(changes, c =>
            c.GetNewValue<string?>() == "Hello, Jane!");
    }

    [Fact]
    public void WhenAddPropertyIsRepeatedWithTheSameShape_ThenTheFirstRegistrationStaysAuthoritative()
    {
        // Arrange: initializers rerun AddProperty on every attach with fresh delegates, so a
        // same-shaped re-registration is idempotent and the original accessors stay authoritative.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var registeredRoot = root.TryGetRegisteredSubject()!;

        // Act
        var first = registeredRoot.AddProperty("Dyn", typeof(string), _ => "first", null);
        var second = registeredRoot.AddProperty("Dyn", typeof(string), _ => "second", null);

        // Assert
        Assert.Same(first, second);
        Assert.Equal("first", first.Reference.Metadata.GetValue?.Invoke(root));
    }

    [Fact]
    public void WhenAddPropertyIsRepeatedWithADifferentShape_ThenItThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var registeredRoot = root.TryGetRegisteredSubject()!;
        registeredRoot.AddProperty("Dyn", typeof(string), _ => "first", null);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => registeredRoot.AddProperty("Dyn", typeof(int), _ => 2, null));
    }

    [Fact]
    public void WhenASubjectWithADynamicPropertyIsReattached_ThenAddPropertyRerunsWithoutError()
    {
        // Arrange: the subject keeps its dynamic metadata across detach while Registry's
        // projection is rebuilt per attach, and initializers rerun their AddProperty on every
        // attach, so the rerun must succeed rather than reject the surviving name.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var father = new Person { FirstName = "F" };
        root.Father = father;
        father.TryGetRegisteredSubject()!.AddProperty("FooBar", typeof(string), _ => "one", null);

        // Act: detach releases the Registry projection; reattach rebuilds it from the surviving
        // metadata, and the initializer-style rerun re-registers the same shape.
        root.Father = null;
        Assert.Null(father.TryGetContext());
        root.Father = father;
        var property = father.TryGetRegisteredSubject()!.AddProperty("FooBar", typeof(string), _ => "two", null);

        // Assert
        Assert.NotNull(father.TryGetRegisteredSubject()!.TryGetProperty("FooBar"));
        Assert.Equal("one", property.Reference.Metadata.GetValue?.Invoke(father));
    }

    [Fact]
    public void WhenAScalarDynamicPropertyIsReregisteredAfterReattach_ThenItsInitialValueIsPublishedAgain()
    {
        // Arrange: an initializer reruns its AddProperty on every attach, and a reattached subject
        // presents its dynamic properties to the graph afresh.
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var father = new Person { FirstName = "F" };
        root.Father = father;
        father.TryGetRegisteredSubject()!.AddProperty("Nickname", typeof(string), _ => "Fred", null);

        root.Father = null;
        root.Father = father;

        context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Where(change => change.Property.Name == "Nickname")
            .Subscribe(changes.Add);

        // Act
        father.TryGetRegisteredSubject()!.AddProperty("Nickname", typeof(string), _ => "Fred", null);

        // Assert
        var change = Assert.Single(changes);
        Assert.Null(change.GetOldValue<string?>());
        Assert.Equal("Fred", change.GetNewValue<string?>());
    }

    [Fact]
    public void WhenAStoredDynamicSubjectPropertyIsAdded_ThenTheProjectionExistsBeforeItsInitialEdgePublishes()
    {
        // Arrange: the registry resolves an edge notification through the parent's registered
        // property, and throws when it is missing. A stored dynamic property with an initial
        // subject value therefore proves by succeeding that admission created the projection
        // before it published the initial structural edge.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var registeredRoot = root.TryGetRegisteredSubject()!;
        var ward = new Person { FirstName = "Ward" };
        var stored = ward;

        // Act
        registeredRoot.AddProperty(
            "Ward",
            typeof(Person),
            getValue: _ => stored,
            setValue: (_, value) => stored = (Person)value!);

        // Assert: the edge resolved through the new projection and is projected on both sides.
        var wardRegistered = ward.TryGetRegisteredSubject();
        Assert.NotNull(wardRegistered);
        var parent = Assert.Single(wardRegistered.Parents);
        Assert.Equal("Ward", parent.Property.Name);
        Assert.Null(parent.Index);

        var wardProperty = registeredRoot.TryGetProperty("Ward")!;
        var child = Assert.Single(wardProperty.Children);
        Assert.Same(ward, child.Subject);

        Assert.Equal(1, ward.GetReferenceCount());
        Assert.Same(context, ward.TryGetContext());
    }

    [Fact]
    public void WhenDynamicDerivedPropertyReturnsSubject_ThenItEstablishesNoOwnershipEdge()
    {
        // Arrange: a dynamic derived property that returns a subject reference (a computed "first
        // child"). [Derived] declares a cache rather than the store of record, so it must not add
        // another parent edge: only Children carries ownership.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        var root = new Person(context)
        {
            FirstName = "Root",
            Children = [child1, child2]
        };

        var registry = context.GetService<ISubjectRegistry>();
        var registeredRoot = root.TryGetRegisteredSubject()!;

        // Act: Add a dynamic derived property that returns the first child
        var derivedProperty = registeredRoot.AddDerivedProperty<Person>(
            "FirstChild",
            _ => root.Children.Length > 0 ? root.Children[0] : null);

        // Assert: the derived property adds no edge, so each child keeps its single Children edge,
        // and the registry projects no parent entry for the derived property either.
        Assert.Equal(1, child1.GetReferenceCount());
        Assert.Equal(1, child2.GetReferenceCount());
        Assert.NotNull(registeredRoot.TryGetProperty("FirstChild"));
        var child1Parent = Assert.Single(child1.TryGetRegisteredSubject()!.Parents);
        Assert.Equal(nameof(Person.Children), child1Parent.Property.Name);
        Assert.DoesNotContain(child1.TryGetRegisteredSubject()!.Parents, parent => parent.Property.Name == "FirstChild");

        // All subjects tracked through their real edges: root + child1 + child2
        Assert.Equal(3, registry.KnownSubjects.Count);

        // Act: Change Children so that child2 is first; the derived value follows the stored edges
        root.Children = [child2];

        // Assert: child1 fully detached; the derived property alone cannot keep it alive
        Assert.Equal(0, child1.GetReferenceCount());
        Assert.Equal(1, child2.GetReferenceCount());

        // root + child2
        Assert.Equal(2, registry.KnownSubjects.Count);

        // Act: Set Children to empty; derived property returns null
        root.Children = [];

        // Assert: child2 fully detached
        Assert.Equal(0, child2.GetReferenceCount());

        // Only root remains
        Assert.Single(registry.KnownSubjects);
    }

    [Fact]
    public void WhenAScalarDynamicPropertyIsAdded_ThenItsInitialValueIsPublishedAsAChangeFromNull()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "John" };
        var registeredRoot = root.TryGetRegisteredSubject()!;

        context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Where(change => change.Property.Name == "Nickname")
            .Subscribe(changes.Add);

        var nickname = "Johnny";

        // Act
        registeredRoot.AddProperty<string>(
            "Nickname",
            getValue: _ => nickname,
            setValue: (_, value) => nickname = value!);

        // Assert: adding the property is the transition from "did not exist" to the initial value.
        var change = Assert.Single(changes);
        Assert.Null(change.GetOldValue<string?>());
        Assert.Equal("Johnny", change.GetNewValue<string?>());
    }

    [Fact]
    public void WhenDynamicDerivedPropertyWithASetterStoresASubject_ThenItEstablishesAnOwnershipEdge()
    {
        // Arrange: a dynamic derived property whose accessors read and write private state of
        // their own. Unlike the getter-only shape above it can hold a subject no other property
        // reaches, so it is the store of record and has to carry the edge.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var registry = context.GetService<ISubjectRegistry>();
        var registeredRoot = root.TryGetRegisteredSubject()!;

        Person? store = null;
        var derivedProperty = registeredRoot.AddDerivedProperty<Person>(
            "Current",
            _ => store,
            (_, value) => store = value);

        var child = new Person { FirstName = "Child" };

        // Act
        derivedProperty.SetValue(child);

        // Assert
        Assert.Equal(1, child.GetReferenceCount());
        Assert.NotNull(child.TryGetRegisteredSubject());
        var parent = Assert.Single(child.TryGetRegisteredSubject()!.Parents);
        Assert.Equal("Current", parent.Property.Name);
        Assert.Equal(2, registry.KnownSubjects.Count);
    }

    [Fact]
    public void WhenDynamicDerivedPropertyWithASetterClearsItsSubject_ThenTheSubjectIsReleased()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var registry = context.GetService<ISubjectRegistry>();
        var registeredRoot = root.TryGetRegisteredSubject()!;

        Person? store = null;
        var derivedProperty = registeredRoot.AddDerivedProperty<Person>(
            "Current",
            _ => store,
            (_, value) => store = value);

        var child = new Person { FirstName = "Child" };
        derivedProperty.SetValue(child);

        // Act
        derivedProperty.SetValue(null);

        // Assert
        Assert.Equal(0, child.GetReferenceCount());
        Assert.Null(child.TryGetRegisteredSubject());
        Assert.Single(registry.KnownSubjects);
    }
}
