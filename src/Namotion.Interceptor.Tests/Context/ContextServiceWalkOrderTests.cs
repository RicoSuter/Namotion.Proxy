using System.Text;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Ordering;

namespace Namotion.Interceptor.Tests.Context;

/// <summary>
/// The differential oracle for the service walk. The order of the resolved services is observable:
/// every context reorders what it collected with <see cref="ServiceOrderResolver"/>, which keeps the
/// input order among services that no ordering attribute separates, so the sequence depends on the
/// exact shape of the walk and not only on the set it reaches.
///
/// The reference below is a direct transcription of the recursive walk that the iterative one
/// replaced: depth first, left to right, own services before fallback contexts and the ownership
/// route after them, distinct and reordered once per context, and a visited set that both cuts
/// cycles and collapses a shared subgraph onto the path that reached it first. Randomized graphs
/// are resolved with both and the two sequences are compared element by element, so a walk that
/// reaches the same services in a different order fails here.
/// </summary>
public class ContextServiceWalkOrderTests
{
    private const int GraphsPerSeed = 400;
    private const int MaxNodeCount = 8;
    private const int SharedServiceCount = 3;

    [Theory]
    [InlineData(11)]
    [InlineData(97)]
    [InlineData(1234)]
    [InlineData(31337)]
    [InlineData(524287)]
    [InlineData(-4242)]
    [InlineData(20260731)]
    [InlineData(-2147483647)]
    public void WhenRandomContextGraphIsResolved_ThenServiceOrderMatchesTheRecursiveWalk(int seed)
    {
        var random = new Random(seed);
        var coverage = new Coverage();

        for (var graphIndex = 0; graphIndex < GraphsPerSeed; graphIndex++)
        {
            // Arrange
            var nodes = BuildGraph(random);
            coverage.Observe(nodes);

            foreach (var node in nodes)
            {
                if (!TryResolveWithReference(node, out var expected))
                {
                    // Act & Assert: a chain of contexts that all delegate and lead back into
                    // themselves resolves nothing and raises instead, which the walk has to
                    // reach the same way.
                    coverage.DelegationCycles++;
                    var exception = Assert.Throws<InvalidOperationException>(() => node.Context.GetServices<OrderedService>());

                    // Checked, because the arity check of TryGetService and a failed ordering
                    // resolution raise the same type and would otherwise pass for a cycle.
                    Assert.Contains("delegation cycle", exception.Message, StringComparison.Ordinal);
                    continue;
                }

                // Act
                var actual = node.Context.GetServices<OrderedService>();

                // Assert
                Assert.True(expected.Count == actual.Length,
                    $"Context c{node.Index} resolved {actual.Length} services but the recursive walk resolves " +
                    $"{expected.Count}.{Environment.NewLine}{Describe(seed, graphIndex, nodes, expected, actual)}");

                for (var index = 0; index < expected.Count; index++)
                {
                    Assert.True(ReferenceEquals(expected[index], actual[index]),
                        $"Context c{node.Index} resolved {actual[index]} at position {index} but the recursive " +
                        $"walk resolves {expected[index]} there.{Environment.NewLine}" +
                        Describe(seed, graphIndex, nodes, expected, actual));
                }
            }
        }

        // Assert: the corpus has to keep containing every shape the walk has to get right,
        // otherwise this test could pass by never producing one.
        Assert.True(coverage.DelegationChains > 0, "No graph contained a delegating context.");
        Assert.True(coverage.DelegationCycles > 0, "No graph contained a delegation cycle.");
        Assert.True(coverage.FallbackSources > 0, "No graph contained a fallback context.");
        Assert.True(coverage.OwnershipRouteSources > 0, "No graph contained an ownership route.");
        Assert.True(coverage.FallbackCycles > 0, "No graph contained a fallback-only relationship cycle.");
        Assert.True(coverage.OwnershipRouteCycles > 0, "No graph contained an ownership-route-only relationship cycle.");
        Assert.True(coverage.MixedRelationshipCycles > 0, "No graph contained a mixed relationship cycle.");
        Assert.True(coverage.MultiFallbackNodes > 0, "No graph contained a context with several fallback contexts.");
        Assert.True(coverage.FallbackSharedNodes > 0, "No shared node had a fallback-context source.");
        Assert.True(coverage.OwnershipRouteSharedNodes > 0, "No shared node had an ownership-route source.");
        Assert.True(coverage.DuplicateServices > 0, "No graph contained a service instance registered twice.");
    }

