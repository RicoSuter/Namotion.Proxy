using System.Reactive.Concurrency;
using System.Text.Json;
using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public class SubjectUpdateTests
{
    [Fact]
    public async Task WhenGeneratingCompleteSubjectDescription_ThenResultIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var father = new Person { FirstName = "Father" };
        var mother = new Person { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Father = father,
            Children = [child1, child2, child3]
        };

        var counter = new TransformCounter();

        // Act
        var completeSubjectUpdate = SubjectUpdate
            .CreateCompleteUpdate(person, [counter, JsonCamelCasePathProcessor.Instance]);

        // Assert
        // Property count varies based on context propagation; snapshot is authoritative
        Assert.True(counter.TransformPropertyCount > 0);

        await Verify(completeSubjectUpdate).DisableDateCounting();
    }

    [Fact]
    public async Task WhenGeneratingPartialSubjectDescription_ThenResultIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var father = new Person { FirstName = "Father" };
        var mother = new Person { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Father = father,
            Children = [child1, child2, child3]
        };

        var changes = new[]
        {
            SubjectPropertyChange.Create(new PropertyReference(person, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewPerson"),
            SubjectPropertyChange.Create(new PropertyReference(father, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewFather"),
            SubjectPropertyChange.Create(new PropertyReference(child1, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewChild1"),
            SubjectPropertyChange.Create(new PropertyReference(child3, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewChild3"),
        };

        var counter = new TransformCounter();

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, [counter, JsonCamelCasePathProcessor.Instance]);

        // Assert
        // Only changed properties are transformed in partial updates
        Assert.True(counter.TransformPropertyCount > 0);

        await Verify(partialSubjectUpdate).DisableDateCounting();
    }

    [Fact]
    public async Task WhenGeneratingPartialSubjectDescriptionWithTransformation_ThenResultIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var father = new Person { FirstName = "Father" };
        var mother = new Person { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Father = father,
            Children = [child1, child2, child3]
        };

        var changes = new[]
        {
            SubjectPropertyChange.Create(new PropertyReference(person, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewPerson"),
            SubjectPropertyChange.Create(new PropertyReference(father, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewFather"),
            SubjectPropertyChange.Create(new PropertyReference(child1, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewChild1"),
            SubjectPropertyChange.Create(new PropertyReference(child3, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewChild3"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes,
            [
                new MockProcessor(child1, child3),
                JsonCamelCasePathProcessor.Instance
            ]);

        // Assert
        await Verify(partialSubjectUpdate).DisableDateCounting();
    }

    public class MockProcessor : ISubjectUpdateProcessor
    {
        private readonly IInterceptorSubject _excluded;
        private readonly IInterceptorSubject _transform;

        public MockProcessor(IInterceptorSubject excluded, IInterceptorSubject transform)
        {
            _excluded = excluded;
            _transform = transform;
        }

        public bool IsIncluded(RegisteredSubjectProperty property)
        {
            return property.Parent.Subject != _excluded;
        }

        public SubjectUpdate TransformSubjectUpdate(IInterceptorSubject subject, SubjectUpdate update)
        {
            return update;
        }

        public SubjectPropertyUpdate TransformSubjectPropertyUpdate(RegisteredSubjectProperty property, SubjectPropertyUpdate update)
        {
            if (property.Parent.Subject == _transform)
            {
                update.Value = "TransformedValue";
            }

            return update;
        }
    }

    [Fact]
    public async Task WhenGeneratingPartialSubjectDescriptionForNonRoot_ThenResultIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };
        var father = new Person { FirstName = "Father", Children = [child1, child2, child3] };
        var mother = new Person { FirstName = "Mother" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Father = father,
        };

        var changes = new[]
        {
            SubjectPropertyChange.Create(new PropertyReference(person, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewPerson"), // ignored
            SubjectPropertyChange.Create(new PropertyReference(father, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewFather"),
            SubjectPropertyChange.Create(new PropertyReference(mother, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewMother"), // ignored
            SubjectPropertyChange.Create(new PropertyReference(child1, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewChild1"),
            SubjectPropertyChange.Create(new PropertyReference(child3, "FirstName"), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Old", "NewChild3"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            // TODO(perf): This method can probably made much faster in case of
            // non-root subjects (no need to create many objects)
            .CreatePartialUpdateFromChanges(father, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        await Verify(partialSubjectUpdate).DisableDateCounting();
    }

    [Fact]
    public async Task WhenGeneratingPartialSubjectDescription_ThenResultCollectionIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeSubscriptions()
            .WithRegistry();

        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Children = [child1, child2, child3]
        };

        // Act
        var changes = new List<SubjectPropertyChange>();
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        person.Mother = new Person { FirstName = "MyMother" };
        person.Children = person.Children.Union([new Person { FirstName = "Child4" }]).ToList(); // add child4
        person.Father = new Person { FirstName = "MyFather" };
        person.Mother.FirstName = "Jane";
        person.Children = person.Children.Skip(2).ToList(); // remove child1 and child2
        person.Children[1].FirstName = "John";

        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes.ToArray().AsSpan(), [JsonCamelCasePathProcessor.Instance]);

        var target = new Person(InterceptorSubjectContext.Create().WithRegistry())
        {
            Children = [new Person { FirstName = "Child1" }, new Person { FirstName = "Child2" }, new Person { FirstName = "Child3" }]
        };
        var roundtripUpdate = SubjectUpdate.CreatePartialUpdateFromChanges(person, changes.ToArray(), []);
        target.ApplySubjectUpdate(roundtripUpdate, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal(2, target.Children.Count);
        Assert.Equal("Child3", target.Children[0].FirstName);
        Assert.Equal("John", target.Children[1].FirstName);
        Assert.Equal("Jane", target.Mother!.FirstName);
        Assert.Equal("MyFather", target.Father!.FirstName);
        await Verify(partialSubjectUpdate).DisableDateCounting();
    }

    [Fact]
    public async Task WhenGeneratingPartialSubjectWithAttribute_ThenResultIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var father = new Person { FirstName = "Father" };
        var mother = new Person { FirstName = "Mother" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Father = father,
            Children = []
        };

        var attribute1 = mother.TryGetRegisteredSubject()!
            .TryGetProperty("FirstName")!
            .AddAttribute("Test1", typeof(int), _ => 42, null);

        var attribute2 = attribute1
            .AddAttribute("Test2", typeof(int), _ => 42, null);

        var changes = new[]
        {
            SubjectPropertyChange.Create(attribute2, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, 20, 21),
            SubjectPropertyChange.Create(attribute1, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, 10, 11),
            SubjectPropertyChange.Create(attribute1, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, 11, 12),
            SubjectPropertyChange.Create(attribute2, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, 20, 22),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        await Verify(partialSubjectUpdate).DisableDateCounting();
    }

    [Fact]
    public async Task WhenGeneratingPartialSubjectWithAttributeAndValue_ThenBothAreIncluded()
    {
        // Arrange
        // This test verifies that when BOTH an attribute change AND a value change
        // are included for the same property, BOTH should appear in the update.
        // Order: Attribute first, then value.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var mother = new Person { FirstName = "Mother" };
        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Children = []
        };

        var firstNameProperty = mother.TryGetRegisteredSubject()!
            .TryGetProperty("FirstName")!;

        var attribute = firstNameProperty
            .AddAttribute("Unit", typeof(string), _ => "meters", null);

        var timestamp = DateTimeOffset.UtcNow;
        var changes = new[]
        {
            // Attribute change comes first
            SubjectPropertyChange.Create(attribute, ChangeOrigin.Local, timestamp, null, "feet", "meters"),
            // Value change comes second - should NOT overwrite attribute
            SubjectPropertyChange.Create(new PropertyReference(mother, "FirstName"), ChangeOrigin.Local, timestamp, null, "Mother", "Jane"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        // The firstName property should have BOTH:
        // - kind: Value, value: "Jane"
        // - attributes: { unit: { kind: Value, value: "meters" } }
        await Verify(partialSubjectUpdate).DisableDateCounting();
    }

    [Fact]
    public async Task WhenGeneratingPartialSubjectWithValueThenAttribute_ThenBothAreIncluded()
    {
        // Arrange
        // Same as above but with REVERSE ORDER: value first, then attribute.
        // This tests that attribute changes are correctly added to existing value updates.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var mother = new Person { FirstName = "Mother" };
        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Children = []
        };

        var firstNameProperty = mother.TryGetRegisteredSubject()!
            .TryGetProperty("FirstName")!;

        var attribute = firstNameProperty
            .AddAttribute("Unit", typeof(string), _ => "meters", null);

        var timestamp = DateTimeOffset.UtcNow;
        var changes = new[]
        {
            // Value change comes FIRST
            SubjectPropertyChange.Create(new PropertyReference(mother, "FirstName"), ChangeOrigin.Local, timestamp, null, "Mother", "Jane"),
            // Attribute change comes SECOND - should be added to existing update
            SubjectPropertyChange.Create(attribute, ChangeOrigin.Local, timestamp, null, "feet", "meters"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        // The firstName property should have BOTH (same as attribute-first test):
        // - kind: Value, value: "Jane"
        // - attributes: { unit: { kind: Value, value: "meters" } }
        await Verify(partialSubjectUpdate).DisableDateCounting();
    }

    [Fact]
    public async Task WhenGeneratingPartialSubjectWithOnlyValueChange_ThenAttributesNotIncluded()
    {
        // Arrange
        // When ONLY a value changes (no attribute changes), unchanged attributes
        // should NOT be included in the partial update.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var mother = new Person { FirstName = "Mother" };
        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Children = []
        };

        // Add an attribute but DON'T include it in the changes
        var firstNameProperty = mother.TryGetRegisteredSubject()!
            .TryGetProperty("FirstName")!;
        firstNameProperty.AddAttribute("Unit", typeof(string), _ => "meters", null);

        var timestamp = DateTimeOffset.UtcNow;
        var changes = new[]
        {
            // Only value change - no attribute change
            SubjectPropertyChange.Create(new PropertyReference(mother, "FirstName"), ChangeOrigin.Local, timestamp, null, "Mother", "Jane"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        // The firstName property should have:
        // - kind: Value, value: "Jane"
        // - NO attributes (because they didn't change)
        await Verify(partialSubjectUpdate).DisableDateCounting();
    }

    [Fact]
    public async Task WhenAssigningNewSubjectToNestedProperty_ThenPartialUpdateContainsOnlyPathAndNewSubject()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var mother = new Person { FirstName = "Mother" };
        var root = new Person(context)
        {
            FirstName = "Root",
            Mother = mother
        };

        var changes = new List<SubjectPropertyChange>();
        using var _ = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changes.Add(change));

        // Act: Assign a new Person to mother.Father (nested property)
        mother.Father = new Person { FirstName = "NewFather" };

        // Log what changes we received for debugging
        var changesSummary = changes.Select(c => $"{c.Property.Subject.GetType().Name}.{c.Property.Name}").ToList();

        var partialUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes.ToArray(), [JsonCamelCasePathProcessor.Instance]);

        // The partial update should contain:
        // 1. root: only the path reference to mother (mother property)
        // 2. mother: only the father property (the actual change)
        // 3. newFather: all properties of the new subject
        //
        // It should NOT contain other properties like root.firstName, root.children, etc.
        await Verify(new { Changes = changesSummary, Update = partialUpdate }).DisableDateCounting();
    }

    [Fact]
    public async Task WhenAssigningNewSubjectWithRootReference_ThenPartialUpdateShouldNotIncludeFullAncestorProperties()
    {
        // Arrange
        // This test reproduces a bug where assigning a new subject that has a Root property
        // (which navigates back up the tree) causes the partial update to include
        // ALL properties of ALL ancestors, not just the path references.

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var person = new PersonWithRoot { FirstName = "Person" };
        var root = new PersonRoot(context)
        {
            Name = "Root",
            Person = person
        };

        var changes = new List<SubjectPropertyChange>();
        using var _ = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changes.Add(change));

        // Act: Assign a new PersonWithRoot to person.Father
        // The new father has a Root property that references back to PersonRoot
        // This creates a circular reference: root -> person -> father -> root
        person.Father = new PersonWithRoot { FirstName = "NewFather", Root = root };

        var changesSummary = changes.Select(c => $"{c.Property.Subject.GetType().Name}.{c.Property.Name}").ToList();

        var partialUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes.ToArray(), [JsonCamelCasePathProcessor.Instance]);

        // Expected behavior:
        // - root (PersonRoot): only path reference to person (person property)
        // - person (PersonWithRoot): only the father property (the actual change)
        // - newFather (PersonWithRoot): all properties INCLUDING the Root reference
        //
        // Bug: The Root property on newFather causes ProcessSubjectComplete to traverse
        // back up to PersonRoot, which then includes ALL properties of PersonRoot and
        // the full tree, not just the minimal path references.

        await Verify(new { Changes = changesSummary, Update = partialUpdate }).DisableDateCounting();
    }

    [Fact]
    public async Task WhenUpdateContainsIncrementalChanges_ThenAllOfThemAreApplied()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        var person = new Person(context)
        {
            FirstName = "Parent",
            Children = [child1, child2]
        };

        var changes = new List<SubjectPropertyChange>();
        using var _ = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        // Act
        var date = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using (SubjectChangeContext.WithChangedTimestamp(date))
        {
            // Modify Child2 properties
            child2.FirstName = "Child2.0";

            // Remove Child1 and move Child2 to first position, previous changes should be kept
            person.Children = person.Children.Where(c => c.FirstName != "Child1").ToList();
        }

        var partialSubjectUpdate1 = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes.ToArray(), [JsonCamelCasePathProcessor.Instance]);

        changes.Clear();
        using (SubjectChangeContext.WithChangedTimestamp(date.AddSeconds(1)))
        {
            child2.FirstName = "Child2.1";
        }

        var partialSubjectUpdate2 = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes.ToArray(), [JsonCamelCasePathProcessor.Instance]);

        changes.Clear();
        using (SubjectChangeContext.WithChangedTimestamp(date.AddSeconds(2)))
        {
            person.FirstName = "ParentNext";
        }

        var partialSubjectUpdate3 = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes.ToArray(), [JsonCamelCasePathProcessor.Instance]);

        // Assert
        await Verify(new
        {
            Update1 = partialSubjectUpdate1,
            Update2 = partialSubjectUpdate2,
            Update3 = partialSubjectUpdate3
        });
    }

    [Fact]
    public void WhenProcessorExcludesAttribute_ThenCompleteUpdateOmitsAttribute()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var person = new Person(context) { FirstName = "Test" };

        var firstNameProperty = person.TryGetRegisteredSubject()!
            .TryGetProperty("FirstName")!;

        firstNameProperty
            .AddAttribute("Included", typeof(int), _ => 42, null);

        var excludedAttribute = firstNameProperty
            .AddAttribute("Excluded", typeof(int), _ => 99, null);

        var processor = new AttributeExclusionProcessor(excludedAttribute);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(person, [processor]);

        // Assert
        var firstNameUpdate = update.Subjects!["1"]["FirstName"];
        Assert.NotNull(firstNameUpdate.Attributes);
        Assert.Contains("Included", firstNameUpdate.Attributes.Keys);
        Assert.DoesNotContain("Excluded", firstNameUpdate.Attributes.Keys);
    }

    [Fact]
    public void WhenProcessorExcludesAttribute_ThenPartialUpdateOmitsAttribute()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var person = new Person(context) { FirstName = "Test" };

        var firstNameProperty = person.TryGetRegisteredSubject()!
            .TryGetProperty("FirstName")!;

        var includedAttribute = firstNameProperty
            .AddAttribute("Included", typeof(int), _ => 42, null);

        var excludedAttribute = firstNameProperty
            .AddAttribute("Excluded", typeof(int), _ => 99, null);

        var processor = new AttributeExclusionProcessor(excludedAttribute);

        var changes = new[]
        {
            SubjectPropertyChange.Create(includedAttribute, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, 40, 42),
            SubjectPropertyChange.Create(excludedAttribute, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, 90, 99),
        };

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(person, changes, [processor]);

        // Assert
        var firstNameUpdate = update.Subjects!["1"]["FirstName"];
        Assert.NotNull(firstNameUpdate.Attributes);
        Assert.Contains("Included", firstNameUpdate.Attributes.Keys);
        Assert.DoesNotContain("Excluded", firstNameUpdate.Attributes.Keys);
    }
}

public class AttributeExclusionProcessor : ISubjectUpdateProcessor
{
    private readonly RegisteredSubjectProperty _excludedAttribute;

    public AttributeExclusionProcessor(RegisteredSubjectProperty excludedAttribute)
    {
        _excludedAttribute = excludedAttribute;
    }

    public bool IsIncluded(RegisteredSubjectProperty property)
    {
        return property != _excludedAttribute;
    }
}

public class TransformCounter : ISubjectUpdateProcessor
{
    private readonly HashSet<SubjectUpdate> _transformedSubjects = [];
    private readonly HashSet<SubjectPropertyUpdate> _transformedProperties = [];

    public int TransformSubjectCount { get; private set; } = 0;

    public int TransformPropertyCount { get; private set; } = 0;

    public SubjectUpdate TransformSubjectUpdate(IInterceptorSubject subject, SubjectUpdate update)
    {
        if (!_transformedSubjects.Add(update))
            throw new InvalidOperationException("Already added.");

        TransformSubjectCount++;
        return update;
    }

    public SubjectPropertyUpdate TransformSubjectPropertyUpdate(RegisteredSubjectProperty property, SubjectPropertyUpdate update)
    {
        if (!_transformedProperties.Add(update))
            throw new InvalidOperationException("Already added.");

        TransformPropertyCount++;
        return update;
    }
}
