using System.Text.Json;
using Namotion.Interceptor.AspNetCore.Extensions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests;

public class SubjectRegistryTests
{
    [Fact]
    public Task WhenTwoChildrenAreAttachedSequentially_ThenWeHaveThreeAttaches()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => handler);

        // Act
        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = new Person
            {
                FirstName = "Mother",
                Mother = new Person
                {
                    FirstName = "Grandmother"
                }
            }
        };

        // Assert
        var registry = context.GetService<ISubjectRegistry>();
        Assert.Equal(3, registry.KnownSubjects.Count());

        return Verify(handler.GetEvents());
    }

    [Fact]
    public Task WhenRemovingSubjectWithChild_ThenBothDetach()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => handler);

        // Act
        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = new Person
            {
                FirstName = "Mother",
                Mother = new Person
                {
                    FirstName = "Grandmother"
                }
            }
        };

        person.Mother = null;

        // Assert
        var registry = context.GetService<ISubjectRegistry>();
        Assert.Single(registry.KnownSubjects);

        return Verify(handler.GetEvents());
    }

    [Fact]
    public void WhenAddingTransitiveProxies_ThenAllAreAvailable()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

        // Act
        var grandmother = new Person
        {
            FirstName = "Grandmother"
        };

        var mother = new Person
        {
            FirstName = "Mother",
            Mother = grandmother
        };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother
        };

        // Assert
        Assert.Equal(3, registry.KnownSubjects.Count);
        Assert.Contains(person, registry.KnownSubjects.Keys);
        Assert.Contains(mother, registry.KnownSubjects.Keys);
        Assert.Contains(grandmother, registry.KnownSubjects.Keys);
    }

    [Fact]
    public void WhenRemovingMiddleElement_ThenChildrenAreAlsoRemoved()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

        // Act
        var grandmother = new Person
        {
            FirstName = "Grandmother"
        };

        var mother = new Person
        {
            FirstName = "Mother",
            Mother = grandmother
        };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother
        };

        mother.Mother = null;

        // Assert
        Assert.Equal(2, registry.KnownSubjects.Count());
        Assert.Contains(person, registry.KnownSubjects.Keys);
        Assert.Contains(mother, registry.KnownSubjects.Keys);
        Assert.DoesNotContain(grandmother, registry.KnownSubjects.Keys);
    }

    [Fact]
    public async Task WhenConvertingToJson_ThenGraphIsPreserved()
    {
        // TODO: Move to Namotion.Interceptor.AspNetCore.Tests
        
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents()
            .WithRegistry();

        // Act
        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = new Person
            {
                FirstName = "Mother",
                Mother = new Person
                {
                    FirstName = "Grandmother"
                }
            }
        };
        
        // Assert
        var jsonSerializerOptions = new JsonSerializerOptions(JsonSerializerOptions.Default)
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var property = person.Mother.Mother
            .TryGetRegisteredProperty("FirstName")!;
        
        var path = property.Reference.GetJsonPath(jsonSerializerOptions);
        
        Assert.Equal("mother.mother.firstName", path);
        await Verify(person.ToJsonObject(jsonSerializerOptions).ToJsonString(jsonSerializerOptions));
    }

    [Fact]
    public async Task WhenChangingCollection_ThenIndexAreCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
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
        person.Children = person.Children.Union([new Person { FirstName = "Child4" }]).ToArray(); // add child4
        person.Children = person.Children.Skip(2).ToArray(); // remove child1 and child2
        
        // Assert
        var children = person
            .TryGetRegisteredSubject()?
            .TryGetProperty(nameof(Person.Children))?
            .Children
            .Select(c => new
            {
                Index = c.Index,
                Subject = c.Subject is Person p ? p.FirstName : "n/a"
            });

        await Verify(children).DisableDateCounting();
    }
    
    [Fact]
    public async Task WhenCreatingSubjectWithInheritance_ThenAllPropertiesAreAvailable()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var teacher = new Teacher(context);

        // Act
        var properties = teacher.TryGetRegisteredSubject()!.Properties;
        
        // Assert
        await Verify(properties.Select(p => p.Name));
    }

    [Fact]
    public void WhenRemovingMultipleCollectionItems_ThenNoChildrenAreLost()
    {
        // Regression test for memory leak: forward-order removal in recursive detach
        // caused IndexOf to fail because renumbered indices didn't match the lifecycle event indices.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var children = Enumerable.Range(0, 10)
            .Select(i => new Person { FirstName = $"Child{i}" })
            .ToArray();

        var person = new Person(context) { Children = children };

        // Act
        person.Children = children[8..];

        // Assert
        var registeredChildren = person
            .TryGetRegisteredSubject()?
            .TryGetProperty(nameof(Person.Children))?
            .Children;

        Assert.NotNull(registeredChildren);
        Assert.Equal(2, registeredChildren.Value.Length);
        Assert.Equal("Child8", ((Person)registeredChildren.Value[0].Subject).FirstName);
        Assert.Equal(0, registeredChildren.Value[0].Index);
        Assert.Equal("Child9", ((Person)registeredChildren.Value[1].Subject).FirstName);
        Assert.Equal(1, registeredChildren.Value[1].Index);
    }

    [Fact]
    public void WhenRemovingCollectionItems_ThenParentsAndChildrenIndicesAreConsistent()
    {
        // Regression test: old code renumbered Children indices but never updated Parents,
        // causing path resolution mismatches.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child1 = new Person { FirstName = "A" };
        var child2 = new Person { FirstName = "B" };
        var child3 = new Person { FirstName = "C" };

        var person = new Person(context)
        {
            Children = [child1, child2, child3]
        };

        // Act
        person.Children = [child1, child3];

        // Assert
        var childrenProp = person.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(Person.Children))!;

        Assert.Same(child1, childrenProp.Children[0].Subject);
        Assert.Equal(0, childrenProp.Children[0].Index);
        Assert.Same(child3, childrenProp.Children[1].Subject);
        Assert.Equal(1, childrenProp.Children[1].Index);

        var child1Parents = child1.TryGetRegisteredSubject()!.Parents;
        Assert.Single(child1Parents);
        Assert.Equal(0, child1Parents[0].Index); // position 0 in [A, C]

        var child3Parents = child3.TryGetRegisteredSubject()!.Parents;
        Assert.Single(child3Parents);
        Assert.Equal(1, child3Parents[0].Index); // position 1 in [A, C]
    }

    [Fact]
    public void WhenReorderingCollection_ThenIndicesMatchLiveCollection()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child1 = new Person { FirstName = "A" };
        var child2 = new Person { FirstName = "B" };
        var child3 = new Person { FirstName = "C" };

        var person = new Person(context)
        {
            Children = [child1, child2, child3]
        };

        // Act
        person.Children = [child3, child2, child1];

        // Assert
        var childrenProp = person.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(Person.Children))!;

        Assert.Equal(3, childrenProp.Children.Length);

        var childBySubject = childrenProp.Children.ToDictionary(c => ((Person)c.Subject).FirstName!, c => c.Index);
        Assert.Equal(0, childBySubject["C"]); // child3 now at position 0
        Assert.Equal(1, childBySubject["B"]); // child2 still at position 1
        Assert.Equal(2, childBySubject["A"]); // child1 now at position 2

        // Parents should also match
        Assert.Equal(2, child1.TryGetRegisteredSubject()!.Parents[0].Index);
        Assert.Equal(1, child2.TryGetRegisteredSubject()!.Parents[0].Index);
        Assert.Equal(0, child3.TryGetRegisteredSubject()!.Parents[0].Index);
    }

    [Fact]
    public void WhenInsertingInMiddleOfCollection_ThenIndicesAreCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child1 = new Person { FirstName = "A" };
        var child3 = new Person { FirstName = "C" };

        var person = new Person(context)
        {
            Children = [child1, child3]
        };

        // Act
        var child2 = new Person { FirstName = "B" };
        person.Children = [child1, child2, child3];

        // Assert
        var childrenProp = person.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(Person.Children))!;

        Assert.Equal(3, childrenProp.Children.Length);

        var childBySubject = childrenProp.Children.ToDictionary(c => ((Person)c.Subject).FirstName!, c => c.Index);
        Assert.Equal(0, childBySubject["A"]);
        Assert.Equal(1, childBySubject["B"]);
        Assert.Equal(2, childBySubject["C"]);

        // All parent indices should match live positions
        Assert.Equal(0, child1.TryGetRegisteredSubject()!.Parents[0].Index);
        Assert.Equal(1, child2.TryGetRegisteredSubject()!.Parents[0].Index);
        Assert.Equal(2, child3.TryGetRegisteredSubject()!.Parents[0].Index); // updated from 1 to 2
    }

    [Fact]
    public void WhenTwoEqualButDistinctSubjectsAreAttached_ThenBothAreRegisteredSeparately()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var first = new ValueEqualitySubject { Name = "same" };
        var second = new ValueEqualitySubject { Name = "same" };

        // Act
        ((IInterceptorSubject)first).Context.AddFallbackContext(context);
        ((IInterceptorSubject)second).Context.AddFallbackContext(context);

        // Assert
        var registry = context.GetService<ISubjectRegistry>();
        Assert.Equal(2, registry.KnownSubjects.Count);
        Assert.Same(first, registry.TryGetRegisteredSubject(first)?.Subject);
        Assert.Same(second, registry.TryGetRegisteredSubject(second)?.Subject);
    }

    [Fact]
    public void WhenTwoEqualButDistinctSubjectsShareACollection_ThenEachKeepsItsOwnPosition()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var first = new ValueEqualitySubject { Name = "same" };
        var second = new ValueEqualitySubject { Name = "same" };
        var parent = new ValueEqualitySubject(context) { Children = [first, second] };

        // Act: a reorder refreshes every child's position against the live collection.
        parent.Children = [second, first];

        // Assert
        Assert.Equal(0, second.TryGetRegisteredSubject()!.Parents[0].Index);
        Assert.Equal(1, first.TryGetRegisteredSubject()!.Parents[0].Index);
    }
    [Fact]
    public void WhenReplacingAChildWithAnEqualInstance_ThenOnlyTheReplacementRemainsRegistered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var first = new ValueEqualitySubject { Name = "same" };
        var second = new ValueEqualitySubject { Name = "same" };
        var parent = new ValueEqualitySubject(context) { Children = [first] };

        // Act
        parent.Children = [second];

        // Assert
        var registry = context.GetService<ISubjectRegistry>();
        Assert.Null(registry.TryGetRegisteredSubject(first));
        Assert.Same(second, registry.TryGetRegisteredSubject(second)!.Subject);
        Assert.Same(second, Assert.Single(parent.TryGetRegisteredProperty(nameof(parent.Children))!.Children).Subject);
    }
}

/// <summary>
/// A subject whose <see cref="object.Equals(object?)"/> and <see cref="object.GetHashCode"/> compare by value,
/// which is legal for a hand-written subject and must not merge distinct graph nodes.
/// </summary>
[InterceptorSubject]
public partial class ValueEqualitySubject
{
    public ValueEqualitySubject()
    {
        Children = [];
    }

    public partial string? Name { get; set; }

    public partial ValueEqualitySubject[] Children { get; set; }

    public override bool Equals(object? obj) => obj is ValueEqualitySubject other && other.Name == Name;

    public override int GetHashCode() => Name?.GetHashCode() ?? 0;
}
