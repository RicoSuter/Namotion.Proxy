using Xunit;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Mutation;

public class KnownNodeGraphTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

    [Fact]
    public void WhenRebuildOnLeafNode_ThenKnownNodesContainsOnlyLeaf()
    {
        // Arrange
        var context = CreateContext();
        var leaf = new TestNode(context);
        var graph = new KnownNodeGraph();

        // Act
        graph.Rebuild(leaf);

        // Assert
        Assert.Single(graph.KnownNodes);
        Assert.Equal(leaf, graph.KnownNodes[0]);
    }

    [Fact]
    public void WhenRebuildOnGraphWithCollection_ThenAllReachableNodesIncluded()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestNode(context)
        {
            Collection = [new TestNode(), new TestNode(), new TestNode()]
        };
        var graph = new KnownNodeGraph();

        // Act
        graph.Rebuild(root);

        // Assert: root + 3 children.
        Assert.Equal(4, graph.KnownNodes.Count);
    }

    [Fact]
    public void WhenGraphHasCycle_ThenRebuildTerminates()
    {
        // Arrange: a <-> b
        var context = CreateContext();
        var b = new TestNode(context);
        var a = new TestNode(context) { ObjectRef = b };
        b.ObjectRef = a;
        var graph = new KnownNodeGraph();

        // Act
        graph.Rebuild(a);

        // Assert: both nodes captured exactly once each.
        Assert.Equal(2, graph.KnownNodes.Count);
    }

    [Fact]
    public void WhenRebuildCalled_ThenStructuralTargetsExcludeNodesBeyondMaxDepth()
    {
        // Arrange: depth 0 (root) -> depth 1 (ObjectRef) -> depth 2 (ObjectRef) -> depth 3 (ObjectRef) -> depth 4.
        var context = CreateContext();
        var d4 = new TestNode(context);
        var d3 = new TestNode(context) { ObjectRef = d4 };
        var d2 = new TestNode(context) { ObjectRef = d3 };
        var d1 = new TestNode(context) { ObjectRef = d2 };
        var root = new TestNode(context) { ObjectRef = d1 };
        var graph = new KnownNodeGraph();

        // Act
        graph.Rebuild(root);

        // Assert: KnownNodes contains all 5 (no depth limit). StructuralTargets uses depth < MaxDepth (= 3), so depths 0, 1, 2 only.
        Assert.Equal(5, graph.KnownNodes.Count);
        Assert.Equal(3, graph.StructuralTargets.Count);
        Assert.Contains(root, graph.StructuralTargets);
        Assert.Contains(d1, graph.StructuralTargets);
        Assert.Contains(d2, graph.StructuralTargets);
        Assert.DoesNotContain(d3, graph.StructuralTargets);
        Assert.DoesNotContain(d4, graph.StructuralTargets);
    }
}
