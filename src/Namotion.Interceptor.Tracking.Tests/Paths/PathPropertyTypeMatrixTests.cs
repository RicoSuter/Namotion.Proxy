using System.Linq.Expressions;
using Moq;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Paths;
using Namotion.Interceptor.Tracking.Tests.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

// The property-type matrix: one test per leaf/collection/dictionary shape the tracker can subscribe to,
// each covering BOTH the initial walk (Current/resolution) AND a retrack (a structural change that moves
// the observed subject/value and asserts the transition delivers). No production change is expected; this
// pins the emergent behavior across property shapes.
[Collection(PerPropertySubscriptionCollection.Name)]
public class PathPropertyTypeMatrixTests
{
    public PathPropertyTypeMatrixTests() => PropertyChangeSubscriptions.ResetForTests();

    // --- Scalar leaves --------------------------------------------------------------------------------

    [Fact]
    public void WhenIntLeaf_ThenResolvesAndRetrackDelivers()
    {
        // Arrange
        var context = FullTracking();
        var holder = new ScalarHolder(context) { Leaf = new ScalarLeaf { Count = 7 } };
        using var subscription = Watch(holder, x => x.Leaf!.Count, out var events);

        // Act & Assert: walk resolves the int leaf.
        Assert.True(subscription.Current.IsResolved);
        Assert.Equal(7, subscription.Current.GetValueOrDefault());

        // Act: retrack by reassigning the intermediate so the observed value moves.
        holder.Leaf = new ScalarLeaf { Count = 42 };

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal(7, change.OldState.GetValueOrDefault());
        Assert.Equal(42, change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenDoubleLeaf_ThenResolvesAndRetrackDelivers()
    {
        // Arrange
        var context = FullTracking();
        var holder = new ScalarHolder(context) { Leaf = new ScalarLeaf { Ratio = 1.5 } };
        using var subscription = Watch(holder, x => x.Leaf!.Ratio, out var events);

        // Act & Assert
        Assert.True(subscription.Current.IsResolved);
        Assert.Equal(1.5, subscription.Current.GetValueOrDefault());

        // Act
        holder.Leaf = new ScalarLeaf { Ratio = 3.5 };

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal(1.5, change.OldState.GetValueOrDefault());
        Assert.Equal(3.5, change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenStringLeaf_ThenResolvesAndRetrackDelivers()
    {
        // Arrange
        var context = FullTracking();
        var root = new Node(context) { Child = new Node { Name = "A" } };
        using var subscription = Watch(root, x => x.Child!.Name, out var events);

        // Act & Assert
        Assert.True(subscription.Current.IsResolved);
        Assert.Equal("A", subscription.Current.GetValueOrDefault());

        // Act
        root.Child = new Node { Name = "B" };

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal("A", change.OldState.GetValueOrDefault());
        Assert.Equal("B", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenNullableIntLeaf_ThenResolvedNullIsDistinctFromUnresolved()
    {
        // Arrange
        var context = FullTracking();
        var holder = new ScalarHolder(context) { Leaf = new ScalarLeaf { OptionalCount = 5 } };
        using var subscription = Watch(holder, x => x.Leaf!.OptionalCount, out var events);

        // Act & Assert: walk resolves the nullable value type with a value.
        Assert.True(subscription.Current.IsResolved);
        Assert.Equal(5, subscription.Current.GetValueOrDefault());

        // Act: retrack to a leaf whose nullable value is null -> resolved with a null value.
        holder.Leaf = new ScalarLeaf(); // OptionalCount left null

        // Assert: the null is a RESOLVED value, not the unresolved state.
        var toNull = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal(5, toNull.OldState.GetValueOrDefault());
        Assert.True(toNull.NewState.IsResolved);
        Assert.Null(toNull.NewState.GetValueOrDefault());

        // Act: retrack again to unresolved (intermediate nulled) -> distinct transition delivers.
        holder.Leaf = null;

        // Assert
        Assert.Equal(2, events.Count);
        Assert.True(events[1].OldState.IsResolved);
        Assert.Null(events[1].OldState.GetValueOrDefault());
        Assert.False(events[1].NewState.IsResolved);
    }

    [Fact]
    public void WhenNullableStringLeaf_ThenResolvedNullIsDistinctFromUnresolved()
    {
        // Arrange
        var context = FullTracking();
        var person = new Person(context); // Father null -> the path is unresolved
        using var subscription = Watch(person, x => x.Father!.FirstName, out var events);

        // Act & Assert: the walk is unresolved (a missing intermediate, not a null value).
        Assert.False(subscription.Current.IsResolved);

        // Act: heal to a father whose FirstName is null -> RESOLVED with a null value.
        person.Father = new Person(); // FirstName null

        // Assert: resolved-null is a distinct state from unresolved.
        var heal = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.False(heal.OldState.IsResolved);
        Assert.True(heal.NewState.IsResolved);
        Assert.Null(heal.NewState.GetValueOrDefault());
        Assert.True(subscription.Current.IsResolved);
        Assert.Null(subscription.Current.GetValueOrDefault());

        // Act: break back to unresolved -> the resolved-null -> unresolved transition delivers.
        person.Father = null;

        // Assert
        Assert.Equal(2, events.Count);
        Assert.True(events[1].OldState.IsResolved);
        Assert.Null(events[1].OldState.GetValueOrDefault());
        Assert.False(events[1].NewState.IsResolved);
    }

    // --- Subject-reference leaf (TValue is itself a subject type) --------------------------------------

    [Fact]
    public void WhenSubjectReferenceLeaf_ThenReferenceEqualityDecidesSuppression()
    {
        // Arrange: x.Child.Child watches a subject reference as the leaf value (TValue = Node?).
        var context = FullTracking();
        var grandA = new Node { Name = "GA" };
        var grandB = new Node { Name = "GB" };
        var root = new Node(context) { Child = new Node { Name = "M1", Child = grandA } };
        using var subscription = Watch(root, x => x.Child!.Child, out var events);

        // Act & Assert: the leaf resolves to the grandchild instance.
        Assert.True(subscription.Current.IsResolved);
        Assert.Same(grandA, subscription.Current.GetValueOrDefault());

        // Act: retrack via a fresh intermediate holding a DIFFERENT grandchild instance.
        root.Child = new Node { Name = "M2", Child = grandB };

        // Assert: a different reference delivers (no Equals override, so EqualityComparer is reference equality).
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Same(grandA, change.OldState.GetValueOrDefault());
        Assert.Same(grandB, change.NewState.GetValueOrDefault());

        // Act: retrack via a fresh intermediate holding the SAME grandchild instance.
        root.Child = new Node { Name = "M3", Child = grandB };

        // Assert: re-observing the same reference is suppressed at the path level.
        Assert.Single(events);
        Assert.Same(grandB, subscription.Current.GetValueOrDefault());
    }

    // --- Subject reference intermediate (including a nullable subject property) ------------------------

    [Fact]
    public void WhenNullableSubjectReferenceIntermediate_ThenResolvesRetracksAndBreaks()
    {
        // Arrange
        var context = FullTracking();
        var person = new Person(context) { Father = new Person { FirstName = "Joe" } };
        using var subscription = Watch(person, x => x.Father!.FirstName, out var events);

        // Act & Assert: the nullable subject intermediate resolves.
        Assert.Equal("Joe", subscription.Current.GetValueOrDefault());

        // Act: reassign the intermediate to a different subject.
        person.Father = new Person { FirstName = "Jack" };

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal("Joe", change.OldState.GetValueOrDefault());
        Assert.Equal("Jack", change.NewState.GetValueOrDefault());

        // Act: null the intermediate -> break to unresolved.
        person.Father = null;

        // Assert
        Assert.Equal(2, events.Count);
        Assert.True(events[1].OldState.IsResolved);
        Assert.False(events[1].NewState.IsResolved);
    }

    // --- Subject collection intermediates -------------------------------------------------------------

    [Fact]
    public void WhenArrayCollectionIntermediate_ThenResolvesAndRetrackDelivers()
    {
        // Arrange
        var context = FullTracking();
        var root = new Node(context) { Children = [new Node { Name = "A" }] };
        using var subscription = Watch(root, x => x.Children[0].Name, out var events);

        // Act & Assert
        Assert.Equal("A", subscription.Current.GetValueOrDefault());

        // Act
        root.Children = [new Node { Name = "B" }];

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal("A", change.OldState.GetValueOrDefault());
        Assert.Equal("B", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenListCollectionIntermediate_ThenResolvesAndRetrackDelivers()
    {
        // Arrange
        var context = FullTracking();
        var holder = new ListHolder(context) { ListItems = new List<Node> { new() { Name = "A" } } };
        using var subscription = Watch(holder, x => x.ListItems[0].Name, out var events);

        // Act & Assert
        Assert.Equal("A", subscription.Current.GetValueOrDefault());

        // Act
        holder.ListItems = new List<Node> { new() { Name = "B" } };

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal("A", change.OldState.GetValueOrDefault());
        Assert.Equal("B", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenInterfaceListCollectionIntermediate_ThenResolvesAndRetrackDelivers()
    {
        // Arrange
        var context = FullTracking();
        var holder = new ListHolder(context) { InterfaceListItems = new List<Node> { new() { Name = "A" } } };
        using var subscription = Watch(holder, x => x.InterfaceListItems[0].Name, out var events);

        // Act & Assert
        Assert.Equal("A", subscription.Current.GetValueOrDefault());

        // Act
        holder.InterfaceListItems = new List<Node> { new() { Name = "B" } };

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal("A", change.OldState.GetValueOrDefault());
        Assert.Equal("B", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenIListImplementationIsGenericOnly_ThenGenericIndexerIsUsed()
    {
        // Arrange: a proxy implementing IList<T>, but not non-generic IList. Clear the lifecycle
        // enumeration calls so only traversal performed while subscribing is measured.
        var context = FullTracking();
        var item = new Node { Name = "A" };
        var items = new Mock<IList<Node>>();
        items.SetupGet(list => list.Count).Returns(1);
        items.SetupGet(list => list[0]).Returns(item);
        items.Setup(list => list.GetEnumerator()).Returns(() => new[] { item }.AsEnumerable().GetEnumerator());
        items.As<System.Collections.IEnumerable>()
            .Setup(list => list.GetEnumerator())
            .Returns(() => new[] { item }.GetEnumerator());

        var holder = new ListHolder(context) { InterfaceListItems = items.Object };
        items.Invocations.Clear();

        // Act
        using var subscription = Watch(holder, x => x.InterfaceListItems[0].Name, out _);

        // Assert
        Assert.False(items.Object is System.Collections.IList);
        Assert.Equal("A", subscription.Current.GetValueOrDefault());
        items.VerifyGet(list => list[0], Times.AtLeastOnce);
        items.Verify(list => list.GetEnumerator(), Times.Never);
    }

    [Fact]
    public void WhenReadOnlyListCollectionIntermediate_ThenResolvesAndRetrackDelivers()
    {
        // Arrange
        var context = FullTracking();
        var garage = new Garage(context) { Cars = new[] { new Car { Name = "A" } } };
        using var subscription = Watch(garage, x => x.Cars[0].Name, out var events);

        // Act & Assert
        Assert.Equal("A", subscription.Current.GetValueOrDefault());

        // Act
        garage.Cars = new[] { new Car { Name = "B" } };

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal("A", change.OldState.GetValueOrDefault());
        Assert.Equal("B", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenReadOnlyListImplementationIsGenericOnly_ThenIndexerIsUsedWithoutEnumeration()
    {
        // Arrange: assign while enumeration is available so lifecycle attachment can inspect the
        // collection, then disable enumeration before subscribing. Only direct indexed access remains.
        var context = FullTracking();
        var items = new GenericOnlyReadOnlyList<Node>(new Node { Name = "A" });
        var holder = new GenericOnlyListHolder(context) { Items = items };
        items.ThrowOnEnumeration = true;

        // Act
        using var subscription = Watch(holder, x => x.Items[0].Name, out _);

        // Assert
        Assert.True(subscription.Current.IsResolved);
        Assert.Equal("A", subscription.Current.GetValueOrDefault());
    }

    [Fact]
    public void WhenCollectionExposesNoGenericListInterface_ThenSubscribeIsLenientAndResolves()
    {
        // Arrange: the container offers an indexer for the path expression but implements only
        // IEnumerable<T>, so subscribe-time decomposition finds no generic list interface.
        var context = FullTracking();
        var holder = new IndexableEnumerableHolder(context) { Items = new IndexableEnumerable<Node>(new Node { Name = "A" }) };

        // Act: subscribing must not throw; the walk indexes the boxed collection leniently.
        using var subscription = Watch(holder, x => x.Items[0].Name, out _);

        // Assert
        Assert.True(subscription.Current.IsResolved);
        Assert.Equal("A", subscription.Current.GetValueOrDefault());
    }

    [Fact]
    public void WhenGenericOnlyReadOnlyListIndexerThrows_ThenPathIsUnresolved()
    {
        // Arrange: enumeration remains valid, but the indexed operation represented by the path throws.
        // Traversal must not bypass that failure by enumerating to the requested position.
        var context = FullTracking();
        var items = new GenericOnlyReadOnlyList<Node>(new Node { Name = "A" }) { ThrowOnIndexer = true };
        var holder = new GenericOnlyListHolder(context) { Items = items };

        // Act
        using var subscription = Watch(holder, x => x.Items[0].Name, out _);

        // Assert
        Assert.False(subscription.Current.IsResolved);
    }

    [Fact]
    public void WhenImmutableArrayCollectionIntermediate_ThenResolvesAndRetrackDelivers()
    {
        // Arrange: a value-typed (ImmutableArray) collection with a decimal leaf.
        var context = FullTracking();
        var garage = new Garage(context) { SpareTires = [new Tire { Pressure = 1.0m }] };
        using var subscription = Watch(garage, x => x.SpareTires[0].Pressure, out var events);

        // Act & Assert
        Assert.Equal(1.0m, subscription.Current.GetValueOrDefault());

        // Act
        garage.SpareTires = [new Tire { Pressure = 2.0m }];

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal(1.0m, change.OldState.GetValueOrDefault());
        Assert.Equal(2.0m, change.NewState.GetValueOrDefault());
    }

    // --- Subject dictionary intermediates -------------------------------------------------------------

    [Fact]
    public void WhenDictionaryIntermediate_ThenResolvesAndRetrackDelivers()
    {
        // Arrange
        var context = FullTracking();
        var root = new Node(context) { ByName = new Dictionary<string, Node> { ["k"] = new() { Name = "A" } } };
        using var subscription = Watch(root, x => x.ByName["k"].Name, out var events);

        // Act & Assert
        Assert.Equal("A", subscription.Current.GetValueOrDefault());

        // Act
        root.ByName = new Dictionary<string, Node> { ["k"] = new() { Name = "B" } };

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal("A", change.OldState.GetValueOrDefault());
        Assert.Equal("B", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenInterfaceDictionaryIntermediate_ThenResolvesAndRetrackDelivers()
    {
        // Arrange
        var context = FullTracking();
        var holder = new DictionaryHolder(context) { ByKey = new Dictionary<string, Node> { ["k"] = new() { Name = "A" } } };
        using var subscription = Watch(holder, x => x.ByKey["k"].Name, out var events);

        // Act & Assert
        Assert.Equal("A", subscription.Current.GetValueOrDefault());

        // Act
        holder.ByKey = new Dictionary<string, Node> { ["k"] = new() { Name = "B" } };

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal("A", change.OldState.GetValueOrDefault());
        Assert.Equal("B", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenReadOnlyDictionaryIntermediate_ThenResolvesAndRetrackDelivers()
    {
        // Arrange
        var context = FullTracking();
        var garage = new Garage(context) { CarsByName = new Dictionary<string, Car> { ["k"] = new() { Name = "A" } } };
        using var subscription = Watch(garage, x => x.CarsByName["k"].Name, out var events);

        // Act & Assert
        Assert.Equal("A", subscription.Current.GetValueOrDefault());

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["k"] = new() { Name = "B" } };

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal("A", change.OldState.GetValueOrDefault());
        Assert.Equal("B", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenIntKeyedDictionaryIntermediate_ThenResolvesAndRetrackDelivers()
    {
        // Arrange: a non-string key type.
        var context = FullTracking();
        var holder = new DictionaryHolder(context) { ById = new Dictionary<int, Node> { [1] = new() { Name = "A" } } };
        using var subscription = Watch(holder, x => x.ById[1].Name, out var events);

        // Act & Assert
        Assert.Equal("A", subscription.Current.GetValueOrDefault());

        // Act
        holder.ById = new Dictionary<int, Node> { [1] = new() { Name = "B" } };

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal("A", change.OldState.GetValueOrDefault());
        Assert.Equal("B", change.NewState.GetValueOrDefault());
    }

    // --- Interface-typed intermediate -----------------------------------------------------------------

    [Fact]
    public void WhenInterfaceTypedIntermediate_ThenResolvesAndRetracks()
    {
        // Arrange: the intermediate's static type is an interface; the runtime value is a concrete subject.
        var context = FullTracking();
        var holder = new InterfaceIntermediateHolder(context) { Node = new NamedSubject { Label = "A" } };
        using var subscription = Watch(holder, x => x.Node!.Label, out var events);

        // Act & Assert
        Assert.True(subscription.Current.IsResolved);
        Assert.Equal("A", subscription.Current.GetValueOrDefault());

        // Act
        holder.Node = new NamedSubject { Label = "B" };

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal("A", change.OldState.GetValueOrDefault());
        Assert.Equal("B", change.NewState.GetValueOrDefault());
    }

    // --- Interface default leaves: [Derived] subscribable, plain rejected ------------------------------

    [Fact]
    public void WhenDerivedInterfaceDefaultLeaf_ThenResolvesAndRetracks()
    {
        // Arrange: DerivedLabel is a [Derived] interface-default leaf (IsDerived => passes the walk rule).
        var context = FullTracking();
        var holder = new DefaultsHolder(context) { Target = new DefaultsSubject { Prefix = "A" } };
        using var subscription = Watch(holder, x => x.Target!.DerivedLabel, out var events);

        // Act & Assert
        Assert.True(subscription.Current.IsResolved);
        Assert.Equal("D:A", subscription.Current.GetValueOrDefault());

        // Act: retrack to an instance whose derived value differs.
        holder.Target = new DefaultsSubject { Prefix = "B" };

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal("D:A", change.OldState.GetValueOrDefault());
        Assert.Equal("D:B", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenPlainInterfaceDefaultLeaf_ThenRejectedAsUnresolved()
    {
        // Arrange: PlainLabel is a plain interface default; the generator emits it neither intercepted nor
        // derived, so the walk's segment rule rejects it (it resolves UNRESOLVED, it does not throw).
        var context = FullTracking();
        var holder = new DefaultsHolder(context) { Target = new DefaultsSubject { Prefix = "A" } };
        using var subscription = Watch(holder, x => x.Target!.PlainLabel, out var events);

        // Act & Assert: unresolved despite the intermediate being present and the property existing.
        Assert.False(subscription.Current.IsResolved);

        // Act: a structural retrack of the intermediate never surfaces the plain default.
        holder.Target = new DefaultsSubject { Prefix = "B" };

        // Assert: still unresolved, and the unresolved -> unresolved transition delivers nothing.
        Assert.Empty(events);
        Assert.False(subscription.Current.IsResolved);
    }

    // --- Derived leaf and derived subject-typed intermediate (fires with detection, inert without) -----

    [Fact]
    public void WhenDerivedLeafAndDerivedChangeDetection_ThenRecalcAndRetrackDeliver()
    {
        // Arrange: FullName is a [Derived] leaf that depends on the intercepted FirstName.
        var context = FullTracking();
        var father = new Person { FirstName = "Ann" };
        var person = new Person(context) { Father = father };
        using var subscription = Watch(person, x => x.Father!.FullName, out var events);

        // Act & Assert: walk resolves the derived leaf.
        Assert.True(subscription.Current.IsResolved);
        Assert.Equal("Ann", subscription.Current.GetValueOrDefault());

        // Act: a write to the derived leaf's dependency recomputes it; derived-change detection fires.
        father.FirstName = "Bob";

        // Assert
        var recalc = AssertSingleTransition(events, SubjectPathChangeKind.ValueChange);
        Assert.Equal("Ann", recalc.OldState.GetValueOrDefault());
        Assert.Equal("Bob", recalc.NewState.GetValueOrDefault());

        // Act: a structural retrack of the intermediate also moves the derived leaf value.
        person.Father = new Person { FirstName = "Cody" };

        // Assert
        Assert.Equal(2, events.Count);
        Assert.Equal(SubjectPathChangeKind.PathChange, events[1].Kind);
        Assert.Equal("Bob", events[1].OldState.GetValueOrDefault());
        Assert.Equal("Cody", events[1].NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenDerivedLeafWithoutDerivedChangeDetection_ThenRecalcInert()
    {
        // Arrange: bare subscriptions (no derived-change detection). Father carries its own notifying context
        // so dormancy is not the reason for inertness.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var father = new Person(context) { FirstName = "Ann" };
        var person = new Person(context) { Father = father };
        using var subscription = Watch(person, x => x.Father!.FullName, out var events);

        // Act & Assert: Current is a fresh walk, so it still resolves the derived value.
        Assert.True(subscription.Current.IsResolved);
        Assert.Equal("Ann", subscription.Current.GetValueOrDefault());

        // Act: write the derived leaf's dependency. With no derived-change detection, no recompute is dispatched.
        father.FirstName = "Bob";

        // Assert: inert.
        Assert.Empty(events);
    }

    [Fact]
    public void WhenDerivedSubjectIntermediateAndDerivedChangeDetection_ThenRecalcRetracks()
    {
        // Arrange: Thrower is a [Derived] subject-typed intermediate aliasing the intercepted Backing.
        var context = FullTracking();
        var holder = new GetterThrowHolder(context);
        holder.Backing = new Node { Name = "A" };
        using var subscription = Watch(holder, x => x.Thrower!.Name, out var events);

        // Act & Assert
        Assert.True(subscription.Current.IsResolved);
        Assert.Equal("A", subscription.Current.GetValueOrDefault());

        // Act: reassign Backing -> the derived intermediate recomputes and derived-change detection retracks.
        holder.Backing = new Node { Name = "B" };

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal("A", change.OldState.GetValueOrDefault());
        Assert.Equal("B", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenDerivedSubjectIntermediateWithoutDerivedChangeDetection_ThenRecalcInert()
    {
        // Arrange: bare subscriptions. Children carry their own notifying context to isolate the variable.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var holder = new GetterThrowHolder(context);
        holder.Backing = new Node(context) { Name = "A" };
        using var subscription = Watch(holder, x => x.Thrower!.Name, out var events);

        // Act & Assert
        Assert.True(subscription.Current.IsResolved);
        Assert.Equal("A", subscription.Current.GetValueOrDefault());

        // Act: reassign Backing. Without derived-change detection, the derived Thrower segment never dispatches.
        holder.Backing = new Node(context) { Name = "B" };

        // Assert: inert (the derived intermediate recompute is not detected).
        Assert.Empty(events);
    }

    // --- Deep mixed path (reference + collection + dictionary segments) --------------------------------

    [Fact]
    public void WhenDeepMixedPath_ThenResolvesAndMidPathRetrackDelivers()
    {
        // Arrange: x.Child.Children[1].ByName["k"].Name combines a reference, a collection index, a
        // dictionary key and a leaf.
        var context = FullTracking();
        var element = new Node
        {
            Name = "n1",
            ByName = new Dictionary<string, Node> { ["k"] = new() { Name = "L" } }
        };
        var mid = new Node { Name = "mid", Children = [new Node { Name = "c0" }, element] };
        var root = new Node(context) { Name = "root", Child = mid };
        using var subscription = Watch(root, x => x.Child!.Children[1].ByName["k"].Name, out var events);

        // Act & Assert
        Assert.True(subscription.Current.IsResolved);
        Assert.Equal("L", subscription.Current.GetValueOrDefault());

        // Act: a mid-path retrack (replace the collection element at [1] carrying a different keyed leaf).
        var element2 = new Node
        {
            Name = "n2",
            ByName = new Dictionary<string, Node> { ["k"] = new() { Name = "L2" } }
        };
        mid.Children = [new Node { Name = "c0" }, element2];

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal("L", change.OldState.GetValueOrDefault());
        Assert.Equal("L2", change.NewState.GetValueOrDefault());
    }

    // --- Custom-comparer dictionary (case-insensitive, generic-only implementation) --------------------

    [Fact]
    public void WhenCaseInsensitiveGenericOnlyDictionary_ThenComparerMatchedKeyResolves()
    {
        // Arrange: a generic-only IDictionary<string,Node> whose comparer is OrdinalIgnoreCase. A stored
        // "Key" must be found by a "key" lookup (the walk's TryGetValue honors the dictionary's comparer).
        var context = FullTracking();
        var holder = new CaseInsensitiveHolder(context)
        {
            ByName = new CaseInsensitiveDictionary<Node> { ["Key"] = new() { Name = "A" } }
        };
        using var subscription = Watch(holder, x => x.ByName["key"].Name, out var events);

        // Act & Assert: comparer-honoring lookup resolves (NOT unresolved).
        Assert.True(subscription.Current.IsResolved);
        Assert.Equal("A", subscription.Current.GetValueOrDefault());

        // Act: retrack with another comparer-matched key.
        holder.ByName = new CaseInsensitiveDictionary<Node> { ["KEY"] = new() { Name = "B" } };

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal("A", change.OldState.GetValueOrDefault());
        Assert.Equal("B", change.NewState.GetValueOrDefault());
    }

    // --- Non-IEquatable struct leaf -------------------------------------------------------------------

    [Fact]
    public void WhenNonEquatableStructLeaf_ThenResolvesAndTransitionValueIsCorrect()
    {
        // Arrange: PlainStruct does not implement IEquatable<T> (the equality comparison boxes); here only
        // value correctness of resolution and transition is asserted.
        var context = FullTracking();
        var parent = new StructHolderParent(context) { Child = new StructLeafHolder { Value = new PlainStruct(1, 2) } };
        using var subscription = Watch(parent, x => x.Child!.Value, out var events);

        // Act & Assert
        Assert.True(subscription.Current.IsResolved);
        Assert.Equal(1, subscription.Current.GetValueOrDefault().X);
        Assert.Equal(2, subscription.Current.GetValueOrDefault().Y);

        // Act: retrack so the observed struct value moves.
        parent.Child = new StructLeafHolder { Value = new PlainStruct(3, 4) };

        // Assert
        var change = AssertSingleTransition(events, SubjectPathChangeKind.PathChange);
        Assert.Equal(1, change.OldState.GetValueOrDefault().X);
        Assert.Equal(3, change.NewState.GetValueOrDefault().X);
        Assert.Equal(4, change.NewState.GetValueOrDefault().Y);
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private static IInterceptorSubjectContext FullTracking()
        => InterceptorSubjectContext.Create().WithFullPropertyTracking();

    // Subscribe to a path and collect its delivered changes. The caller disposes the returned subscription.
    private static SubjectPathSubscription<TValue> Watch<TSubject, TValue>(
        TSubject subject,
        Expression<Func<TSubject, TValue>> path,
        out List<SubjectPathChange<TValue>> events)
        where TSubject : IInterceptorSubject
    {
        var captured = new List<SubjectPathChange<TValue>>();
        events = captured;
        return subject.SubscribeToPath(path, (in SubjectPathChange<TValue> change) => captured.Add(change), SubjectPathValidation.Full);
    }

    private static SubjectPathChange<TValue> AssertSingleTransition<TValue>(
        List<SubjectPathChange<TValue>> events, SubjectPathChangeKind kind)
    {
        var change = Assert.Single(events);
        Assert.Equal(kind, change.Kind);
        return change;
    }
}