    /// <summary>
    /// The recursive reference. Mirrors <c>GetServices</c>: the delegation chain of the queried
    /// context is followed first and raises when it closes, then the walk collects from the context
    /// it ended on with a fresh visited set.
    /// </summary>
    private static bool TryResolveWithReference(Node node, out List<OrderedService> services)
    {
        var resolved = node;
        var chain = new HashSet<Node>();
        while (resolved.DelegationTarget is not null)
        {
            if (!chain.Add(resolved))
            {
                services = [];
                return false;
            }

            resolved = resolved.DelegationTarget;
        }

        services = CollectWithReference(resolved, []);
        return true;
    }

    private static List<OrderedService> CollectWithReference(Node node, HashSet<Node> visited)
    {
        if (!visited.Add(node))
        {
            return [];
        }

        var delegationTarget = node.DelegationTarget;
        if (delegationTarget is not null)
        {
            return CollectWithReference(delegationTarget, visited);
        }

        var collected = new List<object>(node.Services);
        foreach (var fallback in node.Fallbacks)
        {
            collected.AddRange(CollectWithReference(fallback, visited));
        }

        if (node.OwnershipRoute is not null)
        {
            collected.AddRange(CollectWithReference(node.OwnershipRoute, visited));
        }

        return ServiceOrderResolver
            .OrderByDependencies(collected.Distinct().ToArray())
            .Cast<OrderedService>()
            .ToList();
    }

    private static Node[] BuildGraph(Random random)
    {
        var nodeCount = random.Next(2, MaxNodeCount + 1);
        var nodes = new Node[nodeCount];
        for (var index = 0; index < nodeCount; index++)
        {
            nodes[index] = new Node(index);
        }

        // A few instances shared by several contexts, so that the same instance is reachable over
        // more than one path and the per-context Distinct() has something to collapse.
        var sharedServices = new OrderedService[SharedServiceCount];
        for (var index = 0; index < SharedServiceCount; index++)
        {
            sharedServices[index] = CreateService(random);
        }

        foreach (var node in nodes)
        {
            // A context without own services and with exactly one fallback context delegates, so
            // drawing zero often is what produces the delegation chains and cycles.
            var serviceCount = random.Next(0, 4);
            for (var index = 0; index < serviceCount; index++)
            {
                var service = random.Next(3) == 0
                    ? sharedServices[random.Next(SharedServiceCount)]
                    : CreateService(random);

                // Registering the same instance twice is allowed and is collapsed by Distinct(),
                // so it is deliberately not filtered out here.
                node.Services.Add(service);
                node.Context.AddService(service);
            }
        }

        var edgeCandidates = random.Next(0, nodeCount * 2 + 1);
        for (var candidate = 0; candidate < edgeCandidates; candidate++)
        {
            var source = nodes[random.Next(nodeCount)];
            var target = nodes[random.Next(nodeCount)];

            // A repeated registration is rejected by the context, so the model drops it too.
            if (source.Fallbacks.Contains(target))
            {
                continue;
            }

            source.Fallbacks.Add(target);
            source.Context.AddFallbackContext(target.Context);
        }

        var ownershipDomain = InterceptorSubjectContext.Create();
        foreach (var source in nodes)
        {
            if (random.Next(2) == 0)
            {
                continue;
            }

            var target = nodes[random.Next(nodes.Length)];
            source.OwnershipRoute = target;
            var route = new InterceptorSubjectContext.ContextOwnershipRoute(target.Context, ownershipDomain);
            Assert.True(source.Context.TryChangeOwnershipRoute(null, route));
        }

        return nodes;
    }

    private static OrderedService CreateService(Random random)
    {
        var id = random.Next(1000);
        return random.Next(6) switch
        {
            0 => new PlainService(id),
            1 => new OtherPlainService(id),
            2 => new FirstService(id),
            3 => new LastService(id),
            4 => new BeforeOtherPlainService(id),
            _ => new AfterPlainService(id)
        };
    }

    private static string Describe(int seed, int graphIndex, Node[] nodes, List<OrderedService> expected, IEnumerable<OrderedService> actual)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Seed {seed}, graph {graphIndex}:");
        foreach (var node in nodes)
        {
            var ownershipRoute = node.OwnershipRoute;
            builder.AppendLine(
                $"  c{node.Index} services [{string.Join(", ", node.Services)}] " +
                $"fallbacks [{string.Join(", ", node.Fallbacks.Select(fallback => $"c{fallback.Index}"))}] " +
                $"route [{(ownershipRoute is null ? string.Empty : $"c{ownershipRoute.Index}")}]");
        }

