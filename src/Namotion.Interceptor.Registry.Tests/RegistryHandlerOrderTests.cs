using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Pins that the handlers running ahead of the descent resolve in one order for every composition,
/// and what that gives a handler observing a subject as it attaches. See "Handler Order Around the
/// Descent" in docs/design/tracking-lifecycle.md.
/// </summary>
public class RegistryHandlerOrderTests
{
    public static TheoryData<string> RegistrationOrders() =>
    [
        "registry-first",
        "registry-after-tracking",
        "registry-after-parents"
    ];

    private static IInterceptorSubjectContext CreateContext(string registrationOrder)
    {
        var context = InterceptorSubjectContext.Create();
        return registrationOrder switch
        {
            "registry-first" => context.WithRegistry().WithParents().WithFullPropertyTracking(),
            "registry-after-tracking" => context.WithFullPropertyTracking().WithRegistry().WithParents(),
            "registry-after-parents" => context.WithFullPropertyTracking().WithParents().WithRegistry(),
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
            [nameof(SubjectRegistry), nameof(ParentTrackingHandler), nameof(ContextInheritanceHandler)],
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

        // Assert: "middle" is what the child's own registration pulls in on demand and "root"
        // attached earlier, so a registry behind the descent leaves the gap at "top", in the middle
        // of the chain rather than at its end.
        Assert.Equal(["middle", "top", "root"], child.AncestorsVisibleDuringAttach);
    }

    [Fact]
    public void WhenParentTrackingIsAbsent_ThenRegistryParentEdgesResolveTheWholeChainDuringAttach()
    {
        // Arrange: tracking before registry with no parent tracking, the shape the README and the
        // connector docs use. GetParents() is empty without ParentTrackingHandler, so the chain has
        // to be walked over the registry's own parent edges instead.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new OrderNode(context) { Name = "root" };
        var top = BuildDetachedSubtree(out var child);

        // Act
        root.Child = top;

        // Assert
        Assert.Equal(["middle", "top", "root"], child.AncestorsViaRegistryDuringAttach);
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

        // Assert: detach is deliberately not the mirror of attach. A subject's own handler runs
        // before the context handlers that deregister it, but its ancestors were processed further
        // up the descent and are already gone, so a consumer needing ancestor state while detaching
        // has to capture it at attach.
        Assert.Equal(1, child.ParentLinkCountDuringDetach);
        Assert.Empty(child.AncestorsVisibleDuringDetach);
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
