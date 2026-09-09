using System.Reactive.Concurrency;
using System.Text.Json;
using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Tests for cycle detection and handling in SubjectUpdate serialization.
/// These tests ensure that circular object references don't cause infinite loops
/// or JSON serialization failures. The flat structure uses ID references instead
/// of nested objects, making cycles impossible in the JSON.
/// </summary>
public class SubjectUpdateCycleTests
{
    [Fact]
    public async Task WhenTreeHasLoop_ThenCreateCompleteShouldNotFail()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();

        var father = new Person { FirstName = "Father" };
        var person = new Person(context)
        {
            FirstName = "Child",
            Father = father,
        };
        father.Father = person; // create loop

        // Act
        var completeSubjectUpdate = SubjectUpdate
            .CreateCompleteUpdate(person, [JsonCamelCasePathProcessor.Instance]);

        // Assert - flat structure should serialize without cycles
        var json = JsonSerializer.Serialize(completeSubjectUpdate);
        Assert.NotNull(json);

        await Verify(completeSubjectUpdate).DisableDateCounting();
    }

    [Fact]
    public async Task WhenTreeHasLoop_ThenCreatePartialUpdateShouldNotFail()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();

        var father = new Person { FirstName = "Father" };
        var person = new Person(context)
        {
            FirstName = "Child",
            Father = father,
        };
        father.Father = person; // create loop

        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference(person, "Father"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, null, father),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert - flat structure should serialize without cycles
        var json = JsonSerializer.Serialize(partialSubjectUpdate);
        Assert.NotNull(json);

        await Verify(partialSubjectUpdate).DisableDateCounting();
    }

    [Fact]
    public void WhenTreeHasLoopInParentPath_ThenCreatePartialUpdateShouldNotFail()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();

        var father = new Person { FirstName = "Father" };
        var mother = new Person { FirstName = "Mother" };
        father.Mother = mother;
        mother.Father = father; // cycle between father and mother

        var person = new Person(context)
        {
            FirstName = "Child",
            Father = father,
            Mother = mother,
        };

        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference(father, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewFather"),
            SubjectPropertyChange.Create(
                new PropertyReference(mother, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewMother"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        var json = JsonSerializer.Serialize(partialSubjectUpdate);
        Assert.NotNull(json);
    }

    [Fact]
    public void WhenChildPointsToParent_ThenCreatePartialUpdateShouldNotFail()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();

        var person = new Person(context) { FirstName = "Parent" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        person.Children = [child1, child2];
        child1.Father = person;
        child2.Father = person;

        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference(child1, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewChild1"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        var json = JsonSerializer.Serialize(partialSubjectUpdate);
        Assert.NotNull(json);
    }

    [Fact]
    public void WhenCollectionChangesWithCycles_ThenCreatePartialUpdateShouldNotFail()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();

        var person = new Person(context) { FirstName = "Parent" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        child1.Father = person;
        child2.Father = person;
        child1.Mother = child2; // siblings point to each other
        child2.Mother = child1;
        person.Children = [child1, child2];

        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference(person, "Children"),
                ChangeOrigin.Local,
                DateTimeOffset.UtcNow,
                null,
                new List<Person>(),
                new List<Person> { child1, child2 }),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        var json = JsonSerializer.Serialize(partialSubjectUpdate);
        Assert.NotNull(json);
        var operations = partialSubjectUpdate.Subjects[partialSubjectUpdate.Root]["children"].Operations;
        Assert.NotNull(operations);
        Assert.All(operations, operation => Assert.True(partialSubjectUpdate.Subjects.ContainsKey(operation.Id!)));
    }

    [Fact]
    public void WhenMultipleChangesCreateCycle_ThenShouldNotFail()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();

        var person = new Person(context) { FirstName = "Person" };
        var sibling1 = new Person { FirstName = "Sibling1" };
        var sibling2 = new Person { FirstName = "Sibling2" };

        sibling1.Mother = sibling2;
        sibling2.Mother = sibling1;

        person.Father = sibling1;
        person.Mother = sibling2;

        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference(person, "Father"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, null, sibling1),
            SubjectPropertyChange.Create(
                new PropertyReference(person, "Mother"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, null, sibling2),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        var json = JsonSerializer.Serialize(partialSubjectUpdate);
        Assert.NotNull(json);
    }

    [Fact]
    public void WhenDeepNestedStructureWithCycles_ThenShouldNotFail()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();

        var root = new Person(context) { FirstName = "Root" };

        var level1 = new Person { FirstName = "Level1" };
        var level2 = new Person { FirstName = "Level2" };
        var level3 = new Person { FirstName = "Level3" };

        root.Father = level1;
        level1.Father = level2;
        level2.Father = level3;
        level3.Father = root; // cycle back to root

        root.Children = [level1, level2, level3];
        level1.Children = [root]; // child points back to root

        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference(level1, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewLevel1"),
            SubjectPropertyChange.Create(
                new PropertyReference(level2, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewLevel2"),
            SubjectPropertyChange.Create(
                new PropertyReference(level3, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewLevel3"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        var json = JsonSerializer.Serialize(partialSubjectUpdate);
        Assert.NotNull(json);
    }

    [Fact]
    public void WhenChildChangeReferencesParentAndParentHasCollection_ThenShouldNotFail()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var child = new Person { FirstName = "Child" };

        root.Children = [child];
        child.Father = root;

        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference(child, "Father"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, null, root),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        var json = JsonSerializer.Serialize(partialSubjectUpdate);
        Assert.NotNull(json);
    }

    [Fact]
    public void WhenMultipleValueChangesOnNestedSubject_ThenSubjectUpdatesAreAccumulated()
    {
        // Arrange - Multiple value changes on the same nested subject should
        // accumulate in the same subject entry in the Subjects dictionary
        var context = InterceptorSubjectContext.Create().WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var bar = new Person { FirstName = "Bar" };
        var baz = new Person { FirstName = "Baz" };

        root.Father = bar;
        bar.Father = baz;

        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference(baz, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewFirst"),
            SubjectPropertyChange.Create(
                new PropertyReference(baz, "LastName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewLast"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        var json = JsonSerializer.Serialize(partialSubjectUpdate);
        Assert.NotNull(json);

        // Find the subject entry for baz (it should have both properties)
        // With flat structure, we look in the Subjects dictionary
        var bazSubjectId = FindSubjectIdWithProperty(partialSubjectUpdate, "firstName", "NewFirst");
        Assert.NotNull(bazSubjectId);

        var bazProperties = partialSubjectUpdate.Subjects[bazSubjectId];
        Assert.True(bazProperties.ContainsKey("firstName"));
        Assert.True(bazProperties.ContainsKey("lastName"));
        Assert.Equal("NewFirst", bazProperties["firstName"].Value);
        Assert.Equal("NewLast", bazProperties["lastName"].Value);
    }

    [Fact]
    public async Task WhenModelHasRefsCollectionsDictionariesWithCycles_ThenCreateCompleteUpdateSucceeds()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();

        var root = new CycleTestNode(context) { Name = "Root" };
        var child1 = new CycleTestNode { Name = "Child1" };
        var grandChild = new CycleTestNode { Name = "GrandChild" };
        var anotherChild = new CycleTestNode { Name = "AnotherChild" };
        var child2 = new CycleTestNode { Name = "Child2" };

        root.Self = root; // direct self-cycle
        root.Child = child1;
        child1.Parent = root;

        child1.Items = [grandChild];
        grandChild.Parent = root;
        root.Items = [child2];
        child2.Parent = root;

        child1.Lookup = new Dictionary<string, CycleTestNode> { ["key"] = anotherChild };
        anotherChild.Parent = root;

        // Act
        var completeUpdate = SubjectUpdate
            .CreateCompleteUpdate(root, [JsonCamelCasePathProcessor.Instance]);

        // Assert - flat structure eliminates cycles
        var json = JsonSerializer.Serialize(completeUpdate);
        Assert.NotNull(json);

        // With flat structure, all subjects are in the dictionary
        // and reference each other by ID - no nested cycles possible
        Assert.True(completeUpdate.Subjects.Count > 0);

        await Verify(completeUpdate).DisableDateCounting();
    }

    [Fact]
    public async Task WhenModelHasRefsCollectionsDictionariesWithCycles_ThenCreatePartialUpdateSucceeds()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeSubscriptions()
            .WithRegistry();

        var root = new CycleTestNode(context) { Name = "Root" };
        var child1 = new CycleTestNode { Name = "Child1" };
        var grandChild = new CycleTestNode { Name = "GrandChild" };
        var anotherChild = new CycleTestNode { Name = "AnotherChild" };
        var child2 = new CycleTestNode { Name = "Child2" };

        root.Self = root;
        root.Child = child1;
        child1.Parent = root;
        child1.Items = [grandChild];
        grandChild.Parent = root;
        root.Items = [child2];
        child2.Parent = root;
        child1.Lookup = new Dictionary<string, CycleTestNode> { ["key"] = anotherChild };
        anotherChild.Parent = root;

        var changes = new List<SubjectPropertyChange>();
        using var _ = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        // Make changes
        root.Name = "Root_v1";
        root.Name = "Root_v2";
        root.Name = "Root_Final";

        root.Name_Status = "status_v1";
        root.Name_Status = "status_Final";

        var newChild = new CycleTestNode { Name = "NewChild" };
        newChild.Parent = root;
        root.Child = newChild;

        var newItem = new CycleTestNode { Name = "NewItem" };
        newItem.Parent = root;
        root.Items = [child2, newItem];

        // Act
        var partialUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes.ToArray(), [JsonCamelCasePathProcessor.Instance]);

        // Assert
        var json = JsonSerializer.Serialize(partialUpdate);
        Assert.NotNull(json);

        // Verify "last wins" for root.Name
        var rootProperties = partialUpdate.Subjects[partialUpdate.Root];
        Assert.True(rootProperties.ContainsKey("name"));
        Assert.Equal("Root_Final", rootProperties["name"].Value);

        await Verify(partialUpdate).DisableDateCounting();
    }

    [Fact]
    public void WhenCollectionInsertHasItemsReferencingEachOther_ThenShouldNotFail()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();

        var root = new CycleTestNode(context) { Name = "Root" };

        var item1 = new CycleTestNode { Name = "Item1" };
        var item2 = new CycleTestNode { Name = "Item2" };
        item1.Child = item2;
        item2.Child = item1;

        item1.Parent = root;
        item2.Parent = root;

        var oldItems = root.Items.ToList();
        root.Items = [item1, item2];
        var newItems = root.Items.ToList();

        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference(root, "Items"),
                ChangeOrigin.Local,
                DateTimeOffset.UtcNow,
                null,
                oldItems,
                newItems),
        };

        // Act
        var partialUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        var json = JsonSerializer.Serialize(partialUpdate);
        Assert.NotNull(json);

        // Verify we have Operations with Inserts
        var rootProperties = partialUpdate.Subjects[partialUpdate.Root];
        Assert.NotNull(rootProperties["items"].Operations);
        Assert.Equal(2, rootProperties["items"].Operations!.Count);
    }

    [Fact]
    public void WhenInsertedChildReferencesParent_ThenShouldCreateIdReference()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();

        var root = new CycleTestNode(context) { Name = "Root" };

        var child = new CycleTestNode { Name = "Child" };
        child.Parent = root;

        var oldItems = root.Items.ToList();
        root.Items = [child];
        var newItems = root.Items.ToList();

        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference(root, "Items"),
                ChangeOrigin.Local,
                DateTimeOffset.UtcNow,
                null,
                oldItems,
                newItems),
        };

        // Act
        var partialUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        var json = JsonSerializer.Serialize(partialUpdate);
        Assert.NotNull(json);

        // With flat structure, the insert operation has an Id that references
        // the subject in the Subjects dictionary
        var rootProperties = partialUpdate.Subjects[partialUpdate.Root];
        var operations = rootProperties["items"].Operations;
        Assert.NotNull(operations);
        Assert.Single(operations);

        var insertOp = operations[0];
        Assert.NotNull(insertOp.Id);

        // The child subject should exist in the dictionary
        Assert.True(partialUpdate.Subjects.ContainsKey(insertOp.Id));

        // The child's parent property should reference the root by Id
        var childProperties = partialUpdate.Subjects[insertOp.Id];
        Assert.True(childProperties.ContainsKey("parent"));
        Assert.Equal(partialUpdate.Root, childProperties["parent"].Id);
    }

    [Fact]
    public void WhenDeeplyNestedCollectionInsertsWithCrossReferences_ThenShouldNotFail()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();

        var root = new CycleTestNode(context) { Name = "Root" };

        var level1 = new CycleTestNode { Name = "Level1" };
        var level2 = new CycleTestNode { Name = "Level2" };

        root.Child = level1;
        level1.Child = level2;
        level1.Parent = root;
        level2.Parent = level1;

        var item1 = new CycleTestNode { Name = "Item1" };
        var item2 = new CycleTestNode { Name = "Item2" };
        item1.Parent = root;
        item2.Parent = level1;
        item1.Child = item2;
        item2.Child = item1;

        var level1Item = new CycleTestNode { Name = "Level1Item" };
        level1Item.Parent = root;

        var oldLevel2Items = level2.Items.ToList();
        level2.Items = [item1, item2];
        var newLevel2Items = level2.Items.ToList();

        level1Item.Items = [item1];

        var oldLevel1Items = level1.Items.ToList();
        level1.Items = [level1Item];
        var newLevel1Items = level1.Items.ToList();

        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference(level2, "Items"),
                ChangeOrigin.Local,
                DateTimeOffset.UtcNow,
                null,
                oldLevel2Items,
                newLevel2Items),
            SubjectPropertyChange.Create(
                new PropertyReference(level1, "Items"),
                ChangeOrigin.Local,
                DateTimeOffset.UtcNow,
                null,
                oldLevel1Items,
                newLevel1Items),
        };

        // Act
        var partialUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        var json = JsonSerializer.Serialize(partialUpdate);
        Assert.NotNull(json);
    }

    [Fact]
    public async Task WhenNonRootSubjectsFormCycleInParentPath_ThenBuildPathToRootTerminates()
    {
        // Arrange - Create a cycle among non-root subjects where none are the root
        // root -> A (via Father), A -> B (via Father), B -> A (via Father) -- cycle!
        // When a property changes on B, BuildPathToRoot walks B->A->B->A... infinitely
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var nodeA = new Person { FirstName = "A" };
        var nodeB = new Person { FirstName = "B" };

        root.Father = nodeA;
        nodeA.Father = nodeB;
        nodeB.Father = nodeA; // cycle: A -> B -> A

        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference(nodeB, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewB"),
        };

        // Act -- should terminate, not infinite loop
        var partialUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes, []);

        // Assert
        var json = JsonSerializer.Serialize(partialUpdate);
        Assert.NotNull(json);
        await Verify(partialUpdate).DisableDateCounting();
    }

    /// <summary>
    /// Helper to find a subject ID that has a specific property value.
    /// </summary>
    private static string? FindSubjectIdWithProperty(
        SubjectUpdate update, string propertyName, object expectedValue)
    {
        foreach (var (id, properties) in update.Subjects)
        {
            if (properties.TryGetValue(propertyName, out var propUpdate) &&
                Equals(propUpdate.Value, expectedValue))
            {
                return id;
            }
        }
        return null;
    }
}
