using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public class SubjectUpdateReferenceIntegrityTests
{
    [Theory]
    [InlineData("object", false)]
    [InlineData("object", true)]
    [InlineData("collection", false)]
    [InlineData("collection", true)]
    [InlineData("dictionary", false)]
    [InlineData("dictionary", true)]
    public void WhenABatchReferencesASubjectThatLeftTheGraph_ThenCreationOmitsTheReferencingProperty(string shape, bool filtered)
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry()) { FirstName = "Root" };
        var removed = new Person { FirstName = "Ada" };
        var propertyName = shape switch
        {
            "collection" => nameof(Person.Children),
            "dictionary" => nameof(Person.Relationships),
            _ => nameof(Person.Father)
        };
        object? empty = shape switch
        {
            "collection" => new List<Person>(),
            "dictionary" => new Dictionary<string, Person>(),
            _ => null
        };
        object? referencing = shape switch
        {
            "collection" => new List<Person> { removed },
            "dictionary" => new Dictionary<string, Person> { ["child"] = removed },
            _ => removed
        };
        var property = source.TryGetRegisteredProperty(propertyName)!;
        property.SetValue(empty);
        property.SetValue(referencing);
        var timestamp = DateTimeOffset.UtcNow;
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<object?>(property.Reference, ChangeOrigin.Local, timestamp, null, empty, referencing),
            SubjectPropertyChange.Create<string?>(new PropertyReference(source, nameof(Person.FirstName)),
                ChangeOrigin.Local, timestamp, null, null, "Root")
        ];
        property.SetValue(empty); // the referenced subject leaves the graph while the batch still names it
        ISubjectUpdateProcessor[] processors = filtered ? [new ExcludePropertyProcessor(propertyName)] : [];

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, processors);

        // Assert
        Assert.Null(removed.TryGetRegisteredSubject());
        Assert.DoesNotContain(update.Subjects.Values, properties => properties.ContainsKey(propertyName));
        Assert.Equal("Root", update.Subjects[update.Root][nameof(Person.FirstName)].Value);
    }

    [Fact]
    public void WhenACompleteUpdateIsCreatedForAModelWithADetachedProjection_ThenTheSnapshotIsBuiltAndApplied()
    {
        // Arrange: the WebSocket welcome handshake builds exactly this snapshot inside a generic catch
        // that disposes the connection, so a throw here would drop every client of a model whose
        // projection resolves after attachment, right after Hello, forever.
        var source = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry())
        {
            FirstName = "Ada",
            Children = [new Person { FirstName = "Grace" }]
        };
        Person? projected = null;
        source.TryGetRegisteredSubject()!.AddDerivedProperty("Projection", typeof(Person), _ => projected);
        projected = new Person { FirstName = "Projected" };
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry());
        Person? mirrored = new() { FirstName = "Existing" };
        target.TryGetRegisteredSubject()!.AddProperty("Projection", typeof(Person),
            _ => mirrored, (_, value) => mirrored = (Person?)value);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Null(projected.TryGetRegisteredSubject());
        Assert.Equal("Ada", target.FirstName);
        Assert.Equal("Grace", Assert.Single(target.Children).FirstName);
        Assert.Equal("Existing", mirrored!.FirstName);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WhenAnItemReferencesAMissingSubject_ThenApplyReportsFailureAndPreservesTheCollection(bool dictionary, bool insert)
    {
        // Arrange
        var child = new Person { FirstName = "Old" };
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry())
        {
            Children = [child],
            Relationships = new Dictionary<string, Person> { ["child"] = child }
        };
        var originalChildren = target.Children;
        var originalRelationships = target.Relationships;
        object index = dictionary ? "child" : 0;
        var propertyUpdate = new SubjectPropertyUpdate
        {
            Kind = dictionary ? SubjectPropertyUpdateKind.Dictionary : SubjectPropertyUpdateKind.Collection,
            Count = 1,
            Operations = insert ? [new SubjectCollectionOperation { Action = SubjectCollectionOperationType.Insert, Index = index, Id = "missing" }] : null,
            Items = insert ? null : [new SubjectPropertyItemUpdate { Index = index, Id = "missing" }]
        };
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new()
            {
                ["1"] = new()
                {
                    [dictionary ? "Relationships" : "Children"] = propertyUpdate,
                    ["FirstName"] = new() { Kind = SubjectPropertyUpdateKind.Value, Value = "Updated" }
                }
            }
        };

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));

        // Assert
        Assert.Contains("missing", exception.Message);
        Assert.Same(originalChildren, target.Children);
        Assert.Same(originalRelationships, target.Relationships);
        Assert.Same(child, Assert.Single(target.Children));
        Assert.Same(child, target.Relationships!["child"]);
        Assert.Equal("Old", child.FirstName);
        Assert.Equal("Updated", target.FirstName);
    }

    [Fact]
    public void WhenABatchUpdatesAChildAndThenClearsItsReference_ThenTheClearedReferenceCarriesNoStaleId()
    {
        // Arrange: the child's own change builds the path back to the root, which stamps the parent
        // reference with the child's ID, and the clear then reuses that same property update.
        var source = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry());
        var mother = new Person { FirstName = "Ada" };
        source.Mother = mother;
        var timestamp = DateTimeOffset.UtcNow;
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<string?>(new PropertyReference(mother, nameof(Person.FirstName)),
                ChangeOrigin.Local, timestamp, null, "Ada", "Grace"),
            SubjectPropertyChange.Create<Person?>(new PropertyReference(source, nameof(Person.Mother)),
                ChangeOrigin.Local, timestamp, null, mother, null)
        ];
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry())
        {
            Mother = new Person { FirstName = "Existing" }
        };

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Null(update.Subjects[update.Root][nameof(Person.Mother)].Id);
        Assert.Null(target.Mother);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAnEarlierReferenceIsReplacedBeforeBatchCreation_ThenOnlyTheFinalReferenceIsRequired(bool clear)
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry());
        var first = new Person { FirstName = "First" };
        var final = clear ? null : new Person { FirstName = "Final" };
        source.Father = first;
        source.Father = final;
        var property = new PropertyReference(source, nameof(Person.Father));
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<Person?>(property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, null, first),
            SubjectPropertyChange.Create<Person?>(property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, first, final)
        ];
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry()) { Father = new Person() };

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Null(first.TryGetRegisteredSubject());
        if (clear)
        {
            Assert.Null(update.Subjects[update.Root][nameof(Person.Father)].Id);
            Assert.Null(target.Father);
        }
        else
        {
            Assert.Equal("Final", target.Father!.FirstName);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenATransientItemIsInsertedThenRemovedBeforeBatchCreation_ThenTheEmptyResultCarriesNoTransientPayload(bool dictionary)
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry());
        var transient = new Person { FirstName = "Transient" };
        object empty = dictionary ? new Dictionary<string, Person>() : new List<Person>();
        object inserted = dictionary
            ? new Dictionary<string, Person> { ["child"] = transient }
            : new List<Person> { transient };
        var property = source.TryGetRegisteredProperty(dictionary ? nameof(Person.Relationships) : nameof(Person.Children))!;
        property.SetValue(inserted);
        property.SetValue(empty);
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<object?>(property.Reference, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, empty, inserted),
            SubjectPropertyChange.Create<object?>(property.Reference, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, inserted, empty)
        ];
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry()) { Relationships = new() };

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Null(transient.TryGetRegisteredSubject());
        Assert.Equal(update.Root, Assert.Single(update.Subjects).Key);
        var propertyUpdate = update.Subjects[update.Root][property.Name];
        Assert.Equal(dictionary ? SubjectPropertyUpdateKind.Dictionary : SubjectPropertyUpdateKind.Collection, propertyUpdate.Kind);
        Assert.Equal(0, propertyUpdate.Count);
        Assert.Empty(propertyUpdate.Operations ?? []);
        Assert.Empty(propertyUpdate.Items ?? []);
        Assert.Empty(target.Children);
        Assert.Empty(target.Relationships!);
    }

    private sealed class ExcludePropertyProcessor(string propertyName) : ISubjectUpdateProcessor
    {
        public bool IsIncluded(RegisteredSubjectProperty property) => property.Name != propertyName;
    }
}
