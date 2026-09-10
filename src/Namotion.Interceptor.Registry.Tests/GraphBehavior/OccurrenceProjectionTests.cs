using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests.GraphBehavior;

/// <summary>
/// The registry projects lifecycle edges occurrence-aware: a subject listed twice in one collection
/// has two parent entries carrying their collection indices, and a subject stored under two
/// dictionary keys has one entry per key. The projection is navigation only; reference counts and
/// release decisions live in the lifecycle.
/// </summary>
public class OccurrenceProjectionTests
{
    [Fact]
    public void WhenASubjectAppearsTwiceInOneCollection_ThenTheRegistryTracksBothOccurrences()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var duplicated = new Person { FirstName = "Dup" };

        // Act
        var root = new Person(context)
        {
            FirstName = "Root",
            Children = [duplicated, duplicated]
        };

        // Assert: two parent entries on the same property, distinguished by index.
        var registered = duplicated.TryGetRegisteredSubject()!;
        Assert.Equal(2, registered.Parents.Length);
        Assert.All(registered.Parents, parent => Assert.Equal(nameof(Person.Children), parent.Property.Name));
        Assert.Contains(registered.Parents, parent => Equals(parent.Index, 0));
        Assert.Contains(registered.Parents, parent => Equals(parent.Index, 1));

        var childrenProperty = root.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.Children))!;
        Assert.Equal(2, childrenProperty.Children.Length);
        Assert.All(childrenProperty.Children, child => Assert.Same(duplicated, child.Subject));

        Assert.Equal(2, duplicated.GetReferenceCount());
    }

    [Fact]
    public void WhenOneDuplicateOccurrenceIsRemoved_ThenTheSurvivorIsRenumberedAndTheSubjectStays()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var duplicated = new Person { FirstName = "Dup" };
        var root = new Person(context)
        {
            FirstName = "Root",
            Children = [duplicated, duplicated]
        };

        // Act
        root.Children = [duplicated];

        // Assert
        var registered = duplicated.TryGetRegisteredSubject()!;
        var parent = Assert.Single(registered.Parents);
        Assert.Equal(nameof(Person.Children), parent.Property.Name);
        Assert.Equal(0, (int)parent.Index!);

        var childrenProperty = root.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.Children))!;
        var child = Assert.Single(childrenProperty.Children);
        Assert.Equal(0, (int)child.Index!);

        Assert.Equal(1, duplicated.GetReferenceCount());
    }

    [Fact]
    public void WhenASubjectIsStoredUnderTwoDictionaryKeys_ThenEachKeyIsAProjectedOccurrence()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var group = new LightGroup(context);
        var light = new Light { Name = "L" };

        // Act
        group.LightsByName = new Dictionary<string, Light> { ["a"] = light, ["b"] = light };

        // Assert: one parent entry per key, and the keys are the projected indices.
        var registered = light.TryGetRegisteredSubject()!;
        Assert.Equal(2, registered.Parents.Length);
        Assert.All(registered.Parents, parent => Assert.Equal(nameof(LightGroup.LightsByName), parent.Property.Name));
        Assert.Contains(registered.Parents, parent => Equals(parent.Index, "a"));
        Assert.Contains(registered.Parents, parent => Equals(parent.Index, "b"));

        var dictionaryProperty = group.TryGetRegisteredSubject()!.TryGetProperty(nameof(LightGroup.LightsByName))!;
        Assert.Equal(2, dictionaryProperty.Children.Length);
        Assert.Contains(dictionaryProperty.Children, child => Equals(child.Index, "a"));
        Assert.Contains(dictionaryProperty.Children, child => Equals(child.Index, "b"));

        Assert.Equal(2, light.GetReferenceCount());
    }

    [Fact]
    public void WhenOneDictionaryKeyIsDropped_ThenTheSurvivingKeyKeepsItsOccurrence()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var group = new LightGroup(context);
        var light = new Light { Name = "L" };
        group.LightsByName = new Dictionary<string, Light> { ["a"] = light, ["b"] = light };

        // Act
        group.LightsByName = new Dictionary<string, Light> { ["b"] = light };

        // Assert
        var registered = light.TryGetRegisteredSubject()!;
        var parent = Assert.Single(registered.Parents);
        Assert.Equal("b", parent.Index);

        var dictionaryProperty = group.TryGetRegisteredSubject()!.TryGetProperty(nameof(LightGroup.LightsByName))!;
        var child = Assert.Single(dictionaryProperty.Children);
        Assert.Equal("b", child.Index);

        Assert.Equal(1, light.GetReferenceCount());
    }
}
