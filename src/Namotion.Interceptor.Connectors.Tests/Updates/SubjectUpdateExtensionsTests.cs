using System.Reactive.Linq;
using System.Text.Json;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public partial class SubjectUpdateExtensionsTests
{
    [Fact]
    public async Task WhenApplyingSimpleProperty_ThenItWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var source = new Person(context) { FirstName = "John", LastName = "Doe" };
        var target = new Person(context);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("John", target.FirstName);
        Assert.Equal("Doe", target.LastName);
    }

    [Fact]
    public async Task WhenApplyingSimplePropertyWithTimestamp_ThenTimestampIsPreserved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var timestamp = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero);

        Person source;
        using (SubjectChangeContext.WithChangedTimestamp(timestamp))
        {
            source = new Person(context) { FirstName = "John", LastName = "Doe" };
        }

        var target = new Person(context);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal(timestamp, target.GetPropertyReference("FirstName").TryGetWriteTimestamp());
    }

    [Fact]
    public async Task WhenApplyingNestedProperty_ThenItWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var source = new Person(context)
        {
            FirstName = "Child",
            Father = new Person(context) { FirstName = "Father" }
        };
        var target = new Person(context);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Child", target.FirstName);
        Assert.NotNull(target.Father);
        Assert.Equal("Father", target.Father.FirstName);
    }

    [Fact]
    public async Task WhenApplyingCollectionProperty_ThenItWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

        // Use a fixed timestamp so the verified snapshot is deterministic. With wall-clock
        // timestamps, two property writes can land in the same clock tick, which collapses
        // Verify's DateTimeOffset_N numbering and makes the snapshot intermittently fail.
        var timestamp = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero);

        Person source;
        using (SubjectChangeContext.WithChangedTimestamp(timestamp))
        {
            source = new Person(context)
            {
                FirstName = "Parent",
                Children =
                [
                    new Person(context) { FirstName = "Child1" },
                    new Person(context) { FirstName = "Child2" }
                ]
            };
        }
        var target = new Person(context);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal(2, target.Children.Count);
        Assert.Equal("Child1", target.Children[0].FirstName);
        Assert.Equal("Child2", target.Children[1].FirstName);
    }

    [Fact]
    public async Task WhenApplyingPartialUpdate_ThenOnlyChangedPropertiesAreUpdated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(context) { FirstName = "Initial", LastName = "Name" };
        var target = new Person(context) { FirstName = "Initial", LastName = "Name" };

        // Make a change to source
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.FirstName = "Updated";
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Updated", target.FirstName);
        Assert.Equal("Name", target.LastName); // Unchanged
    }

    [Fact]
    public async Task WhenApplyingCollectionInsert_ThenItemIsAdded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(context)
        {
            FirstName = "Parent",
            Children = [new Person(context) { FirstName = "ExistingChild" }]
        };
        var target = new Person(context)
        {
            FirstName = "Parent",
            Children = [new Person(context) { FirstName = "ExistingChild" }]
        };

        // Make a change - add a child
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Children = [..source.Children, new Person(context) { FirstName = "NewChild" }];
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal(2, target.Children.Count);
        Assert.Equal("NewChild", target.Children[1].FirstName);
    }

    [Fact]
    public async Task WhenApplyingCollectionRemove_ThenItemIsRemoved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child1 = new Person(context) { FirstName = "Child1" };
        var child2 = new Person(context) { FirstName = "Child2" };
        var source = new Person(context) { FirstName = "Parent", Children = [child1, child2] };

        var targetChild1 = new Person(context) { FirstName = "Child1" };
        var targetChild2 = new Person(context) { FirstName = "Child2" };
        var target = new Person(context) { FirstName = "Parent", Children = [targetChild1, targetChild2] };

        // Make a change - remove first child
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Children = [child2];
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Single(target.Children);
        Assert.Equal("Child2", target.Children[0].FirstName);
    }

    [Fact]
    public async Task WhenApplyingCollectionMove_ThenItemIsMoved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child1 = new Person(context) { FirstName = "Child1" };
        var child2 = new Person(context) { FirstName = "Child2" };
        var child3 = new Person(context) { FirstName = "Child3" };
        var source = new Person(context) { FirstName = "Parent", Children = [child1, child2, child3] };

        var targetChild1 = new Person(context) { FirstName = "Child1" };
        var targetChild2 = new Person(context) { FirstName = "Child2" };
        var targetChild3 = new Person(context) { FirstName = "Child3" };
        var target = new Person(context) { FirstName = "Parent", Children = [targetChild1, targetChild2, targetChild3] };

        // Make a change - move child3 to front
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Children = [child3, child1, child2];
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal(3, target.Children.Count);
        Assert.Equal("Child3", target.Children[0].FirstName);
        Assert.Equal("Child1", target.Children[1].FirstName);
        Assert.Equal("Child2", target.Children[2].FirstName);
    }

    [Fact]
    public async Task WhenApplyingNestedPropertyUpdate_ThenNestedItemIsUpdated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person(context) { FirstName = "OriginalChild" };
        var source = new Person(context) { FirstName = "Parent", Children = [child] };

        var targetChild = new Person(context) { FirstName = "OriginalChild" };
        var target = new Person(context) { FirstName = "Parent", Children = [targetChild] };

        // Make a change - update nested child's property
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            child.FirstName = "UpdatedChild";
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(targetChild, target.Children[0]); // Same instance reused
        Assert.Equal("UpdatedChild", target.Children[0].FirstName);
    }

    [Fact]
    public async Task WhenApplyingDictionaryComplete_ThenDictionaryIsPopulated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var source = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode>
            {
                ["key1"] = new(context) { Name = "Item1" },
                ["key2"] = new(context) { Name = "Item2" }
            }
        };
        var target = new CycleTestNode(context);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal(2, target.Lookup.Count);
        Assert.Equal("Item1", target.Lookup["key1"].Name);
        Assert.Equal("Item2", target.Lookup["key2"].Name);
    }

    [Fact]
    public async Task WhenApplyingDictionaryInsert_ThenItemIsAdded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var existingItem = new CycleTestNode(context) { Name = "Existing" };
        var source = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["existing"] = existingItem }
        };
        var target = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode>
            {
                ["existing"] = new(context) { Name = "Existing" }
            }
        };

        // Make a change - add new key
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Lookup = new Dictionary<string, CycleTestNode>
            {
                ["existing"] = existingItem,
                ["newKey"] = new(context) { Name = "NewItem" }
            };
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal(2, target.Lookup.Count);
        Assert.Equal("NewItem", target.Lookup["newKey"].Name);
    }

    [Fact]
    public async Task WhenApplyingDictionaryRemove_ThenItemIsRemoved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var item1 = new CycleTestNode(context) { Name = "Item1" };
        var item2 = new CycleTestNode(context) { Name = "Item2" };
        var source = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 }
        };
        var target = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode>
            {
                ["key1"] = new(context) { Name = "Item1" },
                ["key2"] = new(context) { Name = "Item2" }
            }
        };

        // Make a change - remove key1
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Lookup = new Dictionary<string, CycleTestNode> { ["key2"] = item2 };
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Single(target.Lookup);
        Assert.False(target.Lookup.ContainsKey("key1"));
        Assert.Equal("Item2", target.Lookup["key2"].Name);
    }

    [Fact]
    public void WhenApplyingDictionaryValueReplacedAtSameKey_ThenEntryIsReplacedNotDeleted()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var originalItem = new CycleTestNode(context) { Name = "Item1" };
        var source = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = originalItem }
        };
        var target = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode>
            {
                ["key1"] = new(context) { Name = "Item1" }
            }
        };

        // Make a change - replace the value at an existing key with a different subject
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Lookup = new Dictionary<string, CycleTestNode>
            {
                ["key1"] = new(context) { Name = "Replacement" }
            };
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.True(target.Lookup.ContainsKey("key1"), "The replaced entry must survive the round trip.");
        Assert.Equal("Replacement", target.Lookup["key1"].Name);
        Assert.Single(target.Lookup);
    }

    [Fact]
    public async Task WhenApplyingCircularReference_ThenItWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var parent = new CycleTestNode(context) { Name = "Parent" };
        var child = new CycleTestNode(context) { Name = "Child", Parent = parent };
        parent.Child = child;

        var target = new CycleTestNode(context);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(parent, []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Parent", target.Name);
        Assert.NotNull(target.Child);
        Assert.Equal("Child", target.Child.Name);
        // Note: The circular reference back to parent won't be restored since
        // we create new instances. This is expected behavior.
    }

    [Fact]
    public async Task WhenApplyingSelfReference_ThenItWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var node = new CycleTestNode(context) { Name = "SelfRef" };
        node.Self = node;

        var target = new CycleTestNode(context);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(node, []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("SelfRef", target.Name);
        // Self-reference won't be restored to point to target itself
    }

    [Fact]
    public async Task WhenApplyingWithJsonElementValues_ThenValuesAreConverted()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context);

        // Create update with JsonElement values (simulating JSON deserialization)
        var json = """{"value": "JsonValue"}""";
        var jsonElement = JsonDocument.Parse(json).RootElement.GetProperty("value");

        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    ["FirstName"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = jsonElement
                    }
                }
            }
        };

        // Act
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("JsonValue", target.FirstName);
    }

    [Fact]
    public async Task WhenApplyingFromSource_ThenSourceIsTracked()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(context) { FirstName = "John" };
        var target = new Person(context);
        var externalSource = new object();

        // Capture FirstName change specifically (FullName is derived and fires without source)
        SubjectPropertyChange? capturedChange = null;
        using var subscription = context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == "FirstName")
            .Subscribe(c => capturedChange = c);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.FromSource(externalSource));

        // Assert - the applied write carries the FromSource origin so echo suppression can skip the source
        Assert.NotNull(capturedChange);
        Assert.Equal(ChangeOriginKind.FromSource, capturedChange.Value.Origin.Kind);
        Assert.Same(externalSource, capturedChange.Value.Origin.Source);
    }

    [Fact]
    public void WhenUpdateIsAppliedWithSource_ThenChangeCarriesUpdateTimestamp()
    {
        // Arrange - a FromSource apply must publish the inbound update's timestamp, not capture-time now
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var target = new Person(context) { FirstName = "Original" };
        var source = new object();
        var updateTimestamp = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero);

        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    ["FirstName"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Updated",
                        Timestamp = updateTimestamp
                    }
                }
            }
        };

        SubjectPropertyChange? capturedChange = null;
        using var subscription = context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == "FirstName")
            .Subscribe(c => capturedChange = c);

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.FromSource(source));

        // Assert
        Assert.Equal("Updated", target.FirstName);
        Assert.NotNull(capturedChange);
        Assert.Equal(updateTimestamp, capturedChange.Value.ChangedTimestamp);
        Assert.Equal(ChangeOriginKind.FromSource, capturedChange.Value.Origin.Kind);
        Assert.Same(source, capturedChange.Value.Origin.Source);
    }

    [Fact]
    public void WhenFromSourceApplyTransformChangesValue_ThenChangeCarriesLocalOrigin()
    {
        // Arrange - a FromSource apply whose transform corrects the inbound value must publish the
        // corrected value under a Local origin. The pending origin's evidence has to stay the value
        // the source semantically sent (pre-transform); otherwise the survival check compares the
        // corrected value against itself, the FromSource origin survives, and the outbound processor
        // echo-suppresses the correction back to the source, diverging the source from the applied value.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var target = new NumericNode(context) { Value = 0 };
        var source = new object();

        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    ["Value"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = 105
                    }
                }
            }
        };

        SubjectPropertyChange? capturedChange = null;
        using var subscription = context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == "Value")
            .Subscribe(c => capturedChange = c);

        // Act - the transform corrects 105 to 100 before the value is applied
        target.ApplySubjectUpdate(
            update,
            DefaultSubjectFactory.Instance,
            ChangeOrigin.FromSource(source),
            (_, propertyUpdate) => propertyUpdate.Value = 100);

        // Assert
        Assert.Equal(100, target.Value);
        Assert.NotNull(capturedChange);
        Assert.Equal(ChangeOriginKind.Local, capturedChange.Value.Origin.Kind);
    }

    [Fact]
    public void WhenFromSourceApplyTransformLeavesReferenceTypeValueUnchanged_ThenChangeCarriesFromSourceOrigin()
    {
        // Arrange - a FromSource apply whose transform inspects but does NOT replace a reference-type
        // value (int[]) must publish under the FromSource origin so echo suppression skips the source.
        // The JsonElement must be converted once and reused as both the written value and the origin's
        // survival evidence: converting twice produces two reference-distinct int[] instances that fail
        // the reference-equality survival check and wrongly demote a genuine unchanged source write to
        // Local, defeating echo suppression.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var target = new ArrayNode(context) { Numbers = [] };
        var source = new object();

        var jsonElement = JsonDocument.Parse("[1,2,3]").RootElement;

        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    ["Numbers"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = jsonElement
                    }
                }
            }
        };

        SubjectPropertyChange? capturedChange = null;
        using var subscription = context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == "Numbers")
            .Subscribe(c => capturedChange = c);

        // Act - the transform reads the value but leaves propertyUpdate.Value unchanged
        target.ApplySubjectUpdate(
            update,
            DefaultSubjectFactory.Instance,
            ChangeOrigin.FromSource(source),
            (property, propertyUpdate) => { _ = propertyUpdate.Value; });

        // Assert
        Assert.Equal([1, 2, 3], target.Numbers);
        Assert.NotNull(capturedChange);
        Assert.Equal(ChangeOriginKind.FromSource, capturedChange.Value.Origin.Kind);
        Assert.Same(source, capturedChange.Value.Origin.Source);
    }

    [Fact]
    public async Task WhenApplyingNullItem_ThenItemIsSetToNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(context)
        {
            FirstName = "Parent",
            Father = new Person(context) { FirstName = "Father" }
        };
        var target = new Person(context)
        {
            FirstName = "Parent",
            Father = new Person(context) { FirstName = "Father" }
        };

        // Make a change - set Father to null
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Father = null;
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Null(target.Father);
    }

    [Fact]
    public async Task WhenApplyingAttributeUpdate_ThenAttributeIsUpdated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new CycleTestNode(context) { Name = "Node" };
        source.Name_Status = "updated";

        var target = new CycleTestNode(context) { Name = "Node" };

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("updated", target.Name_Status);
    }

    [Fact]
    public void WhenApplyingUpdateWithEmptyRoot_ThenNothingHappens()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context) { FirstName = "Original" };

        var update = new SubjectUpdate
        {
            Root = string.Empty,
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    ["FirstName"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Changed"
                    }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert - nothing should change
        Assert.Equal("Original", target.FirstName);
    }

    [Fact]
    public void WhenApplyingUpdateWithMissingRootSubject_ThenNothingHappens()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context) { FirstName = "Original" };

        var update = new SubjectUpdate
        {
            Root = "nonexistent",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    ["FirstName"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Changed"
                    }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert - nothing should change
        Assert.Equal("Original", target.FirstName);
    }

    [Fact]
    public void WhenApplyingCollectionUpdateWithInvalidIndex_ThenItIsIgnored()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context)
        {
            FirstName = "Parent",
            Children =
            [
                new Person(context) { FirstName = "Child1" }
            ]
        };

        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    ["Children"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Remove,
                                Index = 999 // Invalid index - way out of bounds
                            }
                        ],
                        Count = 1
                    }
                }
            }
        };

        // Act - should not throw
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert - collection unchanged
        Assert.Single(target.Children);
        Assert.Equal("Child1", target.Children[0].FirstName);
    }

    [Fact]
    public void WhenApplyingUpdateWithMissingSubjectId_ThenItIsIgnored()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context) { FirstName = "Original" };

        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    ["Father"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = "nonexistent" // References a subject that doesn't exist
                    }
                }
            }
        };

        // Act - should not throw
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert - Father should remain null (not set to anything)
        Assert.Null(target.Father);
    }

    [Fact]
    public void WhenApplyingSparseUpdateWithIndexExceedingCount_ThenExceptionIsThrown()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context)
        {
            FirstName = "Parent",
            Children = [new Person(context) { FirstName = "Child1" }] // Only 1 item
        };

        // Create an update with index 5 but count 1 - this is invalid (index must be < count)
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    ["Children"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Items =
                        [
                            new SubjectPropertyItemUpdate
                            {
                                Index = 5, // Invalid: index >= count
                                Id = "2"
                            }
                        ],
                        Count = 1 // Final size is 1, so only index 0 is valid
                    }
                },
                ["2"] = new()
                {
                    ["FirstName"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "NewChild"
                    }
                }
            }
        };

        // Act & Assert - should throw because index >= count
        var exception = Assert.Throws<InvalidOperationException>(
            () => target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));
        Assert.Contains("out of bounds", exception.Message.ToLower());
    }

    [Fact]
    public void WhenApplyingSparseUpdateAtNextIndex_ThenItemIsAppended()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context)
        {
            FirstName = "Parent",
            Children = [new Person(context) { FirstName = "Child1" }] // 1 item at index 0
        };

        // Create an update that adds at index 1 (next available position - valid)
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    ["Children"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Items =
                        [
                            new SubjectPropertyItemUpdate
                            {
                                Index = 1, // Append position - valid
                                Id = "2"
                            }
                        ],
                        Count = 2
                    }
                },
                ["2"] = new()
                {
                    ["FirstName"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "NewChild"
                    }
                }
            }
        };

        // Act - should not throw
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal(2, target.Children.Count);
        Assert.Equal("Child1", target.Children[0].FirstName);
        Assert.Equal("NewChild", target.Children[1].FirstName);
    }

    [Fact]
    public void WhenApplyingMoveWithNegativeFromIndex_ThenItIsIgnored()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var child1 = new Person(context) { FirstName = "Child1" };
        var child2 = new Person(context) { FirstName = "Child2" };
        var target = new Person(context) { FirstName = "Parent", Children = [child1, child2] };

        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    ["Children"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Move,
                                FromIndex = -1, // Invalid negative index
                                Index = 0
                            }
                        ],
                        Count = 2
                    }
                }
            }
        };

        // Act - should not throw
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert - collection unchanged
        Assert.Equal(2, target.Children.Count);
        Assert.Equal("Child1", target.Children[0].FirstName);
        Assert.Equal("Child2", target.Children[1].FirstName);
    }

    [Fact]
    public void WhenApplyingRemoveAtNegativeIndex_ThenItIsIgnored()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context)
        {
            FirstName = "Parent",
            Children = [new Person(context) { FirstName = "Child1" }]
        };

        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    ["Children"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Remove,
                                Index = -1 // Invalid negative index
                            }
                        ],
                        Count = 1
                    }
                }
            }
        };

        // Act - should not throw
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert - collection unchanged
        Assert.Single(target.Children);
    }

    [Fact]
    public void WhenApplyingUpdateToEmptyCollection_ThenInsertOperationWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context)
        {
            FirstName = "Parent",
            Children = [] // Empty collection
        };

        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    ["Children"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Insert,
                                Index = 0,
                                Id = "2"
                            }
                        ],
                        Count = 1
                    }
                },
                ["2"] = new()
                {
                    ["FirstName"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "NewChild"
                    }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Single(target.Children);
        Assert.Equal("NewChild", target.Children[0].FirstName);
    }

    [Fact]
    public void WhenApplyingNullCollection_ThenCollectionIsSetToNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(context) { FirstName = "Parent", Children = [new Person(context) { FirstName = "Child" }] };
        var target = new Person(context) { FirstName = "Parent", Children = [new Person(context) { FirstName = "Child" }] };

        // Make a change - set collection to null
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Children = null!;
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Null(target.Children);
    }

    [Fact]
    public void WhenApplyingNullDictionary_ThenDictionaryIsSetToNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = new(context) { Name = "Item1" } }
        };
        var target = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = new(context) { Name = "Item1" } }
        };

        // Make a change - set dictionary to null
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Lookup = null!;
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Null(target.Lookup);
    }

    [Fact]
    public void WhenCreatingCompleteUpdateWithNullCollection_ThenUpdateHasKindValueAndValueNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var source = new Person(context) { FirstName = "Parent", Children = null! };

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        // Assert
        var rootProps = update.Subjects[update.Root];
        Assert.True(rootProps.ContainsKey("Children"));
        Assert.Equal(SubjectPropertyUpdateKind.Value, rootProps["Children"].Kind);
        Assert.Null(rootProps["Children"].Value);
    }

    [InterceptorSubject]
    public partial class NumericNode
    {
        public partial int Value { get; set; }
    }

    [InterceptorSubject]
    public partial class ArrayNode
    {
        public partial int[] Numbers { get; set; }
    }

    [InterceptorSubject]
    public partial class IntKeyNode
    {
        public IntKeyNode()
        {
            Name = "";
            IntLookup = new Dictionary<int, CycleTestNode>();
        }

        public partial string Name { get; set; }
        public partial Dictionary<int, CycleTestNode> IntLookup { get; set; }
    }

    [Fact]
    public void WhenApplyingDictionaryUpdateWithIntKeys_ThenExistingEntriesAreMatched()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var source = new IntKeyNode(context)
        {
            Name = "Root",
            IntLookup = new Dictionary<int, CycleTestNode>
            {
                [1] = new(context) { Name = "Item1" },
                [2] = new(context) { Name = "Item2" }
            }
        };
        var target = new IntKeyNode(context);

        // Act - complete update round-trip (values will be int keys in source, need to be matched after deserialization)
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal(2, target.IntLookup.Count);
        Assert.Equal("Item1", target.IntLookup[1].Name);
        Assert.Equal("Item2", target.IntLookup[2].Name);
    }

    [Fact]
    public void WhenApplyingDictionaryItemPropertyChangeViaJsonRoundTrip_ThenUpdateIsAppliedCorrectly()
    {
        // Arrange - this tests the BuildPathToRoot code path: when a property on a
        // dictionary item changes, the partial update must use Kind=Dictionary (not Collection).
        // After JSON round-trip, dictionary keys become JsonElement strings, so using
        // Kind=Collection would cause ConvertIndexToInt to fail on the string key.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var item1 = new CycleTestNode(context) { Name = "Item1" };
        var source = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["myKey"] = item1 }
        };
        var target = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode>
            {
                ["myKey"] = new(context) { Name = "Item1" }
            }
        };

        // Change a property on the dictionary item (triggers BuildPathToRoot)
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            item1.Name = "Item1Updated";
        }

        // Create the update, serialize to JSON, then deserialize (simulating WebSocket transfer)
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        var json = JsonSerializer.Serialize(update);
        var deserialized = JsonSerializer.Deserialize<SubjectUpdate>(json)!;

        // Act - apply the deserialized update (this is where the bug manifested:
        // Kind=Collection with a string JsonElement key caused ConvertIndexToInt to throw)
        target.ApplySubjectUpdate(deserialized, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Item1Updated", target.Lookup["myKey"].Name);
    }

    [Fact]
    public void WhenApplyingDictionaryPartialUpdateWithIntKeys_ThenExistingEntriesAreMatchedAndNewKeysAdded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var existingItem = new CycleTestNode(context) { Name = "Existing" };
        var source = new IntKeyNode(context)
        {
            Name = "Root",
            IntLookup = new Dictionary<int, CycleTestNode> { [1] = existingItem }
        };
        var target = new IntKeyNode(context)
        {
            Name = "Root",
            IntLookup = new Dictionary<int, CycleTestNode>
            {
                [1] = new(context) { Name = "Existing" }
            }
        };

        // Make a change - add key 2 and update key 1's name
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            existingItem.Name = "Updated";
            source.IntLookup = new Dictionary<int, CycleTestNode>
            {
                [1] = existingItem,
                [2] = new(context) { Name = "NewItem" }
            };
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal(2, target.IntLookup.Count);
        Assert.Equal("Updated", target.IntLookup[1].Name);
        Assert.Equal("NewItem", target.IntLookup[2].Name);
    }
}
