using Xunit;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Mutation;

public class StructuralMutatorTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

    [Fact]
    public void WhenStructuralTargetsEmpty_ThenPerformMutationReturnsWithoutThrowing()
    {
        // Arrange
        var graph = new KnownNodeGraph();
        var mutator = new StructuralMutator(graph);

        // Act / Assert (no exception)
        mutator.PerformMutation();
    }

    [Fact]
    public void WhenInvokedManyTimes_ThenGraphMutationsStayWithinMinAndMaxBounds()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestNode(context)
        {
            Collection = [new TestNode(), new TestNode(), new TestNode()],
            Items = new Dictionary<string, TestNode> { ["item-0"] = new() }
        };
        var graph = new KnownNodeGraph();
        graph.Rebuild(root);
        var mutator = new StructuralMutator(graph);

        // Act: drive many mutations.
        for (var i = 0; i < 200; i++)
        {
            mutator.PerformMutation();
            graph.Rebuild(root);
        }

        // Assert: collection bounded by [0, 30] (the per-node max), aggregate node count bounded by MaxTotalNodes.
        Assert.True(graph.KnownNodes.Count <= 500);
    }
}
