using System.Reactive.Concurrency;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Tests for dictionary diff operations in SubjectUpdate.
/// Tests Insert (add key), Remove (delete key) operations and sparse property updates.
/// Note: Move is not applicable for dictionaries.
/// </summary>
public class SubjectUpdateDictionaryTests
{
    [Fact]
    public async Task WhenKeyAdded_ThenInsertOperationIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1 }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - add new key
        var item2 = new CycleTestNode { Name = "Item2" };
        node.Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 };

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenKeyRemoved_ThenRemoveOperationIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var item2 = new CycleTestNode { Name = "Item2" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - remove key
        node.Lookup = new Dictionary<string, CycleTestNode> { ["key2"] = item2 };

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenItemPropertyChanged_ThenSparseUpdateByKeyIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var item2 = new CycleTestNode { Name = "Item2" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - update property on item (no structural change)
        item1.Name = "Item1Updated";

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert - should have Collection update at key1, not Operations
        await Verify(update);
    }

    [Fact]
    public async Task WhenMultipleItemsHavePropertyChanges_ThenSparseUpdatesAreCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var item2 = new CycleTestNode { Name = "Item2" };
        var item3 = new CycleTestNode { Name = "Item3" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode>
            {
                ["key1"] = item1,
                ["key2"] = item2,
                ["key3"] = item3
            }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - update properties on two items
        item1.Name = "Item1Updated";
        item3.Name = "Item3Updated";

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert - should have sparse Collection updates at key1 and key3
        await Verify(update);
    }

    [Fact]
    public async Task WhenAddAndRemoveCombined_ThenBothOperationsAreCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var item2 = new CycleTestNode { Name = "Item2" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - remove key1, add key3
        var item3 = new CycleTestNode { Name = "Item3" };
        node.Lookup = new Dictionary<string, CycleTestNode> { ["key2"] = item2, ["key3"] = item3 };

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenInsertWithPropertyUpdateOnExisting_ThenBothTypesAreCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1 }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - update existing AND add new key
        item1.Name = "Item1Updated";
        var item2 = new CycleTestNode { Name = "Item2" };
        node.Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 };

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenDictionaryBecomesEmpty_ThenRemoveOperationsAreCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var item2 = new CycleTestNode { Name = "Item2" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - clear dictionary
        node.Lookup = new Dictionary<string, CycleTestNode>();

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenDictionaryPopulatedFromEmpty_ThenInsertOperationsAreCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode>()
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - populate from empty
        var item1 = new CycleTestNode { Name = "Item1" };
        var item2 = new CycleTestNode { Name = "Item2" };
        node.Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 };

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenCompleteDictionaryUpdate_ThenAllItemsIncluded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var item2 = new CycleTestNode { Name = "Item2" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 }
        };

        // Act - create complete update
        var update = SubjectUpdate.CreateCompleteUpdate(node, []);

        // Assert - Collection should have all items with full data
        await Verify(update);
    }

    [Fact]
    public async Task WhenValueReplacedAtSameKey_ThenInsertAndRemoveOperationsAreCreated()
    {
        // Arrange
        // This tests replacing the VALUE at an existing key with a DIFFERENT object.
        // This should be treated as a Remove + Insert, not ignored.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1 }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - replace value at same key with a DIFFERENT object
        var item2 = new CycleTestNode { Name = "ReplacementItem" };
        node.Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item2 };

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert - the new item should be included in the update
        // Either as an Insert operation or as a sparse update with full data
        await Verify(update);
    }

    [Fact]
    public void WhenValueReplacedAtSameKey_ThenRemoveIsEmittedBeforeInsertForThatKey()
    {
        // Arrange
        // The apply side walks operations sequentially, so the Remove for the replaced key
        // has to precede the Insert that carries the replacement.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1 }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act
        var item2 = new CycleTestNode { Name = "ReplacementItem" };
        node.Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item2 };

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        var operations = update.Subjects[update.Root]["Lookup"].Operations;
        Assert.NotNull(operations);
        Assert.Collection(operations,
            operation =>
            {
                Assert.Equal(SubjectCollectionOperationType.Remove, operation.Action);
                Assert.Equal("key1", operation.Index);
            },
            operation =>
            {
                Assert.Equal(SubjectCollectionOperationType.Insert, operation.Action);
                Assert.Equal("key1", operation.Index);
                Assert.NotNull(operation.Id);
            });
    }

    [Fact]
    public async Task WhenDictionarySetToNull_ThenCompleteUpdateHasValueKindWithNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var node = new CycleTestNode(context) { Name = "Root", Lookup = null! };

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(node, []);

        // Assert - Lookup should be Value kind with null, not Dictionary kind
        Assert.NotNull(update.Subjects);
        Assert.True(update.Subjects.TryGetValue(update.Root!, out var rootProperties));
        Assert.True(rootProperties!.TryGetValue("Lookup", out var lookupUpdate));
        Assert.Equal(SubjectPropertyUpdateKind.Value, lookupUpdate!.Kind);
        Assert.Null(lookupUpdate.Value);

        await Verify(update);
    }

    [Fact]
    public void WhenDictionarySetToNull_ThenPartialUpdateHasValueKindWithNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1 }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - set dictionary to null
        node.Lookup = null!;

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert - Lookup should be Value kind with null
        Assert.NotNull(update.Subjects);
        Assert.True(update.Subjects.TryGetValue(update.Root!, out var rootProperties));
        Assert.True(rootProperties!.TryGetValue("Lookup", out var lookupUpdate));
        Assert.Equal(SubjectPropertyUpdateKind.Value, lookupUpdate!.Kind);
        Assert.Null(lookupUpdate.Value);
    }

    [Fact]
    public void WhenNullDictionaryApplied_ThenTargetDictionaryBecomesNull()
    {
        // Arrange - create source with dictionary then set to null
        var sourceContext = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var source = new CycleTestNode(sourceContext)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1 }
        };

        var changes = new List<SubjectPropertyChange>();
        sourceContext.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        source.Lookup = null!;
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);

        // Create target with populated dictionary
        var targetContext = InterceptorSubjectContext.Create().WithRegistry();
        var target = new CycleTestNode(targetContext)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = new CycleTestNode { Name = "Item1" } }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Null(target.Lookup);
    }
}
