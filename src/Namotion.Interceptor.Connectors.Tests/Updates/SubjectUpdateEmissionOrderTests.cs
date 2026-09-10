using System.Reactive.Concurrency;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Characterization of the order in which applying an update emits changes, which downstream
/// consumers observe and forward. Applying a new child assigns it before populating it, so the
/// structural change announcing the child arrives before the child's own property changes. The set
/// of changes is not affected by that: the child is attached either way while it is populated, so
/// its writes were always observed. Only the position of the structural change moves, and with it
/// whether the child is reachable from the graph when its own values arrive.
/// </summary>
public class SubjectUpdateEmissionOrderTests
{
    [Fact]
    public void WhenAnUpdateCreatesAChild_ThenTheStructuralChangeIsEmittedBeforeTheChildsPropertyChanges()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(context)
        {
            FirstName = "John",
            Father = new Person { FirstName = "Bob", LastName = "Senior" }
        };
        var target = new Person(context);

        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        var child = target.Father;
        Assert.NotNull(child);

        var sequence = changes
            .Select(change => (ReferenceEquals(change.Property.Subject, target) ? "target"
                : ReferenceEquals(change.Property.Subject, child) ? "child"
                : "unknown") + "." + change.Property.Name)
            .ToArray();

        Assert.Equal(
            [
                "target.FirstName",
                "target.FullName",
                "target.Father",
                "child.FirstName",
                "child.FullName",
                "child.LastName",
                "child.FullName"
            ],
            sequence);

        // The structural change carries the child instance, so a consumer can resolve the child's
        // path from it before the child's own values arrive.
        var structuralChange = changes.Single(change => change.Property.Name == nameof(Person.Father));
        Assert.Same(child, structuralChange.GetNewValue<object?>());
    }

    [Fact]
    public void WhenAnUpdateCreatesAChild_ThenTheChildIsReachableFromItsParentWhileItsChangesAreEmitted()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(context)
        {
            FirstName = "John",
            Father = new Person { FirstName = "Bob", LastName = "Senior" }
        };
        var target = new Person(context);

        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        // Record parent counts at emission time rather than afterwards: a consumer resolving a path
        // sees the graph as it was when the change was handed to it, not as it ends up.
        var parentCountsAtEmission = new List<(IInterceptorSubject Subject, int ParentCount)>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => parentCountsAtEmission.Add(
                (change.Property.Subject, change.Property.Subject.GetParents().Length)));

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert: every change on the created child was emitted while an edge already held it, so a
        // consumer that resolves a path when it receives a change can resolve one for all of them.
        var child = target.Father;
        Assert.NotNull(child);

        var childEmissions = parentCountsAtEmission
            .Where(emission => ReferenceEquals(emission.Subject, child))
            .ToArray();

        Assert.NotEmpty(childEmissions);
        Assert.All(childEmissions, emission => Assert.Equal(1, emission.ParentCount));
    }
}
