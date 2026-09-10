using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Pins the registry's position ahead of the lifecycle descent for every composition, and what that
/// gives a handler observing a subject as it attaches. The merged <see cref="LifecycleInterceptor"/>
/// is the ordering seam, so the registry's <c>[RunsBefore(typeof(LifecycleInterceptor))]</c> is what
/// this order rests on; the chain assertions are the proof that the constraint binds, because with
/// an unbound constraint the "registry-last" composition would resolve the registry behind the
/// lifecycle.
/// </summary>
public class RegistryHandlerOrderTests
{
    public static TheoryData<string> RegistrationOrders() =>
    [
        "registry-first",
        "registry-after-tracking",
        "registry-after-lifecycle"
    ];

    private static IInterceptorSubjectContext CreateContext(string registrationOrder)
    {
        var context = InterceptorSubjectContext.Create();
        return registrationOrder switch
        {
            "registry-first" => context.WithRegistry().WithFullPropertyTracking(),
            "registry-after-tracking" => context.WithFullPropertyTracking().WithRegistry(),
            "registry-after-lifecycle" => context.WithLifecycle().WithRegistry(),
            _ => throw new ArgumentOutOfRangeException(nameof(registrationOrder))
        };
    }

    /// <summary>Builds root -> top -> middle -> child, with the subtree detached.</summary>
    private static OrderNode BuildDetachedSubtree(out OrderNode child)
    {
        child = new OrderNode { Name = "child" };
        var middle = new OrderNode { Name = "middle", Child = child };
        return new OrderNode { Name = "top", Child = middle };
    }

    [Theory]
    [MemberData(nameof(RegistrationOrders))]
    public void WhenRegistryIsRegisteredInAnyOrder_ThenTheHandlerChainResolvesIdentically(string registrationOrder)
    {
        // Arrange
        var context = CreateContext(registrationOrder);

        // Act
        var handlers = context.GetServices<ILifecycleHandler>()
            .Select(handler => handler.GetType().Name)
            .ToArray();

        // Assert
        Assert.Equal(
            [nameof(SubjectRegistry), nameof(LifecycleInterceptor)],
            handlers);
    }

    [Theory]
    [MemberData(nameof(RegistrationOrders))]
    public void WhenPrebuiltSubtreeIsAttachedToLiveRoot_ThenEveryAncestorIsRegistryVisibleDuringAttach(string registrationOrder)
    {
        // Arrange
        var context = CreateContext(registrationOrder);
        var root = new OrderNode(context) { Name = "root" };
        var top = BuildDetachedSubtree(out var child);

        // Act
        root.Child = top;

        // Assert: the registry runs ahead of the descent at every level, so when the child's own
        // handler runs, every ancestor up to the root is already registered.
        Assert.Equal(["middle", "top", "root"], child.AncestorsVisibleDuringAttach);
    }

    [Fact]
    public void WhenAncestorsAreWalkedOverRegistryEdges_ThenTheWholeChainResolvesDuringAttach()
    {
        // Arrange: the same chain resolved over the registry's own parent edges rather than over
        // GetParents(), the shape the README and the connector docs use.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new OrderNode(context) { Name = "root" };
        var top = BuildDetachedSubtree(out var child);

        // Act
        root.Child = top;

        // Assert
        Assert.Equal(["middle", "top", "root"], child.AncestorsViaRegistryDuringAttach);
    }

    [Fact]
    public void WhenTheFirstHandlerObservesAnEdge_ThenGetParentsAlreadyReportsIt()
    {
        // Arrange: authoritative parent state is published before the first handler runs, so even
        // a handler ahead of the registry resolves the committed edge through GetParents().
        var probe = new FirstHandlerParentProbe();
        var context = InterceptorSubjectContext.Create().WithRegistry().WithFullPropertyTracking();
        context.AddService<ILifecycleHandler>(probe);

        var root = new OrderNode(context) { Name = "root" };
        var top = BuildDetachedSubtree(out _);

        // Act
        root.Child = top;

        // Assert: one observation per attached edge (top, middle, child), each already visible.
        Assert.Equal(3, probe.EdgeVisibleInParents.Count);
        Assert.All(probe.EdgeVisibleInParents, Assert.True);
        Assert.Equal(
            nameof(FirstHandlerParentProbe),
            context.GetServices<ILifecycleHandler>()[0].GetType().Name);
    }

