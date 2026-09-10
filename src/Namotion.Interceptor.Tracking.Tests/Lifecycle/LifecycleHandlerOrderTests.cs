using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The merged lifecycle is the public ordering seam the deleted descent handler used to be: a
/// handler positions itself around the attach descent with
/// <c>[RunsBefore(typeof(LifecycleInterceptor))]</c> or <c>[RunsAfter(typeof(LifecycleInterceptor))]</c>.
/// </summary>
/// <remarks>
/// These tests prove the constraints bind by observed callback order, not by inspection, because
/// <c>ServiceOrderResolver</c> silently drops a constraint whose target does not participate in the
/// resolved interface. Registration order is deliberately adversarial (the behind probe first, the
/// lifecycle, then the ahead probe): unordered resolution preserves registration order, so an
/// unbound constraint leaves each probe on the wrong side of the descent and the observed order
/// flips.
/// </remarks>
public class LifecycleHandlerOrderTests
{
    private static (AheadProbe Ahead, BehindProbe Behind, IInterceptorSubjectContext Context) CreateContext()
    {
        var ahead = new AheadProbe();
        var behind = new BehindProbe();

        var context = InterceptorSubjectContext.Create();
        context.AddService<ILifecycleHandler>(behind);
        context.WithLifecycle();
        context.AddService<ILifecycleHandler>(ahead);

        return (ahead, behind, context);
    }

    /// <summary>Builds the detached chain top -> mid -> leaf.</summary>
    private static Person BuildDetachedChain()
    {
        var leaf = new Person { FirstName = "leaf" };
        var mid = new Person { FirstName = "mid", Father = leaf };
        return new Person { FirstName = "top", Father = mid };
    }

    [Fact]
    public void WhenASubtreeAttaches_ThenAHandlerBeforeTheLifecycleObservesItTopDown()
    {
        // Arrange
        var (ahead, _, context) = CreateContext();
        var root = new Person(context) { FirstName = "root" };
        var top = BuildDetachedChain();
        ahead.Attached.Clear();

        // Act
        root.Father = top;

        // Assert
        Assert.Equal(["top", "mid", "leaf"], ahead.Attached);
    }

    [Fact]
    public void WhenASubtreeAttaches_ThenAHandlerAfterTheLifecycleObservesItBottomUp()
    {
        // Arrange
        var (_, behind, context) = CreateContext();
        var root = new Person(context) { FirstName = "root" };
        var top = BuildDetachedChain();
        behind.Attached.Clear();

        // Act
        root.Father = top;

        // Assert
        Assert.Equal(["leaf", "mid", "top"], behind.Attached);
    }

    [Fact]
    public void WhenASubtreeDetaches_ThenBothPositionsObserveItTopDown()
    {
        // Arrange: one release traversal serves every handler position, so the group behind the
        // descent observes the same top-down order as the group ahead of it. This is the declared
        // breaking change: two detach orders existed only while the descent handler re-entered the
        // detach and ran the behind group a second time from the bottom.
        var (ahead, behind, context) = CreateContext();
        var root = new Person(context) { FirstName = "root" };
        root.Father = BuildDetachedChain();

        // Act
        root.Father = null;

        // Assert
        Assert.Equal(["top", "mid", "leaf"], ahead.Detached);
        Assert.Equal(["top", "mid", "leaf"], behind.Detached);
    }

    [Fact]
    public void WhenTheHandlerChainResolves_ThenTheLifecycleSitsBetweenTheProbes()
    {
        // Arrange
        var (_, _, context) = CreateContext();

        // Act
        var handlers = context.GetServices<ILifecycleHandler>()
            .Select(handler => handler.GetType().Name)
            .ToArray();

        // Assert
        Assert.Equal([nameof(AheadProbe), nameof(LifecycleInterceptor), nameof(BehindProbe)], handlers);
    }

    [Fact]
    public void WhenTheFirstHandlerObservesAnEdge_ThenGetParentsAlreadyReportsIt()
    {
        // Arrange: authoritative parent state is published before the first handler runs, so even
        // the earliest position resolves the committed edge through GetParents().
        var (ahead, _, context) = CreateContext();
        var root = new Person(context) { FirstName = "root" };
        var top = BuildDetachedChain();
        ahead.EdgeVisibleInParents.Clear();

        // Act
        root.Father = top;

        // Assert
        Assert.Equal(3, ahead.EdgeVisibleInParents.Count);
        Assert.All(ahead.EdgeVisibleInParents, Assert.True);
    }

    [RunsBefore(typeof(LifecycleInterceptor))]
    private sealed class AheadProbe : ILifecycleHandler
    {
        public List<string> Attached { get; } = [];

        public List<string> Detached { get; } = [];

        public List<bool> EdgeVisibleInParents { get; } = [];

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach)
            {
                Attached.Add(((Person)change.Subject).FirstName!);
            }

            if (change.IsContextDetach)
            {
                Detached.Add(((Person)change.Subject).FirstName!);
            }

            if (change is { IsPropertyReferenceAdded: true, Property: { } property })
            {
                EdgeVisibleInParents.Add(change.Subject.GetParents()
                    .Any(parent => parent.Property.Equals(property) && Equals(parent.Index, change.Index)));
            }
        }
    }

    [RunsAfter(typeof(LifecycleInterceptor))]
    private sealed class BehindProbe : ILifecycleHandler
    {
        public List<string> Attached { get; } = [];

        public List<string> Detached { get; } = [];

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach)
            {
                Attached.Add(((Person)change.Subject).FirstName!);
            }

            if (change.IsContextDetach)
            {
                Detached.Add(((Person)change.Subject).FirstName!);
            }
        }
    }
}
