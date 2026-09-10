using System.Collections.Immutable;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

/// <summary>
/// Covers the consequences of classifying a <c>Nullable&lt;T&gt;</c> structural property by its
/// underlying type, beyond the attach and detach callbacks exercised in
/// <see cref="LifecycleInterceptorTests"/>: that the children reach the registry with their
/// indices, that the connector update path emits the property as a collection rather than a
/// scalar value, and that a default struct value fails fast instead of being read as empty.
/// </summary>
public class NullableStructuralPropertyTests
{
    [Fact]
    public void WhenANullableStructuralPropertyIsAssigned_ThenItsChildrenAreRegisteredWithTheirIndices()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var holder = new NullableStructuralHolder(context);
        var car1 = new Car { Name = "Car1" };
        var car2 = new Car { Name = "Car2" };

        // Act
        holder.ImmutableCars = ImmutableArray.Create(car1, car2);

        // Assert
        var children = holder
            .TryGetRegisteredSubject()!
            .TryGetProperty(nameof(NullableStructuralHolder.ImmutableCars))!
            .Children;

        Assert.Equal(2, children.Length);
        Assert.Equal([0, 1], children.Select(child => child.Index).Cast<int>().ToArray());
        Assert.Equal([car1, car2], children.Select(child => child.Subject).ToArray());
    }

    [Fact]
    public void WhenANullableStructuralPropertyIsSerialized_ThenItEmitsAsACollection()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var holder = new NullableStructuralHolder(context)
        {
            ImmutableCars = ImmutableArray.Create(new Car { Name = "Car1" })
        };

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(holder, []);

        // Assert
        // Before the classification fix this emitted Kind=Value carrying the raw boxed collection,
        // which leaks subject children into the scalar value channel.
        var properties = update.Subjects[update.Root!];
        Assert.Equal(
            SubjectPropertyUpdateKind.Collection,
            properties[nameof(NullableStructuralHolder.ImmutableCars)].Kind);
    }

    [Fact]
    public void WhenAssigningADefaultStructCollectionToANullableStructuralProperty_ThenItThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var holder = new NullableStructuralHolder(context);

        // Act & Assert
        // A default ImmutableArray cannot be enumerated, so the structural scan surfaces it at the
        // assignment rather than tolerating it as empty. The non-nullable form already behaves this
        // way, and null remains how the nullable form expresses "not set".
        Assert.Throws<InvalidOperationException>(() => holder.ImmutableCars = default(ImmutableArray<Car>));
    }
}
