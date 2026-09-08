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
    [InlineData(false)]
    [InlineData(true)]
    public void WhenARegisteredReadOnlyServerProjectionIsMirroredByAWritableClient_ThenTheClientReceivesItsState(bool partial)
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry());
        var projected = new Person { FirstName = "Ada" };
        projected.AttachToContext(InterceptorSubjectContext.Create().WithRegistry());
        var personProperty = source.TryGetRegisteredSubject()!.AddDerivedProperty("ProjectedPerson", typeof(Person), _ => projected);
        var nameProperty = source.TryGetRegisteredSubject()!.AddDerivedProperty("ProjectedName", typeof(string), _ => "Ada");
        var target = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry());
        Person? mirrored = new() { FirstName = "Old" };
        string? mirroredName = "Old";
        target.TryGetRegisteredSubject()!.AddProperty("ProjectedPerson", typeof(Person), _ => mirrored, (_, value) => mirrored = (Person?)value);
        target.TryGetRegisteredSubject()!.AddProperty("ProjectedName", typeof(string), _ => mirroredName, (_, value) => mirroredName = (string?)value);

        // Act
        var update = partial
            ? SubjectUpdate.CreatePartialUpdateFromChanges(source,
            [
                SubjectPropertyChange.Create<Person?>(personProperty.Reference, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, null, projected),
                SubjectPropertyChange.Create<string?>(nameProperty.Reference, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, null, "Ada")
            ], [])
            : SubjectUpdate.CreateCompleteUpdate(source, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Ada", mirroredName);
        Assert.NotNull(mirrored);
        Assert.Equal("Ada", mirrored.FirstName);
    }

    [Theory]
    [InlineData("object", false, false)]
    [InlineData("object", true, false)]
    [InlineData("collection", false, false)]
    [InlineData("collection", true, false)]
    [InlineData("dictionary", false, false)]
    [InlineData("dictionary", true, false)]
    [InlineData("object", false, true)]
    [InlineData("object", true, true)]
    [InlineData("collection", false, true)]
    [InlineData("collection", true, true)]
    [InlineData("dictionary", false, true)]
    [InlineData("dictionary", true, true)]
    public void WhenADetachedProjectionIsSelectedForAnUpdate_ThenCreationRejectsItUnlessFiltered(string shape, bool partial, bool filtered)
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry());
        var projected = new Person { FirstName = "Ada" };
        object value = shape switch
        {
            "collection" => new[] { projected },
            "dictionary" => new Dictionary<string, Person> { ["child"] = projected },
            _ => projected
        };
        var property = source.TryGetRegisteredSubject()!.AddDerivedProperty("Projection", value.GetType(), _ => value);
        var change = SubjectPropertyChange.Create<object?>(
            property.Reference, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, null, value);
        ISubjectUpdateProcessor[] processors = filtered ? [new ExcludeProjectionProcessor()] : [];
        SubjectUpdate CreateUpdate() => partial
            ? SubjectUpdate.CreatePartialUpdateFromChanges(source, [change], processors)
            : SubjectUpdate.CreateCompleteUpdate(source, processors);

        // Act & Assert
        if (filtered)
        {
            var update = CreateUpdate();
            Assert.DoesNotContain(update.Subjects.Values, properties => properties.ContainsKey("Projection"));
        }
        else
        {
            var exception = Assert.Throws<NotSupportedException>(CreateUpdate);
            Assert.Contains("Registry", exception.Message);
        }
        Assert.Null(projected.TryGetRegisteredSubject());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenARemovalHasNoSubjectPayload_ThenItRemovesTheItem(bool dictionary)
    {
        // Arrange
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry())
        {
            Children = [new Person()],
            Relationships = new Dictionary<string, Person> { ["child"] = new() }
        };
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new()
            {
                ["1"] = new()
                {
                    [dictionary ? "Relationships" : "Children"] = new()
                    {
                        Kind = dictionary ? SubjectPropertyUpdateKind.Dictionary : SubjectPropertyUpdateKind.Collection,
                        Operations = [new() { Action = SubjectCollectionOperationType.Remove, Index = dictionary ? "child" : 0 }],
                        Count = 0
                    }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Empty(dictionary ? (System.Collections.IEnumerable)target.Relationships! : target.Children);
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
    public void WhenATransientItemIsInsertedThenRemovedBeforeBatchCreation_ThenTheFinalRemovalNeedsNoPayload(bool dictionary)
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
        Assert.Empty(target.Children);
        Assert.Empty(target.Relationships!);
    }

    private sealed class ExcludeProjectionProcessor : ISubjectUpdateProcessor
    {
        public bool IsIncluded(RegisteredSubjectProperty property) => property.Name != "Projection";
    }
}
