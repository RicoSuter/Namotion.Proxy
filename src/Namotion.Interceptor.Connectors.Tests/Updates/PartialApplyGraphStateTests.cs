using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// An applier assigns a newly created child into the parent graph before populating it, because the
/// population is registry-driven and the registry only holds attached subjects. A throw part way
/// through therefore leaves the child in the graph, held by the edge that put it there and released
/// with it. There is no separate cleanup obligation, because there is no attachment the graph does
/// not already own.
///
/// How much of the failed update survives differs by shape, and both cases are pinned here. A single
/// object property is assigned per child, so a throw leaves one partially populated child. A
/// collection is assigned as a whole before any of its items are populated, so a throw leaves every
/// item of that collection attached, the one being populated partially filled and the rest empty.
/// </summary>
public class PartialApplyGraphStateTests
{
    [Fact]
    public void WhenApplyingANewChildThrows_ThenThePartiallyAppliedChildStaysInTheGraphAndIsReleasedWithItsEdge()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(context) { FirstName = "John", Father = new Person { FirstName = "Bob" } };
        var target = new Person(context);

        var registry = context.GetService<ISubjectRegistry>();
        var knownBefore = registry.KnownSubjects.Count;

        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        // Act: fail after the newly created father has been assigned but before it is fully populated.
        var exception = Record.Exception(() => target.ApplySubjectUpdate(
            update,
            DefaultSubjectFactory.Instance,
            ChangeOrigin.Local,
            (property, _) =>
            {
                if (property.Subject != target && property.Name == nameof(Person.FirstName))
                {
                    throw new InvalidOperationException("apply failed");
                }
            }));

        // Assert: the child is ordinary graph state, held by its edge rather than by an anchor.
        Assert.NotNull(exception);

        var father = target.Father;
        Assert.NotNull(father);
        Assert.Null(father.FirstName);
        Assert.NotEmpty(father.GetParents());
        Assert.Equal(knownBefore + 1, registry.KnownSubjects.Count);

        // Act: remove the edge that holds it.
        target.Father = null;

        // Assert: reachability releases it, so nothing had to be released by hand.
        Assert.Null(father.TryGetContext());
        Assert.Equal(knownBefore, registry.KnownSubjects.Count);
    }

    [Fact]
    public void WhenApplyingACollectionThrows_ThenEveryItemStaysInTheGraphAndIsReleasedWithTheCollection()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(context)
        {
            FirstName = "John",
            Children = [new Person { FirstName = "First" }, new Person { FirstName = "Second" }]
        };
        var target = new Person(context);

        var registry = context.GetService<ISubjectRegistry>();
        var knownBefore = registry.KnownSubjects.Count;

        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        // Act: fail while populating the first item, after the whole collection was assigned.
        var exception = Record.Exception(() => target.ApplySubjectUpdate(
            update,
            DefaultSubjectFactory.Instance,
            ChangeOrigin.Local,
            (property, _) =>
            {
                if (property.Subject != target && property.Name == nameof(Person.FirstName))
                {
                    throw new InvalidOperationException("apply failed");
                }
            }));

        // Assert: the assignment covers the whole collection, so the failure leaves every item in
        // the graph, not just the one that was being populated.
        Assert.NotNull(exception);
        Assert.Equal(2, target.Children.Count);
        Assert.All(target.Children, child => Assert.Null(child.FirstName));
        Assert.All(target.Children, child => Assert.NotNull(child.TryGetContext()));
        Assert.Equal(knownBefore + 2, registry.KnownSubjects.Count);

        var children = target.Children.ToArray();

        // Act: remove the edge that holds them.
        target.Children = [];

        // Assert: reachability releases the whole set, so nothing had to be released by hand.
        Assert.All(children, child => Assert.Null(child.TryGetContext()));
        Assert.Equal(knownBefore, registry.KnownSubjects.Count);
    }
}