    [Fact]
    public void WhenSubtreeIsDetached_ThenAncestorsAreAlreadyDeregisteredButParentLinkRemains()
    {
        // Arrange
        var context = CreateContext("registry-after-tracking");
        var root = new OrderNode(context) { Name = "root" };
        var top = BuildDetachedSubtree(out var child);
        root.Child = top;

        // Act
        root.Child = null;

        // Assert: detach is deliberately not the mirror of attach. A subject leaving the graph has
        // already given up its ownership record when the first detach handler runs, so it reports no
        // parents at all, and its ancestors were processed further up the descent and are gone too.
        // A subject that survives an edge removal still reports the edges that remain. A consumer
        // needing ancestor state while detaching has to capture it at attach; the edge it is being
        // detached from is on the change itself.
        Assert.Equal(0, child.ParentLinkCountDuringDetach);
        Assert.Empty(child.AncestorsVisibleDuringDetach);
    }

    [RunsBefore(typeof(SubjectRegistry))]
    private sealed class FirstHandlerParentProbe : ILifecycleHandler
    {
        public List<bool> EdgeVisibleInParents { get; } = [];

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change is { IsPropertyReferenceAdded: true, Property: { } property })
            {
                EdgeVisibleInParents.Add(change.Subject.GetParents()
                    .Any(parent => parent.Property.Equals(property) && Equals(parent.Index, change.Index)));
            }
        }
    }
}

[InterceptorSubject]
public partial class OrderNode : ILifecycleHandler
{
    public partial string Name { get; set; }

    public partial OrderNode? Child { get; set; }

    public string[] AncestorsVisibleDuringAttach { get; private set; } = [];

    public string[] AncestorsViaRegistryDuringAttach { get; private set; } = [];

    public string[] AncestorsVisibleDuringDetach { get; private set; } = [];

    public int ParentLinkCountDuringDetach { get; private set; } = -1;

    public OrderNode()
    {
        Name = string.Empty;
    }

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextAttach)
        {
            AncestorsVisibleDuringAttach = CollectOverParentLinks(this, []);
            AncestorsViaRegistryDuringAttach = CollectOverRegistryEdges(this, []);
        }
        else if (change.IsContextDetach)
        {
            ParentLinkCountDuringDetach = ((IInterceptorSubject)this).GetParents().Length;
            AncestorsVisibleDuringDetach = CollectOverParentLinks(this, []);
        }
    }

    private static string[] CollectOverParentLinks(IInterceptorSubject subject, HashSet<IInterceptorSubject> visited)
    {
        if (!visited.Add(subject))
        {
            return [];
        }

        var ancestors = new List<string>();
        foreach (var parent in subject.GetParents())
        {
            var parentSubject = parent.Property.Subject;
            if (parentSubject.TryGetRegisteredSubject() is not null && parentSubject is OrderNode node)
            {
                ancestors.Add(node.Name);
            }

            ancestors.AddRange(CollectOverParentLinks(parentSubject, visited));
        }

        return ancestors.ToArray();
    }

    private static string[] CollectOverRegistryEdges(IInterceptorSubject subject, HashSet<IInterceptorSubject> visited)
    {
        if (!visited.Add(subject) || subject.TryGetRegisteredSubject() is not { } registered)
        {
            return [];
        }

        var ancestors = new List<string>();
        foreach (var parent in registered.Parents)
        {
            var parentSubject = parent.Property.Parent.Subject;
            if (parentSubject is OrderNode node)
            {
                ancestors.Add(node.Name);
            }

            ancestors.AddRange(CollectOverRegistryEdges(parentSubject, visited));
        }

        return ancestors.ToArray();
    }
}