        builder.AppendLine($"  expected: [{string.Join(", ", expected)}]");
        builder.AppendLine($"  actual:   [{string.Join(", ", actual)}]");
        return builder.ToString();
    }

    private sealed class Coverage
    {
        internal int DelegationChains;
        internal int DelegationCycles;
        internal int FallbackSources;
        internal int OwnershipRouteSources;
        internal int FallbackCycles;
        internal int OwnershipRouteCycles;
        internal int MixedRelationshipCycles;
        internal int MultiFallbackNodes;
        internal int FallbackSharedNodes;
        internal int OwnershipRouteSharedNodes;
        internal int DuplicateServices;

        internal void Observe(Node[] nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Fallbacks.Count != 0)
                {
                    FallbackSources++;
                }

                if (node.DelegationTarget is not null)
                {
                    DelegationChains++;
                }

                if (node.Fallbacks.Count > 1)
                {
                    MultiFallbackNodes++;
                }

                if (node.Services.Count != node.Services.Distinct().Count())
                {
                    DuplicateServices++;
                }

                if (node.OwnershipRoute is not null)
                {
                    OwnershipRouteSources++;
                }

                var fallbackSourceCount = nodes.Sum(other => other.Fallbacks.Count(target => ReferenceEquals(target, node)));
                var ownershipRouteSourceCount = nodes.Count(other => ReferenceEquals(other.OwnershipRoute, node));
                if (fallbackSourceCount + ownershipRouteSourceCount > 1)
                {
                    if (fallbackSourceCount != 0)
                    {
                        FallbackSharedNodes++;
                    }

                    if (ownershipRouteSourceCount != 0)
                    {
                        OwnershipRouteSharedNodes++;
                    }
                }

                ObserveCycleShapes(node, node, false, false, [node]);
            }
        }

        private void ObserveCycleShapes(
            Node start,
            Node current,
            bool hasFallback,
            bool hasOwnershipRoute,
            HashSet<Node> path)
        {
            foreach (var fallback in current.Fallbacks)
            {
                ObserveCycleEdge(start, fallback, true, hasFallback, hasOwnershipRoute, path);
            }

            if (current.OwnershipRoute is not null)
            {
                ObserveCycleEdge(start, current.OwnershipRoute, false, hasFallback, hasOwnershipRoute, path);
            }
        }

        private void ObserveCycleEdge(
            Node start,
            Node target,
            bool isFallback,
            bool hasFallback,
            bool hasOwnershipRoute,
            HashSet<Node> path)
        {
            hasFallback |= isFallback;
            hasOwnershipRoute |= !isFallback;

            if (ReferenceEquals(target, start))
            {
                if (hasFallback && hasOwnershipRoute)
                {
                    MixedRelationshipCycles++;
                }
                else if (hasFallback)
                {
                    FallbackCycles++;
                }
                else
                {
                    OwnershipRouteCycles++;
                }

                return;
            }

            if (!path.Add(target))
            {
                return;
            }

            ObserveCycleShapes(start, target, hasFallback, hasOwnershipRoute, path);
            path.Remove(target);
        }
    }

    private sealed class Node(int index)
    {
        internal int Index { get; } = index;

        internal InterceptorSubjectContext Context { get; } = InterceptorSubjectContext.Create();

        internal List<OrderedService> Services { get; } = [];

        internal List<Node> Fallbacks { get; } = [];

        internal Node? OwnershipRoute { get; set; }

        /// <summary>Mirrors <c>ContextState.DelegationTarget</c>.</summary>
        internal Node? DelegationTarget
        {
            get
            {
                if (Services.Count != 0)
                {
                    return null;
                }

                if (OwnershipRoute is null)
                {
                    return Fallbacks.Count == 1 ? Fallbacks[0] : null;
                }

                if (Fallbacks.Count == 0)
                {
                    return OwnershipRoute;
                }

                return Fallbacks.Count == 1 && ReferenceEquals(Fallbacks[0], OwnershipRoute)
                    ? OwnershipRoute
                    : null;
            }
        }
    }

    // The attributes never contradict each other, so no subset of these types can make the resolver
    // raise and every difference this test reports is a difference in the walk.
    private abstract class OrderedService(int id)
    {
        private int Id { get; } = id;

        public override string ToString() => $"{GetType().Name}#{Id}";
    }

    private sealed class PlainService(int id) : OrderedService(id);

    private sealed class OtherPlainService(int id) : OrderedService(id);

    [RunsFirst]
    private sealed class FirstService(int id) : OrderedService(id);

    [RunsLast]
    private sealed class LastService(int id) : OrderedService(id);

    [RunsBefore(typeof(OtherPlainService))]
    private sealed class BeforeOtherPlainService(int id) : OrderedService(id);

    [RunsAfter(typeof(PlainService))]
    private sealed class AfterPlainService(int id) : OrderedService(id);
}
